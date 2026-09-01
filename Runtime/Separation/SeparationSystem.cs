using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Serialization;

namespace MiHordeTraffic.Separation
{
    /*
     * Replaces NavMeshAgent's built in local avoidance, which is a per agent RVO solve and is what makes a crowd snap and teleport when agents end up inside each other.
     * This does the opposite trade: agents are allowed to walk into each other and are pushed back out over the following frames, and the work happens on worker threads.
     *
     * The system itself owns no buffers. It routes bodies into groups and drives them in the one order that is safe, and every group holds its own roster, arrays and job.
     * Splitting on the settings asset means a scene can run flyers in FULL_3D every frame next to a slow melee horde in XZ every third frame without either compromising.
     *
     * Registration is buffered rather than applied immediately. A body can enable itself at any point in the frame, including while a job is reading the arrays,
     * and resizing or reordering those arrays at that moment is a crash rather than a glitch. The queue is drained at the one point in the frame where no group has a job in flight.
     *
     * The system pushes results out to the bodies rather than having each body pull its own in LateUpdate.
     * Pulling would leave the order between the system and a thousand bodies undefined,
     * and a thousand LateUpdate callbacks cost real time on their own before any of them has done anything.
     */
    /// <summary>
    /// Drives every separation group in the scene. Drop one on a manager object and agents find it themselves.
    /// </summary>
    [DisallowMultipleComponent]
    public class SeparationSystem : MonoBehaviour
    {

        private static readonly List<ISeparationBody> PENDING_REGISTER = new List<ISeparationBody>();
        private static readonly List<ISeparationBody> PENDING_UNREGISTER = new List<ISeparationBody>();

        private static SeparationSystem _instance;

        /// <summary>
        /// The system in the loaded scene, or null when there is none.
        /// </summary>
        public static SeparationSystem Instance => _instance;

        [FormerlySerializedAs("settings")]
        [SerializeField] private SeparationSettings defaultSettings;
        [SerializeField] private bool logBurstStatus = true;

        private readonly List<SeparationGroup> _groups = new List<SeparationGroup>();
        private readonly Dictionary<SeparationSettings, SeparationGroup> _groupsBySettings = new Dictionary<SeparationSettings, SeparationGroup>();

        private SeparationSettings _runtimeDefaults;

        /// <summary>
        /// Settings used by bodies that name no group of their own.
        /// </summary>
        public SeparationSettings DefaultSettings => defaultSettings;

        /// <summary>
        /// Every group that has had at least one body registered to it, in the order they first appeared.
        /// </summary>
        public IReadOnlyList<SeparationGroup> Groups => _groups;

        /// <summary>
        /// How many groups are running.
        /// </summary>
        public int GroupCount => _groups.Count;

        /// <summary>
        /// How many bodies are being separated across every group, not counting anything queued this frame.
        /// </summary>
        public int BodyCount
        {
            get
            {
                int total = 0;

                for (int i = 0; i < _groups.Count; i++)
                    total += _groups[i].BodyCount;

                return total;
            }
        }

        /// <summary>
        /// Queues a body to start being separated, in whichever group its settings name. Safe to call at any point in the frame, including from OnEnable.
        /// </summary>
        /// <param name="body">The body to add. Ignored if it is already queued for removal, in which case the removal is cancelled instead.</param>
        public static void Register(ISeparationBody body)
        {
            if (PENDING_UNREGISTER.Remove(body)) return;

            PENDING_REGISTER.Add(body);
        }

        /// <summary>
        /// Queues a body to stop being separated. Safe to call at any point in the frame, including from OnDisable.
        /// </summary>
        /// <param name="body">The body to drop. If it was only queued to be added, that is cancelled instead.</param>
        public static void Unregister(ISeparationBody body)
        {
            if (PENDING_REGISTER.Remove(body)) return;

            PENDING_UNREGISTER.Add(body);
        }

        /// <summary>
        /// The group running a given settings asset, or null when nothing has registered to it yet.
        /// </summary>
        /// <param name="settings">The settings asset that identifies the group.</param>
        /// <returns>The group, or null.</returns>
        public SeparationGroup GroupFor(SeparationSettings settings) => settings && _groupsBySettings.TryGetValue(settings, out SeparationGroup group) ? group : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            PENDING_REGISTER.Clear();
            PENDING_UNREGISTER.Clear();
        }

        /*
         * Bodies registering into a scene that has no system is the one failure this thing has that looks like nothing at all:
         * every agent works, no error is raised, and the crowd simply behaves as though separation was never turned on.
         * This runs after every Awake in the first scene, which is the earliest point where the absence of a system is real rather than a script execution order accident.
         */
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void WarnWhenMissing()
        {
            if (_instance || PENDING_REGISTER.Count == 0) return;

            Debug.LogWarning($"[MiHordeTraffic] {PENDING_REGISTER.Count} bodies registered for separation but no SeparationSystem is in the scene, so nothing will be pushed apart. Add one to a manager object.");
        }

        private void Awake()
        {
            if (_instance && _instance != this)
            {
                Destroy(this);
                return;
            }

            _instance = this;
        }

        private void Start()
        {
            if (logBurstStatus) LogBurstStatus();
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;

            /*
             * Three passes rather than one loop doing all three, because the roster can only be touched when no group anywhere has a job reading its arrays.
             * Completing every group first is what makes that true,
             * and ticking every group only after the drain is what keeps a body from being sampled in the same frame it registered but before its slot exists.
             */
            for (int i = 0; i < _groups.Count; i++)
                _groups[i].CompleteAndApply(deltaTime);

            DrainPendingRoster();

            for (int i = 0; i < _groups.Count; i++)
                _groups[i].TickIfDue();

            JobHandle.ScheduleBatchedJobs();
        }

        private void LateUpdate()
        {
            for (int i = 0; i < _groups.Count; i++)
                _groups[i].LateTick();
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _groups.Count; i++)
                _groups[i].Dispose();

            _groups.Clear();
            _groupsBySettings.Clear();

            if (_runtimeDefaults) Destroy(_runtimeDefaults);

            if (_instance == this) _instance = null;
        }

        /// <summary>
        /// Applies everything that registered or unregistered since the last frame. Only ever called with no job in flight.
        /// </summary>
        private void DrainPendingRoster()
        {
            for (int i = 0; i < PENDING_REGISTER.Count; i++)
            {
                ISeparationBody body = PENDING_REGISTER[i];
                GroupFor(body).Add(body);
            }

            PENDING_REGISTER.Clear();

            /*
             * Removal asks every group rather than looking up which one owns the body.
             * A body that changed its group asset while registered would otherwise be searched for in the wrong place and quietly left behind,
             * and TryRemove is an index compare against a single slot, so asking a handful of groups costs nothing.
             */
            for (int i = 0; i < PENDING_UNREGISTER.Count; i++)
            {
                ISeparationBody body = PENDING_UNREGISTER[i];

                for (int g = 0; g < _groups.Count; g++)
                    if (_groups[g].TryRemove(body)) break;
            }

            PENDING_UNREGISTER.Clear();
        }

        /// <summary>
        /// The group a body belongs in, creating it the first time that settings asset is seen.
        /// </summary>
        private SeparationGroup GroupFor(ISeparationBody body)
        {
            SeparationSettings settings = body.GroupSettings ? body.GroupSettings : ResolveDefaults();

            if (_groupsBySettings.TryGetValue(settings, out SeparationGroup existing)) return existing;

            SeparationGroup group = new SeparationGroup(settings);
            _groupsBySettings.Add(settings, group);
            _groups.Add(group);
            return group;
        }

        /*
         * Built in memory rather than required as an asset, so dropping this component into a scene and pressing play works with nothing else set up.
         * Destroyed with the system, since an instance created this way is not owned by the project and would otherwise outlive play mode in the editor.
         */
        private SeparationSettings ResolveDefaults()
        {
            if (defaultSettings) return defaultSettings;
            if (_runtimeDefaults) return _runtimeDefaults;

            _runtimeDefaults = ScriptableObject.CreateInstance<SeparationSettings>();
            _runtimeDefaults.name = "SeparationSettings (runtime defaults)";
            return _runtimeDefaults;
        }

        private void LogBurstStatus()
        {
            NativeArray<bool> result = new NativeArray<bool>(1, Allocator.TempJob);

            new BurstProbeJob { Result = result }.Schedule().Complete();

            bool compiled = result[0];
            result.Dispose();

            if (compiled) return;

            Debug.LogWarning("[MiHordeTraffic] Burst is not compiling the separation jobs, so they are running as plain IL and will be several times slower. Check that the Burst package is installed and that Jobs > Burst > Enable Compilation is on.");
        }

    }
}

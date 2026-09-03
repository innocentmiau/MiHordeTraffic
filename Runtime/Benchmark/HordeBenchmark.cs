using System.Collections.Generic;
using System.Text;
using MiHordeTraffic.Movement;
using MiHordeTraffic.Pathing;
using MiHordeTraffic.Separation;
using Unity.AI.Navigation;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace MiHordeTraffic.Benchmark
{
    /*
     * The point of this is to change exactly one thing between runs. Same prefab, same spawn positions,
     * same target, same count, and only the avoidance mode differs, because comparing a built in avoidance run
     * against a separation run that also spawned somewhere else or chased something else measures nothing.
     * The spawn seed is fixed for the same reason.
     *
     * The frame time shown is unscaled and smoothed over a short window. Read it in a build, not in the editor:
     * the editor's own overhead sits on top of every number here and flatters whichever run happens to be second.
     */
    /// <summary>
    /// Spawns a fixed horde and reports frame cost, so built in avoidance and the separation system can be measured against each other.
    /// </summary>
    public class HordeBenchmark : MonoBehaviour
    {

        private const float SAMPLE_SMOOTHING = .05f;
        private const float OVERLAY_REFRESH_INTERVAL = .25f;
        private const int LIVE_SAMPLE_WINDOW = 4096;

        [Header("Spawning")]
        [SerializeField] private GameObject agentPrefab;
        [SerializeField] private NavMeshSurface surface;
        [SerializeField] private Transform target;
        [SerializeField] private int spawnCount = 1000;
        [SerializeField] private int spawnSeed = 12345;
        [SerializeField, Range(.05f, 1f)] private float spawnAreaScale = 1f;
        [SerializeField] private bool spawnOnStart = true;

        [Header("Repathing")]
        [SerializeField] private bool repathToTarget = true;
        [SerializeField, Min(1)] private int repathsPerFrame = 100;

        [Header("Mode")]
        [SerializeField] private HordeBenchmarkMode mode = HordeBenchmarkMode.SEPARATION_SYSTEM;
        [SerializeField] private ObstacleAvoidanceType builtInQuality = ObstacleAvoidanceType.HighQualityObstacleAvoidance;

        [Header("Readout")]
        [SerializeField] private bool showOverlay = true;

        /*
         * The frames just after a spawn are instantiation, first path solves and navmesh warm up, none of which say
         * anything about what the crowd costs once it is running. Left in the average they drag the number up and
         * then let it drift down for the rest of the run, which reads as the crowd getting cheaper over time.
         * Two seconds was not enough at a couple of thousand agents, hence a field rather than a constant.
         */
        [SerializeField, Min(0f)] private float settleSeconds = 5f;

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly List<NavMeshAgent> _agents = new List<NavMeshAgent>();

        /*
         * The overlay text is rebuilt four times a second into a reused builder rather than interpolated in OnGUI.
         * OnGUI runs twice a frame, once to lay out and once to repaint, so a handful of interpolated strings there
         * is a few kilobytes of garbage per frame, and the collections that follow land in the frame times
         * this class exists to report. A measurement tool that shows up in its own measurement is worthless.
         */
        private readonly StringBuilder _overlayBuilder = new StringBuilder();

        /*
         * Sampling starts only once the settle window is over. The frames right after spawning two thousand agents
         * are instantiation and first path solves, and letting those into the average makes it climb for the first
         * few seconds and then slowly fall for the rest of the run, which reads as the crowd getting cheaper over time.
         */
        private readonly FrameTimeSamples _samples = new FrameTimeSamples(LIVE_SAMPLE_WINDOW);

        private string _overlayText = string.Empty;
        private float _overlayCountdown;
        private GUIStyle _overlayStyle;
        private FrameTimeStats _liveStats;
        private float _smoothedFrameMs;
        private float _settleCountdown;
        private bool _burstProbed;
        private bool _burstActive;
        private int _repathCursor;

        /*
         * What the crowd standing there is actually configured for, as against what the field says. The mode was
         * applied once per agent as it was created and nowhere else, so editing it mid run looked like it did
         * nothing at all, and the change then arrived silently on the next spawn, which reads as the spawn being
         * broken rather than as the edit having been queued. Holding what was applied is what lets an edit reach
         * the crowd that is already there.
         */
        private HordeBenchmarkMode _appliedMode;
        private bool _warnedAboutTechnique;

        /// <summary>
        /// Smoothed unscaled frame time in milliseconds.
        /// </summary>
        public float SmoothedFrameMs => _smoothedFrameMs;

        /// <summary>
        /// Worst frame seen since the last spawn, ignoring the settle window right after spawning.
        /// </summary>
        public float WorstFrameMs => _liveStats.Max;

        /// <summary>
        /// Mean frame time since the last spawn, over every measured frame rather than a smoothed window.
        /// </summary>
        public float AverageFrameMs => _liveStats.Mean;

        /// <summary>
        /// The full distribution since the last spawn, refreshed as the overlay is rebuilt.
        /// </summary>
        public FrameTimeStats LiveStats => _liveStats;

        /// <summary>
        /// How long after a change this waits before it starts believing the numbers.
        /// </summary>
        public float SettleSeconds => settleSeconds;

        /// <summary>
        /// Throws away everything measured so far and starts again after another settle window.
        /// Used between sweep steps, where the crowd stays put but what is being measured changes.
        /// </summary>
        public void ResetStats()
        {
            _samples.Clear();
            _liveStats = default;
            _settleCountdown = settleSeconds;
        }

        /// <summary>
        /// How many agents a spawn will try to place.
        /// </summary>
        public int SpawnCount => spawnCount;

        /// <summary>
        /// Which avoidance the crowd is running.
        /// </summary>
        public HordeBenchmarkMode Mode => mode;

        /// <summary>
        /// Switches which avoidance is used, on the crowd already standing there as well as on the next spawn.
        /// </summary>
        /// <param name="value">The mode to run from now on.</param>
        public void SetMode(HordeBenchmarkMode value)
        {
            mode = value;

            ApplyModeToSpawned();
        }

        /// <summary>
        /// Whether the overlay is currently drawing.
        /// </summary>
        public bool OverlayVisible => showOverlay;

        /// <summary>
        /// Shows or hides the overlay, so a sweep can take IMGUI out of the frames it is measuring.
        /// </summary>
        /// <param name="value">Whether the overlay should draw.</param>
        public void SetOverlayVisible(bool value) => showOverlay = value;

        [ContextMenu("Spawn Horde")]
        public void SpawnHorde() => Spawn(spawnCount);

        [ContextMenu("Despawn Horde")]
        public void DespawnHorde()
        {
            for (int i = 0; i < _spawned.Count; i++)
                Destroy(_spawned[i]);

            _spawned.Clear();
            _agents.Clear();
            _repathCursor = 0;
            _samples.Clear();
            _liveStats = _samples.Resolve();
        }

        /// <summary>
        /// Clears whatever is spawned and spawns a fresh horde of the given size in the current mode.
        /// </summary>
        /// <param name="count">How many agents to spawn.</param>
        public void Spawn(int count)
        {
            DespawnHorde();

            if (!agentPrefab || !surface)
            {
                Debug.LogError("[HordeBenchmark] Needs both an agent prefab and a NavMeshSurface to spawn against.");
                return;
            }

            Random.InitState(spawnSeed);

            for (int i = 0; i < count; i++)
            {
                if (!NavMeshSurfaceSampler.TrySamplePoint(surface, 0f, spawnAreaScale, out Vector3 position)) continue;

                GameObject spawned = Instantiate(agentPrefab, position, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
                _spawned.Add(spawned);
                _agents.Add(ConfigureAgent(spawned));
            }

            _appliedMode = mode;
            _settleCountdown = settleSeconds;
            _samples.Clear();
            _liveStats = _samples.Resolve();

            WarnAboutTechnique();
        }

        private void Start()
        {
            ProbeBurst();

            if (spawnOnStart) SpawnHorde();
        }

        private void Update()
        {
            float frameMs = Time.unscaledDeltaTime * 1000f;
            _smoothedFrameMs = Mathf.Lerp(_smoothedFrameMs, frameMs, SAMPLE_SMOOTHING);

            /*
             * Polled rather than driven from SetMode, because the field is edited in the inspector far more often
             * than the setter is called, and an inspector edit goes straight to the field. An enum compare a frame
             * is the price of the edit meaning the same thing whichever way it was made.
             */
            if (mode != _appliedMode) ApplyModeToSpawned();
            else WarnAboutTechnique();

            RepathBatch();

            if (_settleCountdown > 0f)
            {
                _settleCountdown -= Time.unscaledDeltaTime;
                return;
            }

            _samples.Add(frameMs);

            _overlayCountdown -= Time.unscaledDeltaTime;
            if (!showOverlay || _overlayCountdown > 0f) return;

            _overlayCountdown = OVERLAY_REFRESH_INTERVAL;
            _liveStats = _samples.Resolve();
            RebuildOverlayText();
        }

        private void OnGUI()
        {
            if (!showOverlay || _overlayText.Length == 0) return;

            _overlayStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 18, normal = { textColor = Color.white } };

            GUI.Label(new Rect(12f, 12f, 720f, 380f), _overlayText, _overlayStyle);
        }

        private void RebuildOverlayText()
        {
            int separated = SeparationSystem.Instance ? SeparationSystem.Instance.BodyCount : 0;

            /*
             * The Burst state is deliberately worded as belonging to the separation jobs and nothing else.
             * It is probed once at startup and does not change with the mode, and reading it as a statement about
             * the built in avoidance is wrong in a way that makes that mode look like it is being helped along:
             * the engine's avoidance is native C++ on the job threads and Burst has nothing to do with it.
             */
            string burst = _burstProbed ? (_burstActive ? "Burst compiled" : "NOT Burst compiled, managed fallback") : "probing";

            _overlayBuilder.Clear();
            _overlayBuilder.Append("Mode: ").Append(mode).AppendLine();
            _overlayBuilder.Append("Agents spawned: ").Append(_spawned.Count).AppendLine();
            _overlayBuilder.Append("Now: ").Append(_smoothedFrameMs.ToString("F2")).Append(" ms  (")
                .Append((1000f / Mathf.Max(_smoothedFrameMs, .0001f)).ToString("F0")).Append(" fps)").AppendLine();
            _overlayBuilder.Append("Since spawn: ").Append(_liveStats.ToShortString()).AppendLine();
            _overlayBuilder.Append("Repaths: ").Append(RepathDescription()).AppendLine();
            _overlayBuilder.Append("MiHordeTraffic separation: ")
                .Append(mode == HordeBenchmarkMode.SEPARATION_SYSTEM ? $"active on {separated}, {burst}" : $"off in this mode ({burst})").AppendLine();
            _overlayBuilder.Append("Built-in avoidance: ")
                .Append(mode == HordeBenchmarkMode.BUILT_IN_AVOIDANCE ? $"{builtInQuality}, engine native, never Burst" : "off in this mode");

            /*
             * Which hand off is live is a per group setting on an asset, so it is invisible from the scene and easy
             * to measure the wrong one for a whole sweep. Reading it back off the running groups rather than off a
             * field here means the overlay reports what is actually happening.
             */
            if (mode == HordeBenchmarkMode.SEPARATION_SYSTEM) AppendHandOff();

            AppendTechniques();

            _overlayText = _overlayBuilder.ToString();
        }

        /// <summary>
        /// Where repathing is coming from and how much of it is going out, so the two drivers are never confused.
        /// </summary>
        private string RepathDescription()
        {
            HordePathScheduler scheduler = HordePathScheduler.Instance;

            if (scheduler && scheduler.BodyCount > 0) return $"{scheduler.RepathsLastFrame}/frame scheduled over {scheduler.BodyCount} bodies, {scheduler.Technique}";

            string fallback = repathToTarget ? Mathf.Min(repathsPerFrame, _agents.Count) + "/frame round robin" : "off";

            return scheduler ? $"{fallback} (scheduler present, 0 bodies registered)" : fallback;
        }

        /*
         * Every technique is listed, not only the active one, so switching in the inspector mid run leaves the
         * previous reading on screen next to the new one. Comparing two numbers you can see at once beats
         * remembering what the last one was.
         */
        /// <summary>
        /// Reports what each pathfinding technique has cost, so they can be compared as they are switched.
        /// </summary>
        private void AppendTechniques()
        {
            HordePathScheduler scheduler = HordePathScheduler.Instance;

            if (!scheduler) return;

            foreach (HordePathTechnique value in System.Enum.GetValues(typeof(HordePathTechnique)))
            {
                HordePathStats stats = scheduler.StatsFor(value);

                _overlayBuilder.AppendLine()
                    .Append(value == scheduler.Technique ? "> " : "  ")
                    .Append(value).Append(": ").Append(stats.ToString());
            }
        }

        /// <summary>
        /// Reports which hand off each running group is using, since that is the thing being compared.
        /// </summary>
        private void AppendHandOff()
        {
            if (!SeparationSystem.Instance) return;

            IReadOnlyList<SeparationGroup> groups = SeparationSystem.Instance.Groups;

            for (int i = 0; i < groups.Count; i++)
            {
                SeparationGroup group = groups[i];
                string settingsName = group.Settings ? group.Settings.name : "defaults";

                _overlayBuilder.AppendLine().Append("Group ").Append(settingsName).Append(": ").Append(group.BodyCount)
                    .Append(" bodies");
            }
        }

        /// <summary>
        /// Puts one spawned agent into whichever avoidance the current mode calls for, and hands back its agent for the repath list.
        /// </summary>
        private NavMeshAgent ConfigureAgent(GameObject spawned)
        {
            NavMeshAgent agent = ApplyModeTo(spawned);

            if (!agent) return null;

            if (target && agent.isOnNavMesh) agent.SetDestination(target.position);

            AssignTarget(spawned);

            return agent;
        }

        /*
         * Switching a HordeAgent off is what takes a body out of the separation system, and it is also what takes
         * it off the flow field, because registering with the mover is something that component does from its own
         * OnEnable. So the two modes that are not the separation system hand the body back to its NavMeshAgent, and
         * the agent has to be put back on for that to mean anything: flow mode switched it off when it took the
         * body over, and the one thing that ever switches it back on is the component being disabled here.
         *
         * Left out, the body has a disabled NavMeshAgent, no place on the mover's roster, and nothing anywhere
         * driving it. It stands exactly where it spawned and every setting on it reads as correct.
         */
        /// <summary>
        /// Puts one object into whichever avoidance the current mode calls for, and hands back its agent.
        /// </summary>
        /// <param name="spawned">The spawned object to configure.</param>
        /// <returns>Its NavMeshAgent, or null when it has none.</returns>
        private NavMeshAgent ApplyModeTo(GameObject spawned)
        {
            NavMeshAgent agent = spawned.GetComponentInChildren<NavMeshAgent>();
            HordeAgent separation = spawned.GetComponentInChildren<HordeAgent>(true);

            bool separated = mode == HordeBenchmarkMode.SEPARATION_SYSTEM;

            if (separation) separation.enabled = separated;

            if (!agent) return null;

            agent.obstacleAvoidanceType = mode == HordeBenchmarkMode.BUILT_IN_AVOIDANCE ? builtInQuality : ObstacleAvoidanceType.NoObstacleAvoidance;

            if (!separated) agent.enabled = true;

            return agent;
        }

        /// <summary>
        /// Applies the current mode to the crowd that is already standing there, rather than only to the next spawn.
        /// </summary>
        private void ApplyModeToSpawned()
        {
            _appliedMode = mode;

            for (int i = 0; i < _spawned.Count; i++)
            {
                GameObject spawned = _spawned[i];

                if (!spawned) continue;

                _agents[i] = ApplyModeTo(spawned);
            }

            WarnAboutTechnique();

            /*
             * Whatever was measured belonged to the mode that has just been replaced, so it is thrown away and the
             * settle window starts again. Averaging across a change is how one mode's cost ends up reported as the
             * other's.
             */
            if (_spawned.Count > 0) ResetStats();
        }

        /*
         * The two axes have to agree and nothing said so. Avoidance mode decides whether the HordeAgent is enabled,
         * and that same component is what puts a body on the flow field's roster, so any mode other than the
         * separation system leaves the crowd with no place on the field. That is fine and intended while the
         * technique is the NavMeshAgent, which is the comparison the mode exists to make, and it is a crowd nothing
         * drives at all while the technique is the flow field.
         *
         * Warned rather than corrected, because either half is a reasonable thing to have meant and picking one
         * would silently discard the other. Once per time the pair falls out of step, not once per frame.
         */
        /// <summary>
        /// Says so when the avoidance mode and the pathfinding technique cannot both be what they are.
        /// </summary>
        private void WarnAboutTechnique()
        {
            HordePathScheduler scheduler = HordePathScheduler.Instance;

            bool handsBodiesToAgents = mode != HordeBenchmarkMode.SEPARATION_SYSTEM;
            bool onField = scheduler && scheduler.Technique == HordePathTechnique.FLOW_FIELD;

            if (!handsBodiesToAgents || !onField)
            {
                _warnedAboutTechnique = false;
                return;
            }

            if (_warnedAboutTechnique) return;

            _warnedAboutTechnique = true;

            Debug.LogWarning(
                $"[HordeBenchmark] Mode {mode} switches every HordeAgent off, and that is also the component that puts a body " +
                $"on the flow field's roster, so nothing is left driving the crowd while the technique is {HordePathTechnique.FLOW_FIELD}. " +
                $"Set the HordePathScheduler's technique to {HordePathTechnique.NAVMESH_AGENT} to measure this mode, " +
                $"or set the mode back to {HordeBenchmarkMode.SEPARATION_SYSTEM} to stay on the field.",
                this);
        }

        /*
         * Setting the destination on the agent above is not the same as giving the entity a target. An entity with
         * no target reports that repathing it would mean nothing, so a prefab that does carry one would register
         * with the scheduler and then be skipped by it forever, which reads as the scheduler quietly doing nothing.
         */
        /// <summary>
        /// Hands the chase target to whichever horde entity the prefab carries, if it carries one at all.
        /// </summary>
        private void AssignTarget(GameObject spawned)
        {
            if (!target) return;

            IHordeTargetable targetable = spawned.GetComponentInChildren<IHordeTargetable>(true);

            targetable?.SetTarget(target);
        }

        /*
         * Repathing is driven from here rather than left to each enemy's own timer, so the load is a number that can be
         * set and compared instead of one that emerges from two thousand independent countdowns.
         * It is also how a real horde would do it: a fixed budget of path requests per frame, spread round robin,
         * rather than every agent deciding for itself and all of them landing on the same frame.
         */
        private void RepathBatch()
        {
            if (!repathToTarget || !target || _agents.Count == 0) return;

            /*
             * Stood down when a scheduler is actually pacing something, not merely when one exists. This spawner
             * wires a NavMeshAgent and a separation component and nothing else, so a prefab with no IRepathBody on
             * it registers with no scheduler however many are in the scene. Standing down on the component being
             * present rather than on it having work left the crowd walking at wherever the target was on frame one.
             */
            if (HordePathScheduler.Instance && HordePathScheduler.Instance.BodyCount > 0) return;

            Vector3 destination = target.position;
            int count = Mathf.Min(repathsPerFrame, _agents.Count);

            for (int i = 0; i < count; i++)
            {
                NavMeshAgent agent = _agents[_repathCursor];
                _repathCursor = _repathCursor + 1 >= _agents.Count ? 0 : _repathCursor + 1;

                if (!agent || !agent.isOnNavMesh) continue;

                agent.SetDestination(destination);
            }
        }

        /// <summary>
        /// Runs the probe job once so the overlay can say whether Burst really compiled the separation jobs.
        /// </summary>
        private void ProbeBurst()
        {
            NativeArray<bool> result = new NativeArray<bool>(1, Allocator.TempJob);
            new BurstProbeJob { Result = result }.Schedule().Complete();
            _burstActive = result[0];
            _burstProbed = true;
            result.Dispose();
        }

    }
}

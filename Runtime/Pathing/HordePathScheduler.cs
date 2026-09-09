using System.Collections.Generic;
using System.Text;
using MiHordeTraffic.Benchmark;
using MiHordeTraffic.Movement;
using MiHordeTraffic.Pathing.FlowField;
using Unity.Profiling;
using UnityEngine;

namespace MiHordeTraffic.Pathing
{
    /*
     * Repathing a horde every frame is wasted work and repathing it on one shared interval is wasted in a worse way:
     * the agents about to reach the player and the agents still crossing the map get the same share of a budget that
     * only the first group can do anything with.
     *
     * Priority here is not a sort. Each body's interval is a function of how far it is from its goal, so a body ten
     * metres out repaths ten times as often as one sixty metres out without anything ever ranking them against each
     * other. Sorting would only change which bodies win a single frame that ran out of budget, and rotating where
     * the scan starts spreads that around more evenly than a sort would anyway.
     *
     * The guarantee pass is separate and runs first. Distance based pacing alone can starve a body indefinitely if
     * the near ranks always fill the budget, and a body that never repaths is a body walking at where the player
     * used to be. Anything past the guarantee interval jumps the queue regardless of how far away it is.
     *
     * Both passes are managed field reads and float compares over the roster. That is a few microseconds at a few
     * thousand bodies, and it buys the right to spend the expensive part, which is the path request itself,
     * only on the bodies that are worth it.
     */
    /*
     * Runs ahead of the mover. A technique may rebuild the shared field in PrepareFrame, and the movement job
     * reads that same field, so deciding paths has to finish before anything is scheduled against it. Left to
     * default ordering the rebuild would land midway through a frame the movement job was already reading.
     */
    /// <summary>
    /// Decides which bodies repath each frame, favouring the ones nearest their goal without starving the rest.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)]
    public class HordePathScheduler : MonoBehaviour
    {

        private const int SAMPLE_CAPACITY = 4096;
        private const int WARMUP_FRAMES = 30;

        private static readonly ProfilerMarker TICK_MARKER = new ProfilerMarker("MiHordeTraffic.Pathing.Tick");
        private static readonly ProfilerMarker SCHEDULE_MARKER = new ProfilerMarker("MiHordeTraffic.Pathing.Schedule");

        /*
         * Registration is queued when no scheduler exists yet, because a body's OnEnable can easily run before the
         * scheduler's Awake, whether from script execution order or from a body that was already in the scene.
         * Dropping those on the floor meant a body silently never repathed for the rest of its life, which looks
         * exactly like the scheduler working and choosing not to.
         */
        private static readonly List<IRepathBody> PENDING = new List<IRepathBody>();

        private static HordePathScheduler _instance;

        /// <summary>
        /// The scheduler in the loaded scene, or null when there is none, in which case bodies pace themselves.
        /// </summary>
        public static HordePathScheduler Instance => _instance;

        /*
         * Serialized so it can be flipped in the inspector while the game runs, which is the point: the crowd
         * changes technique in place, keeping its positions, its goals and the same budget and pacing around it,
         * so the only thing that differs between two readings is the technique itself.
         */
        [Header("Technique")]
        [SerializeField] private HordePathTechnique technique = HordePathTechnique.NAVMESH_AGENT;

        [Header("Budget")]
        [SerializeField, Min(1)] private int repathsPerFrame = 100;

        [Header("Pacing")]
        [SerializeField, Min(0f)] private float nearInterval = .1f;
        [SerializeField, Min(0f)] private float farInterval = 1.5f;
        [SerializeField, Min(0f)] private float nearDistance = 10f;
        [SerializeField, Min(0f)] private float farDistance = 60f;

        /*
         * Off at zero, and worth leaving on. It records nothing unless a frame turns out to be among the slowest of
         * the run, so the cost is one compare a frame against the slowest already kept, which almost every frame
         * fails. Four is enough to tell a recurring shape from a one off; a bigger project wanting more can say so.
         */
        [Header("Diagnostics")]
        [Tooltip("How many of the slowest frames to keep a full phase breakdown for. Zero switches it off.")]
        [SerializeField, Min(0)] private int worstFramesTracked = 4;

        [Header("Starvation")]
        [SerializeField, Min(0f)] private float guaranteedInterval = 4f;

        private readonly List<IRepathBody> _bodies = new List<IRepathBody>();

        private readonly HordePathStats[] _stats = new HordePathStats[System.Enum.GetValues(typeof(HordePathTechnique)).Length];

        /*
         * Frame time kept per technique alongside the scheduler's own cost, because the scheduler's cost is not the
         * answer on its own. A technique that saves a quarter of a millisecond here and hands the bodies to
         * something that costs three more is not cheaper, and the only number that settles that is what the frame
         * actually took while it was running.
         */
        private readonly FrameTimeSamples[] _frames = BuildSamples();

        /*
         * Switching technique enables or disables a component on every body in the crowd, and the frame that
         * happens on is worth several normal ones. Recording it would put a spike into the distribution of
         * whichever technique was just switched to, which is precisely the technique it says nothing about.
         */
        private int _warmup;

        private readonly HordePhaseTimings _phases = new HordePhaseTimings();

        private HordeWorstFrames _worst;

        private IHordePathTechnique _active;
        private HordePathTechnique _activeTechnique;
        private int _cursor;
        private int _repathsLastFrame;

        /// <summary>
        /// How many bodies the scheduler is pacing.
        /// </summary>
        public int BodyCount => _bodies.Count;

        /// <summary>
        /// Which technique is currently driving repathing.
        /// </summary>
        public HordePathTechnique Technique => technique;

        /// <summary>
        /// What a technique has cost since it was last reset, whether or not it is the active one.
        /// </summary>
        /// <param name="value">The technique to report on.</param>
        /// <returns>Its accumulated statistics.</returns>
        public HordePathStats StatsFor(HordePathTechnique value) => _stats[(int)value];

        /// <summary>
        /// Switches technique in place, keeping the crowd, the budget and the pacing exactly as they are.
        /// </summary>
        /// <param name="value">The technique to switch to.</param>
        public void SetTechnique(HordePathTechnique value) => technique = value;

        /// <summary>
        /// Throws away the accumulated statistics for every technique, to re-baseline after a change.
        /// </summary>
        public void ClearStats()
        {
            for (int i = 0; i < _stats.Length; i++)
            {
                _stats[i].Clear();
                _frames[i].Clear();
            }

            _phases.Clear();
            _worst?.Clear();
        }

        /*
         * Printed on quit rather than only drawn on an overlay, because an overlay cannot be copied out of and the
         * whole point of running two techniques over the same crowd is being able to put their numbers next to each
         * other afterwards. Stopping play is the one moment every run has in common.
         */
        /// <summary>
        /// Everything this session measured, as text.
        /// </summary>
        /// <returns>A multi line report.</returns>
        public string Report()
        {
            StringBuilder text = new StringBuilder(512);

            text.AppendLine("[MiHordeTraffic] session report");
            text.Append("  bodies       ").Append(_bodies.Count).Append(" scheduled, ")
                .Append(HordeAgent.AgentCount).AppendLine(" horde agents");

            System.Array values = System.Enum.GetValues(typeof(HordePathTechnique));

            for (int i = 0; i < values.Length; i++)
            {
                HordePathTechnique value = (HordePathTechnique)values.GetValue(i);
                FrameTimeSamples samples = _frames[(int)value];
                FrameTimeStats frame = samples.Resolve();

                text.Append("  ").AppendLine(value.ToString());
                text.Append("    frame      ").AppendLine(frame.Count == 0 ? "not run" : frame.ToString());

                if (samples.Dropped > 0)
                    text.Append("               ").Append(samples.Dropped)
                        .AppendLine(" stalls over a second discarded, which is a pause or a load rather than a frame");

                text.Append("    scheduler  ").AppendLine(_stats[(int)value].ToString());
            }

            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;

            if (driver && driver.TotalCells > 0)
            {
                text.Append("  field        ").Append(driver.WalkableCells).Append(" of ").Append(driver.TotalCells)
                    .Append(" cells walkable (").Append(100f * driver.WalkableCells / driver.TotalCells).AppendLine("%)");
                text.Append("    rebuilds   ").Append(driver.Builds).Append(" at ")
                    .Append(driver.AverageBuildMilliseconds.ToString("F3")).AppendLine(" ms average");
            }

            _phases.AppendTo(text);

            if (_phases.Available) _worst?.AppendTo(text, HordePhaseTimings.Names);

            return text.ToString();
        }

        /// <summary>
        /// How many repaths actually went out on the last frame, which is what to watch when tuning the budget.
        /// A number pinned at the budget every frame means the crowd is asking for more than it is being given.
        /// </summary>
        public int RepathsLastFrame => _repathsLastFrame;

        /// <summary>
        /// Takes a copy of the settings that are safe to change at runtime, so they can be restored later.
        /// </summary>
        /// <returns>The current tuning.</returns>
        public SchedulerTuning CaptureTuning() => new SchedulerTuning
        {
            RepathsPerFrame = repathsPerFrame,
            NearInterval = nearInterval,
            FarInterval = farInterval,
            NearDistance = nearDistance,
            FarDistance = farDistance,
            GuaranteedInterval = guaranteedInterval
        };

        /// <summary>
        /// Applies a tuning, taking effect on the next frame.
        /// </summary>
        /// <param name="tuning">The values to apply.</param>
        public void ApplyTuning(SchedulerTuning tuning)
        {
            repathsPerFrame = Mathf.Max(1, tuning.RepathsPerFrame);
            nearInterval = Mathf.Max(0f, tuning.NearInterval);
            farInterval = Mathf.Max(0f, tuning.FarInterval);
            nearDistance = Mathf.Max(0f, tuning.NearDistance);
            farDistance = Mathf.Max(0f, tuning.FarDistance);
            guaranteedInterval = Mathf.Max(0f, tuning.GuaranteedInterval);
        }

        /// <summary>
        /// Puts a body under the scheduler's control. Safe from OnEnable.
        /// </summary>
        /// <param name="body">The body to pace.</param>
        public static void Register(IRepathBody body)
        {
            if (body.RepathIndex >= 0) return;

            if (!_instance)
            {
                if (!PENDING.Contains(body)) PENDING.Add(body);
                return;
            }

            _instance.AddBody(body);
        }

        /// <summary>
        /// Takes a body back off the scheduler.
        /// </summary>
        /// <param name="body">The body to stop pacing.</param>
        public static void Unregister(IRepathBody body)
        {
            PENDING.Remove(body);

            if (!_instance) return;

            int index = body.RepathIndex;
            List<IRepathBody> bodies = _instance._bodies;

            if (index < 0 || index >= bodies.Count || bodies[index] != body) return;

            int last = bodies.Count - 1;
            bodies[index] = bodies[last];
            bodies[index].RepathIndex = index;
            bodies.RemoveAt(last);
            body.RepathIndex = -1;
        }

        /*
         * Created automatically when bodies registered and nobody put one in the scene. Without this a body would
         * have to keep its own Update as a fallback, and an Update that exists only to early out still costs the
         * native dispatch for every instance every frame, which is exactly the cost this is here to remove.
         */
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureExists()
        {
            if (_instance || PENDING.Count == 0) return;

            new GameObject(nameof(HordePathScheduler)).AddComponent<HordePathScheduler>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            PENDING.Clear();
        }

        private static FrameTimeSamples[] BuildSamples()
        {
            int count = System.Enum.GetValues(typeof(HordePathTechnique)).Length;
            FrameTimeSamples[] samples = new FrameTimeSamples[count];

            for (int i = 0; i < count; i++)
                samples[i] = new FrameTimeSamples(SAMPLE_CAPACITY);

            return samples;
        }

        private void Awake()
        {
            if (_instance && _instance != this)
            {
                Destroy(this);
                return;
            }

            _instance = this;
            _worst = new HordeWorstFrames(worstFramesTracked, HordePhaseTimings.PhaseCount);

            for (int i = 0; i < PENDING.Count; i++)
                AddBody(PENDING[i]);

            PENDING.Clear();
        }

        /*
         * Seeded at the current time rather than at zero, so a body that registers mid game is not treated as
         * though it had been waiting since the scene loaded. Otherwise a spawn wave arrives already past the
         * guarantee interval and blows a whole frame's budget on agents that have never had a path to begin with.
         */
        private void AddBody(IRepathBody body)
        {
            if (body.RepathIndex >= 0) return;

            float now = Time.time;

            body.RepathIndex = _bodies.Count;
            body.LastRepathTime = now;
            body.NextRepathTime = now;

            _bodies.Add(body);
        }

        private void OnApplicationQuit() => Debug.Log(Report());

        private void OnDestroy()
        {
            if (_instance != this) return;

            _active?.End();
            _active = null;

            if (HordeFlowFieldDriver.Instance) HordeFlowFieldDriver.Instance.Driven = false;

            for (int i = 0; i < _bodies.Count; i++)
                _bodies[i].RepathIndex = -1;

            _bodies.Clear();
            _instance = null;

            _phases.Dispose();
        }

        private void Update()
        {
            int count = _bodies.Count;
            if (count == 0) return;

            float now = Time.time;
            float deltaTime = Time.deltaTime;
            int remaining = repathsPerFrame;

            using (TICK_MARKER.Auto())
            {
                for (int i = 0; i < count; i++)
                    _bodies[i].TickBody(deltaTime);
            }

            SyncTechnique();

            /*
             * PrepareFrame is inside the timed region along with the per body passes. A flow field does nearly all
             * of its work there and almost none per body, so timing only the per body half would report it as free
             * and the comparison would be worthless.
             */
            long start = System.Diagnostics.Stopwatch.GetTimestamp();

            using (SCHEDULE_MARKER.Auto())
            {
                _active.PrepareFrame(deltaTime);

                /*
                 * Skipped entirely rather than left to fall through the per body checks. A technique with no per
                 * body work would otherwise pay for two full sweeps of the roster every frame to be told, several
                 * thousand times, that there is nothing to do.
                 */
                if (_active.RepathsBodies)
                {
                    remaining = RunPass(now, remaining, count, true);
                    remaining = RunPass(now, remaining, count, false);
                }
            }

            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;

            _repathsLastFrame = repathsPerFrame - remaining;
            _stats[(int)_activeTechnique].Record(elapsed * 1000000d / System.Diagnostics.Stopwatch.Frequency, _repathsLastFrame);

            if (_warmup > 0) _warmup--;
            else
            {
                float frameMilliseconds = Time.unscaledDeltaTime * 1000f;

                _frames[(int)_activeTechnique].Add(frameMilliseconds);
                _phases.Sample();

                /*
                 * Offered after the sample rather than before it, so the readings belong to the frame being judged
                 * rather than to the one before it.
                 */
                if (_worst != null)
                    _worst.Consider(frameMilliseconds, _phases.Current, HordeFlowMovement.Instance ? HordeFlowMovement.Instance.LastSubsteps : 1);
            }

            /*
             * Advanced by a whole budget rather than to wherever the scan stopped, so the window moves across the
             * roster at a steady rate instead of parking on whichever stretch happened to be due. Without it the
             * same bodies sit at the front of the scan every frame and the far half of the roster only ever gets
             * looked at through the guarantee pass.
             */
            _cursor = count == 0 ? 0 : (_cursor + repathsPerFrame) % count;
        }

        /// <summary>
        /// One sweep of the roster, taking either the bodies past the starvation guarantee or the ones merely due.
        /// </summary>
        /// <param name="now">Current time.</param>
        /// <param name="remaining">How much of this frame's budget is left.</param>
        /// <param name="count">Roster size.</param>
        /// <param name="guaranteed">True to take only bodies past the guarantee interval, false to take due ones.</param>
        /// <returns>The budget left after this pass.</returns>
        private int RunPass(float now, int remaining, int count, bool guaranteed)
        {
            if (remaining <= 0) return 0;

            for (int i = 0; i < count; i++)
            {
                IRepathBody body = _bodies[(_cursor + i) % count];

                if (!body.WantsRepath) continue;

                bool due = guaranteed ? now - body.LastRepathTime >= guaranteedInterval : now >= body.NextRepathTime;
                if (!due) continue;

                float distance = _active.Repath(body, now);

                body.LastRepathTime = now;
                body.NextRepathTime = now + IntervalFor(distance);

                if (--remaining <= 0) return 0;
            }

            return remaining;
        }

        /*
         * Swapped here rather than in a setter, so changing the field straight from the inspector mid play works
         * the same way as calling SetTechnique. Anything else would make the inspector lie about what is running.
         */
        private void SyncTechnique()
        {
            /*
             * Claimed for as long as a scheduler exists, not for as long as the flow field is the active technique.
             * Left to the driver's own LateUpdate, the field goes on expanding while the baseline is running: work
             * nothing reads, charged to no technique, and added to the frame time of the row it is not part of.
             * Only the flow field technique ever calls Rebuild, so claiming it here means the field is maintained
             * exactly when something is using it.
             */
            if (HordeFlowFieldDriver.Instance) HordeFlowFieldDriver.Instance.Driven = true;

            if (_active != null && _activeTechnique == technique) return;

            _active?.End();
            _active = CreateTechnique(technique);
            _activeTechnique = technique;
            _active.Begin();
            _warmup = WARMUP_FRAMES;
        }

        /*
         * An unrecognised technique falls back and rewrites the field, rather than quietly running the baseline
         * under the other one's name. Stats are kept per technique, so a silent fallback would file a reading
         * against a technique that never ran, and the whole point of this harness is that its rows can be trusted.
         */
        private IHordePathTechnique CreateTechnique(HordePathTechnique value)
        {
            switch (value)
            {
                case HordePathTechnique.NAVMESH_AGENT:
                    return new NavMeshAgentPathTechnique();

                case HordePathTechnique.FLOW_FIELD:
                    return new FlowFieldPathTechnique();

                default:
                    Debug.LogWarning($"[MiHordeTraffic] {value} is not implemented, falling back to {HordePathTechnique.NAVMESH_AGENT}.");
                    technique = HordePathTechnique.NAVMESH_AGENT;
                    return new NavMeshAgentPathTechnique();
            }
        }

        /// <summary>
        /// How long a body that far from its goal should wait before its next repath.
        /// </summary>
        /// <param name="distance">Distance from the body to its goal.</param>
        /// <returns>Seconds until this body is due again.</returns>
        private float IntervalFor(float distance)
        {
            if (farDistance <= nearDistance) return nearInterval;

            float t = Mathf.Clamp01((distance - nearDistance) / (farDistance - nearDistance));
            return Mathf.Lerp(nearInterval, farInterval, t);
        }

    }
}

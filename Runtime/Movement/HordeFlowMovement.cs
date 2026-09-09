using System.Collections.Generic;
using MiHordeTraffic.Pathing.FlowField;
using MiHordeTraffic.Tuning;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;

namespace MiHordeTraffic.Movement
{
    /*
     * Owns movement for every body that reads the flow field. One transform job a frame for the whole crowd,
     * against one NavMeshAgent update and one agent.Move per body before it.
     *
     * The gather loop before the job is managed reads only: a float3 and a float off each body. There is no call
     * out to native anywhere in this class, which is the entire difference. The old hand off cost 1.8 ms of
     * agent.Move plus over a millisecond of transform synchronisation it forced on the navmesh system, to protect
     * a crowd simulation that was itself costing four hundredths of a millisecond.
     *
     * Scheduled in Update and joined in LateUpdate, so the workers have the rest of the frame's script code to run
     * alongside. Nothing reads these transforms in between except the engine, which syncs on its own.
     */
    /// <summary>
    /// Moves every registered body along the flow field in one job, replacing NavMeshAgent driven movement.
    /// </summary>
    /*
     * Ordered ahead of the separation system on purpose. Both touch the same transforms from jobs, and the order
     * between two components' Update is otherwise whatever Unity feels like: with movement second, separation
     * samples positions while the move job is still writing them, and the pushes that come out are computed from
     * a crowd that is half a frame old in places. That reads as bodies buzzing against each other for no reason,
     * and it changes with anything that shifts the timing, which is why it looked like a scene view problem.
     */
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class HordeFlowMovement : MonoBehaviour
    {

        private const int MINIMUM_CAPACITY = 64;
        private const float HEXAGONAL_PACKING = .866f;

        /*
         * Sized for the active set rather than for the grid. These passes used to run over every cell, where a
         * thousand at a time was right; they now run over the cells the crowd is actually standing in, which is a
         * few thousand at most, and a batch of a thousand would hand the whole job to two or three workers and
         * leave the rest idle.
         */
        private const int CONGESTION_BATCH = 128;

        private static readonly ProfilerMarker GATHER_MARKER = new ProfilerMarker("MiHordeTraffic.Movement.Gather");
        private static readonly ProfilerMarker MOVE_MARKER = new ProfilerMarker("MiHordeTraffic.Movement.Move");
        /*
         * Its own marker because everything under it blocks the main thread, unlike the last substep, which is the
         * only part of the mover that overlaps with the rest of the frame. Folded into Move it would have read as
         * the crowd waiting on a worker when it is really the crowd being solved several times over on the spot.
         */
        private static readonly ProfilerMarker SCHEDULE_MARKER = new ProfilerMarker("MiHordeTraffic.Movement.Schedule");
        /*
         * Separate from Move because Move is a wait on a worker and this is a sweep of the roster, and folding a
         * loop over every body into the row that reports how long the main thread sat idle is how a real cost
         * hides. It was outside every marker until now, which made the phases table read lower than the truth.
         */
        private static readonly ProfilerMarker PUBLISH_MARKER = new ProfilerMarker("MiHordeTraffic.Movement.Publish");

        private static readonly List<HordeAgent> PENDING_REGISTER = new List<HordeAgent>();
        private static readonly List<HordeAgent> PENDING_UNREGISTER = new List<HordeAgent>();

        private static HordeFlowMovement _instance;

        /// <summary>
        /// The mover in the loaded scene, or null when there is none.
        /// </summary>
        public static HordeFlowMovement Instance => _instance;

        [SerializeField] private HordeFlowFieldDriver driver;

        /*
         * The numbers below are grouped under whichever of these owns them, because on their own most of them
         * cannot be read: nothing about a rise smoothing of five says whether five is a lot, or which way to move
         * it, or which other three fields have to move with it to mean anything.
         *
         * A preset writes its values into those fields rather than hiding them, so picking one shows what it chose
         * and the numbers stay there to be read and compared. The consequence is that editing a governed field by
         * hand snaps back on the next validate, which is the honest behaviour: while a preset owns a field, the
         * preset is what the field says. CUSTOM hands the fields back and changes nothing.
         */
        [Header("Presets")]
        [Tooltip("How fast bodies turn onto a new direction. Set to CUSTOM to edit the turn rates by hand.")]
        [SerializeField] private HordeTurnStyle turnStyle = HordeTurnStyle.NATURAL;
        [Tooltip("How quickly bodies reach walking pace and give up when blocked. CUSTOM to edit by hand.")]
        [SerializeField] private HordeAgility agility = HordeAgility.NATURAL;
        [Tooltip("How much bodies ease off for a crowded cell ahead. CUSTOM to edit by hand.")]
        [SerializeField] private HordeCrowdPressure crowdPressure = HordeCrowdPressure.BALANCED;
        [Tooltip("How far the crowd will detour around itself. CUSTOM to edit by hand.")]
        [SerializeField] private HordeCongestionResponse congestionResponse = HordeCongestionResponse.BALANCED;

        [Header("Turning")]
        [Tooltip("Owned by Turn Style. How fast the model spins to face its heading, as a rate per second.")]
        [SerializeField, Min(0f)] private float turnSpeed = 12f;
        [Tooltip("Owned by Turn Style. How fast the walking direction itself turns, in radians per second.")]
        [SerializeField, Min(.01f)] private float headingTurnRate = 3.5f;

        /*
         * Deceleration is deliberately sharper than acceleration. Running into a crowd should register at once,
         * while leaving one can afford to be gradual, and the asymmetry is what stops a body oscillating between
         * pressing and giving up as the crowd ahead of it shifts.
         */
        [Tooltip("Owned by Agility. Metres per second gained each second.")]
        [SerializeField, Min(.1f)] private float acceleration = 10f;
        [Tooltip("Owned by Agility. Metres per second lost each second, deliberately sharper than acceleration.")]
        [SerializeField, Min(.1f)] private float deceleration = 25f;
        [Tooltip("Owned by Agility. Below this fraction of its commanded speed a body decides it is stuck and stops pushing.")]
        [SerializeField, Range(.05f, 1f)] private float stallFraction = .5f;

        /*
         * Off by default and free when off: one branch per body against a bool that is the same for all of them,
         * which the compiler and the branch predictor both handle for nothing. Nothing is allocated or measured
         * for it either way, so a project that does not want it pays nothing to have the option.
         */
        [Header("Facing")]
        [SerializeField] private bool requireFacing = false;
        [Tooltip("Metres from the goal at which a body counts as arrived and stops being driven.")]
        [SerializeField, Min(0f)] private float arriveRadius = 1.5f;
        [Tooltip("Metres over which a body eases to a stop before that, rather than switching off at a line.")]
        [SerializeField, Min(.01f)] private float arriveTaper = 3f;

        [Header("Advanced")]
        [SerializeField] private HordeFlowAdvanced advanced = new HordeFlowAdvanced();

        /*
         * Off by default. Congestion changes where the crowd goes, not just how fast it gets there, so it wants
         * turning on deliberately and watching rather than arriving as a default nobody asked for.
         */
        [Header("Crowding")]
        /*
         * On by default, unlike congestion routing. Slowing for a crowd changes how a body moves, not where it
         * goes, so it is a smoothness fix rather than a behaviour change and there is little reason to run without it.
         */
        [SerializeField] private bool slowInCrowds = true;

        /*
         * Fractions of what a cell can physically hold, not body counts. The capacity is worked out each frame
         * from the crowd's own radius and the cell size, so these stay meaningful when either changes. Raw counts
         * did not: the previous defaults were above anything a one metre cell could ever contain, which made both
         * crowd slowing and the occupancy half of congestion unreachable while looking perfectly configured.
         */
        [Tooltip("Owned by Crowd Pressure. Fraction of a cell's capacity at which bodies begin slowing.")]
        [SerializeField, Range(0f, 1f)] private float comfortableFill = .5f;
        [Tooltip("Owned by Crowd Pressure. Fraction of capacity treated as completely full.")]
        [SerializeField, Range(.01f, 1.5f)] private float jamFill = .95f;
        [Tooltip("Owned by Crowd Pressure. Slowest a body may be driven, as a fraction of its own speed.")]
        [SerializeField, Range(.01f, 1f)] private float minimumSpeedFraction = .25f;
        [Tooltip("How hard a body still walks after separation says it has settled, as a fraction of its speed. One is the old behaviour: settled bodies keep pressing at full pace and the crowd at a destination never stops shoving itself.")]
        [SerializeField, Range(0f, 1f)] private float settledDriveScale = .25f;
        [Tooltip("Seconds a body keeps trying to walk into something before it accepts it is stuck and stops pressing. Zero switches it off, and a jam that is not at the goal will then never calm down.")]
        [SerializeField, Min(0f)] private float stallSettleDelay = .5f;
        [Header("Congestion")]
        [SerializeField] private bool congestionEnabled = false;
        /*
         * Congestion is a property of a region, not of a cell, and a one-cell detour is cheap enough that an
         * unsmoothed field will bend routes around a single body. Averaging the cost over a neighbourhood is what
         * keeps the field's features large enough to be worth walking around. Zero disables it and costs nothing.
         */
        [Tooltip("Owned by Congestion Response. Cells averaged over, so one body cannot make one cell expensive.")]
        [SerializeField, Range(0, 3)] private int costBlurRadius = 1;
        /*
         * How quickly a body's own progress average follows what it just did. Low enough that shuffling on the
         * spot cancels out over the window instead of counting as movement, high enough that a jam is priced
         * within about a second of forming.
         */
        [Tooltip("Owned by Congestion Response. Higher reacts sooner, lower cancels shuffling on the spot harder.")]
        [SerializeField, Min(.1f)] private float progressSmoothing = 4f;
        /*
         * High on purpose. This is the ceiling on how much worse a jammed cell may look than an empty one, so it is
         * also the longest detour the field is ever able to justify: at six, a route through a solid queue can only
         * ever look six times its length, and any alternative longer than that stays unattractive no matter how bad
         * the jam gets. Real queues are far worse than six times walking pace.
         */
        [Tooltip("Owned by Congestion Response. The longest detour the field can ever justify, as a multiple of route length.")]
        [SerializeField, Min(1f)] private float maximumCost = 30f;
        /*
         * Rise fast, fall slow. The gap between them is what stops the crowd swapping routes: a jam has to build
         * before the field believes it, and has to stay cleared before the field believes that too.
         */
        [Tooltip("Owned by Congestion Response. How fast a jam is believed. Higher reacts sooner.")]
        [SerializeField, Min(0f)] private float riseSmoothing = 3f;
        [Tooltip("Owned by Congestion Response. How fast a cleared route stops looking expensive. Keep below the rise or the crowd swaps routes.")]
        [SerializeField, Min(0f)] private float fallSmoothing = 1f;

        private readonly List<HordeAgent> _bodies = new List<HordeAgent>();

        private TransformAccessArray _transforms;
        private NativeArray<float3> _push;
        private NativeArray<float> _speed;
        private NativeArray<float3> _positions;
        private NativeArray<float3> _previousPositions;
        private NativeArray<float2> _heading;
        private NativeArray<float> _currentSpeed;
        private NativeArray<float> _openSpeed;
        private NativeArray<float> _progress;
        private NativeArray<byte> _arrived;
        private NativeArray<byte> _frozen;
        private NativeArray<byte> _settled;
        private NativeArray<byte> _stalled;
        private NativeArray<float> _stallTime;
        private NativeArray<float3> _lastPosition;
        private NativeArray<float> _speedSum;
        private NativeArray<float> _referenceSum;
        private NativeArray<float> _reference;
        private NativeArray<int> _counts;
        private NativeArray<int> _occupancy;

        /*
         * Which cells the congestion pass is still tracking, and a flag per cell saying so. Everything congestion
         * does used to run over the whole grid every frame, which is work proportional to the map rather than to
         * the crowd: five thousand bodies stand in at most five thousand cells, and on a kilometre of map at one
         * metre the other nine hundred and ninety five thousand were being cleared and repriced to say nothing.
         *
         * A list and a flag rather than a set, because the two questions asked of it are different. Walking the
         * tracked cells wants a dense list, and asking whether one particular cell is already tracked wants an
         * answer without a search.
         */
        private NativeArray<byte> _touched;
        private NativeList<int> _active;
        private int _congestionCells;
        private float _cellCapacity = 1f;

        private JobHandle _handle;
        private bool _scheduled;
        private int _capacity;

        /// <summary>
        /// How many bodies are being moved by the field.
        /// </summary>
        public int BodyCount => _bodies.Count;

        /// <summary>
        /// The field driver this mover reads, or null when none has been assigned.
        /// </summary>
        public HordeFlowFieldDriver Driver => driver;

        /// <summary>
        /// How many pieces the last frame's step had to be cut into to keep bodies moving less than half a cell.
        /// Anything above one at normal speed means bodies cover ground faster than the grid can describe.
        /// </summary>
        public int LastSubsteps { get; private set; } = 1;

        /*
         * Read back so a body can show what it was actually told to do, rather than what it looks like it is doing.
         * Every stuck body so far has been diagnosed by guessing at which of flow, push and heading was wrong, and
         * they are indistinguishable from outside.
         */
        /// <summary>
        /// The heading and separation push most recently used for a body, for debug drawing.
        /// </summary>
        /// <param name="index">The body's FlowIndex.</param>
        /// <param name="heading">Direction the body is steering, which lags the field by the turn rate.</param>
        /// <param name="push">Separation push it was given.</param>
        /// <returns>True when the index is live.</returns>
        public bool TryGetState(int index, out float2 heading, out float3 push)
        {
            heading = float2.zero;
            push = float3.zero;

            if (index < 0 || index >= _bodies.Count) return false;

            heading = _heading[index];
            push = _push[index];
            return true;
        }

        /// <summary>
        /// Whether measured congestion is being fed back into the field's traversal costs.
        /// </summary>
        public bool CongestionEnabled => congestionEnabled;

        /// <summary>
        /// Turns congestion weighting on or off at runtime.
        /// </summary>
        /// <param name="value">True to price cells by how fast bodies cross them.</param>
        public void SetCongestionEnabled(bool value) => congestionEnabled = value;

        /// <summary>
        /// Queues a body to be moved by the field. Safe from OnEnable.
        /// </summary>
        /// <param name="body">The body to move.</param>
        public static void Register(HordeAgent body)
        {
            if (PENDING_UNREGISTER.Remove(body)) return;

            PENDING_REGISTER.Add(body);
        }

        /// <summary>
        /// Queues a body to stop being moved by the field. Safe from OnDisable.
        /// </summary>
        /// <param name="body">The body to drop.</param>
        public static void Unregister(HordeAgent body)
        {
            if (PENDING_REGISTER.Remove(body)) return;

            PENDING_UNREGISTER.Add(body);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            PENDING_REGISTER.Clear();
            PENDING_UNREGISTER.Clear();
        }

        /*
         * Applied unconditionally rather than only when the dropdown changes. A preset owns its fields for as long
         * as it is selected, so a hand edit to a governed field snapping back on the next validate is the correct
         * answer rather than a nuisance: it is the inspector saying which of the two is in charge.
         */
        private void OnValidate() => ApplyPresets();

        /// <summary>
        /// Writes every preset's values into the fields it governs, leaving anything set to CUSTOM alone.
        /// </summary>
        private void ApplyPresets()
        {
            ApplyTurnStyle();
            ApplyAgility();
            ApplyCrowdPressure();
            ApplyCongestionResponse();
        }

        private void ApplyTurnStyle()
        {
            switch (turnStyle)
            {
                case HordeTurnStyle.HEAVY:
                    headingTurnRate = 1.5f;
                    turnSpeed = 6f;
                    break;

                case HordeTurnStyle.NATURAL:
                    headingTurnRate = 3.5f;
                    turnSpeed = 12f;
                    break;

                case HordeTurnStyle.AGILE:
                    headingTurnRate = 7f;
                    turnSpeed = 18f;
                    break;

                case HordeTurnStyle.INSTANT:
                    headingTurnRate = 20f;
                    turnSpeed = 40f;
                    break;
            }
        }

        private void ApplyAgility()
        {
            switch (agility)
            {
                case HordeAgility.SLUGGISH:
                    acceleration = 3f;
                    deceleration = 8f;
                    stallFraction = .5f;
                    break;

                case HordeAgility.NATURAL:
                    acceleration = 10f;
                    deceleration = 25f;
                    stallFraction = .5f;
                    break;

                case HordeAgility.BRISK:
                    acceleration = 25f;
                    deceleration = 50f;
                    stallFraction = .6f;
                    break;

                case HordeAgility.INSTANT:
                    acceleration = 200f;
                    deceleration = 200f;
                    stallFraction = .9f;
                    break;
            }
        }

        private void ApplyCrowdPressure()
        {
            switch (crowdPressure)
            {
                case HordeCrowdPressure.IGNORE:
                    slowInCrowds = false;
                    break;

                case HordeCrowdPressure.POLITE:
                    slowInCrowds = true;
                    comfortableFill = .75f;
                    jamFill = 1.1f;
                    minimumSpeedFraction = .5f;
                    break;

                case HordeCrowdPressure.BALANCED:
                    slowInCrowds = true;
                    comfortableFill = .5f;
                    jamFill = .95f;
                    minimumSpeedFraction = .25f;
                    break;

                case HordeCrowdPressure.STRICT:
                    slowInCrowds = true;
                    comfortableFill = .3f;
                    jamFill = .8f;
                    minimumSpeedFraction = .1f;
                    break;
            }
        }

        private void ApplyCongestionResponse()
        {
            switch (congestionResponse)
            {
                case HordeCongestionResponse.OFF:
                    congestionEnabled = false;
                    break;

                case HordeCongestionResponse.SUBTLE:
                    congestionEnabled = true;
                    maximumCost = 8f;
                    riseSmoothing = 3f;
                    fallSmoothing = 1f;
                    progressSmoothing = 3f;
                    costBlurRadius = 2;
                    break;

                case HordeCongestionResponse.BALANCED:
                    congestionEnabled = true;
                    maximumCost = 30f;
                    riseSmoothing = 5f;
                    fallSmoothing = 2f;
                    progressSmoothing = 4f;
                    costBlurRadius = 1;
                    break;

                case HordeCongestionResponse.AGGRESSIVE:
                    congestionEnabled = true;
                    maximumCost = 60f;
                    riseSmoothing = 8f;
                    fallSmoothing = 1f;
                    progressSmoothing = 6f;
                    costBlurRadius = 1;
                    break;
            }
        }

        private void Awake()
        {
            ApplyPresets();

            if (_instance && _instance != this)
            {
                Destroy(this);
                return;
            }

            _instance = this;
            _transforms = new TransformAccessArray(MINIMUM_CAPACITY);
            EnsureCapacity(MINIMUM_CAPACITY);
        }

        private void OnDestroy()
        {
            /*
             * Guarded so that joining cannot cost us the buffers. On quit the engine tears components down in no
             * particular order, so a job still in flight can find a container another component has already freed,
             * and the safety checks turn that into an exception thrown out of Complete. Everything below would then
             * never run, and native memory that nothing owns any more is a leak the collections detector reports at
             * the next domain reload with no stack to point at.
             */
            try
            {
                Complete();
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, this);
            }

            if (_transforms.isCreated) _transforms.Dispose();
            if (_push.IsCreated) _push.Dispose();
            if (_speed.IsCreated) _speed.Dispose();
            if (_positions.IsCreated) _positions.Dispose();
            if (_reference.IsCreated) _reference.Dispose();
            if (_arrived.IsCreated) _arrived.Dispose();
            if (_frozen.IsCreated) _frozen.Dispose();
            if (_settled.IsCreated) _settled.Dispose();
            if (_stalled.IsCreated) _stalled.Dispose();
            if (_stallTime.IsCreated) _stallTime.Dispose();
            if (_previousPositions.IsCreated) _previousPositions.Dispose();
            if (_heading.IsCreated) _heading.Dispose();
            if (_currentSpeed.IsCreated) _currentSpeed.Dispose();
            if (_openSpeed.IsCreated) _openSpeed.Dispose();
            if (_progress.IsCreated) _progress.Dispose();
            if (_lastPosition.IsCreated) _lastPosition.Dispose();
            if (_speedSum.IsCreated) _speedSum.Dispose();
            if (_referenceSum.IsCreated) _referenceSum.Dispose();
            if (_counts.IsCreated) _counts.Dispose();
            if (_occupancy.IsCreated) _occupancy.Dispose();
            if (_touched.IsCreated) _touched.Dispose();
            if (_active.IsCreated) _active.Dispose();

            _bodies.Clear();

            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            Complete();
            DrainRoster();

            GridFlowField field = driver ? driver.Field : null;

            if (field == null || !field.IsBuilt || _bodies.Count == 0) return;

            /*
             * Shared by the movement and the measurement. Bodies move by this step, so the distance they covered
             * has to be divided by the same one, or a clamped frame would read as everybody slowing down and price
             * the whole map as congested.
             */
            float step = math.min(Time.deltaTime, advanced.MaximumStep * math.max(Time.timeScale, .0001f));

            float radiusSum = 0f;
            float fastest = 0f;

            /*
             * Handed to separation below, because until now nothing did. Separation settles a crowd ring by ring:
             * the bodies that arrived stop first, the ones behind them notice that the neighbours between them and
             * the goal have stopped, and the stop spreads outwards. All of that is measured against the body's own
             * goal, and in this mode the only component that ever set one was the optional HordeEntity. A body
             * running on HordeAgent alone reported no goal at all, so every ring past the first took the no goal
             * exit and never settled, and a destination collected a pile of bodies pressing inwards forever.
             *
             * The driver's target rather than anything per body, because that is what the field is built towards
             * and therefore what every body on it is genuinely walking at.
             */
            Transform destination = driver.Target;
            bool hasDestination = destination;
            float3 goal = hasDestination ? (float3)destination.position : float3.zero;

            using (GATHER_MARKER.Auto())
            {
                for (int i = 0; i < _bodies.Count; i++)
                {
                    HordeAgent body = _bodies[i];

                    /*
                     * Read straight off the body now that separating and moving are one component. Splitting them
                     * meant a null test on a second UnityEngine.Object per body per frame, which is a native call
                     * to ask a question that could not change while the body was registered.
                     */
                    _push[i] = body.LastPush;
                    _speed[i] = body.Speed;
                    _frozen[i] = (byte)(body.IsFrozen ? 1 : 0);
                    _settled[i] = (byte)(body.IsSeparationSettled ? 1 : 0);

                    if (hasDestination) body.SetSeparationGoal(goal);
                    else body.ClearSeparationGoal();

                    /*
                     * Frozen bodies are left out of the fastest reading. They are not going anywhere, so letting
                     * one set the substep count would cut the frame up for movement that is not happening.
                     */
                    if (!body.IsFrozen) fastest = math.max(fastest, body.Speed);

                    /*
                     * Sampled here because the loop is already touching every body, so learning how much room the
                     * crowd takes up costs an add rather than a pass of its own.
                     */
                    radiusSum += body.SeparationRadius;
                }
            }

            _cellCapacity = CapacityFor(radiusSum / _bodies.Count, field.Grid.CellSize);

            using (SCHEDULE_MARKER.Auto())
            {
                /*
                 * Handed to the mover as a dependency rather than joined here. Congestion is a single threaded job
                 * over every body and every cell, so joining it on the spot parked the main thread for the whole of
                 * it, and at five thousand bodies that was the most expensive thing the mover did by some way,
                 * spent entirely on waiting. Chaining says the same thing the join said, that the mover may not
                 * write the positions and reference speeds congestion is reading, and says it to the scheduler
                 * instead of to the clock.
                 *
                 * What the join was protecting is still protected. The field rebuild reads the cost array this
                 * writes, and it runs either from the path scheduler at minus two hundred or from the driver's own
                 * LateUpdate, and this component completes in a LateUpdate at minus one hundred, so the chain has
                 * always finished before either of them looks.
                 */
                JobHandle dependency = ScheduleOccupancy(field, step);

                /*
                 * All but the last substep are run to completion here, and only the final one is left in flight for
                 * LateUpdate to join. That keeps the ordinary single substep case exactly as it was, scheduled once
                 * and overlapped with the rest of the frame, and pays the synchronous cost only while the clock is
                 * pushed past what one step can carry.
                 */
                int substeps = SubstepsFor(step, fastest, field.Grid.CellSize);
                float substep = step / substeps;

                LastSubsteps = substeps;

                /*
                 * Cleared after the first schedule, whichever one that turns out to be, because a dependency that
                 * has already been joined is not one any later job needs to carry.
                 */
                for (int i = 1; i < substeps; i++)
                {
                    ScheduleMove(field, substep, dependency).Complete();
                    dependency = default;
                }

                _handle = ScheduleMove(field, substep, dependency);
            }

            _scheduled = true;
            JobHandle.ScheduleBatchedJobs();
        }

        /*
         * Below the slowest a body that has already given up could still legitimately be driven, which is what
         * keeps giving up from being permanent.
         *
         * A settled body is driven at SettledDriveScale, and on top of that the crowd term can take it down to
         * MinimumSpeedFraction, so the two multiplied are the least it can be asked for while still meaning to
         * move. Anything at or above that threshold is a body creeping through a jam, which is progress and has to
         * release it; only below it is a body actually going nowhere.
         *
         * Getting this wrong in the safe looking direction is what would hurt. A threshold at the drive scale
         * alone reads a body shuffling forward in a dense queue as stuck, so it never takes its stop back and the
         * queue drains at a quarter of the pace it should, which looks like the crowd being sluggish rather than
         * like a threshold being wrong.
         */
        /// <summary>
        /// The fraction of its unobstructed speed a body has to fall below before it counts as going nowhere.
        /// </summary>
        private float StallThreshold()
        {
            float crowd = slowInCrowds ? minimumSpeedFraction : 1f;

            return math.max(settledDriveScale * crowd * .6f, .02f);
        }

        /*
         * How many pieces this frame's step has to be cut into so that no body crosses more than half a cell in one
         * of them. Half rather than a whole cell because the check that keeps bodies out of walls samples where a
         * step ends, so a step that spans a cell can begin and end on walkable ground with a wall in between.
         */
        /// <summary>
        /// How many substeps this frame needs to keep every body moving less than half a cell at a time.
        /// </summary>
        /// <param name="step">The whole frame's step in seconds.</param>
        /// <param name="fastest">The quickest body on the roster.</param>
        /// <param name="cellSize">Width of one grid cell.</param>
        /// <returns>At least one, at most the configured maximum.</returns>
        private int SubstepsFor(float step, float fastest, float cellSize)
        {
            if (!advanced.SubdivideLongSteps) return 1;

            float reach = step * fastest;
            float allowance = math.max(cellSize * .5f, .01f);

            return math.clamp((int)math.ceil(reach / allowance), 1, advanced.MaximumSubsteps);
        }

        /// <summary>
        /// Schedules one movement step over the whole roster.
        /// </summary>
        /// <param name="field">The field to read.</param>
        /// <param name="step">How much time this step covers.</param>
        /// <param name="dependency">Work that has to finish before the mover may touch the buffers, or default for none.</param>
        /// <returns>The handle to join.</returns>
        private JobHandle ScheduleMove(GridFlowField field, float step, JobHandle dependency)
        {
            return new HordeFlowMoveJob
            {
                Grid = field.Grid,
                Flow = field.Flow,
                Walkable = field.Walkable,
                Integration = field.Integration,
                Height = field.Height,
                Push = _push,
                Speed = _speed,
                Density = field.Density,
                SlowInCrowds = slowInCrowds,
                CellCapacity = _cellCapacity,
                ComfortableFill = comfortableFill,
                JamFill = jamFill,
                MinimumSpeedFraction = minimumSpeedFraction,
                DeltaTime = step,
                InverseDeltaTime = step > 0f ? 1f / step : 0f,
                Acceleration = acceleration,
                Deceleration = deceleration,
                StallFraction = stallFraction,
                TurnSpeed = turnSpeed,
                TurnLimitCos = math.cos(headingTurnRate * step),
                TurnLimitSin = math.sin(headingTurnRate * step),
                RequireFacing = requireFacing,
                FacingCosTolerance = math.cos(math.radians(math.min(advanced.FacingTolerance, advanced.FacingLimit))),
                FacingCosLimit = math.cos(math.radians(math.max(advanced.FacingLimit, advanced.FacingTolerance + .01f))),
                Heading = _heading,
                CurrentSpeed = _currentSpeed,
                OpenSpeed = _openSpeed,
                LastPosition = _lastPosition,
                ArriveRadius = arriveRadius,
                ArriveTaper = arriveTaper,
                Goal = driver.Target ? (float3)driver.Target.position : float3.zero,
                RecoverySearchRadius = advanced.RecoverySearchRadius,
                Positions = _positions,
                Reference = _reference,
                Arrived = _arrived,
                Frozen = _frozen,
                Settled = _settled,
                SettledDriveScale = settledDriveScale,
                Stalled = _stalled,
                StallTime = _stallTime,
                StallSettleDelay = stallSettleDelay > 0f ? stallSettleDelay : float.MaxValue,
                StallSpeedFraction = StallThreshold()
            }
            .Schedule(_transforms, dependency);
        }

        private void LateUpdate() => Complete();

        /*
         * Measured before the move rather than after it, so it reads last frame's travel against the positions the
         * bodies actually held while travelling. Running it afterwards would compare this frame's start and end
         * and be a frame out of step with the buffer swap, which on a crowd that is barely moving is the whole
         * signal.
         */
        private JobHandle ScheduleOccupancy(GridFlowField field, float step)
        {
            if (!congestionEnabled && !slowInCrowds) return default;

            /*
             * Told rather than inferred, because the driver skips an expansion when nothing about it would change
             * and it cannot see the cost array being rewritten from here. Only congestion counts: the crowd slowing
             * reads Density, which the expansion does not, so a crowd that only slows itself changes nothing the
             * field would draw differently.
             */
            if (congestionEnabled && driver) driver.MarkDirty();

            EnsureCongestionCapacity(field.Grid.Count);

            int cells = field.Grid.Count;
            float rise = math.saturate(riseSmoothing * step);
            float fall = math.saturate(fallSmoothing * step);

            /*
             * Four jobs where there was one, and three of them run on every worker. Only the gather is proportional
             * to the crowd; the other three are proportional to the grid, and on a kilometre of map at one metre
             * that was fifteen million memory bound operations on a single thread with the mover waiting on all of
             * it. The profiler read as the movement job costing forty milliseconds when the movement job itself
             * costs three tenths of one: what it was doing was queueing behind this.
             */
            JobHandle clear = new HordeCongestionClearJob
            {
                Active = _active.AsDeferredJobArray(),
                SpeedSum = _speedSum,
                ReferenceSum = _referenceSum,
                Counts = _counts,
                Occupancy = _occupancy
            }
            .Schedule(_active, CONGESTION_BATCH, default);

            JobHandle gather = new HordeCongestionGatherJob
            {
                Grid = field.Grid,
                Positions = _positions,
                Previous = _previousPositions,
                Flow = field.Flow,
                Reference = _reference,
                Touched = _touched,
                Active = _active,
                Progress = _progress,
                SpeedSum = _speedSum,
                ReferenceSum = _referenceSum,
                Counts = _counts,
                Occupancy = _occupancy,
                BodyCount = _bodies.Count,
                DeltaTime = step,
                ProgressSmoothing = progressSmoothing,
                Goal = driver.Target ? (float3)driver.Target.position : float3.zero,
                ArriveRadiusSquared = arriveRadius * arriveRadius
            }
            .Schedule(clear);

            JobHandle handle = new HordeCongestionPriceJob
            {
                Active = _active.AsDeferredJobArray(),
                Walkable = field.Walkable,
                SpeedSum = _speedSum,
                Counts = _counts,
                Occupancy = _occupancy,
                Density = field.Density,
                ReferenceSum = _referenceSum,
                WriteCost = congestionEnabled,
                Rise = rise,
                Fall = fall,
                MaximumCost = maximumCost,
                CellCapacity = _cellCapacity,
                ComfortableFill = comfortableFill,
                JamFill = jamFill
            }
            .Schedule(_active, CONGESTION_BATCH, gather);

            /*
             * Only run when congestion is actually writing costs. It is the heaviest of the four, since it samples
             * a whole kernel per cell, and with congestion off there is nothing for it to spread.
             */
            if (congestionEnabled)
                handle = new HordeCongestionSpreadJob
                {
                    Grid = field.Grid,
                    Active = _active.AsDeferredJobArray(),
                    Walkable = field.Walkable,
                    Targets = _referenceSum,
                    Cost = field.Cost,
                    BlurRadius = costBlurRadius,
                    Rise = rise,
                    Fall = fall
                }
                .Schedule(_active, CONGESTION_BATCH, handle);

            /*
             * Last, so it sees this frame's answers. A cell that has decayed back to empty is dropped here and
             * costs nothing again until somebody stands in it.
             */
            handle = new HordeCongestionPruneJob
            {
                Occupancy = _occupancy,
                Active = _active,
                Touched = _touched,
                Density = field.Density,
                Cost = field.Cost,
                ReferenceSum = _referenceSum,
                WriteCost = congestionEnabled
            }
            .Schedule(handle);

            (_positions, _previousPositions) = (_previousPositions, _positions);

            return handle;
        }

        /*
         * Bodies at rest settle about two radii apart, and packed as tightly as circles go that is one centre per
         * (2r) squared times sin sixty of area. Dividing the cell's area by that gives how many bodies a cell can
         * actually contain, which is what any occupancy threshold has to be measured against to mean anything.
         */
        /// <summary>
        /// How many bodies of a given radius fit in one cell once they have stopped pushing.
        /// </summary>
        /// <param name="radius">Average separation radius of the crowd.</param>
        /// <param name="cellSize">Width of one grid cell.</param>
        /// <returns>Bodies per cell at rest, never less than a small positive number.</returns>
        private static float CapacityFor(float radius, float cellSize)
        {
            float spacing = math.max(2f * radius, .01f);

            return math.max(cellSize * cellSize / (spacing * spacing * HEXAGONAL_PACKING), .01f);
        }

        /*
         * Tells separation which bodies are not trying to advance, which is the difference between a crowd that
         * settles and one that shoves itself about forever.
         *
         * Separation already knows how to hold a settled body still: it pushes them at a fraction of the usual
         * strength, so an arriving body moves and the bodies already in place mostly do not. That mechanism was
         * simply never reachable here. It keys off WantsToMove, which nothing sets while the flow field is driving,
         * so it defaulted to true for every body and no body ever settled, however finished it was.
         *
         * Asymmetric by construction, which is what stops the wave. The arriving body is still trying to move and
         * takes the full push; the bodies already there take fifteen percent of it, so the newcomer is the one that
         * gives ground rather than the crowd rearranging itself to let it in.
         *
         * Two reasons a body is not trying, not one. Arrival was the only one for a long time, and because settling
         * spreads outwards from bodies that report it, that quietly meant the only crowd that could ever settle was
         * one standing on the goal. A jam at a bridge or a doorway had no body anywhere in it that had arrived, so
         * nothing seeded, nothing spread, and every body in it pressed at full speed into the back of the one in
         * front for as long as the jam lasted. Being stuck is the second reason, and it is the common one.
         */
        private void PublishStopped()
        {
            for (int i = 0; i < _bodies.Count; i++)
            {
                HordeAgent body = _bodies[i];

                /*
                 * Skipped rather than told it wants to move. Freezing already said it does not, and publishing
                 * arrival over the top would hand it straight back every frame.
                 */
                if (body.IsFrozen) continue;

                body.SetWantsToMove(_arrived[i] == 0 && _stalled[i] == 0);
            }
        }

        private void EnsureCongestionCapacity(int cells)
        {
            if (cells == _congestionCells && _speedSum.IsCreated) return;

            Replace(ref _speedSum, cells);
            Replace(ref _referenceSum, cells);
            Replace(ref _counts, cells);
            Replace(ref _occupancy, cells);
            Replace(ref _touched, cells);

            /*
             * One, not zero, because the blur reads the neighbours of a tracked cell whether or not they are
             * tracked themselves, and an untouched cell has to read as ordinary ground. Zero would have every cell
             * beside the crowd blurring against a neighbourhood that looked free, which is backwards.
             */
            for (int i = 0; i < cells; i++)
                _referenceSum[i] = 1f;

            if (_active.IsCreated) _active.Clear();
            else _active = new NativeList<int>(MINIMUM_CAPACITY, Allocator.Persistent);

            _congestionCells = cells;
        }

        private void Complete()
        {
            if (!_scheduled) return;

            using (MOVE_MARKER.Auto())
                _handle.Complete();

            _scheduled = false;

            using (PUBLISH_MARKER.Auto())
                PublishStopped();
        }

        /// <summary>
        /// Applies everything that registered or unregistered since the last frame. Only ever called with no job in flight.
        /// </summary>
        private void DrainRoster()
        {
            for (int i = 0; i < PENDING_REGISTER.Count; i++)
                Add(PENDING_REGISTER[i]);

            PENDING_REGISTER.Clear();

            for (int i = 0; i < PENDING_UNREGISTER.Count; i++)
                Remove(PENDING_UNREGISTER[i]);

            PENDING_UNREGISTER.Clear();
        }

        private void Add(HordeAgent body)
        {
            if (!body || body.FlowIndex >= 0) return;

            EnsureCapacity(_bodies.Count + 1);

            /*
             * Cleared so a recycled slot does not hand a new body the heading of whoever held it last, which would
             * have it set off in an unrelated direction until it managed to turn round.
             */
            /*
             * Seeded from where the body actually is, so its first frame does not measure the distance from the
             * origin as progress and decide it is travelling at several hundred metres a second.
             */
            int slot = _bodies.Count;

            _heading[slot] = float2.zero;
            _currentSpeed[slot] = 0f;
            _openSpeed[slot] = 0f;
            _progress[slot] = 0f;
            _reference[slot] = 0f;
            _arrived[slot] = 0;
            _frozen[slot] = 0;
            _settled[slot] = 0;
            _stalled[slot] = 0;
            _stallTime[slot] = 0f;
            _lastPosition[slot] = body.transform.position;

            body.FlowIndex = slot;
            _bodies.Add(body);
            _transforms.Add(body.transform);
        }

        private void Remove(HordeAgent body)
        {
            int index = body.FlowIndex;
            if (index < 0 || index >= _bodies.Count || _bodies[index] != body) return;

            int last = _bodies.Count - 1;

            /*
             * Every buffer that EnsureCapacity grows rather than replaces has to move with the body, because those
             * are exactly the ones holding state from earlier frames. The ones it replaces are rewritten in full
             * before anything reads them, so they need nothing here.
             *
             * Three of them were missing, and the two that matter both feed the settle path. A body swapped into a
             * despawned body's slot inherited its stall timer, so a body that had been stuck for most of the settle
             * delay handed that to whoever took its place and the newcomer settled on its next frame having never
             * been blocked by anything. It inherited the open speed ramp too, which is what the stall test measures
             * against, so the same swap could also have it read as stuck immediately. On a crowd that despawns
             * constantly that is a steady trickle of bodies deciding they have given up for no reason at all.
             */
            _bodies[index] = _bodies[last];
            _bodies[index].FlowIndex = index;
            _heading[index] = _heading[last];
            _currentSpeed[index] = _currentSpeed[last];
            _openSpeed[index] = _openSpeed[last];
            _progress[index] = _progress[last];
            _stallTime[index] = _stallTime[last];
            _lastPosition[index] = _lastPosition[last];
            _bodies.RemoveAt(last);
            _transforms.RemoveAtSwapBack(index);

            body.FlowIndex = -1;
        }

        /*
         * Every per body buffer goes through one of these two helpers, so allocating one without freeing the one it
         * replaces is not something this method can express. Written out longhand it was: the list is eleven arrays
         * across two branches, and twice now a buffer has been added to the allocating half and missed in the
         * freeing half, which leaks one array per growth and reports later as a number with no stack attached.
         */
        /// <summary>
        /// Reallocates a buffer whose contents are rewritten every frame, freeing whatever it held.
        /// </summary>
        private static void Replace<T>(ref NativeArray<T> array, int capacity) where T : struct
        {
            if (array.IsCreated) array.Dispose();

            array = new NativeArray<T>(capacity, Allocator.Persistent);
        }

        /// <summary>
        /// Reallocates a buffer that carries per body state, copying what was already there.
        /// </summary>
        private static void Grow<T>(ref NativeArray<T> array, int capacity, int used) where T : struct
        {
            NativeArray<T> grown = new NativeArray<T>(capacity, Allocator.Persistent);

            if (array.IsCreated)
            {
                NativeArray<T>.Copy(array, grown, used);
                array.Dispose();
            }

            array = grown;
        }

        private void EnsureCapacity(int count)
        {
            if (count <= _capacity) return;

            int capacity = math.max(_capacity * 2, math.max(count, MINIMUM_CAPACITY));

            Replace(ref _push, capacity);
            Replace(ref _speed, capacity);
            Replace(ref _positions, capacity);
            Replace(ref _previousPositions, capacity);
            Replace(ref _reference, capacity);
            Replace(ref _arrived, capacity);
            Replace(ref _frozen, capacity);
            Replace(ref _settled, capacity);
            Replace(ref _stalled, capacity);

            /*
             * Carried across rather than replaced. Dropping headings turned every body on the roster to face
             * whatever the field said on that one frame, which in a crowd that is mid turn is a visible twitch
             * through the whole horde every time it grows. The speed ramps and the progress average are running
             * state for the same reason: reset, they read as a crowd that has just stopped dead.
             */
            Grow(ref _heading, capacity, _capacity);
            Grow(ref _currentSpeed, capacity, _capacity);
            Grow(ref _openSpeed, capacity, _capacity);
            Grow(ref _progress, capacity, _capacity);
            Grow(ref _lastPosition, capacity, _capacity);
            Grow(ref _stallTime, capacity, _capacity);

            _capacity = capacity;
        }

    }
}

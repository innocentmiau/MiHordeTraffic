using UnityEngine;

namespace MiHordeTraffic.Separation
{
    /*
     * Split out of the system so the numbers can be tuned in one asset and swapped between scenes,
     * and so a benchmark scene can hold a deliberately harsh profile without touching the one the game ships with.
     * The system builds a default instance in memory when no asset is assigned, so nothing is required to press play.
     */
    /// <summary>
    /// Tuning values for the SeparationSystem, assigned as an asset or left empty for the defaults.
    /// </summary>
    [CreateAssetMenu(fileName = "SeparationSettings", menuName = "MiHordeTraffic/Separation Settings", order = 0)]
    public class SeparationSettings : ScriptableObject
    {

        [Header("Scheduling")]
        [SerializeField, Min(1)] private int updateInterval = 1;
        [SerializeField] private SeparationCompletionMode completionMode = SeparationCompletionMode.NEXT_FRAME;
        [SerializeField] private bool reusePushBetweenTicks = true;

        [Header("Grid")]
        [SerializeField] private SeparationPlane plane = SeparationPlane.XZ;
        [SerializeField] private float cellSize = 0f;
        [SerializeField, Min(16)] private int initialCapacity = 1024;

        [Header("Crowding")]
        [SerializeField] private bool blockingEnabled = false;
        [SerializeField, Min(1)] private int blockingNeighbours = 3;
        [SerializeField, Range(1, 255)] private int settledHoldTicks = 8;
        [SerializeField, Range(0f, 1f)] private float settledPushScale = .15f;

        [Header("Crowd Detour")]
        [SerializeField] private bool detourEnabled = false;
        [SerializeField, Min(0f)] private float detourStrength = 2f;
        [SerializeField, Min(0f)] private float detourDensityWeight = .25f;

        [Header("Push")]
        [SerializeField] private float pushStrength = 8f;
        [SerializeField] private float maxPushSpeed = 4f;
        [SerializeField, Range(.05f, 1f)] private float resolveFraction = .5f;

        /// <summary>
        /// How many frames pass between recalculations. 1 is every frame, 3 is every third frame.
        /// </summary>
        public int UpdateInterval => updateInterval;

        /// <summary>
        /// When the scheduled job is joined back to the main thread.
        /// </summary>
        public SeparationCompletionMode CompletionMode => completionMode;

        /// <summary>
        /// Whether the last computed push keeps being applied on the frames between recalculations, rather than dropping to zero.
        /// </summary>
        public bool ReusePushBetweenTicks => reusePushBetweenTicks;

        /// <summary>
        /// Which axes separation acts on.
        /// </summary>
        public SeparationPlane Plane => plane;

        /// <summary>
        /// Width of one spatial hash cell. Zero or less means it is derived from the largest registered radius each tick.
        /// </summary>
        public float CellSize => cellSize;

        /// <summary>
        /// How many bodies the native arrays are sized for up front, to avoid growing them during a spawn burst.
        /// </summary>
        public int InitialCapacity => initialCapacity;

        /// <summary>
        /// Whether agents settle once neighbours that are closer to their goal have settled themselves.
        /// Off means a crowd converging on one point never comes to rest, because every agent keeps steering into
        /// the ones in front of it. Bodies in a group with this on are expected to report a real SeparationGoal
        /// and to say honestly whether they are trying to advance.
        /// </summary>
        public bool BlockingEnabled => blockingEnabled;

        /// <summary>
        /// How many settled neighbours between a body and its goal it takes before that body settles too.
        /// Lower spreads the stop further out and packs looser, higher packs tighter and churns more.
        /// </summary>
        public int BlockingNeighbours => blockingNeighbours;

        /// <summary>
        /// How many ticks a body stays settled after the last tick that said it should be.
        /// Without it a body on the edge of a crowd starts and stops several times a second.
        /// </summary>
        public byte SettledHoldTicks => (byte)settledHoldTicks;

        /// <summary>
        /// How much of the usual push a settled body still takes, as a fraction. Zero makes settled bodies
        /// immovable and leaves any that settled while overlapping stuck inside each other, so a small value
        /// beats none: enough to ooze apart, not enough to keep shoving the crowd around.
        /// </summary>
        public float SettledPushScale => settledPushScale;

        /// <summary>
        /// How much of its overlap a body may undo in a single frame. One disables the limit and leaves only
        /// MaxPushSpeed, which is the original behaviour. Lower makes the push softer and frame rate independent,
        /// at the cost of resolving overlaps more slowly.
        /// </summary>
        /*
         * Floored rather than returned raw. A zero here makes the push limit zero and silently switches separation
         * off entirely, and a field added after an asset was last written deserialises to whatever the loader felt
         * like. A setting whose failure mode is "the system quietly stops working" should not be able to reach it.
         */
        public float ResolveFraction => Mathf.Max(resolveFraction, .05f);

        /// <summary>
        /// Whether a body that settled while still wanting to advance slides towards emptier ground rather than
        /// waiting. Turns the back of a long thin crowd into one that spreads around the jam instead of queueing.
        /// </summary>
        public bool DetourEnabled => detourEnabled;

        /// <summary>
        /// How hard a stuck body is steered towards the cell it picked, in world units per second.
        /// </summary>
        public float DetourStrength => detourStrength;

        /// <summary>
        /// How many metres of progress towards the goal one occupant of a cell is worth giving up to avoid.
        /// Higher sends bodies further out of their way to find room, lower keeps them pressing forwards.
        /// </summary>
        public float DetourDensityWeight => detourDensityWeight;

        /// <summary>
        /// Multiplier on the raw overlap depth, in world units per second of push per unit of overlap.
        /// </summary>
        public float PushStrength => pushStrength;

        /// <summary>
        /// Ceiling on the push velocity any one body can receive, so a body buried in a crowd is not launched.
        /// </summary>
        public float MaxPushSpeed => maxPushSpeed;

        /*
         * Every one of these is read fresh by the group each frame it is needed, so changing them mid game takes
         * effect on the next tick with nothing to invalidate. That is what lets a benchmark sweep quality presets,
         * and what would let a quality scaler back the crowd off when the frame budget slips.
         */
        /// <summary>
        /// Takes a copy of the settings that are safe to change at runtime, so they can be restored later.
        /// </summary>
        /// <returns>The current tuning.</returns>
        public SeparationTuning CaptureTuning() => new SeparationTuning
        {
            UpdateInterval = updateInterval,
            ReusePushBetweenTicks = reusePushBetweenTicks,
            CellSize = cellSize,
            DetourEnabled = detourEnabled,
            DetourStrength = detourStrength,
            DetourDensityWeight = detourDensityWeight,
            BlockingEnabled = blockingEnabled,
            BlockingNeighbours = blockingNeighbours,
            SettledPushScale = settledPushScale
        };

        /// <summary>
        /// Applies a tuning, taking effect on the next tick.
        /// </summary>
        /// <param name="tuning">The values to apply.</param>
        public void ApplyTuning(SeparationTuning tuning)
        {
            updateInterval = Mathf.Max(1, tuning.UpdateInterval);
            reusePushBetweenTicks = tuning.ReusePushBetweenTicks;
            cellSize = Mathf.Max(0f, tuning.CellSize);
            detourEnabled = tuning.DetourEnabled;
            detourStrength = Mathf.Max(0f, tuning.DetourStrength);
            detourDensityWeight = Mathf.Max(0f, tuning.DetourDensityWeight);
            blockingEnabled = tuning.BlockingEnabled;
            blockingNeighbours = Mathf.Max(1, tuning.BlockingNeighbours);
            settledPushScale = Mathf.Clamp01(tuning.SettledPushScale);
        }

    }
}

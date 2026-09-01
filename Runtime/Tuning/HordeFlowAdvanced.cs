using UnityEngine;

namespace MiHordeTraffic.Tuning
{
    /*
     * The guard rails, kept behind a foldout because they are not tuning. Every value here is either a stability
     * limit or a search bound, correct for any map, and moving one is far more likely to break something than to
     * improve it. Sitting inline they read as more knobs to try, which is exactly the wrong invitation: the ones
     * worth trying are the presets and the numbers with units, and burying these is what makes that obvious.
     *
     * A serialized class rather than a header, because a header still leaves everything on screen. Unity draws a
     * nested serializable type as a foldout, so this costs nothing and hides itself until it is asked for.
     */
    /// <summary>
    /// Stability limits and search bounds for the flow mover, which are correct as they are on nearly any map.
    /// </summary>
    [System.Serializable]
    public class HordeFlowAdvanced
    {

        [Tooltip("Longest step in game time a single frame may integrate, so a hitch does not shake the crowd apart. Scales with the time scale.")]
        [SerializeField, Min(.001f)] private float maximumStep = .0333f;

        [Tooltip("Cut a long step into several short ones so bodies cannot cross a wall that was never sampled. Only ever engages when the clock is sped up.")]
        [SerializeField] private bool subdivideLongSteps = true;

        [Tooltip("Most pieces one frame may be cut into. Past this the world runs slower than the clock says rather than moving bodies through walls.")]
        [SerializeField, Range(1, 16)] private int maximumSubsteps = 8;

        [Tooltip("Cells searched outwards when a body is standing somewhere the grid does not describe. Larger costs more only for bodies that are actually lost.")]
        [SerializeField, Min(1)] private int recoverySearchRadius = 6;

        [Tooltip("Degrees off course still counted as facing the right way, so bodies do not creep the last few degrees. Only used when Require Facing is on.")]
        [SerializeField, Range(0f, 180f)] private float facingTolerance = 30f;

        [Tooltip("Degrees off course at which a body stops and turns on the spot. Only used when Require Facing is on.")]
        [SerializeField, Range(0f, 180f)] private float facingLimit = 100f;

        /// <summary>
        /// Longest step in game time that one frame may integrate.
        /// </summary>
        public float MaximumStep => maximumStep;

        /// <summary>
        /// Whether a long step is cut into several ordinary sized ones.
        /// </summary>
        public bool SubdivideLongSteps => subdivideLongSteps;

        /// <summary>
        /// The most pieces one frame's step may be cut into.
        /// </summary>
        public int MaximumSubsteps => maximumSubsteps;

        /// <summary>
        /// How many cells outwards a lost body searches for ground the grid describes.
        /// </summary>
        public int RecoverySearchRadius => recoverySearchRadius;

        /// <summary>
        /// Degrees off course still treated as facing the right way.
        /// </summary>
        public float FacingTolerance => facingTolerance;

        /// <summary>
        /// Degrees off course at which a body stops moving and turns on the spot.
        /// </summary>
        public float FacingLimit => facingLimit;

    }
}

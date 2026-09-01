using MiHordeTraffic.Movement;
using MiHordeTraffic.Pathing.FlowField;

namespace MiHordeTraffic.Pathing
{
    /*
     * The flow field as a technique the harness can time, which needs it to be more than a wrapper around a repath
     * call, because the flow field has no repath call. One expansion outward from the goal serves the entire crowd,
     * so all of its cost lands in PrepareFrame and none of it lands per body. That asymmetry is the result worth
     * measuring and it is why the interface has a PrepareFrame at all.
     *
     * Switching technique has to switch what moves the bodies, not merely what paths them. A NavMeshAgent that is
     * still enabled is still simulated, still avoiding, and still synchronising its transform, and a flow field
     * running alongside one would be two systems fighting over the same body while the harness reported the cost
     * of one of them. Handing movement over is the whole difference between the two rows, so the switch owns it.
     *
     * Reversible on purpose. The agents are switched off rather than stripped, and the flow components are
     * disabled rather than destroyed, so a comparison can be run back and forth on the same crowd without
     * reloading the scene. A measurement you can only take once is a measurement you cannot check.
     */
    /// <summary>
    /// Moves the crowd by the shared flow field, rebuilding it inside the scheduler's timed region.
    /// </summary>
    public class FlowFieldPathTechnique : IHordePathTechnique
    {

        /// <summary>
        /// Which technique this is.
        /// </summary>
        public HordePathTechnique Technique => HordePathTechnique.FLOW_FIELD;

        /// <summary>
        /// The field is already current for every body at once, so there is nothing to walk the roster for.
        /// </summary>
        public bool RepathsBodies => false;

        /// <summary>
        /// Hands the crowd to the flow field.
        /// </summary>
        public void Begin() => HordeAgent.SetAllMode(HordeMovementMode.FLOW_FIELD);

        /// <summary>
        /// Nothing to tear down. Whichever technique follows decides what moves the bodies.
        /// </summary>
        public void End() { }

        /// <summary>
        /// Where all of this technique's cost lives: one expansion for the whole crowd, when it comes due.
        /// </summary>
        /// <param name="deltaTime">Frame time.</param>
        public void PrepareFrame(float deltaTime)
        {
            if (HordeFlowFieldDriver.Instance) HordeFlowFieldDriver.Instance.Rebuild(deltaTime);
        }

        /*
         * Nothing per body, and the far distance says so. A body reads the cell it is standing in every frame in
         * the movement job, so there is no per body path to recompute and no reason for the scheduler to keep
         * coming back. Anything that still asks is paced as slowly as the tuning allows rather than being told it
         * has arrived, so a body that does have its own idea of repathing is not starved by this technique.
         */
        /// <summary>
        /// Does nothing. The field is already current for every body at once.
        /// </summary>
        /// <param name="body">The body the scheduler picked.</param>
        /// <param name="time">The current time.</param>
        /// <returns>A large distance, so the scheduler paces this body as slowly as it is allowed to.</returns>
        public float Repath(IRepathBody body, float time) => float.MaxValue;

    }
}

namespace MiHordeTraffic.Pathing
{
    /*
     * The baseline every other technique is measured against, and deliberately the thinnest possible wrapper:
     * it hands the work straight back to the body, which is what the scheduler did before techniques existed.
     * Anything measured against this is being measured against the engine doing its own A* on its own threads,
     * which is a genuinely good implementation and not a strawman.
     */
    /// <summary>
    /// Repaths by asking each body to do it, which for a NavMeshAgent body means one SetDestination each.
    /// </summary>
    public class NavMeshAgentPathTechnique : IHordePathTechnique
    {

        /// <summary>
        /// Which technique this is.
        /// </summary>
        public HordePathTechnique Technique => HordePathTechnique.NAVMESH_AGENT;

        /// <summary>
        /// One path search per body, so the roster passes are the whole of this technique.
        /// </summary>
        public bool RepathsBodies => true;

        /*
         * Takes the crowd back off the flow field, because leaving it on would have two systems moving the same
         * bodies and the harness reporting the cost of one of them. This is the baseline, so the baseline has to
         * actually be the engine doing the work.
         */
        /// <summary>
        /// Hands every body back to its own NavMeshAgent.
        /// </summary>
        public void Begin() => Movement.HordeAgent.SetAllMode(Movement.HordeMovementMode.NAVMESH_AGENT);

        /// <summary>
        /// Nothing to tear down.
        /// </summary>
        public void End() { }

        /// <summary>
        /// Nothing is shared between bodies here. Every path is its own search, which is exactly the cost a flow
        /// field exists to avoid and the reason this row is worth having on screen next to it.
        /// </summary>
        /// <param name="deltaTime">Frame time.</param>
        public void PrepareFrame(float deltaTime) { }

        /// <summary>
        /// Asks the body to path itself.
        /// </summary>
        /// <param name="body">The body to repath.</param>
        /// <param name="time">The current time.</param>
        /// <returns>Distance from the body to its goal.</returns>
        public float Repath(IRepathBody body, float time) => body.Repath(time);

    }
}

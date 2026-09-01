namespace MiHordeTraffic.Movement
{
    /*
     * These exist to be measured against each other. The push itself is computed in a Burst job on worker threads and costs almost nothing,
     * but handing the result back to a NavMeshAgent is a main thread call per agent per frame,
     * and at a few thousand agents that hand off can cost more than the avoidance it replaced.
     * Which of these is cheapest is a property of the project, not something to reason about from the API surface.
     */
    /// <summary>
    /// How a HordeAgent hands its push back to the NavMeshAgent.
    /// </summary>
    public enum SeparationApplyMode
    {
        NAVMESH_MOVE, // agent.Move, which projects the push onto the navmesh so agents can never be pushed through a wall or off the mesh
        NEXT_POSITION, // writes agent.nextPosition directly, skipping the navmesh projection, cheaper but able to shove agents somewhere they cannot path from
        NONE // computes the push and throws it away, which is the only way to measure what the jobs cost with the hand off removed
    }
}

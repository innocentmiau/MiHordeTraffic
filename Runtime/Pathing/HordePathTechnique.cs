namespace MiHordeTraffic.Pathing
{
    /// <summary>
    /// Which pathfinding technique the scheduler is driving its bodies with.
    /// </summary>
    public enum HordePathTechnique
    {
        NAVMESH_AGENT, // one SetDestination per body, the engine's own A* on its own threads
        FLOW_FIELD // one Dijkstra expansion outward from the goal, every body reads the cell it stands on

        /*
         * A per body NavMeshQuery A* in jobs was the third row here. It is gone rather than unimplemented: the
         * UnityEngine.Experimental.AI namespace it needs was deprecated without a replacement in Unity 6, which is
         * why the grid exists at all. An enum value that can only ever log a warning and fall back is worse than
         * no enum value, because it reads as a plan rather than as a decision already taken.
         */
    }
}

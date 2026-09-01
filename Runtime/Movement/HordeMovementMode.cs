namespace MiHordeTraffic.Movement
{
    /// <summary>
    /// What is currently moving a horde agent, which the path scheduler switches between at runtime.
    /// </summary>
    public enum HordeMovementMode
    {
        FLOW_FIELD, // the movement job walks the body along the shared field, and no NavMeshAgent is involved
        NAVMESH_AGENT // the body's own NavMeshAgent drives it, and separation is handed back through its apply mode
    }
}

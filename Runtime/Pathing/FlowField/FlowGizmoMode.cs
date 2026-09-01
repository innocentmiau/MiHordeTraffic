namespace MiHordeTraffic.Pathing.FlowField
{
    /// <summary>
    /// What the flow field driver draws in the scene view.
    /// </summary>
    public enum FlowGizmoMode
    {
        FLOW, // an arrow per cell showing which way a body there would walk
        COST, // per cell traversal cost, so a jam can be watched forming and clearing
        INTEGRATION // per cell cost to reach the goal, which is what the routing decision is actually made on
    }
}

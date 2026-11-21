namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * Two answers to where a 2D crowd can walk, because 2D projects arrive with one or the other already in place.
     * Most only have colliders and tilemaps, and should not need a navmesh to use this package. Some already bake a
     * NavMeshPlus navmesh for their agents, and should not have to describe the same level a second time in layers.
     */
    /// <summary>
    /// What a 2D scene's grid bake reads to decide which cells are ground.
    /// </summary>
    public enum HordeGround2D
    {
        PHYSICS_2D, // walls are the 2D colliders on the Blocking Layers, ground is optionally limited to the Floor Tilemaps
        NAVMESH // the navmesh baked by NavMeshPlus on the XY plane, read the same way a 3D scene reads its navmesh
    }
}

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * Which way the ground faces, and nothing else. The grid, the expansion and the movement are the same in both,
     * because a top down 2D level is a floor with walls in it exactly as a 3D one is, only drawn on a different pair of axes.
     */
    /// <summary>
    /// Whether the horde walks on a 3D floor or across a top down 2D scene.
    /// </summary>
    public enum HordeSpace
    {
        WORLD_3D, // the ground is the XZ plane and Y is up, walkability comes from a baked navmesh
        WORLD_2D // a top down 2D scene drawn on the XY plane, walkability comes from 2D colliders and tilemaps
    }
}

namespace MiHordeTraffic.Separation
{
    /// <summary>
    /// Which axes separation is allowed to act on, so the same system serves flat 3D scenes and 2D ones.
    /// </summary>
    public enum SeparationPlane
    {
        XZ, // standard 3D navmesh on flat ground, height is ignored so agents on a ramp never get pushed up or down
        XY, // 2D setups such as NavMeshPlus, where the world is drawn on the XY plane
        FULL_3D // every axis counts, for flying or stacked agents that genuinely need to separate vertically
    }
}

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * What a goal occupies. A point is the original behaviour and stays the default: one cell is seeded and every
     * body on the map walks to it, which is right for chasing a player and wrong for anything with a footprint.
     *
     * A shape is seeded along its whole edge instead, so bodies arrive at the nearest part of it rather than
     * queueing at whichever single cell happened to be nearest the centre.
     */
    /// <summary>
    /// The form a goal takes on the grid, which decides where bodies arrive at it.
    /// </summary>
    public enum HordeTargetShape
    {

        /// <summary>
        /// A single position. Every body walks to the same cell.
        /// </summary>
        POINT = 0,

        /// <summary>
        /// An axis aligned rectangle on the ground plane. Bodies arrive along its edges.
        /// </summary>
        BOX = 1,

        /// <summary>
        /// A circle on the ground plane. Bodies arrive around its rim.
        /// </summary>
        SPHERE = 2

    }
}

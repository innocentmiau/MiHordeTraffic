namespace MiHordeTraffic.Tuning
{
    /*
     * How often the flow field is expanded, which is the whole of how quickly a crowd notices its target has moved.
     * Three numbers govern it and none of them means anything on its own, so they are chosen together here.
     * Cheaper settings are not merely slower to react: the crowd walks confidently towards where the target used
     * to be, which reads as the pathfinding being wrong rather than as it being stale.
     */
    /// <summary>
    /// How eagerly the flow field is rebuilt when the target moves, traded against the cost of rebuilding it.
    /// </summary>
    public enum HordeFieldRefresh
    {
        LAZY, // cheapest, and the crowd visibly walks at where the target was half a second ago
        BALANCED, // reacts within about a cell of target movement, at four expansions a second while it sits still
        RESPONSIVE, // for a target that moves constantly, at roughly double the expansion cost
        IMMEDIATE, // expands almost every frame the target moves, only worth it while tuning
        CUSTOM // leaves the three numbers below exactly as they are set
    }
}

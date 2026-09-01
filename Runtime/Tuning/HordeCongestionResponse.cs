namespace MiHordeTraffic.Tuning
{
    /*
     * How readily the crowd reroutes around itself. Higher settings are not simply better: routing everyone onto
     * whatever is currently cheapest is what makes a crowd swing between two bridges instead of sharing them, which
     * traffic assignment has known since Wardrop. The presets raise the ceiling and the reaction speed together and
     * keep the decay slow, because it is the gap between rising fast and falling slowly that breaks that cycle.
     */
    /// <summary>
    /// How strongly congestion is priced, and so how far the crowd will detour to get around itself.
    /// </summary>
    public enum HordeCongestionResponse
    {
        OFF, // no measurement and no rerouting, the field routes purely on distance
        SUBTLE, // only a solid jam is worth a detour, and never a long one
        BALANCED, // a jammed route can look up to thirty times its length, which is enough to open a second bridge
        AGGRESSIVE, // reroutes early and detours far, at the cost of a crowd that changes its mind more often
        CUSTOM
    }
}

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * A grid can say that ground is not there and it could not say that ground is shut, and those are different
     * answers to different questions. Both stop a body walking through, and only one of them is somewhere worth
     * waiting.
     *
     * Without the distinction a crowd whose way is closed has no route at all, so it falls back to walking at the
     * goal and presses against whatever lies between, which is as likely to be a mountain as the door. Standing at
     * the foot of a cliff because the target happens to be behind it is not waiting, it is being lost.
     */
    /// <summary>
    /// Whether ground taken out of the grid is gone or merely closed, which decides whether the crowd routes to it.
    /// </summary>
    public enum HordeBlockKind
    {
        SOLID, // ground that is simply not there, like a wall or a building. Nothing routes at it and nothing waits by it
        GATE // shut rather than absent, so the crowd still routes to it, queues against it, and pours through when it opens
    }
}

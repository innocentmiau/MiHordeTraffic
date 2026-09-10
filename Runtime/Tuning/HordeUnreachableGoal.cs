namespace MiHordeTraffic.Tuning
{
    /*
     * What a body does when it is standing on real ground and the goal cannot be reached from it. This only ever
     * covers ground genuinely sealed off from the target: a goal standing inside an obstacle is not this case,
     * because the expansion seeds from the nearest walkable cell to it and the whole map still routes there.
     *
     * Neither answer is right for every game, which is why it is a choice. Holding suits anything where the crowd
     * should look like it has run out of options. Approaching suits anything where pressing towards the target is
     * the point, and it is the older behaviour.
     */
    /// <summary>
    /// What bodies do when no route to the goal exists from the ground they are standing on.
    /// </summary>
    public enum HordeUnreachableGoal
    {

        /// <summary>
        /// Stand where they are and let separation spread the crowd, rather than walking at ground they cannot cross.
        /// </summary>
        HOLD = 0,

        /// <summary>
        /// Walk straight at the goal regardless, pressing into whatever lies between them and it.
        /// </summary>
        APPROACH = 1

    }
}

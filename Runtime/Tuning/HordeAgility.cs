namespace MiHordeTraffic.Tuning
{
    /*
     * How quickly a body reaches walking pace and how sharply it gives up when something is in the way.
     * Deceleration is deliberately allowed to be harsher than acceleration in every preset, because running into a
     * crowd should register at once while leaving one can afford to be gradual.
     */
    /// <summary>
    /// How quickly bodies get up to speed, slow down, and stop pushing when they are not getting anywhere.
    /// </summary>
    public enum HordeAgility
    {
        SLUGGISH, // takes over a second to reach walking pace, so a crowd sets off as a wave rather than at once
        NATURAL, // about a third of a second, which is roughly a person starting to walk
        BRISK, // near enough immediate without removing the ramp that stops a jam being measured on frame one
        INSTANT, // no ramp at all, which makes congestion harder to read since nothing ever measures as accelerating
        CUSTOM
    }
}

namespace MiHordeTraffic.Tuning
{
    /*
     * How quickly a body swings onto a new direction, which is two separate rates that have to agree.
     * One turns the heading the body walks along, the other turns the model to face it, and setting the model to
     * turn slower than the heading makes bodies visibly slide sideways while a faster one buys nothing at all.
     */
    /// <summary>
    /// How fast bodies turn towards where the field is sending them.
    /// </summary>
    public enum HordeTurnStyle
    {
        HEAVY, // takes a couple of seconds to reverse, which suits something large and reads as weight
        NATURAL, // about a second to reverse, roughly how a person changes their mind mid stride
        AGILE, // half a second to reverse, for something quick that should not look like it has momentum
        INSTANT, // effectively snaps, which removes turning as a behaviour and can look robotic in a crowd
        CUSTOM
    }
}

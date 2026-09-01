namespace MiHordeTraffic.Tuning
{
    /*
     * How much a body slows down for the cell it is walking into. This is what turns a crowd arriving at a
     * chokepoint from a pile up into a queue, and the thresholds are fractions of what a cell can physically hold
     * rather than body counts, so they stay meaningful when the cell size or the body radius changes.
     */
    /// <summary>
    /// How much bodies ease off when the ground ahead of them is already occupied.
    /// </summary>
    public enum HordeCrowdPressure
    {
        IGNORE, // bodies walk at full pace into a full cell and let separation sort it out
        POLITE, // slows only once the cell ahead is nearly full, and never below half pace
        BALANCED, // starts easing at half capacity, down to a quarter pace when the cell is full
        STRICT, // slows early and hard, which forms orderly queues and can look overly cautious in open ground
        CUSTOM
    }
}

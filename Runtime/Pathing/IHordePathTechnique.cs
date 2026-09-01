namespace MiHordeTraffic.Pathing
{
    /*
     * The scheduler decides who repaths and when; a technique decides what repathing means. Splitting them is what
     * lets the budget, the distance pacing and the starvation guarantee stay identical while the thing underneath
     * changes, which is the only way a comparison between two techniques means anything.
     *
     * PrepareFrame is where a technique does work that serves the whole crowd at once. A per body A* has nothing
     * to do there; a flow field does all of its work there and almost none per body. That asymmetry is the point
     * of measuring them against each other, so both halves are timed together.
     */
    /// <summary>
    /// One way of getting a body onto a path, swappable at runtime so techniques can be compared honestly.
    /// </summary>
    public interface IHordePathTechnique
    {

        /// <summary>
        /// Which technique this is.
        /// </summary>
        HordePathTechnique Technique { get; }

        /*
         * Whether there is any point walking the roster for this technique. A flow field answers every body from
         * one expansion, so the scheduler's two sweeps would ask several thousand bodies whether they want a
         * repath, be told no by every one of them, and charge the technique for having asked. That is not a
         * rounding error at crowd sizes: it is thousands of interface calls a frame buying nothing, and it lands
         * inside the timed region, so it also makes the technique look several times more expensive than it is.
         */
        /// <summary>
        /// Whether this technique does per body work at all. False means the scheduler skips its roster passes.
        /// </summary>
        bool RepathsBodies { get; }

        /// <summary>
        /// Called when this technique becomes the active one, before any body is asked to repath.
        /// </summary>
        void Begin();

        /// <summary>
        /// Called when this technique stops being the active one, so it can release anything it allocated.
        /// </summary>
        void End();

        /// <summary>
        /// Work done once per frame for the whole crowd, before any body is repathed.
        /// </summary>
        /// <param name="deltaTime">Frame time.</param>
        void PrepareFrame(float deltaTime);

        /// <summary>
        /// Puts one body onto a path.
        /// </summary>
        /// <param name="body">The body to repath.</param>
        /// <param name="time">The current time.</param>
        /// <returns>Distance from the body to its goal, which the scheduler turns into how soon to come back.</returns>
        float Repath(IRepathBody body, float time);

    }
}

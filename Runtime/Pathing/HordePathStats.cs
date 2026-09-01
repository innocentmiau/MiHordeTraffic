namespace MiHordeTraffic.Pathing
{
    /*
     * Kept per technique rather than globally, so switching back and forth during a session accumulates two
     * separate histories and the comparison survives the switch. Means are exact and unbounded rather than
     * windowed, because the question being asked is what a technique costs over a run, not what it cost lately.
     */
    /// <summary>
    /// What one pathfinding technique has cost since it was last reset.
    /// </summary>
    public struct HordePathStats
    {

        private double _totalMicroseconds;
        private long _repaths;
        private long _frames;

        /// <summary>
        /// How many repaths this technique has served.
        /// </summary>
        public long Repaths => _repaths;

        /// <summary>
        /// Microseconds spent per repath, averaged over every one of them.
        /// </summary>
        public double MicrosecondsPerRepath => _repaths == 0 ? 0d : _totalMicroseconds / _repaths;

        /// <summary>
        /// Milliseconds this technique cost per frame, averaged over every frame it has been active for.
        /// This is the number worth comparing, since a technique that is cheap per call but called far more often
        /// is not cheaper.
        /// </summary>
        public double MillisecondsPerFrame => _frames == 0 ? 0d : _totalMicroseconds / _frames / 1000d;

        /// <summary>
        /// Microseconds spent on the most recent frame.
        /// </summary>
        public double LastFrameMicroseconds { get; private set; }

        /// <summary>
        /// Repaths served on the most recent frame.
        /// </summary>
        public int LastFrameRepaths { get; private set; }

        /// <summary>
        /// Records one frame's worth of work.
        /// </summary>
        /// <param name="microseconds">Time the pass took.</param>
        /// <param name="repaths">How many repaths it served.</param>
        public void Record(double microseconds, int repaths)
        {
            _totalMicroseconds += microseconds;
            _repaths += repaths;
            _frames++;

            LastFrameMicroseconds = microseconds;
            LastFrameRepaths = repaths;
        }

        /// <summary>
        /// Throws away everything recorded so far.
        /// </summary>
        public void Clear() => this = default;

        /// <summary>
        /// One line summary for an overlay.
        /// </summary>
        /// <returns>The formatted stats.</returns>
        public override string ToString() =>
            _frames == 0
                ? "not run"
                : $"{MillisecondsPerFrame:F3} ms/frame   {MicrosecondsPerRepath:F1} us/repath   {LastFrameRepaths} last frame   ({_repaths} total)";

    }
}

namespace MiHordeTraffic.Benchmark
{
    /*
     * A mean on its own hides exactly the thing a crowd system is judged on. Two runs can average the same
     * while one of them holds a steady 6ms and the other alternates 4ms and 8ms, and it is the second one
     * that people feel. The high percentiles are where a crowd system fails first, so they are reported alongside.
     */
    /// <summary>
    /// The distribution of frame times from one benchmark run, in milliseconds.
    /// </summary>
    public readonly struct FrameTimeStats
    {

        /// <summary>
        /// How many frames went into these numbers.
        /// </summary>
        public int Count { get; }

        /// <summary>
        /// Arithmetic mean frame time across every frame recorded, not just the ones still in the percentile window.
        /// </summary>
        public float Mean { get; }

        /// <summary>
        /// The middle frame time, which ignores the spikes the mean is dragged around by.
        /// </summary>
        public float Median { get; }

        /// <summary>
        /// The frame time 95 percent of frames came in under.
        /// </summary>
        public float P95 { get; }

        /// <summary>
        /// The frame time 99 percent of frames came in under, which is roughly what a player registers as a hitch.
        /// </summary>
        public float P99 { get; }

        /// <summary>
        /// The single worst frame in the run.
        /// </summary>
        public float Max { get; }

        public FrameTimeStats(int count, float mean, float median, float p95, float p99, float max)
        {
            Count = count;
            Mean = mean;
            Median = median;
            P95 = p95;
            P99 = p99;
            Max = max;
        }

        /// <summary>
        /// One aligned line for a comparison table.
        /// </summary>
        /// <returns>The numbers formatted to two decimals.</returns>
        public override string ToString() => $"mean {Mean,6:F2}   median {Median,6:F2}   p95 {P95,6:F2}   p99 {P99,6:F2}   max {Max,6:F2}   ({Count} frames)";

        /// <summary>
        /// A shorter form for a live overlay, where the p99 is noise until a run has been going a while.
        /// </summary>
        /// <returns>Average, median, p95 and max, formatted to two decimals.</returns>
        public string ToShortString() => $"avg {Mean:F2}   median {Median:F2}   p95 {P95:F2}   max {Max:F2}   ({Count} frames)";

    }
}

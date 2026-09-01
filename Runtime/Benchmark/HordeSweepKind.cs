namespace MiHordeTraffic.Benchmark
{
    /// <summary>
    /// What a sweep varies between its measured runs.
    /// </summary>
    public enum HordeSweepKind
    {
        AVOIDANCE_MODES, // built in against the separation system against neither, respawning the crowd for each
        QUALITY_PRESETS // one crowd, spawned once, measured at each quality level in turn
    }
}

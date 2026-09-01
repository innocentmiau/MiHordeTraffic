namespace MiHordeTraffic.Benchmark
{
    /// <summary>
    /// Which avoidance the benchmark configures its spawned agents to use.
    /// </summary>
    public enum HordeBenchmarkMode
    {
        BUILT_IN_AVOIDANCE, // NavMeshAgent solves avoidance itself on the main thread, the thing being measured against
        SEPARATION_SYSTEM, // built in avoidance off, jobified separation on
        NONE // neither, so agents walk straight through each other, the floor of what pathfinding alone costs
    }
}

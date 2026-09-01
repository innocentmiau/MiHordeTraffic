namespace MiHordeTraffic.Benchmark
{
    /// <summary>
    /// How the benchmark's chase target moves, which is what decides how much repathing the horde has to do.
    /// </summary>
    public enum HordeTargetMotion
    {
        STATIC, // never moves, so every repath returns the path the agent already had and nothing is stressed
        ORBIT, // circles the arena, which keeps the crowd packed into a moving clump and paths changing a little every frame
        TELEPORT // jumps to a random point on the navmesh every few seconds, forcing a full long path recalculation on every agent at once
    }
}

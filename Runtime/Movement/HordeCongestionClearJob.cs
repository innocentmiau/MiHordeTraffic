using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace MiHordeTraffic.Movement
{
    /*
     * The first of four passes that used to be one single threaded job over every cell in the grid. Splitting them
     * is what lets three of the four run on every worker at once, and the reason it matters is that three of them
     * are proportional to the grid and only one is proportional to the crowd. On a kilometre of map at one metre a
     * frame spent four million writes here before a single body had been looked at.
     *
     * Nothing in this pass reads anything, so there is no order to preserve inside it and every index is
     * independent. It is pure bandwidth, which is exactly the shape that parallelises perfectly.
     */
    /// <summary>
    /// Empties the per cell accumulators before a frame's bodies are counted into them.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct HordeCongestionClearJob : IJobParallelFor
    {

        [WriteOnly] public NativeArray<float> SpeedSum;
        [WriteOnly] public NativeArray<float> ReferenceSum;
        [WriteOnly] public NativeArray<int> Counts;
        [WriteOnly] public NativeArray<int> Occupancy;

        public void Execute(int index)
        {
            SpeedSum[index] = 0f;
            ReferenceSum[index] = 0f;
            Counts[index] = 0;
            Occupancy[index] = 0;
        }

    }
}

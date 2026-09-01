using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeTraffic.Separation
{
    /*
     * A parallel writer is used rather than filling the map on the main thread because the fill is the one part of the tick that is linear in agent count and
     * has nothing to think about, so it costs almost nothing to hand to the workers alongside the job that reads it.
     *
     * The map has to be at capacity before this runs. A parallel writer cannot grow one, which is why the system resizes at its sync point instead.
     */
    /// <summary>
    /// Buckets every agent index into its spatial hash cell so the push job only looks at nearby agents.
    /// </summary>
    [BurstCompile]
    public struct SpatialHashBuildJob : IJobParallelFor
    {

        [ReadOnly] public NativeArray<float3> Positions;

        public float CellSize;
        public float3 PlaneMask;

        public NativeParallelMultiHashMap<int, int>.ParallelWriter Grid;

        public void Execute(int index)
        {
            Grid.Add(SpatialHash.KeyOf(Positions[index] * PlaneMask, CellSize), index);
        }

    }
}

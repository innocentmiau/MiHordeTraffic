using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace MiHordeTraffic.Movement
{
    /*
     * The first of the congestion passes, and it now runs over the cells that have something in them rather than
     * over the grid. Five thousand bodies can stand in at most five thousand cells; on a kilometre of map at one
     * metre the other nine hundred and ninety five thousand hold nothing, are heading for nothing, and used to be
     * cleared and repriced every frame anyway.
     *
     * The active set is what the gather adds to and the prune takes away from, so this walks a list whose length is
     * only known once the previous frame's prune has run. Deferred scheduling is what lets that be true: the length
     * is read when the job starts rather than when it is queued.
     */
    /// <summary>
    /// Empties the per cell accumulators of every cell still being tracked, before a frame's bodies are counted in.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct HordeCongestionClearJob : IJobParallelForDefer
    {

        [ReadOnly] public NativeArray<int> Active;

        [NativeDisableParallelForRestriction] public NativeArray<float> SpeedSum;
        [NativeDisableParallelForRestriction] public NativeArray<float> ReferenceSum;
        [NativeDisableParallelForRestriction] public NativeArray<int> Counts;
        [NativeDisableParallelForRestriction] public NativeArray<int> Occupancy;

        public void Execute(int index)
        {
            int cell = Active[index];

            SpeedSum[cell] = 0f;
            ReferenceSum[cell] = 0f;
            Counts[cell] = 0;
            Occupancy[cell] = 0;
        }

    }
}

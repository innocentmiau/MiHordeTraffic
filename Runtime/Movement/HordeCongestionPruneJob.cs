using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeTraffic.Movement
{
    /*
     * What lets the active set shrink again, and therefore what stops it becoming the whole grid an hour into a
     * session. A cell a crowd walked across is tracked until its density has decayed back to empty and its cost
     * back to one, and then it is dropped and costs nothing again until somebody stands in it.
     *
     * Snapped to the resting values rather than left at whatever tiny number the decay reached, because decay is
     * exponential and never arrives. Without the snap every cell the crowd ever touched would stay a fraction above
     * zero for good, no cell would ever qualify to be dropped, and the set would only ever grow.
     *
     * Cost is snapped for the same reason and the resting value matters more than it looks: the blur reads the
     * neighbours of an active cell whether or not they are active themselves, so a dropped cell has to read as
     * exactly ordinary ground or it would go on quietly pricing its neighbours.
     */
    /// <summary>
    /// Drops cells that have decayed back to empty out of the active set, snapping them to their resting values.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct HordeCongestionPruneJob : IJob
    {

        private const float RESTING_DENSITY = .001f;
        private const float RESTING_COST = .001f;

        [ReadOnly] public NativeArray<int> Occupancy;

        public NativeList<int> Active;
        public NativeArray<byte> Touched;
        public NativeArray<float> Density;
        public NativeArray<float> Cost;
        public NativeArray<float> ReferenceSum;

        public bool WriteCost;

        public void Execute()
        {
            /*
             * Backwards, because dropping a cell swaps the last one into its place and that one has not been looked
             * at yet. Walking forwards would step straight over it.
             */
            for (int i = Active.Length - 1; i >= 0; i--)
            {
                int cell = Active[i];

                if (Occupancy[cell] > 0) continue;
                if (Density[cell] > RESTING_DENSITY) continue;
                if (WriteCost && math.abs(Cost[cell] - 1f) > RESTING_COST) continue;

                Density[cell] = 0f;
                Cost[cell] = 1f;
                ReferenceSum[cell] = 1f;
                Touched[cell] = 0;

                Active.RemoveAtSwapBack(i);
            }
        }

    }
}

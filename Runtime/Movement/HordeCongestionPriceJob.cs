using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeTraffic.Movement
{
    /*
     * What each cell would like to cost, worked out from what the bodies standing in it managed. Runs on every
     * worker because each cell's answer depends only on its own accumulators, which is what makes this the pass
     * that gains the most from being split out: it is the heaviest of the four and every index is independent.
     *
     * The answer is parked back in ReferenceSum rather than written into Cost, because the blur that follows needs
     * every cell's raw answer. Writing straight into Cost would have later cells blurring against values that had
     * already been smoothed, which drags the whole field towards whatever the sweep happened to reach first.
     */
    /// <summary>
    /// Turns a frame of per cell measurements into how expensive each cell wants to be, and smooths its occupancy.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct HordeCongestionPriceJob : IJobParallelForDefer
    {

        private const float MINIMUM_SPEED_FRACTION = .05f;

        [ReadOnly] public NativeArray<int> Active;
        [ReadOnly] public NativeArray<byte> Walkable;
        [ReadOnly] public NativeArray<float> SpeedSum;
        [ReadOnly] public NativeArray<int> Counts;
        [ReadOnly] public NativeArray<int> Occupancy;
        [NativeDisableParallelForRestriction] public NativeArray<float> Density;
        [NativeDisableParallelForRestriction] public NativeArray<float> ReferenceSum;

        public bool WriteCost;
        public float Rise;
        public float Fall;
        public float MaximumCost;
        public float CellCapacity;
        public float ComfortableFill;
        public float JamFill;

        public void Execute(int slot)
        {
            int index = Active[slot];

            /*
             * Smoothed rather than taken raw, because a body straddling a cell boundary flickers between two cells
             * frame to frame and a raw count would have bodies stuttering as they crossed every line.
             *
             * Decayed for unwalkable cells too, unlike the price below it. Ground can stop being walkable under a
             * crowd now that structures block cells at runtime, and a cell that never decays is one the prune can
             * never retire, which would leak a row into the active set for every cell ever built on.
             */
            float previous = Density[index];
            int occupancy = Occupancy[index];
            float density = math.lerp(previous, occupancy, occupancy > previous ? Rise : Fall);

            Density[index] = density;

            if (!WriteCost || Walkable[index] == 0) return;

            /*
             * An empty cell is worth one, not whatever it was last measured at. A jam that clears has to stop being
             * expensive, and nothing crosses a cell to tell it so.
             */
            float target = 1f;

            if (Counts[index] > 0)
            {
                /*
                 * Somebody here is trying to get somewhere, so how well they manage it is the whole answer. A cell
                 * packed with bodies that are all moving freely is a column, not a jam.
                 *
                 * Measured against what those particular bodies could have done here rather than against a crowd
                 * wide walking pace, so a cell is only expensive when the bodies in it are managing less than they
                 * were capable of.
                 */
                float wanted = ReferenceSum[index] / Counts[index];
                float measured = math.max(SpeedSum[index] / Counts[index], math.max(wanted * MINIMUM_SPEED_FRACTION, .0001f));

                target = math.clamp(wanted / measured, 1f, MaximumCost);
            }
            else if (occupancy > 0)
            {
                /*
                 * Bodies are standing here and none is trying to move, so there is no progress to measure and how
                 * full the cell is has to stand in for it. This is the crowd parked around a target in a doorway:
                 * it will never report a speed, and it is still a wall.
                 */
                float fill = density / CellCapacity;
                float crowding = math.saturate((fill - ComfortableFill) / math.max(JamFill - ComfortableFill, .001f));

                target = math.lerp(1f, MaximumCost, crowding);
            }

            ReferenceSum[index] = target;
        }

    }
}

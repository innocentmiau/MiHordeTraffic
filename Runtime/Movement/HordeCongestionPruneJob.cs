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

        /*
         * How far below the density that starts to matter a cell has to fall before it is dropped. Measured against
         * what a cell holds at rest and the fill at which bodies begin easing off, rather than as an absolute, so
         * it means the same thing whatever the cells are and whatever the crowd is made of.
         *
         * The absolute it replaced was a thousandth of a body, which is not a number anything behaves differently
         * at, and decay is exponential: getting there from a single body took about seven seconds. Every cell the
         * crowd walked over stayed tracked for that long, so what the pass really cost was the crowd multiplied by
         * the length of the trail behind it, and at a metre a cell that was fifteen cells per body.
         *
         * Nothing changes when a cell is dropped. Crowding is zero for any fill at or under the comfortable one, so
         * a quarter of the way there is a density every reader of it already treats as empty.
         */
        private const float RESTING_FRACTION = .25f;
        /*
         * Two percent, because cost is a ratio and what it buys is a preference between routes. A cell a fiftieth
         * dearer than ordinary ground bends nothing: the expansion compares whole cells of distance, and a
         * fiftieth of one is not a detour anybody takes.
         *
         * A thousandth was the same mistake the density threshold was. Cost decays towards one at the same rate
         * everything else does, so coming back from a jam of five took about eight and a half seconds to get that
         * close, and every cell of a jam stayed tracked for all of it. A blocked route cleared ten seconds ago was
         * still being blurred every frame.
         */
        private const float RESTING_COST = .02f;

        [ReadOnly] public NativeArray<int> Occupancy;

        public NativeList<int> Active;
        public NativeArray<byte> Touched;
        public NativeArray<float> Density;
        public NativeArray<float> Cost;
        public NativeArray<float> ReferenceSum;

        public bool WriteCost;
        public float CellCapacity;
        public float ComfortableFill;

        public void Execute()
        {
            float resting = math.max(CellCapacity * ComfortableFill * RESTING_FRACTION, .001f);

            /*
             * Backwards, because dropping a cell swaps the last one into its place and that one has not been looked
             * at yet. Walking forwards would step straight over it.
             */
            for (int i = Active.Length - 1; i >= 0; i--)
            {
                int cell = Active[i];

                if (Occupancy[cell] > 0) continue;
                if (Density[cell] > resting) continue;
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

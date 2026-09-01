using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * The second half of a flow field. The expansion leaves every cell knowing what it costs to get home; this
     * turns that into which way to walk, once, so a body reads one vector instead of comparing eight neighbours
     * every frame. At a couple of thousand bodies that is the difference between eight lookups each and one.
     *
     * Parallel because every cell's answer depends only on its neighbours' costs, which are finished and read only
     * by the time this runs.
     *
     * Two ways of reading the same field. Stepping to the cheapest neighbour is the obvious one and it is what
     * makes a crowd walk on eight headings, which reads as robotic however smoothly the bodies themselves turn:
     * the quantisation is in the field, not in the turning, so no amount of interpolating a body's rotation
     * removes it. Adding more neighbours to sample would buy sixteen headings instead of eight and cost more.
     *
     * Following the slope of the cost field costs the same as the eight neighbour version and gives any angle at
     * all, because a gradient is not a choice between directions. Blocked neighbours contribute the centre cell's
     * own cost rather than a large one, which leaves the slope one sided along a wall and so sends bodies parallel
     * to it instead of into or away from it.
     */
    /// <summary>
    /// Turns a cost field into a direction per cell, so a body only has to read the cell it is standing in.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct GridFlowDirectionJob : IJobParallelFor
    {

        public HordeGridInfo Grid;

        [ReadOnly] public NativeArray<byte> Walkable;
        [ReadOnly] public NativeArray<float> Integration;

        public FlowDirectionMode Mode;

        [WriteOnly] public NativeArray<float2> Flow;

        public void Execute(int index)
        {
            if (Walkable[index] == 0 || Integration[index] == float.MaxValue)
            {
                Flow[index] = float2.zero;
                return;
            }

            int2 cell = Grid.CellAt(index);

            Flow[index] = Mode == FlowDirectionMode.GRADIENT ? Gradient(index, cell) : Neighbour(index, cell);
        }

        /// <summary>
        /// The direction of the cheapest neighbouring cell, which is one of eight.
        /// </summary>
        private float2 Neighbour(int index, int2 cell)
        {
            float best = Integration[index];
            int2 bestStep = int2.zero;

            for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++)
            {
                if (x == 0 && z == 0) continue;

                int2 neighbour = cell + new int2(x, z);
                if (!Grid.Contains(neighbour)) continue;

                int neighbourIndex = Grid.IndexOf(neighbour);
                if (Walkable[neighbourIndex] == 0) continue;

                float cost = Integration[neighbourIndex];
                if (cost >= best) continue;

                best = cost;
                bestStep = new int2(x, z);
            }

            return math.normalizesafe(new float2(bestStep.x, bestStep.y), float2.zero);
        }

        /// <summary>
        /// The downhill direction of the cost field, which can be any angle.
        /// </summary>
        private float2 Gradient(int index, int2 cell)
        {
            float centre = Integration[index];

            float left = Sample(cell + new int2(-1, 0), centre);
            float right = Sample(cell + new int2(1, 0), centre);
            float down = Sample(cell + new int2(0, -1), centre);
            float up = Sample(cell + new int2(0, 1), centre);

            /*
             * Downhill, so the cheaper side wins and the vector points at the goal rather than away from it.
             */
            float2 gradient = new float2(left - right, down - up);

            return math.normalizesafe(gradient, Neighbour(index, cell));
        }

        /*
         * A neighbour that is off the grid, unwalkable or unreached contributes the centre's own cost, which makes
         * that side of the slope flat. Substituting a large value instead would turn every wall into a hill the
         * field pushes bodies down, and every crowd would peel away from walls it should be walking along.
         */
        private float Sample(int2 cell, float fallback)
        {
            if (!Grid.Contains(cell)) return fallback;

            int index = Grid.IndexOf(cell);

            if (Walkable[index] == 0) return fallback;

            float cost = Integration[index];

            return cost == float.MaxValue ? fallback : cost;
        }

    }
}

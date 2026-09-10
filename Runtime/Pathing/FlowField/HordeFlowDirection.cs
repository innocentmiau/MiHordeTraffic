using Unity.Collections;
using Unity.Mathematics;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * Which way to walk out of a cell, worked out where it is asked for rather than stored for every cell in the
     * map. The integration field is the answer already: a direction is the local slope of it, four neighbour reads
     * and a fallback, and nothing about that gets cheaper by having been done earlier.
     *
     * It used to be a parallel job over the whole grid after every expansion, writing an array of a million
     * directions of which a crowd of five thousand ever read five thousand. The waste was not the memory, though
     * that was eight megabytes; it was that the job occupied every worker for the few frames it took. Nothing was
     * waiting on it, because the movement reads the previous field while the next one is built, and it did not need
     * to: with the pool full, the mover's own chain had nobody to run it and the main thread ended up doing all of
     * it alone. That is what a spike of sixteen milliseconds against an average of one and a half was.
     *
     * So the direction of a cell is now computed by whoever wants it, for the cell they are standing in. A body
     * pays four array reads. The map pays nothing.
     *
     * Callable from a job or from the main thread. Inside a Burst job it inlines; the debug drawing outside one
     * calls it a few hundred times a frame and does not care.
     */
    /// <summary>
    /// The direction out of a cell towards the goal, taken from the integration field on demand.
    /// </summary>
    public static class HordeFlowDirection
    {

        /// <summary>
        /// Which way a body standing in a cell should walk.
        /// </summary>
        /// <param name="grid">The grid the cell belongs to.</param>
        /// <param name="walkable">Per cell walkability.</param>
        /// <param name="integration">Per cell cost to reach the goal.</param>
        /// <param name="index">Flat index of the cell.</param>
        /// <param name="mode">Whether to take the slope or step to the cheapest neighbour.</param>
        /// <returns>A unit direction on the grid plane, or zero when there is no route out of this cell.</returns>
        public static float2 At(HordeGridInfo grid, NativeArray<byte> walkable, NativeArray<float> integration, int index, FlowDirectionMode mode)
        {
            if (index < 0 || walkable[index] == 0 || integration[index] == float.MaxValue) return float2.zero;

            int2 cell = grid.CellAt(index);

            return mode == FlowDirectionMode.GRADIENT
                ? Gradient(grid, walkable, integration, index, cell)
                : Neighbour(grid, walkable, integration, index, cell);
        }

        /// <summary>
        /// The direction of the cheapest neighbouring cell, which is one of eight.
        /// </summary>
        private static float2 Neighbour(HordeGridInfo grid, NativeArray<byte> walkable, NativeArray<float> integration, int index, int2 cell)
        {
            float best = integration[index];
            int2 bestStep = int2.zero;

            for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++)
            {
                if (x == 0 && z == 0) continue;

                int2 neighbour = cell + new int2(x, z);
                if (!grid.Contains(neighbour)) continue;

                int neighbourIndex = grid.IndexOf(neighbour);
                if (walkable[neighbourIndex] == 0) continue;

                float cost = integration[neighbourIndex];
                if (cost >= best) continue;

                best = cost;
                bestStep = new int2(x, z);
            }

            return math.normalizesafe(new float2(bestStep.x, bestStep.y), float2.zero);
        }

        /// <summary>
        /// The downhill direction of the cost field, which can be any angle.
        /// </summary>
        private static float2 Gradient(HordeGridInfo grid, NativeArray<byte> walkable, NativeArray<float> integration, int index, int2 cell)
        {
            float centre = integration[index];

            bool leftOpen = TrySample(grid, walkable, integration, cell + new int2(-1, 0), centre, out float left);
            bool rightOpen = TrySample(grid, walkable, integration, cell + new int2(1, 0), centre, out float right);
            bool downOpen = TrySample(grid, walkable, integration, cell + new int2(0, -1), centre, out float down);
            bool upOpen = TrySample(grid, walkable, integration, cell + new int2(0, 1), centre, out float up);

            /*
             * Downhill, so the cheaper side wins and the vector points at the goal rather than away from it.
             */
            float x = left - right;
            float z = down - up;

            /*
             * A blocked side is allowed to flatten its axis and never to win it. Standing in for it with the
             * centre's own cost makes the arithmetic say the wall is exactly as expensive as here, which is fine
             * while the open side is downhill and a lie the moment it is not: an uphill neighbour on one side and a
             * wall on the other reads as the wall being the cheaper way, and the vector points into it.
             *
             * That is not a corner case, it is what a congested route does. Cost rises on the ground the crowd is
             * actually using, so the open side gets steeper, and every cell along a wall beside a busy route starts
             * aiming at the wall. It shows up as bodies peeling off towards a blocked way, being refused at its
             * face, and drifting back, over and over, and it gets worse the busier the route gets.
             *
             * Clamping per axis keeps what the substitution was for. A wall still does not push bodies away from
             * itself, so a crowd walking along one keeps hugging it; it just cannot pull them in.
             */
            if (!rightOpen) x = math.min(x, 0f);
            if (!leftOpen) x = math.max(x, 0f);
            if (!upOpen) z = math.min(z, 0f);
            if (!downOpen) z = math.max(z, 0f);

            return math.normalizesafe(new float2(x, z), Neighbour(grid, walkable, integration, index, cell));
        }

        /*
         * A neighbour that is off the grid, unwalkable or unreached contributes the centre's own cost, which makes
         * that side of the slope flat. Substituting a large value instead would turn every wall into a hill the
         * field pushes bodies down, and every crowd would peel away from walls it should be walking along.
         *
         * Whether it was a real reading is handed back as well, because flat is only half of what a wall means and
         * the caller needs the other half: it may not be steered into either.
         */
        private static bool TrySample(HordeGridInfo grid, NativeArray<byte> walkable, NativeArray<float> integration, int2 cell, float fallback, out float cost)
        {
            cost = fallback;

            if (!grid.Contains(cell)) return false;

            int index = grid.IndexOf(cell);

            if (walkable[index] == 0) return false;

            float reading = integration[index];

            if (reading == float.MaxValue) return false;

            cost = reading;
            return true;
        }

    }
}

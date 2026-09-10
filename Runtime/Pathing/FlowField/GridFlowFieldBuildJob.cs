using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * Dijkstra outward from the goal across every walkable cell, which is the whole point: a thousand agents
     * heading for one target are a thousand searches under per agent A* and exactly one here. The cost is a
     * function of the map, not of the crowd, so it does not grow as the horde does.
     *
     * Outward from the goal rather than towards it, because that is what makes the answer reusable. A search from
     * an agent answers only that agent's question; a search from the goal answers it for every cell at once, and
     * every agent then reads the one it happens to be standing in.
     *
     * Dijkstra rather than a plain wavefront, because Cost is a per cell multiplier and not every step is equal.
     * That multiplier is what turns a jammed bridge into an expensive one, and once it is expensive the expansion
     * routes around it on its own. Nothing has to notice the jam, decide a detour is worthwhile, or compare two
     * routes: the comparison is what the expansion already does, and every agent gets the same answer for free.
     *
     * A binary heap over a NativeList rather than a bucket queue, because bucket queues want quantised costs and
     * a congestion multiplier is a float. Scanning for the cheapest node instead would make this quadratic in cell
     * count, which on any grid worth having is the difference between a fraction of a millisecond and a hitch.
     */
    /// <summary>
    /// Expands a cost field outward from the goal across the walkable grid, so every cell knows what it costs to get home.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct GridFlowFieldBuildJob : IJob
    {

        private const float DIAGONAL = 1.41421356f;

        public HordeGridInfo Grid;

        [ReadOnly] public NativeArray<byte> Walkable;
        [ReadOnly] public NativeArray<float> Cost;

        /*
         * The navmesh height under each cell, which the bake already sampled and nothing has ever read. Without it
         * the expansion is measuring a map drawn flat: two cells a step apart cost a step apart whether the ground
         * between them is level or a cliff face, so a crowd routes over a drop as happily as across a floor and a
         * hill costs exactly what going round it costs.
         */
        [ReadOnly] public NativeArray<float> Height;

        /*
         * Cells that are shut. They stay walkable so the expansion still reaches through them, and cost a great
         * deal so that any genuinely open way wins. Where every way is shut the crowd still gets an answer, and it
         * is the nearest door rather than nothing at all, which is what sends a crowd to stand at a mountain.
         */
        [ReadOnly] public NativeArray<int> Gated;

        public float GatePenalty;

        /*
         * Rise over run, above which two cells are not connected at all. Zero leaves every pair connected and only
         * prices the climb, which is the conservative reading of an existing map.
         *
         * It exists because a flat grid cannot see a ledge. The navmesh on top of one and the navmesh at the foot
         * of it are different surfaces, and they are neighbours in two dimensions, so the expansion will walk from
         * one to the other for the price of a step. One is a genuine slope worth climbing and the other is a fall,
         * and the only thing telling them apart is how much height the step covers.
         */
        public float MaximumSlope;

        /*
         * A tenth of a millimetre, so a cell centre sitting exactly on a target's edge is inside it rather than
         * being rejected over the last bit of a float.
         */
        private const float SEED_TOLERANCE = .0001f;

        public NativeArray<float> Integration;
        public NativeList<HeapEntry> Heap;
        public NativeArray<int> GoalIndex;

        /*
         * Where the goal is and how much ground it covers. A point seeds one cell, as it always did. A shape seeds
         * every walkable cell it covers, which turns this into a multi source expansion and costs nothing extra:
         * each cell is still settled exactly once, the heap simply starts with more than one entry in it.
         */
        public HordeGoalArea Area;
        public int GoalSearchRadius;

        public void Execute()
        {
            for (int i = 0; i < Integration.Length; i++)
                Integration[i] = float.MaxValue;

            Heap.Clear();

            if (Seed() == 0) return;

            while (Heap.Length > 0)
            {
                HeapEntry entry = Pop();

                /*
                 * A cell can sit in the heap more than once, because a cheaper way into it can turn up after it was
                 * already queued. Skipping the stale copies is cheaper than finding and rewriting them.
                 */
                if (entry.Cost > Integration[entry.Index]) continue;

                int2 cell = Grid.CellAt(entry.Index);

                for (int z = -1; z <= 1; z++)
                for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && z == 0) continue;

                    int2 neighbour = cell + new int2(x, z);
                    if (!Grid.Contains(neighbour)) continue;

                    int index = Grid.IndexOf(neighbour);
                    if (Walkable[index] == 0) continue;

                    /*
                     * A diagonal is only taken when both cells beside it are open. Without this a crowd cuts the
                     * corner of a wall and walks through it, which on a grid coarse enough to be cheap happens
                     * constantly around every pillar and doorway.
                     */
                    if (x != 0 && z != 0)
                    {
                        int2 sideX = cell + new int2(x, 0);
                        int2 sideZ = cell + new int2(0, z);

                        if (!Grid.Contains(sideX) || Walkable[Grid.IndexOf(sideX)] == 0) continue;
                        if (!Grid.Contains(sideZ) || Walkable[Grid.IndexOf(sideZ)] == 0) continue;
                    }

                    /*
                     * The real distance between the two cell centres rather than the distance on the map, so a
                     * climb costs what a climb costs. A cell up one and along one is not a step of one, it is the
                     * hypotenuse of both, and up one along a diagonal is the hypotenuse of that again. Nothing has
                     * to be tuned for it: it falls out of the geometry the bake already measured.
                     */
                    float planar = (x != 0 && z != 0 ? DIAGONAL : 1f) * Grid.CellSize;
                    float rise = Height[index] - Height[entry.Index];

                    if (MaximumSlope > 0f && math.abs(rise) > MaximumSlope * planar) continue;

                    float penalty = Gated[index] > 0 ? GatePenalty : 1f;
                    float step = math.sqrt(planar * planar + rise * rise) * Cost[index] * penalty;
                    float total = entry.Cost + step;

                    if (total >= Integration[index]) continue;

                    Integration[index] = total;
                    Push(new HeapEntry { Cost = total, Index = index });
                }
            }
        }

        /*
         * Every cell the target covers becomes a source, so the field measures the distance to the nearest part of
         * it rather than to its middle. That is what lets a crowd surround a building: with one source, everybody
         * on the map walks to the single cell nearest the centre and queues against one face of it, whichever way
         * they came from.
         *
         * Rings outward when the target covers no walkable ground, which is the normal case for anything solid: a
         * keep blocks every cell under itself, so the first ring that finds anything is the ground around its
         * walls, and seeding all of it at once puts bodies on every side.
         *
         * A point keeps the old single cell search untouched. Widening a point into a disc would change how every
         * existing crowd converges, and a target that wants a size can say so.
         */
        private int Seed()
        {
            if (Area.Shape == HordeTargetShape.POINT)
            {
                int goal = NearestWalkable(Area.Centre);

                GoalIndex[0] = goal;

                if (goal < 0) return 0;

                Integration[goal] = 0f;
                Push(new HeapEntry { Cost = 0f, Index = goal });

                return 1;
            }

            for (int radius = 0; radius <= GoalSearchRadius; radius++)
            {
                int seeded = SeedWithin(radius);

                if (seeded > 0) return seeded;
            }

            GoalIndex[0] = -1;
            return 0;
        }

        /*
         * Cells no further than the given number of cells from the target's edge. Anything nearer than that was
         * looked at on an earlier ring and found unwalkable, which is the only reason there is a later one, so
         * nothing is seeded twice.
         */
        private int SeedWithin(int radius)
        {
            float reach = radius * Grid.CellSize;
            float2 half = Area.Reach + reach;

            int2 min = Grid.CellOf(new float3(Area.Centre.x - half.x, 0f, Area.Centre.z - half.y));
            int2 max = Grid.CellOf(new float3(Area.Centre.x + half.x, 0f, Area.Centre.z + half.y));

            min = math.max(min, int2.zero);
            max = math.min(max, new int2(Grid.Width - 1, Grid.Height - 1));

            int seeded = 0;

            for (int z = min.y; z <= max.y; z++)
            for (int x = min.x; x <= max.x; x++)
            {
                int2 cell = new int2(x, z);
                int index = Grid.IndexOf(cell);

                if (Walkable[index] == 0) continue;

                float3 centre = Grid.CentreOf(cell);

                if (Area.Distance(centre.xz) > reach + SEED_TOLERANCE) continue;

                Integration[index] = 0f;
                Push(new HeapEntry { Cost = 0f, Index = index });

                if (seeded == 0) GoalIndex[0] = index;

                seeded++;
            }

            return seeded;
        }

        /*
         * The goal cell is not always walkable, and treating that as "no field" is the difference between a crowd
         * that keeps following the player and one that stops dead. A target standing on a carved obstacle, clipped
         * into geometry, or a step outside the navmesh would otherwise blank the field for everybody at once,
         * and it would look like the whole pathfinding system had failed rather than one cell being unavailable.
         *
         * Searched as expanding rings so the first walkable cell found is the nearest one, and bounded so a goal
         * genuinely far from the navmesh gives up rather than scanning the entire grid every rebuild.
         */
        private int NearestWalkable(float3 position)
        {
            int2 origin = Grid.CellOf(position);

            if (Grid.Contains(origin) && Walkable[Grid.IndexOf(origin)] != 0) return Grid.IndexOf(origin);

            for (int radius = 1; radius <= GoalSearchRadius; radius++)
            {
                for (int z = -radius; z <= radius; z++)
                for (int x = -radius; x <= radius; x++)
                {
                    if (math.abs(x) != radius && math.abs(z) != radius) continue;

                    int2 cell = origin + new int2(x, z);
                    if (!Grid.Contains(cell)) continue;

                    int index = Grid.IndexOf(cell);
                    if (Walkable[index] != 0) return index;
                }
            }

            return -1;
        }

        private void Push(HeapEntry entry)
        {
            Heap.Add(entry);

            int child = Heap.Length - 1;

            while (child > 0)
            {
                int parent = (child - 1) / 2;
                if (Heap[parent].Cost <= Heap[child].Cost) break;

                (Heap[parent], Heap[child]) = (Heap[child], Heap[parent]);
                child = parent;
            }
        }

        private HeapEntry Pop()
        {
            HeapEntry top = Heap[0];
            int last = Heap.Length - 1;

            Heap[0] = Heap[last];
            Heap.RemoveAt(last);

            int parent = 0;

            while (true)
            {
                int left = parent * 2 + 1;
                if (left >= Heap.Length) break;

                int smallest = left;
                int right = left + 1;

                if (right < Heap.Length && Heap[right].Cost < Heap[left].Cost) smallest = right;
                if (Heap[parent].Cost <= Heap[smallest].Cost) break;

                (Heap[parent], Heap[smallest]) = (Heap[smallest], Heap[parent]);
                parent = smallest;
            }

            return top;
        }

        /// <summary>
        /// One cell waiting on the expansion frontier.
        /// </summary>
        public struct HeapEntry
        {

            /// <summary>
            /// Cost from this cell back to the goal.
            /// </summary>
            public float Cost;

            /// <summary>
            /// Flat grid index of the cell.
            /// </summary>
            public int Index;

        }

    }
}

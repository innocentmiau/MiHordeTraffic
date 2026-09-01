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

        public NativeArray<float> Integration;
        public NativeList<HeapEntry> Heap;
        public NativeArray<int> GoalIndex;

        public float3 Goal;
        public int GoalSearchRadius;

        public void Execute()
        {
            for (int i = 0; i < Integration.Length; i++)
                Integration[i] = float.MaxValue;

            Heap.Clear();

            int goal = NearestWalkable(Goal);
            GoalIndex[0] = goal;

            if (goal < 0) return;

            Integration[goal] = 0f;
            Push(new HeapEntry { Cost = 0f, Index = goal });

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

                    float step = (x != 0 && z != 0 ? DIAGONAL : 1f) * Grid.CellSize * Cost[index];
                    float total = entry.Cost + step;

                    if (total >= Integration[index]) continue;

                    Integration[index] = total;
                    Push(new HeapEntry { Cost = total, Index = index });
                }
            }
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

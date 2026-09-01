using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.AI;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * Owns the grid, the buffers and the schedule. The bake is separate from the expansion and runs once, because
     * NavMesh.SamplePosition is a call out to native per cell and doing that per rebuild would cost far more than
     * the routing it serves. The expansion afterwards touches no engine API at all, which is the whole reason for
     * baking rather than querying the navmesh as we go.
     *
     * Cost starts at one everywhere and is the hook congestion writes into. Nothing else about the expansion has
     * to know congestion exists.
     */
    /// <summary>
    /// A flow field over a baked walkability grid, expanded outward from a goal so every cell knows which way to walk.
    /// </summary>
    public class GridFlowField : IDisposable
    {

        private static readonly ProfilerMarker BAKE_MARKER = new ProfilerMarker("MiHordeTraffic.Pathing.Bake");
        private static readonly ProfilerMarker BUILD_MARKER = new ProfilerMarker("MiHordeTraffic.Pathing.FlowField");

        private NativeArray<byte> _walkable;
        private NativeArray<float> _cost;
        private NativeArray<float> _integration;
        private NativeArray<float2> _flow;
        private NativeArray<float> _height;
        private NativeArray<float> _density;
        private NativeList<GridFlowFieldBuildJob.HeapEntry> _heap;
        private NativeArray<int> _goalIndex;

#if UNITY_EDITOR
        /*
         * Baking is on a context menu, which means it can be run in edit mode, which means these buffers can exist
         * without any MonoBehaviour lifetime around them: nothing is ever destroyed in edit mode, so OnDestroy
         * never comes and the memory survives until the domain reloads and the collections detector reports it as
         * a leak with no stack to point at.
         *
         * Held in a list rather than found through the driver, because in edit mode the driver's Awake has not run
         * and its static instance is null, so there is nothing to ask. Anything that has allocated puts itself
         * here and is released before the reload, whoever owns it.
         */
        private static readonly List<GridFlowField> LIVE = new List<GridFlowField>();

        [UnityEditor.InitializeOnLoadMethod]
        private static void ReleaseBeforeAssemblyReload() =>
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                for (int i = LIVE.Count - 1; i >= 0; i--)
                    LIVE[i].Dispose();

                LIVE.Clear();
            };
#endif

        private JobHandle _handle;
        private bool _scheduled;

        /// <summary>
        /// The shape of the grid.
        /// </summary>
        public HordeGridInfo Grid { get; private set; }

        /// <summary>
        /// Whether a bake has produced a usable grid.
        /// </summary>
        public bool IsBaked => _walkable.IsCreated;

        /// <summary>
        /// Whether the last expansion reached anything.
        /// </summary>
        public bool IsBuilt { get; private set; }

        /// <summary>
        /// How many cells the bake found walkable.
        /// </summary>
        public int WalkableCells { get; private set; }

        /// <summary>
        /// What fraction of the grid is walkable. A low number means the bounds are much larger than the floor,
        /// and every cell outside it is memory and expansion checks spent on ground that does not exist.
        /// </summary>
        public float WalkableFraction => IsBaked && Grid.Count > 0 ? (float)WalkableCells / Grid.Count : 0f;

        /// <summary>
        /// Per cell traversal multipliers, which congestion weighting writes into. One means normal speed.
        /// </summary>
        public NativeArray<float> Cost => _cost;

        /// <summary>
        /// Per cell direction towards the goal.
        /// </summary>
        public NativeArray<float2> Flow => _flow;

        /// <summary>
        /// Smoothed count of bodies standing in each cell, which is what bodies slow down for.
        /// </summary>
        public NativeArray<float> Density => _density;

        /// <summary>
        /// Per cell walkability, as sampled at bake time.
        /// </summary>
        public NativeArray<byte> Walkable => _walkable;

        /// <summary>
        /// Per cell navmesh height, for putting bodies on the ground without asking the navmesh again.
        /// </summary>
        public NativeArray<float> Height => _height;

        /// <summary>
        /// Per cell cost to reach the goal, or float.MaxValue where the goal cannot be reached.
        /// </summary>
        public NativeArray<float> Integration => _integration;

        /// <summary>
        /// Samples the navmesh once per cell to decide what is walkable. Main thread, and not cheap, so call it
        /// when the map changes rather than when the crowd does.
        /// </summary>
        /// <param name="bounds">Area to cover.</param>
        /// <param name="cellSize">Width of one cell.</param>
        /// <param name="sampleHeight">How far above and below a cell centre to look for the navmesh.</param>
        /// <param name="edgeClearance">How far from unwalkable ground a cell must be to count as walkable.</param>
        /// <param name="areaMask">Navmesh areas to accept.</param>
        public void Bake(Bounds bounds, float cellSize, float sampleHeight, float edgeClearance, int areaMask)
        {
            Dispose();

            HordeGridInfo grid = new HordeGridInfo
            {
                /*
                 * Y is the plane the bake samples from and the height gizmos draw at, not the floor of the bounds
                 * volume. Cell maths only ever uses XZ, so putting the sample height here costs nothing and stops
                 * everything without a navmesh hit being drawn metres below the ground it is describing.
                 */
                Origin = new float3(bounds.min.x, bounds.center.y, bounds.min.z),
                CellSize = math.max(cellSize, .01f),
                Width = math.max(1, (int)math.ceil(bounds.size.x / math.max(cellSize, .01f))),
                Height = math.max(1, (int)math.ceil(bounds.size.z / math.max(cellSize, .01f)))
            };

            Grid = grid;

            int count = grid.Count;

            _walkable = new NativeArray<byte>(count, Allocator.Persistent);
            _cost = new NativeArray<float>(count, Allocator.Persistent);
            _integration = new NativeArray<float>(count, Allocator.Persistent);
            _flow = new NativeArray<float2>(count, Allocator.Persistent);
            _height = new NativeArray<float>(count, Allocator.Persistent);
            _density = new NativeArray<float>(count, Allocator.Persistent);
            _heap = new NativeList<GridFlowFieldBuildJob.HeapEntry>(math.max(count / 4, 64), Allocator.Persistent);
            _goalIndex = new NativeArray<int>(1, Allocator.Persistent);

#if UNITY_EDITOR
            if (!LIVE.Contains(this)) LIVE.Add(this);
#endif

            int walkable = 0;
            float halfCell = grid.CellSize * .5f;

            using (BAKE_MARKER.Auto())
            {
                for (int i = 0; i < count; i++)
                {
                    float3 centre = grid.CentreOf(grid.CellAt(i));

                    _cost[i] = 1f;

                    if (!NavMesh.SamplePosition(centre, out NavMeshHit hit, sampleHeight, areaMask)) continue;

                    /*
                     * The sample has to land inside this cell, not merely near it. SamplePosition returns the
                     * nearest point on a surface however far away it is, so a generous tolerance quietly invents
                     * walkable ground outside the navmesh: at a full cell of slack every edge in the level grows
                     * by a metre, and bodies walk that metre with half of themselves hanging over the drop.
                     *
                     * Half a cell is the honest test. The navmesh is already inset by the agent radius when Unity
                     * bakes it, and anything looser than this throws that inset away again.
                     */
                    if (math.distancesq(((float3)hit.position).xz, centre.xz) > halfCell * halfCell) continue;

                    _walkable[i] = 1;
                    _height[i] = hit.position.y;
                    walkable++;
                }
            }

            if (edgeClearance > 0f) walkable = Erode(edgeClearance);

            WalkableCells = walkable;
        }

        /*
         * Pulls the walkable area in from its edges, so a body is not asked to walk along ground where only its
         * centre fits. Unity's navmesh is already inset by the agent radius, but the radius it was baked for is
         * whatever the agent type says rather than how wide the model actually is, and a body half a metre across
         * standing exactly on the last legal centimetre still looks like it is falling off.
         *
         * Measured as a distance rather than counted in cells, so the inset means the same thing whatever the grid
         * resolution is. Read from a copy, because eroding in place would let a cell removed early in the sweep
         * remove its neighbours later in the same pass and eat inwards without stopping.
         */
        private int Erode(float clearance)
        {
            NativeArray<byte> source = new NativeArray<byte>(_walkable, Allocator.Temp);

            int reach = (int)math.ceil(clearance / Grid.CellSize);
            float clearanceSquared = clearance * clearance;
            int walkable = 0;

            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == 0) continue;

                int2 cell = Grid.CellAt(i);
                bool keep = true;

                for (int z = -reach; z <= reach && keep; z++)
                for (int x = -reach; x <= reach && keep; x++)
                {
                    int2 neighbour = cell + new int2(x, z);

                    bool blocked = !Grid.Contains(neighbour) || source[Grid.IndexOf(neighbour)] == 0;
                    if (!blocked) continue;

                    float2 offset = new float2(x, z) * Grid.CellSize;

                    if (math.lengthsq(offset) <= clearanceSquared) keep = false;
                }

                if (keep) walkable++;
                else _walkable[i] = 0;
            }

            source.Dispose();

            return walkable;
        }

        /// <summary>
        /// Schedules an expansion from a goal, if one is not already in flight.
        /// </summary>
        /// <param name="goal">Where the crowd is heading.</param>
        /// <param name="mode">How the cost field is turned into a direction.</param>
        /// <param name="goalSearchRadius">How many cells out to look for walkable ground when the goal is not on any.</param>
        /// <returns>True when an expansion was scheduled.</returns>
        public bool Schedule(float3 goal, FlowDirectionMode mode = FlowDirectionMode.GRADIENT, int goalSearchRadius = 8)
        {
            if (_scheduled || !IsBaked) return false;

            JobHandle build = new GridFlowFieldBuildJob
            {
                Grid = Grid,
                Walkable = _walkable,
                Cost = _cost,
                Integration = _integration,
                Heap = _heap,
                GoalIndex = _goalIndex,
                Goal = goal,
                GoalSearchRadius = math.max(0, goalSearchRadius)
            }
            .Schedule();

            _handle = new GridFlowDirectionJob
            {
                Grid = Grid,
                Walkable = _walkable,
                Integration = _integration,
                Mode = mode,
                Flow = _flow
            }
            .Schedule(Grid.Count, 64, build);

            _scheduled = true;
            return true;
        }

        /// <summary>
        /// Joins an outstanding expansion, if there is one.
        /// </summary>
        public void Complete()
        {
            if (!_scheduled) return;

            using (BUILD_MARKER.Auto())
                _handle.Complete();

            _scheduled = false;
            IsBuilt = _goalIndex[0] >= 0;
        }

        /// <summary>
        /// Which way a body standing at a world position should walk.
        /// </summary>
        /// <param name="position">World position.</param>
        /// <param name="direction">Direction on the grid plane, or zero when there is no route.</param>
        /// <returns>True when the position is on the grid and the goal is reachable from it.</returns>
        public bool TryGetFlow(float3 position, out float2 direction)
        {
            direction = float2.zero;

            if (!IsBuilt) return false;

            int index = Grid.IndexOf(position);
            if (index < 0 || _walkable[index] == 0) return false;

            direction = _flow[index];
            return !direction.Equals(float2.zero);
        }

        /// <summary>
        /// The navmesh height under a cell, for drawing and for placing bodies.
        /// </summary>
        /// <param name="index">Flat grid index.</param>
        /// <returns>World Y at that cell.</returns>
        public float HeightAt(int index) => _height[index];

        /// <summary>
        /// Whether a cell was found walkable at bake time.
        /// </summary>
        /// <param name="index">Flat grid index.</param>
        /// <returns>True when walkable.</returns>
        public bool IsWalkable(int index) => _walkable[index] != 0;

        /// <summary>
        /// Joins anything outstanding and frees every buffer.
        /// </summary>
        public void Dispose()
        {
            /*
             * Joining is allowed to fail without taking the buffers with it. Teardown order on quit is arbitrary,
             * and an exception out of Complete here would leave every array below still allocated and unowned.
             */
            if (_scheduled)
            {
                try
                {
                    _handle.Complete();
                }
                catch (System.Exception exception)
                {
                    Debug.LogException(exception);
                }

                _scheduled = false;
            }

            if (_walkable.IsCreated) _walkable.Dispose();
            if (_cost.IsCreated) _cost.Dispose();
            if (_integration.IsCreated) _integration.Dispose();
            if (_flow.IsCreated) _flow.Dispose();
            if (_height.IsCreated) _height.Dispose();
            if (_density.IsCreated) _density.Dispose();
            if (_heap.IsCreated) _heap.Dispose();
            if (_goalIndex.IsCreated) _goalIndex.Dispose();

            IsBuilt = false;
            WalkableCells = 0;

#if UNITY_EDITOR
            LIVE.Remove(this);
#endif
        }

    }
}

using System.Diagnostics;
using Unity.AI.Navigation;
using Unity.Mathematics;
using MiHordeTraffic.Tuning;
using UnityEngine;
using UnityEngine.AI;
using Debug = UnityEngine.Debug;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * Stage one of taking pathfinding off NavMeshAgent, and deliberately inert: it bakes the grid, expands the
     * field and draws it, and nothing reads it to move anything. The routing can be wrong in the editor without a
     * single agent misbehaving, which is the only sane way to bring up a pathfinding system.
     *
     * The expansion is scheduled and joined inside the same LateUpdate rather than deferred a frame. That reports
     * it as a main thread stall, which overstates what it will eventually cost, and overstating it is the right way
     * round while the question is whether this is affordable at all.
     */
    /// <summary>
    /// Bakes a walkability grid and keeps a flow field towards a target on it, without anything depending on it yet.
    /// </summary>
    /*
     * Ordered last of the three, so a rebuild lands after everything that reads the field has finished with it for
     * the frame rather than somewhere in the middle of them.
     */
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public class HordeFlowFieldDriver : MonoBehaviour
    {

        private static HordeFlowFieldDriver _instance;

        /// <summary>
        /// The driver in the loaded scene, or null when there is none.
        /// </summary>
        public static HordeFlowFieldDriver Instance => _instance;

        [Header("Grid")]
        /*
         * Fitting to the navmesh itself rather than to a surface volume or a hand typed box, because those two
         * describe where somebody intended the walkable ground to be and the triangulation describes where it
         * actually is. Every confusing result in this system so far has come from that gap: bodies spawning onto
         * real navmesh the grid had never heard of, then standing still forever because every step they could take
         * failed a walkability test against cells that did not exist.
         */
        [SerializeField] private bool fitToNavMesh = true;
        [SerializeField, Min(0f)] private float fitMargin = 2f;
        [SerializeField] private NavMeshSurface surface;
        [SerializeField] private Bounds bounds = new Bounds(Vector3.zero, new Vector3(100f, 10f, 100f));
        [Tooltip("Metres across one grid cell. Smaller is more precise routing and quadratically more memory and rebuild time.")]
        [SerializeField, Min(.1f)] private float cellSize = 1f;
        [Tooltip("Metres above and below a cell centre to look for the navmesh when baking.")]
        [SerializeField, Min(.1f)] private float sampleHeight = 4f;
        [Tooltip("Metres a cell must be from unwalkable ground to count as walkable, so bodies do not hug walls.")]
        [SerializeField, Min(0f)] private float edgeClearance = .5f;
        [SerializeField] private bool bakeOnStart = true;

        [Header("Field")]
        [SerializeField] private Transform target;

        /*
         * The three numbers under this cannot be read separately. An interval of a quarter second says nothing
         * about how quickly the crowd reacts until you also know that it expands early when the goal moves, and
         * how far it has to move, and how often that is allowed. Choosing them together is the only way any of
         * them means anything.
         */
        [Tooltip("How eagerly the field is rebuilt when the target moves. CUSTOM to edit the three numbers by hand.")]
        [SerializeField] private HordeFieldRefresh refreshRate = HordeFieldRefresh.BALANCED;
        /*
         * The longest the field may go without being expanded, which is what keeps it fresh while the target stands
         * still and everything is already correct. It is a ceiling rather than a schedule: waiting out a quarter of
         * a second before noticing that the goal has moved is most of what makes a crowd look slow to react, and
         * spending the same expansion four times a second on a target that has not moved buys nothing at all.
         */
        [Tooltip("Owned by Refresh Rate. Seconds the field may go unexpanded while the target sits still.")]
        [SerializeField, Min(0f)] private float rebuildInterval = .25f;

        /*
         * How far the goal may drift before the field is expanded early. Below a cell there is nothing to redraw,
         * since every body would read the same direction out of the same cell it read last time.
         */
        [Tooltip("Owned by Refresh Rate. Metres the goal may drift before expanding early. Below one cell there is nothing to redraw.")]
        [SerializeField, Min(0f)] private float rebuildDistance = 1f;

        /*
         * And how often that is allowed to happen, so a target moving quickly cannot ask for an expansion every
         * frame. Without it a sprinting target turns a cost paid four times a second into one paid sixty times.
         */
        [Tooltip("Owned by Refresh Rate. Seconds between expansions, so a fast target cannot ask for one every frame.")]
        [SerializeField, Min(0f)] private float minimumRebuildInterval = .05f;

        /*
         * Gradient by default. Stepping to the cheapest neighbour is marginally cheaper and locks the whole crowd
         * onto eight headings, which is correct for a game built on a grid and reads as robotic in one that is not.
         */
        [SerializeField] private FlowDirectionMode directionMode = FlowDirectionMode.GRADIENT;

        private readonly GridFlowField _field = new GridFlowField();

        private double _totalMilliseconds;
        private int _builds;
        private float _countdown;
        private float _sinceRebuild;
        private float3 _lastGoal;
        private bool _hasLastGoal;

        /// <summary>
        /// Whether something else is calling Rebuild, in which case the driver stops doing it on its own.
        /// </summary>
        public bool Driven { get; set; }

        /// <summary>
        /// The field being maintained.
        /// </summary>
        public GridFlowField Field => _field;

        /// <summary>
        /// What the field expands from.
        /// </summary>
        public Transform Target => target;

        /// <summary>
        /// How many cells the bake found walkable.
        /// </summary>
        public int WalkableCells => _field.WalkableCells;

        /// <summary>
        /// How many cells the grid holds in total.
        /// </summary>
        public int TotalCells => _field.IsBaked ? _field.Grid.Count : 0;

        /// <summary>
        /// Average expansion time across every rebuild so far, which is the number worth quoting.
        /// </summary>
        public double AverageBuildMilliseconds => _builds == 0 ? 0d : _totalMilliseconds / _builds;

        /// <summary>
        /// How many rebuilds have run.
        /// </summary>
        public int Builds => _builds;

        /// <summary>
        /// Who the field expands from.
        /// </summary>
        /// <param name="value">The transform to route towards.</param>
        public void SetTarget(Transform value) => target = value;

        /// <summary>
        /// How the cost field is turned into directions.
        /// </summary>
        public FlowDirectionMode DirectionMode => directionMode;

        /// <summary>
        /// Switches how directions are derived, taking effect on the next rebuild.
        /// </summary>
        /// <param name="value">The mode to use.</param>
        public void SetDirectionMode(FlowDirectionMode value) => directionMode = value;

        [ContextMenu("Bake Grid")]
        public void BakeGrid()
        {
            Bounds area = ResolveBounds();

            Stopwatch watch = Stopwatch.StartNew();
            _field.Bake(area, cellSize, sampleHeight, edgeClearance, NavMesh.AllAreas);
            watch.Stop();

            ClearStats();

            Debug.Log($"[MiHordeTraffic] Baked {_field.Grid.Width}x{_field.Grid.Height} grid at {cellSize}m over {area.size.x:F0}x{area.size.z:F0} in {watch.Elapsed.TotalMilliseconds:F1} ms, {_field.WalkableCells} of {_field.Grid.Count} cells walkable ({_field.WalkableFraction:P0}).");

            /*
             * A grid that is mostly not walkable is nearly always bounds that do not match the floor, and Unity's
             * Bounds drawer showing Extent rather than Size makes doubling them by accident easy. Every one of
             * those cells is memory and a neighbour check in every expansion, for ground that does not exist.
             */
            if (_field.WalkableFraction < .5f)
                Debug.LogWarning($"[MiHordeTraffic] Only {_field.WalkableFraction:P0} of the grid is walkable. The bounds are probably larger than the floor. Note the inspector shows Extent, which is half the size.", this);
        }

        /// <summary>
        /// Works out what area the grid should cover, preferring the navmesh's own extent.
        /// </summary>
        /// <returns>The area to bake.</returns>
        private Bounds ResolveBounds()
        {
            if (fitToNavMesh && TryFitToNavMesh(out Bounds fitted)) return fitted;

            if (surface) return new Bounds(surface.transform.TransformPoint(surface.center), surface.size);

            return bounds;
        }

        /*
         * CalculateTriangulation hands back every vertex of the baked navmesh, which is the only description of the
         * walkable world that cannot disagree with itself. It allocates and it is not cheap, so it belongs here in
         * the bake and nowhere near a frame.
         */
        private bool TryFitToNavMesh(out Bounds fitted)
        {
            fitted = default;

            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();

            if (triangulation.vertices == null || triangulation.vertices.Length == 0) return false;

            fitted = new Bounds(triangulation.vertices[0], Vector3.zero);

            for (int i = 1; i < triangulation.vertices.Length; i++)
                fitted.Encapsulate(triangulation.vertices[i]);

            /*
             * Grown outwards so the cells along an edge have their centres over navmesh rather than just past it.
             * A body walking to the very edge otherwise finds the cell it is standing in marked unwalkable.
             */
            fitted.Expand(new Vector3(fitMargin * 2f, math.max(fitted.size.y, 4f), fitMargin * 2f));

            return true;
        }

        [ContextMenu("Clear Stats")]
        public void ClearStats()
        {
            _totalMilliseconds = 0d;
            _builds = 0;
        }

        private void Awake()
        {
            ApplyRefreshRate();

            _instance = _instance ? _instance : this;
        }

        /*
         * Applied unconditionally rather than only on change, so a preset stays in charge of the fields it owns
         * for as long as it is selected. Editing one of them by hand snapping back is the inspector saying so.
         */
        private void OnValidate() => ApplyRefreshRate();

        private void ApplyRefreshRate()
        {
            switch (refreshRate)
            {
                case HordeFieldRefresh.LAZY:
                    rebuildInterval = .5f;
                    rebuildDistance = 3f;
                    minimumRebuildInterval = .2f;
                    break;

                case HordeFieldRefresh.BALANCED:
                    rebuildInterval = .25f;
                    rebuildDistance = 1f;
                    minimumRebuildInterval = .05f;
                    break;

                case HordeFieldRefresh.RESPONSIVE:
                    rebuildInterval = .15f;
                    rebuildDistance = 1f;
                    minimumRebuildInterval = .033f;
                    break;

                case HordeFieldRefresh.IMMEDIATE:
                    rebuildInterval = .05f;
                    rebuildDistance = .5f;
                    minimumRebuildInterval = 0f;
                    break;
            }
        }

        private void Start()
        {
            if (bakeOnStart) BakeGrid();
        }

        private void OnDestroy()
        {
            _field.Dispose();

            if (_instance == this) _instance = null;
        }

        private void LateUpdate()
        {
            if (!Driven) Rebuild(Time.deltaTime);
        }

        /*
         * Public so the path scheduler can own the timing when the flow field is the technique under test. A
         * rebuild that happens in the driver's own LateUpdate is real work the harness cannot see, and a technique
         * whose only cost is invisible would measure as free, which is the one result this whole comparison exists
         * to avoid.
         */
        /// <summary>
        /// Advances the rebuild countdown and expands the field if it came due.
        /// </summary>
        /// <param name="deltaTime">Time since the last call.</param>
        /// <returns>True when the field was rebuilt this call.</returns>
        public bool Rebuild(float deltaTime)
        {
            if (!target || !_field.IsBaked) return false;

            _countdown -= deltaTime;
            _sinceRebuild += deltaTime;

            float3 goal = target.position;

            /*
             * Either the ceiling has come round, or the goal has moved far enough to be worth redrawing for and
             * enough time has passed since the last expansion to afford one.
             */
            bool due = _countdown <= 0f;
            bool moved = _hasLastGoal
                && _sinceRebuild >= minimumRebuildInterval
                && math.distancesq(goal.xz, _lastGoal.xz) >= rebuildDistance * rebuildDistance;

            if (!due && !moved) return false;

            _countdown = rebuildInterval;
            _sinceRebuild = 0f;
            _lastGoal = goal;
            _hasLastGoal = true;

            long start = Stopwatch.GetTimestamp();

            if (!_field.Schedule(goal, directionMode)) return false;

            _field.Complete();

            _totalMilliseconds += (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
            _builds++;

            return true;
        }

    }
}

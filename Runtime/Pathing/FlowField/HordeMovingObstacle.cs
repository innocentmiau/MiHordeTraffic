using Unity.Mathematics;
using UnityEngine;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * Deliberately a separate component from HordeObstacle rather than a flag on it, so a wall that will never move
     * keeps costing nothing at all. A static obstacle has no Update, no countdown and no per frame work of any
     * kind: it takes its cells on the way in and gives them back on the way out. Most of the obstacles in a map are
     * that, and a hundred of them should not each be checking every frame whether they moved.
     *
     * What this costs is not the cells. Rewriting a footprint is a handful of writes; what it costs is that every
     * move makes the field out of date, and an expansion on a large grid is tens of milliseconds of worker time.
     * A cart crossing a map at sixty updates a second asks for sixty expansions a second, which is the same amount
     * of work as the whole crowd and buys nothing, because the crowd cannot react faster than the rebuild interval
     * anyway. Three or four times a second is roughly the rate the field is republished at, which is the rate above
     * which extra updates are thrown away without ever being read.
     *
     * Skipped entirely when the footprint still covers the same cells. A grid only knows about cells, so an
     * obstacle that has slid a few centimetres inside the one it already occupies has changed nothing that any
     * expansion could express, and asking for one would be a rebuild that produces the field already on screen.
     */
    /// <summary>
    /// An obstacle that moves, taking and giving back grid cells at a fixed rate rather than every frame.
    /// </summary>
    [DisallowMultipleComponent]
    public class HordeMovingObstacle : MonoBehaviour
    {

        [Tooltip("Size of the footprint in world units. Left at zero it is taken from a Collider on this object.")]
        [SerializeField] private Vector3 size = Vector3.zero;

        [Tooltip("Offset of the footprint from this object's position.")]
        [SerializeField] private Vector3 centre = Vector3.zero;

        /*
         * Solid is a wall and a gate is a door, and the difference is where a crowd waits when it cannot get past.
         * Solid ground is gone, so nothing routes at it and a blocked crowd presses against whatever else lies
         * between it and the goal. A gate still routes, at a great price, so the crowd is led to it and queues
         * there until it opens.
         */
        [Tooltip("SOLID is ground that is gone. GATE is ground that is shut, so the crowd still paths to it and waits there for it to open.")]
        [SerializeField] private HordeBlockKind kind = HordeBlockKind.SOLID;

        /*
         * A rate rather than an interval, because the number anyone reasons about is how often it happens.
         */
        [Tooltip("How many times a second the cells under this are rechecked. Above the field's own rebuild rate the extra updates are thrown away.")]
        [SerializeField, Min(.1f)] private float updatesPerSecond = 3f;

        /*
         * Off by default, which is the whole reason this component is separate from the static one. An expansion is
         * tens of milliseconds of worker time on a large grid, and a thing that moves would ask for one every time
         * it did. Left off, a moving obstacle costs a handful of cell writes and nothing else.
         *
         * On is for something big enough that walking into it and sliding along it does not get a body past. That
         * is a routing problem and only an expansion answers it.
         */
        [Tooltip("Expand the field again when this moves, so the crowd routes around it. Off, bodies still cannot walk into it, they just are not steered around it. Leave off unless it is large.")]
        [SerializeField] private bool updateRouting = false;

        private Bounds _applied;
        private HordeBlockKind _appliedKind;
        private int4 _appliedCells;
        private float _countdown;
        private bool _blocked;

        /// <summary>
        /// The area this obstacle is currently taking out of the grid, which is empty when it is not blocking.
        /// </summary>
        public Bounds Applied => _applied;

        /// <summary>
        /// Rechecks the cells under this obstacle now, whatever the countdown says.
        /// </summary>
        public void Refresh() => Apply(true);

        private void Reset()
        {
            Collider collider = GetComponent<Collider>();

            if (!collider) return;

            size = collider.bounds.size;
            centre = collider.bounds.center - transform.position;
        }

        private void OnEnable()
        {
            _countdown = 0f;

            Apply(true);
        }

        /*
         * Retried in Start for the same reason the static obstacle is: script execution order decides whether this
         * object's OnEnable runs before or after the driver's Awake.
         */
        private void Start() => Apply(false);

        private void OnDisable()
        {
            if (!_blocked) return;

            _blocked = false;

            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;

            if (!driver) return;

            driver.UnblockArea(_applied, _appliedKind, updateRouting);
        }

        private void Update()
        {
            _countdown -= Time.deltaTime;

            if (_countdown > 0f) return;

            _countdown = 1f / updatesPerSecond;

            Apply(false);
        }

        private void Apply(bool force)
        {
            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;

            if (!driver || !driver.Field.IsBaked) return;

            Bounds area = Resolve();
            int4 cells = CellsOf(driver, area);

            if (_blocked && !force && cells.Equals(_appliedCells)) return;

            if (_blocked) driver.UnblockArea(_applied, _appliedKind, updateRouting);

            _applied = area;
            _appliedKind = kind;
            _appliedCells = cells;
            _blocked = true;

            driver.BlockArea(_applied, _appliedKind, updateRouting);
        }

        /*
         * The footprint expressed in cells, which is the only thing the grid can tell apart. Two positions that
         * round to the same cell rectangle are the same obstacle as far as any expansion is concerned.
         */
        private int4 CellsOf(HordeFlowFieldDriver driver, Bounds area)
        {
            HordeGridInfo grid = driver.Field.Grid;

            int2 min = grid.CellOf(new float3(area.min.x, 0f, area.min.z));
            int2 max = grid.CellOf(new float3(area.max.x, 0f, area.max.z));

            return new int4(min.x, min.y, max.x, max.y);
        }

        private Bounds Resolve()
        {
            if (size != Vector3.zero) return new Bounds(transform.position + centre, size);

            Collider collider = GetComponent<Collider>();

            if (collider) return collider.bounds;

            return new Bounds(transform.position + centre, transform.lossyScale);
        }

        private void OnDrawGizmosSelected()
        {
            Bounds area = Application.isPlaying && _blocked ? _applied : Resolve();

            Gizmos.color = kind == HordeBlockKind.GATE ? new Color(.3f, .7f, 1f, .5f) : new Color(1f, .7f, .2f, .5f);
            Gizmos.DrawWireCube(area.center, area.size);
        }

    }
}

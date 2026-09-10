using UnityEngine;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * A structure the crowd has to walk around, expressed as ground the grid stops offering rather than as
     * something the navmesh has to be rebuilt to notice. Put one on a building prefab and the cells under it stop
     * being walkable while it is enabled, and come back when it is not.
     *
     * Enable and disable rather than a pair of calls, so a pooled building works without anything remembering to
     * undo it, and so a structure destroyed mid game cannot leave a hole in the map behind it.
     *
     * The area is taken once, on the way in, and kept. Reading it again on the way out would give back whichever
     * cells the object covers wherever it has ended up, which for anything that was moved, scaled or knocked over
     * is not the ground it took.
     *
     * The kind is kept for the same reason and was not, which cost more than the area ever could have. Blocking as
     * a gate and releasing as a solid decrements the wrong counter: the solid count floors at zero and nothing
     * happens, while the gate count is never given back and stays above zero for the rest of the session. Nothing
     * shows it. The expansion still routes through a gate, so the field looks open and draws open, and only the
     * mover refuses to step in, so what is seen is a crowd standing at a doorway it has every reason to walk
     * through. Changing the kind in the inspector on a blocking obstacle is all it takes.
     */
    /// <summary>
    /// Marks the ground under this object unwalkable for the horde grid while it is enabled.
    /// </summary>
    [DisallowMultipleComponent]
    public class HordeObstacle : MonoBehaviour
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

        private Bounds _applied;
        private HordeBlockKind _appliedKind;
        private bool _blocked;

        /// <summary>
        /// The area this obstacle is currently taking out of the grid, which is empty when it is not blocking.
        /// </summary>
        public Bounds Applied => _applied;

        private void Reset()
        {
            Collider collider = GetComponent<Collider>();

            if (!collider) return;

            size = collider.bounds.size;
            centre = collider.bounds.center - transform.position;
        }

        private void OnEnable() => Block();

        /*
         * Retried here because script execution order decides whether this object's OnEnable runs before or after
         * the driver's Awake, and an obstacle that found no driver on the way in would otherwise sit in the scene
         * looking correct and blocking nothing at all.
         */
        private void Start() => Block();

        private void Block()
        {
            if (_blocked) return;

            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;

            if (!driver) return;

            _applied = Resolve();
            _appliedKind = kind;
            _blocked = true;

            driver.BlockArea(_applied, _appliedKind, true);
        }

        private void OnDisable()
        {
            if (!_blocked) return;

            _blocked = false;

            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;

            if (!driver) return;

            driver.UnblockArea(_applied, _appliedKind, true);
        }

        /*
         * So that editing the kind, the size or the offset of a blocking obstacle during play takes effect, rather
         * than needing the object switched off and on again to be noticed. Releasing what was taken before taking
         * the new shape, in that order, because the two footprints usually overlap and blocking first would leave
         * the shared cells released by the unblock that follows.
         */
        private void OnValidate()
        {
            if (!Application.isPlaying || !_blocked) return;

            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;

            if (!driver) return;

            driver.UnblockArea(_applied, _appliedKind, true);

            _applied = Resolve();
            _appliedKind = kind;

            driver.BlockArea(_applied, _appliedKind, true);
        }

        /*
         * A collider is preferred over the serialized size because it is the thing the rest of the game already
         * agrees is where this object is. Falling back to the transform's own scale is only for something with no
         * collider at all, which is rare and is worth being able to express anyway.
         */
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

            Gizmos.color = kind == HordeBlockKind.GATE ? new Color(.3f, .7f, 1f, .5f) : new Color(1f, .4f, .2f, .5f);
            Gizmos.DrawWireCube(area.center, area.size);
        }

    }
}

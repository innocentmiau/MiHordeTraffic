using Unity.Mathematics;
using UnityEngine;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * Put on whatever the crowd is going after, to say that it has a size. Without one a target is a position and
     * the field is seeded at the single cell nearest it, so every body on the map converges on that one cell: fine
     * for chasing a player, and wrong for anything with a footprint, where it leaves a building mobbed on one face
     * and untouched on the other three.
     *
     * The size is read every rebuild rather than cached, so a target that moves, grows or is knocked over keeps
     * describing itself correctly without anything having to tell the driver about it.
     */
    /// <summary>
    /// Gives a goal a shape and a size, so bodies path to the nearest part of it instead of to a single cell.
    /// </summary>
    [DisallowMultipleComponent]
    public class HordeTarget : MonoBehaviour
    {

        [Tooltip("POINT is a bare position. BOX and SPHERE are seeded along their whole edge, so bodies surround the target instead of queueing at one side of it.")]
        [SerializeField] private HordeTargetShape shape = HordeTargetShape.BOX;

        [Tooltip("Full width and depth of the footprint in world units. Left at zero it is taken from a Collider on this object.")]
        [SerializeField] private Vector2 size = Vector2.zero;

        [Tooltip("Radius of the footprint in world units, for SPHERE. Left at zero it is taken from a Collider on this object.")]
        [SerializeField, Min(0f)] private float radius = 0f;

        [Tooltip("Offset of the footprint from this object's position.")]
        [SerializeField] private Vector3 centre = Vector3.zero;

        /// <summary>
        /// Where this target is and how much ground it covers, as the routing reads it.
        /// </summary>
        public HordeGoalArea Area
        {
            get
            {
                float3 middle = (float3)(transform.position + centre);

                if (shape == HordeTargetShape.POINT) return HordeGoalArea.Point(middle);

                if (shape == HordeTargetShape.SPHERE)
                    return new HordeGoalArea { Centre = middle, Radius = ResolveRadius(), Shape = HordeTargetShape.SPHERE };

                return new HordeGoalArea { Centre = middle, Extents = ResolveExtents(), Shape = HordeTargetShape.BOX };
            }
        }

        private void Reset()
        {
            Collider collider = GetComponent<Collider>();

            if (!collider) return;

            Bounds bounds = collider.bounds;

            size = new Vector2(bounds.size.x, bounds.size.z);
            radius = math.max(bounds.extents.x, bounds.extents.z);
            centre = bounds.center - transform.position;
        }

        /*
         * Halved because the field is set as a full width, which is what a size means everywhere else in Unity,
         * and the maths wants half extents.
         */
        private float2 ResolveExtents()
        {
            if (size != Vector2.zero) return new float2(size.x, size.y) * .5f;

            Collider collider = GetComponent<Collider>();

            if (collider) return new float2(collider.bounds.extents.x, collider.bounds.extents.z);

            return new float2(transform.lossyScale.x, transform.lossyScale.z) * .5f;
        }

        private float ResolveRadius()
        {
            if (radius > 0f) return radius;

            Collider collider = GetComponent<Collider>();

            if (collider) return math.max(collider.bounds.extents.x, collider.bounds.extents.z);

            return math.max(transform.lossyScale.x, transform.lossyScale.z) * .5f;
        }

        private void OnDrawGizmosSelected()
        {
            HordeGoalArea area = Area;

            Gizmos.color = new Color(1f, .2f, .6f, .6f);

            if (shape == HordeTargetShape.SPHERE)
            {
                Gizmos.DrawWireSphere(new Vector3(area.Centre.x, area.Centre.y, area.Centre.z), ResolveRadius());
                return;
            }

            if (shape == HordeTargetShape.POINT)
            {
                Gizmos.DrawWireSphere(new Vector3(area.Centre.x, area.Centre.y, area.Centre.z), .25f);
                return;
            }

            float2 extents = ResolveExtents();

            Gizmos.DrawWireCube(new Vector3(area.Centre.x, area.Centre.y, area.Centre.z), new Vector3(extents.x * 2f, .2f, extents.y * 2f));
        }

    }
}

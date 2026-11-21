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

        [Tooltip("Full width and depth of the footprint in world units, or width and height in a 2D scene. Left at zero it is taken from a Collider or Collider2D on this object.")]
        [SerializeField] private Vector2 size = Vector2.zero;

        [Tooltip("Radius of the footprint in world units, for SPHERE. Left at zero it is taken from a Collider or Collider2D on this object.")]
        [SerializeField, Min(0f)] private float radius = 0f;

        [Tooltip("Offset of the footprint from this object's position.")]
        [SerializeField] private Vector3 centre = Vector3.zero;

        /// <summary>
        /// Where this target is in world space and how much ground it covers along the ground axes.
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

        /*
         * Reset runs in the editor, where there may be no driver yet to say which way the ground faces, so the kind of
         * collider found decides it instead. A Collider2D only ever means a 2D scene.
         */
        private void Reset()
        {
            if (!HordeFootprint.TryColliderBounds(this, out Bounds bounds)) return;

            HordePlane plane = GetComponent<Collider2D>() ? HordePlane.For(HordeSpace.WORLD_2D) : HordePlane.Active;
            float2 ground = plane.Ground(bounds.size);

            size = new Vector2(ground.x, ground.y);
            radius = math.cmax(ground) * .5f;
            centre = bounds.center - transform.position;
        }

        /*
         * Halved because the field is set as a full width, which is what a size means everywhere else in Unity,
         * and the maths wants half extents.
         */
        private float2 ResolveExtents()
        {
            if (size != Vector2.zero) return new float2(size.x, size.y) * .5f;

            HordePlane plane = HordePlane.Active;

            if (HordeFootprint.TryColliderBounds(this, out Bounds bounds)) return plane.Ground(bounds.extents);

            return plane.Ground(transform.lossyScale) * .5f;
        }

        private float ResolveRadius()
        {
            if (radius > 0f) return radius;

            HordePlane plane = HordePlane.Active;

            if (HordeFootprint.TryColliderBounds(this, out Bounds bounds)) return math.cmax(plane.Ground(bounds.extents));

            return math.cmax(plane.Ground(transform.lossyScale)) * .5f;
        }

        private void OnDrawGizmosSelected()
        {
            HordeGoalArea area = Area;
            Vector3 middle = new Vector3(area.Centre.x, area.Centre.y, area.Centre.z);

            Gizmos.color = new Color(1f, .2f, .6f, .6f);

            if (shape == HordeTargetShape.SPHERE)
            {
                Gizmos.DrawWireSphere(middle, ResolveRadius());
                return;
            }

            if (shape == HordeTargetShape.POINT)
            {
                Gizmos.DrawWireSphere(middle, .25f);
                return;
            }

            float2 extents = ResolveExtents();

            /*
             * Drawn flat on the ground plane, built in grid space and converted, so the box lies on XY in a 2D scene.
             */
            Gizmos.DrawWireCube(middle, (Vector3)HordePlane.Active.ToWorld(new float3(extents.x * 2f, .2f, extents.y * 2f)));
        }

    }
}

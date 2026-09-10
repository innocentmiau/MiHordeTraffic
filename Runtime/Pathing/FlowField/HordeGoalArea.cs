using Unity.Mathematics;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * A goal with a size, and the arithmetic for asking where its nearest part is. Everything that used to measure
     * against a goal position measures against this instead, which is the whole of what gives a crowd the ability
     * to surround something rather than pile onto one side of it.
     *
     * Flat on the ground plane on purpose. The grid is two dimensional and heights come from the cells, so a target
     * with a height would be describing something no part of the routing could act on.
     *
     * Being inside the shape counts as being at it, at zero distance, for the same reason a body standing in the
     * goal cell has arrived. That falls out of the clamp for a box and needs one test for a circle, and it means a
     * body that has walked in through a doorway is not then told to walk back out to the rim.
     */
    /// <summary>
    /// Where a goal is and how much ground it covers, with the nearest point on it for any position.
    /// </summary>
    public struct HordeGoalArea
    {

        /// <summary>
        /// The middle of the target in world space.
        /// </summary>
        public float3 Centre;

        /// <summary>
        /// Half the width and half the depth, for a box.
        /// </summary>
        public float2 Extents;

        /// <summary>
        /// The radius, for a sphere.
        /// </summary>
        public float Radius;

        /// <summary>
        /// What form the target takes.
        /// </summary>
        public HordeTargetShape Shape;

        /// <summary>
        /// A goal with no size, which behaves exactly as a bare position always did.
        /// </summary>
        /// <param name="centre">World position of the goal.</param>
        /// <returns>A point shaped goal area.</returns>
        public static HordeGoalArea Point(float3 centre) =>
            new HordeGoalArea { Centre = centre, Shape = HordeTargetShape.POINT };

        /// <summary>
        /// How far the shape reaches from its centre along each ground axis, which is zero for a point.
        /// </summary>
        public float2 Reach => Shape switch
        {
            HordeTargetShape.BOX => math.max(Extents, 0f),
            HordeTargetShape.SPHERE => math.max(Radius, 0f),
            _ => float2.zero
        };

        /// <summary>
        /// The point on the target nearest a position, which is the position itself when it is already inside.
        /// </summary>
        /// <param name="position">World position on the ground plane.</param>
        /// <returns>The nearest point on the target, on the ground plane.</returns>
        public float2 ClosestPoint(float2 position)
        {
            if (Shape == HordeTargetShape.BOX)
                return math.clamp(position, Centre.xz - math.max(Extents, 0f), Centre.xz + math.max(Extents, 0f));

            if (Shape == HordeTargetShape.SPHERE)
            {
                float2 offset = position - Centre.xz;
                float length = math.length(offset);

                /*
                 * Already inside, or exactly on the centre where there is no direction to push out along. Both
                 * answer with the position itself, which reads as zero distance rather than as a direction nobody
                 * can normalise.
                 */
                if (length <= math.max(Radius, 0f) || length < math.EPSILON) return position;

                return Centre.xz + offset / length * math.max(Radius, 0f);
            }

            return Centre.xz;
        }

        /// <summary>
        /// How far a position is from the nearest part of the target, which is zero anywhere inside it.
        /// </summary>
        /// <param name="position">World position on the ground plane.</param>
        /// <returns>Distance on the ground plane.</returns>
        public float Distance(float2 position) => math.distance(position, ClosestPoint(position));

    }
}

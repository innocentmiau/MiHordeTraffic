using UnityEngine;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * Obstacles and targets size themselves from a collider when nobody typed a size, and in a 2D scene that
     * collider is a Collider2D. One place that knows to look for both, so the three components that do this cannot
     * drift into one of them only understanding 3D.
     */
    /// <summary>
    /// Finds the world bounds of a 3D or 2D collider on an object.
    /// </summary>
    public static class HordeFootprint
    {

        /// <summary>
        /// The world bounds of the first enabled collider on an object, 3D or 2D.
        /// </summary>
        /// <param name="owner">The object to look on.</param>
        /// <param name="bounds">The collider's world bounds when this returns true.</param>
        /// <returns>True when a collider was found.</returns>
        public static bool TryColliderBounds(Component owner, out Bounds bounds)
        {
            bounds = default;

            if (!owner) return false;

            Collider collider = owner.GetComponent<Collider>();

            if (collider)
            {
                bounds = collider.bounds;
                return true;
            }

            Collider2D collider2D = owner.GetComponent<Collider2D>();

            if (!collider2D) return false;

            bounds = collider2D.bounds;
            return true;
        }

    }
}

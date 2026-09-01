using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace MiHordeTraffic.Benchmark
{
    /*
     * Shared by the spawner, the test scene manager and the moving target, because a benchmark where the three of them
     * disagree about what counts as a valid point is a benchmark that spawns a different crowd every run.
     *
     * SamplePosition's return value is checked rather than its out hit being trusted, which is the part that is easy
     * to get wrong: a candidate off the mesh leaves the hit holding whatever the last successful call wrote,
     * so ignoring the bool silently returns stale positions.
     */
    /// <summary>
    /// Picks random points that are actually on the navmesh under a NavMeshSurface.
    /// </summary>
    public static class NavMeshSurfaceSampler
    {

        private const int SAMPLE_TRIES = 32;
        private const float SAMPLE_RADIUS = 5f;

        /// <summary>
        /// Finds a random point on the navmesh within a surface's bounds.
        /// </summary>
        /// <param name="surface">The surface to sample inside. Its size and transform give the area.</param>
        /// <param name="minimumDistanceFromCenter">Candidates closer than this to the surface centre are rejected, to keep a clearing in the middle.</param>
        /// <param name="areaScale">Fraction of the surface to sample, where 1 is the whole thing and .1 packs everything into a tenth of it.</param>
        /// <param name="position">The point found, or zero when none was.</param>
        /// <returns>True when a point on the navmesh was found.</returns>
        public static bool TrySamplePoint(NavMeshSurface surface, float minimumDistanceFromCenter, float areaScale, out Vector3 position)
        {
            position = Vector3.zero;
            if (!surface) return false;

            Vector3 center = surface.transform.TransformPoint(surface.center);
            Vector3 size = Vector3.Scale(surface.size, surface.transform.lossyScale) * Mathf.Clamp01(areaScale);

            for (int i = 0; i < SAMPLE_TRIES; i++)
            {
                Vector3 candidate = center + new Vector3(Random.Range(-size.x * .5f, size.x * .5f), 0f, Random.Range(-size.z * .5f, size.z * .5f));
                if (Vector3.Distance(candidate, center) < minimumDistanceFromCenter) continue;
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, SAMPLE_RADIUS, NavMesh.AllAreas)) continue;

                position = hit.position;
                return true;
            }

            return false;
        }

    }
}

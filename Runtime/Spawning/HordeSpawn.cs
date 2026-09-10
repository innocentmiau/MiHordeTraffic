using MiHordeTraffic.Movement;
using MiHordeTraffic.Pathing.FlowField;
using Unity.Mathematics;
using UnityEngine;

namespace MiHordeTraffic.Spawning
{
    /*
     * Where a body can actually be put down, asked of the thing that knows. Every project that spawns a crowd has
     * to answer this, and answering it outside the package means sampling the navmesh again, which is a call out to
     * native per spawn and still does not agree with the grid: the bake erodes for clearance, so ground the navmesh
     * is perfectly happy with is not always ground this system will walk on.
     *
     * A body put down on a cell the grid rejects is not merely misplaced. It starts off the described map, takes
     * the recovery path, and visibly wanders out to real ground before it sets off, which reads as the spawn being
     * broken rather than as the position being wrong.
     *
     * Reachability is part of the question and not an extra. A cell can be walkable and cut off from the goal, and
     * a body spawned on one has nowhere to go from the moment it arrives.
     *
     * Deliberately not a job. The search is a handful of cells against a few array reads, so it is a couple of
     * microseconds; a wave of a thousand is a millisecond or two, once, on the frame the wave lands. Making it
     * asynchronous would mean spawning could not answer in the call that asked, which is a large amount of
     * awkwardness for a cost that does not show up.
     */
    /// <summary>
    /// Finds somewhere a horde body can be spawned: on the grid, reachable, and not already packed.
    /// </summary>
    public static class HordeSpawn
    {

        /*
         * Kept inside the cell rather than filling it, so a body does not land with half of itself over the edge of
         * ground the bake only just accepted.
         */
        private const float SCATTER_FRACTION = .8f;

        private const float DEFAULT_MAXIMUM_FILL = .75f;

        /*
         * Turned so that asking twice for the same cell does not hand back the same point twice, which is the whole
         * of a spawn wave. Deliberately not UnityEngine.Random: a benchmark seeds that to make a run repeatable,
         * and quietly drawing from it here would make where the crowd spawns depend on how many times this was
         * called.
         */
        private static uint _calls;

        /// <summary>
        /// Finds the nearest place a body can stand near a position, rejecting cells that are already crowded.
        /// </summary>
        /// <param name="position">Where the spawn is wanted.</param>
        /// <param name="maximumDistance">How far from it is acceptable. Nothing further is considered.</param>
        /// <param name="spawn">Where to put the body, on the ground, when this returns true.</param>
        /// <returns>True when somewhere was found, false when nothing within range will do.</returns>
        public static bool TryFind(Vector3 position, float maximumDistance, out Vector3 spawn) =>
            TryFind(position, maximumDistance, DEFAULT_MAXIMUM_FILL, out spawn);

        /*
         * Fill is measured against what a cell holds at rest rather than as a count, because a count means nothing
         * without knowing how big the bodies are and how wide a cell is. Half is half full whatever the crowd is
         * made of.
         *
         * It is read from the smoothed density, which lags by design so that a body straddling a boundary does not
         * flicker between two cells. That lag is worth knowing about here: several spawns inside one frame all see
         * the density as it was before any of them landed, so they can pick the same cell. The scatter is what
         * keeps that from stacking bodies exactly on top of each other, and separation sorts out the rest.
         */
        /// <summary>
        /// Finds the nearest place a body can stand near a position, with a limit on how full a cell may be.
        /// </summary>
        /// <param name="position">Where the spawn is wanted.</param>
        /// <param name="maximumDistance">How far from it is acceptable. Nothing further is considered.</param>
        /// <param name="maximumFill">How full a cell may already be, as a fraction of what it holds at rest.</param>
        /// <param name="spawn">Where to put the body, on the ground, when this returns true.</param>
        /// <returns>True when somewhere was found, false when nothing within range will do.</returns>
        public static bool TryFind(Vector3 position, float maximumDistance, float maximumFill, out Vector3 spawn)
        {
            spawn = position;

            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;

            if (!driver || !driver.Field.IsBaked) return false;

            GridFlowField field = driver.Field;
            HordeGridInfo grid = field.Grid;

            int2 origin = grid.CellOf(position);
            int reach = (int)math.ceil(math.max(maximumDistance, 0f) / grid.CellSize);
            float allowed = maximumDistance * maximumDistance;
            float crowded = CrowdedAt(maximumFill);

            for (int radius = 0; radius <= reach; radius++)
            {
                float bestDistance = float.MaxValue;
                int best = -1;
                int2 bestCell = int2.zero;

                for (int z = -radius; z <= radius; z++)
                for (int x = -radius; x <= radius; x++)
                {
                    /*
                     * Only the edge of each ring past the first, since everything inside it was covered by the ring
                     * before and rejected.
                     */
                    if (radius > 0 && math.abs(x) != radius && math.abs(z) != radius) continue;

                    int2 cell = origin + new int2(x, z);
                    if (!grid.Contains(cell)) continue;

                    int index = grid.IndexOf(cell);

                    /*
                     * A gate routes but cannot be stood in, so a body put down on one would be inside ground it is
                     * not allowed to occupy and would have every step out of it refused.
                     */
                    if (field.Walkable[index] == 0 || field.Gated[index] > 0) continue;

                    /*
                     * Skipped before the first expansion has run, when every cell reads as unreachable and refusing
                     * on that would mean nothing could be spawned on the frame a scene loads.
                     */
                    if (field.IsBuilt && field.Integration[index] == float.MaxValue) continue;

                    if (field.Density[index] > crowded) continue;

                    float3 centre = grid.CentreOf(cell);
                    float away = math.distancesq(new float2(centre.x, centre.z), new float2(position.x, position.z));

                    if (away > allowed || away >= bestDistance) continue;

                    bestDistance = away;
                    best = index;
                    bestCell = cell;
                }

                if (best < 0) continue;

                spawn = Scatter(grid, field, bestCell, best);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The smoothed occupancy above which a cell counts as too crowded to spawn into.
        /// </summary>
        private static float CrowdedAt(float maximumFill)
        {
            float capacity = HordeFlowMovement.Instance ? HordeFlowMovement.Instance.CellCapacity : 1f;

            return math.max(capacity, .01f) * math.max(maximumFill, 0f);
        }

        /// <summary>
        /// A point inside a cell rather than its centre, so a wave does not stack on one spot.
        /// </summary>
        private static Vector3 Scatter(HordeGridInfo grid, GridFlowField field, int2 cell, int index)
        {
            uint seed = math.hash(new int3(cell.x, cell.y, (int)_calls++));

            float x = (seed & 0xFFFF) / 65535f - .5f;
            float z = ((seed >> 16) & 0xFFFF) / 65535f - .5f;
            float spread = grid.CellSize * SCATTER_FRACTION;

            float3 centre = grid.CentreOf(cell);
            float3 point = new float3(centre.x + x * spread, centre.y, centre.z + z * spread);

            /*
             * Read where the body is actually being put rather than at the cell centre, since the scatter moves it
             * up to most of a cell away and on a slope that is a real difference in height.
             */
            return new Vector3(point.x, HordeGroundHeight.At(grid, field.Walkable, field.Height, point, index), point.z);
        }

    }
}

using Unity.Mathematics;

namespace MiHordeTraffic.Separation
{
    /*
     * Deliberately tiny and free of state so Burst inlines all of it into the job bodies that call it.
     * Cells are hashed rather than indexed into a fixed grid, which means the world needs no bounds and agents that wander far from the origin cost exactly the same as agents standing on it.
     *
     * Collisions between two distant cells landing on the same hash are possible and harmless:
     * the distance test in the push job throws out anything that is not actually overlapping.
     */
    /// <summary>
    /// Turns a world position into the spatial hash cell key the separation jobs bucket agents by.
    /// </summary>
    public static class SpatialHash
    {

        /// <summary>
        /// Which cell a position falls in.
        /// </summary>
        /// <param name="position">World position, already masked down to the active plane.</param>
        /// <param name="cellSize">Width of one cell.</param>
        /// <returns>The integer cell coordinate.</returns>
        public static int3 CellOf(float3 position, float cellSize) => (int3)math.floor(position / cellSize);

        /// <summary>
        /// The hash map key for a cell coordinate.
        /// </summary>
        /// <param name="cell">The cell coordinate from CellOf.</param>
        /// <returns>The key to store agents under.</returns>
        public static int KeyOf(int3 cell) => (int)math.hash(cell);

        /// <summary>
        /// The hash map key a position falls under, in one step.
        /// </summary>
        /// <param name="position">World position, already masked down to the active plane.</param>
        /// <param name="cellSize">Width of one cell.</param>
        /// <returns>The key to store the agent under.</returns>
        public static int KeyOf(float3 position, float cellSize) => KeyOf(CellOf(position, cellSize));

    }
}

using Unity.Mathematics;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * Everything a job needs to turn a world position into a cell and back, in a blittable struct so Burst can
     * inline all of it. Deliberately flat: cells are indexed z * Width + x with no wrapper object, because the
     * expansion touches every cell in the grid and an indirection per touch is the difference between this being
     * free and it being worth thinking about.
     *
     * Two dimensions on purpose. A navmesh that stacks walkable surfaces over each other needs a layer per level
     * and this does not do that, but the case it is built for is a floor with obstacles and chokepoints in it,
     * where the height is a lookup rather than a dimension.
     */
    /// <summary>
    /// The shape of the horde grid, and the mapping between world positions and cells.
    /// </summary>
    public struct HordeGridInfo
    {

        /// <summary>
        /// World position of the minimum corner of cell zero.
        /// </summary>
        public float3 Origin;

        /// <summary>
        /// Width of one square cell.
        /// </summary>
        public float CellSize;

        /// <summary>
        /// Cell count along X.
        /// </summary>
        public int Width;

        /// <summary>
        /// Cell count along Z.
        /// </summary>
        public int Height;

        /// <summary>
        /// How many cells the grid holds.
        /// </summary>
        public int Count => Width * Height;

        /// <summary>
        /// The cell coordinate a world position falls in, which may be outside the grid.
        /// </summary>
        /// <param name="position">World position.</param>
        /// <returns>Cell coordinate.</returns>
        public int2 CellOf(float3 position) => (int2)math.floor((position.xz - Origin.xz) / CellSize);

        /// <summary>
        /// Whether a cell coordinate is inside the grid.
        /// </summary>
        /// <param name="cell">Cell coordinate.</param>
        /// <returns>True when it is in range.</returns>
        public bool Contains(int2 cell) => cell.x >= 0 && cell.y >= 0 && cell.x < Width && cell.y < Height;

        /// <summary>
        /// The flat array index of a cell coordinate, without bounds checking.
        /// </summary>
        /// <param name="cell">Cell coordinate.</param>
        /// <returns>Index into the grid arrays.</returns>
        public int IndexOf(int2 cell) => cell.y * Width + cell.x;

        /// <summary>
        /// The flat array index a world position falls in, or -1 when it is outside the grid.
        /// </summary>
        /// <param name="position">World position.</param>
        /// <returns>Index, or -1.</returns>
        public int IndexOf(float3 position)
        {
            int2 cell = CellOf(position);
            return Contains(cell) ? IndexOf(cell) : -1;
        }

        /// <summary>
        /// The cell coordinate a flat index refers to.
        /// </summary>
        /// <param name="index">Index into the grid arrays.</param>
        /// <returns>Cell coordinate.</returns>
        public int2 CellAt(int index) => new int2(index % Width, index / Width);

        /// <summary>
        /// The world position of the centre of a cell, on the grid plane.
        /// </summary>
        /// <param name="cell">Cell coordinate.</param>
        /// <returns>World position, with Y taken from the origin.</returns>
        public float3 CentreOf(int2 cell) => new float3(Origin.x + (cell.x + .5f) * CellSize, Origin.y, Origin.z + (cell.y + .5f) * CellSize);

    }
}

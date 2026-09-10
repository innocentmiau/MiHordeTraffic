using Unity.Collections;
using Unity.Mathematics;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * The height of the ground at a position rather than the height of the cell it is standing in, which on any
     * slope are different by up to half a cell of climb.
     *
     * The bake samples the navmesh once per cell, at the centre, so what it stores is the height of a slope halfway
     * across each cell and nothing about the rest of it. Placing a body at that one number makes the ground a
     * staircase: level within a cell, a step at every boundary. On flat ground nothing shows. On a slope a body
     * pops up as it crosses each line, and if separation is jostling it across that line it pops back and forth
     * every frame, which reads as the body twitching rather than as the ground being described coarsely.
     *
     * Read across the four cells whose centres surround the position and weighted by how near it is to each, so the
     * surface between the samples is continuous. Crossing a boundary changes which four cells are read and does not
     * change the answer, because at the moment of crossing the two that leave carry no weight.
     *
     * A neighbour with no reading of its own stands in with the cell underfoot, so ground at the edge of the map
     * flattens out rather than being interpolated towards a zero nothing measured.
     */
    /// <summary>
    /// Ground height at a world position, interpolated across the cells around it rather than taken from one.
    /// </summary>
    public static class HordeGroundHeight
    {

        /// <summary>
        /// The height of the ground under a position.
        /// </summary>
        /// <param name="grid">The grid the position falls in.</param>
        /// <param name="walkable">Per cell walkability, since only walkable cells were ever measured.</param>
        /// <param name="height">Per cell navmesh height.</param>
        /// <param name="position">Where to read, on the grid plane.</param>
        /// <param name="cell">Flat index of the cell underfoot, which stands in for neighbours that have no reading.</param>
        /// <returns>The interpolated height.</returns>
        public static float At(HordeGridInfo grid, NativeArray<byte> walkable, NativeArray<float> height, float3 position, int cell)
        {
            if (cell < 0) return position.y;

            float fallback = height[cell];

            /*
             * Shifted by half a cell so the samples are the cell centres, which is where the readings actually are.
             * Without it the weights are taken against cell corners and the surface is offset by half a cell in
             * both directions, which on a slope is a constant error rather than a visible one.
             */
            float2 local = (position.xz - grid.Origin.xz) / grid.CellSize - .5f;
            int2 corner = (int2)math.floor(local);
            float2 blend = local - corner;

            float h00 = Read(grid, walkable, height, corner, fallback);
            float h10 = Read(grid, walkable, height, corner + new int2(1, 0), fallback);
            float h01 = Read(grid, walkable, height, corner + new int2(0, 1), fallback);
            float h11 = Read(grid, walkable, height, corner + new int2(1, 1), fallback);

            return math.lerp(math.lerp(h00, h10, blend.x), math.lerp(h01, h11, blend.x), blend.y);
        }

        private static float Read(HordeGridInfo grid, NativeArray<byte> walkable, NativeArray<float> height, int2 cell, float fallback)
        {
            if (!grid.Contains(cell)) return fallback;

            int index = grid.IndexOf(cell);

            return walkable[index] == 0 ? fallback : height[index];
        }

    }
}

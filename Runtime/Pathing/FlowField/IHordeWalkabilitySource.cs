using Unity.Mathematics;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * The one question the bake asks the engine, split out so it can be asked of something other than a navmesh.
     * Everything after it, the erosion, the blocking, the expansion and the movement, only ever reads the answers,
     * so a 2D scene with no navmesh at all needs a different source and nothing else.
     *
     * Asked once per cell on the main thread while baking and never again, which is why it is allowed to call out to
     * the engine at all.
     */
    /// <summary>
    /// Decides, one cell at a time, which ground the horde grid treats as walkable.
    /// </summary>
    public interface IHordeWalkabilitySource
    {

        /// <summary>
        /// Called once before any cell is sampled, with the grid that is about to be baked.
        /// </summary>
        /// <param name="grid">The grid the samples will fill.</param>
        void Begin(HordeGridInfo grid);

        /// <summary>
        /// Whether the cell centred on a point is walkable, and the ground height there if it is.
        /// </summary>
        /// <param name="centre">The cell centre in grid space, with the ground on XZ.</param>
        /// <param name="height">The grid space height of the ground when this returns true.</param>
        /// <returns>True when a body can stand in this cell.</returns>
        bool Sample(float3 centre, out float height);

    }
}

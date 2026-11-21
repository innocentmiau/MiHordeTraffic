using Unity.Mathematics;
using UnityEngine.AI;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * The bake as it always was, moved out of GridFlowField so a 2D scene can swap it for something else.
     * NavMesh.SamplePosition is a call out to native per cell, which is why it happens here once and never per rebuild.
     *
     * Carries a plane even though a navmesh is usually baked on XZ, because NavMeshPlus bakes an ordinary Unity navmesh
     * lying on XY. Converting the cell centre to the world before asking, and the hit back after, is all that takes.
     */
    /// <summary>
    /// Walkability from the baked Unity navmesh, one SamplePosition per cell.
    /// </summary>
    public class NavMeshWalkability : IHordeWalkabilitySource
    {

        /*
         * A centimetre. Below this a hit is on the cell centre as far as anything here is concerned, and asking for
         * less would reject cells over an exact difference that means nothing on a navmesh.
         */
        private const float EXACT_SAMPLE_TOLERANCE = .01f;

        private readonly HordePlane _plane;
        private readonly float _sampleHeight;
        private readonly int _areaMask;
        private readonly float _sampleAccuracy;

        private float _tolerance;

        /// <summary>
        /// A navmesh source for one bake.
        /// </summary>
        /// <param name="plane">The plane the navmesh lies on.</param>
        /// <param name="sampleHeight">How far above and below a cell centre to look for the navmesh.</param>
        /// <param name="areaMask">Navmesh areas to accept.</param>
        /// <param name="sampleAccuracy">How closely a cell has to sit on the navmesh. One accepts only cells whose centre is on it.</param>
        public NavMeshWalkability(HordePlane plane, float sampleHeight, int areaMask, float sampleAccuracy)
        {
            _plane = plane;
            _sampleHeight = sampleHeight;
            _areaMask = areaMask;
            _sampleAccuracy = sampleAccuracy;
        }

        /*
         * Interpolated so that zero is the half cell slack this used to have unconditionally, and one is as
         * close to on the navmesh as floating point allows. The floor is a centimetre rather than nothing,
         * because a hit on a polygon is projected onto it and does not come back bit identical.
         */
        /// <summary>
        /// Works out how far a hit may land from the cell centre for this grid.
        /// </summary>
        /// <param name="grid">The grid about to be baked.</param>
        public void Begin(HordeGridInfo grid) => _tolerance = math.lerp(grid.CellSize * .5f, EXACT_SAMPLE_TOLERANCE, math.saturate(_sampleAccuracy));

        /// <summary>
        /// Whether the navmesh lies under this cell centre.
        /// </summary>
        /// <param name="centre">The cell centre in grid space.</param>
        /// <param name="height">Grid space height of the navmesh there.</param>
        /// <returns>True when the centre is on the navmesh.</returns>
        public bool Sample(float3 centre, out float height)
        {
            height = 0f;

            if (!NavMesh.SamplePosition(_plane.ToWorld(centre), out NavMeshHit hit, _sampleHeight, _areaMask)) return false;

            float3 found = _plane.ToGrid(hit.position);

            /*
             * The question is whether this cell is on the navmesh, not whether the navmesh is near it, and
             * those are different by exactly the amount that puts bodies inside walls.
             *
             * SamplePosition hands back the nearest point on a surface however far away it is, so a cell
             * centre buried in a wall finds the navmesh at the wall's foot and reports a hit. Measured
             * against half a cell that hit was accepted, the cell was marked walkable, and its height came
             * from the edge it found: a strip of invented ground around every obstacle in the level, half a
             * cell wide, that bodies stand on with the wall through them.
             *
             * Asking for the hit to land on the cell centre is the honest test, and a hit for a centre
             * genuinely inside a navmesh polygon comes back at that centre, so the tolerance only has to
             * cover floating point rather than any real distance.
             *
             * It stays adjustable because tightening it shrinks the walkable area at every edge by up to
             * the part of a cell that hangs off the navmesh, and a corridor already only a cell or two wide
             * can lose its middle. The answer to that is smaller cells rather than a looser test, since a
             * looser test does not recover a corridor that is genuinely too narrow, it invents one.
             */
            if (math.distancesq(found.xz, centre.xz) > _tolerance * _tolerance) return false;

            height = found.y;
            return true;
        }

    }
}

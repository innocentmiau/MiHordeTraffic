using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * Walkability for a top down 2D scene, read from the colliders the game already has rather than from a navmesh it
     * would have to bake. Walls in a 2D game are almost always 2D colliders already, because the player has to bump
     * into them, so a wall the player cannot walk through is a wall the crowd cannot either with nothing to set up.
     *
     * Tilemaps need nothing special for that half. A TilemapCollider2D, with or without a CompositeCollider2D on top,
     * is a Collider2D like any other, so a wall tilemap on a blocking layer is found by the same overlap test.
     *
     * Floor tilemaps are the other half and are optional. Without them the whole grid area is ground unless a wall says
     * otherwise, which is right for an arena with walls round it. With them a cell is only ground where some floor
     * tilemap has a tile, which is what makes holes, pits and the outside of an irregular map come out right without
     * having to wall every edge.
     *
     * Overlaps a box rather than testing the centre point, so a wall that only clips a corner of a cell still takes it.
     * How much of the cell the box covers is the same accuracy setting the navmesh bake uses: at one the whole cell has
     * to be clear, and lower forgives walls that only graze the edge, at the price of bodies brushing them.
     */
    /// <summary>
    /// Walkability from 2D colliders on blocking layers, optionally limited to cells with a floor tile.
    /// </summary>
    public class Physics2DWalkability : IHordeWalkabilitySource
    {

        /*
         * Never the whole cell, even at full accuracy. A wall tile that ends exactly on a cell boundary touches the
         * box of the cell beside it, and an overlap test counts touching, so a box the full width of the cell would
         * take a row of open ground off every wall in the level.
         */
        private const float FULL_PROBE = .96f;
        private const float SMALLEST_PROBE = .1f;

        private static readonly List<Collider2D> HITS = new List<Collider2D>();

        private readonly HordePlane _plane;
        private readonly LayerMask _blockingLayers;
        private readonly Tilemap[] _floors;
        private readonly float _sampleAccuracy;

        private ContactFilter2D _filter;
        private Vector2 _probe;

        /// <summary>
        /// A 2D physics source for one bake.
        /// </summary>
        /// <param name="plane">The plane the scene is drawn on, which is XY for anything 2D.</param>
        /// <param name="blockingLayers">Layers whose colliders are walls.</param>
        /// <param name="floors">Tilemaps that say where ground exists, or empty to treat the whole area as ground.</param>
        /// <param name="sampleAccuracy">How much of a cell has to be clear of walls. One is the whole cell.</param>
        public Physics2DWalkability(HordePlane plane, LayerMask blockingLayers, Tilemap[] floors, float sampleAccuracy)
        {
            _plane = plane;
            _blockingLayers = blockingLayers;
            _floors = floors ?? new Tilemap[0];
            _sampleAccuracy = sampleAccuracy;
        }

        /*
         * Triggers are left out on purpose. A trigger in a 2D game is a pickup, a damage zone or a door sensor, all
         * of which are things a body walks into, and treating them as walls would carve holes wherever one was placed.
         *
         * Transforms are synced first because the bake usually runs in Start, before the physics step that would
         * otherwise have caught up with anything moved in Awake, and a wall the bake cannot see is a wall the crowd
         * walks through for the rest of the session.
         */
        /// <summary>
        /// Prepares the overlap filter and probe size for this grid.
        /// </summary>
        /// <param name="grid">The grid about to be baked.</param>
        public void Begin(HordeGridInfo grid)
        {
            Physics2D.SyncTransforms();

            _filter = new ContactFilter2D { useTriggers = false };
            _filter.SetLayerMask(_blockingLayers);

            float side = grid.CellSize * math.lerp(SMALLEST_PROBE, FULL_PROBE, math.saturate(_sampleAccuracy));
            _probe = new Vector2(side, side);
        }

        /// <summary>
        /// Whether this cell has floor under it and no wall in it.
        /// </summary>
        /// <param name="centre">The cell centre in grid space.</param>
        /// <param name="height">Grid space height, which is the depth of the grid itself since a 2D floor is flat.</param>
        /// <returns>True when a body can stand in this cell.</returns>
        public bool Sample(float3 centre, out float height)
        {
            height = centre.y;

            float3 world = _plane.ToWorld(centre);

            if (!HasFloor(world)) return false;

            return Physics2D.OverlapBox(new Vector2(world.x, world.y), _probe, 0f, _filter, HITS) == 0;
        }

        private bool HasFloor(float3 world)
        {
            if (_floors.Length == 0) return true;

            for (int i = 0; i < _floors.Length; i++)
            {
                Tilemap floor = _floors[i];

                if (floor && floor.HasTile(floor.WorldToCell(world))) return true;
            }

            return false;
        }

    }
}

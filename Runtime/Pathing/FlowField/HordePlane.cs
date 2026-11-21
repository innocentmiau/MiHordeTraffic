using Unity.Mathematics;
using UnityEngine;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * The whole of 2D support at the level of the maths, and the reason it is small. Everything inside the package
     * works in grid space, where the ground is XZ and Y is height, which is what it always worked in. A 2D scene is
     * converted on the way in and on the way back out, and nothing in between has to know which one it is running.
     *
     * The conversion for 2D swaps Y and Z. World X stays grid X, world Y becomes the grid's second ground axis, and
     * world Z, the depth a 2D scene sorts on, becomes grid height. Swapping two axes is its own inverse, so one swizzle
     * goes in both directions and there is no way for the two halves to disagree.
     *
     * The alternative was teaching every job which axes to read, and the move job alone reads the ground axes in a
     * dozen places. Each of those would have been a branch in the hottest code in the package and a place to forget.
     *
     * Blittable so a job can carry one, and the default is 3D, so anything that never heard of this behaves as before.
     */
    /// <summary>
    /// Converts between world space and the grid's own space, where the ground is always XZ.
    /// </summary>
    public struct HordePlane
    {

        /// <summary>
        /// True for a top down 2D scene on the XY plane, false for a 3D floor on XZ.
        /// </summary>
        public bool Is2D;

        /*
         * Read from the scene's driver rather than passed around, because the things that need it are components a
         * designer drops onto prefabs, and none of them can be handed a reference to a driver that does not exist yet.
         */
        /// <summary>
        /// The plane the loaded scene's driver works on, or 3D when there is no driver.
        /// </summary>
        public static HordePlane Active => HordeFlowFieldDriver.Instance ? HordeFlowFieldDriver.Instance.Plane : default;

        /// <summary>
        /// The plane for a space setting.
        /// </summary>
        /// <param name="space">Which way the ground faces.</param>
        /// <returns>The matching plane.</returns>
        public static HordePlane For(HordeSpace space) => new HordePlane { Is2D = space == HordeSpace.WORLD_2D };

        /// <summary>
        /// A world position or direction in grid space.
        /// </summary>
        /// <param name="world">Position or direction in world space.</param>
        /// <returns>The same thing with the ground on XZ.</returns>
        public float3 ToGrid(float3 world) => Is2D ? world.xzy : world;

        /// <summary>
        /// A grid space position or direction back in world space.
        /// </summary>
        /// <param name="grid">Position or direction in grid space.</param>
        /// <returns>The same thing in world space.</returns>
        public float3 ToWorld(float3 grid) => Is2D ? grid.xzy : grid;

        /// <summary>
        /// A world space box in grid space.
        /// </summary>
        /// <param name="world">Box in world space.</param>
        /// <returns>The same box with the ground on XZ.</returns>
        public Bounds ToGrid(Bounds world) => Is2D ? new Bounds((Vector3)ToGrid(world.center), (Vector3)ToGrid(world.size)) : world;

        /// <summary>
        /// The two ground axes of a world position or size, which are XZ in 3D and XY in 2D.
        /// </summary>
        /// <param name="world">Position or size in world space.</param>
        /// <returns>The part of it that lies along the ground.</returns>
        public float2 Ground(float3 world) => Is2D ? world.xy : world.xz;

        /*
         * Drawing in grid space and letting the matrix put it in the world means gizmo code written for XZ is correct in
         * both, which is the same trade the rest of the package makes. The matrix is a reflection in 2D, and Unity draws
         * gizmos through one without complaint.
         */
        /// <summary>
        /// A gizmo matrix that draws grid space coordinates where they are in the world.
        /// </summary>
        public Matrix4x4 GizmoMatrix => Is2D
            ? new Matrix4x4(new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f))
            : Matrix4x4.identity;

    }
}

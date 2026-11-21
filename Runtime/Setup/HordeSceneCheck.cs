using System.Collections.Generic;
using MiHordeTraffic.Movement;
using MiHordeTraffic.Pathing;
using MiHordeTraffic.Pathing.FlowField;
using MiHordeTraffic.Separation;
using UnityEngine;
using UnityEngine.AI;

namespace MiHordeTraffic.Setup
{
    /*
     * One description of what a working scene looks like, used by both the menu that builds one and the check that
     * runs at play. Written twice they drift, and the failure that produces is the worst kind: the setup tool says
     * the scene is fine and the crowd does nothing, or the other way round, and neither of them is lying about the
     * rule it happens to know.
     *
     * Every failure this catches has already cost an afternoon at least once. A mover with no driver assigned, a
     * driver with no target, a grid baked over ground that has no navmesh under it, agents registering for
     * separation into a scene with no system to separate them. All of them look identical from the outside: the
     * crowd stands still and nothing is logged.
     */
    /// <summary>
    /// Checks a scene for everything a working horde needs, and says what to do about whatever is missing.
    /// </summary>
    public static class HordeSceneCheck
    {

        private static readonly List<HordeSceneIssue> ISSUES = new List<HordeSceneIssue>();

        /// <summary>
        /// Everything wrong with the loaded scene's horde setup, worst first.
        /// </summary>
        /// <param name="into">List filled with the result, cleared first.</param>
        public static void Inspect(List<HordeSceneIssue> into)
        {
            into.Clear();

            SeparationSystem separation = Object.FindFirstObjectByType<SeparationSystem>();
            HordeFlowFieldDriver driver = Object.FindFirstObjectByType<HordeFlowFieldDriver>();
            HordeFlowMovement movement = Object.FindFirstObjectByType<HordeFlowMovement>();
            HordePathScheduler scheduler = Object.FindFirstObjectByType<HordePathScheduler>();

            if (!separation)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.BLOCKING,
                    "No SeparationSystem in the scene, so nothing pushes bodies apart.",
                    "Run Tools > MiHordeTraffic > Set Up Scene, or add a SeparationSystem to a manager object."));

            if (!driver)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.BLOCKING,
                    "No HordeFlowFieldDriver, so there is no field for anything to follow.",
                    "Run Tools > MiHordeTraffic > Set Up Scene."));

            if (!movement)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.BLOCKING,
                    "No HordeFlowMovement, so nothing moves bodies along the field.",
                    "Run Tools > MiHordeTraffic > Set Up Scene."));

            if (!scheduler)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.WARNING,
                    "No HordePathScheduler, so one is created at runtime with default pacing.",
                    "Add one to the manager object if you want its budget and intervals under your control."));

            if (movement && !movement.Driver)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.BLOCKING,
                    "HordeFlowMovement has no driver assigned, so it will never move anything.",
                    "Assign the HordeFlowFieldDriver to the mover's Driver field."));

            if (driver && !driver.Target)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.BLOCKING,
                    "HordeFlowFieldDriver has no target, so the field has nothing to expand from.",
                    "Assign whatever the crowd should walk towards to the driver's Target field."));

            if (scheduler && scheduler.Technique == HordePathTechnique.NAVMESH_AGENT && driver && !driver.WalksNavMesh)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.WARNING,
                    "The scheduler is set to NAVMESH_AGENT in a 2D scene on PHYSICS_2D, where there is no navmesh for agents to use. It will run FLOW_FIELD instead.",
                    "Set the scheduler's Technique to FLOW_FIELD, or set the driver's Ground 2D to NAVMESH if the scene has a NavMeshPlus navmesh."));
            else if (scheduler && scheduler.Technique == HordePathTechnique.NAVMESH_AGENT)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.ADVICE,
                    "The scheduler is set to NAVMESH_AGENT, which hands every body back to its own NavMeshAgent.",
                    "Set Technique to FLOW_FIELD unless you are deliberately measuring the baseline."));

            /*
             * Checked last because it is the one that needs the scene to have been baked rather than merely
             * populated, and a scene that is missing components is not yet worth telling about its navmesh.
             */
            if (driver && !driver.WalksNavMesh)
                Inspect2D(driver, into);
            else if (driver && driver.Space == HordeSpace.WORLD_2D)
                InspectNavMeshPlus(into);
            else if (NavMesh.CalculateTriangulation().vertices.Length == 0)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.BLOCKING,
                    "There is no navmesh in the scene, so the grid bake will find no walkable ground at all.",
                    "Bake a NavMeshSurface before pressing play, or set the driver's Space to WORLD_2D for a 2D scene."));

            into.Sort((a, b) => a.Severity.CompareTo(b.Severity));
        }

        /*
         * The 2D bake has no navmesh to be missing, so what goes wrong instead is the bake being told nothing about where
         * the walls are, or the crowd itself standing on a layer the bake treats as wall.
         */
        private static void Inspect2D(HordeFlowFieldDriver driver, List<HordeSceneIssue> into)
        {
            if (driver.BlockingLayers.value == 0 && !driver.HasFloorTilemaps)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.WARNING,
                    "The 2D driver has no Blocking Layers and no Floor Tilemaps, so the whole grid area is open ground with no walls.",
                    "Set Blocking Layers to the layers your wall colliders are on, or assign Floor Tilemaps."));

            HordeAgent[] bodies = Object.FindObjectsByType<HordeAgent>(FindObjectsSortMode.None);

            for (int i = 0; i < bodies.Length; i++)
            {
                if ((driver.BlockingLayers.value & (1 << bodies[i].gameObject.layer)) == 0 || !bodies[i].GetComponent<Collider2D>()) continue;

                into.Add(new HordeSceneIssue(HordeIssueSeverity.WARNING,
                    $"Horde bodies such as '{bodies[i].name}' have a Collider2D on one of the driver's Blocking Layers, so bodies present at bake time carve holes in the grid.",
                    "Put the bodies on a layer that is not in the driver's Blocking Layers."));
                break;
            }
        }

        /*
         * A 2D scene that reads a navmesh can be missing it the same way a 3D one can, and has one more way of getting it
         * wrong: a NavMeshPlus surface that was never rotated bakes on XZ, flat under a camera that looks along Z. The
         * bake then finds every cell of an XY grid off the navmesh, and the crowd stands still with no error.
         *
         * Told apart by which way the navmesh is thin. A level baked on XY has no depth in Z, one baked on XZ has none in Y.
         */
        private static void InspectNavMeshPlus(List<HordeSceneIssue> into)
        {
            Vector3[] vertices = NavMesh.CalculateTriangulation().vertices;

            if (vertices.Length == 0)
            {
                into.Add(new HordeSceneIssue(HordeIssueSeverity.BLOCKING,
                    "The 2D driver's Ground 2D is NAVMESH but there is no navmesh in the scene, so the grid bake will find no walkable ground at all.",
                    "Bake the NavMeshPlus NavMeshSurface before pressing play, or set Ground 2D to PHYSICS_2D to bake from colliders instead."));
                return;
            }

            Bounds extent = new Bounds(vertices[0], Vector3.zero);

            for (int i = 1; i < vertices.Length; i++)
                extent.Encapsulate(vertices[i]);

            if (extent.size.z <= extent.size.y) return;

            into.Add(new HordeSceneIssue(HordeIssueSeverity.BLOCKING,
                "The navmesh lies on XZ, but a 2D driver reads it on XY, so the grid bake will find no walkable ground on it.",
                "Press Rotate Surface to XY on the NavMeshPlus NavMeshSurface (its object should be rotated -90, 0, 0) and bake it again."));
        }

        /*
         * Runs after the first scene's Awakes, which is the earliest point at which a missing component is a real
         * absence rather than a script execution order accident.
         */
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void WarnOnPlay()
        {
            Inspect(ISSUES);

            for (int i = 0; i < ISSUES.Count; i++)
            {
                HordeSceneIssue issue = ISSUES[i];

                if (issue.Severity == HordeIssueSeverity.ADVICE) continue;

                if (issue.Severity == HordeIssueSeverity.BLOCKING) Debug.LogError($"[MiHordeTraffic] {issue}");
                else Debug.LogWarning($"[MiHordeTraffic] {issue}");
            }

            ISSUES.Clear();
        }

    }
}

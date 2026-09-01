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

            if (scheduler && scheduler.Technique == HordePathTechnique.NAVMESH_AGENT)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.ADVICE,
                    "The scheduler is set to NAVMESH_AGENT, which hands every body back to its own NavMeshAgent.",
                    "Set Technique to FLOW_FIELD unless you are deliberately measuring the baseline."));

            /*
             * Checked last because it is the one that needs the scene to have been baked rather than merely
             * populated, and a scene that is missing components is not yet worth telling about its navmesh.
             */
            if (NavMesh.CalculateTriangulation().vertices.Length == 0)
                into.Add(new HordeSceneIssue(HordeIssueSeverity.BLOCKING,
                    "There is no navmesh in the scene, so the grid bake will find no walkable ground at all.",
                    "Bake a NavMeshSurface before pressing play."));

            into.Sort((a, b) => a.Severity.CompareTo(b.Severity));
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

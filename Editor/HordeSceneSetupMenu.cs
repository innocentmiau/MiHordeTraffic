using System.Collections.Generic;
using MiHordeTraffic.Diagnostics;
using MiHordeTraffic.Movement;
using MiHordeTraffic.Pathing;
using MiHordeTraffic.Pathing.FlowField;
using MiHordeTraffic.Separation;
using MiHordeTraffic.Setup;
using MiHordeTraffic.Tuning;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MiHordeTraffic.EditorTools
{
    /*
     * Setting a scene up by hand is four components that have to exist, on an object that does not have to be any
     * particular one, with two references between them that nothing complains about being empty. Every one of those
     * failures looks the same from the outside, which is a crowd that stands still, so the cost of getting it wrong
     * is an afternoon and the cost of automating it is this file.
     *
     * Everything is added to whichever object already holds the SeparationSystem rather than to a new object of its
     * own, because these components find each other through static instances and putting them together is the only
     * arrangement where the inspector shows the whole system in one place.
     *
     * Registered with Undo throughout. A setup tool that cannot be undone is one people are afraid to run on a
     * scene they care about, which means they run it on a new scene and copy things across by hand instead.
     */
    /// <summary>
    /// Menu commands that build and check the components a horde scene needs.
    /// </summary>
    public static class HordeSceneSetupMenu
    {

        private const string ROOT_NAME = "MiHordeTraffic";

        private static readonly List<HordeSceneIssue> ISSUES = new List<HordeSceneIssue>();

        [MenuItem("Tools/MiHordeTraffic/Set Up Scene")]
        private static void SetUpScene() => SetUp(HordeSpace.WORLD_3D, HordeGround2D.PHYSICS_2D);

        /*
         * The same components as a 3D scene, with the driver switched to 2D. Separation needs nothing, because it reads
         * the driver and turns its default XZ into XY on its own, and the mover keeps sprites unrotated by default.
         */
        [MenuItem("Tools/MiHordeTraffic/Set Up 2D Scene")]
        private static void SetUp2DScene() => SetUp(HordeSpace.WORLD_2D, HordeGround2D.PHYSICS_2D);

        /*
         * The NavMeshPlus scene is the same 2D scene reading a different ground. The navmesh itself is not made here,
         * because a project arriving with NavMeshPlus already has its surface set up the way its agents need it.
         */
        [MenuItem("Tools/MiHordeTraffic/Set Up 2D Scene (NavMeshPlus)")]
        private static void SetUp2DNavMeshScene() => SetUp(HordeSpace.WORLD_2D, HordeGround2D.NAVMESH);

        private static void SetUp(HordeSpace space, HordeGround2D ground2D)
        {
            SeparationSystem separation = Object.FindFirstObjectByType<SeparationSystem>();
            GameObject host = separation ? separation.gameObject : CreateHost();

            Undo.RegisterFullObjectHierarchyUndo(host, "Set Up MiHordeTraffic Scene");

            if (!separation) Undo.AddComponent<SeparationSystem>(host);

            HordeFlowFieldDriver driver = Ensure<HordeFlowFieldDriver>(host);

            HordePathScheduler scheduler = Ensure<HordePathScheduler>(host);
            Ensure<HordeFlowAdvisor>(host);

            HordeFlowMovement movement = Ensure<HordeFlowMovement>(host);
            HordeFlowFieldGizmos gizmos = Ensure<HordeFlowFieldGizmos>(host);

            Wire(movement, "driver", driver);
            Wire(gizmos, "driver", driver);
            SetSpace(driver, space);
            SetEnum(driver, "ground2D", (int)ground2D);

            /*
             * The scheduler's own default is the NavMeshAgent baseline, which a 2D scene cannot run, so the 2D setup
             * says flow field outright rather than leaving the scheduler to fall back and warn every play.
             */
            if (space == HordeSpace.WORLD_2D) SetEnum(scheduler, "technique", (int)HordePathTechnique.FLOW_FIELD);

            Selection.activeGameObject = host;

            EditorSceneManager.MarkSceneDirty(host.scene);

            Debug.Log($"[MiHordeTraffic] {(space == HordeSpace.WORLD_2D ? "2D" : "3D")} scene set up on '{host.name}'.", host);

            CheckScene();
        }

        /*
         * Separate from the setup so it can be run on a scene that was built by hand, and so the setup can finish
         * by telling you what it could not do for you. Assigning a target is the obvious one: no tool can guess
         * what the crowd is supposed to be walking towards.
         */
        [MenuItem("Tools/MiHordeTraffic/Check Scene")]
        private static void CheckScene()
        {
            HordeSceneCheck.Inspect(ISSUES);

            if (ISSUES.Count == 0)
            {
                Debug.Log("[MiHordeTraffic] scene check found nothing wrong.");
                return;
            }

            for (int i = 0; i < ISSUES.Count; i++)
            {
                HordeSceneIssue issue = ISSUES[i];

                if (issue.Severity == HordeIssueSeverity.BLOCKING) Debug.LogError($"[MiHordeTraffic] {issue}");
                else if (issue.Severity == HordeIssueSeverity.WARNING) Debug.LogWarning($"[MiHordeTraffic] {issue}");
                else Debug.Log($"[MiHordeTraffic] {issue}");
            }
        }

        private static GameObject CreateHost()
        {
            GameObject host = new GameObject(ROOT_NAME);

            Undo.RegisterCreatedObjectUndo(host, "Create MiHordeTraffic Object");

            return host;
        }

        private static T Ensure<T>(GameObject host) where T : Component
        {
            T existing = host.GetComponent<T>();

            return existing ? existing : Undo.AddComponent<T>(host);
        }

        private static void SetSpace(HordeFlowFieldDriver driver, HordeSpace space) => SetEnum(driver, "space", (int)space);

        private static void SetEnum(Component target, string field, int value)
        {
            if (!target) return;

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(field);

            if (property == null) return;

            property.enumValueIndex = value;
            serialized.ApplyModifiedProperties();
        }

        /*
         * Written through SerializedObject rather than a setter, so the reference lands in the serialized data the
         * same way a drag in the inspector would. Adding a public setter purely so a tool can reach a private field
         * widens the runtime surface of the component for the benefit of the editor, which is backwards.
         */
        private static void Wire(Component target, string field, Object value)
        {
            if (!target) return;

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(field);

            if (property == null || property.objectReferenceValue) return;

            property.objectReferenceValue = value;
            serialized.ApplyModifiedProperties();
        }

    }
}

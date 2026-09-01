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
        private static void SetUpScene()
        {
            SeparationSystem separation = Object.FindFirstObjectByType<SeparationSystem>();
            GameObject host = separation ? separation.gameObject : CreateHost();

            Undo.RegisterFullObjectHierarchyUndo(host, "Set Up MiHordeTraffic Scene");

            if (!separation) Undo.AddComponent<SeparationSystem>(host);

            HordeFlowFieldDriver driver = Ensure<HordeFlowFieldDriver>(host);

            Ensure<HordePathScheduler>(host);
            Ensure<HordeFlowAdvisor>(host);

            HordeFlowMovement movement = Ensure<HordeFlowMovement>(host);
            HordeFlowFieldGizmos gizmos = Ensure<HordeFlowFieldGizmos>(host);

            Wire(movement, "driver", driver);
            Wire(gizmos, "driver", driver);

            Selection.activeGameObject = host;

            EditorSceneManager.MarkSceneDirty(host.scene);

            Debug.Log($"[MiHordeTraffic] scene set up on '{host.name}'.", host);

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

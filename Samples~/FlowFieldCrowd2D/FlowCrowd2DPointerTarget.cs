using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MiHordeTraffic.Samples.FlowFieldCrowd2D
{
    /*
     * Not part of a crowd setup, only part of testing one. The interesting behaviour of a flow field is what it does
     * when the goal moves: the whole crowd turns at once, splits round walls and finds the other door when one is
     * jammed. Dragging the target around with the mouse shows all of that in a few seconds, where a fixed target
     * only ever shows the crowd arriving.
     *
     * Reads whichever input backend the project has enabled, so the sample compiles in a project on the Input System
     * package, on the old Input Manager, or on both.
     */
    /// <summary>
    /// Moves this object to wherever the mouse is held down in a 2D scene, so the crowd can be steered while testing.
    /// </summary>
    public class FlowCrowd2DPointerTarget : MonoBehaviour
    {

        [Tooltip("Camera the scene is viewed through. Left empty it uses the main camera.")]
        [SerializeField] private Camera viewCamera;

        [Tooltip("Follow the pointer while the button is held rather than only jumping on the click.")]
        [SerializeField] private bool followWhileHeld = true;

        private void Awake() => viewCamera = viewCamera ? viewCamera : Camera.main;

        private void Update()
        {
            if (!viewCamera || !TryReadPointer(out Vector2 screen)) return;

            Vector3 world = viewCamera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -viewCamera.transform.position.z));
            world.z = transform.position.z;

            transform.position = world;
        }

        private bool TryReadPointer(out Vector2 screen)
        {
            screen = Vector2.zero;

#if ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;

            if (mouse == null) return false;

            bool pressed = followWhileHeld ? mouse.leftButton.isPressed : mouse.leftButton.wasPressedThisFrame;

            if (!pressed) return false;

            screen = mouse.position.ReadValue();
            return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            bool pressed = followWhileHeld ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0);

            if (!pressed) return false;

            screen = Input.mousePosition;
            return true;
#else
            return false;
#endif
        }

    }
}

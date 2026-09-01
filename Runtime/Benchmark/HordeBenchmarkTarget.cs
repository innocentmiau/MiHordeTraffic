using Unity.AI.Navigation;
using UnityEngine;
using Random = UnityEngine.Random;

namespace MiHordeTraffic.Benchmark
{
    /*
     * Dragging the target around in the editor by hand does move the crowd, but no two runs move it the same way,
     * and a benchmark whose input differs between the two things being compared is not comparing them.
     * This drives the target off a seeded, fixed pattern so a built in avoidance run and a separation run
     * are chasing the same thing through the same positions at the same times.
     *
     * Orbit and teleport stress different halves of the problem. Orbit changes every path slightly every frame
     * and keeps the crowd bunched into one moving clump, which is where separation has the most overlaps to resolve.
     * Teleport invalidates every path at once, which is a pathfinding spike rather than an avoidance one,
     * and is the one to reach for when checking whether pathfinding is what is actually costing the frame.
     */
    /// <summary>
    /// Moves the transform the horde is chasing, on a repeatable pattern, so repathing load is the same between runs.
    /// </summary>
    public class HordeBenchmarkTarget : MonoBehaviour
    {

        [SerializeField] private HordeTargetMotion motion = HordeTargetMotion.ORBIT;

        [Header("Orbit")]
        [SerializeField] private float orbitRadius = 60f;
        [SerializeField] private float orbitDegreesPerSecond = 20f;

        [Header("Teleport")]
        [SerializeField] private NavMeshSurface surface;
        [SerializeField] private float teleportInterval = 5f;
        [SerializeField] private int teleportSeed = 4242;

        private Vector3 _center;
        private float _angle;
        private float _teleportCountdown;

        /// <summary>
        /// Switches the pattern at runtime, from a button or the console, without reconfiguring the scene.
        /// </summary>
        /// <param name="value">The pattern to follow from now on.</param>
        public void SetMotion(HordeTargetMotion value) => motion = value;

        private void Start()
        {
            _center = transform.position;
            _teleportCountdown = teleportInterval;
            Random.InitState(teleportSeed);
        }

        private void Update()
        {
            if (motion == HordeTargetMotion.ORBIT)
            {
                Orbit();
                return;
            }

            if (motion == HordeTargetMotion.TELEPORT) Teleport();
        }

        private void Orbit()
        {
            _angle += orbitDegreesPerSecond * Mathf.Deg2Rad * Time.deltaTime;
            transform.position = _center + new Vector3(Mathf.Cos(_angle), 0f, Mathf.Sin(_angle)) * orbitRadius;
        }

        private void Teleport()
        {
            _teleportCountdown -= Time.deltaTime;
            if (_teleportCountdown > 0f) return;

            _teleportCountdown = teleportInterval;
            if (NavMeshSurfaceSampler.TrySamplePoint(surface, 0f, 1f, out Vector3 position)) transform.position = new Vector3(position.x, _center.y, position.z);
        }

    }
}

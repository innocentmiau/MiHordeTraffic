using System.Collections;
using MiHordeTraffic.Movement;
using MiHordeTraffic.Pathing.FlowField;
using MiHordeTraffic.Spawning;
using UnityEngine;

namespace MiHordeTraffic.Samples.FlowFieldCrowd2D
{
    /*
     * The 2D twin of the Flow Field Crowd spawner, and nearly a copy of it, which is the point. The only real
     * difference is that the ring is drawn on XY, because HordeSpawn takes and returns world positions and does the
     * conversion to the grid itself. A spawner written for one plane is wrong on the other only in where it aims.
     *
     * Bodies keep the depth of whatever point was asked for, so a crowd spawned around a target at Z zero stays at Z
     * zero. Sorting in 2D is usually by sorting layer and order rather than by depth, but a game that does sort on Z
     * gets back the Z it asked for.
     */
    /// <summary>
    /// Spawns a 2D crowd around the flow field's target, asking the grid where each body can actually stand.
    /// </summary>
    public class FlowCrowd2DSpawner : MonoBehaviour
    {

        /*
         * A ring point can land inside a wall or off the map, and the answer to that is another ring point rather
         * than forcing the body somewhere wrong.
         */
        private const int PLACEMENT_ATTEMPTS = 8;

        [Tooltip("Prefab to spawn. It needs a HordeAgent on it, and a sprite to see it by.")]
        [SerializeField] private GameObject bodyPrefab;

        [SerializeField, Min(1)] private int count = 300;

        [Tooltip("Bodies are spawned on a ring around the driver's Target, between these two distances.")]
        [SerializeField, Min(0f)] private float innerRadius = 8f;
        [SerializeField, Min(0f)] private float outerRadius = 14f;

        [Tooltip("How far from the wanted point HordeSpawn may look for ground the crowd can actually use.")]
        [SerializeField, Min(.5f)] private float searchRadius = 3f;

        [SerializeField] private bool spawnOnStart = true;

        [Tooltip("How many bodies the Spawn Wave context menu adds while playing, for pushing the crowd size up live.")]
        [SerializeField] private int waveSize = 100;

        private static bool FieldReady => HordeFlowFieldDriver.Instance && HordeFlowFieldDriver.Instance.Field.IsBaked;

        /*
         * The driver bakes in its own Start, and Unity does not say whose Start runs first. Spawning straight away
         * lost that race about half the time: every TryFind said no because there was no grid yet, and the wave came
         * out empty. Waiting for the bake also covers a driver with Bake On Start off, the wave lands once it bakes.
         */
        private IEnumerator Start()
        {
            if (!spawnOnStart) yield break;

            while (!FieldReady)
                yield return null;

            SpawnWave(count);
        }

        /// <summary>
        /// Puts a wave of bodies down around the target.
        /// </summary>
        /// <param name="wanted">How many to place.</param>
        /// <returns>How many actually landed, which is lower when the ring runs out of usable ground.</returns>
        public int SpawnWave(int wanted)
        {
            if (!bodyPrefab) return 0;

            int placed = 0;

            for (int i = 0; i < wanted; i++)
                if (TrySpawnOne()) placed++;

            return placed;
        }

        /// <summary>
        /// Spawns another wave of the configured size, for a UI button or the inspector's context menu.
        /// </summary>
        [ContextMenu("Spawn Wave")]
        public void SpawnExtraWave() => Debug.Log($"[MiHordeTraffic sample] Spawned {SpawnWave(waveSize)} of {waveSize}.", this);

        private bool TrySpawnOne()
        {
            for (int attempt = 0; attempt < PLACEMENT_ATTEMPTS; attempt++)
            {
                if (!HordeSpawn.TryFind(RingPoint(), searchRadius, out Vector3 spawn)) continue;

                GameObject spawned = Instantiate(bodyPrefab, spawn, Quaternion.identity);

                /*
                 * Bodies on the flow field follow the driver's target and need nothing here. A body with a HordeEntity
                 * paths with its own NavMeshAgent when the scheduler runs NAVMESH_AGENT, and that path needs a target.
                 */
                HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;

                if (driver && spawned.TryGetComponent(out IHordeTargetable targetable)) targetable.SetTarget(driver.Target);

                return true;
            }

            return false;
        }

        private Vector3 RingPoint()
        {
            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;
            Vector3 centre = driver && driver.Target ? driver.Target.position : transform.position;

            float angle = Random.value * Mathf.PI * 2f;
            float distance = Mathf.Lerp(innerRadius, outerRadius, Random.value);

            return centre + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * distance;
        }

    }
}

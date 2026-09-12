using MiHordeTraffic.Pathing.FlowField;
using MiHordeTraffic.Spawning;
using UnityEngine;

namespace MiHordeTraffic.Samples.FlowFieldCrowd
{
    /*
     * Put this on an empty object, give it a prefab with a HordeAgent on it, and press play. That is the whole
     * setup for a flow field crowd, and it is worth seeing how little of it is about this package.
     *
     * The one thing that is genuinely worth copying is asking HordeSpawn where a body can stand rather than
     * sampling the navmesh. The two do not agree: the bake erodes for clearance and rejects cells the navmesh is
     * perfectly happy with, so a body placed by the navmesh can land on ground the grid will not walk on. It then
     * takes the recovery path and visibly wanders out to real ground before it sets off, which reads as the spawn
     * being broken rather than as the position being wrong.
     */
    /// <summary>
    /// Spawns a crowd around the flow field's target, asking the grid where each body can actually stand.
    /// </summary>
    public class FlowCrowdSpawner : MonoBehaviour
    {

        /*
         * A ring point can land on a building, inside a gate or off the map, and the answer to that is another
         * ring point rather than forcing the body somewhere wrong. A handful of tries is plenty: if this many
         * points in a row are all unusable then the ring itself is in the wrong place.
         */
        private const int PLACEMENT_ATTEMPTS = 8;

        [Tooltip("Prefab to spawn. It needs a HordeAgent on it and nothing else.")]
        [SerializeField] private GameObject bodyPrefab;

        [SerializeField, Min(1)] private int count = 200;

        [Tooltip("Bodies are spawned on a flat ring around the driver's Target, between these two distances.")]
        [SerializeField, Min(0f)] private float innerRadius = 20f;
        [SerializeField, Min(0f)] private float outerRadius = 35f;

        [Tooltip("How far from the wanted point HordeSpawn may look for ground the crowd can actually use.")]
        [SerializeField, Min(.5f)] private float searchRadius = 5f;

        [SerializeField] private bool spawnOnStart = true;

        private void Start()
        {
            if (spawnOnStart) SpawnWave(count);
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

        /*
         * Safe to call from a coroutine, a wave timer or a button, which is worth saying because it was not always
         * true: HordeSpawn reads how full a cell is, and that array belongs to the congestion jobs for most of the
         * frame. It finishes them itself now rather than throwing at whoever asked.
         */
        private bool TrySpawnOne()
        {
            for (int attempt = 0; attempt < PLACEMENT_ATTEMPTS; attempt++)
            {
                if (!HordeSpawn.TryFind(RingPoint(), searchRadius, out Vector3 spawn)) continue;

                Instantiate(bodyPrefab, spawn, Quaternion.identity);
                return true;
            }

            return false;
        }

        /*
         * Around the target rather than around this object, so the sample behaves the same wherever the spawner
         * happens to be parked in the scene.
         */
        private Vector3 RingPoint()
        {
            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;
            Vector3 centre = driver && driver.Target ? driver.Target.position : transform.position;

            float angle = Random.value * Mathf.PI * 2f;
            float distance = Mathf.Lerp(innerRadius, outerRadius, Random.value);

            return centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
        }

    }
}

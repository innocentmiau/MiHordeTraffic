using MiHordeTraffic.Movement;
using UnityEngine;
using UnityEngine.AI;

namespace MiHordeTraffic.Samples.NavMeshCrowd
{
    /*
     * The other technique, kept as a separate sample on purpose. Nothing in here is needed to use the flow field
     * and nothing in the flow field sample is needed here, so whichever one is open is the whole of what that
     * approach costs to set up rather than half of a script that does both.
     *
     * There is deliberately no body script beside this one. HordeEntity already is one: it holds the NavMeshAgent,
     * registers with HordePathScheduler so repaths are paced across the crowd instead of every body asking for one
     * every frame, takes a target, and freezes and resumes. Writing another component next to it is how a project
     * ends up reimplementing it, usually without the pacing, which is the part that makes a large crowd affordable.
     *
     * The prefab wants a NavMeshAgent, a HordeEntity and a HordeAgent. The HordeAgent is still worth having here:
     * it replaces the NavMeshAgent's own local avoidance with this package's separation, which is the half of the
     * comparison that does not depend on which thing is doing the pathfinding.
     */
    /// <summary>
    /// Spawns a NavMeshAgent crowd, for comparing stock pathfinding against the flow field in the same scene.
    /// </summary>
    public class NavMeshCrowdSpawner : MonoBehaviour
    {

        private const int PLACEMENT_ATTEMPTS = 8;

        [Tooltip("Prefab to spawn. It needs a NavMeshAgent, a HordeEntity and a HordeAgent.")]
        [SerializeField] private GameObject bodyPrefab;

        [Tooltip("What the crowd chases. Handed to each body's HordeEntity as it spawns.")]
        [SerializeField] private Transform target;

        [SerializeField, Min(1)] private int count = 200;

        [Tooltip("Bodies are spawned on a flat ring around the target, between these two distances.")]
        [SerializeField, Min(0f)] private float innerRadius = 20f;
        [SerializeField, Min(0f)] private float outerRadius = 35f;

        [Tooltip("How far from the wanted point the navmesh may be sampled for somewhere to stand.")]
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
        /// <returns>How many actually landed, which is lower when the ring runs out of navmesh.</returns>
        public int SpawnWave(int wanted)
        {
            if (!bodyPrefab) return 0;

            int placed = 0;

            for (int i = 0; i < wanted; i++)
                if (TrySpawnOne()) placed++;

            return placed;
        }

        /*
         * Sampled from the navmesh rather than from HordeSpawn, because this sample is the stock technique and
         * should not need the grid baked to run. In a scene using the flow field, ask HordeSpawn instead: it knows
         * about clearance erosion and about runtime obstacles, and the navmesh knows about neither.
         */
        private bool TrySpawnOne()
        {
            for (int attempt = 0; attempt < PLACEMENT_ATTEMPTS; attempt++)
            {
                if (!NavMesh.SamplePosition(RingPoint(), out NavMeshHit hit, searchRadius, NavMesh.AllAreas)) continue;

                GameObject spawned = Instantiate(bodyPrefab, hit.position, Quaternion.identity);
                HordeEntity body = spawned.GetComponent<HordeEntity>();

                if (body) body.SetTarget(target);

                return true;
            }

            return false;
        }

        private Vector3 RingPoint()
        {
            Vector3 centre = target ? target.position : transform.position;

            float angle = Random.value * Mathf.PI * 2f;
            float distance = Mathf.Lerp(innerRadius, outerRadius, Random.value);

            return centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
        }

    }
}

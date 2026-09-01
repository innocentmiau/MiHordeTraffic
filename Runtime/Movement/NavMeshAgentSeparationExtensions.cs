using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;

namespace MiHordeTraffic.Movement
{
    /*
     * Move is used rather than writing transform.position or nextPosition directly because it is the one path that projects the displacement back onto the navmesh.
     * A crowd pressing against a wall generates pushes pointing straight into it,
     * and a raw position write would post agents through the geometry or drop them off the mesh entirely, where they stop pathing and never recover.
     *
     * Move also leaves the agent's own steering alone. It is a displacement applied on top of whatever the agent decided this frame,
     * so the agent keeps following its path and separation only ever nudges it sideways,
     * which is the difference between this and fighting the agent for control of its position.
     */
    /// <summary>
    /// Hooks a separation push into an agent that is still doing its own pathfinding and path following.
    /// </summary>
    public static class NavMeshAgentSeparationExtensions
    {

        private const float NEGLIGIBLE_PUSH_SQUARED = .000001f;

        /// <summary>
        /// Turns off the built in local avoidance solver, which is the per agent cost the separation system replaces.
        /// </summary>
        /// <param name="agent">The agent to strip avoidance from.</param>
        public static void DisableBuiltInAvoidance(this NavMeshAgent agent)
        {
            if (!agent) return;

            agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
        }

        /// <summary>
        /// Applies one frame of separation push on top of whatever the agent's own steering did.
        /// </summary>
        /// <param name="agent">The agent to displace.</param>
        /// <param name="push">Push velocity in world units per second, as produced by the SeparationSystem.</param>
        /// <param name="deltaTime">Frame time to scale the push by.</param>
        /// <param name="mode">Which hand off to use, which is the difference between navmesh safety and main thread cost.</param>
        /// <returns>True when a displacement was actually applied.</returns>
        public static bool ApplySeparationPush(this NavMeshAgent agent, float3 push, float deltaTime, SeparationApplyMode mode)
        {
            /*
             * The magnitude test comes before anything that touches the agent on purpose.
             * isActiveAndEnabled and isOnNavMesh are both calls out to native, and in a sparse crowd most agents have no push at all,
             * so checking those first would pay the expensive part of this method for every agent that needs nothing.
             */
            if (mode == SeparationApplyMode.NONE) return false;
            if (math.lengthsq(push) < NEGLIGIBLE_PUSH_SQUARED) return false;
            if (!agent || !agent.isActiveAndEnabled || !agent.isOnNavMesh) return false;

            Vector3 displacement = new Vector3(push.x, push.y, push.z) * deltaTime;

            if (mode == SeparationApplyMode.NAVMESH_MOVE)
            {
                agent.Move(displacement);
                return true;
            }

            agent.nextPosition += displacement;
            return true;
        }

    }
}

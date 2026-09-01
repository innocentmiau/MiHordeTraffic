using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Jobs;

namespace MiHordeTraffic.Separation
{
    /*
     * Reading transform.position on the main thread is a call out to native per agent, and at a few thousand agents
     * that read alone shows up in the profiler before any separation has been computed. A transform job hands the
     * whole hierarchy read to the worker threads in one go, and Unity's transform system feeds them in the order
     * the transforms are laid out in memory rather than chasing them one at a time.
     *
     * Sampling only. Pushes still go back to each agent individually through agent.Move, which is what keeps them
     * projected onto the navmesh and is the reason a crowd pressed against a wall never ends up inside it.
     */
    /// <summary>
    /// Reads every registered body's world position into the array the separation solve runs on.
    /// </summary>
    [BurstCompile]
    public struct TransformSampleJob : IJobParallelForTransform
    {

        [WriteOnly] public NativeArray<float3> Positions;

        public void Execute(int index, TransformAccess transform)
        {
            Positions[index] = transform.position;
        }

    }
}

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace MiHordeTraffic.Separation
{
    /*
     * Burst silently falling back to plain IL is the failure that looks like success: everything still runs, the numbers are just several times worse,
     * and nothing in the console says so. BurstDiscard is stripped out of a Burst compiled body and kept in a managed one,
     * so if the flag comes back still false the job really did go through Burst.
     *
     * Run once at startup, not every frame.
     */
    /// <summary>
    /// Reports whether Burst actually compiled the jobs in this assembly, rather than falling back to managed code.
    /// </summary>
    [BurstCompile]
    public struct BurstProbeJob : IJob
    {

        [WriteOnly] public NativeArray<bool> Result;

        public void Execute()
        {
            bool managed = false;
            MarkManaged(ref managed);
            Result[0] = !managed;
        }

        [BurstDiscard]
        private static void MarkManaged(ref bool value) => value = true;

    }
}

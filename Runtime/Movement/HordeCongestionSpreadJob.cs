using MiHordeTraffic.Pathing.FlowField;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeTraffic.Movement
{
    /*
     * Congestion is a property of a region, not of a cell. Priced per cell, a single body is enough to make one
     * cell expensive, and one cell of extra distance is all a one-cell detour costs, so the field will happily
     * bend every route behind that body around it. Do that with a crowd and the detours interfere with each other
     * and the whole thing turns into bodies weaving around each other in waves.
     *
     * Averaging over a neighbourhood dilutes a lone hot cell into nothing while leaving a jam that actually spans
     * several cells hot, so the field only ever sees features big enough to be worth walking around.
     *
     * Unwalkable neighbours are left out rather than counted as free, because averaging a wall in would make the
     * cells beside a chokepoint look cheaper than the ones in the middle of it, which is backwards.
     *
     * The heaviest pass of the four by some way, since it samples a whole kernel per cell, and the one that gains
     * most from running on every worker. It only reads its neighbours and only writes its own cell, so splitting
     * it across threads changes nothing about the answer.
     */
    /// <summary>
    /// Averages each cell's price over its neighbourhood and eases the field's costs towards the result.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct HordeCongestionSpreadJob : IJobParallelFor
    {

        public HordeGridInfo Grid;

        [ReadOnly] public NativeArray<byte> Walkable;
        [ReadOnly] public NativeArray<float> Targets;

        public NativeArray<float> Cost;

        public int BlurRadius;
        public float Rise;
        public float Fall;

        public void Execute(int index)
        {
            if (Walkable[index] == 0) return;

            int radius = math.max(BlurRadius, 0);
            int x = index % Grid.Width;
            int z = index / Grid.Width;

            float sum = 0f;
            int samples = 0;

            for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = x + dx;
                int nz = z + dz;

                if (nx < 0 || nz < 0 || nx >= Grid.Width || nz >= Grid.Height) continue;

                int neighbour = nz * Grid.Width + nx;

                if (Walkable[neighbour] == 0) continue;

                sum += Targets[neighbour];
                samples++;
            }

            float target = samples > 0 ? sum / samples : Targets[index];

            Cost[index] = math.lerp(Cost[index], target, target > Cost[index] ? Rise : Fall);
        }

    }
}

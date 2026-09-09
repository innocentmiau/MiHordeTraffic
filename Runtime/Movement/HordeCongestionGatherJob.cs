using MiHordeTraffic.Pathing.FlowField;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeTraffic.Movement
{
    /*
     * The only one of the four passes that is proportional to the crowd rather than to the grid, and the only one
     * that has to stay on one thread. Every body adds itself into whichever cell it is standing in, and several
     * bodies share a cell by definition, so the additions collide. Atomics would fix that and would cost more than
     * they save: this is a few thousand iterations against the millions the other passes do.
     *
     * Progress along the route, not distance travelled. A crammed crowd is being shoved about by separation at up
     * to walking pace while getting nowhere, and measuring how far bodies moved would price that cell as flowing.
     * Only bodies trying to get somewhere are counted, so a crowd parked on its target does not price the ground
     * it is standing on as impassable for everybody still arriving.
     */
    /// <summary>
    /// Counts every body into the cell it stands in, and accumulates how well the ones walking a route are doing.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct HordeCongestionGatherJob : IJob
    {

        public HordeGridInfo Grid;

        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float3> Previous;
        [ReadOnly] public NativeArray<float2> Flow;
        [ReadOnly] public NativeArray<float> Reference;

        /*
         * Membership of the active set, kept as a flag per cell beside the list so asking whether a cell is already
         * in it is a read rather than a search. A body walking into a cell nothing has touched for a while is the
         * only way a cell joins, so this is the one place that has to be right.
         */
        public NativeArray<byte> Touched;
        public NativeList<int> Active;

        public NativeArray<float> Progress;
        public NativeArray<float> SpeedSum;
        public NativeArray<float> ReferenceSum;
        public NativeArray<int> Counts;
        public NativeArray<int> Occupancy;

        public int BodyCount;
        public float DeltaTime;
        public float ProgressSmoothing;
        public float3 Goal;
        public float ArriveRadiusSquared;

        public void Execute()
        {
            float inverseDelta = DeltaTime > 0f ? 1f / DeltaTime : 0f;
            float progressRate = math.saturate(ProgressSmoothing * DeltaTime);

            for (int i = 0; i < BodyCount; i++)
            {
                float3 position = Positions[i];
                int cell = Grid.IndexOf(position);

                if (cell < 0) continue;

                /*
                 * Zeroed on the way in rather than by the clear pass, because the clear only knows about cells that
                 * were already being tracked. A cell joining the set now holds whatever it held the last time
                 * anybody stood in it, which could be minutes ago.
                 */
                if (Touched[cell] == 0)
                {
                    Touched[cell] = 1;
                    Active.Add(cell);

                    SpeedSum[cell] = 0f;
                    ReferenceSum[cell] = 0f;
                    Counts[cell] = 0;
                    Occupancy[cell] = 0;
                }

                /*
                 * Counted before anything else, so a body that arrived and stopped still makes the cell it stands
                 * in as full as it really is.
                 */
                Occupancy[cell]++;

                if (math.distancesq(position.xz, Goal.xz) <= ArriveRadiusSquared) continue;

                float2 flow = Flow[cell];
                if (flow.Equals(float2.zero)) continue;

                /*
                 * What this body could have managed here on open ground. Zero means it is not attempting a route
                 * at all, and a body with no opinion about its own speed has nothing to say about the cell's.
                 */
                float reference = Reference[i];
                if (reference <= .01f) continue;

                /*
                 * Clamped both ways because the roster swap removes bodies, so a slot can hold a different body
                 * than it did last frame and report the distance between two unrelated positions as one frame of
                 * movement. Left unclamped, one respawn would read as a body moving at hundreds of metres a second
                 * and would mark its whole route as gloriously fast, or as one moving backwards just as fast and
                 * poison the average the other way.
                 */
                float ceiling = reference * 2f;
                float instant = math.clamp(math.dot(position.xz - Previous[i].xz, flow) * inverseDelta, -ceiling, ceiling);
                float progress = math.lerp(Progress[i], instant, progressRate);

                Progress[i] = progress;

                SpeedSum[cell] += math.max(progress, 0f);
                ReferenceSum[cell] += reference;
                Counts[cell]++;
            }
        }

    }
}

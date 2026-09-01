using MiHordeTraffic.Pathing.FlowField;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeTraffic.Movement
{
    /*
     * Prices each cell by how fast bodies actually get through it, which is the difference between knowing a route
     * is jammed and guessing that it might be. Counting bodies per cell would be the easy version and it is wrong
     * in both directions: a wide corridor packed with bodies can still flow freely, and a narrow one with a handful
     * in it can be solid.
     *
     * Progress along the route, not distance travelled. A crammed crowd is being shoved about by separation at up
     * to the maximum push speed, so bodies churning in place with no progress at all cover plenty of ground and
     * would measure as flowing freely. Projecting the movement onto the direction the cell says to walk counts
     * only what got the body closer to where it is going, which is what a queue actually costs. A body shoved
     * backwards scores negative and is floored at zero rather than being allowed to make the cell look faster.
     *
     * The cost this writes is read by the expansion as a straight multiplier on every step through the cell, so a
     * bridge that has slowed to a third of walking pace costs three times as much to cross. Nothing then has to
     * notice the jam, decide a detour is worthwhile, or compare two routes: comparing routes is what Dijkstra
     * already does, and it does it for every cell at once. A second bridge wins the moment it is genuinely cheaper,
     * and every body gets the same answer on the same frame.
     *
     * Smoothed towards the measurement rather than set to it, and asymmetrically. Traffic assignment has known
     * since Wardrop that routing everyone onto the cheapest route at once simply moves the jam: the empty bridge
     * fills, becomes expensive, and the crowd swings back.
     *
     * Rising fast and falling slowly is what breaks that cycle. A route that has just been abandoned looks clear
     * within a frame of the last body leaving it, and if the field believes that immediately the crowd turns round
     * and refills it. Letting the cost decay over several seconds means a route has to stay clear before it
     * becomes attractive again, which is the difference between two routes sharing a crowd and one route being
     * swapped for the other over and over.
     *
     * Two signals, because there are two ways a cell can be expensive and only one of them is about movement.
     *
     * Progress prices a cell by how well bodies are getting through it, and only bodies trying to get somewhere
     * can report on that. A body that has arrived is not failing to make progress, it is not attempting any, and
     * averaging its zero in would say the ground it stands on is impassable.
     *
     * Occupancy prices a cell by how full it is, and every body counts whether it is walking, waiting or finished.
     * A crowd parked around a target in a doorway is a wall as far as everyone behind it is concerned, and it does
     * not stop being one because none of it is trying to move. That case is the whole reason to route around.
     *
     * Occupancy is only consulted where nobody is attempting progress at all, which is the case it was added for.
     * Taking the worse of the two instead made a dense crowd expensive for being dense, and a column marching
     * through a corridor at full speed is dense by definition: it would paint its own route red and send everyone
     * behind it around a jam that was never there. Where bodies are trying to move, how fast they manage it
     * already accounts for how many of them there are, and a full cell that is flowing is not a problem.
     *
     * Single threaded on purpose. Accumulating per cell from a parallel job means either atomics or per thread
     * buffers and a reduce, and this walks a couple of thousand positions and a few thousand cells, which in Burst
     * is microseconds. The complexity would buy nothing.
     */
    /// <summary>
    /// Measures how fast bodies are actually crossing each cell and turns that into a traversal cost for the field.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct HordeCongestionJob : IJob
    {

        private const float MINIMUM_SPEED_FRACTION = .05f;

        public HordeGridInfo Grid;

        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float3> Previous;
        [ReadOnly] public NativeArray<byte> Walkable;
        [ReadOnly] public NativeArray<float2> Flow;
        [ReadOnly] public NativeArray<float> Reference;

        /*
         * Each body's own progress, averaged over a window rather than read off one frame. A body that has stopped
         * making headway does not stand still: it shuffles, it is shoved by its neighbours, and it hunts back and
         * forth as the field changes under it. Flooring one frame of that at zero counts every forward twitch and
         * discards every backward one, so a body oscillating in place measures as walking at half pace and the
         * cell it is wedged in never goes red.
         *
         * Keeping the signed average is what makes oscillation cancel. Real walking accumulates in one direction
         * and survives the average; shuffling sums to nothing however violent it is. Only the result is floored,
         * so a body being shoved backwards still cannot make its cell look faster than empty.
         */
        public NativeArray<float> Progress;

        public NativeArray<float> Cost;
        public NativeArray<float> Density;
        public NativeArray<float> SpeedSum;
        public NativeArray<float> ReferenceSum;
        public NativeArray<int> Counts;
        public NativeArray<int> Occupancy;

        public bool WriteCost;

        public int BodyCount;
        public int BlurRadius;
        public float DeltaTime;
        public float ProgressSmoothing;
        public float MaximumCost;
        public float RiseSmoothing;
        public float FallSmoothing;
        public float3 Goal;
        public float ArriveRadiusSquared;
        /*
         * Occupancy is only meaningful against what a cell can hold. Counted raw it depends on the cell size and
         * on how big the bodies are, so any threshold typed into the inspector is a guess that silently stops
         * being reachable the moment either changes, and a cell that is physically full reports as comfortable.
         */
        public float CellCapacity;
        public float ComfortableFill;
        public float JamFill;

        public void Execute()
        {
            for (int i = 0; i < Counts.Length; i++)
            {
                SpeedSum[i] = 0f;
                ReferenceSum[i] = 0f;
                Counts[i] = 0;
                Occupancy[i] = 0;
            }

            float inverseDelta = DeltaTime > 0f ? 1f / DeltaTime : 0f;
            float progressRate = math.saturate(ProgressSmoothing * DeltaTime);

            for (int i = 0; i < BodyCount; i++)
            {
                float3 position = Positions[i];
                int cell = Grid.IndexOf(position);
                if (cell < 0) continue;

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

            float rise = math.saturate(RiseSmoothing * DeltaTime);
            float fall = math.saturate(FallSmoothing * DeltaTime);

            for (int i = 0; i < Cost.Length; i++)
            {
                if (Walkable[i] == 0) continue;

                /*
                 * Smoothed rather than taken raw, because a body straddling a cell boundary flickers between two
                 * cells frame to frame and a raw count would have bodies stuttering as they crossed every line.
                 */
                Density[i] = math.lerp(Density[i], Occupancy[i], Occupancy[i] > Density[i] ? rise : fall);

                if (!WriteCost) continue;

                /*
                 * An empty cell is worth one, not whatever it was last measured at. A jam that clears has to stop
                 * being expensive, and nothing crosses a cell to tell it so.
                 */
                float target = 1f;

                if (Counts[i] > 0)
                {
                    /*
                     * Somebody here is trying to get somewhere, so how well they manage it is the whole answer.
                     * A cell packed with bodies that are all moving freely is a column, not a jam.
                     *
                     * Measured against what those particular bodies could have done here rather than against a
                     * crowd-wide walking pace, so a cell is only expensive when the bodies in it are managing less
                     * than they were capable of. A body still winding up to speed, or easing off because it has
                     * nearly arrived, now scores as flowing perfectly, which is what it is doing.
                     */
                    float wanted = ReferenceSum[i] / Counts[i];
                    float measured = math.max(SpeedSum[i] / Counts[i], math.max(wanted * MINIMUM_SPEED_FRACTION, .0001f));

                    target = math.clamp(wanted / measured, 1f, MaximumCost);
                }
                else if (Occupancy[i] > 0)
                {
                    /*
                     * Bodies are standing here and none is trying to move, so there is no progress to measure and
                     * how full the cell is has to stand in for it. This is the crowd parked around a target in a
                     * doorway: it will never report a speed, and it is still a wall.
                     *
                     * Read off the smoothed density rather than this frame's count, so a cell does not price
                     * itself on the one frame a body happened to be standing over its boundary.
                     */
                    float fill = Density[i] / CellCapacity;
                    float crowding = math.saturate((fill - ComfortableFill) / math.max(JamFill - ComfortableFill, .001f));

                    target = math.lerp(1f, MaximumCost, crowding);
                }

                /*
                 * Parked in the sum that has already been read, rather than in an array of its own. Blurring needs
                 * the neighbours' raw answers and writing straight into Cost would have later cells blurring
                 * against values that had already been smoothed.
                 */
                ReferenceSum[i] = target;
            }

            if (WriteCost) Spread(rise, fall);
        }

        /*
         * Congestion is a property of a region, not of a cell. Priced per cell, a single body is enough to make one
         * cell expensive, and one cell of extra distance is all a one-cell detour costs, so the field will happily
         * bend every route behind that body around it. Do that with a crowd and the detours interfere with each
         * other and the whole thing turns into bodies weaving around each other in waves.
         *
         * Averaging over a neighbourhood dilutes a lone hot cell into nothing while leaving a jam that actually
         * spans several cells hot, so the field only ever sees features big enough to be worth walking around.
         *
         * Unwalkable neighbours are left out rather than counted as free, because averaging a wall in would make
         * the cells beside a chokepoint look cheaper than the ones in the middle of it, which is backwards.
         */
        private void Spread(float rise, float fall)
        {
            int radius = math.max(BlurRadius, 0);

            for (int z = 0; z < Grid.Height; z++)
            for (int x = 0; x < Grid.Width; x++)
            {
                int index = z * Grid.Width + x;

                if (Walkable[index] == 0) continue;

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

                    sum += ReferenceSum[neighbour];
                    samples++;
                }

                float target = samples > 0 ? sum / samples : ReferenceSum[index];

                Cost[index] = math.lerp(Cost[index], target, target > Cost[index] ? rise : fall);
            }
        }

    }
}

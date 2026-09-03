using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeTraffic.Separation
{
    /*
     * This is reactive, not predictive. It does not look at where anybody is heading, it looks at who is already
     * standing inside somebody else and pushes them out. Agents are allowed to overlap for a frame or two,
     * which is the whole trade: full RVO costs a solve per agent per neighbour and is what the built in
     * avoidance was spending the main thread on, and this costs one subtract and one square root per overlapping pair.
     *
     * The cell range is derived from the plane mask instead of being a fixed 3x3x3. On the XZ plane every agent
     * shares cell zero on Y, so scanning the Y neighbours would triple the lookups to find nothing.
     *
     * The push is also capped at what it would take to undo the overlap this frame, which is what keeps it from
     * overshooting. MaxPushSpeed on its own is a speed, so how far it actually moves a body depends on the frame:
     * four metres a second is six centimetres at sixty frames and two at a hundred and fifty. A crowd whose
     * overlaps are millimetres deep gets shoved centimetres to fix them, arrives on the far side still overlapping,
     * and is shoved back. That buzz is proportional to frame time rather than triggered by it, so it never looks
     * like an instability, just like the crowd being worse whenever anything else is running.
     *
     * Half the overlap rather than all of it, because both bodies in a pair are being pushed apart at once and
     * each closing the whole gap would resolve it twice over.
     *
     * Two agents at the exact same position have no direction to separate along, which happens more than it sounds like
     * when a pool spawns a batch at one point. Those get a direction seeded from the pair of indices,
     * so it is stable frame to frame and opposite for the two of them rather than jittering them into each other.
     *
     * The second thing this computes is whether an agent has settled.
     * A crowd converging on one point never settles on its own: every agent keeps steering at the target, the ones
     * in front are in the way, separation pushes back, and the pack churns forever because neither side can win.
     * The fix is the one RTS games have used for decades. An agent settles when enough of the neighbours it is
     * already overlapping are both closer to its goal than it is and have settled themselves. The ring that
     * genuinely arrived settles first, the ring behind it sees settled neighbours ahead and settles, and it spreads
     * outwards a ring per tick. It carries the same information a carved navmesh would, without touching the navmesh.
     *
     * What seeds that is the body saying so, not this job guessing it from a distance. A body knows whether it is
     * trying to advance, and reading that beats inferring it from an arrival radius that has to be tuned per group
     * and is wrong for anything that stopped for a reason other than arriving.
     *
     * Settled agents are pushed at a fraction of the usual strength rather than not at all. Not at all would leave
     * two that settled while overlapping interpenetrating for good, and a fraction still stops the crowd churning,
     * which is the whole point: an agent that cannot go anywhere should not be shoving the one behind it.
     *
     * Reading last tick's blocked flags rather than this tick's is what makes that spread rather than resolve at once,
     * and is also what keeps it order independent: no agent's answer depends on which agent the job happened to run first.
     */
    /// <summary>
    /// Computes the push velocity that pulls each agent out of whichever neighbours it is currently overlapping,
    /// and whether it is walled in by neighbours that already gave up on reaching the same goal.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct SeparationPushJob : IJobParallelFor
    {

        private const float COINCIDENT_EPSILON_SQUARED = .0001f;
        private const float SCATTER_TURN = 6.2831853f;
        private const float SCATTER_SKEW = 1.7f;

        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float> Radii;
        [ReadOnly] public NativeArray<float4> Goals;
        [ReadOnly] public NativeArray<byte> WantsToMove;
        [ReadOnly] public NativeArray<byte> SettledPrevious;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> Grid;

        public float CellSize;
        public float3 PlaneMask;
        public int3 CellRange;
        public float PushStrength;
        public float MaxPushSpeed;
        public float InverseDeltaTime;
        public float ResolveFraction;
        public float OverlapTolerance;

        public bool BlockingEnabled;
        public bool DetourEnabled;
        public float DetourStrength;
        public float DetourDensityWeight;
        public int BlockingNeighbours;
        public byte SettledHoldTicks;
        public float SettledPushScale;

        [WriteOnly] public NativeArray<float3> Push;

        [NativeDisableParallelForRestriction] public NativeArray<byte> SettledHold;

        [WriteOnly] public NativeArray<byte> Settled;

        public void Execute(int index)
        {
            float3 position = Positions[index] * PlaneMask;
            float4 goalData = Goals[index];
            float3 goal = goalData.xyz * PlaneMask;
            bool hasGoal = goalData.w > 0f;
            bool wantsToMove = WantsToMove[index] != 0;
            float radius = Radii[index];
            float3 accumulated = float3.zero;
            int blocking = 0;
            int3 baseCell = SpatialHash.CellOf(position, CellSize);

            for (int x = -CellRange.x; x <= CellRange.x; x++)
            for (int y = -CellRange.y; y <= CellRange.y; y++)
            for (int z = -CellRange.z; z <= CellRange.z; z++)
                accumulated += PushFromCell(index, position, radius, goal, hasGoal, baseCell + new int3(x, y, z), ref blocking);

            byte settled = ResolveSettled(index, wantsToMove, hasGoal, blocking);

            /*
             * Kept before the strength is applied, because this is the actual distance that needs undoing and the
             * strength is only how eagerly it is undone.
             */
            float overlap = math.length(accumulated);

            accumulated *= settled != 0 ? PushStrength * SettledPushScale : PushStrength;

            /*
             * Added after the settled scaling rather than before it, and deliberately so. The scaling exists to stop
             * a settled crowd shoving itself around, but this is the one push a settled agent should still act on:
             * it is the difference between waiting behind a jam and going around it.
             *
             * Only agents that settled while still wanting to move get it. An agent that settled because it arrived
             * is where it wants to be, and nudging it sideways would walk the front rank off the target.
             */
            if (DetourEnabled && settled != 0 && wantsToMove && hasGoal)
                accumulated += DetourDirection(position, goal, baseCell) * DetourStrength;

            /*
             * One disables the overlap clamp entirely and leaves only the speed cap, which is how this behaved
             * before the clamp existed. Anything lower limits a body to that fraction of its overlap per frame.
             */
            float limit = ResolveFraction >= 1f ? MaxPushSpeed : math.min(MaxPushSpeed, overlap * ResolveFraction * InverseDeltaTime);
            float speed = math.length(accumulated);

            Push[index] = speed > limit ? accumulated * (limit / speed) : accumulated;
        }

        /// <summary>
        /// Total overlap depth pushed back at one agent by everything bucketed in a single cell,
        /// counting on the way how many of those neighbours are standing between it and its goal having already stopped.
        /// </summary>
        private float3 PushFromCell(int index, float3 position, float radius, float3 goal, bool hasGoal, int3 cell, ref int blocking)
        {
            if (!Grid.TryGetFirstValue(SpatialHash.KeyOf(cell), out int other, out NativeParallelMultiHashMapIterator<int> iterator)) return float3.zero;

            float3 accumulated = float3.zero;
            float goalDistanceSquared = math.lengthsq(goal - position);

            do
            {
                if (other == index) continue;

                float3 otherPosition = Positions[other] * PlaneMask;
                float3 delta = position - otherPosition;
                float minimumDistance = radius + Radii[other];
                float distanceSquared = math.lengthsq(delta);

                if (distanceSquared >= minimumDistance * minimumDistance) continue;

                /*
                 * Measured against this agent's own goal, not the neighbour's. Two agents heading opposite ways
                 * are in each other's way for a moment and should walk through it, not decide they have arrived.
                 */
                if (BlockingEnabled && hasGoal && SettledPrevious[other] != 0 && math.lengthsq(goal - otherPosition) < goalDistanceSquared) blocking++;

                if (distanceSquared < COINCIDENT_EPSILON_SQUARED)
                {
                    accumulated += ScatterDirection(index, other) * minimumDistance;
                    continue;
                }

                float distance = math.sqrt(distanceSquared);

                /*
                 * Subtracted from the depth rather than from the range the pair is found at, so how deep an overlap
                 * has to be before it is worth undoing is separate from how deep it has to be to count as a
                 * neighbour standing in the way. Folding the two together would quietly loosen the settle spread
                 * every time the tolerance was raised.
                 */
                float depth = minimumDistance - distance - minimumDistance * OverlapTolerance;

                if (depth <= 0f) continue;

                accumulated += delta * (depth / distance);
            }
            while (Grid.TryGetNextValue(out other, ref iterator));

            return accumulated;
        }

        /*
         * Where a stuck agent should slide to. Scored against the cell it is already standing in, so a neighbouring
         * cell wins by being emptier, by being nearer the goal, or by some mix of the two. Cells further from the
         * goal are never considered, which is what keeps this a way around the jam rather than a way out of it.
         *
         * Nothing here paths. It is a steer, and it reaches the agent through the same agent.Move as every other
         * push, so the navmesh projects it and an agent steered at a wall slides along the wall instead of into it.
         * Going around a genuinely different route is a pathfinding problem and this is not it.
         */
        private float3 DetourDirection(float3 position, float3 goal, int3 baseCell)
        {
            float goalDistance = math.distance(position, goal);
            int hereOccupancy = Grid.CountValuesForKey(SpatialHash.KeyOf(baseCell));

            float bestScore = 0f;
            float3 best = float3.zero;

            for (int x = -CellRange.x; x <= CellRange.x; x++)
            for (int y = -CellRange.y; y <= CellRange.y; y++)
            for (int z = -CellRange.z; z <= CellRange.z; z++)
            {
                int3 offset = new int3(x, y, z);
                if (math.all(offset == int3.zero)) continue;

                int3 cell = baseCell + offset;
                float3 centre = ((float3)cell + .5f) * CellSize * PlaneMask;

                float progress = goalDistance - math.distance(centre, goal);
                if (progress < 0f) continue;

                float score = progress + (hereOccupancy - Grid.CountValuesForKey(SpatialHash.KeyOf(cell))) * DetourDensityWeight;

                if (score <= bestScore) continue;

                bestScore = score;
                best = centre - position;
            }

            return math.normalizesafe(best * PlaneMask, float3.zero);
        }

        /*
         * The hold is what stops the propagated half of this flickering. On the edge of a crowd an agent drifts in
         * and out of the neighbour count every few ticks, and a flag that followed it exactly would start and stop
         * the agent several times a second. A body that says outright it is not trying to move needs none of that,
         * since that answer is not a measurement and does not wobble.
         */
        private byte ResolveSettled(int index, bool wantsToMove, bool hasGoal, int blocking)
        {
            if (!BlockingEnabled) return Write(index, 0, 0);

            if (!wantsToMove) return Write(index, SettledHoldTicks, 1);

            if (!hasGoal) return Write(index, 0, 0);

            byte hold = SettledHold[index];

            if (blocking >= BlockingNeighbours) hold = SettledHoldTicks;
            else if (hold > 0) hold--;

            return Write(index, hold, hold > 0 ? (byte)1 : (byte)0);
        }

        private byte Write(int index, byte hold, byte settled)
        {
            SettledHold[index] = hold;
            Settled[index] = settled;
            return settled;
        }

        /// <summary>
        /// A repeatable direction for a pair of agents sitting on top of each other, opposite for each of the two.
        /// </summary>
        private float3 ScatterDirection(int index, int other)
        {
            uint seed = math.hash(new int2(math.min(index, other), math.max(index, other)));
            float angle = seed * (SCATTER_TURN / uint.MaxValue);
            float3 spread = new float3(math.cos(angle), math.sin(angle * SCATTER_SKEW), math.sin(angle));
            float3 direction = math.normalizesafe(spread * PlaneMask, new float3(1f, 0f, 0f) * PlaneMask);
            return index < other ? direction : -direction;
        }

    }
}

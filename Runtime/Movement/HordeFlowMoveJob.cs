using MiHordeTraffic.Pathing.FlowField;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Jobs;

namespace MiHordeTraffic.Movement
{
    /*
     * The whole of movement, for the whole crowd, in one job. No NavMeshAgent, no SetDestination, no agent.Move,
     * and so none of the transform synchronisation that the engine has to do when something else moves its agents.
     * Reading the profiler, that sync was over a millisecond a frame while the crowd simulation it protected was
     * costing four hundredths of one.
     *
     * A body reads the flow direction of the cell it is standing in and nothing else. It does not know where the
     * goal is, whether there is a shorter way, or that a bridge is jammed. All of that is already answered by the
     * expansion, once, for every cell at the same time, which is the entire reason for building a field rather
     * than a path.
     *
     * Bodies slow down for the crowd ahead of them rather than only being pushed out of it once they are inside.
     * Separation is a positional correction and runs after the fact: bodies overlap, then get shoved apart, and
     * in a chokepoint that is a shoving match nobody wins. Slowing on approach is a velocity correction and keeps
     * the overlap from happening, which is why it helps where raising the push strength only makes the shoving
     * more violent. It is the fundamental diagram out of traffic and pedestrian flow: speed falls as density rises.
     *
     * The floor under that slowdown is not optional. A crowd that slows in proportion to its own density, with
     * nothing stopping it reaching zero, deadlocks: the cell stays full because nobody is moving and nobody moves
     * because the cell is full. The floor guarantees a jam always drains, however slowly.
     *
     * Sampled one cell ahead along the flow rather than underfoot, so a body slows before it arrives in the press
     * rather than after joining it.
     *
     * Speed is built up and wound down rather than switched on, and how much of it a body keeps depends on how
     * much of it it is actually getting. Setting speed directly means a body pressed against a crowd walks into it
     * at full pace every frame, achieving nothing and pushing hard the whole time, and separation is left to
     * absorb all of that. Winding the speed down to whatever the body is really managing turns that press into a
     * body that has stopped trying, which is both what a person in a queue does and much less for separation to
     * fight.
     *
     * Measured along the heading rather than as raw distance, so being shoved sideways by neighbours does not read
     * as progress. A body going nowhere is going nowhere however much it is being jostled.
     *
     * A body can be made to face where it is going before it goes there. Off, the heading lags the field and the
     * body walks along it regardless, which is fine for anything overhead or fast moving and reads as sliding when
     * it is neither. On, how far the heading still has to turn scales the speed down, so a body that needs to
     * reverse turns on the spot first and only then sets off.
     *
     * Compared as a dot product against precomputed cosines rather than by taking an angle, because acos per body
     * per frame buys nothing here: the comparison is monotonic either way.
     *
     * The tolerance is what stops this looking robotic. Demanding an exact facing means a body creeps the last few
     * degrees before moving at all, so anything inside the tolerance is treated as close enough and the band up to
     * the limit tapers rather than switching.
     *
     * Heading is turned towards the field rather than set to it, and each body keeps its own. The field is global,
     * so when a route becomes expensive it reverses for every body standing on it at the same instant, and bodies
     * that snap to it all spin on the spot together. Turning at a limited rate gives a body somewhere to be
     * mid-decision: if the field swings back before the turn finishes, it wobbles instead of pirouetting, and
     * because bodies are at different angles they stop reversing in lockstep.
     *
     * Turned by angle rather than by interpolating the vectors, because interpolating between opposite directions
     * passes through zero length and a body reversing would snap through a degenerate heading exactly halfway.
     *
     * Walkability is checked on the destination rather than the origin. Separation can push a body towards a wall
     * and the flow can send it along one, and without agent.Move projecting the result onto the navmesh there is
     * nothing else stopping it walking through. Refusing the step is cruder than sliding along the surface would
     * be, but the crowd is dense enough that a body refused this frame is pushed sideways by its neighbours the
     * next one, which produces the sliding for free.
     */
    /// <summary>
    /// Moves every body by its flow direction and separation push, and keeps it on walkable ground.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct HordeFlowMoveJob : IJobParallelForTransform
    {

        private const float FACING_EPSILON = .0001f;
        private const float STEERING_EPSILON = .05f;

        public HordeGridInfo Grid;

        [ReadOnly] public NativeArray<float2> Flow;
        [ReadOnly] public NativeArray<byte> Walkable;
        [ReadOnly] public NativeArray<float> Integration;
        [ReadOnly] public NativeArray<float> Height;
        [ReadOnly] public NativeArray<float3> Push;
        [ReadOnly] public NativeArray<float> Speed;
        [ReadOnly] public NativeArray<float> Density;

        /*
         * Whose movement is suspended. Read rather than the bodies being taken off the roster, because the roster
         * is also what the congestion pass counts, and a body that stops being counted stops blocking the ground
         * it is standing on.
         */
        [ReadOnly] public NativeArray<byte> Frozen;

        /*
         * Which bodies separation has decided are not getting anywhere: they have arrived, or enough of the
         * neighbours between them and the goal have already given up. Read here because settling had until now
         * only ever been applied to the push, which is the reaction, and never to the drive, which is the cause.
         * A settled body was still being handed a full speed pointed at the goal every frame, so the crowd at a
         * destination was a pile of bodies all pressing inwards while separation was told to shove them apart
         * gently. That is a pressure source with a damper bolted to the wrong end of it.
         */
        [ReadOnly] public NativeArray<byte> Settled;

        [NativeDisableParallelForRestriction] public NativeArray<float2> Heading;
        [NativeDisableParallelForRestriction] public NativeArray<float> CurrentSpeed;
        /*
         * What the body would be up to on open ground, ramped by the same acceleration but never wound down by
         * stalling. CurrentSpeed cannot answer that question: it is deliberately dragged towards whatever the body
         * is actually achieving, so a body that has given up reports that it wanted to crawl and managed to crawl,
         * and the cell it is stuck in prices itself as flowing perfectly. Keeping the unobstructed ramp separately
         * is what lets a jam still be measured as a jam.
         */
        [NativeDisableParallelForRestriction] public NativeArray<float> OpenSpeed;
        [NativeDisableParallelForRestriction] public NativeArray<float3> LastPosition;

        /*
         * How long this body has wanted to move and not managed it. Carried between frames because one frame of
         * being blocked is a body squeezing past somebody, and only a run of them is a body that is stuck.
         */
        [NativeDisableParallelForRestriction] public NativeArray<float> StallTime;

        /*
         * Not zero. A queue behind a chokepoint settles from the front backwards, and a settled body that stops
         * completely cannot take up the room the body in front of it just vacated, so the queue would only ever
         * drain at the rate the settle flag decays. A fraction lets it shuffle forward, which is both what a real
         * queue does and what keeps the flag from being a second way to deadlock.
         */
        public float SettledDriveScale;

        /*
         * How long a body presses before it accepts it is not getting through. Too short and a crowd gives up
         * every time it brushes past itself; too long and a jam churns for that long before it calms down.
         */
        public float StallSettleDelay;

        /*
         * The fraction of what a body wanted that counts as getting somewhere. Derived from SettledDriveScale
         * rather than set, and deliberately below it, because the two together are a feedback loop otherwise:
         * giving up scales the drive down, and if the reduced drive still reads as not getting anywhere then
         * nothing that gave up could ever take it back, and the crowd would freeze solid the first time it
         * touched. Below it, a body that can actually creep at its reduced pace is by definition not stuck and
         * lets go, which is what makes a queue drain rather than set.
         */
        public float StallSpeedFraction;

        public bool SlowInCrowds;
        /*
         * How many bodies fit in a cell at rest, worked out from their radius and the cell size rather than typed
         * in. Typed in it is always wrong: it depends on both numbers, and a threshold above what a cell can
         * physically hold turns crowd slowing into dead code that looks configured.
         */
        public float CellCapacity;
        public float ComfortableFill;
        public float JamFill;
        public float MinimumSpeedFraction;

        public float DeltaTime;
        public float InverseDeltaTime;
        public float Acceleration;
        public float Deceleration;
        public float StallFraction;
        public float TurnSpeed;
        public float HeadingTurnRate;

        public bool RequireFacing;
        public float FacingCosTolerance;
        public float FacingCosLimit;
        public float ArriveRadius;
        public float ArriveTaper;
        public float3 Goal;
        public int RecoverySearchRadius;

        [WriteOnly] public NativeArray<float3> Positions;

        /*
         * What the body could have managed this frame on open ground, which is what congestion has to price its
         * progress against. Dividing by a crowd-wide free speed instead makes every deliberate slowdown look like
         * a jam: a body still accelerating away from a standstill, or easing off on approach to the target, is
         * moving slowly for reasons that have nothing to do with the cell it is standing in, and pricing that as
         * congestion sends everyone behind it around a queue that does not exist.
         *
         * The crowd term is deliberately left out of it. Slowing down because the cell ahead is full is exactly
         * what congestion is meant to detect, so folding it into the reference too would cancel the signal out
         * and every cell would report as flowing perfectly no matter how solid it was.
         */
        [WriteOnly] public NativeArray<float> Reference;

        /*
         * Whether the body has reached what it was walking towards, handed back so separation can be told. A body
         * that has arrived and is standing in a crowd is not failing to get anywhere, it is finished, and treating
         * the two the same is what leaves the ring around a target shoving itself about forever.
         */
        [WriteOnly] public NativeArray<byte> Arrived;

        /*
         * Whether this body has given up, handed back for the same reason arrival is. Until this existed, the only
         * thing in the whole system that ever told separation a body was not trying to advance was standing within
         * ArriveRadius of the goal. Settling spreads outwards from bodies that say so, so a crowd jammed anywhere
         * that was not the goal itself, a bridge, a doorway, the back of another crowd, had nothing to spread from
         * and every body in it drove at full speed into the one in front for as long as it was there.
         */
        [WriteOnly] public NativeArray<byte> Stalled;

        /*
         * Searched as expanding rings so the first hit is the nearest, and bounded so a body genuinely far from any
         * walkable ground gives up rather than scanning the grid every frame for something that is not there.
         */
        /// <summary>
        /// A direction towards the closest cell the goal can actually be reached from, or zero when none is in range.
        /// </summary>
        private float3 NearestReachable(float3 position)
        {
            int2 origin = Grid.CellOf(position);

            for (int radius = 1; radius <= RecoverySearchRadius; radius++)
            {
                float bestDistance = float.MaxValue;
                float3 best = float3.zero;

                for (int z = -radius; z <= radius; z++)
                for (int x = -radius; x <= radius; x++)
                {
                    if (math.abs(x) != radius && math.abs(z) != radius) continue;

                    int2 cell = origin + new int2(x, z);
                    if (!Grid.Contains(cell)) continue;

                    int index = Grid.IndexOf(cell);

                    /*
                     * Rescuing onto ground that is walkable but cut off would only move the problem, so the target
                     * has to be somewhere the expansion actually reached.
                     */
                    if (Walkable[index] == 0 || Integration[index] == float.MaxValue) continue;

                    float3 centre = Grid.CentreOf(cell);
                    float3 offset = new float3(centre.x - position.x, 0f, centre.z - position.z);
                    float distance = math.lengthsq(offset);

                    /*
                     * Best in the ring rather than first found, because scanning order otherwise decides between
                     * equally near cells and sends bodies off at whatever angle the loop happened to reach first.
                     */
                    if (distance >= bestDistance) continue;

                    bestDistance = distance;
                    best = offset;
                }

                if (!best.Equals(float3.zero)) return math.normalizesafe(best, float3.zero);
            }

            return float3.zero;
        }

        /// <summary>
        /// How much of its speed a body may use, given how far it still has to turn to face where it is going.
        /// </summary>
        private float FacingFactor(float2 heading, float2 flow)
        {
            if (!RequireFacing) return 1f;

            float alignment = math.dot(heading, flow);

            return math.saturate((alignment - FacingCosLimit) / math.max(FacingCosTolerance - FacingCosLimit, .0001f));
        }

        /*
         * A body only accelerates towards what it wants when it is getting most of what it already asked for.
         * When it is not, the thing it is heading into is not going to be moved by asking harder, so the speed
         * follows what is actually achievable instead. Deceleration is allowed to be sharper than acceleration
         * because running into a crowd should register immediately while leaving one can afford to be gradual.
         */
        private float ResolveSpeed(int index, float3 position, float2 heading, float desired, out float achieved)
        {
            float current = CurrentSpeed[index];

            achieved = math.dot(position.xz - LastPosition[index].xz, heading) * InverseDeltaTime;

            LastPosition[index] = position;

            bool stalled = current > .01f && achieved < current * StallFraction;
            float target = stalled ? math.max(achieved, 0f) : desired;

            float limit = (target > current ? Acceleration : Deceleration) * DeltaTime;

            current = math.clamp(target, current - limit, current + limit);
            CurrentSpeed[index] = current;

            return current;
        }

        /// <summary>
        /// Turns a heading towards a desired direction at a limited rate, the short way round.
        /// </summary>
        private float2 TurnTowards(float2 heading, float2 desired)
        {
            if (heading.Equals(float2.zero)) return desired;

            float current = math.atan2(heading.y, heading.x);
            float want = math.atan2(desired.y, desired.x);

            /*
             * Wrapped through atan2 of the difference so a turn from just under a half turn to just over it goes
             * the short way rather than most of the way round the circle.
             */
            float delta = math.atan2(math.sin(want - current), math.cos(want - current));
            float limit = HeadingTurnRate * DeltaTime;
            float angle = current + math.clamp(delta, -limit, limit);

            return new float2(math.cos(angle), math.sin(angle));
        }

        /// <summary>
        /// How much of its speed this body may use, given how crowded the cell it is heading into already is.
        /// </summary>
        private float CrowdFactor(float3 position, float2 flow, int cell)
        {
            if (!SlowInCrowds) return 1f;

            int ahead = Grid.IndexOf(position + new float3(flow.x, 0f, flow.y) * Grid.CellSize);

            if (ahead < 0 || Walkable[ahead] == 0) ahead = cell;

            float fill = Density[ahead] / CellCapacity;
            float crowding = math.saturate((fill - ComfortableFill) / math.max(JamFill - ComfortableFill, .001f));

            return math.lerp(1f, MinimumSpeedFraction, crowding);
        }

        public void Execute(int index, TransformAccess transform)
        {
            float3 position = transform.position;

            /*
             * Everything that costs is below this, so a frozen body pays a transform read and a branch. Its
             * position is still published, because the congestion pass reads that array to work out which cells
             * are occupied, and a body nobody can see is a body the crowd walks into.
             *
             * It reports no reference speed, so it counts towards how full its cell is and not towards how fast
             * anything is crossing it. That is exactly the case the occupancy half of congestion was written for:
             * bodies that are not failing to make progress, they are not attempting any.
             */
            if (Frozen[index] != 0)
            {
                Positions[index] = position;
                Reference[index] = 0f;
                OpenSpeed[index] = 0f;
                Arrived[index] = 0;
                Stalled[index] = 0;
                StallTime[index] = 0f;
                return;
            }

            int cell = Grid.IndexOf(position);

            /*
             * Arrival is measured against the goal rather than left to the field, because the field points at the
             * goal from everywhere including the cell the goal is standing in. Without this the front rank walks
             * into the target and keeps pressing, which is the churn settling exists to stop.
             */
            /*
             * Reachable, not merely walkable. Eroding the walkable area for edge clearance can leave a corner
             * surviving as a little island that the expansion never gets to, and a body standing on one has a
             * perfectly good cell under its feet, no flow to follow, and nothing to recover it. It stands there
             * for good. Treating unreachable ground the same as no ground is what lets it walk back out.
             */
            bool onGrid = cell >= 0 && Walkable[cell] != 0 && Integration[cell] != float.MaxValue;
            /*
             * Faded out over a band rather than switched off at a line. A hard threshold is a limit cycle waiting
             * to happen: inside it the pull is gone and separation shoves a body out, the moment it crosses back
             * out the pull returns at full strength and drags it in, and it sits on the boundary jittering forever.
             * It also means everyone still arriving hits the crowd at walking pace instead of easing into it.
             *
             * Ramping the pull to nothing across the last stretch leaves no boundary to oscillate across, and bodies
             * settle into a ring because the only thing left acting on them there is each other.
             */
            float distance = math.distance(position.xz, Goal.xz);
            float approach = math.saturate((distance - ArriveRadius) / math.max(ArriveTaper, .001f));
            bool arrived = approach <= 0f;

            float3 velocity = Push[index];

            /*
             * How fast the body is trying to go under its own steam, which is what decides whether it should be
             * turning at all. Separation is deliberately not part of it: being shoved does not change where a body
             * is facing, and letting it turn a body is the whole of the shaking.
             */
            float steering = 0f;

            /*
             * Turning without walking, which is what a body that has arrived does when the thing it arrived at
             * moves around it. Kept apart from steering because steering is a speed and this is a permission.
             */
            bool turning = false;

            /*
             * Taken out before the clears below, because both are last frame's value and both are read again inside
             * the branch that walks a route. Clearing the slot first and reading the array afterwards is the same
             * slot, so the read came back zero every frame and took two mechanisms down with it.
             *
             * OpenSpeed at zero left the ramp one acceleration step wide, so a body walking at three and a half
             * metres a second reported an unobstructed speed of about a tenth of that. Congestion prices a cell by
             * comparing progress against that reference, so every jam on the map measured as flowing perfectly.
             *
             * StallTime at zero restarted the stall accumulator every frame, so it could never reach the settle
             * delay and Stalled was written as zero for every body on every frame since it was added. The only way
             * left to settle a crowd was standing inside the arrive radius, which is the exact behaviour the stall
             * path was written to replace: a jam at a doorway or at the back of another crowd had nothing anywhere
             * in it that had arrived, so nothing seeded a stop and the whole queue pressed forever.
             */
            float previousOpenSpeed = OpenSpeed[index];
            float previousStallTime = StallTime[index];

            /*
             * Zero unless this body is actually walking a route, so congestion skips anything that has no opinion
             * about how fast it should be going: bodies recovering onto the grid, bodies with no flow to follow,
             * and bodies that have arrived. None of them is failing to make progress, and averaging their stillness
             * into the cost of the ground they stand on is how a target's own doorway prices itself as impassable.
             */
            Reference[index] = 0f;
            OpenSpeed[index] = 0f;

            /*
             * Cleared for the same reason Reference is. A body that is off the grid, has no flow to follow or has
             * arrived is not failing to get anywhere, so none of them should be seeding a stop through the crowd.
             */
            Stalled[index] = 0;
            StallTime[index] = 0f;

            if (onGrid)
            {
                float2 flow = Flow[cell];

                /*
                 * Steered at the goal itself rather than by the field once a body is closing on it, because the
                 * field has no answer finer than a cell and no answer at all in the cell the goal is standing in.
                 * That cell integrates to zero, so nothing around it is cheaper, and both direction modes hand back
                 * a zero vector for it. A body standing there was skipping this whole block: it never updated its
                 * heading and never turned again, whatever the target did.
                 *
                 * Outside that cell it is a resolution problem rather than a hole. Every body in a cell reads one
                 * direction, and it points at the neighbouring cell rather than at the target, so a target moving
                 * about inside its own cell changes nothing any body can see and the crowd only reacts when it
                 * crosses a boundary. At two and a half metres that is a two and a half metre dead zone around
                 * whatever the crowd is chasing, which is exactly where being wrong is most visible.
                 *
                 * Blended across the arrive taper rather than switched at a line, so there is no distance at which
                 * a body's heading jumps between two sources that disagree. The exact goal is already here, it is
                 * what the distance and the arrival test are measured against, so this costs a lerp.
                 *
                 * Only near the goal on purpose. Far away the direct vector is not a route, it points through
                 * whatever walls are in the way, and the field is the thing that knows better.
                 */
                float2 steer = flow;

                if (approach < 1f)
                {
                    float2 direct = math.normalizesafe(Goal.xz - position.xz, flow);

                    steer = math.normalizesafe(math.lerp(flow, direct, 1f - approach), flow);
                }

                if (!steer.Equals(float2.zero))
                {
                    float2 heading = TurnTowards(Heading[index], steer);

                    Heading[index] = heading;

                    /*
                     * A body that has arrived is asked for no speed at all, so it would fall through the rotation
                     * test at the bottom and hold whatever way it was last facing. Standing in a ring around a
                     * target and not turning as it moves around them is the same bug seen from the other side.
                     */
                    turning = arrived;

                    float open = Speed[index] * approach * FacingFactor(heading, steer);

                    /*
                     * Ramped rather than taken flat, so a body that has only just set off is not measured against a
                     * walking pace it cannot physically have reached yet. It rises at the acceleration limit and
                     * falls only when the body genuinely wants less, never because something is in its way.
                     */
                    float openSpeed = math.min(open, previousOpenSpeed + Acceleration * DeltaTime);

                    OpenSpeed[index] = openSpeed;
                    Reference[index] = openSpeed;
                    steering = openSpeed;

                    /*
                     * Applied to the speed the body is driven at and deliberately not to Reference above it.
                     * Reference is what congestion prices the cell against, and it has to stay the speed the body
                     * would have managed on open ground: scaling it here too would have a settled crowd report
                     * that it wanted to crawl and managed to crawl, which is the cell describing itself as clear
                     * at the exact moment it is most solid. Bodies further back would then walk straight into it.
                     */
                    float drive = Settled[index] != 0 ? SettledDriveScale : 1f;

                    float speed = ResolveSpeed(index, position, heading, open * drive * CrowdFactor(position, flow, cell), out float achieved);

                    velocity += new float3(heading.x, 0f, heading.y) * speed;

                    /*
                     * Measured against the unobstructed ramp rather than against CurrentSpeed. CurrentSpeed is
                     * deliberately dragged down towards whatever the body is managing, so a body that has given up
                     * eventually reports that it wanted to stand still and succeeded, and the one signal that says
                     * it is stuck cancels itself out. OpenSpeed never falls for being blocked, which is the whole
                     * reason it is kept separately, so it is the only honest thing to compare against.
                     *
                     * Progress is taken along the heading, so being shoved sideways by the crowd is not mistaken
                     * for getting somewhere, and a body pushed backwards reads as worse than standing still.
                     */
                    bool stalling = openSpeed > STEERING_EPSILON && achieved < openSpeed * StallSpeedFraction;

                    float stalled = stalling ? previousStallTime + DeltaTime : 0f;

                    StallTime[index] = stalled;
                    Stalled[index] = (byte)(stalled >= StallSettleDelay ? 1 : 0);
                }
            }
            else
            {
                /*
                 * Off the grid, or standing on ground the bake rejected. Walking at the goal was the first attempt
                 * at this and it is wrong in exactly the case that matters: a body wedged in a corner has walkable
                 * ground a metre beside it and the goal straight through a wall, so aiming at the goal presses it
                 * into the wall forever. Aiming at the nearest walkable cell gets it back onto described ground
                 * from anywhere, and the field takes over the moment it arrives.
                 */
                float3 rescue = NearestReachable(position);

                if (rescue.Equals(float3.zero) && !arrived)
                    rescue = math.normalizesafe(new float3(Goal.x - position.x, 0f, Goal.z - position.z), float3.zero);

                if (!rescue.Equals(float3.zero))
                {
                    /*
                     * A recovering body still turns, and still turns at a limited rate, so walking back onto
                     * described ground does not look like a body snapping round on the spot.
                     */
                    Heading[index] = TurnTowards(Heading[index], math.normalizesafe(rescue.xz, Heading[index]));

                    velocity += rescue * Speed[index];
                    steering = Speed[index];
                }
            }

            float3 step = velocity * DeltaTime;
            float3 next = position + step;
            int nextCell = Grid.IndexOf(next);
            bool nextWalkable = nextCell >= 0 && Walkable[nextCell] != 0;

            if (!nextWalkable && onGrid)
            {
                /*
                 * Blocked, so try the two axes on their own before giving up. Refusing the whole step was what made
                 * bodies pile against walls and press into each other in doorways: the flow points diagonally at a
                 * gap, one axis of that is into the wall, and the pair being refused together stops a body that had
                 * a perfectly good sideways move available. Taking whichever axis survives is what turns pressing
                 * into a wall into sliding along it, and it is the difference between a queue that funnels through a
                 * gap and one that jams solid outside it.
                 */
                float3 alongX = position + new float3(step.x, 0f, 0f);
                float3 alongZ = position + new float3(0f, 0f, step.z);

                int cellX = Grid.IndexOf(alongX);
                int cellZ = Grid.IndexOf(alongZ);

                bool okX = cellX >= 0 && Walkable[cellX] != 0;
                bool okZ = cellZ >= 0 && Walkable[cellZ] != 0;

                /*
                 * When both survive the longer one wins, which keeps a body sliding the way it was mostly going
                 * rather than picking an axis by accident of ordering.
                 */
                if (okX && okZ)
                {
                    bool preferX = math.abs(step.x) >= math.abs(step.z);

                    next = preferX ? alongX : alongZ;
                    nextCell = preferX ? cellX : cellZ;
                    nextWalkable = true;
                }
                else if (okX)
                {
                    next = alongX;
                    nextCell = cellX;
                    nextWalkable = true;
                }
                else if (okZ)
                {
                    next = alongZ;
                    nextCell = cellZ;
                    nextWalkable = true;
                }
            }

            if (nextWalkable)
            {
                next.y = Height[nextCell];
                transform.position = next;
            }
            else if (!onGrid)
            {
                /*
                 * A recovering body is allowed to move onto ground the grid says nothing about, because refusing
                 * that is what stranded it. Its height is left alone until it reaches a cell that has one.
                 */
                transform.position = next;
            }
            else
            {
                next = position;
            }

            Positions[index] = next;
            Arrived[index] = (byte)(arrived ? 1 : 0);

            /*
             * Turned towards the heading rather than towards the velocity, and only while the body is actually
             * trying to go somewhere.
             *
             * Velocity includes the separation push, and for a body that has arrived the push is all there is.
             * Neighbours jostle it from a different side every frame, so the rotation the body was being asked to
             * take was white noise: slerping a fifth of the way towards a target that has flipped is a swing of
             * tens of degrees, and it repeats forever because the target never settles. That is not steering being
             * insufficiently smooth, it is smoothing applied to noise.
             *
             * Heading is already rate limited by TurnTowards and follows the field rather than the crowd, so a body
             * wedged in a jam still faces where it is trying to go instead of facing whoever last bumped it. And a
             * body with nothing to steer towards holds the rotation it has, which is what a person who has stopped
             * walking does.
             */
            if (steering <= STEERING_EPSILON && !turning) return;

            float2 aim = Heading[index];

            if (math.lengthsq(aim) < FACING_EPSILON) return;

            quaternion desired = quaternion.LookRotationSafe(new float3(aim.x, 0f, aim.y), math.up());
            transform.rotation = math.slerp(transform.rotation, desired, math.saturate(TurnSpeed * DeltaTime));
        }

    }
}

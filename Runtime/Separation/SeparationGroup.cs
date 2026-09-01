using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;

namespace MiHordeTraffic.Separation
{
    /*
     * One group is one independent crowd: its own roster, its own arrays, its own grid, its own job.
     * Bodies in different groups never see each other, which is the point. A flock of flyers wants FULL_3D
     * and a tick every frame, a herd of slow melee wants XZ and a tick every third frame, and folding both into
     * one solve would mean picking settings that suit neither. Running them apart also keeps each grid smaller,
     * and a spatial hash gets cheaper faster than linearly as the population in it drops.
     *
     * There are two hand offs and they have different frame shapes, which is most of the branching below.
     *
     * The per body one solves in Update and gives each body its push through an interface call. It is simple,
     * it goes through agent.Move so the navmesh can never be left, and it costs a main thread call out to native
     * per agent per frame.
     *
     * The batched one moves that to a transform job in LateUpdate and folds only a slice of agents back into their
     * own position each frame. It is the same solve with the hand off taken off the main thread, and it trades
     * the navmesh projection for a bounded disagreement between where an agent paths from and where it is drawn.
     *
     * Both assume the ordering SeparationSystem gives them: nothing touches these arrays between a Schedule
     * and the CompletePending that joins it.
     */
    /// <summary>
    /// One independent separation crowd, holding the roster and native buffers for every body that shares a settings asset.
    /// </summary>
    public class SeparationGroup
    {

        private const int MINIMUM_CAPACITY = 64;
        private const int BUILD_BATCH = 128;
        private const int PUSH_BATCH = 32;
        private const float MINIMUM_CELL_SIZE = .01f;
        private const float NEGLIGIBLE_PUSH_SQUARED = .000001f;

        /*
         * Named markers rather than raw timing, so the cost of this system shows up as its own row in the Profiler
         * next to the engine's own AI row. Total frame time cannot separate the two, which is exactly the comparison
         * anyone tuning this needs to make. All groups share the markers, so the rows stay readable
         * however many groups a scene ends up with.
         */
        private static readonly ProfilerMarker COMPLETE_MARKER = new ProfilerMarker("MiHordeTraffic.Separation.Complete");
        private static readonly ProfilerMarker APPLY_MARKER = new ProfilerMarker("MiHordeTraffic.Separation.Apply");
        private static readonly ProfilerMarker TICK_MARKER = new ProfilerMarker("MiHordeTraffic.Separation.Tick");

        private readonly SeparationSettings _settings;
        private readonly List<ISeparationBody> _bodies = new List<ISeparationBody>();

        private TransformAccessArray _transforms;

        private NativeArray<float3> _positionsFront;
        private NativeArray<float3> _positionsBack;
        private NativeArray<float> _radiiFront;
        private NativeArray<float> _radiiBack;
        private NativeArray<float3> _pushFront;
        private NativeArray<float3> _pushBack;
        /*
         * Double buffered for the same reason positions and radii are: the solve reads these for as long as it is
         * in flight, and the next sample writes them before it has finished. Single buffering them was a genuine
         * race, caught only because the editor's collections safety checks were on to notice it.
         */
        private NativeArray<float4> _goalsFront;
        private NativeArray<float4> _goalsBack;
        private NativeArray<byte> _wantsToMoveFront;
        private NativeArray<byte> _wantsToMoveBack;
        private NativeArray<byte> _settledFront;
        private NativeArray<byte> _settledBack;
        private NativeArray<byte> _settledHold;
        private NativeParallelMultiHashMap<int, int> _grid;

        /*
         * Managed rather than native, and compared against the solve's output instead of being read from it,
         * because telling a body it is blocked is a main thread call and doing that for every body every frame
         * is the cost the batched hand off just finished removing. Blocking changes for a handful of bodies
         * on any given frame, so only those handful are told.
         */
        private bool[] _settledNotified = new bool[0];

        /*
         * Which bodies were given a push last time, so the frame a body's push falls away can be told apart from
         * the many frames it stays away. A body that is never told its push ended keeps applying the last one it
         * was given, and anything reading that value rather than acting on it immediately drifts under a force
         * that stopped existing.
         */
        private bool[] _pushed = new bool[0];

        private JobHandle _handle;
        private bool _scheduled;
        private bool _pushReady;
        private bool _pushFresh;
        private int _capacity;
        private int _scheduledCount;
        private int _framesSinceTick;
        private int _resyncCursor;
        private float _largestRadius;

        /// <summary>
        /// The settings this group runs on, which is also what identifies it.
        /// </summary>
        public SeparationSettings Settings => _settings;

        /// <summary>
        /// How many bodies this group is separating, not counting anything queued this frame.
        /// </summary>
        public int BodyCount => _bodies.Count;

        /// <summary>
        /// Width of one grid cell as of the last tick, which is the configured size or the one derived from the largest radius.
        /// </summary>
        public float CellSize => _settings.CellSize > 0f ? _settings.CellSize : math.max(_largestRadius * 2f, MINIMUM_CELL_SIZE);

        /// <summary>
        /// Creates a group and sizes its buffers up front so a spawn wave does not reallocate them.
        /// </summary>
        /// <param name="settings">The settings this group runs on. Never null.</param>
        public SeparationGroup(SeparationSettings settings)
        {
            _settings = settings;
            _transforms = new TransformAccessArray(math.max(settings.InitialCapacity, MINIMUM_CAPACITY));
            EnsureCapacity(settings.InitialCapacity);
        }

        /// <summary>
        /// The plane mask that zeroes out whichever axes separation is not allowed to act on.
        /// </summary>
        /// <param name="plane">The plane to mask for.</param>
        /// <returns>A vector of ones on the active axes and zeroes elsewhere.</returns>
        public static float3 MaskFor(SeparationPlane plane) => plane switch
        {
            SeparationPlane.XY => new float3(1f, 1f, 0f),
            SeparationPlane.FULL_3D => new float3(1f, 1f, 1f),
            _ => new float3(1f, 0f, 1f)
        };

        /// <summary>
        /// Puts a body on the roster. Only ever called with no job in flight.
        /// </summary>
        /// <param name="body">The body to start separating.</param>
        public void Add(ISeparationBody body)
        {
            Transform bodyTransform = body.SeparationTransform;
            if (!bodyTransform) return;

            int index = _bodies.Count;

            /*
             * Grown here rather than at the tick because the roster is drained before the tick runs, and a spawn
             * wave can push the count past capacity between the two. The offset slot has to exist before it is
             * zeroed, and doubling means a wave of a thousand pays for this a handful of times.
             */
            EnsureCapacity(index + 1);

            body.SeparationIndex = index;
            _bodies.Add(body);
            _transforms.Add(bodyTransform);

            /*
             * The push slots are cleared as well as the offset. A slot that a removed body used still holds that
             * body's last push, and the batched hand off runs over every transform rather than only the ones the
             * last solve covered, so a newly spawned agent landing on a recycled slot would be shoved by whatever
             * the agent before it was being shoved by, on its first frame, before any solve had looked at it.
             */
            _pushFront[index] = float3.zero;
            _pushBack[index] = float3.zero;
            _wantsToMoveBack[index] = 1;
            _wantsToMoveFront[index] = 1;
            _settledFront[index] = 0;
            _settledBack[index] = 0;
            _settledHold[index] = 0;
            _settledNotified[index] = false;
            _pushed[index] = false;
        }

        /// <summary>
        /// Takes a body off the roster if it is on this one. Only ever called with no job in flight.
        /// </summary>
        /// <param name="body">The body to stop separating.</param>
        /// <returns>True when the body belonged to this group and was removed.</returns>
        public bool TryRemove(ISeparationBody body)
        {
            int index = body.SeparationIndex;
            if (index < 0 || index >= _bodies.Count || _bodies[index] != body) return false;

            int last = _bodies.Count - 1;

            /*
             * The offset moves with the body that is swapped into this slot. It is the one piece of per body state
             * that survives between frames, so leaving it behind would hand the moved body a stranger's displacement.
             */
            _bodies[index] = _bodies[last];
            _bodies[index].SeparationIndex = index;
            _wantsToMoveBack[index] = _wantsToMoveBack[last];
            _wantsToMoveFront[index] = _wantsToMoveFront[last];
            _settledFront[index] = _settledFront[last];
            _settledBack[index] = _settledBack[last];
            _settledHold[index] = _settledHold[last];
            _settledNotified[index] = _settledNotified[last];
            _pushed[index] = _pushed[last];
            _bodies.RemoveAt(last);
            _transforms.RemoveAtSwapBack(index);

            body.SeparationIndex = -1;
            return true;
        }

        /// <summary>
        /// Joins whatever job this group has outstanding, and in the per body hand off gives each body its push.
        /// </summary>
        /// <param name="deltaTime">Frame time to scale the pushes by.</param>
        public void CompleteAndApply(float deltaTime)
        {
            CompletePending();
            NotifySettledChanges();

            ApplyPushToBodies(deltaTime);
        }

        /// <summary>
        /// Samples the roster and schedules the next solve, if this group's interval says it is due.
        /// </summary>
        public void TickIfDue()
        {
            _framesSinceTick++;
            if (_framesSinceTick < _settings.UpdateInterval) return;

            _framesSinceTick = 0;

            using (TICK_MARKER.Auto())
            {
                EnsureCapacity(_bodies.Count);
                SampleRadii();

                Schedule(new TransformSampleJob { Positions = _positionsBack }.Schedule(_transforms), Time.deltaTime);
            }
        }

        /// <summary>
        /// Joins the outstanding job at the end of the frame, for the completion modes that ask for it.
        /// </summary>
        public void LateTick() => TryCompleteInLateUpdate();

        /// <summary>
        /// Joins any outstanding job and frees every native buffer this group owns.
        /// </summary>
        public void Dispose()
        {
            /*
             * Joining is allowed to fail without taking the buffers with it. Teardown order on quit is arbitrary,
             * and an exception out of Complete here would leave every buffer this group owns allocated and unowned.
             */
            if (_scheduled)
            {
                try
                {
                    _handle.Complete();
                }
                catch (System.Exception exception)
                {
                    Debug.LogException(exception);
                }

                _scheduled = false;
            }

            DisposeBuffers();

            if (_transforms.isCreated) _transforms.Dispose();

            _bodies.Clear();
            _capacity = 0;
        }

        /// <summary>
        /// Joins the outstanding job and promotes what it wrote into the buffer the bodies read from.
        /// </summary>
        private void CompletePending()
        {
            if (!_scheduled) return;

            using (COMPLETE_MARKER.Auto())
                _handle.Complete();

            _scheduled = false;

            (_pushFront, _pushBack) = (_pushBack, _pushFront);
            (_settledFront, _settledBack) = (_settledBack, _settledFront);
            _pushReady = true;
            _pushFresh = true;
        }

        private void TryCompleteInLateUpdate()
        {
            if (!_scheduled) return;

            if (_settings.CompletionMode == SeparationCompletionMode.LATE_UPDATE)
            {
                CompletePending();
                return;
            }

            if (_settings.CompletionMode == SeparationCompletionMode.POLLED && _handle.IsCompleted) CompletePending();
        }

        /*
         * The magnitude test sits here rather than inside the body, so an agent with nothing pushing on it costs
         * an array read and a compare instead of an interface dispatch followed by several calls out to native.
         * In any crowd that is not uniformly packed most agents are in exactly that state on any given frame.
         */
        private void ApplyPushToBodies(float deltaTime)
        {
            if (!_pushReady) return;
            if (!_pushFresh && !_settings.ReusePushBetweenTicks) return;

            int count = math.min(_scheduledCount, _bodies.Count);

            using (APPLY_MARKER.Auto())
            {
                for (int i = 0; i < count; i++)
                {
                    float3 push = _pushFront[i];
                    bool negligible = math.lengthsq(push) < NEGLIGIBLE_PUSH_SQUARED;

                    /*
                     * Nothing to say only when there was nothing last time either. The frame a push falls away
                     * still costs one call, so a body always hears that it stopped, and the skip still covers the
                     * many frames where an agent with no neighbours has nothing happening to it at all.
                     */
                    if (negligible && !_pushed[i]) continue;

                    _pushed[i] = !negligible;

                    _bodies[i].ApplySeparationPush(negligible ? float3.zero : push, deltaTime);
                }
            }

            _pushFresh = false;
        }

        /*
         * Radii stay on the main thread while positions moved to a job, because a radius is a plain field on a
         * managed object and a position is a call out to native. Only one of the two was ever worth batching.
         */
        private void SampleRadii()
        {
            float largestRadius = 0f;

            for (int i = 0; i < _bodies.Count; i++)
            {
                ISeparationBody body = _bodies[i];
                float radius = body.SeparationRadius;

                _radiiBack[i] = radius;

                /*
                 * Sampled whether or not blocking is on. Both of these are plain managed reads, and skipping them
                 * meant the first tick after blocking was switched on ran against goals left over from whenever
                 * it was last off, which reads as the whole crowd having arrived somewhere it has never been.
                 */
                _goalsBack[i] = body.HasSeparationGoal ? new float4(body.SeparationGoal, 1f) : float4.zero;
                _wantsToMoveBack[i] = body.WantsToMove ? (byte)1 : (byte)0;

                largestRadius = math.max(largestRadius, radius);
            }

            _largestRadius = largestRadius;
        }

        /*
         * Compared against what was last sent rather than against last frame's array, so a body that is added or
         * swapped into a slot inherits no history from whoever was there before it.
         */
        private void NotifySettledChanges()
        {
            /*
             * Deliberately not skipped when blocking is off. The solve writes a zero into every slot in that case,
             * so running this is what delivers the unblock to bodies that were stopped when the setting changed.
             * Returning early here instead left every one of them stopped for good, with nothing that would ever
             * tell them otherwise.
             */
            int count = math.min(_scheduledCount, _bodies.Count);

            for (int i = 0; i < count; i++)
            {
                bool blocked = _settledFront[i] != 0;
                if (_settledNotified[i] == blocked) continue;

                _settledNotified[i] = blocked;
                _bodies[i].SetSeparationSettled(blocked);
            }
        }

        private void Schedule(JobHandle dependency, float deltaTime)
        {
            _scheduledCount = _bodies.Count;

            if (_scheduledCount == 0)
            {
                dependency.Complete();
                return;
            }

            float cellSize = CellSize;
            float3 planeMask = MaskFor(_settings.Plane);

            _grid.Clear();

            SpatialHashBuildJob buildJob = new SpatialHashBuildJob
            {
                Positions = _positionsBack,
                CellSize = cellSize,
                PlaneMask = planeMask,
                Grid = _grid.AsParallelWriter()
            };

            SeparationPushJob pushJob = new SeparationPushJob
            {
                Positions = _positionsBack,
                Radii = _radiiBack,
                Goals = _goalsBack,
                WantsToMove = _wantsToMoveBack,
                SettledPrevious = _settledFront,
                Grid = _grid,
                CellSize = cellSize,
                PlaneMask = planeMask,
                CellRange = (int3)planeMask,
                PushStrength = _settings.PushStrength,
                MaxPushSpeed = _settings.MaxPushSpeed,
                InverseDeltaTime = deltaTime > 0f ? 1f / deltaTime : 0f,
                ResolveFraction = _settings.ResolveFraction,
                DetourEnabled = _settings.DetourEnabled,
                DetourStrength = _settings.DetourStrength,
                DetourDensityWeight = _settings.DetourDensityWeight,
                BlockingEnabled = _settings.BlockingEnabled,
                BlockingNeighbours = _settings.BlockingNeighbours,
                SettledHoldTicks = _settings.SettledHoldTicks,
                SettledPushScale = _settings.SettledPushScale,
                Push = _pushBack,
                SettledHold = _settledHold,
                Settled = _settledBack
            };

            _handle = pushJob.Schedule(_scheduledCount, PUSH_BATCH, buildJob.Schedule(_scheduledCount, BUILD_BATCH, dependency));
            _scheduled = true;

            (_positionsFront, _positionsBack) = (_positionsBack, _positionsFront);
            (_radiiFront, _radiiBack) = (_radiiBack, _radiiFront);
            (_goalsFront, _goalsBack) = (_goalsBack, _goalsFront);
            (_wantsToMoveFront, _wantsToMoveBack) = (_wantsToMoveBack, _wantsToMoveFront);
        }

        /*
         * Growth is doubling rather than exact, because a spawn wave arrives one agent at a time and reallocating
         * a dozen native containers per agent would cost more than the separation it is there to serve.
         * Only ever called with no job in flight.
         */
        private void EnsureCapacity(int count)
        {
            if (count <= _capacity) return;

            int capacity = math.max(_capacity * 2, math.max(count, MINIMUM_CAPACITY));

            NativeArray<float3> positionsFront = new NativeArray<float3>(capacity, Allocator.Persistent);
            NativeArray<float3> positionsBack = new NativeArray<float3>(capacity, Allocator.Persistent);
            NativeArray<float> radiiFront = new NativeArray<float>(capacity, Allocator.Persistent);
            NativeArray<float> radiiBack = new NativeArray<float>(capacity, Allocator.Persistent);
            NativeArray<float3> pushFront = new NativeArray<float3>(capacity, Allocator.Persistent);
            NativeArray<float3> pushBack = new NativeArray<float3>(capacity, Allocator.Persistent);
            NativeArray<float4> goalsFront = new NativeArray<float4>(capacity, Allocator.Persistent);
            NativeArray<float4> goalsBack = new NativeArray<float4>(capacity, Allocator.Persistent);
            NativeArray<byte> wantsToMoveFront = new NativeArray<byte>(capacity, Allocator.Persistent);
            NativeArray<byte> wantsToMoveBack = new NativeArray<byte>(capacity, Allocator.Persistent);
            NativeArray<byte> settledFront = new NativeArray<byte>(capacity, Allocator.Persistent);
            NativeArray<byte> settledBack = new NativeArray<byte>(capacity, Allocator.Persistent);
            NativeArray<byte> settledHold = new NativeArray<byte>(capacity, Allocator.Persistent);

            /*
             * Positions and offsets carry over because both describe where bodies already are. Dropping the offsets
             * on a resize would snap every agent that was being held apart back onto its agent position at once,
             * which is exactly the visible pop this whole system exists to avoid.
             */
            if (_capacity > 0)
            {
                NativeArray<float3>.Copy(_positionsFront, positionsFront, _capacity);
                NativeArray<float3>.Copy(_positionsBack, positionsBack, _capacity);
                NativeArray<float3>.Copy(_pushFront, pushFront, _capacity);
                NativeArray<float3>.Copy(_pushBack, pushBack, _capacity);
                NativeArray<float4>.Copy(_goalsFront, goalsFront, _capacity);
                NativeArray<float4>.Copy(_goalsBack, goalsBack, _capacity);
                NativeArray<byte>.Copy(_wantsToMoveFront, wantsToMoveFront, _capacity);
                NativeArray<byte>.Copy(_wantsToMoveBack, wantsToMoveBack, _capacity);
                NativeArray<byte>.Copy(_settledFront, settledFront, _capacity);
                NativeArray<byte>.Copy(_settledBack, settledBack, _capacity);
                NativeArray<byte>.Copy(_settledHold, settledHold, _capacity);
            }

            System.Array.Resize(ref _settledNotified, capacity);
            System.Array.Resize(ref _pushed, capacity);

            DisposeBuffers();

            _positionsFront = positionsFront;
            _positionsBack = positionsBack;
            _radiiFront = radiiFront;
            _radiiBack = radiiBack;
            _pushFront = pushFront;
            _pushBack = pushBack;
            _goalsFront = goalsFront;
            _goalsBack = goalsBack;
            _wantsToMoveFront = wantsToMoveFront;
            _wantsToMoveBack = wantsToMoveBack;
            _settledFront = settledFront;
            _settledBack = settledBack;
            _settledHold = settledHold;
            _grid = new NativeParallelMultiHashMap<int, int>(capacity, Allocator.Persistent);
            _capacity = capacity;
        }

        private void DisposeBuffers()
        {
            if (_positionsFront.IsCreated) _positionsFront.Dispose();
            if (_positionsBack.IsCreated) _positionsBack.Dispose();
            if (_radiiFront.IsCreated) _radiiFront.Dispose();
            if (_radiiBack.IsCreated) _radiiBack.Dispose();
            if (_pushFront.IsCreated) _pushFront.Dispose();
            if (_pushBack.IsCreated) _pushBack.Dispose();
            if (_goalsFront.IsCreated) _goalsFront.Dispose();
            if (_goalsBack.IsCreated) _goalsBack.Dispose();
            if (_wantsToMoveFront.IsCreated) _wantsToMoveFront.Dispose();
            if (_wantsToMoveBack.IsCreated) _wantsToMoveBack.Dispose();
            if (_settledFront.IsCreated) _settledFront.Dispose();
            if (_settledBack.IsCreated) _settledBack.Dispose();
            if (_settledHold.IsCreated) _settledHold.Dispose();
            if (_grid.IsCreated) _grid.Dispose();
        }

    }
}

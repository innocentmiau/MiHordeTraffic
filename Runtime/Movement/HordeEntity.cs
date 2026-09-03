using System.Collections;
using MiHordeTraffic.Pathing;
using UnityEngine;
using UnityEngine.AI;

namespace MiHordeTraffic.Movement
{
    /*
     * The chase behaviour a horde needs and nothing else: walk at a target, repath now and then, and be able to stop dead while an attack or a stagger plays out.
     * It owns the pathfinding and leaves crowd spacing entirely to HordeAgent, so neither has to know what the other is doing.
     *
     * Repathing is on an interval rather than every frame because SetDestination queues a path request,
     * and a thousand agents asking every frame is a thousand requests a frame for a target that has moved a few centimetres.
     * The countdown starts at a random point in its own interval so a wave spawned in one frame does not then repath in lockstep forever,
     * which turns a smooth cost into a spike every interval.
     *
     * Freezing sets isStopped rather than disabling the agent. A disabled agent leaves the navmesh and has to be warped back,
     * and anything that was standing on it during those frames pathed through where it used to be.
     * isStopped keeps the agent in place and on the mesh, and stops the steering and path following that make up most of what an idle agent costs.
     */
    /// <summary>
    /// Chases a target with NavMeshAgent pathfinding, with freeze and resume for when something else takes over.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [DisallowMultipleComponent]
    public class HordeEntity : MonoBehaviour, IRepathBody, IHordeTargetable
    {

        private const float FACING_TOLERANCE = .5f;
        private const float FACING_EPSILON_SQUARED = .0001f;

        [SerializeField] private NavMeshAgent agent;
        [SerializeField] private HordeAgent separation;

        [Header("Pathfinding")]
        [SerializeField, Min(0f)] private float repathInterval = .1f;
        [SerializeField, Min(0f)] private float repathThreshold = .1f;
        [SerializeField, Min(0f)] private float arriveDistance = 1.5f;
        [SerializeField] private bool staggerFirstRepath = true;

        [Header("Settling")]
        [SerializeField] private bool faceTargetWhenSettled = true;

        [Header("Freezing")]

        private Transform _target;
        private Vector3 _requestedDestination = Vector3.positiveInfinity;
        private float _repathCountdown;
        private bool _frozen;
        private bool _settled;
        /*
         * Cached as a plain bool because the tick asks it every frame for every body. Comparing a UnityEngine.Object
         * against null calls out to native to ask whether the native half is still alive, which is the cost this
         * whole component exists to keep out of the per body path. Reading a property on a reference already known
         * to be good is managed only.
         */
        private bool _hasSeparation;

        private int _repathIndex = -1;
        private float _nextRepathTime;
        private float _lastRepathTime;
        private Vector3 _facingTarget;
        private bool _facing;

        /// <summary>
        /// Slot this entity occupies in the scheduler's roster. Written by the HordePathScheduler only.
        /// </summary>
        public int RepathIndex
        {
            get => _repathIndex;
            set => _repathIndex = value;
        }

        /// <summary>
        /// When this entity is next due a repath. Written by the HordePathScheduler only.
        /// </summary>
        public float NextRepathTime
        {
            get => _nextRepathTime;
            set => _nextRepathTime = value;
        }

        /// <summary>
        /// When this entity was last repathed. Written by the HordePathScheduler only.
        /// </summary>
        public float LastRepathTime
        {
            get => _lastRepathTime;
            set => _lastRepathTime = value;
        }

        /// <summary>
        /// Whether repathing this entity would mean anything right now.
        /// </summary>
        public bool WantsRepath => !_frozen && _target && agent && agent.enabled;

        /// <summary>
        /// The agent doing the pathfinding.
        /// </summary>
        public NavMeshAgent Agent => agent;

        /// <summary>
        /// The separation component this entity leaves crowd spacing to, which may be null.
        /// </summary>
        public HordeAgent Separation => separation;

        /// <summary>
        /// Who this entity is walking towards, or null when it is heading to a fixed point or nowhere.
        /// </summary>
        public Transform Target => _target;

        /// <summary>
        /// Whether movement and repathing are currently suspended by a Freeze call.
        /// </summary>
        public bool IsFrozen => _frozen;

        /// <summary>
        /// Whether the separation system has settled this entity, so it stops pressing into the crowd ahead.
        /// Unlike freezing this is not something the game asked for, and it clears itself when the crowd does.
        /// </summary>
        public bool IsSettled => _settled;

        /*
         * Arrival lives here rather than in the separation settings, because it is a property of what this entity
         * is doing and not of the crowd it happens to be standing in. A group of archers that stop at range and a
         * group of melee that walk into contact want different answers and can share one separation profile.
         */
        /// <summary>
        /// Whether this entity is currently trying to advance, which is what the separation system settles on.
        /// </summary>
        public bool WantsToMove => !_frozen && _target && !HasArrived();

        /// <summary>
        /// Sets who this entity chases. It repaths on the next frame rather than waiting out the current interval.
        /// </summary>
        /// <param name="target">The transform to follow, or null to stop chasing.</param>
        public void SetTarget(Transform target)
        {
            _target = target;
            _requestedDestination = Vector3.positiveInfinity;
            _repathCountdown = 0f;
        }

        /// <summary>
        /// Sends this entity to a fixed point instead of a moving target, which clears whatever it was chasing.
        /// </summary>
        /// <param name="position">Where to walk to.</param>
        public void SetDestination(Vector3 position)
        {
            _target = null;
            _requestedDestination = position;

            if (CanPath()) agent.SetDestination(position);
        }

        /// <summary>
        /// Stops chasing and clears the current path, leaving the entity standing where it is.
        /// </summary>
        public void ClearTarget()
        {
            _target = null;
            _requestedDestination = Vector3.positiveInfinity;

            if (CanPath()) agent.ResetPath();
        }

        /*
         * The distance handed back is what paces this entity, so a settled one reports itself as far away however
         * close to the target it is standing. It is not going anywhere, and spending the frame's budget on the rank
         * that has already arrived is exactly the waste the scheduler exists to avoid.
         */
        /// <summary>
        /// Recomputes the path, called by the scheduler when this entity's turn comes up.
        /// </summary>
        /// <param name="time">The current time.</param>
        /// <returns>Distance to the goal, used to decide how soon to come back.</returns>
        public float Repath(float time)
        {
            if (!_target || !CanPath()) return float.MaxValue;

            Vector3 destination = _target.position;
            float distance = Vector3.Distance(destination, transform.position);

            if (Vector3.Distance(destination, _requestedDestination) > repathThreshold)
            {
                _requestedDestination = destination;
                agent.SetDestination(destination);
            }

            return _settled ? float.MaxValue : distance;
        }

        /// <summary>
        /// Repaths immediately, ignoring both the interval and the distance threshold.
        /// </summary>
        public void ForceRepath()
        {
            _requestedDestination = Vector3.positiveInfinity;
            _repathCountdown = repathInterval;
            _nextRepathTime = Time.time;
            Repath(Time.time);
        }

        /// <summary>
        /// Suspends movement and repathing, for an attack, a stagger or anything else that takes over the entity.
        /// The agent stays on the navmesh and keeps its place in the crowd.
        /// </summary>
        public void Freeze()
        {
            if (_frozen) return;

            _frozen = true;

            ApplyMovementState();

            /*
             * Handed to the agent rather than repeated here. It is the thing on the mover's roster, so it is the
             * only one that can take the body off it, and owning the decision in two places is how the two of them
             * end up disagreeing about whether a body is still being separated.
             */
            separation?.Freeze();
        }

        /// <summary>
        /// Resumes movement and repathing, pathing again on the next frame since the world moved while it was frozen.
        /// </summary>
        public void Resume()
        {
            if (!_frozen) return;

            _frozen = false;

            ApplyMovementState();

            separation?.Resume();

            if (_target) _requestedDestination = Vector3.positiveInfinity;
            _repathCountdown = 0f;
        }

        /// <summary>
        /// Freezes or resumes in one call, for driving from a state machine.
        /// </summary>
        /// <param name="value">True to freeze, false to resume.</param>
        public void SetFrozen(bool value)
        {
            if (value) Freeze();
            else Resume();
        }

        /// <summary>
        /// Moves the entity without letting the agent try to path there.
        /// </summary>
        /// <param name="position">Where to land, which must already be on the navmesh.</param>
        public void Warp(Vector3 position)
        {
            agent.Warp(position);
            _requestedDestination = Vector3.positiveInfinity;
        }

        /// <summary>
        /// Warps at the end of the frame, for a pooled entity that is not on the navmesh yet at the moment it is spawned.
        /// </summary>
        /// <param name="position">Where to land.</param>
        public void TeleportAgent(Vector3 position)
        {
            StopAllCoroutines();
            StartCoroutine(WarpAtEndOfFrame(position));
        }

        private void Reset()
        {
            agent = GetComponent<NavMeshAgent>();
            separation = GetComponent<HordeAgent>();
        }

        private void Awake()
        {
            agent = agent ? agent : GetComponent<NavMeshAgent>();
            separation = separation ? separation : GetComponent<HordeAgent>();
        }

        private void OnEnable()
        {
            _frozen = false;
            _settled = false;
            _requestedDestination = Vector3.positiveInfinity;
            _repathCountdown = staggerFirstRepath ? Random.value * repathInterval : 0f;

            _hasSeparation = separation;

            if (_hasSeparation) separation.SeparationSettledChanged += OnSeparationSettledChanged;

            HordePathScheduler.Register(this);

            /*
             * A pooled entity carries whatever isStopped it had when it was despawned, so the flags being reset
             * above is not enough on its own. One that was standing in a blocked crowd would come back frozen.
             */
            ApplyMovementState();
        }

        private void OnDisable()
        {
            if (separation) separation.SeparationSettledChanged -= OnSeparationSettledChanged;

            HordePathScheduler.Unregister(this);
        }

        /// <summary>
        /// The per frame work, driven by the HordePathScheduler rather than by a MonoBehaviour Update.
        /// </summary>
        /// <param name="deltaTime">Frame time.</param>
        public void TickBody(float deltaTime)
        {
            /*
             * Stood down while the mover is driving this particular body, which is a different question from which
             * mode the crowd is in and was written as though it were the same one. Everything past here belongs to
             * the mover for as long as it holds a body: the separation goal and the wants to move flag are what
             * separation reads to decide who has settled, and the mover publishes both every frame from what the
             * movement job measured, where RefreshGoal would publish them on a repath interval from what a
             * NavMeshAgent would have thought, and FaceTarget writes transform.rotation, which the job also writes.
             *
             * Asked of the body rather than of the static mode, because a body can be out of the mover's hands
             * while the crowd around it is still in flow mode. Disabling a HordeAgent is the ordinary way to take
             * one body off the field, and it is what the benchmark does to every body when it measures built in
             * avoidance against this. A disabled HordeAgent never registers, so nothing is moving that body, and a
             * gate on the mode alone stood its pathfinding down as well and left it standing still with nothing
             * driving it at all.
             *
             * FlowIndex is written when the mover takes a body onto its roster and cleared when it lets one go, so
             * it answers exactly the question being asked. It costs two managed reads, where the agent enabled
             * check underneath was a call out to native for every body on every frame.
             */
            if (_hasSeparation && separation.FlowIndex >= 0)
            {
                _facing = false;
                return;
            }

            if (!agent.enabled) return;

            if (_facing) FaceTarget();

            _repathCountdown -= deltaTime;
            if (_repathCountdown > 0f) return;

            _repathCountdown = repathInterval;

            /*
             * The goal is refreshed even while blocked or frozen, and only the path request is skipped.
             * Blocking clears when an entity stops being near its goal, so an entity that stopped updating where
             * that goal is would still believe it had arrived long after the target walked off, and the whole
             * crowd would stay standing in the shape of a target that is no longer there.
             */
            RefreshGoal();

        }

        private void OnSeparationSettledChanged(bool settled)
        {
            _settled = settled;
            _facing = false;

            ApplyMovementState();

            if (settled) return;

            /*
             * Coming unsettled repaths straight away rather than waiting out the interval. A settled entity reports
             * itself as infinitely far away so the scheduler paces it as slowly as it can, so without this it would
             * sit out a full far interval before getting a path, which is the crowd visibly lagging behind a target
             * that has already moved on.
             */
            _requestedDestination = Vector3.positiveInfinity;
            _nextRepathTime = Time.time;
        }

        /// <summary>
        /// Puts the agent into whatever movement state the freeze and blocked flags currently add up to.
        /// </summary>
        private void ApplyMovementState()
        {
            if (!CanPath()) return;

            agent.isStopped = _frozen || _settled;
        }

        private void RefreshGoal()
        {
            if (!separation) return;

            if (_target) separation.SetSeparationGoal(_target.position);
            else if (float.IsFinite(_requestedDestination.x)) separation.SetSeparationGoal(_requestedDestination);
            else separation.ClearSeparationGoal();

            separation.SetWantsToMove(WantsToMove);

            /*
             * A settled entity has its agent stopped, and a stopped agent does not turn either. Standing in the
             * right place facing where the target used to be reads as having lost track of it, even though staying
             * put is the correct answer. Aiming is latched here rather than run every frame, so an entity already
             * facing the right way costs nothing at all.
             */
            if (!faceTargetWhenSettled || !_settled || !_target) return;

            _facingTarget = _target.position;
            _facing = true;
        }

        /// <summary>
        /// Turns towards the last seen target position, until facing it closely enough to stop.
        /// </summary>
        private void FaceTarget()
        {
            Vector3 delta = _facingTarget - transform.position;
            delta.y = 0f;

            if (delta.sqrMagnitude < FACING_EPSILON_SQUARED)
            {
                _facing = false;
                return;
            }

            Quaternion desired = Quaternion.LookRotation(delta);

            transform.rotation = Quaternion.RotateTowards(transform.rotation, desired, agent.angularSpeed * Time.deltaTime);

            if (Quaternion.Angle(transform.rotation, desired) <= FACING_TOLERANCE) _facing = false;
        }

        /// <summary>
        /// Whether this entity is close enough to what it is chasing to stop advancing on it.
        /// </summary>
        private bool HasArrived()
        {
            if (!_target) return true;

            return (_target.position - transform.position).sqrMagnitude <= arriveDistance * arriveDistance;
        }

        private bool CanPath() => agent && agent.enabled && agent.isOnNavMesh;

        private IEnumerator WarpAtEndOfFrame(Vector3 position)
        {
            yield return new WaitForEndOfFrame();
            Warp(position);
        }

    }
}

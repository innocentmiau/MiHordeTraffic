using System;
using System.Collections.Generic;
using MiHordeTraffic.Pathing.FlowField;
using MiHordeTraffic.Separation;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;

namespace MiHordeTraffic.Movement
{
    /*
     * One component per body, because separating a crowd and moving it along a field are two halves of the same
     * job and were never usefully separate. Split across two components they had to hold references to each other,
     * agree about which of them owned the push, and be added in pairs that nothing checked for, and every one of
     * those was a way to end up with a body that looks configured and does nothing.
     *
     * The NavMeshAgent is optional, which is the point of the merge. Under flow movement the apply mode is NONE,
     * and NONE never touches the agent at all, so an agent on every body is a component that is simulated,
     * synchronised and disabled again for no reason. It is only genuinely needed when the push is handed back
     * through the navmesh, or when the scheduler is running the NavMeshAgent technique, and it is added on demand
     * in those cases rather than required up front.
     *
     * Switching technique flips a mode rather than disabling this component. Disabling it would take the body out
     * of separation as well, and the whole value of the NavMeshAgent baseline is that it keeps the same separation
     * the flow field gets, so that what is being compared is the pathing and nothing else.
     */
    /// <summary>
    /// Makes a body part of the horde: separated from its neighbours, and moved by the flow field or its own agent.
    /// </summary>
    [DisallowMultipleComponent]
    public class HordeAgent : MonoBehaviour, ISeparationBody
    {

        /*
         * Every agent that exists, enabled or not, so the technique switch can reach all of them. The mover's own
         * roster only holds the ones currently under the field, which is the wrong set to ask when switching back.
         *
         * Each agent remembers where it sits and leaves by swapping the last one into its place, rather than by
         * List.Remove. Remove is a linear scan, and on a UnityEngine.Object it is a linear scan whose comparison
         * calls out to native, so tearing down a crowd of five thousand was five thousand scans of an average of
         * two and a half thousand entries: about twelve million of those comparisons, all of it in the frame the
         * scene unloads or a wave despawns. This is the same swap back the mover and separation already use.
         */
        private static readonly List<HordeAgent> ALL = new List<HordeAgent>();

        private static HordeMovementMode _mode = HordeMovementMode.FLOW_FIELD;
        private static bool _warnedAboutMissingAgent;
        private static int _agentsAddedAtRuntime;

        [Header("Separation")]
        [Tooltip("Which crowd this body separates within. Leave empty for the system's default settings.")]
        [SerializeField] private SeparationSettings group;

        [Tooltip("Metres of room this body wants around itself. Kept separate from the navmesh radius, which is also wall clearance.")]
        [SerializeField, Min(.01f)] private float separationRadius = .5f;

        [Tooltip("How the push is handed back. NONE is correct under flow movement and needs no NavMeshAgent.")]
        [SerializeField] private SeparationApplyMode applyMode = SeparationApplyMode.NAVMESH_MOVE;

        [Header("Movement")]
        [Tooltip("Metres per second this body walks along the field.")]
        [SerializeField, Min(0f)] private float speed = 3.5f;

        [Tooltip("Keep being pushed apart from neighbours while frozen. Off is cheaper, but a frozen body stops holding its ground and the crowd closes over it.")]
        [SerializeField] private bool separateWhileFrozen = true;

        [Header("NavMesh")]
        [Tooltip("Only used when this body is driven by a NavMeshAgent. Left empty it is found, and added if there is none.")]
        [SerializeField] private NavMeshAgent agent;

        [Tooltip("Turn Unity's own local avoidance off on the agent, since separation replaces it.")]
        [SerializeField] private bool disableBuiltInAvoidance = true;

        [Header("Debug")]
        [SerializeField] private bool drawRoute = true;
        [SerializeField, Min(1)] private int routeSteps = 400;

        private int _allIndex = -1;
        private int _separationIndex = -1;
        private bool _separationEnabled = true;
        private bool _settled;
        private bool _wantsToMove = true;
        private bool _hasGoal;
        private float3 _lastPush;
        private Vector3 _goal;
        private bool _frozen;

        /// <summary>
        /// Raised when the system decides this agent is or is no longer walled in by neighbours that already
        /// stopped trying to reach the same goal. Only raised on a change, never every frame.
        /// </summary>
        public event Action<bool> SeparationSettledChanged;

        /// <summary>
        /// What is currently moving every horde agent.
        /// </summary>
        public static HordeMovementMode Mode => _mode;

        /// <summary>
        /// How many horde agents exist, enabled or not.
        /// </summary>
        public static int AgentCount => ALL.Count;

        /// <summary>
        /// How many bodies have had a NavMeshAgent added for them at runtime, which is a cost worth removing.
        /// </summary>
        public static int AgentsAddedAtRuntime => _agentsAddedAtRuntime;

        /// <summary>
        /// Slot this agent occupies in its group's arrays. Written by the SeparationSystem only.
        /// </summary>
        public int SeparationIndex
        {
            get => _separationIndex;
            set => _separationIndex = value;
        }

        /// <summary>
        /// Slot this body occupies in the mover's roster, or -1 while it is not registered. Only the mover writes this.
        /// </summary>
        public int FlowIndex { get; set; } = -1;

        /// <summary>
        /// The settings asset naming which crowd this agent separates within, or null for the system's default.
        /// </summary>
        public SeparationSettings GroupSettings => group;

        /// <summary>
        /// How much room this agent wants around itself, independent of the navmesh radius used for path clearance.
        /// </summary>
        public float SeparationRadius => separationRadius;

        /// <summary>
        /// The transform the system samples from, and writes to in the batched hand off.
        /// </summary>
        public Transform SeparationTransform => transform;

        /// <summary>
        /// Whether this agent is currently taking part in separation at all.
        /// </summary>
        public bool SeparationEnabled => _separationEnabled;

        /// <summary>
        /// How fast this body walks along the field.
        /// </summary>
        public float Speed => speed;

        /// <summary>
        /// Whether movement is currently suspended. A frozen body still occupies the ground it stands on.
        /// </summary>
        public bool IsFrozen => _frozen;

        /// <summary>
        /// Whether this agent is currently trying to advance.
        /// </summary>
        public bool WantsToMove => _wantsToMove;

        /// <summary>
        /// Whether the system has settled this agent.
        /// </summary>
        public bool IsSeparationSettled => _settled;

        /// <summary>
        /// Whether anything has told this agent where it is heading.
        /// </summary>
        public bool HasSeparationGoal => _hasGoal;

        /// <summary>
        /// Where this agent is trying to get to.
        /// </summary>
        public float3 SeparationGoal => _goal;

        /// <summary>
        /// The most recent push the system computed for this agent, whether or not the apply mode used it.
        /// </summary>
        public float3 LastPush => _lastPush;

        /// <summary>
        /// How this agent hands its push back. Always NONE under flow movement, whatever is authored.
        /// </summary>
        public SeparationApplyMode ApplyMode => _mode == HordeMovementMode.FLOW_FIELD ? SeparationApplyMode.NONE : applyMode;

        /// <summary>
        /// The NavMeshAgent driving this body, which is null and unnecessary under flow movement.
        /// </summary>
        public NavMeshAgent Agent => agent;

        /*
         * A mode rather than an enable, so switching technique does not take the crowd out of separation on the way
         * past. The baseline is only worth measuring if it keeps the same separation the flow field had.
         */
        /// <summary>
        /// Switches every horde agent between being moved by the field and being moved by its own NavMeshAgent.
        /// </summary>
        /// <param name="value">What should move the bodies from now on.</param>
        public static void SetAllMode(HordeMovementMode value)
        {
            _mode = value;

            for (int i = 0; i < ALL.Count; i++)
                ALL[i].ApplyMode_Internal();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ALL.Clear();
            _mode = HordeMovementMode.FLOW_FIELD;
            _warnedAboutMissingAgent = false;
            _agentsAddedAtRuntime = 0;
        }

        /// <summary>
        /// Sets how fast this body walks along the field.
        /// </summary>
        /// <param name="value">Speed in world units per second.</param>
        public void SetSpeed(float value) => speed = Mathf.Max(0f, value);

        /*
         * The body stays on the mover's roster and the movement job returns early for it, rather than the body
         * being unregistered. Taking it off the roster is the obvious way to make it free and it is wrong: the
         * roster is also what the congestion pass counts, so an unregistered body stops occupying the cell it is
         * standing in. Nothing would then route around it, nothing would slow for it, and the crowd would walk
         * straight into a body it cannot see and be stopped only by separation shoving it. A frozen body has to go
         * on taking up space, because taking up space is most of what it is doing.
         *
         * Returning early costs a transform read and a branch, and skips everything that actually costs: the field
         * lookup, the turn, the speed resolve, the wall checks, the rotation, and the transform write, which is the
         * expensive half of any job that touches a hierarchy. The saving is nearly all of it.
         *
         * Separation is left running by default, because a frozen body that stops being separated stops holding
         * its ground and the crowd closes over the top of it. Turning it off is cheaper and correct for something
         * that should be walked through, such as a corpse.
         */
        /// <summary>
        /// Suspends movement. The body keeps its place in the crowd and still blocks the ground it stands on.
        /// </summary>
        public void Freeze()
        {
            if (_frozen) return;

            _frozen = true;

            /*
             * Told rather than inferred, so the neighbours behind settle against it instead of grinding, which is
             * the same signal arrival publishes.
             */
            SetWantsToMove(false);

            if (!separateWhileFrozen) SetSeparationEnabled(false);

            StopAgent(true);
        }

        /// <summary>
        /// Resumes movement.
        /// </summary>
        public void Resume()
        {
            if (!_frozen) return;

            _frozen = false;

            if (!separateWhileFrozen) SetSeparationEnabled(true);

            SetWantsToMove(true);
            StopAgent(false);
        }

        private void StopAgent(bool stopped)
        {
            if (!agent || !agent.isActiveAndEnabled || !agent.isOnNavMesh) return;

            agent.isStopped = stopped;
        }

        /// <summary>
        /// Tells the system where this agent is heading, so it can work out whether the neighbours in its way
        /// are between it and that goal.
        /// </summary>
        /// <param name="value">The world position the agent is trying to reach.</param>
        public void SetSeparationGoal(Vector3 value)
        {
            _goal = value;
            _hasGoal = true;
        }

        /// <summary>
        /// Forgets the goal, which reads as this agent having nowhere in particular to be.
        /// </summary>
        public void ClearSeparationGoal() => _hasGoal = false;

        /// <summary>
        /// Tells the system whether this agent is trying to advance.
        /// </summary>
        /// <param name="value">False when the agent is stopped, arrived, staggered or otherwise not advancing.</param>
        public void SetWantsToMove(bool value) => _wantsToMove = value;

        /// <summary>
        /// Called by the system when this agent's settled state changes.
        /// </summary>
        /// <param name="settled">True when the agent should stop trying to advance.</param>
        public void SetSeparationSettled(bool settled)
        {
            if (_settled == settled) return;

            _settled = settled;
            SeparationSettledChanged?.Invoke(settled);
        }

        /// <summary>
        /// Applies the push the system computed for this agent.
        /// </summary>
        /// <param name="push">Push velocity in world units per second.</param>
        /// <param name="deltaTime">Frame time to scale the push by.</param>
        public void ApplySeparationPush(float3 push, float deltaTime)
        {
            /*
             * Stored whether or not it is applied here, because under flow movement the push is an input to the
             * movement job rather than something handed to an agent.
             */
            _lastPush = push;

            if (ApplyMode == SeparationApplyMode.NONE) return;

            agent.ApplySeparationPush(push, deltaTime, applyMode);
        }

        /// <summary>
        /// Drops this agent out of separation entirely, or puts it back in.
        /// </summary>
        /// <param name="value">True to take part, false to drop out.</param>
        public void SetSeparationEnabled(bool value)
        {
            if (_separationEnabled == value) return;

            _separationEnabled = value;

            if (!isActiveAndEnabled) return;

            if (value) SeparationSystem.Register(this);
            else SeparationSystem.Unregister(this);
        }

        /// <summary>
        /// Moves this agent into a different crowd, re-registering it so the change takes effect this frame.
        /// </summary>
        /// <param name="value">The settings asset naming the group to join, or null for the system's default.</param>
        public void SetGroup(SeparationSettings value)
        {
            if (group == value) return;

            bool wasRegistered = _separationEnabled && isActiveAndEnabled;

            if (wasRegistered) SeparationSystem.Unregister(this);

            group = value;

            if (wasRegistered) SeparationSystem.Register(this);
        }

        /// <summary>
        /// Switches the hand off at runtime, so a benchmark can compare them without respawning the crowd.
        /// </summary>
        /// <param name="value">The hand off to use from now on.</param>
        public void SetApplyMode(SeparationApplyMode value) => applyMode = value;

        [ContextMenu("Match Radius To Agent")]
        private void MatchRadiusToAgent()
        {
            agent = agent ? agent : GetComponent<NavMeshAgent>();
            separationRadius = agent ? agent.radius : separationRadius;
        }

        private void Reset() => MatchRadiusToAgent();

        private void Awake()
        {
            agent = agent ? agent : GetComponent<NavMeshAgent>();

            _allIndex = ALL.Count;
            ALL.Add(this);
        }

        private void OnDestroy()
        {
            /*
             * Guarded because the statics are cleared on a domain reload while the agents that were in them are
             * still alive, so an agent destroyed after that reset holds an index into a list that no longer has it.
             */
            if (_allIndex < 0 || _allIndex >= ALL.Count || ALL[_allIndex] != this) return;

            int last = ALL.Count - 1;

            ALL[_allIndex] = ALL[last];
            ALL[_allIndex]._allIndex = _allIndex;
            ALL.RemoveAt(last);

            _allIndex = -1;
        }

        private void OnEnable()
        {
            if (_separationEnabled) SeparationSystem.Register(this);

            ApplyMode_Internal();
        }

        private void OnDisable()
        {
            if (_separationEnabled) SeparationSystem.Unregister(this);

            HordeFlowMovement.Unregister(this);

            /*
             * Cleared on the way out so a pooled agent does not come back still believing it is walled in by a
             * crowd that was standing somewhere else entirely. Raised rather than assigned, because whatever
             * stopped the agent on the way in is listening for this and will otherwise leave it stopped forever.
             */
            SetSeparationSettled(false);
            _wantsToMove = true;
            _hasGoal = false;
            _frozen = false;
        }

        /*
         * Everything that has to change when the mode does, in one place, so a body that spawns mid session lands
         * on the same side of the switch as the ones already running. Getting that wrong meant a crowd that was
         * quietly half on each system, with the newcomers following a field nothing was rebuilding.
         */
        private void ApplyMode_Internal()
        {
            bool flow = _mode == HordeMovementMode.FLOW_FIELD;

            if (flow)
            {
                /*
                 * A disabled component is not taking part, so the field half is skipped for it entirely. Switching
                 * off a NavMeshAgent that belongs to a body this component is not driving would leave that body
                 * with nothing moving it, which is the exact trap the other branch has to undo.
                 */
                if (!isActiveAndEnabled) return;

                if (agent) agent.enabled = false;

                HordeFlowMovement.Register(this);
                return;
            }

            HordeFlowMovement.Unregister(this);

            /*
             * The restore below runs whether or not this component is enabled, unlike the field half above, and
             * that asymmetry is the point. Disabling a HordeAgent is how a body is taken off the field one at a
             * time, and it is what the benchmark does to the whole crowd when it measures built in avoidance.
             * The body's NavMeshAgent was switched off by flow mode, and the only thing that ever switches it back
             * on is this method, so bailing on the disabled component left the agent off for good and the body
             * standing still with neither system driving it. Switching the crowd back to NavMeshAgent movement
             * then changed nothing, because every one of those bodies took the same early return again.
             *
             * Adding a missing agent is still skipped for a disabled component. Putting one back is repairing
             * something this component broke; creating one is joining in, and a body that has been taken off the
             * field is not asking to be.
             */
            if (isActiveAndEnabled) EnsureAgent();

            if (!agent) return;

            agent.enabled = true;

            if (disableBuiltInAvoidance) agent.DisableBuiltInAvoidance();

            StopAgent(_frozen);
        }

        /*
         * Added rather than demanded, and warned about once rather than per body. A NavMeshAgent is only needed
         * when something actually hands the push through it, which under flow movement is never, so requiring one
         * on every prefab is a component per body that exists to be switched off.
         *
         * Adding one at runtime is a real cost on a crowd that spawns constantly, which is why the warning says to
         * put it on the prefab rather than treating this as the fix. It is also not always enough: an agent added
         * where there is no navmesh underfoot reports isOnNavMesh false and the push still goes nowhere, so the
         * warning has to say that too or the same afternoon gets spent twice.
         */
        private void EnsureAgent()
        {
            if (agent) return;

            agent = GetComponent<NavMeshAgent>();

            if (agent) return;

            agent = gameObject.AddComponent<NavMeshAgent>();
            _agentsAddedAtRuntime++;

            if (_warnedAboutMissingAgent) return;

            _warnedAboutMissingAgent = true;

            Debug.LogWarning(
                $"[MiHordeTraffic] A horde body needed a NavMeshAgent and had none, so one was added at runtime. " +
                $"Add a NavMeshAgent to the prefab instead: adding components while spawning is expensive, and an agent " +
                $"added onto ground with no navmesh under it reports isOnNavMesh false and the push still goes nowhere.",
                this);
        }

        /*
         * Traced by walking the field forward from the cell this body is standing in, one step at a time, the same
         * way the body itself will. That is the difference between guessing what a stuck body thinks and being
         * shown it: a route that runs off the wrong way, doubles back, or stops dead after two cells says exactly
         * which of those three things went wrong, and none of them are distinguishable from watching the crowd.
         */
        private void OnDrawGizmosSelected()
        {
            if (!drawRoute || !Application.isPlaying) return;

            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;
            if (!driver || driver.Field == null || !driver.Field.IsBuilt) return;

            GridFlowField field = driver.Field;
            HordeGridInfo grid = field.Grid;

            float3 position = transform.position;
            int cell = grid.IndexOf(position);

            /*
             * The state of the cell underfoot comes first, because most stuck bodies are stuck for one of these
             * three reasons and they look identical from outside.
             */
            if (cell < 0)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(position, 1f);
                return;
            }

            bool walkable = field.IsWalkable(cell);
            bool reachable = walkable && field.Integration[cell] != float.MaxValue;

            Gizmos.color = !walkable ? Color.red : reachable ? Color.green : new Color(1f, .5f, 0f);
            Gizmos.DrawWireCube(grid.CentreOf(grid.CellAt(cell)), new Vector3(grid.CellSize, .1f, grid.CellSize));

            if (!reachable) return;

            Gizmos.color = Color.yellow;

            float3 point = position;

            for (int i = 0; i < routeSteps; i++)
            {
                float2 flow = field.Flow[cell];
                if (flow.Equals(float2.zero)) break;

                int2 next = grid.CellAt(cell) + new int2((int)math.round(flow.x), (int)math.round(flow.y));
                if (!grid.Contains(next)) break;

                int index = grid.IndexOf(next);
                if (!field.IsWalkable(index)) break;

                float3 centre = grid.CentreOf(grid.CellAt(index));
                centre.y = field.HeightAt(index) + .2f;

                Gizmos.DrawLine(point, centre);

                point = centre;
                cell = index;
            }

            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(point, .5f);

            DrawState(field, position, grid.IndexOf(position));
        }

        /*
         * The three forces that decide where a body actually goes, drawn on top of each other so which one is
         * winning is visible rather than inferred. A body that will not follow a correct route is being overpowered
         * by one of them, and from outside they all look the same.
         */
        private void DrawState(GridFlowField field, float3 position, int cell)
        {
            float3 origin = position + new float3(0f, 1.5f, 0f);

            if (cell >= 0)
            {
                float2 flow = field.Flow[cell];

                Gizmos.color = Color.green;
                Gizmos.DrawLine(origin, origin + new float3(flow.x, 0f, flow.y) * 2f);
            }

            if (!HordeFlowMovement.Instance || !HordeFlowMovement.Instance.TryGetState(FlowIndex, out float2 heading, out float3 push)) return;

            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(origin, origin + new float3(heading.x, 0f, heading.y) * 2f);

            /*
             * Drawn to the same scale as the others, so a push long enough to cancel the walk is obviously long
             * enough rather than a judgement call.
             */
            Gizmos.color = Color.red;
            Gizmos.DrawLine(origin, origin + push / math.max(speed, .01f) * 2f);
        }


    }
}

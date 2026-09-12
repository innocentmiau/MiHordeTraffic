using MiHordeTraffic.Movement;
using UnityEngine;

namespace MiHordeTraffic.Samples.FlowFieldCrowd
{
    /*
     * Not required for anything. A body walks with the crowd with nothing on it but a HordeAgent, and deleting this
     * script changes none of that. It exists because the two questions that come straight after "it walks" are how
     * to tell when a body got there and what pooling needs, and both are shorter to read as code.
     *
     * Nothing here sets a destination, and nothing should. The flow field walks every body towards the driver's
     * Target, and HordeFlowMovement writes the separation goal and the wants to move flag itself, once a frame,
     * for every body it drives. A script that also writes either of them is overwriting the mover's answer with a
     * worse one, and it is the most common way a crowd ends up behaving strangely with nothing obviously wrong.
     *
     * Pooling needs nothing at all. HordeAgent joins the crowd in OnEnable and leaves it in OnDisable, so a body
     * deactivated on its way back to the pool has already left by the time the pool has it, and rejoins where it
     * is next activated. There is no register call to remember and no slot to release.
     */
    /// <summary>
    /// Reacts to a flow field body arriving, as an example of what a game adds on top of HordeAgent.
    /// </summary>
    [RequireComponent(typeof(HordeAgent))]
    public class FlowCrowdBody : MonoBehaviour
    {

        [SerializeField] private HordeAgent agent;

        /// <summary>
        /// Whether the crowd has stopped pushing this body forward, which is as close to arrived as a crowd gets.
        /// </summary>
        public bool HasArrived { get; private set; }

        private void Reset() => agent = GetComponent<HordeAgent>();

        private void Awake() => agent = agent ? agent : GetComponent<HordeAgent>();

        private void OnEnable()
        {
            HasArrived = false;

            if (agent) agent.SeparationSettledChanged += OnSettledChanged;
        }

        private void OnDisable()
        {
            if (agent) agent.SeparationSettledChanged -= OnSettledChanged;
        }

        /*
         * Settling is the crowd's own answer to having arrived, and it is the one worth reacting to. Distance to
         * the target is not: a body at the back of a pile of five thousand has not reached it and is never going
         * to, so anything waiting for it to get there waits forever. Settling says the crowd around it has stopped
         * moving, which is what "as far as I am getting" actually looks like.
         *
         * It comes back off when the target moves away and the crowd starts walking again, so this is a state to
         * read rather than a one shot to latch.
         */
        private void OnSettledChanged(bool settled) => HasArrived = settled;

    }
}

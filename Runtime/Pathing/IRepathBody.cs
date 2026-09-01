namespace MiHordeTraffic.Pathing
{
    /*
     * The scheduler owns when a body repaths and the body owns what repathing means, which is what keeps this
     * usable by anything that has a destination rather than only by a NavMeshAgent.
     *
     * Repath hands back a distance rather than taking one, because working out how far a body is from its goal
     * means reading two transforms, and the whole point of the budget is that only the bodies actually being
     * repathed this frame pay for that. Asking every body its distance in order to decide which ones to repath
     * would cost more than repathing them.
     */
    /// <summary>
    /// Something the HordePathScheduler can decide when to repath.
    /// </summary>
    public interface IRepathBody
    {

        /// <summary>
        /// Slot this body occupies in the scheduler's roster, or -1 while it is not registered. Only the scheduler writes this.
        /// </summary>
        int RepathIndex { get; set; }

        /// <summary>
        /// The earliest time this body wants to be repathed again. Only the scheduler writes this.
        /// </summary>
        float NextRepathTime { get; set; }

        /// <summary>
        /// When this body was last repathed, which is what the starvation guarantee is measured against.
        /// Only the scheduler writes this.
        /// </summary>
        float LastRepathTime { get; set; }

        /// <summary>
        /// Whether this body is in a state where repathing means anything. A frozen or targetless body is not.
        /// </summary>
        bool WantsRepath { get; }

        /*
         * The per frame half, called by the scheduler rather than by Unity. A MonoBehaviour Update is dispatched
         * from native code per component, and a few thousand of those cost real time before any of them has done
         * anything. One managed loop making one interface call each is several times cheaper for the same work,
         * which is the whole reason the scheduler owns a roster in the first place.
         */
        /// <summary>
        /// The work this body needs doing every frame, whether or not it is due a repath.
        /// </summary>
        /// <param name="deltaTime">Frame time.</param>
        void TickBody(float deltaTime);

        /// <summary>
        /// Recomputes this body's path.
        /// </summary>
        /// <param name="time">The current time, for whatever the body wants to record.</param>
        /// <returns>
        /// How far this body is from its goal, which the scheduler turns into how soon to come back.
        /// A body that has nothing to do should return a large distance so it is paced as slowly as possible.
        /// </returns>
        float Repath(float time);

    }
}

using UnityEngine;

namespace MiHordeTraffic.Pathing.FlowField
{
    /*
     * A block or unblock waiting for a safe moment to be applied. The walkability array is read by the expansion
     * for as long as one is in flight, and an expansion on a large grid is in flight for most of every rebuild
     * interval, so a structure put down mid expansion cannot edit it there and then.
     *
     * Queued rather than forced, because the alternative is joining the expansion on the spot to make room for the
     * edit, which is the exact fifth of a second stall that moving the expansion off the main thread removed. A
     * structure that takes a few frames to register is a structure that takes a few frames to register; a frame
     * that takes a fifth of a second is a hitch every time anyone builds anything.
     */
    /// <summary>
    /// One queued change to the grid's walkability, waiting for a frame with no expansion in flight.
    /// </summary>
    public struct HordeBlockRequest
    {

        /// <summary>
        /// The world area the structure covers.
        /// </summary>
        public Bounds Area;

        /// <summary>
        /// One to block the cells it covers, minus one to give them back.
        /// </summary>
        public int Delta;

        /*
         * Whether the field should be expanded again for this. Off is what makes a moving obstacle affordable: the
         * cells go, and the crowd walks around them on the very next frame without any expansion at all, because
         * the movement job tests walkability on every step it takes and slides along whichever axis survives.
         *
         * What it costs is that the flow vectors still point through the obstacle, so bodies aim at it, are refused
         * at its face and shuffle around the edges. That reads correctly for something small or something crossing
         * a lane, and badly for anything big enough that getting past it needs a route rather than a slide.
         */
        public bool UpdateRouting;

        /// <summary>
        /// Whether the ground is gone or merely shut, which decides whether the crowd still routes to it.
        /// </summary>
        public HordeBlockKind Kind;

    }
}

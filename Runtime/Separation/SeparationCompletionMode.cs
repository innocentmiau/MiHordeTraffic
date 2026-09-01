namespace MiHordeTraffic.Separation
{
    /// <summary>
    /// When the scheduled separation job is joined back to the main thread.
    /// </summary>
    public enum SeparationCompletionMode
    {
        NEXT_FRAME, // never blocks, the push forces read this frame were computed from last frame's positions
        LATE_UPDATE, // joined at the end of the same frame it was scheduled in, so the main thread can still stall on a slow job
        POLLED // joined in LateUpdate only when the handle already reports itself finished, otherwise it rolls into the next frame
    }
}

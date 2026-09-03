using System.Text;
using Unity.Profiling;

namespace MiHordeTraffic.Benchmark
{
    /*
     * The session report knew the frame was slow and knew nothing about why. Total frame time cannot tell a mover
     * that got twice as expensive apart from a crowd that piled into one spot and doubled the renderer's work,
     * and those two have opposite fixes, so guessing between them is how a tuning session gets spent on the wrong half.
     *
     * Read through ProfilerRecorder rather than by timing the calls again, because the markers are already there
     * and already wrap the right regions. Timing them a second time would drift from the Profiler window's own rows
     * the first time somebody moved a marker, and the two disagreeing is worse than only having one of them.
     *
     * Only the main thread's share of each marker is counted, which is the number that matters here: everything
     * listed is either main thread work or a Complete that blocks on it. Worker time is not frame time until
     * something waits for it, and the wait is what the Complete markers already measure.
     *
     * Nested regions are counted twice on purpose. Pathing Schedule contains the field rebuild, and Movement Move
     * is the wait for a job the mover scheduled, so the column does not sum to the frame and is not meant to.
     * Reading it is a matter of finding the biggest number, not of making it add up.
     *
     * Markers are compiled out of a non development build, so the recorders never bind there and the report says so
     * rather than printing a column of zeroes that would read as everything being free.
     */
    /// <summary>
    /// Per frame cost of each stage of the horde, accumulated over a session and printed with the rest of the report.
    /// </summary>
    public class HordePhaseTimings
    {

        private const double NANOSECONDS_PER_MILLISECOND = 1000000d;

        /*
         * Ordered the way a frame runs rather than alphabetically, so reading down the column follows the crowd
         * through one frame and a stage that grew shows up next to whatever feeds it.
         */
        private static readonly string[] PHASE_NAMES =
        {
            "MiHordeTraffic.Pathing.Tick",
            "MiHordeTraffic.Pathing.Schedule",
            "MiHordeTraffic.Pathing.FlowField",
            "MiHordeTraffic.Pathing.Bake",
            "MiHordeTraffic.Movement.Gather",
            "MiHordeTraffic.Movement.Schedule",
            "MiHordeTraffic.Movement.Move",
            "MiHordeTraffic.Movement.Publish",
            "MiHordeTraffic.Separation.Tick",
            "MiHordeTraffic.Separation.Complete",
            "MiHordeTraffic.Separation.Apply"
        };

        private readonly ProfilerRecorder[] _recorders = new ProfilerRecorder[PHASE_NAMES.Length];
        private readonly double[] _totals = new double[PHASE_NAMES.Length];

        private int _frames;

        /// <summary>
        /// Whether any marker was found, which is false in a build with the profiler stripped out.
        /// </summary>
        public bool Available
        {
            get
            {
                for (int i = 0; i < _recorders.Length; i++)
                    if (_recorders[i].Valid) return true;

                return false;
            }
        }

        /*
         * Bound here rather than in a constructor that runs at Awake, because a recorder started before the marker
         * it names has been touched never binds, and every one of these markers is created by a static initialiser
         * that does not run until its own class is first used. Retrying is what makes the order between them stop mattering.
         */
        /// <summary>
        /// Adds one frame of every marker to the running total, binding any recorder that has not bound yet.
        /// </summary>
        public void Sample()
        {
            for (int i = 0; i < _recorders.Length; i++)
            {
                if (!_recorders[i].Valid)
                {
                    _recorders[i] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, PHASE_NAMES[i]);
                    continue;
                }

                _totals[i] += _recorders[i].LastValue / NANOSECONDS_PER_MILLISECOND;
            }

            _frames++;
        }

        /// <summary>
        /// Throws away everything measured so far, to re-baseline after a change.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _totals.Length; i++)
                _totals[i] = 0d;

            _frames = 0;
        }

        /// <summary>
        /// Writes one indented line per stage, as milliseconds of main thread time per frame.
        /// </summary>
        /// <param name="text">The report being built.</param>
        public void AppendTo(StringBuilder text)
        {
            if (_frames == 0) return;

            if (!Available)
            {
                text.AppendLine("  phases       markers stripped, run in the editor or a development build");
                return;
            }

            text.Append("  phases       ms per frame on the main thread over ").Append(_frames).AppendLine(" frames");

            double counted = 0d;

            for (int i = 0; i < _recorders.Length; i++)
            {
                if (!_recorders[i].Valid) continue;

                double perFrame = _totals[i] / _frames;
                counted += perFrame;

                text.Append("    ").Append(PHASE_NAMES[i].Substring("MiHordeTraffic.".Length).PadRight(22))
                    .AppendLine(perFrame.ToString("F3"));
            }

            /*
             * Printed because it is the answer to the only question worth asking first. A total well under the
             * frame time means the horde is not what is slow and no amount of tuning it will help, and the same
             * number being most of the frame is what makes tuning it worth doing.
             */
            text.Append("    ").Append("counted (overlaps)".PadRight(22)).AppendLine(counted.ToString("F3"));
        }

        /// <summary>
        /// Stops every recorder. Called when the scheduler goes away.
        /// </summary>
        public void Dispose()
        {
            for (int i = 0; i < _recorders.Length; i++)
                if (_recorders[i].Valid) _recorders[i].Dispose();
        }

    }
}

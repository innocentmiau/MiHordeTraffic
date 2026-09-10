using System.Text;

namespace MiHordeTraffic.Benchmark
{
    /*
     * The slowest frames of a run, kept whole rather than averaged away. An average is the wrong tool for a spike
     * by construction: one frame of a tenth of a second across a couple of thousand moves every row of the phases
     * table by four hundredths of a millisecond, which means the report you would go to looking for it is the one
     * report guaranteed not to show it.
     *
     * Ranked by what this package cost the frame rather than by how long the frame took, which are different
     * questions and only one of them is answerable from in here. A run holding four editor stalls, a collection or
     * a shader compile would otherwise report those four and nothing else, and the frames where the crowd was
     * genuinely slow never make the list at all. The frame time is still carried on every entry, so a spike that
     * is mostly not this package still says so.
     *
     * Kept as the worst several rather than the single worst, because one outlier says nothing about whether it is
     * a recurring shape or a one off. Four of them with the same row lit up is a cause; four of them with four
     * different rows lit up is the editor, or the GPU, or the collector, and the answer is that it is not this.
     *
     * Held in flat arrays sized once, so recording a spike allocates nothing. A diagnostic that produces garbage
     * lands the collection inside the frames it is supposed to be measuring.
     */
    /// <summary>
    /// The slowest frames seen in a run, each with the phase breakdown of that frame rather than an average.
    /// </summary>
    public class HordeWorstFrames
    {

        private readonly int _capacity;
        private readonly int _phases;

        private readonly float[] _frameMilliseconds;
        private readonly double[] _counted;
        private readonly int[] _frameNumbers;
        private readonly int[] _substeps;
        private readonly double[] _readings;

        private int _count;
        private int _seen;

        /// <summary>
        /// How many of the worst frames are being kept.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Creates a buffer that keeps a fixed number of the slowest frames.
        /// </summary>
        /// <param name="capacity">How many frames to keep. Zero switches the whole thing off.</param>
        /// <param name="phases">How many phase readings each frame carries.</param>
        public HordeWorstFrames(int capacity, int phases)
        {
            _capacity = capacity < 0 ? 0 : capacity;
            _phases = phases;

            _frameMilliseconds = new float[_capacity];
            _counted = new double[_capacity];
            _frameNumbers = new int[_capacity];
            _substeps = new int[_capacity];
            _readings = new double[_capacity * phases];
        }

        /*
         * Sorted worst first and inserted by shifting, which is the right shape at this size. A heap would be the
         * answer for thousands; for four entries, checking whether a frame beats the slowest one already kept is a
         * single compare, and it fails for almost every frame of a run.
         */
        /// <summary>
        /// Offers a frame to the buffer, which keeps it only if it is slower than one already held.
        /// </summary>
        /// <param name="milliseconds">How long the frame took.</param>
        /// <param name="readings">That frame's phase readings, one per phase.</param>
        /// <param name="substeps">How many movement substeps that frame ran, which is one cause of a slow one.</param>
        public void Consider(float milliseconds, double[] readings, int substeps)
        {
            _seen++;

            if (_capacity == 0) return;

            double counted = 0d;

            for (int i = 0; i < _phases; i++)
                counted += readings[i];

            if (_count == _capacity && counted <= _counted[_count - 1]) return;

            int slot = _count < _capacity ? _count++ : _capacity - 1;

            while (slot > 0 && counted > _counted[slot - 1])
            {
                _frameMilliseconds[slot] = _frameMilliseconds[slot - 1];
                _counted[slot] = _counted[slot - 1];
                _frameNumbers[slot] = _frameNumbers[slot - 1];
                _substeps[slot] = _substeps[slot - 1];

                System.Array.Copy(_readings, (slot - 1) * _phases, _readings, slot * _phases, _phases);

                slot--;
            }

            _frameMilliseconds[slot] = milliseconds;
            _counted[slot] = counted;
            _frameNumbers[slot] = _seen;
            _substeps[slot] = substeps;

            System.Array.Copy(readings, 0, _readings, slot * _phases, _phases);
        }

        /// <summary>
        /// Throws away everything recorded so far.
        /// </summary>
        public void Clear()
        {
            _count = 0;
            _seen = 0;
        }

        /*
         * Rows under a hundredth of a millisecond are left out. A spike report is read by looking for the one row
         * that is enormous, and printing eleven rows of zeroes beside it is how that row gets missed.
         */
        /// <summary>
        /// Writes the kept frames and what the main thread was doing on each of them.
        /// </summary>
        /// <param name="text">Builder to append to.</param>
        /// <param name="names">Phase names, in the same order as the readings.</param>
        public void AppendTo(StringBuilder text, string[] names)
        {
            if (_count == 0) return;

            text.Append("  worst frames ").Append(_count).AppendLine(" costliest for this package, with the frame they landed on");

            for (int i = 0; i < _count; i++)
            {
                text.Append("    ").Append(_counted[i].ToString("F3")).Append(" ms of package on a ")
                    .Append(_frameMilliseconds[i].ToString("F2")).Append(" ms frame, at frame ")
                    .Append(_frameNumbers[i]);

                if (_substeps[i] > 1) text.Append(", ").Append(_substeps[i]).Append(" movement substeps");

                text.AppendLine();

                for (int phase = 0; phase < _phases; phase++)
                {
                    double reading = _readings[i * _phases + phase];

                    if (reading < .01d) continue;

                    text.Append("        ").Append(names[phase].PadRight(20)).AppendLine(reading.ToString("F3"));
                }

                text.Append("        ").Append("counted".PadRight(20)).Append(_counted[i].ToString("F3"))
                    .Append(" of ").Append(_frameMilliseconds[i].ToString("F2"))
                    .AppendLine(_counted[i] < _frameMilliseconds[i] * .5f ? "   the rest of that frame was not this package" : "");
            }
        }

    }
}

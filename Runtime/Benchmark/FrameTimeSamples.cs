using System;
using UnityEngine;

namespace MiHordeTraffic.Benchmark
{
    /*
     * Both arrays are allocated once at construction and reused for every run, because a collector that allocates
     * while sampling shows up in the very numbers it is collecting. Resolve sorts into the spare array
     * rather than sorting the live one, so a run can keep going after being read.
     *
     * Mean, max and frame count are accumulated exactly and are not bounded by the buffer, so a readout left running
     * for an hour still reports the true average over that hour. Only the percentiles come out of the buffer,
     * which is a ring: they describe the most recent capacity frames rather than the first ones recorded.
     * That is the useful way round for a live readout, where what happened two minutes ago is not what is being watched.
     */
    /// <summary>
    /// Collects one frame time per frame into a fixed buffer and turns them into a distribution.
    /// </summary>
    public class FrameTimeSamples
    {

        private readonly float[] _samples;
        private readonly float[] _sorted;

        private double _total;
        private int _totalCount;
        private float _max;
        private int _writeIndex;
        private int _filled;

        /// <summary>
        /// How many frames have been recorded since the last Clear.
        /// </summary>
        public int Count => _totalCount;

        /// <summary>
        /// How many of those frames the percentiles are drawn from, which stops climbing once the ring is full.
        /// </summary>
        public int WindowCount => _filled;

        /// <summary>
        /// Builds a collector sized for a fixed number of frames.
        /// </summary>
        /// <param name="capacity">How many frames can be recorded before samples start being dropped.</param>
        public FrameTimeSamples(int capacity)
        {
            _samples = new float[Mathf.Max(capacity, 1)];
            _sorted = new float[_samples.Length];
        }

        /// <summary>
        /// Throws away everything recorded so far.
        /// </summary>
        public void Clear()
        {
            _total = 0d;
            _totalCount = 0;
            _max = 0f;
            _writeIndex = 0;
            _filled = 0;
        }

        /// <summary>
        /// Records one frame. Never drops anything from the mean, max or count, and overwrites the oldest entry in the percentile window.
        /// </summary>
        /// <param name="milliseconds">The frame time to record.</param>
        public void Add(float milliseconds)
        {
            _total += milliseconds;
            _totalCount++;
            _max = Mathf.Max(_max, milliseconds);

            _samples[_writeIndex] = milliseconds;
            _writeIndex = _writeIndex + 1 >= _samples.Length ? 0 : _writeIndex + 1;

            if (_filled < _samples.Length) _filled++;
        }

        /// <summary>
        /// Works out the distribution of everything recorded so far.
        /// </summary>
        /// <returns>The stats, or an all zero result when nothing has been recorded.</returns>
        public FrameTimeStats Resolve()
        {
            if (_totalCount == 0) return new FrameTimeStats(0, 0f, 0f, 0f, 0f, 0f);

            Array.Copy(_samples, _sorted, _filled);
            Array.Sort(_sorted, 0, _filled);

            return new FrameTimeStats(_totalCount, (float)(_total / _totalCount), PercentileOf(.5f), PercentileOf(.95f), PercentileOf(.99f), _max);
        }

        private float PercentileOf(float fraction) => _sorted[Mathf.Clamp(Mathf.RoundToInt(fraction * (_filled - 1)), 0, _filled - 1)];

    }
}

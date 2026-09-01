using System;
using MiHordeTraffic.Pathing;
using MiHordeTraffic.Separation;
using UnityEngine;

namespace MiHordeTraffic.Benchmark
{
    /*
     * One point on the curve between how the crowd looks and what it costs. Separation and repathing are bundled
     * together on purpose: they draw on the same frame budget, and measuring one while the other stays fixed says
     * very little about what a given quality level actually costs.
     */
    /// <summary>
    /// A named separation and scheduling configuration for the sweep to measure.
    /// </summary>
    [Serializable]
    public class HordeQualityPreset
    {

        [SerializeField] private string label = "Preset";
        [SerializeField] private SeparationTuning separation;
        [SerializeField] private SchedulerTuning scheduler;

        /// <summary>
        /// What this preset is called in the report.
        /// </summary>
        public string Label => label;

        /// <summary>
        /// The separation values this preset runs at.
        /// </summary>
        public SeparationTuning Separation => separation;

        /// <summary>
        /// The scheduling values this preset runs at.
        /// </summary>
        public SchedulerTuning Scheduler => scheduler;

        /// <summary>
        /// Creates a preset. The parameterless form exists for the inspector.
        /// </summary>
        public HordeQualityPreset() { }

        /// <summary>
        /// Creates a named preset from a pair of tunings.
        /// </summary>
        /// <param name="label">Name shown in the report.</param>
        /// <param name="separation">Separation values.</param>
        /// <param name="scheduler">Scheduling values.</param>
        public HordeQualityPreset(string label, SeparationTuning separation, SchedulerTuning scheduler)
        {
            this.label = label;
            this.separation = separation;
            this.scheduler = scheduler;
        }

    }
}

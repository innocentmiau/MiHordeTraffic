using System.Collections.Generic;
using System.Text;
using MiHordeTraffic.Movement;
using MiHordeTraffic.Pathing;
using MiHordeTraffic.Pathing.FlowField;
using MiHordeTraffic.Setup;
using UnityEngine;

namespace MiHordeTraffic.Diagnostics
{
    /*
     * Watches the running crowd and says which setting is costing the most, so calibrating does not mean reading a
     * profiler and knowing in advance which of thirty numbers to suspect.
     *
     * Every check here compares two things the system already measures against each other rather than against a
     * fixed threshold, because a fixed threshold is wrong on any hardware but the one it was written on. A rebuild
     * taking one and a third milliseconds is either irrelevant or half the frame, and which of those it is depends
     * entirely on what the rest of the frame costs.
     *
     * Advice is deliberately one sided. Each check only fires when something is expensive and names the field that
     * makes it cheaper, and none of them ever suggests raising anything. A tool that tells you a setting could be
     * higher is a tool that talks constantly and gets switched off, and the setting that could be higher is the one
     * you can find yourself by watching the crowd.
     */
    /// <summary>
    /// Samples the running horde every few seconds and reports what is costing the most and which field to change.
    /// </summary>
    [DisallowMultipleComponent]
    public class HordeFlowAdvisor : MonoBehaviour
    {

        /*
         * How much of the rebuild interval an expansion may take before the field cannot keep up with the schedule
         * that asks for it. Three quarters, so it says something before the two cross rather than after.
         */
        private const float REBUILD_LATENCY_LIMIT = .75f;
        private const float CONGESTION_CELL_LIMIT = 40000f;
        private const float WALKABLE_FRACTION_LIMIT = .5f;
        private const float CROWDED_CELL_LIMIT = .75f;

        private static readonly List<HordeSceneIssue> ISSUES = new List<HordeSceneIssue>();
        private static readonly StringBuilder TEXT = new StringBuilder(512);

        [Tooltip("Off costs nothing at all: no sampling, no allocation, no report.")]
        [SerializeField] private bool advise = true;

        [Tooltip("Seconds between reports. Long enough that a hitch does not become advice.")]
        [SerializeField, Min(1f)] private float interval = 5f;

        [Tooltip("Also report when nothing is wrong, so silence is not ambiguous.")]
        [SerializeField] private bool reportWhenHealthy = false;

        private float _countdown;
        private float _elapsed;
        private int _frames;
        private int _buildsAtLastReport;

        /// <summary>
        /// The most recent report, or an empty string before the first one.
        /// </summary>
        public string LastReport { get; private set; } = string.Empty;

        /// <summary>
        /// Builds a report right now rather than waiting for the interval.
        /// </summary>
        /// <returns>The report, which is empty when there is nothing worth saying.</returns>
        public string ReportNow() => Build(Mathf.Max(_elapsed, .001f), Mathf.Max(_frames, 1));

        private void Update()
        {
            if (!advise) return;

            _elapsed += Time.unscaledDeltaTime;
            _frames++;
            _countdown -= Time.unscaledDeltaTime;

            if (_countdown > 0f) return;

            _countdown = interval;

            string report = Build(_elapsed, _frames);

            _elapsed = 0f;
            _frames = 0;

            if (report.Length == 0) return;

            LastReport = report;

            Debug.Log(report);
        }

        /*
         * The window is passed in rather than read off the fields, so ReportNow can ask for one over whatever has
         * accumulated so far without disturbing the running average the periodic report is building.
         */
        private string Build(float seconds, int frames)
        {
            ISSUES.Clear();

            float frameMilliseconds = seconds * 1000f / frames;

            CheckField(seconds, frames, frameMilliseconds);
            CheckScheduler();
            CheckMovement();
            CheckGrid();

            if (ISSUES.Count == 0 && !reportWhenHealthy) return string.Empty;

            TEXT.Clear();
            TEXT.Append("[MiHordeTraffic] advisor: ")
                .Append(frameMilliseconds.ToString("F2")).Append(" ms/frame over ").Append(frames).AppendLine(" frames");

            if (ISSUES.Count == 0) TEXT.AppendLine("  nothing worth changing");

            for (int i = 0; i < ISSUES.Count; i++)
                TEXT.Append("  ").AppendLine(ISSUES[i].ToString());

            return TEXT.ToString();
        }

        /*
         * How long the expansion takes to arrive, against how often one is asked for. It costs the frame nothing,
         * since it runs on a worker and the crowd reads the previous field until it lands, so what it can be too
         * slow for is not the frame but the schedule: an expansion that takes longer than the interval means every
         * field is stale before it arrives and the crowd is permanently steering off an older one.
         *
         * It used to be reported as a share of frame time, which was true while the expansion was joined on the
         * spot and became nonsense the moment it stopped being. At a fifth of a second an expansion four times a
         * second reads as eighty percent of the frame, so a scene doing exactly the right thing was told its field
         * was ruinous and advised to undo it.
         */
        private void CheckField(float seconds, int frames, float frameMilliseconds)
        {
            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;

            if (!driver || driver.Builds == 0) return;

            int builds = driver.Builds - _buildsAtLastReport;

            _buildsAtLastReport = driver.Builds;

            if (builds <= 0) return;

            float interval = driver.RebuildInterval * 1000f;

            if (interval <= 0f) return;

            float latency = (float)driver.AverageBuildMilliseconds;

            if (latency < interval * REBUILD_LATENCY_LIMIT) return;

            ISSUES.Add(new HordeSceneIssue(HordeIssueSeverity.ADVICE,
                $"The field takes {latency:F0} ms to expand against a rebuild interval of {interval:F0} ms, so it is most of a rebuild out of date by the time it arrives ({builds} in {seconds:F0}s).",
                "This costs the frame nothing, the crowd just reacts late. Raise Cell Size, which cuts expansion time quadratically, or set Refresh Rate to LAZY so the interval matches what the grid can manage."));
        }

        private void CheckScheduler()
        {
            HordePathScheduler scheduler = HordePathScheduler.Instance;

            if (!scheduler) return;

            SchedulerTuning tuning = scheduler.CaptureTuning();

            if (scheduler.RepathsLastFrame < tuning.RepathsPerFrame) return;

            ISSUES.Add(new HordeSceneIssue(HordeIssueSeverity.WARNING,
                $"The repath budget is saturated at {tuning.RepathsPerFrame} a frame with {scheduler.BodyCount} bodies, so each one repaths at best every {Mathf.CeilToInt((float)scheduler.BodyCount / tuning.RepathsPerFrame)} frames.",
                "Raise Repaths Per Frame, or raise Near Interval so fewer bodies ask at once."));
        }

        private void CheckMovement()
        {
            HordeFlowMovement movement = HordeFlowMovement.Instance;

            if (!movement || movement.BodyCount == 0) return;

            /*
             * Substepping at normal speed is not a tuning preference, it means bodies cross more than half a cell
             * in one frame, which is the condition the wall check cannot see through. Worth saying loudly.
             */
            if (movement.LastSubsteps > 1 && Time.timeScale <= 1f)
                ISSUES.Add(new HordeSceneIssue(HordeIssueSeverity.WARNING,
                    $"Bodies are moving more than half a cell per frame at normal speed, so the step is being cut into {movement.LastSubsteps}.",
                    "Raise the driver's Cell Size, or lower body speed. Each substep costs a full pass over the crowd."));

            /*
             * Adding a component while spawning is the kind of cost that never shows up as a spike, only as every
             * frame being slightly worse, so it is worth naming rather than leaving to the profiler.
             */
            if (HordeAgent.AgentsAddedAtRuntime > 0)
                ISSUES.Add(new HordeSceneIssue(HordeIssueSeverity.WARNING,
                    $"{HordeAgent.AgentsAddedAtRuntime} bodies have had a NavMeshAgent added at runtime because their apply mode needed one.",
                    "Put a NavMeshAgent on the prefab, or set Apply Mode to NONE if the flow field is moving them."));
        }

        private void CheckGrid()
        {
            HordeFlowFieldDriver driver = HordeFlowFieldDriver.Instance;

            if (!driver || driver.TotalCells == 0) return;

            if (driver.Field.WalkableFraction < WALKABLE_FRACTION_LIMIT)
                ISSUES.Add(new HordeSceneIssue(HordeIssueSeverity.ADVICE,
                    $"Only {driver.Field.WalkableFraction * 100f:F0}% of the {driver.TotalCells} cells are walkable, so most of the grid is describing nothing.",
                    "Lower Fit Margin, or turn Fit To Nav Mesh on so the grid follows the ground rather than a box around it."));

            if (driver.TotalCells > CONGESTION_CELL_LIMIT)
                ISSUES.Add(new HordeSceneIssue(HordeIssueSeverity.ADVICE,
                    $"The grid is {driver.TotalCells} cells, and congestion walks all of them every frame, nine times over where the cost blur is on.",
                    "Raise Cell Size. Doubling it quarters the cell count and the per frame congestion cost with it."));

            HordeFlowMovement movement = HordeFlowMovement.Instance;

            if (!movement || movement.BodyCount == 0 || driver.WalkableCells == 0) return;

            float perCell = (float)movement.BodyCount / driver.WalkableCells;

            if (perCell > CROWDED_CELL_LIMIT)
                ISSUES.Add(new HordeSceneIssue(HordeIssueSeverity.ADVICE,
                    $"There are {perCell:F2} bodies per walkable cell, so the crowd is at the density where the grid stops being able to tell one part of it from another.",
                    "Lower Cell Size so congestion can see inside the crowd, or spawn fewer bodies."));
        }

    }
}

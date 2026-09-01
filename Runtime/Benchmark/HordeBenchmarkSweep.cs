using System.Collections;
using System.Collections.Generic;
using System.Text;
using MiHordeTraffic.Pathing;
using MiHordeTraffic.Separation;
using UnityEngine;

namespace MiHordeTraffic.Benchmark
{
    /*
     * A frame time number read off a live overlay cannot resolve the differences being chased here. It is smoothed,
     * it moves while you read it, and it includes whatever the crowd happened to be doing at that moment.
     * This holds each configuration for a fixed window and reports the distribution, which is the only way
     * a sub millisecond difference means anything.
     *
     * Quality presets are measured without respawning between them. The crowd, its positions and its paths all stay
     * exactly as they are and only the tuning changes, so the difference between two rows is the tuning and nothing
     * else. Respawning would reshuffle the crowd and put a different clustering under each preset.
     *
     * Avoidance modes do respawn, because switching those requires reconfiguring every agent.
     */
    /// <summary>
    /// Runs the benchmark through each avoidance mode or quality preset in turn and reports the frame time distribution of each.
    /// </summary>
    public class HordeBenchmarkSweep : MonoBehaviour
    {

        private const int SAMPLE_CAPACITY = 8192;

        [SerializeField] private HordeBenchmark benchmark;
        [SerializeField] private HordeSweepKind sweepKind = HordeSweepKind.QUALITY_PRESETS;
        [SerializeField] private float settleSeconds = 5f;
        [SerializeField] private float measureSeconds = 15f;
        [SerializeField] private bool runOnStart = false;

        [Header("Avoidance Modes")]
        [SerializeField] private HordeBenchmarkMode[] modes = { HordeBenchmarkMode.NONE, HordeBenchmarkMode.BUILT_IN_AVOIDANCE, HordeBenchmarkMode.SEPARATION_SYSTEM };

        [Header("Quality Presets")]
        /*
         * What varies is how much work the crowd is asked to do: whether agents settle once they cannot
         * make progress, how readily they settle, and how much repathing budget they get. None of those move an
         * agent anywhere it would not otherwise have gone.
         *
         * A deliberately has settling off, so the gap from A to B is what settling alone is worth.
         */
        [SerializeField] private HordeQualityPreset[] qualityPresets =
        {
            new HordeQualityPreset("A no settling",
                new SeparationTuning { UpdateInterval = 1, ReusePushBetweenTicks = true, CellSize = 0f, BlockingEnabled = false, BlockingNeighbours = 3, SettledPushScale = .15f },
                new SchedulerTuning { RepathsPerFrame = 150, NearInterval = .1f, FarInterval = 1f, NearDistance = 10f, FarDistance = 60f, GuaranteedInterval = 3f }),

            new HordeQualityPreset("B settling on",
                new SeparationTuning { UpdateInterval = 1, ReusePushBetweenTicks = true, CellSize = 0f, BlockingEnabled = true, BlockingNeighbours = 3, SettledPushScale = .15f },
                new SchedulerTuning { RepathsPerFrame = 150, NearInterval = .1f, FarInterval = 1f, NearDistance = 10f, FarDistance = 60f, GuaranteedInterval = 3f }),

            new HordeQualityPreset("C settling + lean paths",
                new SeparationTuning { UpdateInterval = 2, ReusePushBetweenTicks = true, CellSize = 0f, BlockingEnabled = true, BlockingNeighbours = 2, SettledPushScale = .05f },
                new SchedulerTuning { RepathsPerFrame = 50, NearInterval = .25f, FarInterval = 3f, NearDistance = 10f, FarDistance = 60f, GuaranteedInterval = 8f })
        };

        private readonly FrameTimeSamples _samples = new FrameTimeSamples(SAMPLE_CAPACITY);
        private readonly StringBuilder _report = new StringBuilder();
        private readonly Dictionary<SeparationSettings, SeparationTuning> _originalSeparation = new Dictionary<SeparationSettings, SeparationTuning>();

        private SchedulerTuning _originalScheduler;
        private bool _schedulerCaptured;

        private FrameTimeStats[] _results;
        private string[] _labels;
        private GUIStyle _overlayStyle;
        private string _status = "Sweep: idle";
        private string _reportText = string.Empty;
        private bool _running;

        /// <summary>
        /// Whether a sweep is currently in progress.
        /// </summary>
        public bool Running => _running;

        /// <summary>
        /// The finished report, or an empty string before the first sweep completes.
        /// </summary>
        public string Report => _reportText;

        [ContextMenu("Run Sweep")]
        public void RunSweep()
        {
            if (_running) return;

            StopAllCoroutines();
            StartCoroutine(Sweep());
        }

        private void Start()
        {
            if (runOnStart) RunSweep();
        }

        /*
         * Settings are ScriptableObjects, so in the editor anything this writes to them lands on the asset on disk.
         * Stopping play mid sweep would otherwise leave the project permanently configured to whichever preset
         * happened to be running, which is a quiet way to lose your own tuning.
         */
        private void OnDisable() => RestoreTuning();

        private IEnumerator Sweep()
        {
            if (!benchmark)
            {
                Debug.LogError("[HordeBenchmarkSweep] No HordeBenchmark assigned, nothing to drive.");
                yield break;
            }

            _running = true;
            _reportText = string.Empty;

            /*
             * IMGUI is switched off for the whole sweep, not just dimmed. OnGUI runs twice a frame and allocates
             * as it lays out, and the collections that follow are exactly the uncontrollable spikes that make
             * a p99 unreadable. The results are drawn once the sweep is over and nothing is being timed.
             */
            bool previousOverlay = benchmark.OverlayVisible;
            benchmark.SetOverlayVisible(false);

            int previousVSync = QualitySettings.vSyncCount;
            int previousTargetFrameRate = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            if (sweepKind == HordeSweepKind.AVOIDANCE_MODES) yield return SweepModes();
            else yield return SweepPresets();

            QualitySettings.vSyncCount = previousVSync;
            Application.targetFrameRate = previousTargetFrameRate;
            benchmark.SetOverlayVisible(previousOverlay);

            RestoreTuning();
            BuildReport();

            _status = "Sweep: done";
            _running = false;
        }

        private IEnumerator SweepModes()
        {
            _results = new FrameTimeStats[modes.Length];
            _labels = new string[modes.Length];

            for (int i = 0; i < modes.Length; i++)
            {
                _labels[i] = modes[i].ToString();

                benchmark.SetMode(modes[i]);
                benchmark.SpawnHorde();

                yield return Measure(i, _labels[i]);
            }

            benchmark.DespawnHorde();
        }

        /*
         * Spawned once, outside the loop. The crowd carries its clustering and its paths from one preset into the
         * next, which is the whole point: two rows differ by the tuning between them and by nothing else.
         */
        private IEnumerator SweepPresets()
        {
            _results = new FrameTimeStats[qualityPresets.Length];
            _labels = new string[qualityPresets.Length];

            benchmark.SetMode(HordeBenchmarkMode.SEPARATION_SYSTEM);
            benchmark.SpawnHorde();

            CaptureTuning();

            for (int i = 0; i < qualityPresets.Length; i++)
            {
                HordeQualityPreset preset = qualityPresets[i];

                _labels[i] = preset.Label;
                ApplyPreset(preset);

                yield return Measure(i, preset.Label);
            }

            benchmark.DespawnHorde();
        }

        /// <summary>
        /// Settles, then records one measurement window into the results at the given index.
        /// </summary>
        private IEnumerator Measure(int index, string label)
        {
            _status = $"Sweep: {label}  settling";

            benchmark.ResetStats();

            yield return WaitUnscaled(settleSeconds);

            _status = $"Sweep: {label}  measuring";
            _samples.Clear();

            float remaining = measureSeconds;

            while (remaining > 0f)
            {
                yield return null;
                remaining -= Time.unscaledDeltaTime;
                _samples.Add(Time.unscaledDeltaTime * 1000f);
            }

            _results[index] = _samples.Resolve();
        }

        private void CaptureTuning()
        {
            _originalSeparation.Clear();

            SeparationSystem system = SeparationSystem.Instance;

            if (system)
            {
                IReadOnlyList<SeparationGroup> groups = system.Groups;

                for (int i = 0; i < groups.Count; i++)
                {
                    SeparationSettings settings = groups[i].Settings;
                    if (settings && !_originalSeparation.ContainsKey(settings)) _originalSeparation.Add(settings, settings.CaptureTuning());
                }
            }

            HordePathScheduler scheduler = HordePathScheduler.Instance;

            if (!scheduler) return;

            _originalScheduler = scheduler.CaptureTuning();
            _schedulerCaptured = true;
        }

        private void ApplyPreset(HordeQualityPreset preset)
        {
            foreach (KeyValuePair<SeparationSettings, SeparationTuning> entry in _originalSeparation)
                entry.Key.ApplyTuning(preset.Separation);

            if (HordePathScheduler.Instance) HordePathScheduler.Instance.ApplyTuning(preset.Scheduler);
        }

        private void RestoreTuning()
        {
            foreach (KeyValuePair<SeparationSettings, SeparationTuning> entry in _originalSeparation)
                if (entry.Key) entry.Key.ApplyTuning(entry.Value);

            _originalSeparation.Clear();

            if (_schedulerCaptured && HordePathScheduler.Instance) HordePathScheduler.Instance.ApplyTuning(_originalScheduler);

            _schedulerCaptured = false;
        }

        private IEnumerator WaitUnscaled(float seconds)
        {
            float remaining = seconds;

            while (remaining > 0f)
            {
                yield return null;
                remaining -= Time.unscaledDeltaTime;
            }
        }

        private void BuildReport()
        {
            _report.Clear();
            _report.AppendLine($"[HordeBenchmarkSweep] {sweepKind}, {benchmark.SpawnCount} agents, {measureSeconds:F0}s each after {settleSeconds:F0}s settle, all times in ms");

            for (int i = 0; i < _results.Length; i++)
                _report.AppendLine($"  {_labels[i],-22} {_results[i]}");

            AppendDelta();

            _reportText = _report.ToString();
            Debug.Log(_reportText);
        }

        /*
         * Modes are measured against the no avoidance floor, since what anyone wants from that sweep is what the
         * avoidance itself costs rather than what the scene costs. Presets are measured against the first one,
         * which is the best looking, so each row reads as what you buy by giving that quality up.
         */
        private void AppendDelta()
        {
            if (_results.Length < 2) return;

            if (sweepKind == HordeSweepKind.QUALITY_PRESETS)
            {
                _report.AppendLine($"  saved against {_labels[0]}, on the mean of {_results[0].Mean:F2} ms");

                for (int i = 1; i < _results.Length; i++)
                {
                    float saved = _results[0].Mean - _results[i].Mean;
                    _report.AppendLine($"    {_labels[i],-20} {saved,6:F2} ms   {saved / Mathf.Max(_results[0].Mean, .0001f) * 100f,5:F1}%");
                }

                return;
            }

            int floor = System.Array.IndexOf(modes, HordeBenchmarkMode.NONE);
            if (floor < 0) return;

            _report.AppendLine($"  cost of avoidance, median above the {HordeBenchmarkMode.NONE} floor of {_results[floor].Median:F2} ms");

            for (int i = 0; i < _results.Length; i++)
            {
                if (i == floor) continue;

                _report.AppendLine($"    {_labels[i],-20} {_results[i].Median - _results[floor].Median,6:F2} ms");
            }
        }

        private void OnGUI()
        {
            _overlayStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };

            GUI.Label(new Rect(12f, 296f, 720f, 24f), _status, _overlayStyle);

            if (_reportText.Length > 0) GUI.Label(new Rect(12f, 320f, 720f, 260f), _reportText, _overlayStyle);
        }

    }
}

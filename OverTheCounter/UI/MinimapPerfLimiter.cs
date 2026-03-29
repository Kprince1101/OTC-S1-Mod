using OverTheCounter.Utilities;
using System.Diagnostics;
using UnityEngine;

namespace OverTheCounter.UI
{
    /// <summary>
    /// Adaptive frame-skip throttle for the minimap overlay.
    /// <para>
    /// The minimap's <c>UpdateMinimap()</c> has two cost tiers:
    /// <list type="bullet">
    ///   <item><b>Cheap</b> — player position offset, rotation, compass, time/day text, rank bar (~0.01 ms)</item>
    ///   <item><b>Expensive</b> — POI cache refresh, clone management, customer markers, edge indicators (~0.3+ ms)</item>
    /// </list>
    /// When enabled, this limiter lets the cheap path run every frame (so the map stays smooth)
    /// while skipping expensive work on some frames to reduce CPU load.
    /// </para>
    /// <para><b>How it decides when to throttle:</b></para>
    /// <para>
    /// Rather than using a fixed FPS cap, the limiter uses a <b>probe-and-evaluate</b> cycle:
    /// <list type="number">
    ///   <item>
    ///     <b>Settled</b> — The limiter watches two signals every frame:
    ///     <list type="bullet">
    ///       <item>Script cost: is the minimap's render time above 15% of the target frame budget (2.5 ms at 60 fps)?</item>
    ///       <item>Probe cooldown: have enough frames passed since the last probe? (default 300 frames / ~5 sec)</item>
    ///     </list>
    ///     When both conditions are met, it starts a probe.
    ///   </item>
    ///   <item>
    ///     <b>Probing</b> — The limiter bumps the skip interval up or down by 1 and waits for the
    ///     EMA averages to settle (60 frames / ~1 sec).
    ///     <list type="bullet">
    ///       <item><b>Probe UP</b> (increase throttle): triggered when script cost is over budget and skip &lt; 5.</item>
    ///       <item><b>Probe DOWN</b> (decrease throttle): triggered when script cost is under budget and skip &gt; 0.</item>
    ///     </list>
    ///   </item>
    ///   <item>
    ///     <b>Evaluate</b> — After settling, the limiter compares the new average frame time against
    ///     the baseline captured before the probe:
    ///     <list type="bullet">
    ///       <item>Keep a <b>probe UP</b> if FPS improved by at least 5% (e.g., 87 fps → 92+ fps).</item>
    ///       <item>Keep a <b>probe DOWN</b> if FPS didn't drop by more than 3%.</item>
    ///       <item>Otherwise, revert to the previous skip level.</item>
    ///     </list>
    ///     If a kept probe showed &gt;15% gain, the next probe starts sooner (fast ramp for weak hardware).
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// The skip interval maxes at 5, meaning the minimap updates POIs at worst every 6th frame
    /// (~10 fps at 60 fps). The cheap path always runs, so the map doesn't visually "jump."
    /// </para>
    /// </summary>
    internal class MinimapPerfLimiter
    {
        // ── Tuning constants ──────────────────────────────────────────
        private const float EmaAlpha = 0.15f;
        private const float TargetFrameMs = 16.67f;
        private const int SettleFrames = 60;
        private const int ProbeInterval = 300;
        private const float MinGainRatio = 0.05f;
        private const float MaxLossRatio = 0.03f;
        private const int MaxSkipInterval = 5;

        // ── State ─────────────────────────────────────────────────────
        private readonly Stopwatch _renderStopwatch = new();
        private float _avgRenderMs;
        private float _avgFrameMs;
        private float _baselineFrameMs;
        private int _skipInterval;
        private int _frameCounter;
        private int _preProbeSkip;
        private int _settleCounter;
        private int _probeTimer = ProbeInterval;
        private bool _probedUp;
        private bool _probing;
        private string _lastProbeResult = "";

        // ── Public API ────────────────────────────────────────────────

        /// <summary>
        /// True when the current frame is a full update (expensive POI work should run).
        /// False when the frame was skipped by the limiter (only cheap work should run).
        /// </summary>
        public bool IsFullUpdate { get; private set; } = true;

        /// <summary>
        /// Called at the start of the visible minimap block each frame.
        /// Ticks internal timers, decides whether to skip this frame, and returns
        /// true if the caller should skip the expensive update path.
        /// </summary>
        /// <returns>True if the expensive update should be skipped this frame.</returns>
        public bool ShouldSkipExpensiveUpdate()
        {
            bool perfLimit = Config.MinimapPerfLimit.Value;
            if (!perfLimit)
            {
                ResetState();
                UpdateProfilerNote(perfLimit);
                IsFullUpdate = true;
                return false;
            }

            // Tick probe/settle timers every frame (not just full-update frames)
            // so wall-clock timing stays consistent regardless of skip level
            if (!_probing && _probeTimer > 0)
                _probeTimer--;
            if (_probing)
                _settleCounter++;

            if (_skipInterval > 0)
            {
                _frameCounter++;
                if (_frameCounter <= _skipInterval)
                {
                    IsFullUpdate = false;
                    return true;
                }
                _frameCounter = 0;
            }

            IsFullUpdate = true;
            return false;
        }

        /// <summary>
        /// Call immediately before <c>UpdateMinimap()</c> on full-update frames.
        /// Starts the render stopwatch.
        /// </summary>
        public void BeginTiming()
        {
            if (!Config.MinimapPerfLimit.Value) return;
            _renderStopwatch.Restart();
        }

        /// <summary>
        /// Call immediately after <c>UpdateMinimap()</c> on full-update frames.
        /// Stops the stopwatch, updates EMA averages, runs the probe-and-evaluate cycle,
        /// and updates profiler notes.
        /// </summary>
        public void EndTiming()
        {
            if (!Config.MinimapPerfLimit.Value) return;

            _renderStopwatch.Stop();
            float renderMs = (float)_renderStopwatch.Elapsed.TotalMilliseconds;
            _avgRenderMs = _avgRenderMs == 0f ? renderMs
                : _avgRenderMs * (1f - EmaAlpha) + renderMs * EmaAlpha;

            float frameMs = Time.unscaledDeltaTime * 1000f;
            _avgFrameMs = _avgFrameMs == 0f ? frameMs
                : _avgFrameMs * (1f - EmaAlpha) + frameMs * EmaAlpha;

            UpdateProfilerNote(true);
            RunProbeEvaluate();
        }

        // ── Internals ─────────────────────────────────────────────────

        /// <summary>
        /// Resets all limiter state. Called when performance limiting is toggled off.
        /// </summary>
        private void ResetState()
        {
            _skipInterval = 0;
            _frameCounter = 0;
            _probing = false;
            _probeTimer = ProbeInterval;
            _avgRenderMs = 0f;
            _avgFrameMs = 0f;
        }

        /// <summary>
        /// Probe-and-evaluate cycle: tries small skip interval changes and measures
        /// whether they produce meaningful FPS improvements before committing.
        /// </summary>
        private void RunProbeEvaluate()
        {
            if (!_probing)
            {
                if (_probeTimer <= 0)
                {
                    float scriptBudget = TargetFrameMs * 0.15f;
                    bool shouldProbeUp = _avgRenderMs > scriptBudget && _skipInterval < MaxSkipInterval;
                    bool shouldProbeDown = !shouldProbeUp && _skipInterval > 0;

                    if (shouldProbeUp || shouldProbeDown)
                    {
                        _baselineFrameMs = _avgFrameMs;
                        _preProbeSkip = _skipInterval;
                        _probedUp = shouldProbeUp;
                        _skipInterval += shouldProbeUp ? 1 : -1;
                        _settleCounter = 0;
                        _probing = true;
                    }
                    else
                    {
                        _probeTimer = ProbeInterval;
                    }
                }
            }
            else
            {
                if (_settleCounter >= SettleFrames)
                {
                    float gain = _baselineFrameMs > 0
                        ? (_baselineFrameMs - _avgFrameMs) / _baselineFrameMs
                        : 0f;
                    bool keep = _probedUp
                        ? gain > MinGainRatio
                        : gain > -MaxLossRatio;

                    if (!keep)
                        _skipInterval = _preProbeSkip;

                    _probing = false;
                    _probeTimer = keep && gain > 0.15f
                        ? ProbeInterval / 3
                        : ProbeInterval;

                    if (_probedUp && keep)
                        _lastProbeResult = $"Reduced marker updates, gained {gain * 100:F0}% fps.";
                    else if (_probedUp && !keep)
                        _lastProbeResult = $"Tried reducing updates but only gained {gain * 100:F0}% fps, not worth it.";
                    else if (!_probedUp && keep)
                        _lastProbeResult = "Increased marker updates, minimal fps impact.";
                    else
                        _lastProbeResult = "Tried increasing updates but lost too much fps, staying throttled.";
                }
            }
        }

        /// <summary>
        /// Updates the PerfTracker note for the minimap. Only allocates strings
        /// when profiling is enabled to avoid per-frame GC pressure.
        /// </summary>
        private void UpdateProfilerNote(bool perfLimitEnabled)
        {
            if (!Config.ProfilingEnabled.Value) return;

            if (!perfLimitEnabled)
            {
                PerfTracker.SetNote("Minimap",
                    "Performance limiting is off. Minimap updates every frame.");
                return;
            }

            float fps = _avgFrameMs > 0 ? 1000f / _avgFrameMs : 0f;
            if (_skipInterval == 0)
            {
                PerfTracker.SetNote("Minimap",
                    $"Running at full speed ({fps:F0} fps). " +
                    (_lastProbeResult.Length > 0 ? _lastProbeResult : "No throttling needed."));
            }
            else
            {
                int poiFps = _avgFrameMs > 0
                    ? (int)(fps / (_skipInterval + 1))
                    : 0;
                PerfTracker.SetNote("Minimap",
                    $"Throttled: updating markers ~{poiFps} times/sec to save performance ({fps:F0} fps). " +
                    (_lastProbeResult.Length > 0 ? _lastProbeResult : "Adjusting..."));
            }
        }
    }
}

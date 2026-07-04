using UnityEngine;
using Arena.Input;

namespace Arena.Simulation
{
    /// <summary>
    /// S7 (design review): adaptive presentation-delay budget for the F4
    /// server-time timeline, shared by every RemotePresentationBuffer on the
    /// connection — delivery lateness is a connection property, not a
    /// per-entity one, so one budget serves players and NPCs alike.
    ///
    /// Why adaptive: the F4 A/B rerun 2 (2026-07-03) showed the fixed 100 ms
    /// budget was effectively UNDER-delayed — absolute delivery lateness vs
    /// the estimated server clock has a wider tail than inter-arrival gaps —
    /// so the server-time timeline paid +34 ms over arrival's 66 ms and still
    /// extrapolated 2.6 pp more. The budget must be sized from the measured
    /// lateness itself:
    ///
    ///   target = clamp(p95(arrival lateness over the last 10 s) + one tick,
    ///                  66 ms, 200 ms)
    ///
    /// The one-tick margin covers the inter-arrival gap: a render point that
    /// clears the p95-late snapshot still needs the room between that
    /// snapshot and the next one, or every gap tail counts as extrapolation.
    /// (Row stamps are tick-quantized — RemotePresentationBuffer
    /// .QuantizeServerTimeMicros — so sub-tick stamp jitter is already gone.)
    ///
    /// The applied budget slews toward the target instead of jumping: raises
    /// fast enough to close a jitter-burst starvation window in a couple of
    /// seconds, lowers slowly enough that reclaimed delay never reads as
    /// motion warp (lowering 8 ms/s shifts the render point by 0.8 % of
    /// real time — imperceptible under the pose smoother). Target recompute
    /// runs at 1 Hz; there is no per-frame pumping.
    ///
    /// Lateness samples are recorded at snapshot arrival
    /// (RemotePresentationBuffer.Push): serverNow − snapshot.ServerTimeMs,
    /// precise-clock-gated. With no precise sample nothing records, the
    /// window stays empty, and the budget holds the pre-S7 fixed 100 ms —
    /// behavior without a precise clock is exactly the shipped F4 (which
    /// also keeps the editor tests' synthetic Push/Tick sequences pure).
    /// Main-thread only, like the buffers that feed it.
    /// </summary>
    public static class ServerTimeDelayBudget
    {
        public const long MinBudgetMs = 66;
        public const long MaxBudgetMs = 200;
        /// <summary>Pre-adaptation hold — the pre-S7 fixed budget.</summary>
        public const long DefaultBudgetMs = 100;

        private const int WindowCapacity = 1024;
        private const float WindowSeconds = 10f;
        /// <summary>~1–2 s of row flow before the p95 is worth acting on.</summary>
        private const int MinSamplesForAdaptation = 60;
        private const float RecomputeIntervalSeconds = 1f;
        private const float MarginMs = MovementNetcodeConfig.FixedTickMilliseconds;
        private const float RaiseSlewMsPerSecond = 60f;
        private const float LowerSlewMsPerSecond = 8f;
        /// <summary>Junk guard for mid-snap clock estimates.</summary>
        private const float MaxRecordableLatenessMs = 2000f;

        private static readonly float[] LatenessMs = new float[WindowCapacity];
        private static readonly float[] RecordedAt = new float[WindowCapacity];
        private static readonly float[] Scratch = new float[WindowCapacity];
        private static int _count;
        private static int _next;
        private static float _appliedMs = DefaultBudgetMs;
        private static float _targetMs = DefaultBudgetMs;
        private static float _nextRecomputeAt = float.NegativeInfinity;
        private static float _lastAdvanceAt = float.NegativeInfinity;

        /// <summary>Budget applied by the last BudgetMs call, in ms.</summary>
        public static float LastAppliedBudgetMs => _appliedMs;
        /// <summary>Target the applied budget is slewing toward, in ms.</summary>
        public static float LastTargetBudgetMs => _targetMs;
        /// <summary>Lateness p95 at the last recompute; 0 while warming.</summary>
        public static float LastLatenessP95Ms { get; private set; }
        /// <summary>Window samples at the last recompute.</summary>
        public static int WindowSampleCount { get; private set; }
        /// <summary>False while holding DefaultBudgetMs for lack of samples.</summary>
        public static bool AdaptationActive { get; private set; }

        public static void Reset()
        {
            _count = 0;
            _next = 0;
            _appliedMs = DefaultBudgetMs;
            _targetMs = DefaultBudgetMs;
            _nextRecomputeAt = float.NegativeInfinity;
            _lastAdvanceAt = float.NegativeInfinity;
            LastLatenessP95Ms = 0f;
            WindowSampleCount = 0;
            AdaptationActive = false;
        }

        /// <summary>
        /// Records one arrival-lateness observation (serverNow at receive
        /// minus the row's tick-quantized server stamp). Callers gate on a
        /// precise clock; negatives (estimate ahead of the stamp) clamp to 0
        /// so they still count as on-time arrivals in the percentile.
        /// </summary>
        public static void RecordArrivalLatenessMs(long latenessMs, float nowRealtime)
        {
            float value = Mathf.Clamp(latenessMs, 0f, MaxRecordableLatenessMs);
            LatenessMs[_next] = value;
            RecordedAt[_next] = nowRealtime;
            _next = (_next + 1) % WindowCapacity;
            if (_count < WindowCapacity)
                _count++;
        }

        /// <summary>
        /// The current budget in ms. Recomputes the target at 1 Hz and
        /// advances the slew by the elapsed time since the previous call —
        /// repeated calls within one frame (same nowRealtime) are free, and a
        /// long gap (e.g. an OFF A/B leg where nothing samples the budget)
        /// simply lands the applied value on the target.
        /// </summary>
        public static float BudgetMs(float nowRealtime)
        {
            if (nowRealtime >= _nextRecomputeAt)
            {
                RecomputeTarget(nowRealtime);
                _nextRecomputeAt = nowRealtime + RecomputeIntervalSeconds;
            }

            float dt = nowRealtime - _lastAdvanceAt;
            _lastAdvanceAt = nowRealtime;
            if (dt > 0f && !float.IsInfinity(dt))
            {
                float delta = _targetMs - _appliedMs;
                float step = delta > 0f
                    ? Mathf.Min(delta, RaiseSlewMsPerSecond * dt)
                    : Mathf.Max(delta, -LowerSlewMsPerSecond * dt);
                _appliedMs += step;
            }

            return _appliedMs;
        }

        private static void RecomputeTarget(float nowRealtime)
        {
            int live = 0;
            for (int i = 0; i < _count; i++)
            {
                if (nowRealtime - RecordedAt[i] <= WindowSeconds)
                    Scratch[live++] = LatenessMs[i];
            }

            WindowSampleCount = live;
            if (live < MinSamplesForAdaptation)
            {
                // Not enough evidence (session warmup, or row flow stopped
                // and the window aged out): hold the pre-S7 fixed budget.
                AdaptationActive = false;
                LastLatenessP95Ms = 0f;
                _targetMs = DefaultBudgetMs;
                return;
            }

            System.Array.Sort(Scratch, 0, live);
            float p95 = Scratch[(int)((live - 1) * 0.95f)];
            AdaptationActive = true;
            LastLatenessP95Ms = p95;
            _targetMs = Mathf.Clamp(p95 + MarginMs, MinBudgetMs, MaxBudgetMs);
        }
    }
}

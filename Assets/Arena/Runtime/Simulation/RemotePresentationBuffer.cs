using UnityEngine;
using Arena.Input;

namespace Arena.Simulation
{
    /// <summary>
    /// Snapshot-buffered presentation for a server-driven entity (remote
    /// players, NPCs): buffers authoritative snapshots, samples a render
    /// target at now − delay, and drives a render pose with the strict
    /// interpolation-first policy extracted from ClientSimulationState:
    ///   - interpolation is the default
    ///   - extrapolation is temporary, velocity-only, and capped
    ///     (entities replicated without velocity — NPCs — push zero-velocity
    ///     snapshots, so their extrapolation degrades to position-hold)
    ///   - large discontinuities snap, small errors smooth
    ///
    /// Time is passed in by the caller so the math stays testable.
    ///
    /// Two timelines (feel audit F4). The server-time timeline keys the ring
    /// on PlayerSnapshot.ServerTimeMs and renders at
    /// serverNowMs − ServerTimeDelayMs (fixed 100 ms), so delivery jitter
    /// costs buffered delay instead of warping sampled motion. ServerTimeMs
    /// is the row's UpdatedAt quantized to the fixed-tick grid
    /// (QuantizeServerTimeMicros): quantization was chosen over a per-entity
    /// tick→UpdatedAt anchor because it needs no held state, stays anchored
    /// to the server epoch clock (comparable to ArenaServerClock.ServerNowMs,
    /// no drift), works identically for players (PlayerPhysics.UpdatedAt) and
    /// NPCs (NpcPhysics.UpdatedAt, which has no tick), and still removes the
    /// sub-tick server-side write jitter that preferring the tick index would
    /// have removed. The pre-F4 client-arrival timeline
    /// (PlayerSnapshot.ReceivedTime, now − InterpolationDelaySeconds) is kept
    /// unchanged as the automatic fallback — used while the caller has no
    /// server-clock estimate, while any buffered snapshot lacks ServerTimeMs,
    /// or while the ServerTimeTimelineEnabled A/B toggle is off.
    ///
    /// Idle-aware sample taxonomy (design review S1). Every non-seeding Tick
    /// sample is classified interpolated / extrapolating (within the cap) /
    /// starved (past the cap, delivery is late) / settled (past the cap, the
    /// entity is authoritatively at rest) — see ClassifySample. Classification
    /// and its counters are reporting only; the rendered pose never depends on
    /// them.
    /// </summary>
    public sealed class RemotePresentationBuffer
    {
        public enum SampleMode
        {
            Fallback,
            Interpolation,
            Extrapolation,
        }

        /// <summary>
        /// What a Tick sample means for delivery health (SampleMode says which
        /// math produced the pose; SampleClass says whether being past the
        /// newest snapshot indicates late delivery or an idle entity).
        /// </summary>
        public enum SampleClass
        {
            Interpolated,
            /// <summary>Past the newest snapshot, within the extrapolation cap.</summary>
            Extrapolating,
            /// <summary>Past the cap and delivery is late.</summary>
            Starved,
            /// <summary>Past the cap and the entity is authoritatively at rest.</summary>
            Settled,
        }

        /// <summary>
        /// How the entity's authoritative source row is written server-side —
        /// the settled-vs-starved discriminator's per-entity input.
        /// </summary>
        public enum SourceRowCadence
        {
            /// <summary>
            /// The row is written every server tick while the entity exists
            /// (PlayerPhysics): no rows past the cap always means delivery is
            /// late.
            /// </summary>
            EveryTick,
            /// <summary>
            /// Row writes stop while the entity is at rest (NpcPhysics has no
            /// heartbeat): no rows past the cap means settled while global row
            /// delivery is healthy, starved when it has stalled.
            /// </summary>
            StopsWhenIdle,
        }

        private const int SnapshotCapacity = 12;
        private const float DefaultInterpolationDelaySeconds = 2.0f * MovementNetcodeConfig.FixedTickSeconds;
        private const float DefaultMaxExtrapolationSeconds = 2.0f * MovementNetcodeConfig.FixedTickSeconds;
        private const float DefaultSmoothingSpeed = 18f;
        private const float DefaultHardSnapDistance = 2.0f;
        private const float DefaultHardSnapYawRadians = 60f * Mathf.Deg2Rad;
        private const long DefaultServerTimeDelayMs = 100L;

        /// <summary>
        /// Runtime A/B toggle for the F4 server-time timeline (all buffers at
        /// once). Off, or unusable (no clock estimate / snapshot without
        /// ServerTimeMs), falls back to the pre-F4 arrival timeline.
        /// Surfaced in NetcodeDebugOverlay (semicolon while visible).
        /// </summary>
        public static bool ServerTimeTimelineEnabled { get; set; } = true;

        private readonly PlayerSnapshot[] _snapshots = new PlayerSnapshot[SnapshotCapacity];
        private int _snapshotStart;
        private int _snapshotCount;

        private Vector3 _renderPos;
        private float _renderYaw; // radians

        private int _hardSnapCount;
        private int _smoothUpdateCount;
        private int _interpolationSampleCount;
        private int _extrapolationSampleCount;
        private int _starvedSampleCount;
        private int _settledSampleCount;
        private float _lastPositionError;
        private float _maxPositionErrorObserved;
        private float _lastExtrapolationSeconds;
        private float _lastExtrapolationUnclampedSeconds;

        public RemotePresentationBuffer(SourceRowCadence sourceRowCadence)
        {
            RowCadence = sourceRowCadence;
        }

        public SourceRowCadence RowCadence { get; }
        public float InterpolationDelaySeconds { get; } = DefaultInterpolationDelaySeconds;
        public float MaxExtrapolationSeconds { get; } = DefaultMaxExtrapolationSeconds;
        public float SmoothingSpeed { get; } = DefaultSmoothingSpeed;
        public float HardSnapDistance { get; } = DefaultHardSnapDistance;
        public float HardSnapYawRadians { get; } = DefaultHardSnapYawRadians;
        public long ServerTimeDelayMs { get; } = DefaultServerTimeDelayMs;

        public int SnapshotCount => _snapshotCount;
        public Vector3 RenderPosition => _renderPos;
        public float RenderYawRadians => _renderYaw;
        public int HardSnapCount => _hardSnapCount;
        public int SmoothUpdateCount => _smoothUpdateCount;
        public int InterpolationSampleCount => _interpolationSampleCount;
        /// <summary>Tick samples past the newest snapshot but within the extrapolation cap.</summary>
        public int ExtrapolationSampleCount => _extrapolationSampleCount;
        /// <summary>Tick samples past the cap while delivery was late (SampleClass.Starved).</summary>
        public int StarvedSampleCount => _starvedSampleCount;
        /// <summary>Tick samples past the cap while the entity was authoritatively at rest (SampleClass.Settled).</summary>
        public int SettledSampleCount => _settledSampleCount;
        public float LastPositionError => _lastPositionError;
        public float MaxPositionErrorObserved => _maxPositionErrorObserved;
        public float LastExtrapolationSeconds => _lastExtrapolationSeconds;
        /// <summary>Whether the last Tick sampled the server-time timeline (vs arrival fallback).</summary>
        public bool LastTickUsedServerTimeline { get; private set; }
        /// <summary>Render delay the last Tick actually applied, in ms (100 server-time / 66 arrival).</summary>
        public float LastEffectiveDelayMs { get; private set; } = DefaultInterpolationDelaySeconds * 1000f;
        /// <summary>
        /// Buffered headroom ahead of the last Tick's render point, in fixed
        /// ticks (negative = extrapolating), after the settled reporting rule
        /// (ReportableBufferAheadTicks) — a settled entity pins at the cap
        /// boundary instead of diving while no rows arrive.
        /// </summary>
        public float LastBufferAheadTicks { get; private set; }
        /// <summary>Classification of the last Tick's sample; null while seeding (Fallback mode).</summary>
        public SampleClass? LastTickSampleClass { get; private set; }

        public void Push(PlayerSnapshot snapshot)
        {
            if (_snapshotCount < SnapshotCapacity)
            {
                int index = (_snapshotStart + _snapshotCount) % SnapshotCapacity;
                _snapshots[index] = snapshot;
                _snapshotCount++;
                return;
            }

            _snapshots[_snapshotStart] = snapshot;
            _snapshotStart = (_snapshotStart + 1) % SnapshotCapacity;
        }

        public void ResetSnapshots()
        {
            _snapshotStart = 0;
            _snapshotCount = 0;
        }

        /// <summary>Sets the render pose directly, bypassing smoothing (seeding/teleports).</summary>
        public void ForceRenderPose(Vector3 position, float yawRadians)
        {
            _renderPos = position;
            _renderYaw = yawRadians;
        }

        /// <summary>
        /// Advances the render pose one frame toward the target sampled on
        /// the active timeline: server time at serverNowMs − ServerTimeDelayMs
        /// when usable, otherwise arrival time at now − InterpolationDelaySeconds.
        /// serverNowMs is the caller's ArenaServerClock.ServerNowMs, or null
        /// while it has no estimate. The fallback pose is used while the ring
        /// is empty (latest authoritative state from the caller).
        /// globalRowDeliveryHealthy is the settled-vs-starved discriminator's
        /// global input (NetcodeReceiveCounters.RowDeliveryFresh at the call
        /// site); it feeds sample classification only, never the pose.
        /// </summary>
        public void Tick(float dt, float now, long? serverNowMs, Vector3 fallbackPosition, float fallbackYawRadians,
            bool globalRowDeliveryHealthy)
        {
            SampleActiveTimeline(now, serverNowMs, fallbackPosition, fallbackYawRadians,
                out Vector3 targetPos, out float targetYaw, out SampleMode sampleMode,
                out bool usedServerTimeline);

            SampleClass? sampleClass = sampleMode == SampleMode.Fallback
                ? (SampleClass?)null
                : ClassifySample(
                    sampleMode,
                    _lastExtrapolationUnclampedSeconds,
                    MaxExtrapolationSeconds,
                    RowCadence,
                    globalRowDeliveryHealthy);
            LastTickSampleClass = sampleClass;
            switch (sampleClass)
            {
                case SampleClass.Interpolated:
                    _interpolationSampleCount++;
                    break;
                case SampleClass.Extrapolating:
                    _extrapolationSampleCount++;
                    break;
                case SampleClass.Starved:
                    _starvedSampleCount++;
                    break;
                case SampleClass.Settled:
                    _settledSampleCount++;
                    break;
            }

            LastTickUsedServerTimeline = usedServerTimeline;
            LastEffectiveDelayMs = usedServerTimeline
                ? ServerTimeDelayMs
                : InterpolationDelaySeconds * 1000f;
            float bufferAheadTicks = 0f;
            if (_snapshotCount > 0)
            {
                PlayerSnapshot newest = GetSnapshot(_snapshotCount - 1);
                bufferAheadTicks = usedServerTimeline
                    ? (newest.ServerTimeMs - (serverNowMs!.Value - ServerTimeDelayMs))
                      / (float)MovementNetcodeConfig.FixedTickMilliseconds
                    : (newest.ReceivedTime - (now - InterpolationDelaySeconds))
                      / MovementNetcodeConfig.FixedTickSeconds;
            }
            LastBufferAheadTicks = sampleClass.HasValue
                ? ReportableBufferAheadTicks(bufferAheadTicks, sampleClass.Value, MaxExtrapolationSeconds)
                : bufferAheadTicks;

            float positionError = Vector3.Distance(_renderPos, targetPos);
            float yawError = Mathf.Abs(DeltaAngleRadians(_renderYaw, targetYaw));
            _lastPositionError = positionError;
            if (positionError > _maxPositionErrorObserved)
                _maxPositionErrorObserved = positionError;

            if (positionError >= HardSnapDistance || yawError >= HardSnapYawRadians)
            {
                _renderPos = targetPos;
                _renderYaw = targetYaw;
                _hardSnapCount++;
                return;
            }

            float t = Mathf.Min(1f, SmoothingSpeed * dt);
            _renderPos = Vector3.Lerp(_renderPos, targetPos, t);
            _renderYaw = LerpAngle(_renderYaw, targetYaw, t);
            _smoothUpdateCount++;
        }

        public void Sample(
            float renderTime,
            Vector3 fallbackPosition,
            float fallbackYawRadians,
            out Vector3 position,
            out float yaw,
            out SampleMode mode)
        {
            if (_snapshotCount <= 0)
            {
                position = fallbackPosition;
                yaw = fallbackYawRadians;
                mode = SampleMode.Fallback;
                _lastExtrapolationSeconds = 0.0f;
                _lastExtrapolationUnclampedSeconds = 0.0f;
                return;
            }

            PlayerSnapshot oldest = GetSnapshot(0);
            if (_snapshotCount == 1 || renderTime <= oldest.ReceivedTime)
            {
                position = oldest.Position;
                yaw = oldest.Yaw;
                mode = SampleMode.Fallback;
                _lastExtrapolationSeconds = 0.0f;
                _lastExtrapolationUnclampedSeconds = 0.0f;
                return;
            }

            for (int i = 1; i < _snapshotCount; i++)
            {
                PlayerSnapshot newer = GetSnapshot(i);
                if (renderTime > newer.ReceivedTime)
                    continue;

                PlayerSnapshot older = GetSnapshot(i - 1);
                float interval = Mathf.Max(0.0001f, newer.ReceivedTime - older.ReceivedTime);
                float t = Mathf.Clamp01((renderTime - older.ReceivedTime) / interval);
                position = Vector3.Lerp(older.Position, newer.Position, t);
                yaw = LerpAngle(older.Yaw, newer.Yaw, t);
                mode = SampleMode.Interpolation;
                _lastExtrapolationSeconds = 0.0f;
                _lastExtrapolationUnclampedSeconds = 0.0f;
                return;
            }

            PlayerSnapshot latest = GetSnapshot(_snapshotCount - 1);
            float extrapolation = Mathf.Clamp(
                renderTime - latest.ReceivedTime,
                0f,
                MaxExtrapolationSeconds);

            position = latest.Position + latest.Velocity * extrapolation;
            yaw = latest.Yaw;
            mode = SampleMode.Extrapolation;
            _lastExtrapolationSeconds = extrapolation;
            _lastExtrapolationUnclampedSeconds = renderTime - latest.ReceivedTime;
        }

        /// <summary>
        /// Samples the server-time timeline: same interpolation-first policy
        /// as Sample, keyed on PlayerSnapshot.ServerTimeMs instead of
        /// arrival time. renderServerTimeMs is typically
        /// serverNowMs − ServerTimeDelayMs.
        /// </summary>
        public void SampleServerTime(
            long renderServerTimeMs,
            Vector3 fallbackPosition,
            float fallbackYawRadians,
            out Vector3 position,
            out float yaw,
            out SampleMode mode)
        {
            if (_snapshotCount <= 0)
            {
                position = fallbackPosition;
                yaw = fallbackYawRadians;
                mode = SampleMode.Fallback;
                _lastExtrapolationSeconds = 0.0f;
                _lastExtrapolationUnclampedSeconds = 0.0f;
                return;
            }

            PlayerSnapshot oldest = GetSnapshot(0);
            if (_snapshotCount == 1 || renderServerTimeMs <= oldest.ServerTimeMs)
            {
                position = oldest.Position;
                yaw = oldest.Yaw;
                mode = SampleMode.Fallback;
                _lastExtrapolationSeconds = 0.0f;
                _lastExtrapolationUnclampedSeconds = 0.0f;
                return;
            }

            for (int i = 1; i < _snapshotCount; i++)
            {
                PlayerSnapshot newer = GetSnapshot(i);
                if (renderServerTimeMs > newer.ServerTimeMs)
                    continue;

                PlayerSnapshot older = GetSnapshot(i - 1);
                float intervalMs = Mathf.Max(1f, newer.ServerTimeMs - older.ServerTimeMs);
                float t = Mathf.Clamp01((renderServerTimeMs - older.ServerTimeMs) / intervalMs);
                position = Vector3.Lerp(older.Position, newer.Position, t);
                yaw = LerpAngle(older.Yaw, newer.Yaw, t);
                mode = SampleMode.Interpolation;
                _lastExtrapolationSeconds = 0.0f;
                _lastExtrapolationUnclampedSeconds = 0.0f;
                return;
            }

            PlayerSnapshot latest = GetSnapshot(_snapshotCount - 1);
            float extrapolation = Mathf.Clamp(
                (renderServerTimeMs - latest.ServerTimeMs) / 1000f,
                0f,
                MaxExtrapolationSeconds);

            position = latest.Position + latest.Velocity * extrapolation;
            yaw = latest.Yaw;
            mode = SampleMode.Extrapolation;
            _lastExtrapolationSeconds = extrapolation;
            _lastExtrapolationUnclampedSeconds = (renderServerTimeMs - latest.ServerTimeMs) / 1000f;
        }

        /// <summary>
        /// Samples whichever timeline Tick would use for the given clocks
        /// (server time when enabled, clocked, and every buffered snapshot
        /// carries ServerTimeMs; arrival time otherwise).
        /// </summary>
        public void SampleActiveTimeline(
            float now,
            long? serverNowMs,
            Vector3 fallbackPosition,
            float fallbackYawRadians,
            out Vector3 position,
            out float yaw,
            out SampleMode mode,
            out bool usedServerTimeline)
        {
            usedServerTimeline = ServerTimeTimelineEnabled
                && serverNowMs.HasValue
                && AllSnapshotsCarryServerTime()
                && ServerTimelineDepthSane(serverNowMs.Value);
            if (usedServerTimeline)
            {
                SampleServerTime(serverNowMs!.Value - ServerTimeDelayMs,
                    fallbackPosition, fallbackYawRadians, out position, out yaw, out mode);
                return;
            }

            Sample(now - InterpolationDelaySeconds,
                fallbackPosition, fallbackYawRadians, out position, out yaw, out mode);
        }

        /// <summary>
        /// Idle-aware sample classification (design review S1), pure so the
        /// settled-vs-starved discriminator is trivially testable.
        /// extrapolationSecondsUnclamped is the raw distance past the newest
        /// snapshot, NOT clamped to the cap; a sample exactly at the cap
        /// counts as past it. Past the cap, an EveryTick entity is always
        /// starved (its rows only stop when delivery is late), while a
        /// StopsWhenIdle entity is settled while global row delivery is
        /// healthy and starved once it has stalled. Fallback samples
        /// (empty/seeding ring) are not classified — callers must not pass
        /// SampleMode.Fallback.
        /// </summary>
        public static SampleClass ClassifySample(
            SampleMode mode,
            float extrapolationSecondsUnclamped,
            float maxExtrapolationSeconds,
            SourceRowCadence sourceRowCadence,
            bool globalRowDeliveryHealthy)
        {
            if (mode != SampleMode.Extrapolation)
                return SampleClass.Interpolated;

            if (extrapolationSecondsUnclamped < maxExtrapolationSeconds)
                return SampleClass.Extrapolating;

            if (sourceRowCadence == SourceRowCadence.EveryTick)
                return SampleClass.Starved;

            return globalRowDeliveryHealthy ? SampleClass.Settled : SampleClass.Starved;
        }

        /// <summary>
        /// Buffer-depth reporting rule (design review S1): a settled entity is
        /// authoritatively at rest, so its depth pins at the extrapolation-cap
        /// boundary instead of diving unboundedly negative while no rows
        /// arrive (observed live: −241 ticks on an idle kobold). Non-settled
        /// samples report the raw depth — for a starved entity the dive is
        /// the signal.
        /// </summary>
        public static float ReportableBufferAheadTicks(
            float bufferAheadTicks,
            SampleClass sampleClass,
            float maxExtrapolationSeconds)
        {
            if (sampleClass != SampleClass.Settled)
                return bufferAheadTicks;

            return Mathf.Max(
                bufferAheadTicks,
                -maxExtrapolationSeconds / MovementNetcodeConfig.FixedTickSeconds);
        }

        /// <summary>
        /// Maps a replicated row timestamp (UpdatedAt micros) onto the
        /// snapshot server timeline: epoch ms rounded to the nearest
        /// fixed-tick grid point, removing sub-tick server-side write jitter.
        /// </summary>
        public static long QuantizeServerTimeMicros(long serverTimestampMicros)
        {
            const long tickMs = MovementNetcodeConfig.FixedTickMilliseconds;
            long ms = serverTimestampMicros / 1000L;
            return (ms + tickMs / 2L) / tickMs * tickMs;
        }

        // Sanity window for engaging the server-time timeline (design review
        // S5 / F4 warmup gating): while the clock estimate is converging, the
        // implied buffer depth can be wildly wrong (observed live: a session
        // that started at −1739 ticks in an extrapolation storm). A healthy
        // render point trails the newest snapshot by roughly ServerTimeDelayMs;
        // outside ±1 s of that the clock, not delivery, is the problem — use
        // the arrival timeline this frame instead.
        private const long ServerTimelineDepthSanityWindowMs = 1000L;

        private bool ServerTimelineDepthSane(long serverNowMs)
        {
            if (_snapshotCount <= 0)
                return false;

            long renderPointMs = serverNowMs - ServerTimeDelayMs;
            long newestLagMs = GetSnapshot(_snapshotCount - 1).ServerTimeMs - renderPointMs;
            return newestLagMs > -ServerTimelineDepthSanityWindowMs
                && newestLagMs < ServerTimelineDepthSanityWindowMs;
        }

        private bool AllSnapshotsCarryServerTime()
        {
            if (_snapshotCount <= 0)
                return false;

            for (int i = 0; i < _snapshotCount; i++)
            {
                if (GetSnapshot(i).ServerTimeMs <= 0L)
                    return false;
            }

            return true;
        }

        private PlayerSnapshot GetSnapshot(int index)
        {
            int arrayIndex = (_snapshotStart + index) % SnapshotCapacity;
            return _snapshots[arrayIndex];
        }

        private static float LerpAngle(float a, float b, float t)
        {
            float delta = DeltaAngleRadians(a, b);
            return a + delta * t;
        }

        private static float DeltaAngleRadians(float a, float b)
        {
            return Mathf.Repeat(b - a + Mathf.PI, Mathf.PI * 2f) - Mathf.PI;
        }
    }
}

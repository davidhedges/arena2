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
    /// </summary>
    public sealed class RemotePresentationBuffer
    {
        public enum SampleMode
        {
            Fallback,
            Interpolation,
            Extrapolation,
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
        private float _lastPositionError;
        private float _maxPositionErrorObserved;
        private float _lastExtrapolationSeconds;

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
        public int ExtrapolationSampleCount => _extrapolationSampleCount;
        public float LastPositionError => _lastPositionError;
        public float MaxPositionErrorObserved => _maxPositionErrorObserved;
        public float LastExtrapolationSeconds => _lastExtrapolationSeconds;
        /// <summary>Whether the last Tick sampled the server-time timeline (vs arrival fallback).</summary>
        public bool LastTickUsedServerTimeline { get; private set; }
        /// <summary>Render delay the last Tick actually applied, in ms (100 server-time / 66 arrival).</summary>
        public float LastEffectiveDelayMs { get; private set; } = DefaultInterpolationDelaySeconds * 1000f;
        /// <summary>Buffered headroom ahead of the last Tick's render point, in fixed ticks (negative = extrapolating).</summary>
        public float LastBufferAheadTicks { get; private set; }

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
        /// </summary>
        public void Tick(float dt, float now, long? serverNowMs, Vector3 fallbackPosition, float fallbackYawRadians)
        {
            SampleActiveTimeline(now, serverNowMs, fallbackPosition, fallbackYawRadians,
                out Vector3 targetPos, out float targetYaw, out SampleMode sampleMode,
                out bool usedServerTimeline);

            LastTickUsedServerTimeline = usedServerTimeline;
            LastEffectiveDelayMs = usedServerTimeline
                ? ServerTimeDelayMs
                : InterpolationDelaySeconds * 1000f;
            LastBufferAheadTicks = 0f;
            if (_snapshotCount > 0)
            {
                PlayerSnapshot newest = GetSnapshot(_snapshotCount - 1);
                LastBufferAheadTicks = usedServerTimeline
                    ? (newest.ServerTimeMs - (serverNowMs!.Value - ServerTimeDelayMs))
                      / (float)MovementNetcodeConfig.FixedTickMilliseconds
                    : (newest.ReceivedTime - (now - InterpolationDelaySeconds))
                      / MovementNetcodeConfig.FixedTickSeconds;
            }

            float positionError = Vector3.Distance(_renderPos, targetPos);
            float yawError = Mathf.Abs(DeltaAngleRadians(_renderYaw, targetYaw));
            _lastPositionError = positionError;
            if (positionError > _maxPositionErrorObserved)
                _maxPositionErrorObserved = positionError;

            switch (sampleMode)
            {
                case SampleMode.Interpolation:
                    _interpolationSampleCount++;
                    break;
                case SampleMode.Extrapolation:
                    _extrapolationSampleCount++;
                    break;
            }

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
                return;
            }

            PlayerSnapshot oldest = GetSnapshot(0);
            if (_snapshotCount == 1 || renderTime <= oldest.ReceivedTime)
            {
                position = oldest.Position;
                yaw = oldest.Yaw;
                mode = SampleMode.Fallback;
                _lastExtrapolationSeconds = 0.0f;
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
                return;
            }

            PlayerSnapshot oldest = GetSnapshot(0);
            if (_snapshotCount == 1 || renderServerTimeMs <= oldest.ServerTimeMs)
            {
                position = oldest.Position;
                yaw = oldest.Yaw;
                mode = SampleMode.Fallback;
                _lastExtrapolationSeconds = 0.0f;
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
                && AllSnapshotsCarryServerTime();
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

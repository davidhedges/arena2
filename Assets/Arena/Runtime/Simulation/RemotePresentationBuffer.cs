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
    /// Time is passed in by the caller so the math stays testable; the
    /// timeline is client-arrival-based (PlayerSnapshot.ReceivedTime) until
    /// feel-audit F4 moves it to server time inside this class.
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
        /// Advances the render pose one frame toward the target sampled at
        /// now − InterpolationDelaySeconds. The fallback pose is used while
        /// the ring is empty (latest authoritative state from the caller).
        /// </summary>
        public void Tick(float dt, float now, Vector3 fallbackPosition, float fallbackYawRadians)
        {
            float renderTime = now - InterpolationDelaySeconds;
            Sample(renderTime, fallbackPosition, fallbackYawRadians,
                out Vector3 targetPos, out float targetYaw, out SampleMode sampleMode);

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

using UnityEngine;
using Arena.Input;
using Arena.Network;
namespace Arena.Simulation
{
    /// <summary>
    /// Stores authoritative snapshots and derives a remote-player visual pose.
    /// The snapshot ring, render-target sampling, and smoothing/hard-snap
    /// policy live in RemotePresentationBuffer (shared with NPCs); this class
    /// delegates to one buffer instance and layers player-specific state on
    /// top (movement context, special movement tracks, prediction inputs).
    ///
    /// For the local player, predicted state drives position/rotation and this
    /// class remains the container for authoritative snapshots.
    ///
    /// INVARIANT: Only EntityRegistry writes via PushSnapshot.
    ///            Only PlayerView reads via GetRender* and calls Tick (remote players only).
    /// </summary>
    public class ClientSimulationState
    {
        public readonly struct RemoteObserverSample
        {
            public RemoteObserverSample(
                Vector3 position,
                float yawRadians,
                float interpolationDelaySeconds,
                float extrapolationSeconds,
                bool isExtrapolating)
            {
                Position = position;
                YawRadians = yawRadians;
                InterpolationDelaySeconds = interpolationDelaySeconds;
                ExtrapolationSeconds = extrapolationSeconds;
                IsExtrapolating = isExtrapolating;
            }

            public Vector3 Position { get; }
            public float YawRadians { get; }
            public float InterpolationDelaySeconds { get; }
            public float ExtrapolationSeconds { get; }
            public bool IsExtrapolating { get; }
        }

        public readonly struct MovementContextSample
        {
            public MovementContextSample(
                uint tick,
                bool movementBlocked,
                float moveSpeedMultiplier,
                float hitRadius,
                float hitHeight)
            {
                Tick = tick;
                MovementBlocked = movementBlocked;
                MoveSpeedMultiplier = moveSpeedMultiplier;
                HitRadius = hitRadius;
                HitHeight = hitHeight;
            }

            public uint Tick { get; }
            public bool MovementBlocked { get; }
            public float MoveSpeedMultiplier { get; }
            public float HitRadius { get; }
            public float HitHeight { get; }
        }

        public readonly struct MovementRestrictionSample
        {
            public MovementRestrictionSample(
                uint tick,
                bool movementBlocked,
                float minMoveSpeedMultiplier,
                float maxMoveSpeedMultiplier)
            {
                Tick = tick;
                MovementBlocked = movementBlocked;
                MinMoveSpeedMultiplier = minMoveSpeedMultiplier;
                MaxMoveSpeedMultiplier = maxMoveSpeedMultiplier;
            }

            public uint Tick { get; }
            public bool MovementBlocked { get; }
            public float MinMoveSpeedMultiplier { get; }
            public float MaxMoveSpeedMultiplier { get; }
        }

        private const int MovementContextCapacity = 24;
        private const int MovementRestrictionCapacity = 12;

        // Latest server state (authoritative)
        private Vector3 _serverPos;
        private Vector3 _serverVelocity;
        private float _serverYaw; // radians

        // Snapshot ring + render pose for remote players.
        private readonly RemotePresentationBuffer _remotePresentation = new();
        private uint _movementContextVersion;

        private readonly MovementContextSample[] _movementContextSamples = new MovementContextSample[MovementContextCapacity];
        private int _movementContextStart;
        private int _movementContextCount;
        private readonly MovementRestrictionSample[] _movementRestrictionSamples = new MovementRestrictionSample[MovementRestrictionCapacity];
        private int _movementRestrictionStart;
        private int _movementRestrictionCount;

        private bool _hasAny;
        private bool _isLocalPlayer;
        private bool _hasSpecialMovementTrack;
        private bool _hasMovementActionState;
        private bool _grounded;
        private uint _lastProcessedTick;
        private uint _authoritativeSnapshotVersion;
        private float _lastSnapshotReceivedTime;
        private SpecialMovementTrack _specialMovementTrack;
        private SpacetimeDB.Types.MovementActionState _movementActionState = new();
        private float _predictedRestrictionBaselineMoveSpeedMultiplier = 1.0f;

        public bool HasState => _hasAny;
        public bool IsGrounded => _grounded;
        public uint LastProcessedTick => _lastProcessedTick;
        public uint AuthoritativeSnapshotVersion => _authoritativeSnapshotVersion;
        public uint MovementContextVersion => _movementContextVersion;
        public float LastSnapshotReceivedTime => _lastSnapshotReceivedTime;
        public bool MovementBlocked => LatestMovementContext.MovementBlocked;
        public float MoveSpeedMultiplier => LatestMovementContext.MoveSpeedMultiplier;
        public float HitRadius => LatestMovementContext.HitRadius;
        public float HitHeight => LatestMovementContext.HitHeight;
        public uint MovementContextTick => LatestMovementContext.Tick;
        public int RemoteSnapshotCount => _remotePresentation.SnapshotCount;
        public int RemoteHardSnapCount => _remotePresentation.HardSnapCount;
        public int RemoteSmoothUpdateCount => _remotePresentation.SmoothUpdateCount;
        public int RemoteInterpolationSampleCount => _remotePresentation.InterpolationSampleCount;
        public int RemoteExtrapolationSampleCount => _remotePresentation.ExtrapolationSampleCount;
        public float LastRemotePositionError => _remotePresentation.LastPositionError;
        public float MaxRemotePositionErrorObserved => _remotePresentation.MaxPositionErrorObserved;
        public float LastRemoteExtrapolationSeconds => _remotePresentation.LastExtrapolationSeconds;
        public float PredictedRestrictionBaselineMoveSpeedMultiplier => _predictedRestrictionBaselineMoveSpeedMultiplier;
        public float RemoteInterpolationDelaySecondsForDebug => _remotePresentation.InterpolationDelaySeconds;
        public float RemoteMaxExtrapolationSecondsForDebug => _remotePresentation.MaxExtrapolationSeconds;
        public bool RemoteUsedServerTimelineForDebug => _remotePresentation.LastTickUsedServerTimeline;
        public float RemoteEffectiveDelayMsForDebug => _remotePresentation.LastEffectiveDelayMs;
        public float RemoteBufferAheadTicksForDebug => _remotePresentation.LastBufferAheadTicks;

        public void SetIsLocalPlayer(bool isLocal)
        {
            _isLocalPlayer = isLocal;
        }

        public void SetSpecialMovementRuntime(SpacetimeDB.Types.SpecialMovementRuntime row)
        {
            _specialMovementTrack = SpecialMovementTrack.FromRow(row);
            _hasSpecialMovementTrack = true;
        }

        public void SetMovementActionState(SpacetimeDB.Types.MovementActionState row)
        {
            _movementActionState = row;
            _hasMovementActionState = true;
        }

        public void ClearMovementActionState()
        {
            _movementActionState = new SpacetimeDB.Types.MovementActionState();
            _hasMovementActionState = false;
        }

        public void ClearSpecialMovementRuntime()
        {
            if (_hasSpecialMovementTrack)
            {
                long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_isLocalPlayer)
                    SeedLocalAuthoritativePositionFromSpecialMovementEnd(_specialMovementTrack, nowMs);
                else
                    SeedRemoteInterpolationFromSpecialMovementEnd(_specialMovementTrack, nowMs);
            }
            _hasSpecialMovementTrack = false;
            _specialMovementTrack = default;
        }

        private void SeedLocalAuthoritativePositionFromSpecialMovementEnd(
            SpecialMovementTrack track,
            long nowMs)
        {
            SampledSpecialMovementPose sampled = SpecialMovementRuntimeSampler.Sample(track, nowMs);
            _serverPos = sampled.Position;
            _serverVelocity = Vector3.zero;
            _serverYaw = sampled.FacingYawRadians;
            _remotePresentation.ForceRenderPose(sampled.Position, sampled.FacingYawRadians);
            _lastSnapshotReceivedTime = Time.realtimeSinceStartup;
            _authoritativeSnapshotVersion++;
            _hasAny = true;
        }

        private void SeedRemoteInterpolationFromSpecialMovementEnd(
            SpecialMovementTrack track,
            long nowMs)
        {
            SampledSpecialMovementPose sampled = SpecialMovementRuntimeSampler.Sample(track, nowMs);
            _remotePresentation.ForceRenderPose(sampled.Position, sampled.FacingYawRadians);

            _remotePresentation.ResetSnapshots();
            // The synthetic seed has no server timestamp (ServerTimeMs = 0),
            // which holds the buffer on the arrival timeline until fresh
            // authoritative rows push it out of the ring.
            _remotePresentation.Push(new PlayerSnapshot(
                sampled.Position.x,
                sampled.Position.y,
                sampled.Position.z,
                0.0f,
                0.0f,
                0.0f,
                sampled.FacingYawRadians,
                _grounded,
                _lastProcessedTick));
        }

        public bool TryGetMovementActionState(out SpacetimeDB.Types.MovementActionState row)
        {
            row = _movementActionState;
            return _hasMovementActionState;
        }

        public bool TryGetSpecialMovementTrack(out SpecialMovementTrack track)
        {
            track = _specialMovementTrack;
            return _hasSpecialMovementTrack;
        }

        public void PushSnapshot(PlayerSnapshot snapshot)
        {
            _serverPos = snapshot.Position;
            _serverVelocity = snapshot.Velocity;
            _serverYaw = snapshot.Yaw;
            _grounded  = snapshot.Grounded;
            _lastProcessedTick = snapshot.LastProcessedTick;
            _lastSnapshotReceivedTime = snapshot.ReceivedTime;
            _authoritativeSnapshotVersion++;
            _remotePresentation.Push(snapshot);

            if (!_hasAny)
            {
                _remotePresentation.ForceRenderPose(_serverPos, _serverYaw);
                _hasAny = true;
            }
        }

        public void SetMovementContext(
            bool movementBlocked,
            float moveSpeedMultiplier,
            float hitRadius,
            float hitHeight,
            uint movementContextTick)
        {
            MovementContextSample incoming = new(
                movementContextTick,
                movementBlocked,
                Mathf.Clamp(moveSpeedMultiplier, 0.0f, 4.0f),
                Mathf.Max(hitRadius, 0.1f),
                Mathf.Max(hitHeight, Mathf.Max(hitRadius, 0.1f) * 2.0f));

            if (_movementContextCount > 0)
            {
                MovementContextSample newest = GetMovementContextSample(_movementContextCount - 1);
                if (incoming.Tick < newest.Tick)
                    return;

                if (incoming.Tick == newest.Tick)
                {
                    if (MovementContextSamplesEqual(newest, incoming))
                        return;

                    int newestIndex = (_movementContextStart + _movementContextCount - 1) % MovementContextCapacity;
                    _movementContextSamples[newestIndex] = incoming;
                    _movementContextVersion++;
                    return;
                }
            }

            AppendMovementContextSample(incoming);
            _movementContextVersion++;
        }

        public void SetPredictedMovementRestriction(
            uint movementContextTick,
            bool movementBlocked,
            float minMoveSpeedMultiplier,
            float maxMoveSpeedMultiplier)
        {
            MovementRestrictionSample incoming = new(
                movementContextTick,
                movementBlocked,
                Mathf.Clamp(minMoveSpeedMultiplier, 0.0f, 4.0f),
                Mathf.Clamp(maxMoveSpeedMultiplier, 0.0f, 4.0f));

            if (_movementRestrictionCount > 0)
            {
                MovementRestrictionSample newest = GetMovementRestrictionSample(_movementRestrictionCount - 1);
                if (incoming.Tick < newest.Tick)
                    return;

                if (incoming.Tick == newest.Tick)
                {
                    if (MovementRestrictionSamplesEqual(newest, incoming))
                        return;

                    int newestIndex = (_movementRestrictionStart + _movementRestrictionCount - 1) % MovementRestrictionCapacity;
                    _movementRestrictionSamples[newestIndex] = incoming;
                    _movementContextVersion++;
                    return;
                }
            }

            AppendMovementRestrictionSample(incoming);
            _movementContextVersion++;
        }

        public void ClearPredictedMovementRestrictions()
        {
            if (_movementRestrictionCount == 0)
                return;

            _movementRestrictionStart = 0;
            _movementRestrictionCount = 0;
            _movementContextVersion++;
        }

        public void SetPredictedRestrictionBaselineMoveSpeedMultiplier(float moveSpeedMultiplier)
        {
            _predictedRestrictionBaselineMoveSpeedMultiplier =
                Mathf.Clamp(moveSpeedMultiplier, 0.0f, 4.0f);
        }

        public MovementContextSample GetMovementContextForTick(uint tick, out bool usedFallback)
        {
            MovementContextSample baseSample = DefaultMovementContext;
            bool foundBaseSample = false;
            for (int i = _movementContextCount - 1; i >= 0; i--)
            {
                MovementContextSample sample = GetMovementContextSample(i);
                if (sample.Tick > tick)
                    continue;

                baseSample = sample;
                foundBaseSample = true;
                break;
            }

            MovementRestrictionSample? restriction = GetMovementRestrictionForTick(tick);
            if (!restriction.HasValue)
            {
                usedFallback = !foundBaseSample;
                return baseSample;
            }

            MovementRestrictionSample predictedRestriction = restriction.Value;
            usedFallback = false;
            return new MovementContextSample(
                foundBaseSample
                    ? (baseSample.Tick >= predictedRestriction.Tick ? baseSample.Tick : predictedRestriction.Tick)
                    : predictedRestriction.Tick,
                baseSample.MovementBlocked || predictedRestriction.MovementBlocked,
                Mathf.Clamp(
                    baseSample.MoveSpeedMultiplier,
                    predictedRestriction.MinMoveSpeedMultiplier,
                    predictedRestriction.MaxMoveSpeedMultiplier),
                baseSample.HitRadius,
                baseSample.HitHeight);
        }

        /// <summary>
        /// Called each frame by PlayerView for remote players.
        /// Interpolates between buffered authoritative snapshots by default and
        /// only allows short bounded velocity-only extrapolation when delayed.
        /// </summary>
        public void Tick(float dt)
        {
            if (!_hasAny || _isLocalPlayer) return;

            if (_hasSpecialMovementTrack)
            {
                long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SampledSpecialMovementPose sampled =
                    SpecialMovementRuntimeSampler.Sample(_specialMovementTrack, nowMs);
                _remotePresentation.ForceRenderPose(sampled.Position, sampled.FacingYawRadians);
                return;
            }

            _remotePresentation.Tick(
                dt,
                Time.realtimeSinceStartup,
                ArenaServerClock.HasEstimate ? ArenaServerClock.ServerNowMs : (long?)null,
                _serverPos,
                _serverYaw);
        }

        public Vector3 GetRenderPosition() => _remotePresentation.RenderPosition;

        /// <summary>
        /// Returns the raw server-authoritative position (no interpolation).
        /// Used by MovementNetDriver for position reconciliation.
        /// </summary>
        public Vector3 GetServerPosition() => _serverPos;
        public Vector3 GetServerVelocity() => _serverVelocity;
        public float GetServerYawRadians() => _serverYaw;
        public float EstimateAuthoritativeTick(float now, float tickSeconds)
        {
            if (!_hasAny || tickSeconds <= 0.0f)
                return _lastProcessedTick;

            float elapsed = Mathf.Max(0.0f, now - _lastSnapshotReceivedTime);
            return _lastProcessedTick + elapsed / tickSeconds;
        }

        public float GetRenderYawDegrees() => _remotePresentation.RenderYawRadians * Mathf.Rad2Deg;

        public bool TryGetRemoteObserverSample(
            float now,
            out RemoteObserverSample sample)
        {
            if (!_hasAny)
            {
                sample = default;
                return false;
            }

            if (_hasSpecialMovementTrack)
            {
                long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SampledSpecialMovementPose specialMovementSample =
                    SpecialMovementRuntimeSampler.Sample(_specialMovementTrack, nowMs);
                sample = new RemoteObserverSample(
                    specialMovementSample.Position,
                    specialMovementSample.FacingYawRadians,
                    0.0f,
                    0.0f,
                    false);
                return true;
            }

            _remotePresentation.SampleActiveTimeline(
                now,
                ArenaServerClock.HasEstimate ? ArenaServerClock.ServerNowMs : (long?)null,
                _serverPos,
                _serverYaw,
                out Vector3 position,
                out float yaw,
                out RemotePresentationBuffer.SampleMode mode,
                out bool usedServerTimeline);
            sample = new RemoteObserverSample(
                position,
                yaw,
                usedServerTimeline
                    ? _remotePresentation.ServerTimeDelayMs / 1000f
                    : _remotePresentation.InterpolationDelaySeconds,
                _remotePresentation.LastExtrapolationSeconds,
                mode == RemotePresentationBuffer.SampleMode.Extrapolation);
            return true;
        }

        private MovementContextSample LatestMovementContext =>
            _movementContextCount > 0
                ? GetMovementContextSample(_movementContextCount - 1)
                : DefaultMovementContext;

        private static MovementContextSample DefaultMovementContext => new(
            0u,
            false,
            1.0f,
            MovementPrediction.DefaultHitRadius,
            MovementPrediction.DefaultHitHeight);

        private void AppendMovementContextSample(MovementContextSample sample)
        {
            if (_movementContextCount < MovementContextCapacity)
            {
                int index = (_movementContextStart + _movementContextCount) % MovementContextCapacity;
                _movementContextSamples[index] = sample;
                _movementContextCount++;
                return;
            }

            _movementContextSamples[_movementContextStart] = sample;
            _movementContextStart = (_movementContextStart + 1) % MovementContextCapacity;
        }

        private void AppendMovementRestrictionSample(MovementRestrictionSample sample)
        {
            if (_movementRestrictionCount < MovementRestrictionCapacity)
            {
                int index = (_movementRestrictionStart + _movementRestrictionCount) % MovementRestrictionCapacity;
                _movementRestrictionSamples[index] = sample;
                _movementRestrictionCount++;
                return;
            }

            _movementRestrictionSamples[_movementRestrictionStart] = sample;
            _movementRestrictionStart = (_movementRestrictionStart + 1) % MovementRestrictionCapacity;
        }

        private MovementContextSample GetMovementContextSample(int index)
        {
            int arrayIndex = (_movementContextStart + index) % MovementContextCapacity;
            return _movementContextSamples[arrayIndex];
        }

        private MovementRestrictionSample GetMovementRestrictionSample(int index)
        {
            int arrayIndex = (_movementRestrictionStart + index) % MovementRestrictionCapacity;
            return _movementRestrictionSamples[arrayIndex];
        }

        private MovementRestrictionSample? GetMovementRestrictionForTick(uint tick)
        {
            for (int i = _movementRestrictionCount - 1; i >= 0; i--)
            {
                MovementRestrictionSample sample = GetMovementRestrictionSample(i);
                if (sample.Tick > tick)
                    continue;

                return sample;
            }

            return null;
        }

        private static bool MovementContextSamplesEqual(MovementContextSample a, MovementContextSample b)
        {
            return a.Tick == b.Tick &&
                   a.MovementBlocked == b.MovementBlocked &&
                   Mathf.Approximately(a.MoveSpeedMultiplier, b.MoveSpeedMultiplier) &&
                   Mathf.Approximately(a.HitRadius, b.HitRadius) &&
                   Mathf.Approximately(a.HitHeight, b.HitHeight);
        }

        private static bool MovementRestrictionSamplesEqual(
            MovementRestrictionSample a,
            MovementRestrictionSample b)
        {
            return a.Tick == b.Tick &&
                   a.MovementBlocked == b.MovementBlocked &&
                   Mathf.Approximately(a.MinMoveSpeedMultiplier, b.MinMoveSpeedMultiplier) &&
                   Mathf.Approximately(a.MaxMoveSpeedMultiplier, b.MaxMoveSpeedMultiplier);
        }
    }
}

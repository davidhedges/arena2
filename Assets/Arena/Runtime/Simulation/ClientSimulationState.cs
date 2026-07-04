using UnityEngine;
using Arena.Debugging;
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

        /// <summary>
        /// One authoritative ack tick's consume truth (design review S5),
        /// recorded per snapshot so the input lead control loop sees every
        /// tick even when several rows arrive in one frame.
        /// </summary>
        public readonly struct InputAckSample
        {
            public InputAckSample(uint tick, bool consumedCommand, int bufferedCommands)
            {
                Tick = tick;
                ConsumedCommand = consumedCommand;
                BufferedCommands = bufferedCommands;
            }

            public uint Tick { get; }
            public bool ConsumedCommand { get; }
            public int BufferedCommands { get; }
        }

        private const int MovementContextCapacity = 24;
        private const int MovementRestrictionCapacity = 12;
        private const int InputAckCapacity = 64;
        private const int AuthoritativeJumpCapacity = 8;
        // A precise-clock estimate this far behind the newest snapshot's own
        // stamp is not believable — fall back to the arrival anchor.
        private const double PreciseEstimateInsaneBehindMs = 250.0;

        // Latest server state (authoritative)
        private Vector3 _serverPos;
        private Vector3 _serverVelocity;
        private float _serverYaw; // radians

        // Snapshot ring + render pose for remote players. EveryTick: the
        // server commits PlayerPhysics (updated_at included) each tick for
        // every live connected player, so no rows past the extrapolation cap
        // always means delivery is late. Known exception: playground/practice
        // dummy targets only commit on change and read as starved while
        // parked — they are debug fixtures, excluded from A/B legs.
        private readonly RemotePresentationBuffer _remotePresentation =
            new(RemotePresentationBuffer.SourceRowCadence.EveryTick);
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
        private long _lastSnapshotServerTimeMs;
        private readonly InputAckSample[] _inputAcks = new InputAckSample[InputAckCapacity];
        private long _inputAckTotal;
        private readonly uint[] _authoritativeJumpTicks = new uint[AuthoritativeJumpCapacity];
        private long _authoritativeJumpTotal;
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
        public int RemoteStarvedSampleCount => _remotePresentation.StarvedSampleCount;
        public int RemoteSettledSampleCount => _remotePresentation.SettledSampleCount;
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
            bool tickAdvanced = _hasAny && snapshot.LastProcessedTick > _lastProcessedTick;
            if (tickAdvanced)
            {
                _inputAcks[_inputAckTotal % InputAckCapacity] = new InputAckSample(
                    snapshot.LastProcessedTick,
                    snapshot.ConsumedCommand,
                    snapshot.BufferedCommands);
                _inputAckTotal++;

                // Authoritative jump edge: grounded -> airborne with upward
                // velocity. Feeds the S5 jump-delivery ledger.
                if (_grounded && !snapshot.Grounded && snapshot.Velocity.y > 0.5f)
                {
                    _authoritativeJumpTicks[_authoritativeJumpTotal % AuthoritativeJumpCapacity] =
                        snapshot.LastProcessedTick;
                    _authoritativeJumpTotal++;
                }
            }

            _serverPos = snapshot.Position;
            _serverVelocity = snapshot.Velocity;
            _serverYaw = snapshot.Yaw;
            _grounded  = snapshot.Grounded;
            _lastProcessedTick = snapshot.LastProcessedTick;
            _lastSnapshotReceivedTime = snapshot.ReceivedTime;
            if (snapshot.ServerTimeMs > 0L)
                _lastSnapshotServerTimeMs = snapshot.ServerTimeMs;
            _authoritativeSnapshotVersion++;
            _remotePresentation.Push(snapshot);

            if (!_hasAny)
            {
                _remotePresentation.ForceRenderPose(_serverPos, _serverYaw);
                _hasAny = true;
            }
        }

        /// <summary>Total ack samples recorded; readers keep their own cursor.</summary>
        public long InputAckTotal => _inputAckTotal;

        /// <summary>Oldest ack sequence still retained in the ring.</summary>
        public long OldestRetainedInputAck =>
            _inputAckTotal > InputAckCapacity ? _inputAckTotal - InputAckCapacity : 0L;

        public InputAckSample GetInputAck(long sequence) =>
            _inputAcks[sequence % InputAckCapacity];

        public long AuthoritativeJumpTotal => _authoritativeJumpTotal;

        public long OldestRetainedAuthoritativeJump =>
            _authoritativeJumpTotal > AuthoritativeJumpCapacity
                ? _authoritativeJumpTotal - AuthoritativeJumpCapacity
                : 0L;

        public uint GetAuthoritativeJumpTick(long sequence) =>
            _authoritativeJumpTicks[sequence % AuthoritativeJumpCapacity];

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

            // F4 warmup gate (S5): the server-time timeline engages only once
            // a precise clock sample exists — the observed-only monotonic-max
            // estimate is biased and mid-convergence, which is how sessions
            // used to start in an extrapolation storm.
            _remotePresentation.Tick(
                dt,
                Time.realtimeSinceStartup,
                ArenaServerClock.HasPreciseSample ? ArenaServerClock.ServerNowMs : (long?)null,
                _serverPos,
                _serverYaw,
                NetcodeReceiveCounters.RowDeliveryFresh(Time.realtimeSinceStartup));
        }

        public Vector3 GetRenderPosition() => _remotePresentation.RenderPosition;

        /// <summary>
        /// Returns the raw server-authoritative position (no interpolation).
        /// Used by MovementNetDriver for position reconciliation.
        /// </summary>
        public Vector3 GetServerPosition() => _serverPos;
        public Vector3 GetServerVelocity() => _serverVelocity;
        public float GetServerYawRadians() => _serverYaw;

        /// <summary>Whether the last EstimateAuthoritativeTick call used the
        /// precise server clock (vs the arrival-anchored fallback).</summary>
        public bool LastTickEstimateUsedPreciseClock { get; private set; }

        /// <summary>
        /// Estimated current server tick (design review S5, clock
        /// unification): anchored on the newest snapshot's own server
        /// timestamp against the precise ArenaServerClock estimate — neither
        /// biased low by the downstream one-way delay nor wobbled by delivery
        /// jitter. Falls back to the pre-S5 arrival-anchored elapsed-time
        /// estimate while no precise clock sample exists (session warmup) or
        /// when the precise estimate is not believable against the snapshot's
        /// stamp (mid-convergence snap).
        /// </summary>
        public float EstimateAuthoritativeTick(float now, float tickSeconds)
        {
            if (!_hasAny || tickSeconds <= 0.0f)
                return _lastProcessedTick;

            if (ArenaServerClock.HasPreciseSample && _lastSnapshotServerTimeMs > 0L)
            {
                double elapsedMs = ArenaServerClock.ServerNowMs - _lastSnapshotServerTimeMs;
                if (elapsedMs > -PreciseEstimateInsaneBehindMs)
                {
                    LastTickEstimateUsedPreciseClock = true;
                    return _lastProcessedTick
                        + Mathf.Max(0.0f, (float)(elapsedMs / (tickSeconds * 1000.0)));
                }
            }

            LastTickEstimateUsedPreciseClock = false;
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
                ArenaServerClock.HasPreciseSample ? ArenaServerClock.ServerNowMs : (long?)null,
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
                    ? _remotePresentation.LastServerTimeBudgetMs / 1000f
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

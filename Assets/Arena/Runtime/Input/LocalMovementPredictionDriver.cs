#nullable enable
using System;
using UnityEngine;
using Arena.Simulation;
using Arena.Network;

namespace Arena.Input
{
    /// <summary>
    /// Owns the local fixed-tick prediction clock, rebuilds from authoritative
    /// snapshots, and applies predicted local state to the player transform.
    /// </summary>
    [DefaultExecutionOrder(-250)]
    public sealed class LocalMovementPredictionDriver : MonoBehaviour
    {
        private const string SpecialMovementFixedYCollisionPolicy = "STOP_AT_BLOCK_FIXED_Y";
        private const string SpecialMovementKeepHeightCollisionPolicyLegacy = "STOP_AT_BLOCK_KEEP_HEIGHT";
        private const int RenderHistoryCapacity = 6;
        private const int PredictedStateHistoryCapacity = MovementNetcodeConfig.MaxPendingCommands * 2;
        private const float LocalRenderHardSnapDistance = 2.0f;
        private const float LargeCorrectionWarningDistance = 0.25f;

        private ClientSimulationState? _simState;
        private LocalPlayerMotor? _motor;
        private LocalMovementWorldContext? _worldContext;
        private LocalPlayerStateProvider? _stateProvider;
        private LocalMovementPredictor? _predictor;
        private MovementCommandBuffer? _commandHistory;
        private IMovementEnvironment? _environment;
        private bool _warnedUnsupportedWorld;
        private PredictedMovementState _currentPredictedState;
        private bool _hasCurrentPredictedState;
        private uint _lastProcessedSnapshotVersion;
        private uint _lastProcessedMovementContextVersion;
        private uint _lastObservedCommandHistoryGeneration;
        private int _lastReplayDepth;
        private int _maxReplayDepthObserved;
        private float _lastCorrectionPositionError;
        private float _maxCorrectionPositionErrorObserved;
        private uint _lastLargeCorrectionWarningTick;
        private float _localTickAccumulator;
        private readonly PredictedMovementState[] _renderHistory = new PredictedMovementState[RenderHistoryCapacity];
        private int _renderHistoryStart;
        private int _renderHistoryCount;
        private readonly PredictedMovementState[] _predictedStateHistory = new PredictedMovementState[PredictedStateHistoryCapacity];
        private int _predictedStateHistoryStart;
        private int _predictedStateHistoryCount;
        private uint _effectiveMovementContextTick;
        private bool _effectiveMovementBlocked;
        private float _effectiveMoveSpeedMultiplier = 1.0f;
        private int _lastReplayFallbackContextUses;
        private int _totalReplayFallbackContextUses;
        private bool _wasDrivingSpecialMovement;
        private SpecialMovementTrack _lastSpecialMovementTrack;
        private bool _hasLastSpecialMovementTrack;
        private bool _hasPendingSpecialMovementSettleCheck;
        private SpecialMovementTrack _pendingSpecialMovementTrack;
        private Vector3 _pendingSpecialMovementTransformPosition;
        private float _pendingSpecialMovementSettleStartedAt;

        private const float SpecialMovementSettleWarningDelaySeconds = 0.2f;
        private const float SpecialMovementSettleWarningDistance = 0.25f;

        public int LastReplayDepth => _lastReplayDepth;
        public int MaxReplayDepthObserved => _maxReplayDepthObserved;
        public float LastCorrectionPositionError => _lastCorrectionPositionError;
        public float MaxCorrectionPositionErrorObserved => _maxCorrectionPositionErrorObserved;
        public int LastReplayFallbackContextUses => _lastReplayFallbackContextUses;
        public int TotalReplayFallbackContextUses => _totalReplayFallbackContextUses;
        public float FixedTickAlpha => Mathf.Clamp01(_localTickAccumulator / MovementNetcodeConfig.FixedTickSeconds);
        public uint CurrentPredictedTick => _hasCurrentPredictedState ? _currentPredictedState.LastProcessedTick : 0u;
        public uint NextMovementContextProposalTick
        {
            get
            {
                if (_hasCurrentPredictedState)
                    return _currentPredictedState.LastProcessedTick == uint.MaxValue ? uint.MaxValue : _currentPredictedState.LastProcessedTick + 1u;
                if (_simState != null && _simState.HasState)
                    return _simState.LastProcessedTick == uint.MaxValue ? uint.MaxValue : _simState.LastProcessedTick + 1u;
                if (_commandHistory != null)
                    return _commandHistory.NextInputTick;
                return 1u;
            }
        }
        public uint EffectiveMovementContextTick => _effectiveMovementContextTick;
        public bool EffectiveMovementBlocked => _effectiveMovementBlocked;
        public float EffectiveMoveSpeedMultiplier => _effectiveMoveSpeedMultiplier;
        public int CurrentTickLead
        {
            get
            {
                if (!_hasCurrentPredictedState || _simState == null || !_simState.HasState)
                    return 0;

                uint authoritativeTick = _simState.LastProcessedTick;
                return _currentPredictedState.LastProcessedTick >= authoritativeTick
                    ? (int)(_currentPredictedState.LastProcessedTick - authoritativeTick)
                    : 0;
            }
        }
        public Vector3 CurrentPredictedPosition => _hasCurrentPredictedState ? _currentPredictedState.Position : transform.position;

        public void Initialize(
            ClientSimulationState simState,
            LocalPlayerMotor motor,
            LocalMovementWorldContext worldContext,
            LocalPlayerStateProvider stateProvider,
            MovementCommandBuffer commandHistory)
        {
            MovementSharedDataLoader.ValidateBundledDataAvailable();
            _simState = simState;
            _motor = motor;
            _worldContext = worldContext;
            _stateProvider = stateProvider;
            _commandHistory = commandHistory;
            _lastObservedCommandHistoryGeneration = commandHistory.Generation;

            PrimeInitialPredictionLead();
        }

        private void LateUpdate()
        {
            if (_simState == null || _motor == null || _worldContext == null || _stateProvider == null || _commandHistory == null)
                return;

            CheckPendingSpecialMovementSettleWarning();

            if (_simState.TryGetSpecialMovementTrack(out SpecialMovementTrack specialMovementTrack))
            {
                if (!_wasDrivingSpecialMovement)
                    EnterSpecialMovement();
                _warnedUnsupportedWorld = false;
                DriveLocalSpecialMovement(specialMovementTrack);
                _wasDrivingSpecialMovement = true;
                return;
            }

            if (_wasDrivingSpecialMovement)
            {
                ResetAfterSpecialMovement();
                _wasDrivingSpecialMovement = false;
            }

            if (!_worldContext.TryGetPredictionEnvironment(out IMovementEnvironment? environment) || environment == null)
            {
                if (!_warnedUnsupportedWorld)
                {
                    Debug.LogWarning(
                        $"[LocalMovementPredictionDriver] Prediction disabled for world kind '{_worldContext.WorldKind}' until a matching client environment exists.");
                    _warnedUnsupportedWorld = true;
                }
                ClearLocalPrediction();
                return;
            }

            _warnedUnsupportedWorld = false;

            if (!ReferenceEquals(_environment, environment) || _predictor == null)
            {
                _environment = environment;
                _predictor = new LocalMovementPredictor(environment);
                _hasCurrentPredictedState = false;
                _lastProcessedSnapshotVersion = 0;
                _lastProcessedMovementContextVersion = 0;
            }

            if (_lastObservedCommandHistoryGeneration != _commandHistory.Generation)
            {
                _lastObservedCommandHistoryGeneration = _commandHistory.Generation;
                _hasCurrentPredictedState = false;
                _lastProcessedSnapshotVersion = 0;
                _lastProcessedMovementContextVersion = 0;
                ClearRenderHistory();
                ClearPredictedStateHistory();
            }

            if (_simState.HasState)
            {
                uint snapshotVersion = _simState.AuthoritativeSnapshotVersion;
                uint movementContextVersion = _simState.MovementContextVersion;
                if (!_hasCurrentPredictedState ||
                    _lastProcessedSnapshotVersion != snapshotVersion ||
                    _lastProcessedMovementContextVersion != movementContextVersion)
                {
                    ReconcileFromAuthoritative(snapshotVersion);
                }
            }

            AdvanceLocalPrediction();

            if (_renderHistoryCount == 0 && _hasCurrentPredictedState)
                PushRenderSample(_currentPredictedState);

            ApplyRenderedTransform(FixedTickAlpha);
        }

        private void PrimeInitialPredictionLead()
        {
            if (_commandHistory == null || _motor == null || _commandHistory.Count > 0)
                return;

            for (int i = 0; i < ResolveDesiredServerInputLeadTicks(); i++)
                _commandHistory.AppendNext(_motor.SampleIntentForPredictionTick());
        }

        private void AdvanceLocalPrediction()
        {
            if (_motor == null || _commandHistory == null || _environment == null)
                return;

            _localTickAccumulator = Mathf.Min(
                _localTickAccumulator + Time.deltaTime,
                MovementNetcodeConfig.FixedTickSeconds * MovementNetcodeConfig.MaxPredictionLeadTicks);

            int simulatedTicksThisFrame = 0;
            while (_localTickAccumulator >= MovementNetcodeConfig.FixedTickSeconds &&
                   simulatedTicksThisFrame < MovementNetcodeConfig.MaxLocalPredictionTicksPerFrame)
            {
                uint authoritativeTick = _simState != null && _simState.HasState
                    ? _simState.LastProcessedTick
                    : 0u;
                uint maxPredictedTick = authoritativeTick + (uint)MovementNetcodeConfig.MaxPredictionLeadTicks;
                uint nextInputTick = _commandHistory.NextInputTick;
                if (nextInputTick > maxPredictedTick)
                {
                    _localTickAccumulator = Mathf.Min(_localTickAccumulator, MovementNetcodeConfig.FixedTickSeconds);
                    break;
                }

                _localTickAccumulator -= MovementNetcodeConfig.FixedTickSeconds;

                MovementCommand command = _commandHistory.AppendNext(_motor.SampleIntentForPredictionTick());
                if (!_hasCurrentPredictedState)
                {
                    simulatedTicksThisFrame++;
                    continue;
                }

                MovementStepContext context = ResolveMovementStepContext(command.InputTick);

                _currentPredictedState = MovementPrediction.Step(
                    _currentPredictedState,
                    command,
                    context,
                    _environment,
                    MovementNetcodeConfig.FixedTickSeconds);
                _stateProvider?.SetPredictedState(_currentPredictedState);
                RecordPredictedState(_currentPredictedState);
                PushRenderSample(_currentPredictedState);
                simulatedTicksThisFrame++;
            }
        }

        private void DriveLocalSpecialMovement(in SpecialMovementTrack track)
        {
            if (_simState == null || _motor == null || _stateProvider == null)
                return;

            _lastSpecialMovementTrack = track;
            _hasLastSpecialMovementTrack = true;

            AdvanceCommandHistoryOnly();

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Func<float, float, float, float>? sampleGroundHeight =
                _environment != null ? _environment.SampleGroundHeight : null;
            SampledSpecialMovementPose sampled = SpecialMovementRuntimeSampler.Sample(
                track,
                nowMs,
                sampleGroundHeight);
            Vector3 sampledPosition = sampled.Position;
            uint lastProcessedTick = _simState.HasState
                ? _simState.LastProcessedTick
                : (_hasCurrentPredictedState ? _currentPredictedState.LastProcessedTick : 0u);
            bool grounded = _simState.HasState ? _simState.IsGrounded : true;
            if (UsesFixedYCollisionPolicy(track.CollisionPolicy))
                grounded = false;

            _currentPredictedState = new PredictedMovementState(
                sampledPosition,
                Vector3.zero,
                sampled.FacingYawRadians,
                grounded,
                lastProcessedTick);
            _hasCurrentPredictedState = true;
            _stateProvider.SetPredictedState(_currentPredictedState);

            if (_simState.HasState)
            {
                SetEffectiveMovementContext(
                    _simState.GetMovementContextForTick(_simState.LastProcessedTick, out _));
            }
            else
            {
                SetEffectiveMovementContext(new ClientSimulationState.MovementContextSample(
                    0u,
                    false,
                    1.0f,
                    MovementPrediction.DefaultHitRadius,
                    MovementPrediction.DefaultHitHeight));
            }

            ClearRenderHistory();
            ApplyTransform(sampledPosition, sampled.FacingYawRadians);
        }

        private static int ResolveDesiredServerInputLeadTicks()
        {
            return NetworkManager.Instance?.ActiveEndpoint.Kind == NetworkEnvironmentKind.Remote
                ? MovementNetcodeConfig.RemoteDesiredServerInputLeadTicks
                : MovementNetcodeConfig.DesiredServerInputLeadTicks;
        }

        private static bool UsesFixedYCollisionPolicy(string collisionPolicy)
        {
            return string.Equals(
                       collisionPolicy,
                       SpecialMovementFixedYCollisionPolicy,
                       StringComparison.OrdinalIgnoreCase)
                   || string.Equals(
                       collisionPolicy,
                       SpecialMovementKeepHeightCollisionPolicyLegacy,
                       StringComparison.OrdinalIgnoreCase);
        }

        private void AdvanceCommandHistoryOnly()
        {
            if (_motor == null || _commandHistory == null)
                return;

            _localTickAccumulator = Mathf.Min(
                _localTickAccumulator + Time.deltaTime,
                MovementNetcodeConfig.FixedTickSeconds * MovementNetcodeConfig.MaxPredictionLeadTicks);

            int authoredTicksThisFrame = 0;
            while (_localTickAccumulator >= MovementNetcodeConfig.FixedTickSeconds &&
                   authoredTicksThisFrame < MovementNetcodeConfig.MaxLocalPredictionTicksPerFrame)
            {
                uint authoritativeTick = _simState != null && _simState.HasState
                    ? _simState.LastProcessedTick
                    : 0u;
                uint maxPredictedTick = authoritativeTick + (uint)MovementNetcodeConfig.MaxPredictionLeadTicks;
                uint nextInputTick = _commandHistory.NextInputTick;
                if (nextInputTick > maxPredictedTick)
                {
                    _localTickAccumulator = Mathf.Min(_localTickAccumulator, MovementNetcodeConfig.FixedTickSeconds);
                    break;
                }

                _localTickAccumulator -= MovementNetcodeConfig.FixedTickSeconds;
                _commandHistory.AppendNext(_motor.SampleIntentForPredictionTick());
                authoredTicksThisFrame++;
            }
        }

        private void ReconcileFromAuthoritative(uint snapshotVersion)
        {
            if (_simState == null || _commandHistory == null || _predictor == null || _stateProvider == null)
                return;

            _lastReplayDepth = _commandHistory.Count;
            if (_lastReplayDepth > _maxReplayDepthObserved)
                _maxReplayDepthObserved = _lastReplayDepth;

            if (TryGetPredictedStateForTick(_simState.LastProcessedTick, out PredictedMovementState predictedAtAuthoritativeTick))
            {
                Vector3 authoritativePosition = _simState.GetServerPosition();
                _lastCorrectionPositionError = Vector3.Distance(
                    authoritativePosition,
                    predictedAtAuthoritativeTick.Position);
                if (_lastCorrectionPositionError > _maxCorrectionPositionErrorObserved)
                    _maxCorrectionPositionErrorObserved = _lastCorrectionPositionError;

                if (_lastCorrectionPositionError >= LargeCorrectionWarningDistance &&
                    _lastLargeCorrectionWarningTick != _simState.LastProcessedTick)
                {
                    bool hasSpecialMovement = _simState.TryGetSpecialMovementTrack(out SpecialMovementTrack track);
                    string specialMovementSummary = hasSpecialMovement
                        ? $"{track.Kind}/{track.CollisionPolicy}"
                        : "none";
                    Debug.LogWarning(
                        "[LocalMovementPredictionDriver] Large correction " +
                        $"err={_lastCorrectionPositionError:F3}m tick={_simState.LastProcessedTick} " +
                        $"server=({authoritativePosition.x:F2},{authoritativePosition.y:F2},{authoritativePosition.z:F2}) " +
                        $"predicted=({predictedAtAuthoritativeTick.Position.x:F2},{predictedAtAuthoritativeTick.Position.y:F2},{predictedAtAuthoritativeTick.Position.z:F2}) " +
                        $"grounded={_simState.IsGrounded} moveCtxTick={_simState.MovementContextTick} " +
                        $"specialMovement={specialMovementSummary}");
                    _lastLargeCorrectionWarningTick = _simState.LastProcessedTick;
                }
            }
            else
            {
                _lastCorrectionPositionError = 0.0f;
            }

            PredictedMovementState reconciled = _predictor.Rebuild(
                _simState.GetServerPosition(),
                _simState.GetServerVelocity(),
                _simState.GetServerYawRadians(),
                _simState.IsGrounded,
                _simState.LastProcessedTick,
                _commandHistory,
                _simState,
                out ClientSimulationState.MovementContextSample effectiveContext,
                out int fallbackContextUses);

            _lastProcessedSnapshotVersion = snapshotVersion;
            _lastProcessedMovementContextVersion = _simState.MovementContextVersion;
            _lastReplayFallbackContextUses = fallbackContextUses;
            _totalReplayFallbackContextUses += fallbackContextUses;
            if (fallbackContextUses > 0)
            {
                Debug.LogWarning(
                    $"[LocalMovementPredictionDriver] Replay used default movement context {fallbackContextUses} time(s) while rebuilding from authoritative tick {_simState.LastProcessedTick}.");
            }
            _currentPredictedState = reconciled;
            _stateProvider.SetPredictedState(_currentPredictedState);
            _hasCurrentPredictedState = true;
            SetEffectiveMovementContext(effectiveContext);
            RecordPredictedState(_currentPredictedState);
            PushRenderSample(_currentPredictedState);
        }

        private void PushRenderSample(in PredictedMovementState predicted)
        {
            if (_renderHistoryCount > 0)
            {
                PredictedMovementState previous = GetRenderHistorySample(_renderHistoryCount - 1);
                float positionDelta = Vector3.Distance(previous.Position, predicted.Position);
                if (positionDelta >= LocalRenderHardSnapDistance)
                    ClearRenderHistory();
            }

            AppendRenderHistory(predicted);
        }

        private void ApplyRenderedTransform(float alpha)
        {
            if (_renderHistoryCount <= 0)
                return;

            PredictedMovementState newest = GetRenderHistorySample(_renderHistoryCount - 1);
            if (_renderHistoryCount == 1)
            {
                ApplyTransform(newest.Position, newest.FacingYaw);
                return;
            }

            PredictedMovementState previous = GetRenderHistorySample(_renderHistoryCount - 2);
            float t = Mathf.Clamp01(alpha);
            Vector3 position = Vector3.Lerp(previous.Position, newest.Position, t);
            float yaw = LerpAngle(previous.FacingYaw, newest.FacingYaw, t);

            if (_hasCurrentPredictedState && !_currentPredictedState.Grounded && _motor != null)
                yaw = _motor.GetMovementYaw();

            ApplyTransform(position, yaw);
        }

        private void ApplyTransform(Vector3 position, float facingYaw)
        {
            transform.position = position;
            transform.rotation = Quaternion.Euler(0.0f, facingYaw * Mathf.Rad2Deg, 0.0f);
        }

        private void ClearLocalPrediction()
        {
            _stateProvider?.ClearPredictedState();
            _hasCurrentPredictedState = false;
            _lastProcessedSnapshotVersion = 0;
            _lastProcessedMovementContextVersion = 0;
            _lastReplayDepth = 0;
            _lastCorrectionPositionError = 0.0f;
            _lastLargeCorrectionWarningTick = 0;
            _lastReplayFallbackContextUses = 0;
            _effectiveMovementContextTick = 0;
            _effectiveMovementBlocked = false;
            _effectiveMoveSpeedMultiplier = 1.0f;
            _localTickAccumulator = 0.0f;
            ClearPendingSpecialMovementSettleWarning();
            ClearRenderHistory();
            ClearPredictedStateHistory();
        }

        private void EnterSpecialMovement()
        {
            ClearPendingSpecialMovementSettleWarning();
            ResetSpecialMovementInputBoundary();
            _hasCurrentPredictedState = false;
            _lastProcessedSnapshotVersion = 0;
            _lastProcessedMovementContextVersion = 0;
            ClearRenderHistory();
            ClearPredictedStateHistory();
        }

        private void ResetAfterSpecialMovement()
        {
            if (_hasLastSpecialMovementTrack && _simState != null)
            {
                _pendingSpecialMovementTrack = _lastSpecialMovementTrack;
                _pendingSpecialMovementTransformPosition = transform.position;
                _pendingSpecialMovementSettleStartedAt = Time.realtimeSinceStartup;
                _hasPendingSpecialMovementSettleCheck = true;
            }

            // Commands authored during special movement are not replayable:
            // server runtime owns position, while local input may contain stale
            // jump or strafe edges. Re-anchor local input at both boundaries.
            ResetSpecialMovementInputBoundary();

            _stateProvider?.ClearPredictedState();
            _hasCurrentPredictedState = false;
            _lastProcessedSnapshotVersion = 0;
            _lastProcessedMovementContextVersion = 0;
            _lastReplayDepth = 0;
            _lastCorrectionPositionError = 0.0f;
            _lastReplayFallbackContextUses = 0;
            _effectiveMovementContextTick = 0u;
            _effectiveMovementBlocked = false;
            _effectiveMoveSpeedMultiplier = 1.0f;
            ClearRenderHistory();
            ClearPredictedStateHistory();
            _hasLastSpecialMovementTrack = false;
            _lastSpecialMovementTrack = default;
        }

        private void ResetSpecialMovementInputBoundary()
        {
            if (_commandHistory != null && _simState != null && _simState.HasState)
            {
                _commandHistory.Reset(_simState.LastProcessedTick + 1);
                _lastObservedCommandHistoryGeneration = _commandHistory.Generation;
            }

            _motor?.ClearBufferedJumpInput();
        }

        private void CheckPendingSpecialMovementSettleWarning()
        {
            if (!_hasPendingSpecialMovementSettleCheck || _simState == null || !_simState.HasState)
                return;

            if (Time.realtimeSinceStartup - _pendingSpecialMovementSettleStartedAt < SpecialMovementSettleWarningDelaySeconds)
                return;

            Vector3 serverPos = _simState.GetServerPosition();
            Vector3 localPos = transform.position;
            float localServerDelta = Vector3.Distance(localPos, serverPos);
            float endpointDelta = Vector3.Distance(serverPos, _pendingSpecialMovementTrack.End);
            if (localServerDelta >= SpecialMovementSettleWarningDistance)
            {
                Debug.LogWarning(
                    $"[LOCAL_SPECIAL_MOVEMENT_SETTLE] kind={_pendingSpecialMovementTrack.Kind} " +
                    $"settle_transform=({_pendingSpecialMovementTransformPosition.x:F4},{_pendingSpecialMovementTransformPosition.y:F4},{_pendingSpecialMovementTransformPosition.z:F4}) " +
                    $"local=({localPos.x:F4},{localPos.y:F4},{localPos.z:F4}) " +
                    $"server=({serverPos.x:F4},{serverPos.y:F4},{serverPos.z:F4}) " +
                    $"track_start=({_pendingSpecialMovementTrack.Start.x:F4},{_pendingSpecialMovementTrack.Start.y:F4},{_pendingSpecialMovementTrack.Start.z:F4}) " +
                    $"track_end=({_pendingSpecialMovementTrack.End.x:F4},{_pendingSpecialMovementTrack.End.y:F4},{_pendingSpecialMovementTrack.End.z:F4}) " +
                    $"local_server_delta={localServerDelta:F3} " +
                    $"endpoint_delta={endpointDelta:F3}");
            }

            ClearPendingSpecialMovementSettleWarning();
        }

        private void ClearPendingSpecialMovementSettleWarning()
        {
            _hasPendingSpecialMovementSettleCheck = false;
            _pendingSpecialMovementTrack = default;
            _pendingSpecialMovementTransformPosition = Vector3.zero;
            _pendingSpecialMovementSettleStartedAt = 0.0f;
        }

        private void ClearRenderHistory()
        {
            _renderHistoryStart = 0;
            _renderHistoryCount = 0;
        }

        private void RecordPredictedState(in PredictedMovementState state)
        {
            if (_predictedStateHistoryCount > 0)
            {
                PredictedMovementState newest = GetPredictedStateHistorySample(_predictedStateHistoryCount - 1);
                if (newest.LastProcessedTick == state.LastProcessedTick)
                {
                    SetPredictedStateHistorySample(_predictedStateHistoryCount - 1, state);
                    return;
                }
            }

            AppendPredictedStateHistory(state);
        }

        private bool TryGetPredictedStateForTick(uint tick, out PredictedMovementState state)
        {
            for (int offset = 0; offset < _predictedStateHistoryCount; offset++)
            {
                PredictedMovementState sample = GetPredictedStateHistorySample(_predictedStateHistoryCount - 1 - offset);
                if (sample.LastProcessedTick != tick)
                    continue;

                state = sample;
                return true;
            }

            state = default;
            return false;
        }

        private void ClearPredictedStateHistory()
        {
            _predictedStateHistoryStart = 0;
            _predictedStateHistoryCount = 0;
        }

        private MovementStepContext ResolveMovementStepContext(uint inputTick)
        {
            if (_simState == null)
            {
                SetEffectiveMovementContext(new ClientSimulationState.MovementContextSample(
                    0u,
                    false,
                    1.0f,
                    MovementPrediction.DefaultHitRadius,
                    MovementPrediction.DefaultHitHeight));
                return new MovementStepContext(
                    false,
                    1.0f,
                    MovementPrediction.DefaultHitRadius,
                    MovementPrediction.DefaultHitHeight);
            }

            ClientSimulationState.MovementContextSample sample =
                _simState.GetMovementContextForTick(inputTick, out bool usedFallback);

            if (usedFallback)
            {
                sample = new ClientSimulationState.MovementContextSample(
                    0u,
                    false,
                    1.0f,
                    MovementPrediction.DefaultHitRadius,
                    MovementPrediction.DefaultHitHeight);
            }

            SetEffectiveMovementContext(sample);
            return new MovementStepContext(
                sample.MovementBlocked,
                sample.MoveSpeedMultiplier,
                sample.HitRadius,
                sample.HitHeight);
        }

        private void SetEffectiveMovementContext(ClientSimulationState.MovementContextSample sample)
        {
            _effectiveMovementContextTick = sample.Tick;
            _effectiveMovementBlocked = sample.MovementBlocked;
            _effectiveMoveSpeedMultiplier = sample.MoveSpeedMultiplier;
        }

        private void AppendRenderHistory(in PredictedMovementState state)
        {
            if (_renderHistoryCount < RenderHistoryCapacity)
            {
                int index = (_renderHistoryStart + _renderHistoryCount) % RenderHistoryCapacity;
                _renderHistory[index] = state;
                _renderHistoryCount++;
                return;
            }

            _renderHistory[_renderHistoryStart] = state;
            _renderHistoryStart = (_renderHistoryStart + 1) % RenderHistoryCapacity;
        }

        private PredictedMovementState GetRenderHistorySample(int index)
        {
            int arrayIndex = (_renderHistoryStart + index) % RenderHistoryCapacity;
            return _renderHistory[arrayIndex];
        }

        private void AppendPredictedStateHistory(in PredictedMovementState state)
        {
            if (_predictedStateHistoryCount < PredictedStateHistoryCapacity)
            {
                int index = (_predictedStateHistoryStart + _predictedStateHistoryCount) % PredictedStateHistoryCapacity;
                _predictedStateHistory[index] = state;
                _predictedStateHistoryCount++;
                return;
            }

            _predictedStateHistory[_predictedStateHistoryStart] = state;
            _predictedStateHistoryStart = (_predictedStateHistoryStart + 1) % PredictedStateHistoryCapacity;
        }

        private PredictedMovementState GetPredictedStateHistorySample(int index)
        {
            int arrayIndex = (_predictedStateHistoryStart + index) % PredictedStateHistoryCapacity;
            return _predictedStateHistory[arrayIndex];
        }

        private void SetPredictedStateHistorySample(int index, in PredictedMovementState state)
        {
            int arrayIndex = (_predictedStateHistoryStart + index) % PredictedStateHistoryCapacity;
            _predictedStateHistory[arrayIndex] = state;
        }

        private static float LerpAngle(float a, float b, float t)
        {
            return a + DeltaAngleRadians(a, b) * t;
        }

        private static float DeltaAngleRadians(float a, float b)
        {
            return Mathf.Repeat(b - a + Mathf.PI, Mathf.PI * 2.0f) - Mathf.PI;
        }
    }
}

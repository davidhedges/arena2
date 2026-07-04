#nullable enable
using System;
using UnityEngine;
using Arena.Simulation;
using Arena.Presentation;

namespace Arena.Input
{
    /// <summary>
    /// Owns the local fixed-tick prediction clock, rebuilds from authoritative
    /// snapshots, and applies predicted local state to the player transform.
    /// S5: also the input-lead actuator — the authoring clock injects extra
    /// ticks (raise lead / re-anchor drift) and skips slots (drain surplus)
    /// to keep command numbering at the InputLeadController's target ahead of
    /// the estimated server tick.
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
        private InputLeadController? _leadController;
        private LocalPresentationDriver? _presentationDriver;
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

        // --- S5 lead actuation + jump-delivery ledger state ---
        // Depth-delta telemetry: per frame, authored-vs-estimate-advance;
        // advances beyond this are re-anchor events, not pacing, and are
        // excluded from the counters.
        private const int TelemetryMaxPlausibleEstimateAdvance = 30;
        private int _injectedCommands;
        private int _skippedAuthoringSlots;
        private float _authoringTargetAlpha;
        private bool _hasAuthoringTargetAlpha;
        private uint _lastEstimateWholeTick;
        private bool _hasLastEstimateWholeTick;
        private const int PendingPredictedJumpCapacity = 8;
        private readonly uint[] _pendingPredictedJumpTicks = new uint[PendingPredictedJumpCapacity];
        private int _pendingPredictedJumpCount;
        private long _authoritativeJumpCursor;
        // A jump predicted at tick P lands authoritatively at P, or P+1 when
        // the server slid a late jump one tick; past P + timeout it was eaten.
        private const uint JumpConfirmWindowTicks = 3;
        private const uint JumpLossTimeoutTicks = 6;

        public int LastReplayDepth => _lastReplayDepth;
        public int MaxReplayDepthObserved => _maxReplayDepthObserved;
        public float LastCorrectionPositionError => _lastCorrectionPositionError;
        public float MaxCorrectionPositionErrorObserved => _maxCorrectionPositionErrorObserved;
        public int InjectedCommands => _injectedCommands;
        public int SkippedAuthoringSlots => _skippedAuthoringSlots;
        public int JumpsPredicted { get; private set; }
        public int JumpsConfirmed { get; private set; }
        public int JumpsLost { get; private set; }
        public int LastReplayFallbackContextUses => _lastReplayFallbackContextUses;
        public int TotalReplayFallbackContextUses => _totalReplayFallbackContextUses;
        // Render-interpolation fraction toward the next authored tick. On the
        // target-chasing clock this is the fractional distance of
        // estimate+lead past the last authored tick; the wall-clock
        // accumulator fraction survives only for the pre-state fallback.
        public float FixedTickAlpha => _hasAuthoringTargetAlpha
            ? _authoringTargetAlpha
            : Mathf.Clamp01(_localTickAccumulator / MovementNetcodeConfig.FixedTickSeconds);
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
            MovementCommandBuffer commandHistory,
            InputLeadController leadController,
            LocalPresentationDriver? presentationDriver)
        {
            MovementSharedDataLoader.ValidateBundledDataAvailable();
            _simState = simState;
            _motor = motor;
            _worldContext = worldContext;
            _stateProvider = stateProvider;
            _commandHistory = commandHistory;
            _leadController = leadController;
            _presentationDriver = presentationDriver;
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
                ClearPendingPredictedJumps();
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

            UpdateJumpLedger();
            AdvanceLocalPrediction();

            if (_renderHistoryCount == 0 && _hasCurrentPredictedState)
                PushRenderSample(_currentPredictedState);

            ApplyRenderedTransform(FixedTickAlpha);
        }

        private void PrimeInitialPredictionLead()
        {
            if (_commandHistory == null || _motor == null || _commandHistory.Count > 0)
                return;

            int initialLead = _leadController?.LeadTicks ?? MovementNetcodeConfig.InitialInputLeadTicks;
            for (int i = 0; i < initialLead; i++)
                _commandHistory.AppendNext(_motor.SampleIntentForPredictionTick());
        }

        private void AdvanceLocalPrediction()
        {
            if (_motor == null || _commandHistory == null || _environment == null)
                return;

            if (TryGetAuthoringTarget(out float estimate, out float target))
            {
                AdvanceAuthoringTowardTarget(estimate, target, authorOnly: false);
                return;
            }

            _hasAuthoringTargetAlpha = false;
            AdvanceAuthoringByWallClock(authorOnly: false);
        }

        private bool TryGetAuthoringTarget(out float estimate, out float target)
        {
            estimate = 0.0f;
            target = 0.0f;
            if (_simState == null || !_simState.HasState || _leadController == null ||
                _commandHistory == null)
            {
                return false;
            }

            estimate = _simState.EstimateAuthoritativeTick(
                Time.realtimeSinceStartup,
                MovementNetcodeConfig.FixedTickSeconds);
            target = estimate + _leadController.LeadTicks;
            return true;
        }

        /// <summary>
        /// Target-chasing authoring (S5 cadence fix): author input tick N when
        /// estimate + lead crosses N. The estimate re-anchors on every ack, so
        /// this paces command production at the server's REAL consume cadence
        /// — measured live at ~36.6 ms/tick against the authored 33 ms, a
        /// permanent ~3-tick/s surplus no rate-capped skip could drain. Rate
        /// scaling, lead raises (burst a tick), and lead lowers (pause until
        /// the target catches up) all fall out of the same comparison; the
        /// old wall-clock accumulator survives only as the pre-state fallback.
        /// </summary>
        private void AdvanceAuthoringTowardTarget(float estimate, float target, bool authorOnly)
        {
            if (_motor == null || _commandHistory == null)
                return;

            int authored = 0;
            while (authored < MovementNetcodeConfig.MaxLocalPredictionTicksPerFrame &&
                   !NextInputTickExceedsPredictionBound() &&
                   _commandHistory.NextInputTick <= target)
            {
                if (authorOnly)
                    _commandHistory.AppendNext(_motor.SampleIntentForPredictionTick());
                else
                    AuthorAndStepOneTick();
                authored++;
            }

            _authoringTargetAlpha = Mathf.Clamp01(target - ((float)_commandHistory.NextInputTick - 1.0f));
            _hasAuthoringTargetAlpha = true;

            // Depth-delta telemetry: ticks authored beyond the estimate's
            // advance raise server-side depth (injected); estimate advance we
            // deliberately sat out while ahead of target drains it (skipped).
            uint estimateWholeTick = (uint)Mathf.Max(0.0f, estimate);
            if (_hasLastEstimateWholeTick && estimateWholeTick >= _lastEstimateWholeTick)
            {
                int estimateAdvance = (int)(estimateWholeTick - _lastEstimateWholeTick);
                if (estimateAdvance <= TelemetryMaxPlausibleEstimateAdvance)
                {
                    if (authored > estimateAdvance)
                        _injectedCommands += authored - estimateAdvance;
                    else if (estimateAdvance > authored && _commandHistory.NextInputTick > target)
                        _skippedAuthoringSlots += estimateAdvance - authored;
                }
            }
            _lastEstimateWholeTick = estimateWholeTick;
            _hasLastEstimateWholeTick = true;
        }

        /// <summary>Pre-state fallback: 33 ms wall-clock pacing until the
        /// first authoritative snapshot gives the target clock an anchor.</summary>
        private void AdvanceAuthoringByWallClock(bool authorOnly)
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
                if (NextInputTickExceedsPredictionBound())
                {
                    _localTickAccumulator = Mathf.Min(_localTickAccumulator, MovementNetcodeConfig.FixedTickSeconds);
                    break;
                }

                _localTickAccumulator -= MovementNetcodeConfig.FixedTickSeconds;

                if (authorOnly)
                    _commandHistory.AppendNext(_motor.SampleIntentForPredictionTick());
                else
                    AuthorAndStepOneTick();
                authoredTicksThisFrame++;
            }
        }

        /// <summary>
        /// Authors one input tick and advances prediction through it — the
        /// shared body of the paced authoring clock and lead injection.
        /// </summary>
        private void AuthorAndStepOneTick()
        {
            if (_motor == null || _commandHistory == null || _environment == null)
                return;

            bool wasGrounded = _hasCurrentPredictedState && _currentPredictedState.Grounded;
            MovementCommand command = _commandHistory.AppendNext(_motor.SampleIntentForPredictionTick());
            if (!_hasCurrentPredictedState)
                return;

            MovementStepContext context = ResolveMovementStepContext(command.InputTick);

            if (wasGrounded && command.JumpPressed && !context.MovementBlocked)
                RecordPredictedJump(command.InputTick);

            _currentPredictedState = MovementPrediction.Step(
                _currentPredictedState,
                command,
                context,
                _environment,
                MovementNetcodeConfig.FixedTickSeconds);
            _stateProvider?.SetPredictedState(_currentPredictedState);
            RecordPredictedState(_currentPredictedState);
            PushRenderSample(_currentPredictedState);
        }

        private bool NextInputTickExceedsPredictionBound()
        {
            if (_commandHistory == null)
                return true;

            uint authoritativeTick = _simState != null && _simState.HasState
                ? _simState.LastProcessedTick
                : 0u;
            uint maxPredictedTick = authoritativeTick + (uint)MovementNetcodeConfig.MaxPredictionLeadTicks;
            return _commandHistory.NextInputTick > maxPredictedTick;
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

        // --- S5 jump-delivery ledger (evidence): a predicted jump must land
        // authoritatively within its confirm window or it was eaten. ---

        private void RecordPredictedJump(uint tick)
        {
            JumpsPredicted++;
            if (_pendingPredictedJumpCount == PendingPredictedJumpCapacity)
                RemovePendingPredictedJumpAt(0);
            _pendingPredictedJumpTicks[_pendingPredictedJumpCount++] = tick;
        }

        private void UpdateJumpLedger()
        {
            if (_simState == null || !_simState.HasState)
                return;

            long total = _simState.AuthoritativeJumpTotal;
            long start = Math.Max(_authoritativeJumpCursor, _simState.OldestRetainedAuthoritativeJump);
            for (long seq = start; seq < total; seq++)
            {
                uint authoritativeJumpTick = _simState.GetAuthoritativeJumpTick(seq);
                for (int i = 0; i < _pendingPredictedJumpCount; i++)
                {
                    uint predictedTick = _pendingPredictedJumpTicks[i];
                    if (authoritativeJumpTick >= predictedTick &&
                        authoritativeJumpTick - predictedTick <= JumpConfirmWindowTicks)
                    {
                        JumpsConfirmed++;
                        RemovePendingPredictedJumpAt(i);
                        break;
                    }
                }
            }
            _authoritativeJumpCursor = total;

            uint ackTick = _simState.LastProcessedTick;
            while (_pendingPredictedJumpCount > 0 &&
                   ackTick > _pendingPredictedJumpTicks[0] + JumpLossTimeoutTicks)
            {
                JumpsLost++;
                Debug.LogWarning(
                    $"[LocalMovementPredictionDriver] Predicted jump at tick {_pendingPredictedJumpTicks[0]} never landed authoritatively (ack {ackTick}) — eaten jump.");
                RemovePendingPredictedJumpAt(0);
            }
        }

        private void RemovePendingPredictedJumpAt(int index)
        {
            for (int i = index; i < _pendingPredictedJumpCount - 1; i++)
                _pendingPredictedJumpTicks[i] = _pendingPredictedJumpTicks[i + 1];
            _pendingPredictedJumpCount--;
        }

        private void ClearPendingPredictedJumps()
        {
            _pendingPredictedJumpCount = 0;
            _authoritativeJumpCursor = _simState?.AuthoritativeJumpTotal ?? 0L;
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

            if (TryGetAuthoringTarget(out float estimate, out float target))
            {
                AdvanceAuthoringTowardTarget(estimate, target, authorOnly: true);
                return;
            }

            _hasAuthoringTargetAlpha = false;
            AdvanceAuthoringByWallClock(authorOnly: true);
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

            bool hadCurrentPredictedState = _hasCurrentPredictedState;
            Vector3 previousHeadPosition = _currentPredictedState.Position;

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

            // S5 correction presentation: hand the render-stream discontinuity
            // to the presentation budget — sub-threshold errors decay at a
            // capped rate, larger ones snap once honestly.
            if (hadCurrentPredictedState)
                _presentationDriver?.NotifyReconcileDisplacement(
                    reconciled.Position - previousHeadPosition);
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
            _hasAuthoringTargetAlpha = false;
            _hasLastEstimateWholeTick = false;
            ClearPendingSpecialMovementSettleWarning();
            ClearRenderHistory();
            ClearPredictedStateHistory();
            ClearPendingPredictedJumps();
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
            ClearPendingPredictedJumps();
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
            ClearPendingPredictedJumps();
            _hasLastSpecialMovementTrack = false;
            _lastSpecialMovementTrack = default;
        }

        private void ResetSpecialMovementInputBoundary()
        {
            if (_commandHistory != null && _simState != null && _simState.HasState &&
                _leadController != null)
            {
                // S5: re-anchor FORWARD onto the estimated server timeline.
                // The old ackTick+1 anchor re-entered a permanently-late
                // regime at any real RTT (every re-authored command was
                // already consumed-by-fallback on arrival). The anchor also
                // clears the server's post-special-movement input discard
                // window and never reuses a tick number the receive cursor
                // has seen.
                float estimate = _simState.EstimateAuthoritativeTick(
                    Time.realtimeSinceStartup,
                    MovementNetcodeConfig.FixedTickSeconds);
                uint anchor = _leadController.ComputeForwardAnchorTick(
                    estimate,
                    _commandHistory.HighestAuthoredTick);
                uint discardFloor = _simState.LastProcessedTick
                    + (uint)MovementNetcodeConfig.SpecialMovementInputDiscardLeadTicks + 1u;
                if (anchor < discardFloor)
                    anchor = discardFloor;
                _commandHistory.Reset(anchor);
                _leadController.SuppressFeedbackBelowTick(anchor);
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

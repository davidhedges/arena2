#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using Arena.Network;
using Arena.Simulation;

namespace Arena.Input
{
    /// <summary>
    /// Transport-side movement net driver (S5, design review §4).
    /// Local prediction owns command creation and paces the authoring clock;
    /// this class sends unsent command history immediately, prunes
    /// acknowledged input, drains the server's per-tick consume feedback into
    /// the shared <see cref="InputLeadController"/>, and owns the last rung
    /// of the degradation ladder (hard resync on *sustained* overrun only —
    /// input production is already throttled by the prediction-lead bound,
    /// so a transient stall degrades instead of teleporting).
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class MovementNetDriver : MonoBehaviour
    {
        private ClientSimulationState? _simState;
        private MovementCommandBuffer? _commandHistory;
        private InputLeadController? _leadController;
        private float _localTickEstimateStartTime;
        private readonly List<MovementCommand> _unsentCommandsScratch =
            new(MovementNetcodeConfig.MaxPendingCommands);
        private uint _lastAckedTick;
        private bool _hasAckedTick;
        private float _estimatedServerTick;
        private int _commandsSent;
        private int _commandsAcked;
        private int _maxPendingCommandsObserved;
        private int _resyncCount;
        private long _ackFeedbackCursor;
        private float _overrunSinceTime = -1.0f;

        public int PendingCommandCount => _commandHistory?.Count ?? 0;
        public int CommandsSent => _commandsSent;
        public int CommandsAcked => _commandsAcked;
        public int MaxPendingCommandsObserved => _maxPendingCommandsObserved;
        public int ResyncCount => _resyncCount;
        public uint? OldestPendingTick => _commandHistory?.OldestPendingTick;
        public uint? NewestPendingTick => _commandHistory?.NewestPendingTick;
        public float EstimatedServerTick => _estimatedServerTick;
        public InputLeadController? LeadController => _leadController;

        public void Initialize(
            ClientSimulationState simState,
            MovementCommandBuffer commandHistory,
            InputLeadController leadController)
        {
            _simState = simState;
            _commandHistory = commandHistory;
            _leadController = leadController;
            _localTickEstimateStartTime = Time.realtimeSinceStartup;
        }

        private void LateUpdate()
        {
            if (_simState == null || _commandHistory == null || _leadController == null)
                return;

            PruneAckedCommands();
            DrainAckFeedback();
            UpdateServerTickEstimate();
            EnforceSustainedOverrunResync();
            if (_simState.TryGetSpecialMovementTrack(out _))
                return;
            SendUnsentCommands();
        }

        /// <summary>
        /// Feeds every new authoritative ack tick's consume truth to the lead
        /// controller. Feedback is skipped (cursor still advances) while a
        /// special movement runtime owns the character: its consume results
        /// describe input the boundary re-anchor deliberately abandons.
        /// </summary>
        private void DrainAckFeedback()
        {
            if (_simState == null || _commandHistory == null || _leadController == null)
                return;

            long total = _simState.InputAckTotal;
            if (total <= _ackFeedbackCursor)
                return;

            long start = Math.Max(_ackFeedbackCursor, _simState.OldestRetainedInputAck);
            bool suppress = _simState.TryGetSpecialMovementTrack(out _);
            float now = Time.realtimeSinceStartup;
            for (long seq = start; seq < total; seq++)
            {
                if (suppress)
                    continue;

                ClientSimulationState.InputAckSample ack = _simState.GetInputAck(seq);
                bool tickWasSent = ack.Tick <= _commandHistory.HighestAuthoredTick;
                _leadController.ObserveAck(
                    ack.Tick,
                    ack.ConsumedCommand,
                    ack.BufferedCommands,
                    tickWasSent,
                    now);
            }

            _ackFeedbackCursor = total;
        }

        private void UpdateServerTickEstimate()
        {
            if (_simState == null)
            {
                _estimatedServerTick = _lastAckedTick;
                return;
            }

            if (!_simState.HasState)
            {
                float elapsed = Mathf.Max(0.0f, Time.realtimeSinceStartup - _localTickEstimateStartTime);
                _estimatedServerTick = _lastAckedTick + elapsed / MovementNetcodeConfig.FixedTickSeconds;
                return;
            }

            float estimatedTick = _simState.EstimateAuthoritativeTick(
                Time.realtimeSinceStartup,
                MovementNetcodeConfig.FixedTickSeconds);
            _estimatedServerTick = Mathf.Max(_simState.LastProcessedTick, estimatedTick);
        }

        /// <summary>
        /// Degradation ladder, last rung. Overrunning the prediction-lead
        /// bound already throttles input production (authoring stops at
        /// ack + MaxPredictionLeadTicks); only when the overrun *persists* —
        /// acks stopped entirely — does the driver take the one honest hard
        /// resync, re-anchored forward onto the estimated server timeline
        /// instead of behind it.
        /// </summary>
        private void EnforceSustainedOverrunResync()
        {
            if (_simState == null || _commandHistory == null || _leadController == null || !_simState.HasState)
                return;

            if (_commandHistory.Count <= MovementNetcodeConfig.MaxPredictionLeadTicks)
            {
                _overrunSinceTime = -1.0f;
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (_overrunSinceTime < 0.0f)
            {
                _overrunSinceTime = now;
                return;
            }

            if (now - _overrunSinceTime < MovementNetcodeConfig.HardResyncSustainedOverrunSeconds)
                return;

            uint anchor = _leadController.ComputeForwardAnchorTick(
                _estimatedServerTick,
                _commandHistory.HighestAuthoredTick);
            Debug.LogWarning(
                $"[MovementNetDriver] Hard resync: {_commandHistory.Count} pending commands exceeded MaxPredictionLeadTicks ({MovementNetcodeConfig.MaxPredictionLeadTicks}) for {now - _overrunSinceTime:F1}s. Re-anchoring command numbering at tick {anchor}.");
            _commandHistory.Reset(anchor);
            _leadController.SuppressFeedbackBelowTick(anchor);
            _resyncCount++;
            _overrunSinceTime = -1.0f;
        }

        /// <summary>
        /// Sends every authored-but-unsent command immediately (burst-capped).
        /// S5 deleted the estimate+lead send gate: authoring pace is the lead
        /// actuator now, and holding an authored command back only adds
        /// arrival risk.
        /// </summary>
        private void SendUnsentCommands()
        {
            if (_simState == null || _commandHistory == null)
                return;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            _commandHistory.CopyUnsentTo(_unsentCommandsScratch);

            int sentThisFrame = 0;
            for (int i = 0; i < _unsentCommandsScratch.Count; i++)
            {
                if (sentThisFrame >= MovementNetcodeConfig.MaxTicksToSendPerFrame)
                    break;

                MovementCommand command = _unsentCommandsScratch[i];
                conn.Reducers.SendMovementIntent(
                    command.Forward,
                    command.Strafe,
                    command.FacingYaw,
                    command.JumpPressed,
                    command.InputTick);
                sentThisFrame++;
            }

            if (sentThisFrame <= 0)
                return;

            _commandHistory.MarkSent(sentThisFrame);
            _commandsSent += sentThisFrame;
            if (_commandHistory.Count > _maxPendingCommandsObserved)
                _maxPendingCommandsObserved = _commandHistory.Count;
        }

        private void PruneAckedCommands()
        {
            if (_simState == null || _commandHistory == null || !_simState.HasState)
                return;

            uint ackTick = _simState.LastProcessedTick;
            if (_hasAckedTick && ackTick == _lastAckedTick)
                return;

            _hasAckedTick = true;
            _lastAckedTick = ackTick;
            int before = _commandHistory.Count;
            _commandHistory.PruneUpTo(ackTick);
            _commandsAcked += before - _commandHistory.Count;
        }
    }
}

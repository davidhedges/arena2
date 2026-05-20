#nullable enable
using System.Collections.Generic;
using UnityEngine;
using Arena.Network;
using Arena.Simulation;

namespace Arena.Input
{
    /// <summary>
    /// Transport-only movement net driver.
    /// Local prediction owns command creation; this class only sends unsent
    /// command history and prunes acknowledged input.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class MovementNetDriver : MonoBehaviour
    {
        private ClientSimulationState? _simState;
        private MovementCommandBuffer? _commandHistory;
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

        public int PendingCommandCount => _commandHistory?.Count ?? 0;
        public int CommandsSent => _commandsSent;
        public int CommandsAcked => _commandsAcked;
        public int MaxPendingCommandsObserved => _maxPendingCommandsObserved;
        public int ResyncCount => _resyncCount;
        public uint? OldestPendingTick => _commandHistory?.OldestPendingTick;
        public uint? NewestPendingTick => _commandHistory?.NewestPendingTick;
        public float EstimatedServerTick => _estimatedServerTick;

        public void Initialize(ClientSimulationState simState, MovementCommandBuffer commandHistory)
        {
            _simState = simState;
            _commandHistory = commandHistory;
            _localTickEstimateStartTime = Time.realtimeSinceStartup;
        }

        private void LateUpdate()
        {
            if (_simState == null || _commandHistory == null)
                return;

            PruneAckedCommands();
            UpdateServerTickEstimate();
            EnforcePredictionLeadBounds();
            if (_simState.TryGetSpecialMovementTrack(out _))
                return;
            SendCommandsAgainstEstimatedServerTick();
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

        private void EnforcePredictionLeadBounds()
        {
            if (_simState == null || _commandHistory == null || !_simState.HasState)
                return;

            if (_commandHistory.Count <= MovementNetcodeConfig.MaxPredictionLeadTicks)
                return;

            uint ackTick = _simState.LastProcessedTick;
            Debug.LogWarning(
                $"[MovementNetDriver] Emergency re-sync: {_commandHistory.Count} pending commands exceeded MaxPredictionLeadTicks ({MovementNetcodeConfig.MaxPredictionLeadTicks}). Clearing command history and re-anchoring to server tick {ackTick}.");
            _commandHistory.Reset(ackTick + 1);
            _resyncCount++;
        }

        private void SendCommandsAgainstEstimatedServerTick()
        {
            if (_simState == null || _commandHistory == null)
                return;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            uint ackTick = _simState.HasState ? _simState.LastProcessedTick : _lastAckedTick;
            int estimatedWholeTick = Mathf.FloorToInt(_estimatedServerTick);
            uint estimatedServerTick = estimatedWholeTick > 0 ? (uint)estimatedWholeTick : 0u;
            uint targetInputTick = estimatedServerTick + (uint)ResolveDesiredServerInputLeadTicks();
            uint maxSafeInputTick = ackTick + (uint)MovementNetcodeConfig.MaxPredictionLeadTicks;
            if (targetInputTick > maxSafeInputTick)
                targetInputTick = maxSafeInputTick;

            _commandHistory.CopyUnsentTo(_unsentCommandsScratch);

            int sentThisFrame = 0;
            for (int i = 0; i < _unsentCommandsScratch.Count; i++)
            {
                MovementCommand command = _unsentCommandsScratch[i];
                if (command.InputTick > targetInputTick ||
                    sentThisFrame >= MovementNetcodeConfig.MaxTicksToSendPerFrame)
                {
                    break;
                }

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

        private static int ResolveDesiredServerInputLeadTicks()
        {
            return NetworkManager.Instance?.ActiveEndpoint.Kind == NetworkEnvironmentKind.Remote
                ? MovementNetcodeConfig.RemoteDesiredServerInputLeadTicks
                : MovementNetcodeConfig.DesiredServerInputLeadTicks;
        }
    }
}

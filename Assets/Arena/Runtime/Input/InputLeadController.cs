#nullable enable
using UnityEngine;

namespace Arena.Input
{
    /// <summary>
    /// Closed-loop input lead control (design review §4, S5).
    ///
    /// The server publishes per-tick consume truth beside the ack
    /// (PlayerPhysics.last_tick_consumed_command / buffered_command_count).
    /// This controller holds a setpoint on that buffer occupancy (~1–2
    /// commands): starvation raises the lead immediately (rate-limited so a
    /// burst of stale acks after a delivery stall counts once), surplus
    /// lowers it only after it has persisted — asymmetric on purpose, because
    /// a raise costs a few ms of extra server-sim lag while a miss costs a
    /// fallback tick and a reconcile error.
    ///
    /// There is no endpoint-kind switch: loopback and a 250 ms connection run
    /// the identical loop and converge to what their delivery needs.
    ///
    /// The lead is a *number*; actuation lives in
    /// LocalMovementPredictionDriver's target-chasing authoring clock (author
    /// input tick N when estimate + lead crosses N — which also paces
    /// production at the server's real tick cadence) and in MovementNetDriver
    /// (re-anchor targets). Feedback observed while the command numbering is
    /// being re-anchored is suppressed via
    /// <see cref="SuppressFeedbackBelowTick"/> so a deliberate wipe never
    /// reads as starvation.
    /// </summary>
    public sealed class InputLeadController
    {
        private float _leadTicks = MovementNetcodeConfig.InitialInputLeadTicks;
        private float _nextRaiseAllowedTime;
        private float _surplusSinceTime = -1.0f;
        private uint _suppressFeedbackBelowTick;

        public int LeadTicks => Mathf.Clamp(
            Mathf.RoundToInt(_leadTicks),
            MovementNetcodeConfig.MinInputLeadTicks,
            MovementNetcodeConfig.MaxInputLeadTicks);

        // Evidence counters (S5 CSV/overlay).
        public long AckTicksObserved { get; private set; }
        public long SendCoveredFallbackAcks { get; private set; }
        public int LeadRaises { get; private set; }
        public int LeadLowers { get; private set; }
        public int LastAckBufferedCommands { get; private set; }

        /// <summary>
        /// Ignore ack feedback for ticks below the given anchor. Called at
        /// every command-numbering re-anchor (special-movement boundary, hard
        /// resync): those ticks' consume results describe input the client
        /// deliberately abandoned.
        /// </summary>
        public void SuppressFeedbackBelowTick(uint anchorTick)
        {
            if (anchorTick > _suppressFeedbackBelowTick)
                _suppressFeedbackBelowTick = anchorTick;
        }

        /// <summary>
        /// Feed one authoritative ack tick's consume truth.
        /// tickWasSent must be true only for ticks the client actually
        /// authored and sent — a fallback on a tick we never covered is the
        /// server's business, not a starvation signal.
        /// </summary>
        public void ObserveAck(uint tick, bool consumedCommand, int bufferedCommands, bool tickWasSent, float now)
        {
            if (tick < _suppressFeedbackBelowTick)
                return;

            AckTicksObserved++;
            LastAckBufferedCommands = bufferedCommands;

            if (tickWasSent && !consumedCommand)
                SendCoveredFallbackAcks++;

            // Starvation: send-covered occupancy below the setpoint floor
            // (a fallback tick always is; a real consume that left the
            // buffer empty is one jitter spike away from one).
            if (tickWasSent && bufferedCommands < MovementNetcodeConfig.BufferOccupancySetpointLow)
            {
                _surplusSinceTime = -1.0f;
                if (now >= _nextRaiseAllowedTime &&
                    _leadTicks < MovementNetcodeConfig.MaxInputLeadTicks)
                {
                    _leadTicks = Mathf.Min(
                        _leadTicks + 1.0f,
                        MovementNetcodeConfig.MaxInputLeadTicks);
                    _nextRaiseAllowedTime = now + MovementNetcodeConfig.LeadRaiseHoldoffSeconds;
                    LeadRaises++;
                }
                return;
            }

            // Surplus: occupancy above the setpoint ceiling, sustained.
            if (bufferedCommands > MovementNetcodeConfig.BufferOccupancySetpointHigh)
            {
                if (_surplusSinceTime < 0.0f)
                {
                    _surplusSinceTime = now;
                }
                else if (now - _surplusSinceTime >= MovementNetcodeConfig.LeadLowerAfterSurplusSeconds &&
                         _leadTicks > MovementNetcodeConfig.MinInputLeadTicks)
                {
                    _leadTicks = Mathf.Max(
                        _leadTicks - 1.0f,
                        MovementNetcodeConfig.MinInputLeadTicks);
                    _surplusSinceTime = now;
                    LeadLowers++;
                }
                return;
            }

            // In band: healthy.
            _surplusSinceTime = -1.0f;
        }

        /// <summary>
        /// The command-numbering anchor for a fresh start: far enough ahead
        /// of the estimated server tick to arrive in time, and always past
        /// everything already authored (the server's receive cursor never
        /// accepts a reused tick number).
        /// </summary>
        public uint ComputeForwardAnchorTick(float estimatedServerTick, uint highestAuthoredTick)
        {
            uint target = (uint)Mathf.Max(0f, Mathf.Ceil(estimatedServerTick)) + (uint)LeadTicks;
            return (target > highestAuthoredTick ? target : highestAuthoredTick) + 1u;
        }
    }
}

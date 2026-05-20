#nullable enable
using System.Collections.Generic;
using UnityEngine;
using Arena.Simulation;

namespace Arena.Input
{
    /// <summary>
    /// Rebuilds local predicted movement state from the latest authoritative
    /// snapshot plus all unacknowledged movement commands.
    /// </summary>
    public sealed class LocalMovementPredictor
    {
        private readonly IMovementEnvironment _environment;
        private readonly List<MovementCommand> _replayScratch =
            new(MovementNetcodeConfig.MaxPendingCommands);

        public LocalMovementPredictor(IMovementEnvironment environment)
        {
            _environment = environment;
        }

        public PredictedMovementState Rebuild(
            Vector3 authoritativePosition,
            Vector3 authoritativeVelocity,
            float authoritativeFacingYaw,
            bool authoritativeGrounded,
            uint lastProcessedTick,
            MovementCommandBuffer pendingCommands,
            ClientSimulationState simState,
            out ClientSimulationState.MovementContextSample effectiveContext,
            out int fallbackContextUses)
        {
            pendingCommands.CopyPendingTo(_replayScratch);

            PredictedMovementState state = new(
                authoritativePosition,
                authoritativeVelocity,
                authoritativeFacingYaw,
                authoritativeGrounded,
                lastProcessedTick);

            effectiveContext = simState.GetMovementContextForTick(lastProcessedTick, out bool usedFallback);
            fallbackContextUses = usedFallback ? 1 : 0;

            for (int i = 0; i < _replayScratch.Count; i++)
            {
                MovementCommand command = _replayScratch[i];
                if (command.InputTick <= lastProcessedTick)
                    continue;

                effectiveContext = simState.GetMovementContextForTick(command.InputTick, out usedFallback);
                if (usedFallback)
                    fallbackContextUses++;

                state = MovementPrediction.Step(
                    state,
                    command,
                    new MovementStepContext(
                        effectiveContext.MovementBlocked,
                        effectiveContext.MoveSpeedMultiplier,
                        effectiveContext.HitRadius,
                        effectiveContext.HitHeight),
                    _environment,
                    MovementNetcodeConfig.FixedTickSeconds);
            }

            return state;
        }
    }
}

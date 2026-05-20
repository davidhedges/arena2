#nullable enable
using System;
using System.Collections.Generic;
using Arena.Simulation;

namespace Arena.Presentation
{
    internal enum LocalSpellPresentationState
    {
        Idle,
        PredictedHold,
        PendingCorrelation,
        AuthoritativeHold,
        Released,
        Terminal,
    }

    internal enum LocalSpellPresentationCommandKind
    {
        None,
        StartHold,
        RequestCancel,
        RequestRelease,
    }

    internal enum LocalSpellPresentationCommandAuthority
    {
        Predicted,
        Authoritative,
    }

    internal readonly struct LocalSpellPresentationPoint
    {
        public LocalSpellPresentationPoint(float x, float y, float z)
        {
            HasValue = true;
            X = x;
            Y = y;
            Z = z;
        }

        public readonly bool HasValue;
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
    }

    internal readonly struct LocalSpellPresentationCommand
    {
        private static readonly LocalSpellPresentationCommand NoneCommand = new(
            LocalSpellPresentationCommandKind.None,
            string.Empty,
            string.Empty,
            default,
            0L,
            LocalSpellPresentationCommandAuthority.Predicted);

        private LocalSpellPresentationCommand(
            LocalSpellPresentationCommandKind kind,
            string spellActionId,
            string targetId,
            LocalSpellPresentationPoint aimPoint,
            long startedAtMs,
            LocalSpellPresentationCommandAuthority authority)
        {
            Kind = kind;
            SpellActionId = spellActionId ?? string.Empty;
            TargetId = targetId ?? string.Empty;
            AimPoint = aimPoint;
            StartedAtMs = startedAtMs;
            Authority = authority;
        }

        public readonly LocalSpellPresentationCommandKind Kind;
        public readonly string SpellActionId;
        public readonly string TargetId;
        public readonly LocalSpellPresentationPoint AimPoint;
        public readonly long StartedAtMs;
        public readonly LocalSpellPresentationCommandAuthority Authority;

        public static LocalSpellPresentationCommand None => NoneCommand;

        public static LocalSpellPresentationCommand StartHold(
            string spellActionId,
            string targetId,
            LocalSpellPresentationPoint aimPoint,
            long startedAtMs,
            LocalSpellPresentationCommandAuthority authority)
        {
            return new LocalSpellPresentationCommand(
                LocalSpellPresentationCommandKind.StartHold,
                spellActionId,
                targetId,
                aimPoint,
                startedAtMs,
                authority);
        }

        public static LocalSpellPresentationCommand RequestCancel(string spellActionId)
        {
            return new LocalSpellPresentationCommand(
                LocalSpellPresentationCommandKind.RequestCancel,
                spellActionId,
                string.Empty,
                default,
                0L,
                LocalSpellPresentationCommandAuthority.Authoritative);
        }

        public static LocalSpellPresentationCommand RequestRelease(
            string spellActionId,
            string targetId,
            LocalSpellPresentationPoint aimPoint,
            long startedAtMs)
        {
            return new LocalSpellPresentationCommand(
                LocalSpellPresentationCommandKind.RequestRelease,
                spellActionId,
                targetId,
                aimPoint,
                startedAtMs,
                LocalSpellPresentationCommandAuthority.Authoritative);
        }
    }

    internal readonly struct LocalSpellPresentationPredictInput
    {
        public LocalSpellPresentationPredictInput(
            string spellActionId,
            long startedAtMs,
            long expiresAtMs,
            string targetId,
            LocalSpellPresentationPoint aimPoint,
            CastActionToken token)
        {
            SpellActionId = spellActionId ?? string.Empty;
            StartedAtMs = startedAtMs;
            ExpiresAtMs = expiresAtMs;
            TargetId = targetId ?? string.Empty;
            AimPoint = aimPoint;
            Token = token;
        }

        public readonly string SpellActionId;
        public readonly long StartedAtMs;
        public readonly long ExpiresAtMs;
        public readonly string TargetId;
        public readonly LocalSpellPresentationPoint AimPoint;
        public readonly CastActionToken Token;
    }

    internal readonly struct LocalSpellPresentationActiveCast
    {
        public LocalSpellPresentationActiveCast(
            string castId,
            string spellActionId,
            string targetId,
            LocalSpellPresentationPoint aimPoint,
            long startedAtMs,
            long endsAtMs,
            string predictedCastId,
            ulong clientActionSeq)
        {
            CastId = castId ?? string.Empty;
            SpellActionId = spellActionId ?? string.Empty;
            TargetId = targetId ?? string.Empty;
            AimPoint = aimPoint;
            StartedAtMs = startedAtMs;
            EndsAtMs = endsAtMs;
            ReleaseRequested = false;
            PredictedCastId = predictedCastId ?? string.Empty;
            ClientActionSeq = clientActionSeq;
        }

        private LocalSpellPresentationActiveCast(
            string castId,
            string spellActionId,
            string targetId,
            LocalSpellPresentationPoint aimPoint,
            long startedAtMs,
            long endsAtMs,
            bool releaseRequested,
            string predictedCastId,
            ulong clientActionSeq)
        {
            CastId = castId ?? string.Empty;
            SpellActionId = spellActionId ?? string.Empty;
            TargetId = targetId ?? string.Empty;
            AimPoint = aimPoint;
            StartedAtMs = startedAtMs;
            EndsAtMs = endsAtMs;
            ReleaseRequested = releaseRequested;
            PredictedCastId = predictedCastId ?? string.Empty;
            ClientActionSeq = clientActionSeq;
        }

        public readonly string CastId;
        public readonly string SpellActionId;
        public readonly string TargetId;
        public readonly LocalSpellPresentationPoint AimPoint;
        public readonly long StartedAtMs;
        public readonly long EndsAtMs;
        public readonly bool ReleaseRequested;
        public readonly string PredictedCastId;
        public readonly ulong ClientActionSeq;

        public LocalSpellPresentationActiveCast WithReleaseRequested()
        {
            return new LocalSpellPresentationActiveCast(
                CastId,
                SpellActionId,
                TargetId,
                AimPoint,
                StartedAtMs,
                EndsAtMs,
                true,
                PredictedCastId,
                ClientActionSeq);
        }

        public LocalSpellPresentationActiveCast WithPredictionToken(string predictedCastId, ulong clientActionSeq)
        {
            return new LocalSpellPresentationActiveCast(
                CastId,
                SpellActionId,
                TargetId,
                AimPoint,
                StartedAtMs,
                EndsAtMs,
                ReleaseRequested,
                predictedCastId,
                clientActionSeq);
        }

        public LocalSpellPresentationActiveCast WithTiming(long startedAtMs, long endsAtMs)
        {
            return new LocalSpellPresentationActiveCast(
                CastId,
                SpellActionId,
                TargetId,
                AimPoint,
                startedAtMs,
                endsAtMs,
                ReleaseRequested,
                PredictedCastId,
                ClientActionSeq);
        }
    }

    internal readonly struct LocalSpellPresentationResult
    {
        public LocalSpellPresentationResult(
            string actionInstanceId,
            string predictedCastId,
            ulong clientActionSeq,
            string result)
        {
            ActionInstanceId = actionInstanceId ?? string.Empty;
            Token = new CastActionToken(predictedCastId ?? string.Empty, clientActionSeq, string.Empty);
            Result = result ?? string.Empty;
        }

        public readonly string ActionInstanceId;
        public readonly CastActionToken Token;
        public readonly string Result;
    }

    internal sealed class LocalSpellPresentationStateMachine
    {
        private const string CastResultAccepted = "accepted";
        private const string CastResultRejected = "rejected";
        private const string CastResultCanceled = "canceled";
        private const string CastResultCancelTooLate = "cancel_too_late";
        private const string CastResultStaleToken = "stale_token";

        private readonly Dictionary<string, CastActionToken> _acceptedActionTokens = new(StringComparer.Ordinal);
        private PendingPredictedHold? _pendingPrediction;
        private LocalSpellPresentationActiveCast? _activeCast;
        private LocalSpellPresentationActiveCast? _pendingCorrelation;

        public LocalSpellPresentationState State { get; private set; } = LocalSpellPresentationState.Idle;
        public bool HasPendingPrediction => _pendingPrediction.HasValue;
        public LocalSpellPresentationActiveCast? ActiveCast => _activeCast;

        public LocalSpellPresentationCommand Predict(LocalSpellPresentationPredictInput input)
        {
            _pendingPrediction = new PendingPredictedHold(
                input.SpellActionId,
                input.StartedAtMs,
                input.ExpiresAtMs,
                input.TargetId,
                input.AimPoint,
                input.Token.PredictedCastId,
                input.Token.ClientActionSeq);
            _activeCast = null;
            _pendingCorrelation = null;
            State = LocalSpellPresentationState.PredictedHold;

            return LocalSpellPresentationCommand.StartHold(
                input.SpellActionId,
                input.TargetId,
                input.AimPoint,
                input.StartedAtMs,
                LocalSpellPresentationCommandAuthority.Predicted);
        }

        public LocalSpellPresentationCommand ActiveCastInserted(LocalSpellPresentationActiveCast activeCast)
        {
            LocalSpellPresentationActiveCast next = WithAcceptedToken(activeCast);

            if (_pendingPrediction is { } prediction
                && string.Equals(prediction.SpellActionId, next.SpellActionId, StringComparison.Ordinal))
            {
                if (PredictionMatches(prediction, ToToken(next), next.SpellActionId))
                {
                    _activeCast = next.WithTiming(
                        prediction.StartedAtMs,
                        Math.Max(prediction.StartedAtMs, next.EndsAtMs));
                    _pendingPrediction = null;
                    _pendingCorrelation = null;
                    State = LocalSpellPresentationState.AuthoritativeHold;
                    return LocalSpellPresentationCommand.None;
                }

                if (HasAcceptedToken(next.CastId))
                {
                    _pendingCorrelation = null;
                    State = LocalSpellPresentationState.PredictedHold;
                    return LocalSpellPresentationCommand.None;
                }

                _pendingCorrelation = next;
                State = LocalSpellPresentationState.PendingCorrelation;
                return LocalSpellPresentationCommand.None;
            }

            _activeCast = next;
            _pendingCorrelation = null;
            State = LocalSpellPresentationState.AuthoritativeHold;
            return LocalSpellPresentationCommand.StartHold(
                next.SpellActionId,
                next.TargetId,
                next.AimPoint,
                next.StartedAtMs,
                LocalSpellPresentationCommandAuthority.Authoritative);
        }

        public LocalSpellPresentationCommand ActiveCastUpdated(LocalSpellPresentationActiveCast activeCast)
        {
            LocalSpellPresentationActiveCast next = WithAcceptedToken(activeCast);

            if (_pendingCorrelation.HasValue
                && string.Equals(_pendingCorrelation.Value.CastId, next.CastId, StringComparison.Ordinal))
            {
                _pendingCorrelation = next;
                State = LocalSpellPresentationState.PendingCorrelation;
                return LocalSpellPresentationCommand.None;
            }

            if (_activeCast.HasValue
                && string.Equals(_activeCast.Value.CastId, next.CastId, StringComparison.Ordinal))
            {
                LocalSpellPresentationActiveCast current = _activeCast.Value;
                if (ActiveCastMatches(next, ToToken(current), next.SpellActionId))
                {
                    next = next.WithTiming(
                        current.StartedAtMs,
                        Math.Max(current.StartedAtMs, next.EndsAtMs));
                }
                if (current.ReleaseRequested)
                    next = next.WithReleaseRequested();
            }

            _activeCast = next;
            State = next.ReleaseRequested
                ? LocalSpellPresentationState.Released
                : LocalSpellPresentationState.AuthoritativeHold;
            return LocalSpellPresentationCommand.None;
        }

        public LocalSpellPresentationCommand ActiveCastDeleted(string castId, string spellActionId)
        {
            if (_activeCast.HasValue
                && string.Equals(_activeCast.Value.CastId, castId, StringComparison.Ordinal))
            {
                bool releaseAlreadyRequested = _activeCast.Value.ReleaseRequested;
                _activeCast = null;
                _pendingCorrelation = null;
                _pendingPrediction = null;
                RemoveActionState(castId);
                State = LocalSpellPresentationState.Terminal;
                return releaseAlreadyRequested
                    ? LocalSpellPresentationCommand.None
                    : LocalSpellPresentationCommand.RequestCancel(spellActionId);
            }

            if (_pendingCorrelation.HasValue
                && string.Equals(_pendingCorrelation.Value.CastId, castId, StringComparison.Ordinal))
            {
                _pendingCorrelation = null;
                State = _pendingPrediction.HasValue
                    ? LocalSpellPresentationState.PredictedHold
                    : LocalSpellPresentationState.Idle;
            }

            RemoveActionState(castId);
            return LocalSpellPresentationCommand.None;
        }

        public LocalSpellPresentationCommand PredictedActionResultInserted(LocalSpellPresentationResult result)
        {
            if (string.Equals(result.Result, CastResultAccepted, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(result.ActionInstanceId) && result.Token.IsPredicted)
                    _acceptedActionTokens[result.ActionInstanceId] = result.Token;

                if (_pendingPrediction is { } prediction
                    && TokenMatches(prediction, result.Token)
                    && _pendingCorrelation.HasValue
                    && string.Equals(
                        _pendingCorrelation.Value.CastId,
                        result.ActionInstanceId,
                        StringComparison.Ordinal))
                {
                    _activeCast = _pendingCorrelation.Value.WithPredictionToken(
                        result.Token.PredictedCastId,
                        result.Token.ClientActionSeq);
                    _pendingCorrelation = null;
                    _pendingPrediction = null;
                    State = LocalSpellPresentationState.AuthoritativeHold;
                }

                return LocalSpellPresentationCommand.None;
            }

            bool isStaleToken = string.Equals(result.Result, CastResultStaleToken, StringComparison.Ordinal);
            bool shouldCancelLocalPresentation =
                !isStaleToken
                && (string.Equals(result.Result, CastResultRejected, StringComparison.Ordinal)
                    || string.Equals(result.Result, CastResultCanceled, StringComparison.Ordinal));
            bool shouldClearActivePresentation = shouldCancelLocalPresentation
                || string.Equals(result.Result, CastResultCancelTooLate, StringComparison.Ordinal);
            bool shouldDropAcceptedToken = shouldCancelLocalPresentation
                || string.Equals(result.Result, CastResultCancelTooLate, StringComparison.Ordinal);

            LocalSpellPresentationCommand command = LocalSpellPresentationCommand.None;
            if (_pendingPrediction is { } pending && TokenMatches(pending, result.Token))
            {
                _pendingPrediction = null;
                if (shouldCancelLocalPresentation)
                    command = LocalSpellPresentationCommand.RequestCancel(pending.SpellActionId);
            }

            if (_activeCast.HasValue
                && string.Equals(_activeCast.Value.CastId, result.ActionInstanceId, StringComparison.Ordinal))
            {
                string spellActionId = _activeCast.Value.SpellActionId;
                if (shouldClearActivePresentation)
                {
                    _activeCast = null;
                    if (shouldCancelLocalPresentation && command.Kind == LocalSpellPresentationCommandKind.None)
                        command = LocalSpellPresentationCommand.RequestCancel(spellActionId);
                }
            }

            if (_pendingCorrelation.HasValue
                && string.Equals(_pendingCorrelation.Value.CastId, result.ActionInstanceId, StringComparison.Ordinal))
            {
                _pendingCorrelation = null;
            }

            if (shouldDropAcceptedToken)
                RemoveActionState(result.ActionInstanceId);

            State = ResolveStateAfterTerminalInput();
            return command;
        }

        public LocalSpellPresentationCommand LocalCancel(CastActionToken token)
        {
            string normalizedKind = token.Kind;
            bool canceled = false;
            if (_pendingPrediction.HasValue && PredictionMatches(_pendingPrediction.Value, token, normalizedKind))
            {
                _pendingPrediction = null;
                canceled = true;
            }

            if (_activeCast.HasValue && ActiveCastMatches(_activeCast.Value, token, normalizedKind))
            {
                _activeCast = null;
                canceled = true;
            }

            State = ResolveStateAfterTerminalInput();
            return canceled
                ? LocalSpellPresentationCommand.RequestCancel(normalizedKind)
                : LocalSpellPresentationCommand.None;
        }

        public LocalSpellPresentationCommand Timeout(long nowMs)
        {
            if (!_pendingPrediction.HasValue)
                return LocalSpellPresentationCommand.None;

            PendingPredictedHold pending = _pendingPrediction.Value;
            if (nowMs < pending.ExpiresAtMs)
                return LocalSpellPresentationCommand.None;

            _pendingPrediction = null;
            _pendingCorrelation = null;
            State = ResolveStateAfterTerminalInput();
            return LocalSpellPresentationCommand.RequestCancel(pending.SpellActionId);
        }

        public LocalSpellPresentationCommand CombatRelease(string actionKind, long releaseStartMs)
        {
            if (!_activeCast.HasValue)
                return LocalSpellPresentationCommand.None;

            LocalSpellPresentationActiveCast active = _activeCast.Value;
            if (active.ReleaseRequested
                || !string.Equals(active.SpellActionId, actionKind, StringComparison.Ordinal))
            {
                return LocalSpellPresentationCommand.None;
            }

            _activeCast = active.WithReleaseRequested();
            State = LocalSpellPresentationState.Released;
            return LocalSpellPresentationCommand.RequestRelease(
                active.SpellActionId,
                active.TargetId,
                active.AimPoint,
                releaseStartMs);
        }

        public LocalSpellPresentationCommand ScheduledReleaseDue(long releaseStartMs)
        {
            if (!_activeCast.HasValue)
                return LocalSpellPresentationCommand.None;

            LocalSpellPresentationActiveCast active = _activeCast.Value;
            if (active.ReleaseRequested)
                return LocalSpellPresentationCommand.None;

            _activeCast = active.WithReleaseRequested();
            State = LocalSpellPresentationState.Released;
            return LocalSpellPresentationCommand.RequestRelease(
                active.SpellActionId,
                active.TargetId,
                active.AimPoint,
                releaseStartMs);
        }

        private LocalSpellPresentationActiveCast WithAcceptedToken(LocalSpellPresentationActiveCast activeCast)
        {
            return _acceptedActionTokens.TryGetValue(activeCast.CastId, out CastActionToken token)
                ? activeCast.WithPredictionToken(token.PredictedCastId, token.ClientActionSeq)
                : activeCast;
        }

        private bool HasAcceptedToken(string actionInstanceId)
        {
            return !string.IsNullOrWhiteSpace(actionInstanceId)
                && _acceptedActionTokens.ContainsKey(actionInstanceId);
        }

        private void RemoveActionState(string actionInstanceId)
        {
            if (!string.IsNullOrWhiteSpace(actionInstanceId))
                _acceptedActionTokens.Remove(actionInstanceId);
        }

        private LocalSpellPresentationState ResolveStateAfterTerminalInput()
        {
            if (_activeCast.HasValue)
                return _activeCast.Value.ReleaseRequested
                    ? LocalSpellPresentationState.Released
                    : LocalSpellPresentationState.AuthoritativeHold;
            if (_pendingCorrelation.HasValue)
                return LocalSpellPresentationState.PendingCorrelation;
            if (_pendingPrediction.HasValue)
                return LocalSpellPresentationState.PredictedHold;
            return LocalSpellPresentationState.Terminal;
        }

        private static CastActionToken ToToken(LocalSpellPresentationActiveCast active)
        {
            return new CastActionToken(active.PredictedCastId, active.ClientActionSeq, active.SpellActionId);
        }

        private static bool PredictionMatches(
            PendingPredictedHold pending,
            CastActionToken token,
            string normalizedKind)
        {
            if (!string.Equals(pending.SpellActionId, normalizedKind, StringComparison.Ordinal))
                return false;
            if (!token.IsPredicted)
                return false;

            return TokenMatches(pending, token);
        }

        private static bool TokenMatches(PendingPredictedHold pending, CastActionToken token)
        {
            return token.IsPredicted
                && string.Equals(pending.PredictedCastId, token.PredictedCastId, StringComparison.Ordinal)
                && pending.ClientActionSeq == token.ClientActionSeq;
        }

        private static bool ActiveCastMatches(
            LocalSpellPresentationActiveCast active,
            CastActionToken token,
            string normalizedKind)
        {
            if (!string.Equals(active.SpellActionId, normalizedKind, StringComparison.Ordinal))
                return false;
            if (!token.IsPredicted)
                return true;
            if (string.IsNullOrWhiteSpace(active.PredictedCastId))
                return false;

            return string.Equals(active.PredictedCastId, token.PredictedCastId, StringComparison.Ordinal)
                && active.ClientActionSeq == token.ClientActionSeq;
        }

        private readonly struct PendingPredictedHold
        {
            public PendingPredictedHold(
                string spellActionId,
                long startedAtMs,
                long expiresAtMs,
                string targetId,
                LocalSpellPresentationPoint aimPoint,
                string predictedCastId,
                ulong clientActionSeq)
            {
                SpellActionId = spellActionId ?? string.Empty;
                StartedAtMs = startedAtMs;
                ExpiresAtMs = expiresAtMs;
                TargetId = targetId ?? string.Empty;
                AimPoint = aimPoint;
                PredictedCastId = predictedCastId ?? string.Empty;
                ClientActionSeq = clientActionSeq;
            }

            public readonly string SpellActionId;
            public readonly long StartedAtMs;
            public readonly long ExpiresAtMs;
            public readonly string TargetId;
            public readonly LocalSpellPresentationPoint AimPoint;
            public readonly string PredictedCastId;
            public readonly ulong ClientActionSeq;
        }
    }
}

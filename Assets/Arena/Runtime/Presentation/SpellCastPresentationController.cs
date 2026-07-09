#nullable enable
using System.Collections.Generic;
using Arena.Combat;
using Arena.Entity;
using Arena.Network;
using Arena.Simulation;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Unity adapter for cast-time spell presentation. Lifecycle decisions live
    /// in LocalSpellPresentationStateMachine; this type only converts callbacks
    /// and dispatches animation commands.
    /// </summary>
    public sealed class SpellCastPresentationController : MonoBehaviour
    {
        private const long PredictionConfirmTimeoutMs = 750L;
        private const long ActionSuppressionRetentionMs = 10_000L;

        private readonly LocalSpellPresentationStateMachine _stateMachine = new();
        private readonly Dictionary<string, long> _suppressedActionInstanceIds = new(System.StringComparer.Ordinal);

        private PlayerEntity? _owner;
        private bool _isLocalPlayer;
        private string _locallySuppressedSpellActionId = string.Empty;

        public void Initialize(PlayerEntity owner, bool isLocalPlayer)
        {
            _owner = owner;
            _isLocalPlayer = isLocalPlayer;
        }

        public void PredictLocalCastHold(
            string spellActionId,
            long localStartedAtMs,
            string targetId,
            Vector3? aimPoint,
            CastActionToken token)
        {
            if (!_isLocalPlayer || _owner == null)
                return;

            _locallySuppressedSpellActionId = string.Empty;
            Dispatch(_stateMachine.Predict(
                new LocalSpellPresentationPredictInput(
                    WireIdentifier.Normalize(spellActionId),
                    localStartedAtMs,
                    localStartedAtMs + PredictionConfirmTimeoutMs,
                    targetId ?? string.Empty,
                    ToPoint(aimPoint),
                    token)));
        }

        public void OnActiveCastInsert(ActiveCast row, ulong castTimeMs)
        {
            if (_owner == null || !ShouldTrackActiveCast(row, castTimeMs))
                return;

            LocalSpellPresentationActiveCast activeCast = FromActiveCast(row);
            if (_isLocalPlayer && IsActionSuppressed(activeCast.CastId))
                return;

            // Keep the kind fallback only for the narrow pre-result race: the
            // local player canceled, but the old ActiveCast insert arrives
            // before its owner-scoped PredictedActionResult can identify the action id.
            // Do not apply it while a new prediction is pending; otherwise an
            // old same-kind cast could hide the new local hold.
            if (_isLocalPlayer
                && !_stateMachine.HasPendingPrediction
                && IsLocallySuppressed(activeCast.SpellActionId))
            {
                return;
            }

            Dispatch(_stateMachine.ActiveCastInserted(activeCast));
        }

        public void OnActiveCastUpdate(ActiveCast row, ulong castTimeMs)
        {
            if (_owner == null || !ShouldTrackActiveCast(row, castTimeMs))
                return;

            LocalSpellPresentationActiveCast activeCast = FromActiveCast(row);
            if (_isLocalPlayer && IsActionSuppressed(activeCast.CastId))
                return;
            if (_isLocalPlayer
                && !_stateMachine.HasPendingPrediction
                && IsLocallySuppressed(activeCast.SpellActionId))
            {
                return;
            }

            Dispatch(_stateMachine.ActiveCastUpdated(activeCast));
        }

        public void OnActiveCastDelete(ActiveCast row)
        {
            string castId = row.CastId ?? string.Empty;
            if (_isLocalPlayer && IsActionSuppressed(castId))
                _locallySuppressedSpellActionId = string.Empty;

            RemoveActionSuppression(castId);
            Dispatch(_stateMachine.ActiveCastDeleted(castId, WireIdentifier.Normalize(row.Kind)));
        }

        public void OnPredictedActionResultInsert(PredictedActionResult row)
        {
            if (!_isLocalPlayer || _owner == null || row.Family != PredictedActionFamily.SpellCast)
                return;

            string actionInstanceId = row.ActionInstanceId ?? string.Empty;
            PruneSuppressedActionIds();
            if (row.Result == ActionResultKind.Canceled || row.Result == ActionResultKind.CancelTooLate)
            {
                if (!string.IsNullOrWhiteSpace(actionInstanceId))
                    _suppressedActionInstanceIds[actionInstanceId] =
                        NowMs() + ActionSuppressionRetentionMs;
            }
            if (row.Result != ActionResultKind.Accepted)
                _locallySuppressedSpellActionId = string.Empty;

            Dispatch(_stateMachine.PredictedActionResultInserted(
                new LocalSpellPresentationResult(
                    actionInstanceId,
                    row.PredictedActionId ?? string.Empty,
                    row.ClientActionSeq,
                    ToResultString(row.Result))));
        }

        private static string ToResultString(ActionResultKind result)
        {
            return result switch
            {
                ActionResultKind.Accepted => "accepted",
                ActionResultKind.Canceled => "canceled",
                ActionResultKind.CancelTooLate => "cancel_too_late",
                ActionResultKind.StaleToken => "stale_token",
                _ => "rejected",
            };
        }

        public void OnCombatRelease(CombatEvent row)
        {
            if (_owner != null && !_owner.PlaysSpellReleasePresentation(row.ActionKind))
                return;

            Dispatch(_stateMachine.CombatRelease(
                WireIdentifier.Normalize(row.ActionKind),
                row.CreatedAt.MicrosecondsSinceUnixEpoch / 1000L));
        }

        public void CancelLocalCastHold(CastActionToken token)
        {
            if (!_isLocalPlayer || _owner == null)
                return;

            if (!string.IsNullOrWhiteSpace(token.Kind))
                _locallySuppressedSpellActionId = WireIdentifier.Normalize(token.Kind);

            Dispatch(_stateMachine.LocalCancel(token));
        }

        private void Update()
        {
            if (_owner == null)
                return;

            PruneSuppressedActionIds();
            LocalSpellPresentationCommand timeoutCommand = _stateMachine.Timeout(NowMs());
            Dispatch(timeoutCommand);
            UpdateScheduledRelease();
        }

        private void UpdateScheduledRelease()
        {
            if (_owner == null || _stateMachine.ActiveCast is not { } active)
                return;
            if (!ArenaServerClock.HasEstimate)
                return;
            if (!_owner.PlaysSpellReleasePresentation(active.SpellActionId))
                return;

            if (!_owner.TryResolveSpellReleaseOffsetSeconds(active.SpellActionId, out float releaseOffsetSeconds))
                releaseOffsetSeconds = 0f;

            long releaseStartMs = ComputeReleaseStartMs(active.StartedAtMs, active.EndsAtMs, releaseOffsetSeconds);
            if (ArenaServerClock.ServerNowMs < releaseStartMs)
                return;

            Dispatch(_stateMachine.ScheduledReleaseDue(releaseStartMs));
        }

        internal static long ComputeReleaseStartMs(
            long startedAtMs,
            long endsAtMs,
            float authoredReleaseOffsetSeconds)
        {
            // Release alignment is authored by OnReleaseFrame in clip seconds.
            // ActiveCast is the authoritative, cast-speed-scaled server window.
            // Clamp the authored lead-in so fast casts never schedule release before
            // the server says the cast actually started.
            long castDurationMs = System.Math.Max(0L, endsAtMs - startedAtMs);
            long authoredOffsetMs = ResolveFiniteOffsetMs(authoredReleaseOffsetSeconds);
            long effectiveOffsetMs = System.Math.Min(authoredOffsetMs, castDurationMs);
            return endsAtMs - effectiveOffsetMs;
        }

        private static long ResolveFiniteOffsetMs(float offsetSeconds)
        {
            if (float.IsNaN(offsetSeconds) || float.IsInfinity(offsetSeconds) || offsetSeconds <= 0f)
                return 0L;

            return System.Math.Max(0L, Mathf.RoundToInt(offsetSeconds * 1000f));
        }

        private bool ShouldTrackActiveCast(ActiveCast row, ulong castTimeMs)
        {
            if (castTimeMs > 0UL)
                return true;

            return _owner != null && _owner.UsesSpellCastHoldPresentation(row.Kind);
        }

        private void Dispatch(LocalSpellPresentationCommand command)
        {
            if (_owner == null)
                return;

            switch (command.Kind)
            {
                case LocalSpellPresentationCommandKind.StartHold:
                    RequestHoldStart(command);
                    break;
                case LocalSpellPresentationCommandKind.RequestCancel:
                    RequestCancel(command.SpellActionId);
                    break;
                case LocalSpellPresentationCommandKind.RequestRelease:
                    RequestRelease(command);
                    break;
            }
        }

        private void RequestHoldStart(LocalSpellPresentationCommand command)
        {
            if (_owner == null)
                return;

            Vector3? facing = ResolveFacingPoint(command.TargetId, ToVector3(command.AimPoint));
            var authority = command.Authority == LocalSpellPresentationCommandAuthority.Predicted
                ? CombatAnimationAuthority.Predicted
                : CombatAnimationAuthority.Authoritative;
            string source = command.Authority == LocalSpellPresentationCommandAuthority.Predicted
                ? CombatEventSources.PlayerInput
                : CombatEventSources.Spell;

            var request = new CombatAnimationRequest(
                command.SpellActionId,
                CombatAnimationCategory.Spell,
                authority,
                CombatAnimationPlayback.Automatic,
                CombatSpellAnimationPhase.HoldStart,
                source,
                command.StartedAtMs,
                facing);
            _owner.RequestCombatAnimation(request);
        }

        private void RequestRelease(LocalSpellPresentationCommand command)
        {
            if (_owner == null)
                return;

            Vector3? facing = ResolveFacingPoint(command.TargetId, ToVector3(command.AimPoint));
            _owner.RequestCombatAnimation(
                CombatAnimationRequest.AuthoritativeSpell(
                    command.SpellActionId,
                    command.StartedAtMs,
                    CombatSpellAnimationPhase.Release,
                    CombatEventSources.Spell,
                    facing));
        }

        private void RequestCancel(string spellActionId)
        {
            if (_owner == null || string.IsNullOrWhiteSpace(spellActionId))
                return;

            _owner.RequestCombatAnimation(
                CombatAnimationRequest.AuthoritativeSpell(
                    spellActionId,
                    NowMs(),
                    CombatSpellAnimationPhase.Cancel,
                    CombatEventSources.Spell));
        }

        private bool IsLocallySuppressed(string spellActionId)
        {
            return !string.IsNullOrWhiteSpace(_locallySuppressedSpellActionId)
                && string.Equals(
                    _locallySuppressedSpellActionId,
                    WireIdentifier.Normalize(spellActionId),
                    System.StringComparison.Ordinal);
        }

        private bool IsActionSuppressed(string actionInstanceId)
        {
            PruneSuppressedActionIds();
            return !string.IsNullOrWhiteSpace(actionInstanceId)
                && _suppressedActionInstanceIds.ContainsKey(actionInstanceId);
        }

        private void RemoveActionSuppression(string actionInstanceId)
        {
            if (!string.IsNullOrWhiteSpace(actionInstanceId))
                _suppressedActionInstanceIds.Remove(actionInstanceId);
        }

        private void PruneSuppressedActionIds()
        {
            if (_suppressedActionInstanceIds.Count == 0)
                return;

            long nowMs = NowMs();
            List<string>? expired = null;
            foreach (var (actionInstanceId, expiresAtMs) in _suppressedActionInstanceIds)
            {
                if (expiresAtMs > nowMs)
                    continue;

                expired ??= new List<string>();
                expired.Add(actionInstanceId);
            }

            if (expired == null)
                return;

            foreach (string actionInstanceId in expired)
                _suppressedActionInstanceIds.Remove(actionInstanceId);
        }

        private static LocalSpellPresentationActiveCast FromActiveCast(ActiveCast row)
        {
            return new LocalSpellPresentationActiveCast(
                row.CastId ?? string.Empty,
                WireIdentifier.Normalize(row.Kind),
                row.TargetId ?? string.Empty,
                ToPoint(new Vector3(row.AimX, row.AimY, row.AimZ)),
                row.StartedAt.MicrosecondsSinceUnixEpoch / 1000L,
                row.EndsAt.MicrosecondsSinceUnixEpoch / 1000L,
                row.PredictedCastId ?? string.Empty,
                row.ClientActionSeq);
        }

        private static LocalSpellPresentationPoint ToPoint(Vector3? point)
        {
            if (!point.HasValue || point.Value.sqrMagnitude <= 0.0001f)
                return default;

            Vector3 value = point.Value;
            return new LocalSpellPresentationPoint(value.x, value.y, value.z);
        }

        private static Vector3? ToVector3(LocalSpellPresentationPoint point)
        {
            return point.HasValue
                ? new Vector3(point.X, point.Y, point.Z)
                : null;
        }

        private static Vector3? ResolveFacingPoint(string targetId, Vector3? aimPoint)
        {
            if (aimPoint.HasValue && aimPoint.Value.sqrMagnitude > 0.0001f)
                return aimPoint.Value;

            if (!string.IsNullOrWhiteSpace(targetId)
                && EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetCombatTargetByHex(targetId, out ICombatTargetEntity target))
            {
                return target.GetPresentationRoot().position + Vector3.up;
            }

            return null;
        }

        private static long NowMs() => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}

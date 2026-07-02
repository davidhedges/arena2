#nullable enable
using System.Collections.Generic;
using UnityEngine;
using Arena.Combat;
using Arena.Entity;
using Arena.Network;
using Arena.Simulation;
using Arena.Presentation;
using Arena.Debugging;
using SpacetimeDB.Types;

namespace Arena.Input
{
    /// <summary>
    /// Handles client-side melee prediction for slotted action-bar actions.
    /// Direct keyboard ownership lives in SpellInputHandler; this component only
    /// flushes queued followups and exposes TryTriggerAction for action-bar-dispatched
    /// melee inputs.
    /// </summary>
    public class MeleeInputHandler : MonoBehaviour
    {
        public static MeleeInputHandler? Instance { get; private set; }

        private string _lastStrikeId = string.Empty;
        private long _lastStrikeAtMs;
        private string _queuedStrikeId = string.Empty;
        private long _queuedStrikeExecuteAtMs;
        private const long PredictedStrikeStateRetentionMs = 2500L;
        private const long PredictedStrikeVisualRetentionMs = 400L;
        private const long PendingMeleePredictionTtlMs = 5000L;
        private const long PendingLocalMeleeEventHoldMs = 250L;
        private readonly Dictionary<string, long> _predictedStrikeVisualUntilMs = new();
        private readonly Dictionary<string, PendingPredictedMeleeVisual> _pendingPredictedMeleeByToken = new();
        private readonly Dictionary<string, AcceptedPredictedMeleeAction> _acceptedPredictedMeleeByActionInstance = new();
        private readonly List<PendingAuthoritativeMeleeReplay> _pendingAuthoritativeMeleeReplays = new();
        private readonly Dictionary<string, (PredictedActionLedger ledger, long expiresAtMs)> _predictionLedgersByToken = new();

        private readonly struct PendingPredictedMeleeVisual
        {
            public PendingPredictedMeleeVisual(
                ActionPredictionToken token,
                string authoredActionId,
                string runtimeActionId,
                long startedAtMs,
                long expiresAtMs)
            {
                Token = token;
                AuthoredActionId = authoredActionId ?? string.Empty;
                RuntimeActionId = runtimeActionId ?? string.Empty;
                StartedAtMs = startedAtMs;
                ExpiresAtMs = expiresAtMs;
            }

            public ActionPredictionToken Token { get; }
            public string AuthoredActionId { get; }
            public string RuntimeActionId { get; }
            public long StartedAtMs { get; }
            public long ExpiresAtMs { get; }
        }

        private readonly struct PendingAuthoritativeMeleeReplay
        {
            public PendingAuthoritativeMeleeReplay(string actionInstanceId, CombatAnimationRequest request, long releaseAtMs)
            {
                ActionInstanceId = actionInstanceId ?? string.Empty;
                Request = request;
                ReleaseAtMs = releaseAtMs;
            }

            public string ActionInstanceId { get; }
            public CombatAnimationRequest Request { get; }
            public long ReleaseAtMs { get; }
        }

        private readonly struct AcceptedPredictedMeleeAction
        {
            public AcceptedPredictedMeleeAction(string tokenKey, long expiresAtMs)
            {
                TokenKey = tokenKey ?? string.Empty;
                ExpiresAtMs = expiresAtMs;
            }

            public string TokenKey { get; }
            public long ExpiresAtMs { get; }
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            var entity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (entity == null || entity.IsDestroyed || !entity.IsAlive) return;

            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LocalCombatState.Instance.ReconcilePredictedPrimaryResource(entity);
            FlushQueuedLocalStrike(entity, nowMs);
            FlushPendingAuthoritativeMeleeReplays(entity, nowMs);
            PrunePendingMeleePredictionState(nowMs);
        }

        public bool TryTriggerAction(DbConnection conn, PlayerEntity entity, string slotId)
        {
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return TryTriggerAction(conn, entity, slotId, nowMs);
        }

        private bool TryTriggerAction(DbConnection conn, PlayerEntity entity, string slotId, long nowMs)
        {
            if (entity.IsDestroyed || !entity.IsAlive)
            {
                ActionBarTrace.Trace($"melee rejected: {slotId} has no live local entity");
                return false;
            }

            string combatProfile = CombatProfileResolver.ResolveForEntity(conn, entity);

            if (!IsDirectlyBindableMeleeStrike(conn, combatProfile, slotId))
            {
                ActionBarTrace.Trace($"melee rejected: {slotId} is not a bindable opener for {combatProfile}");
                return false;
            }

            var combat = LocalCombatState.Instance;

            // Resolve which strike this keypress fires from authoritative player
            // state timing so early local inputs cannot predict combo follow-ups
            // before the server has actually accepted the predecessor strike.
            var authorityState = GetAuthoritativeLastStrikeState(conn, entity.Identity);
            var effectiveLastStrikeState = GetEffectiveLastStrikeState(authorityState, nowMs);
            var strikeChoice = ResolveStrikeChoice(
                conn, combatProfile, slotId,
                effectiveLastStrikeState.lastStrikeId, effectiveLastStrikeState.lastStrikeAtMs, nowMs);
            slotId = strikeChoice.strikeId;
            MeleeAbilityCatalog? gameplay =
                MeleeGameplayResolver.ResolveForAction(conn, entity.Identity, combatProfile, slotId);
            if (gameplay == null)
            {
                ActionBarTrace.Trace($"melee rejected: {slotId} has no melee gameplay row");
                return false;
            }
            MeleeGapCloseCatalog? gapClose = ResolveGapCloseForAction(conn, entity.Identity, combatProfile, slotId);
            if (!HasResourceForMeleeAction(conn, entity, slotId))
                return false;

            bool requiresTarget = RequiresTarget(gameplay);

            ICombatTargetEntity? target = null;
            if (requiresTarget)
            {
                target = TargetSelector.Instance?.SelectedTarget;
                if (target == null || target.IsDestroyed || !target.IsAlive)
                {
                    ActionBarTrace.Trace($"melee rejected: {slotId} requires a live target");
                    return false;
                }
                if (!IsTargetWithinFacingArc(entity, target))
                {
                    ActionBarTrace.Trace($"melee rejected: {slotId} requires target facing");
                    return false;
                }
                if (!PartyRelationship.TargetAudienceAllowsLocal(target, gameplay.TargetAudience))
                {
                    var relation = PartyRelationship.RelationToLocal(target);
                    ActionBarTrace.Trace(
                        $"melee rejected: {slotId} target audience {TraceAudience(gameplay.TargetAudience)} rejects relation={relation} target={target.DisplayName}");
                    return false;
                }
            }

            // Root strikes still respect the active GCD. Authored combo follow-ups
            // chain on their own transition timing and can execute while the prior
            // strike's GCD is still active.
            bool usesGlobalCooldown = gameplay.UsesGlobalCooldown;
            bool isComboFollowup = IsComboFollowup(conn, combatProfile, slotId);
            if (!strikeChoice.shouldQueue)
            {
                if (usesGlobalCooldown && !isComboFollowup && combat.IsGlobalCooldownActive(nowMs))
                {
                    ActionBarTrace.Trace($"melee rejected: {slotId} blocked by GCD");
                    return false;
                }
            }

            // Per-strike cooldown check (melee cooldowns share the SpellCooldown table,
            // now keyed by canonical combat-style slot id.
            if (combat.SpellCooldowns.TryGetValue(slotId, out var cd))
            {
                if (nowMs < cd.lastCastMs + cd.durationMs)
                {
                    ActionBarTrace.Trace($"melee rejected: {slotId} blocked by cooldown");
                    return false;
                }
            }

            // Range check — horizontal distance against the server-synced definition.
            var localPos = entity.GameObject.transform.position;
            if (requiresTarget)
            {
                float strikeRange = MeleeAttackModifierResolver.ResolveEffectiveRange(
                    conn,
                    entity.Identity,
                    gameplay.Range,
                    nowMs);
                var targetPos = target!.GetPresentationRoot().position;
                float horizDist = Mathf.Sqrt(
                    (localPos.x - targetPos.x) * (localPos.x - targetPos.x) +
                    (localPos.z - targetPos.z) * (localPos.z - targetPos.z));
                float targetRadius = Mathf.Max(0f, target.HitRadius);
                float strictAllowedDistance = strikeRange + targetRadius;
                if (horizDist > strictAllowedDistance)
                {
                    ActionBarTrace.Trace(
                        gapClose != null
                            ? $"melee rejected: {slotId} gap close acquisition out of range dist={horizDist:F2} allowed={strictAllowedDistance:F2} range={strikeRange:F2} target_radius={targetRadius:F2}"
                            : $"melee rejected: {slotId} out of range dist={horizDist:F2} allowed={strictAllowedDistance:F2} range={strikeRange:F2} target_radius={targetRadius:F2}");
                    return false;
                }
                float minimumRange = Mathf.Max(0f, gameplay.MinimumRange);
                if (minimumRange > 0f)
                {
                    float strictMinimumDistance = minimumRange + targetRadius;
                    if (horizDist < strictMinimumDistance)
                    {
                        ActionBarTrace.Trace(
                            $"melee rejected: {slotId} inside minimum range dist={horizDist:F2} min={strictMinimumDistance:F2} minimum_range={minimumRange:F2} target_radius={targetRadius:F2}");
                        return false;
                    }
                }
            }

            // Send to server for authoritative validation, damage, and remote sync.
            ActionBarTrace.Trace(
                strikeChoice.shouldQueue
                    ? $"sending MeleeAttack queue request for {slotId} execute_at={strikeChoice.executeNotBeforeMs}"
                    : $"sending MeleeAttack for {slotId}");
            bool predictsLocalVisual = gapClose == null && !strikeChoice.shouldQueue;
            ActionPredictionToken token = predictsLocalVisual
                ? LocalCombatState.Instance.CreateActionPredictionToken(slotId)
                : ActionPredictionToken.None(slotId);
            conn.Reducers.MeleeAttack(
                slotId,
                requiresTarget ? target!.TargetIdentity.ToString() : string.Empty,
                localPos.x,
                localPos.y,
                localPos.z,
                entity.GameObject.transform.eulerAngles.y * Mathf.Deg2Rad,
                token.PredictedActionId,
                token.ClientActionSeq);

            if (gapClose != null)
            {
                ActionBarTrace.Trace(
                    $"melee gap close awaiting authoritative movement+animation: {slotId}");
                return true;
            }

            if (!strikeChoice.shouldQueue)
            {
                // Same predictions as before, but routed through the ledger so a
                // server Rejected/StaleToken restores all of them (feel audit F1).
                (string resourceKind, float resourceCost) =
                    ResolvePredictedResourceForMeleeAction(conn, entity, slotId);
                PredictedActionLedger ledger = combat.PredictActionStart(
                    entity,
                    slotId,
                    (long)gameplay.CooldownMs,
                    usesGlobalCooldown && !isComboFollowup,
                    GameplayTuning.ResolveDefaultGlobalCooldownDurationMs(conn),
                    resourceKind,
                    resourceCost,
                    nowMs);
                if (token.IsPredicted)
                    _predictionLedgersByToken[ActionTokenKey(token)] = (ledger, nowMs + PendingMeleePredictionTtlMs);
            }
            else
            {
                ReservePredictedResourceForMeleeAction(conn, entity, slotId, nowMs);
            }

            if (strikeChoice.shouldQueue)
            {
                _queuedStrikeId = slotId;
                _queuedStrikeExecuteAtMs = strikeChoice.executeNotBeforeMs;
                ActionBarTrace.Trace($"melee queued locally: {slotId}");
                return true;
            }

            TriggerLocalStrike(entity, slotId, nowMs, token);
            return true;
        }

        private static MeleeGapCloseCatalog? ResolveGapCloseForAction(
            DbConnection conn,
            SpacetimeDB.Identity owner,
            string combatProfile,
            string actionId)
        {
            string authoredActionId = CombatActionIds.ResolveAuthoredStrikeId(conn, combatProfile, actionId);
            ActiveActionBarAction direct =
                ActiveActionBarResolver.ResolveActiveSelectableActionForAction(conn, owner, authoredActionId);
            if (direct.HasAssignedAction)
            {
                MeleeGapCloseCatalog? directGapClose = conn.Db.MeleeGapCloseCatalog.AbilityId.Find(direct.AbilityId);
                if (directGapClose != null)
                    return directGapClose;
            }

            string rootAuthoredActionId = MeleeGameplayResolver.FindComboRootAuthored(conn, combatProfile, actionId);
            if (string.Equals(rootAuthoredActionId, authoredActionId, System.StringComparison.OrdinalIgnoreCase))
                return null;

            ActiveActionBarAction root =
                ActiveActionBarResolver.ResolveActiveSelectableActionForAction(conn, owner, rootAuthoredActionId);
            AbilityCatalog? rootAbility = string.IsNullOrWhiteSpace(root.AbilityId)
                ? null
                : conn.Db.AbilityCatalog.AbilityId.Find(root.AbilityId);
            if (rootAbility == null)
                return null;

            foreach (AbilityCatalog ability in conn.Db.AbilityCatalog.Iter())
            {
                if (!string.Equals(ability.AbilityKind, "MELEE", System.StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(
                    CombatProfileResolver.ResolveForAbility(conn, ability),
                    CombatProfileResolver.ResolveForAbility(conn, rootAbility),
                    System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!string.Equals(ability.ActionId, authoredActionId, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                MeleeGapCloseCatalog? gapClose = conn.Db.MeleeGapCloseCatalog.AbilityId.Find(ability.AbilityId);
                if (gapClose != null)
                    return gapClose;
            }

            return null;
        }

        private static bool IsTargetWithinFacingArc(PlayerEntity entity, ICombatTargetEntity target)
        {
            Vector3 delta = target.GetPresentationRoot().position - entity.GameObject.transform.position;
            delta.y = 0.0f;
            if (delta.sqrMagnitude <= 0.0001f)
                return true;

            Vector3 forward = entity.GameObject.transform.forward;
            forward.y = 0.0f;
            if (forward.sqrMagnitude <= 0.0001f)
                return true;

            float dot = Vector3.Dot(forward.normalized, delta.normalized);
            return dot >= 0.0f;
        }

        private bool HasResourceForMeleeAction(DbConnection conn, PlayerEntity entity, string actionId)
        {
            ActiveActionBarAction action = ActiveActionBarResolver.ResolveActiveSelectableActionForAction(
                conn,
                entity.Identity,
                actionId);
            if (!action.HasAssignedAction || action.ResourceCost <= 0.001f)
                return true;

            string requiredKind = string.IsNullOrWhiteSpace(action.ResourceKind)
                ? entity.PrimaryResourceKind
                : action.ResourceKind.Trim().ToUpperInvariant();
            if (!string.Equals(requiredKind, entity.PrimaryResourceKind, System.StringComparison.OrdinalIgnoreCase))
            {
                ActionBarTrace.Trace(
                    $"melee rejected: {actionId} requires {requiredKind}, active resource is {entity.PrimaryResourceKind}");
                return false;
            }

            float available = LocalCombatState.Instance.EffectiveCurrentPrimaryResource(entity, requiredKind);
            if (available + 0.001f < action.ResourceCost)
            {
                ActionBarTrace.Trace(
                    $"melee rejected: {actionId} requires {action.ResourceCost:F0} {requiredKind} ({available:F0} available)");
                return false;
            }

            return true;
        }

        private static (string kind, float cost) ResolvePredictedResourceForMeleeAction(
            DbConnection conn,
            PlayerEntity entity,
            string actionId)
        {
            ActiveActionBarAction action = ActiveActionBarResolver.ResolveActiveSelectableActionForAction(
                conn,
                entity.Identity,
                actionId);
            if (!action.HasAssignedAction || action.ResourceCost <= 0.001f)
                return (string.Empty, 0f);

            string resourceKind = string.IsNullOrWhiteSpace(action.ResourceKind)
                ? entity.PrimaryResourceKind
                : action.ResourceKind.Trim().ToUpperInvariant();
            return (resourceKind, action.ResourceCost);
        }

        private void ReservePredictedResourceForMeleeAction(DbConnection conn, PlayerEntity entity, string actionId, long nowMs)
        {
            (string resourceKind, float cost) = ResolvePredictedResourceForMeleeAction(conn, entity, actionId);
            if (cost <= 0.001f)
                return;

            LocalCombatState.Instance.ReservePredictedPrimaryResource(
                entity,
                resourceKind,
                cost,
                nowMs);
        }

        private void FlushQueuedLocalStrike(PlayerEntity entity, long nowMs)
        {
            if (string.IsNullOrEmpty(_queuedStrikeId) || nowMs < _queuedStrikeExecuteAtMs)
                return;

            TriggerLocalStrike(
                entity,
                _queuedStrikeId,
                _queuedStrikeExecuteAtMs,
                ActionPredictionToken.None(_queuedStrikeId));
            _queuedStrikeId = string.Empty;
            _queuedStrikeExecuteAtMs = 0L;
        }

        private void TriggerLocalStrike(
            PlayerEntity entity,
            string slotId,
            long startedAtMs,
            ActionPredictionToken token)
        {
            if (!entity.IsInCombat)
                entity.EnterCombatImmediate();
            string runtimeActionId = slotId;
            MeleeAttackModifierResolver.ActiveModifierIdentity activeModifier = default;
            var conn = NetworkManager.Instance?.Conn;
            if (conn != null)
            {
                string combatProfile = CombatProfileResolver.ResolveForEntity(conn, entity);
                runtimeActionId = CombatActionIds.ResolveRuntimeActionId(conn, combatProfile, slotId);
                activeModifier = MeleeAttackModifierResolver.ResolveActiveModifierIdentity(
                    conn,
                    entity.Identity,
                    startedAtMs);
            }

            entity.RequestCombatAnimation(
                CombatAnimationRequest.PredictedMeleeSkill(
                    slotId,
                    startedAtMs,
                    CombatEventSources.PlayerInput,
                    activeModifier.StatusKind,
                    activeModifier.StackGroup));
            if (token.IsPredicted)
                RememberPredictedMeleeVisual(token, slotId, runtimeActionId, startedAtMs);
            else
                RememberPredictedStrikeVisual(runtimeActionId, startedAtMs);
            ActionBarTrace.Trace($"local melee prediction triggered: {slotId}");

            _queuedStrikeId = string.Empty;
            _queuedStrikeExecuteAtMs = 0L;
            _lastStrikeId = slotId;
            _lastStrikeAtMs = startedAtMs;
        }

        public bool ConsumePredictedStrikeVisual(
            DbConnection conn,
            PlayerEntity entity,
            string actionId,
            long nowMs)
        {
            string combatProfile = CombatProfileResolver.ResolveForEntity(conn, entity);
            string runtimeActionId = CombatActionIds.ResolveRuntimeActionId(conn, combatProfile, actionId);
            if (string.IsNullOrWhiteSpace(runtimeActionId))
                return false;

            if (!_predictedStrikeVisualUntilMs.TryGetValue(runtimeActionId, out long untilMs))
                return false;

            _predictedStrikeVisualUntilMs.Remove(runtimeActionId);
            return nowMs <= untilMs;
        }

        public bool HandleAuthoritativeLocalMeleeReplay(
            DbConnection conn,
            PlayerEntity entity,
            string actionInstanceId,
            in CombatAnimationRequest request,
            long nowMs)
        {
            if (!string.IsNullOrWhiteSpace(actionInstanceId)
                && _acceptedPredictedMeleeByActionInstance.Remove(actionInstanceId))
            {
                return true;
            }

            string combatProfile = CombatProfileResolver.ResolveForEntity(conn, entity);
            string runtimeActionId = CombatActionIds.ResolveRuntimeActionId(conn, combatProfile, request.ActionId);
            if (!string.IsNullOrWhiteSpace(runtimeActionId)
                && HasPendingPredictedMeleeForRuntimeAction(runtimeActionId, nowMs))
            {
                _pendingAuthoritativeMeleeReplays.Add(new PendingAuthoritativeMeleeReplay(
                    actionInstanceId,
                    request,
                    nowMs + PendingLocalMeleeEventHoldMs));
                return true;
            }

            return ConsumePredictedStrikeVisual(conn, entity, request.ActionId, nowMs);
        }

        public void OnPredictedActionResultInsert(PredictedActionResult row)
        {
            if (row.Family != PredictedActionFamily.Melee)
                return;

            string tokenKey = ActionTokenKey(row.PredictedActionId, row.ClientActionSeq);
            if (row.Result == ActionResultKind.Accepted)
            {
                _predictionLedgersByToken.Remove(tokenKey);
                if (_pendingPredictedMeleeByToken.Remove(tokenKey)
                    && !string.IsNullOrWhiteSpace(row.ActionInstanceId))
                {
                    long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _acceptedPredictedMeleeByActionInstance[row.ActionInstanceId] =
                        new AcceptedPredictedMeleeAction(tokenKey, nowMs + PendingMeleePredictionTtlMs);
                    RemovePendingAuthoritativeReplay(row.ActionInstanceId);
                }
                return;
            }

            if (row.Result == ActionResultKind.Rejected || row.Result == ActionResultKind.StaleToken)
            {
                _pendingPredictedMeleeByToken.Remove(tokenKey);
                if (_predictionLedgersByToken.TryGetValue(tokenKey, out var pendingLedger))
                {
                    LocalCombatState.Instance.RollbackPrediction(pendingLedger.ledger, row.RejectReason);
                    ActionBarTrace.Trace(
                        $"rolled back predicted melee state for {pendingLedger.ledger.ActionKind} after {row.Result} reason={row.RejectReason}");
                    _predictionLedgersByToken.Remove(tokenKey);
                }
            }
        }

        public static void OnPredictedActionResultInsert(EventContext ctx, PredictedActionResult row)
        {
            _ = ctx;
            Instance?.OnPredictedActionResultInsert(row);
        }

        private void RememberPredictedStrikeVisual(string runtimeActionId, long nowMs)
        {
            if (string.IsNullOrWhiteSpace(runtimeActionId))
                return;

            _predictedStrikeVisualUntilMs[runtimeActionId] = nowMs + PredictedStrikeVisualRetentionMs;
        }

        private void RememberPredictedMeleeVisual(
            ActionPredictionToken token,
            string authoredActionId,
            string runtimeActionId,
            long startedAtMs)
        {
            if (!token.IsPredicted || string.IsNullOrWhiteSpace(runtimeActionId))
                return;

            _pendingPredictedMeleeByToken[ActionTokenKey(token)] = new PendingPredictedMeleeVisual(
                token,
                authoredActionId,
                runtimeActionId,
                startedAtMs,
                startedAtMs + PendingMeleePredictionTtlMs);
        }

        private bool HasPendingPredictedMeleeForRuntimeAction(string runtimeActionId, long nowMs)
        {
            foreach (PendingPredictedMeleeVisual pending in _pendingPredictedMeleeByToken.Values)
            {
                if (nowMs <= pending.ExpiresAtMs
                    && string.Equals(pending.RuntimeActionId, runtimeActionId, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private void FlushPendingAuthoritativeMeleeReplays(PlayerEntity entity, long nowMs)
        {
            for (int i = _pendingAuthoritativeMeleeReplays.Count - 1; i >= 0; i--)
            {
                PendingAuthoritativeMeleeReplay pending = _pendingAuthoritativeMeleeReplays[i];
                if (!string.IsNullOrWhiteSpace(pending.ActionInstanceId)
                    && _acceptedPredictedMeleeByActionInstance.Remove(pending.ActionInstanceId))
                {
                    _pendingAuthoritativeMeleeReplays.RemoveAt(i);
                    continue;
                }

                if (nowMs < pending.ReleaseAtMs)
                    continue;

                _pendingAuthoritativeMeleeReplays.RemoveAt(i);
                entity.RequestCombatAnimation(pending.Request);
            }
        }

        private void RemovePendingAuthoritativeReplay(string actionInstanceId)
        {
            if (string.IsNullOrWhiteSpace(actionInstanceId))
                return;

            for (int i = _pendingAuthoritativeMeleeReplays.Count - 1; i >= 0; i--)
            {
                if (string.Equals(
                    _pendingAuthoritativeMeleeReplays[i].ActionInstanceId,
                    actionInstanceId,
                    System.StringComparison.Ordinal))
                {
                    _pendingAuthoritativeMeleeReplays.RemoveAt(i);
                }
            }
        }

        private void PrunePendingMeleePredictionState(long nowMs)
        {
            var staleTokens = new List<string>();
            foreach (var entry in _pendingPredictedMeleeByToken)
            {
                if (nowMs > entry.Value.ExpiresAtMs)
                    staleTokens.Add(entry.Key);
            }
            foreach (string token in staleTokens)
                _pendingPredictedMeleeByToken.Remove(token);

            var staleActionInstances = new List<string>();
            foreach (var entry in _acceptedPredictedMeleeByActionInstance)
            {
                if (nowMs > entry.Value.ExpiresAtMs)
                    staleActionInstances.Add(entry.Key);
            }
            foreach (string actionInstanceId in staleActionInstances)
                _acceptedPredictedMeleeByActionInstance.Remove(actionInstanceId);

            var staleLedgerTokens = new List<string>();
            foreach (var entry in _predictionLedgersByToken)
            {
                if (nowMs > entry.Value.expiresAtMs)
                    staleLedgerTokens.Add(entry.Key);
            }
            foreach (string token in staleLedgerTokens)
                _predictionLedgersByToken.Remove(token);
        }

        private static string ActionTokenKey(ActionPredictionToken token)
            => ActionTokenKey(token.PredictedActionId, token.ClientActionSeq);

        private static string ActionTokenKey(string predictedActionId, ulong clientActionSeq)
            => $"{predictedActionId}:{clientActionSeq}";

        private (string lastStrikeId, long lastStrikeAtMs) GetEffectiveLastStrikeState(
            (string lastStrikeId, long lastStrikeAtMs) authoritative,
            long nowMs)
        {
            if (!string.IsNullOrEmpty(authoritative.lastStrikeId)
                && authoritative.lastStrikeAtMs >= _lastStrikeAtMs)
            {
                _lastStrikeId = authoritative.lastStrikeId;
                _lastStrikeAtMs = authoritative.lastStrikeAtMs;
                return authoritative;
            }

            if (!string.IsNullOrEmpty(_lastStrikeId)
                && nowMs <= _lastStrikeAtMs + PredictedStrikeStateRetentionMs)
            {
                return (_lastStrikeId, _lastStrikeAtMs);
            }

            if (!string.IsNullOrEmpty(_lastStrikeId))
            {
                _lastStrikeId = string.Empty;
                _lastStrikeAtMs = 0L;
            }

            return authoritative;
        }

        private static (string strikeId, bool shouldQueue, long executeNotBeforeMs) ResolveStrikeChoice(
            DbConnection conn, string combatProfile, string baseStrikeId,
            string lastStrikeId, long lastStrikeAtMs, long nowMs)
        {
            // Collapse combo-child bindings back to their root strike so follow-ups
            // can never be cold-cast. Then advance one step only while the authored
            // combo queue window for that successor is still valid.
            string current = FindComboRoot(conn, combatProfile, baseStrikeId);
            bool shouldQueue = false;
            long executeNotBeforeMs = nowMs;
            for (int safety = 0; safety < 16; safety++)
            {
                if (string.IsNullOrEmpty(lastStrikeId) || current != lastStrikeId)
                    return (current, shouldQueue, executeNotBeforeMs);
                var successor = FindSuccessor(conn, combatProfile, current);
                if (successor == null) return (current, shouldQueue, executeNotBeforeMs);
                long queueOpenAtMs = lastStrikeAtMs;
                long executeAtMs = lastStrikeAtMs + (long)successor.ComboOpenMs;
                long queueCloseAtMs = executeAtMs + (long)successor.ComboGraceMs;
                if (nowMs < queueOpenAtMs || nowMs > queueCloseAtMs)
                    return (current, shouldQueue, executeNotBeforeMs);
                current = successor.Kind;
                executeNotBeforeMs = executeAtMs;
                shouldQueue = nowMs < executeNotBeforeMs;
            }
            return (current, shouldQueue, executeNotBeforeMs);
        }

        private static (string lastStrikeId, long lastStrikeAtMs) GetAuthoritativeLastStrikeState(
            DbConnection conn,
            SpacetimeDB.Identity playerId)
        {
            var row = conn.Db.PlayerState.PlayerId.Find(playerId);
            if (row == null)
                return (string.Empty, 0L);

            return (
                row.LastStrikeId ?? string.Empty,
                row.LastStrikeAt.MicrosecondsSinceUnixEpoch / 1000L
            );
        }

        private static bool IsDirectlyBindableMeleeStrike(DbConnection conn, string combatProfile, string strikeId)
        {
            var def = GetStrikeDefinition(conn, combatProfile, strikeId);
            return def == null || string.IsNullOrWhiteSpace(def.ComboFrom);
        }

        private static string FindComboRoot(DbConnection conn, string combatProfile, string strikeId)
        {
            string current = strikeId;
            for (int safety = 0; safety < 16; safety++)
            {
                var def = GetStrikeDefinition(conn, combatProfile, current);
                if (def == null || string.IsNullOrWhiteSpace(def.ComboFrom))
                    return current;
                current = def.ComboFrom.Trim();
            }
            return strikeId;
        }

        private static MeleeDefinition? FindSuccessor(DbConnection conn, string combatProfile, string predecessorId)
        {
            string profile = CombatProfileIds.Normalize(combatProfile);
            foreach (var def in conn.Db.MeleeDefinition.Iter())
            {
                if (def.ComboFrom != predecessorId) continue;
                if (!string.Equals(CombatProfileIds.Normalize(def.CombatProfile), profile,
                        System.StringComparison.Ordinal)) continue;
                return def;
            }
            return null;
        }

        private static long StrikeTotalDurationMs(DbConnection conn, string combatProfile, string strikeId)
        {
            var def = GetStrikeDefinition(conn, combatProfile, strikeId);
            if (def == null)
                return 0L;
            return (long)def.ImpactDelayMs + (long)def.RecoveryMs;
        }

        private static bool IsComboFollowup(DbConnection conn, string combatProfile, string strikeId)
        {
            var def = GetStrikeDefinition(conn, combatProfile, strikeId);
            return def != null && !string.IsNullOrWhiteSpace(def.ComboFrom);
        }

        private static bool RequiresTarget(MeleeAbilityCatalog gameplay)
        {
            return string.IsNullOrWhiteSpace(gameplay.TargetingKind)
                || string.Equals(gameplay.TargetingKind, "TARGET", System.StringComparison.OrdinalIgnoreCase)
                || gameplay.RequiresTarget;
        }

        private static string TraceAudience(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? PartyRelationship.TargetAudienceHostile
                : value.Trim().ToUpperInvariant();

        private static MeleeDefinition? GetStrikeDefinition(DbConnection conn, string combatProfile, string strikeId)
        {
            return CombatActionIds.FindMeleeDefinition(conn, combatProfile, strikeId);
        }
    }
}

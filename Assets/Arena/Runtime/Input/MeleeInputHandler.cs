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
        private const string ConditionalTeleportBehindKind = "TELEPORT_BEHIND_TARGET_DISABLED";
        private const string CoupDeGraceAbilityId = "DAGGER_COUP_DE_GRACE";
        // Gap-close camera arm: set at press, consumed when the runtime row
        // lands. The TTL only bounds a press the server silently dropped.
        private const long GapCloseCameraAlignTtlMs = 1500L;
        private static bool _gapCloseCameraAlignArmed;
        private static long _gapCloseCameraAlignExpiresAtMs;
        private readonly Dictionary<string, long> _predictedStrikeVisualUntilMs = new();
        private readonly Dictionary<string, PendingPredictedMeleeVisual> _pendingPredictedMeleeByToken = new();
        private readonly Dictionary<string, AcceptedPredictedMeleeAction> _acceptedPredictedMeleeByActionInstance = new();
        private readonly List<PendingAuthoritativeMeleeReplay> _pendingAuthoritativeMeleeReplays = new();
        private readonly Dictionary<string, (PredictedActionLedger ledger, long expiresAtMs)> _predictionLedgersByToken = new();
        // Predicted gap-close windups (feel audit F5): tokens whose special-
        // movement-driven windup presentation must be unwound if the server
        // rejects the press or never answers. Accepted presses leave the row
        // lifecycle (SpecialMovementRuntime delete) to end the windup.
        private readonly Dictionary<string, long> _pendingGapCloseWindupExpiryByToken = new();

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
            LocalCombatState.Instance.ReconcilePredictedResources(entity);
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
            string pressedActionId = slotId;
            if (entity.IsDestroyed || !entity.IsAlive)
            {
                return RejectLocalMeleeAction(
                    slotId,
                    pressedActionId,
                    ActionRejectReason.Dead,
                    $"melee rejected: {slotId} has no live local entity");
            }

            string combatProfile = RuntimeCombatProfile.ResolveForEntity(conn, entity);
            MeleeDefinition? pressedDefinition = GetStrikeDefinition(conn, combatProfile, slotId);
            ActionBarTrace.Diagnostic(
                $"melee evaluate pressed={slotId} profile={combatProfile} " +
                $"definition={(pressedDefinition == null ? "<missing>" : $"{pressedDefinition.Key}|comboFrom={pressedDefinition.ComboFrom}")}");

            if (!IsDirectlyBindableMeleeStrike(conn, combatProfile, slotId))
            {
                return RejectLocalMeleeAction(
                    slotId,
                    pressedActionId,
                    ActionRejectReason.InvalidInput,
                    $"melee rejected: {slotId} is not a bindable opener for {combatProfile}");
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
            ActionBarTrace.Diagnostic(
                $"melee choice pressed={pressedActionId} resolved={strikeChoice.strikeId} " +
                $"queue={strikeChoice.shouldQueue} authoritativeLast={authorityState.lastStrikeId} " +
                $"effectiveLast={effectiveLastStrikeState.lastStrikeId}");
            // The bar slot keeps showing the pressed opener even when the
            // strike choice resolves a combo follow-up; the denial flash
            // (netcode design review S2) must target the pressed id.
            slotId = strikeChoice.strikeId;
            MeleeAbilityCatalog? gameplay =
                MeleeGameplayResolver.ResolveForAction(conn, entity.Identity, combatProfile, slotId);
            if (gameplay == null)
            {
                return RejectLocalMeleeAction(
                    slotId,
                    pressedActionId,
                    ActionRejectReason.InvalidInput,
                    $"melee rejected: {slotId} has no melee gameplay row");
            }
            MeleeGapCloseCatalog? configuredGapClose =
                ResolveGapCloseForAction(conn, entity.Identity, combatProfile, slotId);
            MeleeGapCloseCatalog? gapClose = configuredGapClose;
            bool scalesGapClosePhasesFromImpactReach =
                configuredGapClose?.ActivateOutsideImpactReach == true;
            float gapCloseLoopScale = 1f;
            if (!HasResourceForMeleeAction(conn, entity, slotId, pressedActionId))
                return false;

            bool requiresTarget = RequiresTarget(gameplay);
            bool automaticallyFacesTarget = AutomaticallyFacesTarget(gameplay.AbilityId);

            ICombatTargetEntity? target = null;
            if (requiresTarget)
            {
                target = TargetSelector.Instance?.SelectedTarget;
                if (target == null || target.IsDestroyed || !target.IsAlive)
                {
                    return RejectLocalMeleeAction(
                        slotId,
                        pressedActionId,
                        ActionRejectReason.InvalidTarget,
                        $"melee rejected: {slotId} requires a live target");
                }
                if (!automaticallyFacesTarget && !IsTargetWithinFacingArc(entity, target))
                {
                    return RejectLocalMeleeAction(
                        slotId,
                        pressedActionId,
                        ActionRejectReason.NotFacingTarget,
                        $"melee rejected: {slotId} requires target facing");
                }
                if (!PartyRelationship.TargetAudienceAllowsLocal(target, gameplay.TargetAudience))
                {
                    var relation = PartyRelationship.RelationToLocal(target);
                    return RejectLocalMeleeAction(
                        slotId,
                        pressedActionId,
                        ActionRejectReason.InvalidTarget,
                        $"melee rejected: {slotId} target audience {TraceAudience(gameplay.TargetAudience)} rejects relation={relation} target={target.DisplayName}");
                }
                if (configuredGapClose != null
                    && !GapCloseActivationSatisfied(
                        conn,
                        entity,
                        configuredGapClose,
                        target,
                        nowMs,
                        out gapCloseLoopScale))
                {
                    gapClose = null;
                    ActionBarTrace.Trace(
                        $"melee conditional gap close inactive: {slotId} kind={configuredGapClose.Kind}");
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
                    return RejectLocalMeleeAction(
                        slotId,
                        pressedActionId,
                        ActionRejectReason.OnGlobalCooldown,
                        $"melee rejected: {slotId} blocked by GCD");
                }
            }

            // Per-strike cooldown check (melee cooldowns share the SpellCooldown table,
            // now keyed by canonical combat-style slot id.
            if (combat.SpellCooldowns.TryGetValue(slotId, out var cd))
            {
                if (nowMs < cd.lastCastMs + cd.durationMs)
                {
                    return RejectLocalMeleeAction(
                        slotId,
                        pressedActionId,
                        ActionRejectReason.OnCooldown,
                        $"melee rejected: {slotId} blocked by cooldown");
                }
            }

            // Range check — horizontal distance against the server-synced
            // definition, via the geometry the predicted contact cue's
            // advisory test replays at the first hit window (feel audit F5).
            var localPos = entity.GameObject.transform.position;
            float strikeRange = 0f;
            float minimumRange = 0f;
            float targetHorizontalDistance = 0f;
            float targetHitRadius = 0f;
            if (requiresTarget)
            {
                strikeRange = MeleeAttackModifierResolver.ResolveEffectiveRange(
                    conn,
                    entity.Identity,
                    gameplay.Range,
                    nowMs,
                    includeRangeBonus: configuredGapClose == null);
                if (configuredGapClose != null && gapClose == null)
                    strikeRange = Mathf.Min(strikeRange, Mathf.Max(0f, configuredGapClose.ImpactRange));
                var targetPos = target!.GetPresentationRoot().position;
                float horizDist = MeleeStrikeGeometry.HorizontalDistance(localPos, targetPos);
                float targetRadius = Mathf.Max(0f, target.HitRadius);
                targetHorizontalDistance = horizDist;
                targetHitRadius = targetRadius;
                float strictAllowedDistance = MeleeStrikeGeometry.MaximumContactDistance(strikeRange, targetRadius);
                if (horizDist > strictAllowedDistance)
                {
                    string trace = gapClose != null
                        ? $"melee rejected: {slotId} gap close acquisition out of range dist={horizDist:F2} allowed={strictAllowedDistance:F2} range={strikeRange:F2} target_radius={targetRadius:F2}"
                        : $"melee rejected: {slotId} out of range dist={horizDist:F2} allowed={strictAllowedDistance:F2} range={strikeRange:F2} target_radius={targetRadius:F2}";
                    return RejectLocalMeleeAction(
                        slotId,
                        pressedActionId,
                        ActionRejectReason.OutOfRange,
                        trace,
                        notifyUser: gapClose != null);
                }
                minimumRange = Mathf.Max(0f, gameplay.MinimumRange);
                if (minimumRange > 0f)
                {
                    float strictMinimumDistance = MeleeStrikeGeometry.MinimumContactDistance(minimumRange, targetRadius);
                    if (horizDist < strictMinimumDistance)
                    {
                        return RejectLocalMeleeAction(
                            slotId,
                            pressedActionId,
                            ActionRejectReason.OutOfRange,
                            $"melee rejected: {slotId} inside minimum range dist={horizDist:F2} min={strictMinimumDistance:F2} minimum_range={minimumRange:F2} target_radius={targetRadius:F2}");
                    }
                }

                // Advisory LOS pre-check (netcode design review S4): deny the
                // press instantly with the server's reason text instead of a
                // round trip. Permissive by construction — a press this check
                // lets through still gets the authoritative server check.
                if (gameplay.RequiresTargetLos
                    && AdvisoryTargetLineOfSight.IsPressBlocked(entity, target!))
                {
                    return RejectLocalMeleeAction(
                        slotId,
                        pressedActionId,
                        ActionRejectReason.LineOfSightBlocked,
                        $"melee denied locally: {slotId} advisory line of sight blocked",
                        notifyUser: true);
                }
            }

            if (requiresTarget && automaticallyFacesTarget)
            {
                if (gapClose == null)
                {
                    // No authored movement is coming, so nothing else will ever
                    // point the caster at the target. Turn now.
                    FaceTargetImmediately(entity, target!);
                }

                // Facing and camera are both left to the arrival when a
                // behind-target teleport is coming: it lands the caster facing
                // roughly OPPOSITE the press-time approach direction, so a
                // press-time snap aims at the wrong yaw and then fights the
                // arrival. Only a press that was actually used as a gap closer
                // arms the camera — a point-blank execute still repositions
                // behind a disabled target, but leaves the camera exactly where
                // the player put it.
                ArmGapCloseCameraAlign(
                    gapClose != null
                        && MeleeStrikeGeometry.ShouldActivateGapCloseOutsideImpactReach(
                            targetHorizontalDistance,
                            gapClose.ImpactRange,
                            targetHitRadius),
                    nowMs);
            }

            // Send to server for authoritative validation, damage, and remote sync.
            bool predictsLocalVisual = !strikeChoice.shouldQueue;
            ActionPredictionToken token = predictsLocalVisual
                ? LocalCombatState.Instance.CreateActionPredictionToken(slotId)
                : ActionPredictionToken.None(slotId);
            ActionBarTrace.Diagnostic(
                $"sending MeleeAttack action={slotId} profile={combatProfile} " +
                $"target={(requiresTarget ? target!.TargetIdentity.ToString() : "<none>")} " +
                $"queue={strikeChoice.shouldQueue} executeAt={strikeChoice.executeNotBeforeMs} " +
                $"prediction={token.PredictedActionId}:{token.ClientActionSeq}");
            conn.Reducers.MeleeAttack(
                slotId,
                requiresTarget ? target!.TargetIdentity.ToString() : string.Empty,
                localPos.x,
                localPos.y,
                localPos.z,
                entity.GameObject.transform.eulerAngles.y * Mathf.Deg2Rad,
                token.PredictedActionId,
                token.ClientActionSeq,
                // S8 targeted report; S10 (G2): a no-target caster-cone/radius
                // sweep (e.g. WARRIOR_CATACLYSM) reports the shared per-connection
                // delay so its area membership rewinds like a spell sweep.
                requiresTarget
                    ? AttackerViewTime.ViewServerTimeMsFor(target)
                    : AttackerViewTime.ViewServerTimeMsForConnection());

            if (gapClose != null && strikeChoice.shouldQueue)
            {
                // Queued combo follow-ups keep the authoritative-only flow; the
                // predicted windup ships for direct presses first (feel audit
                // F5, slice 1).
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
                    nowMs).WithPressedActionId(pressedActionId);
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

            TriggerLocalStrike(
                entity,
                slotId,
                nowMs,
                token,
                predictedGapCloseWindup: gapClose != null,
                scaleGapClosePhasesFromImpactReach: scalesGapClosePhasesFromImpactReach,
                gapCloseLoopScale: gapCloseLoopScale);
            if (token.IsPredicted && configuredGapClose == null && requiresTarget)
            {
                // Predicted contact cue (feel audit F5 slice 2): schedule the
                // advisory hit test at the authored first hit window.
                // Gap-close presses are excluded — their contact moment
                // depends on the server-owned dash.
                PredictedMeleeContactCueController.OnPredictedLocalStrike(
                    conn,
                    entity,
                    target!,
                    token,
                    combatProfile,
                    slotId,
                    strikeRange,
                    minimumRange,
                    nowMs);
            }
            return true;
        }

        private static bool RejectLocalMeleeAction(
            string actionId,
            string pressedActionId,
            ActionRejectReason reason,
            string trace,
            bool notifyUser = false)
        {
            ActionBarTrace.Diagnostic(
                $"local melee rejection reason={reason} action={actionId} pressed={pressedActionId}: {trace}");
            if (notifyUser)
            {
                LocalCombatState.NotifyLocalAdvisoryDenial(
                    actionId,
                    pressedActionId,
                    reason);
            }
            return false;
        }

        private static void FaceTargetImmediately(
            PlayerEntity entity,
            ICombatTargetEntity target)
        {
            Vector3 toTarget =
                target.GetPresentationRoot().position - entity.GameObject.transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude <= 0.0001f)
                return;

            float targetYaw = Mathf.Atan2(toTarget.x, toTarget.z);
            LocalMovementPredictionDriver? predictionDriver =
                entity.GameObject.GetComponent<LocalMovementPredictionDriver>();
            if (predictionDriver != null)
            {
                predictionDriver.FaceYawImmediately(targetYaw);
            }
            else
            {
                entity.GameObject.GetComponent<LocalPlayerMotor>()?.FaceYawImmediately(targetYaw);
                entity.GameObject.transform.rotation =
                    Quaternion.Euler(0f, targetYaw * Mathf.Rad2Deg, 0f);
            }
        }

        /// <summary>
        /// Records whether the press that is about to open a gap-close runtime
        /// was used as a gap closer. EntityRegistry consumes this when the
        /// runtime row lands, so the camera follows the server's arrival facing
        /// only for a real gap close. Every qualifying press writes the flag —
        /// including a false — so a point-blank press immediately disarms a
        /// previous arm rather than inheriting it.
        /// </summary>
        private static void ArmGapCloseCameraAlign(bool armed, long nowMs)
        {
            _gapCloseCameraAlignArmed = armed;
            _gapCloseCameraAlignExpiresAtMs = armed ? nowMs + GapCloseCameraAlignTtlMs : 0L;
        }

        public static bool ConsumeGapCloseCameraAlign(long nowMs)
        {
            bool armed = _gapCloseCameraAlignArmed && nowMs < _gapCloseCameraAlignExpiresAtMs;
            _gapCloseCameraAlignArmed = false;
            _gapCloseCameraAlignExpiresAtMs = 0L;
            return armed;
        }

        private static bool AutomaticallyFacesTarget(string abilityId)
        {
            return string.Equals(
                WireIdentifier.Normalize(abilityId),
                CoupDeGraceAbilityId,
                System.StringComparison.Ordinal);
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
                    RuntimeCombatProfile.ResolveForAbility(conn, ability),
                    RuntimeCombatProfile.ResolveForAbility(conn, rootAbility),
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

        private static bool GapCloseActivationSatisfied(
            DbConnection conn,
            PlayerEntity entity,
            MeleeGapCloseCatalog gapClose,
            ICombatTargetEntity target,
            long nowMs,
            out float loopScale)
        {
            loopScale = 1f;
            string kind = WireIdentifier.Normalize(gapClose.Kind);
            if (string.Equals(
                    kind,
                    ConditionalTeleportBehindKind,
                    System.StringComparison.Ordinal)
                && !HasActiveDisablingStatus(conn, target.TargetIdentity, nowMs))
            {
                return false;
            }

            if (!gapClose.ActivateOutsideImpactReach)
                return true;

            float horizontalDistance = MeleeStrikeGeometry.HorizontalDistance(
                entity.GameObject.transform.position,
                target.GetPresentationRoot().position);
            loopScale = MeleeStrikeGeometry.ImpactReachLoopScale(
                horizontalDistance,
                gapClose.ImpactRange,
                target.HitRadius);
            return MeleeStrikeGeometry.ShouldActivateGapCloseOutsideImpactReach(
                horizontalDistance,
                gapClose.ImpactRange,
                target.HitRadius);
        }

        private static bool HasActiveDisablingStatus(
            DbConnection conn,
            SpacetimeDB.Identity target,
            long nowMs)
        {
            long nowMicros = nowMs * 1000L;
            foreach (StatusEffect effect in conn.Db.StatusEffect.Target.Filter(target))
            {
                if (effect.ExpiresAtMicros > 0L && effect.ExpiresAtMicros <= nowMicros)
                    continue;

                switch (WireIdentifier.Normalize(effect.EffectKind))
                {
                    case "STUN":
                    case "FREEZE":
                    case "INTIMIDATED":
                    case "FEAR":
                    case "CONFUSION":
                    case "STAGGER":
                    case "KNOCKDOWN":
                        return true;
                }
            }

            return false;
        }

        private static bool IsTargetWithinFacingArc(PlayerEntity entity, ICombatTargetEntity target)
        {
            return MeleeStrikeGeometry.IsWithinFacingArc(
                entity.GameObject.transform.forward,
                entity.GameObject.transform.position,
                target.GetPresentationRoot().position);
        }

        private bool HasResourceForMeleeAction(
            DbConnection conn,
            PlayerEntity entity,
            string actionId,
            string pressedActionId)
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

            float available = LocalCombatState.Instance.EffectiveCurrentResource(entity, requiredKind);
            if (available + 0.001f < action.ResourceCost)
            {
                return RejectLocalMeleeAction(
                    actionId,
                    pressedActionId,
                    ActionRejectReason.InsufficientResource,
                    $"melee rejected: {actionId} requires {action.ResourceCost:F0} {requiredKind} ({available:F0} available)",
                    notifyUser: true);
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

            LocalCombatState.Instance.ReservePredictedResource(
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
            ActionPredictionToken token,
            bool predictedGapCloseWindup = false,
            bool scaleGapClosePhasesFromImpactReach = false,
            float gapCloseLoopScale = 1f)
        {
            // Gap-close windups skip the combat-stance snap: movement-driven
            // phased playback owns stance flags so the base layer never enters
            // a combat transition ahead of the phased set (feel audit F5).
            if (!predictedGapCloseWindup && !entity.IsInCombat)
                entity.EnterCombatImmediate();
            string runtimeActionId = slotId;
            MeleeAttackModifierResolver.ActiveModifierIdentity activeModifier = default;
            var conn = NetworkManager.Instance?.Conn;
            if (conn != null)
            {
                string combatProfile = RuntimeCombatProfile.ResolveForEntity(conn, entity);
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
                    activeModifier.StackGroup,
                    drivePhasesFromSpecialMovement: predictedGapCloseWindup,
                    scaleGapClosePhasesFromImpactReach: scaleGapClosePhasesFromImpactReach,
                    gapCloseUsedMovementAtCast: predictedGapCloseWindup,
                    gapCloseLoopScale: gapCloseLoopScale));
            if (token.IsPredicted)
                RememberPredictedMeleeVisual(token, slotId, runtimeActionId, startedAtMs);
            else
                RememberPredictedStrikeVisual(runtimeActionId, startedAtMs);
            if (predictedGapCloseWindup && token.IsPredicted)
                _pendingGapCloseWindupExpiryByToken[ActionTokenKey(token)] =
                    startedAtMs + PendingMeleePredictionTtlMs;
            ActionBarTrace.Trace(
                predictedGapCloseWindup
                    ? $"local melee gap close windup prediction triggered: {slotId}"
                    : $"local melee prediction triggered: {slotId}");

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
            string combatProfile = RuntimeCombatProfile.ResolveForEntity(conn, entity);
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

            string combatProfile = RuntimeCombatProfile.ResolveForEntity(conn, entity);
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

            ActionBarTrace.Diagnostic(
                $"MeleeAttack result={row.Result} reason={row.RejectReason} " +
                $"prediction={row.PredictedActionId}:{row.ClientActionSeq} " +
                $"actionInstance={row.ActionInstanceId}");

            // Predicted contact cue correlation shares this subscription
            // (feel audit F5 slice 2).
            PredictedMeleeContactCueController.OnPredictedActionResult(row);

            string tokenKey = ActionTokenKey(row.PredictedActionId, row.ClientActionSeq);
            if (row.Result == ActionResultKind.Accepted)
            {
                _predictionLedgersByToken.Remove(tokenKey);
                // Accepted gap-close: the SpecialMovementRuntime row delete now
                // owns the windup's end request.
                _pendingGapCloseWindupExpiryByToken.Remove(tokenKey);
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
                bool hadPendingVisual = _pendingPredictedMeleeByToken.TryGetValue(tokenKey, out var pendingVisual);
                _pendingPredictedMeleeByToken.Remove(tokenKey);
                if (_predictionLedgersByToken.TryGetValue(tokenKey, out var pendingLedger))
                {
                    LocalCombatState.Instance.RollbackPrediction(pendingLedger.ledger, row.RejectReason);
                    ActionBarTrace.Trace(
                        $"rolled back predicted melee state for {pendingLedger.ledger.ActionKind} after {row.Result} reason={row.RejectReason}");
                    _predictionLedgersByToken.Remove(tokenKey);
                }

                bool hadGapCloseWindup = _pendingGapCloseWindupExpiryByToken.Remove(tokenKey);

                // Reject = interrupt, never completion (netcode design review
                // S2): cut the predicted swing/windup via the shared preemption
                // primitives instead of letting it play through (or, for a
                // gap close, forcing the completed-looking end segment).
                // StaleToken keeps the old resolution paths — a newer press of
                // the same action owns the presentation, so a scoped cut could
                // eat it (same exclusion the spell cast state machine uses).
                if (row.Result == ActionResultKind.Rejected && hadPendingVisual)
                {
                    EntityRegistry.Instance?.LocalPlayerEntity?.CutRejectedActionPresentation(
                        pendingVisual.AuthoredActionId);
                    ActionBarTrace.Trace(
                        $"cut rejected predicted melee presentation for {pendingVisual.AuthoredActionId} reason={row.RejectReason}");
                }
                else if (hadGapCloseWindup)
                {
                    EntityRegistry.Instance?.LocalPlayerEntity?.RollbackPredictedGapCloseWindup();
                    ActionBarTrace.Trace(
                        $"rolled back predicted gap close windup after {row.Result} reason={row.RejectReason}");
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

            // A gap-close windup with no server answer at all must not hold the
            // loop segment forever; the rollback is a no-op when a live special
            // movement already owns the end request.
            var staleGapCloseTokens = new List<string>();
            foreach (var entry in _pendingGapCloseWindupExpiryByToken)
            {
                if (nowMs > entry.Value)
                    staleGapCloseTokens.Add(entry.Key);
            }
            foreach (string token in staleGapCloseTokens)
            {
                _pendingGapCloseWindupExpiryByToken.Remove(token);
                EntityRegistry.Instance?.LocalPlayerEntity?.RollbackPredictedGapCloseWindup();
                ActionBarTrace.Trace("rolled back predicted gap close windup after prediction timeout");
            }
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

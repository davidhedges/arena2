#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Entity;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Arena.Simulation
{
    public readonly struct FixedActionChargeSnapshot
    {
        public readonly uint CurrentCharges;
        public readonly uint MaxCharges;
        public readonly long RechargeDurationMs;
        public readonly bool IsRecharging;
        public readonly long RechargeStartedMs;
        public readonly long NextChargeReadyMs;

        public FixedActionChargeSnapshot(
            uint currentCharges,
            uint maxCharges,
            long rechargeDurationMs,
            bool isRecharging,
            long rechargeStartedMs,
            long nextChargeReadyMs)
        {
            CurrentCharges = currentCharges;
            MaxCharges = maxCharges;
            RechargeDurationMs = rechargeDurationMs;
            IsRecharging = isRecharging;
            RechargeStartedMs = rechargeStartedMs;
            NextChargeReadyMs = nextChargeReadyMs;
        }
    }

    public readonly struct CastBarSnapshot
    {
        public CastBarSnapshot(long startMs, long endMs, string kind)
        {
            StartMs = startMs;
            EndMs = endMs;
            Kind = kind ?? string.Empty;
        }

        public long StartMs { get; }
        public long EndMs { get; }
        public string Kind { get; }
    }

    public struct PredictedResourceSpend
    {
        public float Spend;
        public long ExpiresAtMs;
        public float LastObserved;
    }

    public readonly struct CastActionToken
    {
        public CastActionToken(string predictedCastId, ulong clientActionSeq, string kind)
        {
            PredictedCastId = predictedCastId ?? string.Empty;
            ClientActionSeq = clientActionSeq;
            Kind = kind ?? string.Empty;
        }

        public string PredictedCastId { get; }
        public ulong ClientActionSeq { get; }
        public string Kind { get; }
        public bool IsPredicted => !string.IsNullOrWhiteSpace(PredictedCastId);

        public static CastActionToken Legacy(string kind) => new(string.Empty, 0UL, kind);
    }

    public readonly struct ActionPredictionToken
    {
        public ActionPredictionToken(string predictedActionId, ulong clientActionSeq, string kind)
        {
            PredictedActionId = predictedActionId ?? string.Empty;
            ClientActionSeq = clientActionSeq;
            Kind = kind ?? string.Empty;
        }

        public string PredictedActionId { get; }
        public ulong ClientActionSeq { get; }
        public string Kind { get; }
        public bool IsPredicted => !string.IsNullOrWhiteSpace(PredictedActionId);

        public static ActionPredictionToken None(string kind) => new(string.Empty, 0UL, kind);
    }

    public readonly struct CastCancelSnapshot
    {
        public CastCancelSnapshot(CastActionToken token, ulong observedRemainingMs)
        {
            Token = token;
            ObservedRemainingMs = observedRemainingMs;
        }

        public CastActionToken Token { get; }
        public ulong ObservedRemainingMs { get; }
    }

    /// <summary>
    /// Everything a single predicted action press wrote into prediction-layer
    /// state, captured so a server Rejected/StaleToken can restore all of it
    /// (feel audit F1). Never references authoritative rows.
    /// </summary>
    public readonly struct PredictedActionLedger
    {
        public PredictedActionLedger(
            string actionKind,
            bool gcdPredicted,
            long priorGcdStartMs,
            long priorGcdDurationMs,
            long predictedGcdStartMs,
            long predictedGcdDurationMs,
            bool cooldownPredicted,
            bool hadPriorCooldown,
            long priorCooldownLastCastMs,
            long priorCooldownDurationMs,
            long predictedCooldownLastCastMs,
            long predictedCooldownDurationMs,
            string reservedResourceKind,
            float reservedResourceCost)
        {
            ActionKind = actionKind ?? string.Empty;
            GcdPredicted = gcdPredicted;
            PriorGcdStartMs = priorGcdStartMs;
            PriorGcdDurationMs = priorGcdDurationMs;
            PredictedGcdStartMs = predictedGcdStartMs;
            PredictedGcdDurationMs = predictedGcdDurationMs;
            CooldownPredicted = cooldownPredicted;
            HadPriorCooldown = hadPriorCooldown;
            PriorCooldownLastCastMs = priorCooldownLastCastMs;
            PriorCooldownDurationMs = priorCooldownDurationMs;
            PredictedCooldownLastCastMs = predictedCooldownLastCastMs;
            PredictedCooldownDurationMs = predictedCooldownDurationMs;
            ReservedResourceKind = reservedResourceKind ?? string.Empty;
            ReservedResourceCost = reservedResourceCost;
            PressedActionId = ActionKind;
        }

        private PredictedActionLedger(in PredictedActionLedger source, string pressedActionId)
        {
            ActionKind = source.ActionKind;
            GcdPredicted = source.GcdPredicted;
            PriorGcdStartMs = source.PriorGcdStartMs;
            PriorGcdDurationMs = source.PriorGcdDurationMs;
            PredictedGcdStartMs = source.PredictedGcdStartMs;
            PredictedGcdDurationMs = source.PredictedGcdDurationMs;
            CooldownPredicted = source.CooldownPredicted;
            HadPriorCooldown = source.HadPriorCooldown;
            PriorCooldownLastCastMs = source.PriorCooldownLastCastMs;
            PriorCooldownDurationMs = source.PriorCooldownDurationMs;
            PredictedCooldownLastCastMs = source.PredictedCooldownLastCastMs;
            PredictedCooldownDurationMs = source.PredictedCooldownDurationMs;
            ReservedResourceKind = source.ReservedResourceKind;
            ReservedResourceCost = source.ReservedResourceCost;
            PressedActionId = string.IsNullOrWhiteSpace(pressedActionId) ? source.ActionKind : pressedActionId;
        }

        /// <summary>
        /// The action id as pressed on the action bar. Differs from
        /// <see cref="ActionKind"/> only when press-time resolution swapped the
        /// action (melee combo follow-ups resolve to the follow-up strike id,
        /// while the bar slot shows the opener). This is the id HUD surfaces
        /// should match slots against for the denial flash (netcode design
        /// review S2).
        /// </summary>
        public string PressedActionId { get; }

        public PredictedActionLedger WithPressedActionId(string pressedActionId)
            => new(this, pressedActionId);

        public string ActionKind { get; }
        public bool GcdPredicted { get; }
        public long PriorGcdStartMs { get; }
        public long PriorGcdDurationMs { get; }
        public long PredictedGcdStartMs { get; }
        public long PredictedGcdDurationMs { get; }
        public bool CooldownPredicted { get; }
        public bool HadPriorCooldown { get; }
        public long PriorCooldownLastCastMs { get; }
        public long PriorCooldownDurationMs { get; }
        public long PredictedCooldownLastCastMs { get; }
        public long PredictedCooldownDurationMs { get; }
        public string ReservedResourceKind { get; }
        public float ReservedResourceCost { get; }
    }

    /// <summary>
    /// Simulation-layer cache of the local player's combat state:
    /// GCD, per-spell cooldowns, authoritative active cast, and cast-bar prediction.
    ///
    /// Populated by NetworkManager table callbacks and local action predictions.
    /// Read by input gates and HUD presentation.
    /// </summary>
    public class LocalCombatState : ITimedActionPresentationSource
    {
        public static LocalCombatState Instance { get; } = new();
        public long GcdStartMs { get; private set; }
        public long GcdDurationMs { get; private set; }
        public long GcdEndMs => GcdStartMs + GcdDurationMs;

        private const long PredictedResourceSpendTimeoutMs = 1250L;
        private readonly Dictionary<string, (long lastCastMs, long durationMs)> _spellCds = new();
        private readonly Dictionary<string, FixedActionChargeSnapshot> _fixedActionCharges = new();
        // Both replicated and predicted per-action cooldowns use server UTC milliseconds.
        public IReadOnlyDictionary<string, (long lastCastMs, long durationMs)> SpellCooldowns => _spellCds;
        public IReadOnlyDictionary<string, FixedActionChargeSnapshot> FixedActionCharges => _fixedActionCharges;

        public (long startMs, long endMs, string kind, string targetId, float aimX, float aimY, float aimZ)? ActiveCast { get; private set; }
        public (long startMs, long activeUntilMs, long recoveryUntilMs, string kind)? MovementAction { get; private set; }

        private Identity _localId;
        private bool _bound;
        private CastBarSnapshot? _predictedCastBar;
        private CastActionToken? _currentCastToken;
        private ulong _nextClientActionSeq = 1UL;
        private string _activeCastPredictedCastId = string.Empty;
        private ulong _activeCastClientActionSeq;
        private string _locallySuppressedCastBarKind = string.Empty;
        private readonly Dictionary<string, PredictedResourceSpend> _predictedResourceSpends = new();

        public void Bind(Identity localId)
        {
            _localId = localId;
            _bound = true;
        }

        internal void ResetForNetworkReconnect()
        {
            Reset();
        }

        internal void ResetForTests()
        {
            Reset();
        }

        private void Reset()
        {
            GcdStartMs = 0L;
            GcdDurationMs = 0L;
            _spellCds.Clear();
            _fixedActionCharges.Clear();
            ActiveCast = null;
            MovementAction = null;
            _predictedCastBar = null;
            _currentCastToken = null;
            _nextClientActionSeq = 1UL;
            _activeCastPredictedCastId = string.Empty;
            _activeCastClientActionSeq = 0UL;
            _locallySuppressedCastBarKind = string.Empty;
            _predictedResourceSpends.Clear();
            _localId = default;
            _bound = false;
        }

        // ---------------------------------------------------------------
        // GlobalCooldown callbacks (wired by NetworkManager)
        // ---------------------------------------------------------------

        public void OnGlobalCooldownInsert(EventContext ctx, GlobalCooldown row)
        {
            if (_bound && row.Caster == _localId)
            {
                GcdStartMs = row.StartedAt.MicrosecondsSinceUnixEpoch / 1000L;
                GcdDurationMs = (long)row.DurationMs;
            }
        }

        public void OnGlobalCooldownUpdate(EventContext ctx, GlobalCooldown oldRow, GlobalCooldown newRow)
        {
            if (_bound && newRow.Caster == _localId)
            {
                GcdStartMs = newRow.StartedAt.MicrosecondsSinceUnixEpoch / 1000L;
                GcdDurationMs = (long)newRow.DurationMs;
            }
        }

        public void OnGlobalCooldownDelete(EventContext ctx, GlobalCooldown row)
        {
            if (_bound && row.Caster == _localId)
            {
                GcdStartMs = 0L;
                GcdDurationMs = 0L;
            }
        }

        public bool IsGlobalCooldownActive(long nowMs)
            => GcdDurationMs > 0L && nowMs < GcdEndMs;

        public void PredictGlobalCooldown(long startMs, long durationMs)
        {
            if (durationMs <= 0L)
                return;

            long predictedEndMs = startMs + durationMs;
            if (predictedEndMs <= GcdEndMs)
                return;

            GcdStartMs = startMs;
            GcdDurationMs = durationMs;
        }

        public void ClearPredictedGlobalCooldown()
        {
            GcdStartMs = 0L;
            GcdDurationMs = 0L;
        }

        public static bool ShouldClearPredictedGlobalCooldownForSelfCancel(
            ulong castTimeMs,
            string behavior,
            bool isMovementDeliveryAction)
        {
            return !isMovementDeliveryAction
                && castTimeMs > 0UL
                && !SpellDefinitionContracts.BehaviorCastsOnRelease(behavior);
        }

        public float GlobalCooldownFractionRemaining(long nowMs)
        {
            if (!IsGlobalCooldownActive(nowMs) || GcdDurationMs <= 0L)
                return 0f;

            float elapsed = nowMs - GcdStartMs;
            return UnityEngine.Mathf.Clamp01(1f - elapsed / GcdDurationMs);
        }

        // ---------------------------------------------------------------
        // SpellCooldown callbacks
        // ---------------------------------------------------------------

        public void OnSpellCooldownInsert(EventContext ctx, SpellCooldown row)
        {
            if (_bound && row.Caster == _localId)
                _spellCds[row.Kind] = (row.LastCastAt.MicrosecondsSinceUnixEpoch / 1000L, (long)row.DurationMs);
        }

        public void OnSpellCooldownUpdate(EventContext ctx, SpellCooldown oldRow, SpellCooldown newRow)
        {
            if (_bound && newRow.Caster == _localId)
                _spellCds[newRow.Kind] = (newRow.LastCastAt.MicrosecondsSinceUnixEpoch / 1000L, (long)newRow.DurationMs);
        }

        public void OnSpellCooldownDelete(EventContext ctx, SpellCooldown row)
        {
            if (_bound && row.Caster == _localId)
                _spellCds.Remove(row.Kind);
        }

        /// <summary>Remaining per-action cooldown at a client UTC timestamp; zero means available.</summary>
        public long GetSpellCooldownRemainingMs(string kind, long clientNowMs)
        {
            if (!_spellCds.TryGetValue(kind, out var cooldown))
                return 0L;

            return Math.Max(0L, cooldown.lastCastMs + cooldown.durationMs - ArenaServerClock.ToServerTimeMs(clientNowMs));
        }

        /// <summary>Predict a per-action cooldown using a server UTC timestamp.</summary>
        public void PredictSpellCooldown(string kind, long lastCastMs, long durationMs)
        {
            if (string.IsNullOrWhiteSpace(kind) || durationMs <= 0L)
                return;

            if (_spellCds.TryGetValue(kind, out var existing))
            {
                long existingEndMs = existing.lastCastMs + existing.durationMs;
                long predictedEndMs = lastCastMs + durationMs;
                if (predictedEndMs <= existingEndMs)
                    return;
            }

            _spellCds[kind] = (lastCastMs, durationMs);
        }

        // ---------------------------------------------------------------
        // Predicted-action ledger (feel audit F1)
        // ---------------------------------------------------------------

        /// <summary>
        /// Denial cue hook: fired with the action kind, the action id as
        /// pressed on the action bar (slot identity — netcode design review
        /// S2), and the server's machine-readable rejection reason (netcode
        /// audit R2) whenever a predicted action is rolled back after a server
        /// rejection. HUD/action-bar surfaces subscribe for the slot flash and
        /// the honest denial toast ("insufficient stamina" vs "on cooldown").
        /// </summary>
        public static event Action<string, string, ActionRejectReason>? PredictionRejected;

        /// <summary>
        /// Advisory local denial (netcode design review S4): the client denied
        /// a press before sending it — e.g. the bundled-collision LOS
        /// pre-check. Rides the same event surface as server rejections so the
        /// toast and slot flash read identically; nothing was predicted, so
        /// there is nothing to roll back. The server stays authoritative for
        /// every press that is actually sent.
        /// </summary>
        public static void NotifyLocalAdvisoryDenial(
            string actionKind,
            string pressedActionId,
            ActionRejectReason reason)
        {
            PredictionRejected?.Invoke(actionKind, pressedActionId, reason);
        }

        /// <summary>
        /// Applies the standard press-time predictions (GCD, per-action
        /// cooldown, resource reservation) and records exactly what changed so
        /// <see cref="RollbackPrediction"/> can restore it on rejection.
        /// Guards match the individual Predict*/Reserve* methods, so behavior
        /// on the happy path is unchanged.
        /// </summary>
        public PredictedActionLedger PredictActionStart(
            PlayerEntity? entity,
            string actionKind,
            long cooldownDurationMs,
            bool usesGlobalCooldown,
            long gcdDurationMs,
            string resourceKind,
            float resourceCost,
            long nowMs)
        {
            long priorGcdStartMs = GcdStartMs;
            long priorGcdDurationMs = GcdDurationMs;
            bool gcdPredicted = false;
            if (usesGlobalCooldown)
            {
                PredictGlobalCooldown(nowMs, gcdDurationMs);
                gcdPredicted = GcdStartMs != priorGcdStartMs || GcdDurationMs != priorGcdDurationMs;
            }

            bool hadPriorCooldown = _spellCds.TryGetValue(actionKind, out var priorCooldown);
            bool cooldownPredicted = false;
            long cooldownStartServerMs = ArenaServerClock.ToServerTimeMs(nowMs);
            if (cooldownDurationMs > 0L)
            {
                PredictSpellCooldown(actionKind, cooldownStartServerMs, cooldownDurationMs);
                cooldownPredicted = _spellCds.TryGetValue(actionKind, out var applied)
                    && (!hadPriorCooldown || applied != priorCooldown);
            }

            float reservedCost = entity != null
                ? ReservePredictedResource(entity, resourceKind, resourceCost, nowMs)
                : 0f;

            return new PredictedActionLedger(
                actionKind,
                gcdPredicted,
                priorGcdStartMs,
                priorGcdDurationMs,
                GcdStartMs,
                GcdDurationMs,
                cooldownPredicted,
                hadPriorCooldown,
                priorCooldown.lastCastMs,
                priorCooldown.durationMs,
                cooldownStartServerMs,
                cooldownDurationMs,
                resourceKind,
                reservedCost);
        }

        /// <summary>
        /// Restores every prediction-layer side effect recorded on the ledger.
        /// Each restore is value-guarded: if an authoritative row (or a later
        /// legitimate prediction) has overwritten the predicted value since the
        /// press, that state is left untouched. Never mutates authoritative rows.
        /// </summary>
        public void RollbackPrediction(in PredictedActionLedger ledger, ActionRejectReason rejectReason)
        {
            if (ledger.CooldownPredicted
                && _spellCds.TryGetValue(ledger.ActionKind, out var current)
                && current.lastCastMs == ledger.PredictedCooldownLastCastMs
                && current.durationMs == ledger.PredictedCooldownDurationMs)
            {
                if (ledger.HadPriorCooldown)
                    _spellCds[ledger.ActionKind] = (ledger.PriorCooldownLastCastMs, ledger.PriorCooldownDurationMs);
                else
                    _spellCds.Remove(ledger.ActionKind);
            }

            if (ledger.GcdPredicted
                && GcdStartMs == ledger.PredictedGcdStartMs
                && GcdDurationMs == ledger.PredictedGcdDurationMs)
            {
                GcdStartMs = ledger.PriorGcdStartMs;
                GcdDurationMs = ledger.PriorGcdDurationMs;
            }

            ReleasePredictedResource(ledger.ReservedResourceKind, ledger.ReservedResourceCost);

            PredictionRejected?.Invoke(ledger.ActionKind, ledger.PressedActionId, rejectReason);
        }

        // ---------------------------------------------------------------
        // Predicted resource reservation
        // ---------------------------------------------------------------

        public float EffectiveCurrentResource(PlayerEntity entity, string resourceKind)
        {
            ReconcilePredictedResources(entity);
            string normalizedKind = NormalizeResourceKind(resourceKind);
            if (string.IsNullOrWhiteSpace(normalizedKind))
                return 0f;

            float current = CurrentResourceForKind(entity, normalizedKind);
            return _predictedResourceSpends.TryGetValue(normalizedKind, out PredictedResourceSpend predicted)
                ? UnityEngine.Mathf.Max(0f, current - predicted.Spend)
                : current;
        }

        /// <summary>
        /// Returns the cost actually reserved (0 when the guards reject the
        /// reservation) so prediction ledgers can roll back exactly that amount.
        /// </summary>
        public float ReservePredictedResource(
            PlayerEntity entity,
            string resourceKind,
            float cost,
            long nowMs)
        {
            if (cost <= 0.001f)
                return 0f;

            string normalizedKind = NormalizeResourceKind(resourceKind);
            if (string.IsNullOrWhiteSpace(normalizedKind))
                return 0f;

            float current = CurrentResourceForKind(entity, normalizedKind);
            bool hadPrediction = _predictedResourceSpends.TryGetValue(normalizedKind, out PredictedResourceSpend predicted);
            if (!hadPrediction)
                predicted.LastObserved = current;

            predicted.Spend = UnityEngine.Mathf.Max(0f, predicted.Spend + cost);
            predicted.ExpiresAtMs = nowMs + PredictedResourceSpendTimeoutMs;
            _predictedResourceSpends[normalizedKind] = predicted;
            return cost;
        }

        /// <summary>
        /// Releases a previously reserved predicted spend immediately (server
        /// rejected the action, so no authoritative resource drop will ever
        /// confirm it). Floored at zero so reconciliation that already released
        /// part of it cannot double-credit.
        /// </summary>
        public void ReleasePredictedResource(string resourceKind, float cost)
        {
            if (cost <= 0.001f)
                return;

            string normalizedKind = NormalizeResourceKind(resourceKind);
            if (!_predictedResourceSpends.TryGetValue(normalizedKind, out PredictedResourceSpend predicted))
                return;

            predicted.Spend = UnityEngine.Mathf.Max(0f, predicted.Spend - cost);
            if (predicted.Spend <= 0.001f)
            {
                _predictedResourceSpends.Remove(normalizedKind);
                return;
            }

            _predictedResourceSpends[normalizedKind] = predicted;
        }

        public void ReconcilePredictedResources(PlayerEntity entity)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (string resourceKind in new List<string>(_predictedResourceSpends.Keys))
            {
                PredictedResourceSpend predicted = _predictedResourceSpends[resourceKind];
                if (predicted.ExpiresAtMs > 0L && nowMs > predicted.ExpiresAtMs)
                {
                    _predictedResourceSpends.Remove(resourceKind);
                    continue;
                }

                float current = CurrentResourceForKind(entity, resourceKind);
                if (predicted.LastObserved >= 0f && current < predicted.LastObserved)
                {
                    float confirmedSpend = predicted.LastObserved - current;
                    predicted.Spend = UnityEngine.Mathf.Max(0f, predicted.Spend - confirmedSpend);
                    if (predicted.Spend <= 0.001f)
                    {
                        _predictedResourceSpends.Remove(resourceKind);
                        continue;
                    }
                }

                if (predicted.LastObserved >= 0f && current > predicted.LastObserved + 0.001f)
                    predicted.Spend = UnityEngine.Mathf.Min(predicted.Spend, current);

                predicted.LastObserved = current;
                _predictedResourceSpends[resourceKind] = predicted;
            }
        }

        private static string NormalizeResourceKind(string resourceKind)
            => string.IsNullOrWhiteSpace(resourceKind)
                ? string.Empty
                : resourceKind.Trim().ToUpperInvariant();

        private static float CurrentResourceForKind(PlayerEntity entity, string resourceKind)
        {
            string normalizedKind = NormalizeResourceKind(resourceKind);
            if (string.Equals(normalizedKind, "MANA", StringComparison.OrdinalIgnoreCase))
                return entity.CurrentMana;
            if (string.Equals(normalizedKind, "STAMINA", StringComparison.OrdinalIgnoreCase))
                return entity.CurrentStamina;
            if (string.Equals(
                    normalizedKind,
                    NormalizeResourceKind(entity.PrimaryResourceKind),
                    StringComparison.OrdinalIgnoreCase))
            {
                return entity.CurrentPrimaryResource;
            }

            return 0f;
        }

        // ---------------------------------------------------------------
        // FixedActionChargeState callbacks
        // ---------------------------------------------------------------

        public void OnFixedActionChargeStateInsert(EventContext ctx, FixedActionChargeState row)
        {
            ApplyFixedActionChargeState(row);
        }

        public void OnFixedActionChargeStateUpdate(EventContext ctx, FixedActionChargeState oldRow, FixedActionChargeState newRow)
        {
            ApplyFixedActionChargeState(newRow);
        }

        public void OnFixedActionChargeStateDelete(EventContext ctx, FixedActionChargeState row)
        {
            if (_bound && row.Owner == _localId)
                _fixedActionCharges.Remove(Arena.Combat.WireIdentifier.Normalize(row.ActionId));
        }

        private void ApplyFixedActionChargeState(FixedActionChargeState row)
        {
            if (!_bound || row.Owner != _localId)
                return;

            string actionId = Arena.Combat.WireIdentifier.Normalize(row.ActionId);
            if (string.IsNullOrWhiteSpace(actionId))
                return;

            _fixedActionCharges[actionId] = new FixedActionChargeSnapshot(
                row.CurrentCharges,
                row.MaxCharges,
                (long)row.RechargeDurationMs,
                row.IsRecharging,
                row.RechargeStartedAt.MicrosecondsSinceUnixEpoch / 1000L,
                row.NextChargeReadyAt.MicrosecondsSinceUnixEpoch / 1000L);
        }

        public CastBarSnapshot? CurrentCastBar(long nowMs)
        {
            if (_predictedCastBar.HasValue)
            {
                CastBarSnapshot predicted = _predictedCastBar.Value;
                if (nowMs < predicted.EndMs
                    && ActiveCastMatchesCurrentPrediction(predicted.Kind))
                {
                    return predicted;
                }
            }

            if (ActiveCast is { } active
                && nowMs < active.endMs
                && !string.Equals(
                    Arena.Combat.WireIdentifier.Normalize(active.kind),
                    _locallySuppressedCastBarKind,
                    System.StringComparison.Ordinal))
            {
                return new CastBarSnapshot(active.startMs, active.endMs, active.kind);
            }

            if (_predictedCastBar.HasValue)
            {
                CastBarSnapshot predicted = _predictedCastBar.Value;
                if (nowMs < predicted.EndMs)
                    return predicted;

                _predictedCastBar = null;
            }

            return null;
        }

        public TimedActionPresentationSnapshot? CurrentTimedAction(long nowMs)
        {
            CastBarSnapshot? cast = CurrentCastBar(nowMs);
            if (!cast.HasValue)
                return null;

            CastBarSnapshot value = cast.Value;
            return new TimedActionPresentationSnapshot(
                value.Kind,
                value.StartMs,
                value.EndMs,
                value.Kind,
                TimedActionPresentationStyle.CombatCast);
        }

        public CastActionToken CreateCastActionToken(string kind)
        {
            ulong seq = _nextClientActionSeq++;
            if (_nextClientActionSeq == 0UL)
                _nextClientActionSeq = 1UL;

            var token = new CastActionToken(
                Guid.NewGuid().ToString("N"),
                seq,
                Arena.Combat.WireIdentifier.Normalize(kind));
            _currentCastToken = token;
            _locallySuppressedCastBarKind = string.Empty;
            return token;
        }

        public ActionPredictionToken CreateActionPredictionToken(string kind)
        {
            ulong seq = _nextClientActionSeq++;
            if (_nextClientActionSeq == 0UL)
                _nextClientActionSeq = 1UL;

            return new ActionPredictionToken(
                Guid.NewGuid().ToString("N"),
                seq,
                Arena.Combat.WireIdentifier.Normalize(kind));
        }

        public CastActionToken CurrentCastTokenForRelease(string kind)
        {
            string normalizedKind = Arena.Combat.WireIdentifier.Normalize(kind);
            if (_currentCastToken is { } token
                && string.Equals(
                    Arena.Combat.WireIdentifier.Normalize(token.Kind),
                    normalizedKind,
                    StringComparison.Ordinal))
            {
                return token;
            }

            return CastActionToken.Legacy(normalizedKind);
        }

        public void PredictCastBar(string kind, long startMs, long durationMs, CastActionToken token)
        {
            if (string.IsNullOrWhiteSpace(kind) || durationMs <= 0L)
                return;

            _locallySuppressedCastBarKind = string.Empty;
            _currentCastToken = token;
            _predictedCastBar = new CastBarSnapshot(startMs, startMs + durationMs, kind);
        }

        public CastCancelSnapshot? SuppressCastBarForLocalInterrupt()
        {
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            CastBarSnapshot? current = CurrentCastBar(nowMs);
            if (!current.HasValue)
                return null;

            // Server time remains authoritative; this only signals that the local bar still showed remaining cast time when cancel was authored.
            ulong observedRemainingMs = (ulong)Math.Max(0L, current.Value.EndMs - nowMs);
            _predictedCastBar = null;
            _locallySuppressedCastBarKind =
                Arena.Combat.WireIdentifier.Normalize(current.Value.Kind);
            if (_currentCastToken is { } token
                && string.Equals(
                    Arena.Combat.WireIdentifier.Normalize(token.Kind),
                    _locallySuppressedCastBarKind,
                    StringComparison.Ordinal))
            {
                return new CastCancelSnapshot(token, observedRemainingMs);
            }

            return new CastCancelSnapshot(
                CastActionToken.Legacy(current.Value.Kind),
                observedRemainingMs);
        }

        // ---------------------------------------------------------------
        // PredictedActionResult callbacks
        // ---------------------------------------------------------------

        public void OnPredictedActionResultInsert(EventContext ctx, PredictedActionResult row)
        {
            if (!_bound
                || row.Family != PredictedActionFamily.SpellCast
                || row.Owner != _localId
                || !MatchesCurrentCastToken(row))
            {
                return;
            }

            if (row.Result == ActionResultKind.Accepted)
            {
                return;
            }

            if (row.Result == ActionResultKind.Canceled)
            {
                string suppressedKind = _currentCastToken?.Kind ?? string.Empty;
                _predictedCastBar = null;
                _currentCastToken = null;
                _locallySuppressedCastBarKind = Arena.Combat.WireIdentifier.Normalize(suppressedKind);
                return;
            }

            if (row.Result == ActionResultKind.Rejected || row.Result == ActionResultKind.StaleToken)
            {
                _predictedCastBar = null;
                _currentCastToken = null;
                _locallySuppressedCastBarKind = string.Empty;
                return;
            }

            if (row.Result == ActionResultKind.CancelTooLate)
            {
                _predictedCastBar = null;
                _currentCastToken = null;
            }
        }

        private bool MatchesCurrentCastToken(PredictedActionResult row)
        {
            if (_currentCastToken is not { } token || !token.IsPredicted)
                return false;

            return string.Equals(row.PredictedActionId, token.PredictedCastId, StringComparison.Ordinal)
                && row.ClientActionSeq == token.ClientActionSeq;
        }

        // ---------------------------------------------------------------
        // ActiveCast callbacks
        // ---------------------------------------------------------------

        public void OnActiveCastInsert(EventContext ctx, ActiveCast row)
        {
            if (_bound && row.Caster == _localId && !IsMovementDeliveryCast(row))
            {
                ActiveCast = (row.StartedAt.MicrosecondsSinceUnixEpoch / 1000L,
                              row.EndsAt.MicrosecondsSinceUnixEpoch / 1000L,
                              row.Kind,
                              row.TargetId,
                              row.AimX,
                              row.AimY,
                              row.AimZ);
                _activeCastPredictedCastId = row.PredictedCastId ?? string.Empty;
                _activeCastClientActionSeq = row.ClientActionSeq;
                string normalizedKind = Arena.Combat.WireIdentifier.Normalize(row.Kind);
                if (!string.Equals(
                    _locallySuppressedCastBarKind,
                    normalizedKind,
                    System.StringComparison.Ordinal))
                {
                    ConfirmPredictedCastBar(row);
                    _locallySuppressedCastBarKind = string.Empty;
                }
                if (_currentCastToken is { } token
                    && !string.Equals(
                        Arena.Combat.WireIdentifier.Normalize(token.Kind),
                        normalizedKind,
                        System.StringComparison.Ordinal))
                {
                    _currentCastToken = null;
                }
            }
        }

        public void OnActiveCastUpdate(EventContext ctx, ActiveCast oldRow, ActiveCast newRow)
        {
            if (_bound && newRow.Caster == _localId && !IsMovementDeliveryCast(newRow))
            {
                ActiveCast = (newRow.StartedAt.MicrosecondsSinceUnixEpoch / 1000L,
                              newRow.EndsAt.MicrosecondsSinceUnixEpoch / 1000L,
                              newRow.Kind,
                              newRow.TargetId,
                              newRow.AimX,
                              newRow.AimY,
                              newRow.AimZ);
                _activeCastPredictedCastId = newRow.PredictedCastId ?? string.Empty;
                _activeCastClientActionSeq = newRow.ClientActionSeq;
                ConfirmPredictedCastBar(newRow);
            }
        }

        public void OnActiveCastDelete(EventContext ctx, ActiveCast row)
        {
            if (_bound && row.Caster == _localId && !IsMovementDeliveryCast(row))
            {
                ActiveCast = null;
                _activeCastPredictedCastId = string.Empty;
                _activeCastClientActionSeq = 0UL;
                if (_predictedCastBar.HasValue
                    && string.Equals(
                        Arena.Combat.WireIdentifier.Normalize(_predictedCastBar.Value.Kind),
                        Arena.Combat.WireIdentifier.Normalize(row.Kind),
                        StringComparison.Ordinal))
                {
                    _predictedCastBar = null;
                }
                if (!_predictedCastBar.HasValue)
                    _currentCastToken = null;
                _locallySuppressedCastBarKind = string.Empty;
            }
        }

        private void ConfirmPredictedCastBar(ActiveCast row)
        {
            if (!_predictedCastBar.HasValue)
                return;
            if (_currentCastToken is not { } token)
            {
                _predictedCastBar = null;
                return;
            }

            CastBarSnapshot predicted = _predictedCastBar.Value;
            string normalizedKind = Arena.Combat.WireIdentifier.Normalize(row.Kind);
            if (!string.Equals(
                    Arena.Combat.WireIdentifier.Normalize(token.Kind),
                    normalizedKind,
                    StringComparison.Ordinal)
                || !string.Equals(
                    Arena.Combat.WireIdentifier.Normalize(_predictedCastBar.Value.Kind),
                    normalizedKind,
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(row.PredictedCastId)
                || !string.Equals(
                    row.PredictedCastId,
                    token.PredictedCastId,
                    StringComparison.Ordinal))
            {
                _predictedCastBar = null;
                return;
            }
            if (row.ClientActionSeq != token.ClientActionSeq)
            {
                _predictedCastBar = null;
                return;
            }

            long authoritativeEndMs = row.EndsAt.MicrosecondsSinceUnixEpoch / 1000L;
            _predictedCastBar = new CastBarSnapshot(
                predicted.StartMs,
                Math.Max(predicted.EndMs, authoritativeEndMs),
                row.Kind);
        }

        private bool ActiveCastMatchesCurrentPrediction(string predictedKind)
        {
            if (ActiveCast is not { } active || _currentCastToken is not { } token || !token.IsPredicted)
                return false;

            return string.Equals(
                    Arena.Combat.WireIdentifier.Normalize(active.kind),
                    Arena.Combat.WireIdentifier.Normalize(predictedKind),
                    StringComparison.Ordinal)
                && string.Equals(_activeCastPredictedCastId, token.PredictedCastId, StringComparison.Ordinal)
                && _activeCastClientActionSeq == token.ClientActionSeq;
        }

        private static bool IsMovementDeliveryCast(ActiveCast row)
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn != null)
            {
                string normalizedKind = Arena.Combat.WireIdentifier.Normalize(row.Kind);
                foreach (AbilityCatalog ability in conn.Db.AbilityCatalog.Iter())
                {
                    if (string.Equals(Arena.Combat.WireIdentifier.Normalize(ability.ActionId), normalizedKind, System.StringComparison.Ordinal)
                        && string.Equals(Arena.Combat.WireIdentifier.Normalize(ability.AbilityKind), Arena.Combat.AbilityKinds.Movement, System.StringComparison.Ordinal))
                        return true;
                }
            }

            SpellDefinition? definition = conn?.Db.SpellDefinition.Kind.Find(row.Kind);
            return definition != null
                && string.Equals(
                    definition.Behavior,
                    SpellDefinitionContracts.BehaviorChargedRelease,
                    System.StringComparison.Ordinal);
        }

        public void OnMovementActionStateInsert(EventContext ctx, MovementActionState row)
        {
            if (_bound && row.Owner == _localId)
                MovementAction = (
                    row.StartedAt.MicrosecondsSinceUnixEpoch / 1000L,
                    row.ActiveUntil.MicrosecondsSinceUnixEpoch / 1000L,
                    row.RecoveryUntil.MicrosecondsSinceUnixEpoch / 1000L,
                    row.Kind);
        }

        public void OnMovementActionStateUpdate(EventContext ctx, MovementActionState oldRow, MovementActionState newRow)
        {
            if (_bound && newRow.Owner == _localId)
                MovementAction = (
                    newRow.StartedAt.MicrosecondsSinceUnixEpoch / 1000L,
                    newRow.ActiveUntil.MicrosecondsSinceUnixEpoch / 1000L,
                    newRow.RecoveryUntil.MicrosecondsSinceUnixEpoch / 1000L,
                    newRow.Kind);
        }

        public void OnMovementActionStateDelete(EventContext ctx, MovementActionState row)
        {
            if (_bound && row.Owner == _localId)
                MovementAction = null;
        }
    }
}

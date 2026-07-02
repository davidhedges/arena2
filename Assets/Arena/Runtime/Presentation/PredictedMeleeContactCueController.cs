#nullable enable
using System;
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
    /// Predicted melee contact cues (feel audit F5 slice 2). At the authored
    /// first hit window of a predicted local melee press, runs an ADVISORY
    /// hit test against current rendered positions (the press gate's
    /// range/facing math via MeleeStrikeGeometry) and, if it passes, plays a
    /// light contact layer only: the authored MELEE_IMPACT cue set (spark +
    /// impact audio) plus a small hitstop. Damage numbers, health, and target
    /// hit reactions remain 100% authoritative — this predicts startup
    /// feedback, never outcomes (docs/combat-authoring-contract.md, "Hit
    /// Validation Timing"). Cosmetic-only and instantly killable:
    /// <see cref="CompiledIn"/> is the compile-time kill switch,
    /// <see cref="DebugEnabled"/> the default-ON runtime flag (quote key
    /// while the netcode overlay is visible).
    /// </summary>
    public sealed class PredictedMeleeContactCueController : MonoBehaviour
    {
        /// <summary>Compile-time kill switch: set false to fully disable the feature.</summary>
        public const bool CompiledIn = true;

        /// <summary>Debug flag, default ON; toggled from NetcodeDebugOverlay.</summary>
        public static bool DebugEnabled = true;

        public static bool IsActive => CompiledIn && DebugEnabled;

        // Property indirection so the const kill switch can gate statements
        // without tripping CS0162 unreachable-code warnings.
        public static bool FeatureCompiledIn => CompiledIn;

        private const float HitstopSeconds = 0.04f;
        private const int PredictedFirstHitIndex = 0;

        private static PredictedMeleeContactCueController? _instance;

        private readonly PredictedMeleeContactCueLedger _ledger = new();
        private readonly Dictionary<string, PendingAdvisoryContact> _pendingByToken =
            new(StringComparer.Ordinal);

        public static int CuesFired => _instance?._ledger.CuesFired ?? 0;
        public static int MatchedCues => _instance?._ledger.MatchedCues ?? 0;
        public static int FalsePositives => _instance?._ledger.FalsePositives ?? 0;
        public static int SuppressedAuthoritativeCues => _instance?._ledger.SuppressedAuthoritativeCues ?? 0;

        private readonly struct PendingAdvisoryContact
        {
            public PendingAdvisoryContact(
                PlayerEntity attacker,
                ICombatTargetEntity target,
                string runtimeActionId,
                float strikeRange,
                float minimumRange,
                long fireAtMs)
            {
                Attacker = attacker;
                Target = target;
                RuntimeActionId = runtimeActionId;
                StrikeRange = strikeRange;
                MinimumRange = minimumRange;
                FireAtMs = fireAtMs;
            }

            public PlayerEntity Attacker { get; }
            public ICombatTargetEntity Target { get; }
            public string RuntimeActionId { get; }
            public float StrikeRange { get; }
            public float MinimumRange { get; }
            public long FireAtMs { get; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!FeatureCompiledIn)
                return;
            if (_instance != null)
                return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("PredictedMeleeContactCueController");
            DontDestroyOnLoad(go);
            go.AddComponent<PredictedMeleeContactCueController>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        /// <summary>
        /// Called by MeleeInputHandler for a predicted, non-queued,
        /// non-gap-close press with a live target. Schedules the advisory
        /// contact test at the authored first hit window (scaled from the
        /// timing reference to the played clip, exactly as playback scales
        /// its authored thresholds).
        /// </summary>
        public static void OnPredictedLocalStrike(
            DbConnection conn,
            PlayerEntity entity,
            ICombatTargetEntity target,
            ActionPredictionToken token,
            string combatProfile,
            string actionId,
            float strikeRange,
            float minimumRange,
            long pressedAtMs)
        {
            if (!IsActive || _instance == null || !token.IsPredicted)
                return;

            CombatAnimationSet? animationSet = CombatAnimationSetCatalog.Resolve(combatProfile);
            if (animationSet == null)
                return;

            string runtimeActionId = CombatActionIds.ResolveRuntimeActionId(conn, combatProfile, actionId);
            int strikeIndex = animationSet.GetStrikeIndexForActionId(runtimeActionId);
            if (strikeIndex <= 0)
                return;

            float hitWindowSeconds = animationSet.GetStrikeFirstHitWindowSeconds(strikeIndex);
            if (hitWindowSeconds <= 0f)
                return;

            AnimationClip? playedClip = animationSet.GetStrikeClip(strikeIndex);
            float scaledHitWindowSeconds = CombatActionPlaybackController.ScaleAuthoredMeleeSeconds(
                hitWindowSeconds,
                animationSet.GetStrikeTimingReferenceLengthSeconds(strikeIndex),
                playedClip != null ? playedClip.length : 0f);

            string tokenKey = TokenKey(token.PredictedActionId, token.ClientActionSeq);
            long fireAtMs = pressedAtMs + (long)(scaledHitWindowSeconds * 1000f);
            _instance._pendingByToken[tokenKey] = new PendingAdvisoryContact(
                entity,
                target,
                runtimeActionId,
                strikeRange,
                minimumRange,
                fireAtMs);
            _instance._ledger.Schedule(
                tokenKey,
                target.TargetIdentity.ToString(),
                fireAtMs,
                PredictedFirstHitIndex);
        }

        /// <summary>
        /// Forwarded by MeleeInputHandler from its PredictedActionResult
        /// subscription — one subscription, two consumers.
        /// </summary>
        public static void OnPredictedActionResult(PredictedActionResult row)
        {
            if (_instance == null)
                return;

            string tokenKey = TokenKey(row.PredictedActionId, row.ClientActionSeq);
            if (row.Result == ActionResultKind.Accepted)
            {
                _instance._ledger.MapAcceptedActionInstance(row.ActionInstanceId, tokenKey);
                return;
            }

            if (row.Result == ActionResultKind.Rejected || row.Result == ActionResultKind.StaleToken)
            {
                _instance._pendingByToken.Remove(tokenKey);
                _instance._ledger.OnPredictionRejected(tokenKey);
            }
        }

        /// <summary>
        /// Called by CombatVFXDispatcher for every CombatEvent before cue
        /// dispatch. Returns true when the row's impact cue would duplicate a
        /// predicted contact cue that already played. Contact/block/parry
        /// rows also feed the false-positive correlation but never suppress.
        /// </summary>
        public static bool ShouldSuppressAuthoritativeContactCue(CombatEvent row)
        {
            if (_instance == null)
                return false;

            if (string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal))
                return false;

            bool isImpact = string.Equals(row.EventType, CombatEventTypes.Impact, StringComparison.Ordinal);
            if (!isImpact
                && !string.Equals(row.EventType, CombatEventTypes.Contact, StringComparison.Ordinal)
                && !string.Equals(row.EventType, CombatEventTypes.Block, StringComparison.Ordinal)
                && !string.Equals(row.EventType, CombatEventTypes.Parry, StringComparison.Ordinal))
            {
                return false;
            }

            PlayerEntity? local = EntityRegistry.Instance?.LocalPlayerEntity;
            if (local == null || row.Caster != local.Identity)
                return false;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            AuthoritativeContactCueDecision decision = _instance._ledger.OnAuthoritativeContact(
                row.ActionInstanceId,
                row.Hit.ToString(),
                isImpact,
                row.HitIndex,
                nowMs);
            return decision == AuthoritativeContactCueDecision.SuppressCue;
        }

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _ledger.Tick(nowMs);
            if (_pendingByToken.Count == 0)
                return;

            List<string>? dueTokens = null;
            foreach (KeyValuePair<string, PendingAdvisoryContact> pair in _pendingByToken)
            {
                if (nowMs >= pair.Value.FireAtMs)
                    (dueTokens ??= new List<string>()).Add(pair.Key);
            }

            if (dueTokens == null)
                return;

            foreach (string tokenKey in dueTokens)
            {
                PendingAdvisoryContact pending = _pendingByToken[tokenKey];
                _pendingByToken.Remove(tokenKey);
                FireAdvisoryContact(tokenKey, pending, nowMs);
            }
        }

        private void FireAdvisoryContact(string tokenKey, in PendingAdvisoryContact pending, long nowMs)
        {
            if (!_ledger.TryBeginFiring(tokenKey))
                return;

            if (!IsActive || !PassesAdvisoryHitTest(pending))
            {
                _ledger.Drop(tokenKey);
                return;
            }

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                _ledger.Drop(tokenKey);
                return;
            }

            _ledger.RecordCueFired(tokenKey, nowMs);
            CombatVFXDispatcher.PlayPredictedLocalMeleeContactCue(
                conn,
                pending.Attacker,
                pending.Target,
                pending.RuntimeActionId,
                PredictedFirstHitIndex);
            MeleeContactHitstop.Play(pending.Attacker.GameObject, HitstopSeconds);
        }

        private static bool PassesAdvisoryHitTest(in PendingAdvisoryContact pending)
        {
            PlayerEntity attacker = pending.Attacker;
            ICombatTargetEntity target = pending.Target;
            if (attacker == null || attacker.IsDestroyed || !attacker.IsAlive)
                return false;
            if (target == null || target.IsDestroyed || !target.IsAlive)
                return false;

            Vector3 attackerPosition = attacker.GameObject.transform.position;
            Vector3 targetPosition = target.GetPresentationRoot().position;
            float horizontalDistance = MeleeStrikeGeometry.HorizontalDistance(attackerPosition, targetPosition);
            if (!MeleeStrikeGeometry.PassesRangeGate(
                    horizontalDistance,
                    pending.StrikeRange,
                    pending.MinimumRange,
                    Mathf.Max(0f, target.HitRadius)))
            {
                return false;
            }

            return MeleeStrikeGeometry.IsWithinFacingArc(
                attacker.GameObject.transform.forward,
                attackerPosition,
                targetPosition);
        }

        private static string TokenKey(string predictedActionId, ulong clientActionSeq)
            => $"{predictedActionId}:{clientActionSeq}";
    }
}

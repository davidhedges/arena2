#nullable enable
using System;
using Arena.Combat;
using Arena.Debugging;
using Arena.Entity;
using Arena.Network;
using Arena.Simulation;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Local auto-attack swing scheduling (netcode design review S6). The
    /// server replicates the owner's <see cref="AutoAttackState"/> row, so the
    /// client knows the swing schedule: this component starts the LOCAL
    /// player's swing presentation at <c>next_swing_at</c> converted through
    /// <see cref="ArenaServerClock"/> instead of waiting for the COMBAT_CAST
    /// to arrive ~RTT late. The authoritative CAST is then consumed as a
    /// duplicate (the F5 slice-1 suppression pattern, routed here by
    /// <see cref="CombatAnimationReplayPolicy"/>), and the scheduled swing
    /// rides the same advisory contact-cue path as predicted melee.
    ///
    /// Never swings a lie: scheduling requires a precise clock sample
    /// (degrades to today's CAST-driven playback without one) and fires only
    /// when a client-side mirror of every server hold passes — live harmable
    /// target, range, advisory LOS, hard CC, active defense, blocking cast,
    /// dodge, voluntary-move cadence reset, <c>pending_due</c>. A swing the
    /// mirror holds is simply not predicted; the authoritative CAST plays it
    /// on arrival exactly as before. Presentation-only: cadence, damage, and
    /// all validation stay server-side. Remote players are untouched.
    /// </summary>
    public sealed class AutoAttackSwingScheduler : MonoBehaviour
    {
        /// <summary>Runtime kill switch, default ON; toggled from NetcodeDebugOverlay.</summary>
        public static bool DebugEnabled = true;

        // The authoritative CAST for an on-time swing arrives one-way delay +
        // tick rounding after the locally scheduled start; a CAST later than
        // this window is a different swing (e.g. held behind cover, then
        // released) and must play authoritatively.
        private const long CastSuppressWindowMs = 400L;
        // Never fire a local swing this long past its converted schedule —
        // frame hitches and late row arrival degrade to CAST-driven playback.
        private const long LateFireCutoffMs = 150L;
        // After any authoritative local auto CAST, hold local fires briefly so
        // a clock-estimate error can never double-present one swing (cadences
        // are far longer than this).
        private const long PostCastLockoutMs = 250L;
        // An armed auto-attack replacement swaps the next swing's strike
        // server-side and its pending row is not replicated; skip prediction
        // until that swing's CAST arrives (or the arm clearly expired).
        private const long ReplacementSuppressMs = 15000L;

        private const string DodgeMovementActionKind = "DODGE";
        private const string ResetCadenceOnVoluntaryMovePolicy = "RESET_CADENCE_ON_VOLUNTARY_MOVE";

        private static AutoAttackSwingScheduler? _instance;

        private string _lastScheduleKey = string.Empty;
        private bool _lastScheduleResolved;
        private long _replacementSuppressUntilMs;
        private long _lastAutoCastReceivedAtMs;
        private bool _hasPendingLocalSwing;
        private PendingLocalSwing _pendingLocalSwing;

        private readonly struct PendingLocalSwing
        {
            public PendingLocalSwing(
                string runtimeActionId,
                string cueTokenKey,
                long firedAtMs,
                double serverMinusClientAtFireMs,
                long expiresAtMs)
            {
                RuntimeActionId = runtimeActionId;
                CueTokenKey = cueTokenKey;
                FiredAtMs = firedAtMs;
                ServerMinusClientAtFireMs = serverMinusClientAtFireMs;
                ExpiresAtMs = expiresAtMs;
            }

            public string RuntimeActionId { get; }
            public string CueTokenKey { get; }
            public long FiredAtMs { get; }
            public double ServerMinusClientAtFireMs { get; }
            public long ExpiresAtMs { get; }
        }

        // Evidence counters (netcode design review S6 acceptance): surfaced in
        // NetcodeDebugOverlay and the remote-presentation-ab.csv aa_* columns.
        public static int SwingsFired { get; private set; }
        public static int SwingsHeldByMirror { get; private set; }
        public static int SwingsMissedLate { get; private set; }
        public static int SuppressedCasts { get; private set; }
        public static int UnpredictedCasts { get; private set; }
        public static int MismatchedCasts { get; private set; }
        public static int ExpiredWithoutCast { get; private set; }
        public static long StartErrorLastMs { get; private set; }
        public static long StartErrorMaxMs { get; private set; }
        public static long CastAlignLastMs { get; private set; }
        public static long CastAlignMaxAbsMs { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("AutoAttackSwingScheduler");
            DontDestroyOnLoad(go);
            go.AddComponent<AutoAttackSwingScheduler>();
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
        /// Called by ActionBarInputDispatcher when the local player arms an
        /// auto-attack replacement: the server's pending replacement row is
        /// private, so the next swing's strike identity is unknowable here.
        /// </summary>
        public static void NotifyAutoAttackReplacementArmed()
        {
            if (_instance == null)
                return;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _instance._replacementSuppressUntilMs = nowMs + ReplacementSuppressMs;
        }

        private void Update()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ExpirePendingLocalSwing(nowMs);

            var conn = NetworkManager.Instance?.Conn;
            var entity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (conn == null || entity == null || entity.IsDestroyed)
            {
                _lastScheduleKey = string.Empty;
                _lastScheduleResolved = false;
                _hasPendingLocalSwing = false;
                return;
            }

            AutoAttackState? row = conn.Db.AutoAttackState.Owner.Find(entity.Identity);
            if (row == null)
            {
                _lastScheduleKey = string.Empty;
                _lastScheduleResolved = false;
                return;
            }

            string scheduleKey = ScheduleKey(row);
            if (!string.Equals(scheduleKey, _lastScheduleKey, StringComparison.Ordinal))
            {
                _lastScheduleKey = scheduleKey;
                _lastScheduleResolved = false;
            }

            if (_lastScheduleResolved)
                return;

            CombatAnimationSet? animationSet = CombatAnimationSetCatalog.Resolve(row.CombatProfileId);
            if (!SupportsLocalPrediction(animationSet))
            {
                // The current prediction ledger correlates one local swing to
                // one authoritative CAST. Multi-strike charge windows stay
                // authoritative until that ledger can represent the complete
                // sequence without consuming or reordering continuation CASTs.
                _lastScheduleResolved = true;
                return;
            }

            if (!DebugEnabled || !ArenaServerClock.HasPreciseSample)
                return; // No prediction without a precise clock (contract item 1).

            double serverMinusClientMs = ArenaServerClock.EstimatedServerMinusClientMs;
            long nextSwingServerMs = row.NextSwingAt.MicrosecondsSinceUnixEpoch / 1000L;
            long fireAtClientMs = nextSwingServerMs - (long)Math.Round(serverMinusClientMs);
            if (nowMs < fireAtClientMs)
                return;

            if (nowMs > fireAtClientMs + LateFireCutoffMs)
            {
                _lastScheduleResolved = true;
                SwingsMissedLate++;
                return;
            }

            if (nowMs < _lastAutoCastReceivedAtMs + PostCastLockoutMs
                || nowMs < _replacementSuppressUntilMs)
            {
                _lastScheduleResolved = true;
                SwingsMissedLate++;
                return;
            }

            _lastScheduleResolved = true;
            if (!MirrorsAllowSwing(conn, entity, row, nowMs, out ICombatTargetEntity? target, out float strikeRange))
            {
                SwingsHeldByMirror++;
                ActionBarTrace.Trace(
                    $"[AA_SCHED] held local auto swing strike={row.StrikeId} next_at_ms={nextSwingServerMs}");
                return;
            }

            FireLocalSwing(conn, entity, row, target!, strikeRange, nowMs, fireAtClientMs, serverMinusClientMs);
        }

        /// <summary>
        /// Client-side mirror of every condition the server holds a due swing
        /// for (auto_attack.rs tick_auto_attacks + auto_attack_paused).
        /// Conservative by design: when any mirror is missing or ambiguous the
        /// swing is not predicted and the CAST drives playback as today.
        /// </summary>
        private static bool MirrorsAllowSwing(
            DbConnection conn,
            PlayerEntity entity,
            AutoAttackState row,
            long nowMs,
            out ICombatTargetEntity? target,
            out float strikeRange)
        {
            target = null;
            strikeRange = 0f;

            if (row.PendingDue)
                return false; // Server already held this swing.
            if (!entity.IsAlive)
                return false;

            var registry = EntityRegistry.Instance;
            if (registry == null
                || !registry.TryGetCombatTarget(row.Target, out ICombatTargetEntity resolvedTarget)
                || resolvedTarget.IsDestroyed
                || !resolvedTarget.IsAlive)
            {
                return false;
            }

            AutoAttackCatalog? gameplay = ResolveAutoAttackGameplay(conn, row);
            if (gameplay == null)
                return false;

            // Voluntary-move cadence reset (FULL_DRAW-style policies): the
            // server reschedules instead of swinging when the epoch moved.
            if (string.Equals(
                    gameplay.MovementPolicy?.Trim(),
                    ResetCadenceOnVoluntaryMovePolicy,
                    StringComparison.OrdinalIgnoreCase))
            {
                PlayerState? state = conn.Db.PlayerState.PlayerId.Find(entity.Identity);
                if (state == null || state.VoluntaryMoveEpoch != row.MovementEpochAtSchedule)
                    return false;
            }

            // Range, against the same geometry the server due-check uses.
            Vector3 localPos = entity.SimState.GetServerPosition();
            Vector3 targetPos = resolvedTarget.GetRenderPosition();
            float horizontalDistance = MeleeStrikeGeometry.HorizontalDistance(localPos, targetPos);
            float allowed = MeleeStrikeGeometry.MaximumContactDistance(
                gameplay.Range,
                Mathf.Max(0f, resolvedTarget.HitRadius));
            if (horizontalDistance > allowed)
                return false;

            // Advisory LOS (S4 mirror) — same pre-check the press path uses.
            if (gameplay.RequiresTargetLos
                && AdvisoryTargetLineOfSight.IsPressBlocked(entity, resolvedTarget))
            {
                return false;
            }

            if (HasDisablingStatusMirror(conn, entity.Identity))
                return false;
            if (IsDefenseActiveMirror(conn, entity.Identity))
                return false;
            // Blocking cast mirror: LocalCombatState.ActiveCast already
            // excludes movement-delivery casts, matching has_blocking_active_cast.
            if (LocalCombatState.Instance.ActiveCast.HasValue)
                return false;
            if (LocalCombatState.Instance.MovementAction is { } movementAction
                && string.Equals(movementAction.kind?.Trim(), DodgeMovementActionKind, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            target = resolvedTarget;
            strikeRange = gameplay.Range;
            return true;
        }

        private void FireLocalSwing(
            DbConnection conn,
            PlayerEntity entity,
            AutoAttackState row,
            ICombatTargetEntity target,
            float strikeRange,
            long nowMs,
            long fireAtClientMs,
            double serverMinusClientMs)
        {
            string combatProfile = CombatProfileResolver.ResolveForEntity(conn, entity);
            string runtimeActionId = CombatActionIds.ResolveRuntimeActionId(conn, combatProfile, row.StrikeId);
            if (string.IsNullOrWhiteSpace(runtimeActionId))
                return;

            ActionPredictionToken cueToken = LocalCombatState.Instance.CreateActionPredictionToken(runtimeActionId);
            entity.RequestCombatAnimation(new CombatAnimationRequest(
                runtimeActionId,
                CombatAnimationCategory.AutoAttack,
                CombatAnimationAuthority.Predicted,
                source: CombatEventSources.AutoAttack,
                startedAtMs: nowMs));

            string cueTokenKey = $"{cueToken.PredictedActionId}:{cueToken.ClientActionSeq}";
            _pendingLocalSwing = new PendingLocalSwing(
                runtimeActionId,
                cueTokenKey,
                nowMs,
                serverMinusClientMs,
                nowMs + CastSuppressWindowMs);
            _hasPendingLocalSwing = true;

            // Cue parity (contract item 3): the scheduled swing takes the
            // exact advisory contact path predicted melee presses use.
            PredictedMeleeContactCueController.OnPredictedLocalStrike(
                conn,
                entity,
                target,
                cueToken,
                combatProfile,
                runtimeActionId,
                strikeRange,
                minimumRange: 0f,
                nowMs,
                isAutoAttack: true);

            SwingsFired++;
            StartErrorLastMs = nowMs - fireAtClientMs;
            StartErrorMaxMs = Math.Max(StartErrorMaxMs, StartErrorLastMs);
            ActionBarTrace.Trace(
                $"[AA_SCHED] fired local auto swing strike={runtimeActionId} start_err_ms={StartErrorLastMs}");
        }

        /// <summary>
        /// Routed by CombatAnimationReplayPolicy for every authoritative local
        /// auto-attack CAST. Returns true when the CAST duplicates the swing
        /// this component already started (contract item 2).
        /// </summary>
        public static bool HandleAuthoritativeLocalAutoAttackCast(
            DbConnection conn,
            PlayerEntity entity,
            string actionInstanceId,
            in CombatAnimationRequest request,
            long nowMs)
        {
            if (_instance == null)
                return false;

            string combatProfile = CombatProfileResolver.ResolveForEntity(conn, entity);
            CombatAnimationSet? animationSet = CombatAnimationSetCatalog.Resolve(combatProfile);
            if (animationSet?.IsAutoAttackVisualSequenceContinuation(request.ActionId) == true)
            {
                // Continuation CASTs are distinct authored swings. They must
                // reach PlayerAnimator and must never consume the root swing's
                // local-prediction correlation slot.
                return false;
            }

            _instance._lastAutoCastReceivedAtMs = nowMs;
            // Any auto CAST resolves an armed replacement (consumed or fallen
            // back to the intrinsic strike server-side).
            _instance._replacementSuppressUntilMs = 0L;

            if (!_instance._hasPendingLocalSwing)
            {
                UnpredictedCasts++;
                return false;
            }

            PendingLocalSwing pending = _instance._pendingLocalSwing;
            if (nowMs > pending.ExpiresAtMs)
            {
                _instance._hasPendingLocalSwing = false;
                ExpiredWithoutCast++;
                UnpredictedCasts++;
                return false;
            }

            string incomingRuntimeActionId =
                CombatActionIds.ResolveRuntimeActionId(conn, combatProfile, request.ActionId);
            _instance._hasPendingLocalSwing = false;

            // The authoritative impact correlates through the same instance id
            // regardless of which strike the server resolved (replacements swap
            // the animation, not the swing), so the cue ledger maps either way.
            PredictedMeleeContactCueController.MapLocalAutoSwingActionInstance(
                actionInstanceId,
                pending.CueTokenKey);

            if (!string.Equals(incomingRuntimeActionId, pending.RuntimeActionId, StringComparison.OrdinalIgnoreCase))
            {
                // Mispredicted strike identity (auto-attack replacement): the
                // authoritative animation must play; it preempts the local swing
                // through the normal playback policy.
                MismatchedCasts++;
                ActionBarTrace.Trace(
                    $"[AA_SCHED] auto cast strike mismatch local={pending.RuntimeActionId} auth={incomingRuntimeActionId}");
                return false;
            }

            SuppressedCasts++;
            long firedAtServerEstMs = pending.FiredAtMs + (long)Math.Round(pending.ServerMinusClientAtFireMs);
            CastAlignLastMs = firedAtServerEstMs - request.StartedAtMs;
            CastAlignMaxAbsMs = Math.Max(CastAlignMaxAbsMs, Math.Abs(CastAlignLastMs));
            ActionBarTrace.Trace(
                $"[AA_SCHED] suppressed duplicate auto cast strike={incomingRuntimeActionId} cast_align_ms={CastAlignLastMs}");
            return true;
        }

        internal static bool SupportsLocalPrediction(CombatAnimationSet? animationSet)
            => animationSet == null || animationSet.AutoAttackVisualSequenceActionIds.Length <= 1;

        private void ExpirePendingLocalSwing(long nowMs)
        {
            if (!_hasPendingLocalSwing || nowMs <= _pendingLocalSwing.ExpiresAtMs)
                return;

            _hasPendingLocalSwing = false;
            ExpiredWithoutCast++;
            ActionBarTrace.Trace(
                $"[AA_SCHED] local auto swing expired with no authoritative cast strike={_pendingLocalSwing.RuntimeActionId}");
        }

        private static AutoAttackCatalog? ResolveAutoAttackGameplay(DbConnection conn, AutoAttackState row)
        {
            string profile = row.CombatProfileId?.Trim().ToUpperInvariant() ?? string.Empty;
            string mode = row.ModeId?.Trim().ToUpperInvariant() ?? string.Empty;
            string action = NormalizeAuthoredActionId(row.StrikeId);
            if (profile.Length == 0 || action.Length == 0)
                return null;

            if (mode.Length > 0)
            {
                AutoAttackCatalog? modeRow = conn.Db.AutoAttackCatalog.Key.Find($"{profile}:{mode}:{action}");
                if (modeRow != null)
                    return modeRow;
            }

            return conn.Db.AutoAttackCatalog.Key.Find($"{profile}:{action}");
        }

        // Mirrors server normalize_authored_action_id (action_ids.rs).
        private static string NormalizeAuthoredActionId(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant().Replace('-', '_');

        // Mirrors is_hard_crowd_control_kind (combat.rs) — the set behind
        // has_active_disabling_status.
        private static bool HasDisablingStatusMirror(DbConnection conn, SpacetimeDB.Identity identity)
        {
            long serverNowMicros = ArenaServerClock.ServerNowMs * 1000L;
            foreach (StatusEffect effect in conn.Db.StatusEffect.Target.Filter(identity))
            {
                if (effect.ExpiresAt.MicrosecondsSinceUnixEpoch <= serverNowMicros)
                    continue;

                switch (effect.EffectKind?.Trim().ToUpperInvariant())
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

        // Mirrors is_defense_active (defense.rs).
        private static bool IsDefenseActiveMirror(DbConnection conn, SpacetimeDB.Identity identity)
        {
            DefenseState? state = conn.Db.DefenseState.Owner.Find(identity);
            if (state == null)
                return false;

            switch (state.Kind?.Trim().ToUpperInvariant())
            {
                case "BLOCK":
                case "BLOCK_COOLDOWN":
                case "PARRY":
                case "PARRY_COOLDOWN":
                    break;
                default:
                    return false;
            }

            long serverNowMicros = ArenaServerClock.ServerNowMs * 1000L;
            return serverNowMicros >= state.ActiveFrom.MicrosecondsSinceUnixEpoch
                && serverNowMicros < state.ActiveUntil.MicrosecondsSinceUnixEpoch;
        }

        private static string ScheduleKey(AutoAttackState row)
            => $"{row.NextSwingAt.MicrosecondsSinceUnixEpoch}:{row.CadenceStartedAt.MicrosecondsSinceUnixEpoch}:{row.Target}";
    }
}

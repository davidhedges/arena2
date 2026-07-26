#nullable enable
using System.Collections.Generic;
using UnityEngine;
using Arena.Network;
using Arena.Match;
using Arena.Combat;
using Arena.Presentation;
using Arena.Simulation;
using Arena.Entity;
using Arena.Debugging;
using Arena.UI;
using SpacetimeDB.Types;

namespace Arena.Input
{
    /// <summary>
    /// Owns spell-specific input behavior: aim mode, charge-on-release, and
    /// CastRequest / ReleaseCastRequest reducer calls.
    ///
    /// Client-side gates (matching TS canAttemptCast):
    ///   - GCD active → reject
    ///   - Active cast in progress → reject
    ///   - Per-spell cooldown active → reject
    ///
    /// Charge-on-release spells (INSTANT_BEAM, ELECTROCUTE):
    ///   - Key down → CastRequest (begins charge)
    ///   - Key up   → ReleaseCastRequest (releases charge)
    ///
    /// Point-targeted spells:
    ///   - Key down → enter aim mode (unlock cursor, show circle indicator)
    ///   - Mouse move → update aim point, character yaw tracks toward aim
    ///   - Left click → confirm aim, send CastRequest with aim point
    ///   - Right click → cancel aim, restore original yaw
    ///
    /// INVARIANT: Never moves the transform. Spell logic only.
    /// </summary>
    public class SpellInputHandler : MonoBehaviour
    {
        private static readonly Color AimCircleColor = new(1f, 0.02f, 0.015f, 0.72f);
        private static readonly Color AimCircleOutOfRangeColor = new(0.55f, 0.55f, 0.55f, 0.45f);

        // TS: DEFAULT_YAW_DAMPING = 12
        private const float YawDamping = 12f;
        private const long PendingInstantSpellPredictionTtlMs = 5000L;
        private const long PendingLocalSpellEventHoldMs = 250L;

        // --- Aim mode state ---
        private string? _activeAimSpell;
        private float _originalYaw;
        private float _currentYaw;
        private bool _yawLocked;
        private readonly Dictionary<string, PendingPredictedInstantSpellVisual> _pendingInstantSpellByToken = new();
        private readonly Dictionary<string, AcceptedPredictedInstantSpell> _acceptedInstantSpellByActionInstance = new();
        private readonly List<PendingAuthoritativeInstantSpellReplay> _pendingAuthoritativeInstantSpellReplays = new();
        private readonly Dictionary<string, (PredictedActionLedger ledger, long expiresAtMs)> _predictionLedgersByToken = new();

        /// <summary>
        /// True when the player is in interactive aim mode (cursor unlocked, indicator visible).
        /// Used by the local state provider to suppress movement-facing updates during aim.
        /// </summary>
        public bool IsAimActive => _activeAimSpell != null;

        /// <summary>
        /// True when aim mode is locking the character yaw.
        /// </summary>
        public bool IsYawLocked => _yawLocked;

        public bool TryCancelAimFromSecondaryWorldAction()
        {
            if (_activeAimSpell == null)
                return false;

            CancelAim();
            return true;
        }

        public static SpellInputHandler? Instance { get; private set; }

        private readonly struct PendingPredictedInstantSpellVisual
        {
            public PendingPredictedInstantSpellVisual(CastActionToken token, string spellActionId, long startedAtMs, long expiresAtMs)
            {
                Token = token;
                SpellActionId = Arena.Combat.WireIdentifier.Normalize(spellActionId);
                StartedAtMs = startedAtMs;
                ExpiresAtMs = expiresAtMs;
            }

            public CastActionToken Token { get; }
            public string SpellActionId { get; }
            public long StartedAtMs { get; }
            public long ExpiresAtMs { get; }
        }

        private readonly struct AcceptedPredictedInstantSpell
        {
            public AcceptedPredictedInstantSpell(string tokenKey, long expiresAtMs)
            {
                TokenKey = tokenKey ?? string.Empty;
                ExpiresAtMs = expiresAtMs;
            }

            public string TokenKey { get; }
            public long ExpiresAtMs { get; }
        }

        private readonly struct PendingAuthoritativeInstantSpellReplay
        {
            public PendingAuthoritativeInstantSpellReplay(string actionInstanceId, CombatAnimationRequest request, long releaseAtMs)
            {
                ActionInstanceId = actionInstanceId ?? string.Empty;
                Request = request;
                ReleaseAtMs = releaseAtMs;
            }

            public string ActionInstanceId { get; }
            public CombatAnimationRequest Request { get; }
            public long ReleaseAtMs { get; }
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
            if (Instance != this)
                return;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null) return;
            ActionBarTrace.EnsureSubscribed(conn);
            PlayerEntity? localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (localPlayer != null && !localPlayer.IsDestroyed && localPlayer.IsAlive)
            {
                FlushPendingAuthoritativeInstantSpellReplays(localPlayer, nowMs);
                LocalCombatState.Instance.ReconcilePredictedResources(localPlayer);
            }
            PruneInstantSpellPredictionState(nowMs);

            LocalPlayerInputSource? input = localPlayer?.GetLocalInputSource();
            if (input == null) return;

            var aim = AimIndicator.Instance;

            // If in aim mode, handle aim-mode input
            if (_activeAimSpell != null)
            {
                UpdateAimMode(conn, aim, input);
                return;
            }

            // Normal mode: hide aim indicator when cursor is locked
            if (aim != null)
                aim.Hide();

            FixedActionDispatcher.ProcessMovementBindings(conn, input);

            bool shiftHeld = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            ActionBarInputDispatcher.ProcessSelectableBindings(conn, input, shiftHeld, this);
        }

        // ---------------------------------------------------------------
        // Aim mode — interactive point targeting
        // ---------------------------------------------------------------

        private void StartAimMode(string spellId, float aimRadius)
        {
            _activeAimSpell = spellId;

            // Unlock cursor for free aiming
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Save current yaw for potential cancel/restore
            LocalPlayerStateProvider? stateProvider = EntityRegistry.Instance?.LocalPlayerEntity?.GetLocalStateProvider();
            _originalYaw = stateProvider != null && stateProvider.HasPredictedState
                ? stateProvider.MovementFacingYaw
                : transform.eulerAngles.y * Mathf.Deg2Rad;
            _currentYaw = _originalYaw;
            _yawLocked = true;

            // Show aim indicator
            var aim = AimIndicator.Instance;
            aim?.ShowCircle(aimRadius, AimCircleColor);
        }

        private void UpdateAimMode(
            SpacetimeDB.Types.DbConnection conn,
            AimIndicator? aim,
            LocalPlayerInputSource input)
        {
            // Show aim indicator and update position
            var spellDef = GetSpellDefinition(conn, _activeAimSpell ?? string.Empty);
            if (!TryGetAimRadius(spellDef, out float aimRadius))
            {
                CancelAim();
                return;
            }
            if (aim != null)
            {
                bool hasAimPoint = aim.RefreshFromCursor(input.MousePosition);
                bool inRange = hasAimPoint && IsAimPointWithinMaxDistance(spellDef, aim.AimPoint);
                aim.ShowCircle(aimRadius, inRange ? AimCircleColor : AimCircleOutOfRangeColor);
            }

            // Escape → cancel aim
            if (input.EscapePressed)
            {
                if (RuntimeUiEscapeRouter.TryCloseTopmost())
                    return;

                CancelAim();
                RuntimeUiEscapeRouter.ConsumeEscapeThisFrame();
                return;
            }

            // Update yaw to face aim point (damped)
            if (aim != null && aim.IsActive)
            {
                var localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
                if (localPlayer != null)
                {
                    Vector3 playerPos = GetLocalPlayerPosition(localPlayer);
                    Vector3 toAim = aim.AimPoint - playerPos;
                    toAim.y = 0;

                    if (toAim.sqrMagnitude > 0.0001f)
                    {
                        float targetYaw = Mathf.Atan2(toAim.x, toAim.z);
                        float t = 1f - Mathf.Exp(-YawDamping * Time.deltaTime);
                        _currentYaw = LerpAngle(_currentYaw, targetYaw, t);

                        localPlayer.GetLocalStateProvider()?.SetAimYaw(_currentYaw);
                    }
                }
            }

            // Left click → confirm aim
            if (input.LeftMousePressed)
            {
                ConfirmAim(conn, aim);
                return;
            }
        }

        private void ConfirmAim(SpacetimeDB.Types.DbConnection conn, AimIndicator? aim)
        {
            if (_activeAimSpell == null) return;

            string spellId = _activeAimSpell;
            var spellDef = GetSpellDefinition(conn, spellId);
            bool usesGlobalCooldown = UsesGlobalCooldown(conn, spellId);

            // Re-check cast gates (can change while aiming)
            if (MatchController.Instance?.CanCast == false) { FinishAimMode(); RestoreCursorState(); return; }
            if (!CanAttemptCast(
                usesGlobalCooldown,
                RequiresStationaryLocalCastGate(spellDef)))
            {
                FinishAimMode();
                RestoreCursorState();
                return;
            }

            // Check per-spell cooldown
            var combat = LocalCombatState.Instance;
            if (combat.SpellCooldowns.TryGetValue(spellId, out var cd))
            {
                long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (nowMs < cd.lastCastMs + cd.durationMs)
                {
                    FinishAimMode();
                    RestoreCursorState();
                    return;
                }
            }

            // Send cast with aim point
            float aimX, aimY, aimZ;
            if (aim != null && aim.IsActive)
            {
                aimX = aim.AimPoint.x;
                aimY = aim.AimPoint.y;
                aimZ = aim.AimPoint.z;
            }
            else
            {
                // Fallback: cast forward from player
                var fallbackPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
                if (fallbackPlayer != null)
                {
                    Vector3 pos = GetLocalPlayerPosition(fallbackPlayer);
                    float yaw = _currentYaw;
                    aimX = pos.x + Mathf.Sin(yaw) * 15f;
                    aimY = 0f;
                    aimZ = pos.z + Mathf.Cos(yaw) * 15f;
                }
                else
                {
                    aimX = aimY = aimZ = 0f;
                }
            }

            var aimPoint = new Vector3(aimX, aimY, aimZ);
            if (!IsAimPointWithinMaxDistance(spellDef, aimPoint))
            {
                ActionBarTrace.Trace($"spell dispatch rejected: aim point out of range for {spellId}");
                return;
            }

            PlayerEntity? localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
            if (localPlayer == null || !HasResourceForSpell(conn, localPlayer, spellId, spellDef))
            {
                FinishAimMode();
                aim?.Hide();
                RestoreCursorState();
                return;
            }

            FinishAimMode();
            aim?.Hide();
            RestoreCursorState();

            // Release aim yaw override so movement resumes owning facing.
            EntityRegistry.Instance?.LocalPlayerEntity?.GetLocalStateProvider()?.ClearAimYaw();

            string targetId = TargetSelector.Instance?.SelectedTargetId ?? "";
            CastActionToken token = LocalCombatState.Instance.CreateCastActionToken(spellId);
            SendCastRequest(conn, spellId, targetId, aimX, aimY, aimZ, token);
            RecordPredictedSpellStart(
                conn,
                localPlayer,
                spellId,
                spellDef,
                token,
                System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            PredictCastTimeSpellHold(spellId, spellDef, targetId, aimPoint, token);
            PredictImmediateInstantSpellVisual(conn, spellId, spellDef, targetId, aimPoint, token);
        }

        private void FinishAimMode()
        {
            _activeAimSpell = null;
            _yawLocked = false;
        }

        private void CancelAim()
        {
            _activeAimSpell = null;
            _yawLocked = false;

            transform.rotation = Quaternion.Euler(0f, _originalYaw * Mathf.Rad2Deg, 0f);
            EntityRegistry.Instance?.LocalPlayerEntity?.GetLocalStateProvider()?.SetAimYaw(_originalYaw);

            AimIndicator.Instance?.Hide();
            RestoreCursorState();

            // Release aim yaw override so movement resumes owning facing.
            EntityRegistry.Instance?.LocalPlayerEntity?.GetLocalStateProvider()?.ClearAimYaw();
        }

        private void RestoreCursorState()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private static Vector3 GetLocalPlayerPosition(PlayerEntity localPlayer)
        {
            LocalPlayerStateProvider? stateProvider = localPlayer.GetLocalStateProvider();
            if (stateProvider != null && stateProvider.HasPredictedState)
                return stateProvider.PredictedPosition;

            return localPlayer.GameObject.transform.position;
        }

        // ---------------------------------------------------------------
        // Spell trigger (non-aim)
        // ---------------------------------------------------------------

        public bool TryTriggerSpellFromActionBar(
            SpacetimeDB.Types.DbConnection conn,
            string spellId)
        {
            if (_activeAimSpell != null)
                return false;

            return TryTriggerSpell(conn, spellId, AimIndicator.Instance);
        }

        public bool TryTriggerMovementFromActionBar(
            SpacetimeDB.Types.DbConnection conn,
            Arena.Combat.ActiveActionBarAction action)
        {
            if (_activeAimSpell != null)
                return false;

            if (MatchController.Instance?.CanCast == false)
            {
                ActionBarTrace.Trace($"movement dispatch blocked by MatchController for {action.ActionId}");
                return false;
            }
            if (!CanAttemptCast(usesGlobalCooldown: true))
            {
                ActionBarTrace.Trace($"movement dispatch rejected by local cast gate for {action.ActionId}");
                return false;
            }

            var combat = LocalCombatState.Instance;
            if (combat.SpellCooldowns.TryGetValue(action.ActionId, out var cd))
            {
                long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (nowMs < cd.lastCastMs + cd.durationMs)
                {
                    ActionBarTrace.Trace($"movement dispatch rejected by cooldown for {action.ActionId}");
                    return false;
                }
            }

            PlayerEntity? localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
            if (localPlayer == null)
            {
                ActionBarTrace.Trace($"movement dispatch rejected: no local player for {action.ActionId}");
                return false;
            }
            if (!localPlayer.IsInCombat)
                localPlayer.EnterCombatImmediate();

            string targetId = TargetSelector.Instance?.SelectedTargetId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetId))
            {
                ActionBarTrace.Trace($"movement dispatch rejected: no selected target for {action.ActionId}");
                return false;
            }
            ActionBarTrace.Trace($"movement dispatch sending CastRequest for {action.ActionId} target={targetId}");
            CastActionToken token = LocalCombatState.Instance.CreateCastActionToken(action.ActionId);
            SendCastRequest(conn, action.ActionId, targetId, 0f, 0f, 0f, token);
            return true;
        }

        private bool TryTriggerSpell(SpacetimeDB.Types.DbConnection conn, string spellId, AimIndicator? aim)
        {
            if (MatchController.Instance?.CanCast == false)
            {
                ActionBarTrace.Trace($"spell dispatch blocked by MatchController for {spellId}");
                return false;
            }
            bool usesGlobalCooldown = UsesGlobalCooldown(conn, spellId);
            var spellDef = GetSpellDefinition(conn, spellId);
            PlayerEntity? localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
            if (localPlayer == null)
            {
                ActionBarTrace.Trace($"spell dispatch rejected: no local player for {spellId}");
                return false;
            }
            // TS: canAttemptCast() — client-side gates for snappy rejection.
            if (!CanAttemptCast(
                usesGlobalCooldown,
                RequiresStationaryLocalCastGate(spellDef)))
            {
                ActionBarTrace.Trace($"spell dispatch rejected by local cast gate for {spellId}");
                return false;
            }

            // TS: per-spell cooldown check via cooldowns.isUsable(kind)
            var combat = LocalCombatState.Instance;
            if (combat.SpellCooldowns.TryGetValue(spellId, out var cd))
            {
                long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (nowMs < cd.lastCastMs + cd.durationMs)
                {
                    ActionBarTrace.Trace($"spell dispatch rejected by cooldown for {spellId}");
                    return false;
                }
            }

            if (!HasResourceForSpell(conn, localPlayer, spellId, spellDef))
                return false;

            if (spellDef == null)
            {
                ActionBarTrace.Trace($"spell dispatch waiting for definition for {spellId}");
                return false;
            }
            if (SpellDefinitionContracts.UsesPointTargeting(spellDef))
            {
                if (!TryGetAimRadius(spellDef, out float aimRadius))
                {
                    ActionBarTrace.Trace($"spell dispatch waiting for aim radius definition for {spellId}");
                    return false;
                }
                // Enter interactive aim mode instead of instant cast
                ActionBarTrace.Trace($"spell dispatch entered aim mode for {spellId}");
                StartAimMode(spellId, aimRadius);
                return true;
            }

            ActionBarTrace.Trace($"spell dispatch sending cast request for {spellId}");
            return TryCastTargeted(conn, spellId);
        }

        private bool TryCastTargeted(SpacetimeDB.Types.DbConnection conn, string spellId)
        {
            string targetId = TargetSelector.Instance?.SelectedTargetId ?? "";
            var spellDef = GetSpellDefinition(conn, spellId);
            if (string.IsNullOrEmpty(targetId)
                && spellDef?.RequiresTarget == true
                && PartyRelationship.TargetAudienceAllowsSelf(spellDef.TargetAudience))
            {
                targetId = EntityRegistry.Instance?.LocalPlayerEntity?.Identity.ToString() ?? "";
                ActionBarTrace.Trace($"spell dispatch defaulted {spellId} target to self");
            }
            if (string.IsNullOrEmpty(targetId) && spellDef?.RequiresTarget == true)
            {
                Debug.LogWarning($"[SpellInput] {spellId} — no target selected (use Tab or click a target)");
                ActionBarTrace.Trace($"spell dispatch rejected: no target selected for {spellId}");
                return false;
            }

            // Advisory LOS pre-check (netcode design review S4): same rule and
            // reason text as the server's targeting check, denied before the
            // round trip. Permissive by construction; the server stays
            // authoritative for any press that goes through.
            if (spellDef is { RequiresTarget: true, RequiresTargetLos: true })
            {
                PlayerEntity? losLocal = EntityRegistry.Instance?.LocalPlayerEntity;
                ICombatTargetEntity? losTarget = TargetSelector.Instance?.SelectedTarget;
                if (losLocal != null
                    && losTarget != null
                    && AdvisoryTargetLineOfSight.IsPressBlocked(losLocal, losTarget))
                {
                    ActionBarTrace.Trace($"spell denied locally: {spellId} advisory line of sight blocked");
                    LocalCombatState.NotifyLocalAdvisoryDenial(
                        spellId,
                        spellId,
                        SpacetimeDB.Types.ActionRejectReason.LineOfSightBlocked);
                    return false;
                }
            }

            ActionBarTrace.Trace($"sending CastRequest for {spellId} target={targetId}");
            CastActionToken token = LocalCombatState.Instance.CreateCastActionToken(spellId);
            SendCastRequest(conn, spellId, targetId, 0f, 0f, 0f, token);
            RecordPredictedSpellStart(
                conn,
                EntityRegistry.Instance?.LocalPlayerEntity,
                spellId,
                spellDef,
                token,
                System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            PredictCastTimeSpellHold(spellId, spellDef, targetId, null, token);
            PredictImmediateInstantSpellVisual(conn, spellId, spellDef, targetId, null, token);
            return true;
        }

        private static float SpellPrimaryResourceCost(SpacetimeDB.Types.SpellDefinition? spellDef)
            => spellDef == null ? 0f : Mathf.Max(0f, spellDef.PrimaryResourceCost);

        private static float SpellStartResourceCost(SpacetimeDB.Types.SpellDefinition? spellDef)
        {
            if (spellDef == null)
                return 0f;

            float cost = SpellPrimaryResourceCost(spellDef);
            if (SpellDefinitionContracts.UsesPerSecondResourceCost(spellDef))
                return Mathf.Max(0f, cost * Mathf.Max(0f, spellDef.UpdateInterval));

            return cost;
        }

        private static string SpellResourceKind(
            SpacetimeDB.Types.DbConnection conn,
            PlayerEntity entity,
            string spellId)
        {
            ActiveActionBarAction action =
                ActiveActionBarResolver.ResolveActiveSelectableActionForAction(conn, entity.Identity, spellId);
            return string.IsNullOrWhiteSpace(action.ResourceKind)
                ? entity.PrimaryResourceKind
                : action.ResourceKind.Trim().ToUpperInvariant();
        }

        private bool HasResourceForSpell(
            SpacetimeDB.Types.DbConnection conn,
            PlayerEntity entity,
            string spellId,
            SpacetimeDB.Types.SpellDefinition? spellDef)
        {
            if (IsActiveEmanationToggleOff(conn, entity.Identity, spellId, spellDef))
                return true;

            float cost = SpellStartResourceCost(spellDef);
            if (cost <= 0.001f)
                return true;

            string requiredKind = SpellResourceKind(conn, entity, spellId);
            float available = LocalCombatState.Instance.EffectiveCurrentResource(entity, requiredKind);
            if (available + 0.001f < cost)
            {
                ActionBarTrace.Trace(
                    $"spell rejected: {spellId} requires {cost:F0} {requiredKind} ({available:F0} available)");
                LocalCombatState.NotifyLocalAdvisoryDenial(
                    spellId,
                    spellId,
                    SpacetimeDB.Types.ActionRejectReason.InsufficientResource);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Applies press-time predictions through the prediction ledger so a
        /// server Rejected/StaleToken can restore them. Normal cast-time spells
        /// intentionally do not reserve resource here; the server commits cost
        /// only when the active cast reaches release.
        /// </summary>
        private void RecordPredictedSpellStart(
            SpacetimeDB.Types.DbConnection conn,
            PlayerEntity? localPlayer,
            string spellId,
            SpacetimeDB.Types.SpellDefinition? spellDef,
            CastActionToken token,
            long nowMs)
        {
            if (spellDef == null)
                return;

            // Resource kind comes from the authored AbilityCatalog row like
            // melee (netcode audit R2b); the server resolves the same catalog
            // kind in resolve_ability_action_resource_cost_amount.
            string resourceKind = localPlayer != null
                ? SpellResourceKind(conn, localPlayer, spellId)
                : "MANA";
            bool togglingOff = localPlayer != null
                && IsActiveEmanationToggleOff(conn, localPlayer.Identity, spellId, spellDef);
            PredictedActionLedger ledger = LocalCombatState.Instance.PredictActionStart(
                localPlayer,
                spellId,
                (long)spellDef.CooldownMs,
                spellDef.UsesGlobalCooldown,
                GameplayTuning.ResolveDefaultGlobalCooldownDurationMs(conn),
                resourceKind,
                ShouldReserveResourceAtCastStart(spellDef) && !togglingOff
                    ? SpellStartResourceCost(spellDef)
                    : 0f,
                nowMs);
            if (token.IsPredicted)
                _predictionLedgersByToken[SpellTokenKey(token)] = (ledger, nowMs + PendingInstantSpellPredictionTtlMs);
        }

        private static bool ShouldReserveResourceAtCastStart(SpacetimeDB.Types.SpellDefinition spellDef)
            => spellDef.CastTimeMs == 0UL;

        private static bool IsActiveEmanationToggleOff(
            SpacetimeDB.Types.DbConnection conn,
            SpacetimeDB.Identity owner,
            string spellId,
            SpacetimeDB.Types.SpellDefinition? spellDef)
        {
            if (spellDef == null
                || !string.Equals(
                    spellDef.Behavior,
                    SpellDefinitionContracts.BehaviorEmanation,
                    System.StringComparison.Ordinal))
            {
                return false;
            }

            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            foreach (SpacetimeDB.Types.ActiveRadialEffect row in conn.Db.ActiveRadialEffect.Owner.Filter(owner))
            {
                if (string.Equals(
                    WireIdentifier.Normalize(row.SpellId),
                    normalizedSpellId,
                    System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void PredictCastTimeSpellHold(
            string spellId,
            SpacetimeDB.Types.SpellDefinition? spellDef,
            string targetId,
            Vector3? aimPoint,
            CastActionToken token)
        {
            if (spellDef == null)
            {
                return;
            }

            PlayerEntity? localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
            if (localPlayer == null)
                return;

            bool useCastTimeHold = spellDef.CastTimeMs > 0UL
                && !SpellDefinitionContracts.CastsOnRelease(spellDef);
            bool useAuthoredHold = localPlayer.UsesSpellCastHoldPresentation(spellId);
            if (!useCastTimeHold && !useAuthoredHold)
                return;

            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (spellDef.CastTimeMs > 0UL)
                LocalCombatState.Instance.PredictCastBar(spellId, nowMs, (long)spellDef.CastTimeMs, token);

            localPlayer.PredictSpellCastHold(spellId, nowMs, targetId, aimPoint, token);
            ActionBarTrace.Trace($"predicted local cast hold for {spellId}");
        }

        private static void PredictImmediateInstantSpellVisual(
            SpacetimeDB.Types.DbConnection conn,
            string spellId,
            SpacetimeDB.Types.SpellDefinition? spellDef,
            string targetId,
            Vector3? aimPoint,
            CastActionToken token)
        {
            if (spellDef == null
                || spellDef.CastTimeMs > 0
                || SpellDefinitionContracts.CastsOnRelease(spellDef))
            {
                return;
            }
            if (spellDef.RequiresTarget && string.IsNullOrWhiteSpace(targetId))
                return;

            PlayerEntity? localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
            if (localPlayer == null)
                return;

            // HoldOnly channel spells are not instant spells: their presentation is owned
            // by the cast-hold state machine (enter/idle loop until the ActiveCast ends) and
            // they never play a release. Do not remember a pending instant visual or fire a
            // predicted release for them — that machinery schedules an authoritative replay
            // that preempts the hold. HoldThenRelease spells keep the instant-replay path
            // (their COMBAT_CAST drives the release), so gate on HoldOnly specifically.
            if (!localPlayer.PlaysSpellReleasePresentation(spellId))
                return;

            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Instance?.RememberPredictedInstantSpellVisual(token, spellId, nowMs);
            localPlayer.RequestCombatAnimation(
                CombatAnimationRequest.PredictedSpell(
                    spellId,
                    nowMs,
                    CombatEventSources.Spell));
            CombatVFXDispatcher.PredictLocalInstantSpellRelease(
                conn,
                spellId,
                spellDef,
                targetId,
                aimPoint,
                token);
            ActionBarTrace.Trace($"predicted local instant spell animation for {spellId}");
        }

        public bool HandleAuthoritativeLocalSpellReplay(
            PlayerEntity entity,
            string actionInstanceId,
            in CombatAnimationRequest request,
            long nowMs)
        {
            if (!string.Equals(request.Source, CombatEventSources.Spell, System.StringComparison.Ordinal))
                return false;

            if (!string.IsNullOrWhiteSpace(actionInstanceId)
                && _acceptedInstantSpellByActionInstance.Remove(actionInstanceId))
            {
                return true;
            }

            string normalizedActionId = Arena.Combat.WireIdentifier.Normalize(request.ActionId);
            if (!string.IsNullOrWhiteSpace(normalizedActionId)
                && HasPendingInstantSpellForAction(normalizedActionId, nowMs))
            {
                _pendingAuthoritativeInstantSpellReplays.Add(new PendingAuthoritativeInstantSpellReplay(
                    actionInstanceId,
                    request,
                    nowMs + PendingLocalSpellEventHoldMs));
                return true;
            }

            return false;
        }

        public void OnPredictedActionResultInsert(PredictedActionResult row)
        {
            if (row.Family != PredictedActionFamily.SpellCast)
                return;

            ActionBarTrace.Trace(
                $"spell server result {row.Result} reason={row.RejectReason} predicted={row.PredictedActionId}:{row.ClientActionSeq} action={row.ActionInstanceId}");

            string tokenKey = SpellTokenKey(row.PredictedActionId, row.ClientActionSeq);
            if (row.Result == ActionResultKind.Accepted)
            {
                _predictionLedgersByToken.Remove(tokenKey);
                if (_pendingInstantSpellByToken.Remove(tokenKey)
                    && !string.IsNullOrWhiteSpace(row.ActionInstanceId))
                {
                    long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _acceptedInstantSpellByActionInstance[row.ActionInstanceId] =
                        new AcceptedPredictedInstantSpell(tokenKey, nowMs + PendingInstantSpellPredictionTtlMs);
                    RemovePendingAuthoritativeInstantSpellReplay(row.ActionInstanceId);
                }
                return;
            }

            if ((row.Result == ActionResultKind.Rejected || row.Result == ActionResultKind.StaleToken)
                && _predictionLedgersByToken.TryGetValue(tokenKey, out var pendingLedger))
            {
                LocalCombatState.Instance.RollbackPrediction(pendingLedger.ledger, row.RejectReason);
                ActionBarTrace.Trace(
                    $"rolled back predicted spell state for {pendingLedger.ledger.ActionKind} after {row.Result} reason={row.RejectReason}");
            }

            // Reject = interrupt, never completion (netcode design review S2):
            // cut the predicted instant-spell animation via the shared
            // preemption primitives. Cast-time holds already cancel through the
            // spell presentation state machine; StaleToken stays excluded there
            // and here — a newer press of the same action owns the presentation.
            if (row.Result == ActionResultKind.Rejected
                && _pendingInstantSpellByToken.TryGetValue(tokenKey, out var pendingInstant))
            {
                EntityRegistry.Instance?.LocalPlayerEntity?.CutRejectedActionPresentation(
                    pendingInstant.SpellActionId);
                ActionBarTrace.Trace(
                    $"cut rejected predicted instant spell presentation for {pendingInstant.SpellActionId} reason={row.RejectReason}");
            }

            _predictionLedgersByToken.Remove(tokenKey);
            _pendingInstantSpellByToken.Remove(tokenKey);
        }

        public static void OnPredictedActionResultInsert(EventContext ctx, PredictedActionResult row)
        {
            _ = ctx;
            Instance?.OnPredictedActionResultInsert(row);
        }

        private void RememberPredictedInstantSpellVisual(CastActionToken token, string spellId, long startedAtMs)
        {
            if (!token.IsPredicted)
                return;

            string normalizedSpellId = Arena.Combat.WireIdentifier.Normalize(spellId);
            if (string.IsNullOrWhiteSpace(normalizedSpellId))
                return;

            _pendingInstantSpellByToken[SpellTokenKey(token)] = new PendingPredictedInstantSpellVisual(
                token,
                normalizedSpellId,
                startedAtMs,
                startedAtMs + PendingInstantSpellPredictionTtlMs);
        }

        private bool HasPendingInstantSpellForAction(string normalizedActionId, long nowMs)
        {
            foreach (PendingPredictedInstantSpellVisual pending in _pendingInstantSpellByToken.Values)
            {
                if (nowMs <= pending.ExpiresAtMs
                    && string.Equals(pending.SpellActionId, normalizedActionId, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private void FlushPendingAuthoritativeInstantSpellReplays(PlayerEntity entity, long nowMs)
        {
            for (int i = _pendingAuthoritativeInstantSpellReplays.Count - 1; i >= 0; i--)
            {
                PendingAuthoritativeInstantSpellReplay pending = _pendingAuthoritativeInstantSpellReplays[i];
                if (!string.IsNullOrWhiteSpace(pending.ActionInstanceId)
                    && _acceptedInstantSpellByActionInstance.Remove(pending.ActionInstanceId))
                {
                    _pendingAuthoritativeInstantSpellReplays.RemoveAt(i);
                    continue;
                }

                if (nowMs < pending.ReleaseAtMs)
                    continue;

                _pendingAuthoritativeInstantSpellReplays.RemoveAt(i);
                entity.RequestCombatAnimation(pending.Request);
            }
        }

        private void RemovePendingAuthoritativeInstantSpellReplay(string actionInstanceId)
        {
            if (string.IsNullOrWhiteSpace(actionInstanceId))
                return;

            for (int i = _pendingAuthoritativeInstantSpellReplays.Count - 1; i >= 0; i--)
            {
                if (string.Equals(
                    _pendingAuthoritativeInstantSpellReplays[i].ActionInstanceId,
                    actionInstanceId,
                    System.StringComparison.Ordinal))
                {
                    _pendingAuthoritativeInstantSpellReplays.RemoveAt(i);
                }
            }
        }

        private void PruneInstantSpellPredictionState(long nowMs)
        {
            var staleTokens = new List<string>();
            foreach (var entry in _pendingInstantSpellByToken)
            {
                if (nowMs > entry.Value.ExpiresAtMs)
                    staleTokens.Add(entry.Key);
            }
            foreach (string token in staleTokens)
                _pendingInstantSpellByToken.Remove(token);

            var staleActionInstances = new List<string>();
            foreach (var entry in _acceptedInstantSpellByActionInstance)
            {
                if (nowMs > entry.Value.ExpiresAtMs)
                    staleActionInstances.Add(entry.Key);
            }
            foreach (string actionInstanceId in staleActionInstances)
                _acceptedInstantSpellByActionInstance.Remove(actionInstanceId);

            var staleLedgerTokens = new List<string>();
            foreach (var entry in _predictionLedgersByToken)
            {
                if (nowMs > entry.Value.expiresAtMs)
                    staleLedgerTokens.Add(entry.Key);
            }
            foreach (string token in staleLedgerTokens)
                _predictionLedgersByToken.Remove(token);
        }

        private static string SpellTokenKey(CastActionToken token)
            => SpellTokenKey(token.PredictedCastId, token.ClientActionSeq);

        private static string SpellTokenKey(string predictedCastId, ulong clientActionSeq)
            => $"{predictedCastId}:{clientActionSeq}";

        private void SendCastRequest(
            SpacetimeDB.Types.DbConnection conn,
            string spellId,
            string targetId,
            float aimX,
            float aimY,
            float aimZ,
            CastActionToken token)
        {
            uint castInputTick = 0;
            float castPosX = transform.position.x;
            float castPosY = transform.position.y;
            float castPosZ = transform.position.z;
            float castYaw = transform.eulerAngles.y * Mathf.Deg2Rad;

            var localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
            LocalPlayerStateProvider? stateProvider = localPlayer?.GetLocalStateProvider();
            if (stateProvider != null && stateProvider.HasPredictedState)
            {
                Vector3 predictedPosition = stateProvider.PredictedPosition;
                castInputTick = stateProvider.LastProcessedTick;
                castPosX = predictedPosition.x;
                castPosY = predictedPosition.y;
                castPosZ = predictedPosition.z;
                castYaw = stateProvider.MovementFacingYaw;
            }
            LocalMovementPredictionDriver? movementDriver =
                localPlayer?.GameObject.GetComponent<LocalMovementPredictionDriver>();
            if (movementDriver != null)
                castInputTick = movementDriver.NextMovementContextProposalTick;

            // S8 attacker-view report: for a targeted press, only when the
            // pressed target is the selected entity whose rendered (delayed)
            // pose the player was judging. S10 (G2): a no-target (area) cast
            // reports the shared per-connection delay instead, so cone/radius
            // sweep membership rewinds too. A non-selected targeted cast still
            // reports 0 (S8 contract untouched).
            var selectedTarget = TargetSelector.Instance?.SelectedTarget;
            ulong viewServerTimeMs;
            if (!string.IsNullOrEmpty(targetId)
                && selectedTarget != null
                && selectedTarget.TargetIdentity.ToString() == targetId)
            {
                viewServerTimeMs = AttackerViewTime.ViewServerTimeMsFor(selectedTarget);
            }
            else if (string.IsNullOrEmpty(targetId))
            {
                viewServerTimeMs = AttackerViewTime.ViewServerTimeMsForConnection();
            }
            else
            {
                viewServerTimeMs = 0UL;
            }

            conn.Reducers.CastRequest(
                spellId,
                targetId,
                aimX,
                aimY,
                aimZ,
                castInputTick,
                castPosX,
                castPosY,
                castPosZ,
                castYaw,
                token.PredictedCastId,
                token.ClientActionSeq,
                viewServerTimeMs);
        }

        /// <summary>
        /// TS: canAttemptCast() — checks GCD and active cast.
        /// </summary>
        private static bool RequiresStationaryLocalCastGate(SpacetimeDB.Types.SpellDefinition? spellDef)
        {
            return spellDef != null
                && spellDef.CastTimeMs > 0UL
                && !SpellDefinitionContracts.CastsOnRelease(spellDef);
        }

        private bool CanAttemptCast(bool usesGlobalCooldown, bool requireStationary = false)
        {
            var combat = LocalCombatState.Instance;
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // GCD check
            if (usesGlobalCooldown && combat.IsGlobalCooldownActive(nowMs))
                return false;

            if (combat.CurrentCastBar(nowMs).HasValue)
                return false;

            // Active cast check (can't cast while already casting)
            if (combat.ActiveCast != null)
            {
                if (nowMs < combat.ActiveCast.Value.endMs)
                    return false;
            }

            if (combat.MovementAction != null)
            {
                if (nowMs < combat.MovementAction.Value.recoveryUntilMs)
                    return false;
            }

            if (requireStationary)
            {
                LocalPlayerInputSource? input = EntityRegistry.Instance?.LocalPlayerEntity?.GetLocalInputSource();
                if (input != null && (input.Move.sqrMagnitude > 0.0001f || input.JumpPressed))
                    return false;
            }

            return true;
        }

        private static bool UsesGlobalCooldown(SpacetimeDB.Types.DbConnection conn, string spellId)
        {
            return GetSpellDefinition(conn, spellId)?.UsesGlobalCooldown ?? true;
        }

        private static SpacetimeDB.Types.SpellDefinition? GetSpellDefinition(
            SpacetimeDB.Types.DbConnection conn,
            string spellId)
        {
            return conn.Db.SpellDefinition.Kind.Find(spellId);
        }

        private static bool TryGetAimRadius(
            SpacetimeDB.Types.DbConnection conn,
            string? spellId,
            out float aimRadius)
        {
            aimRadius = 0f;
            if (string.IsNullOrWhiteSpace(spellId))
                return false;

            return TryGetAimRadius(GetSpellDefinition(conn, spellId), out aimRadius);
        }

        private static bool TryGetAimRadius(
            SpacetimeDB.Types.SpellDefinition? spellDef,
            out float aimRadius)
        {
            aimRadius = 0f;
            if (spellDef == null || !spellDef.HasAimRadius || spellDef.AimRadius < 0f)
                return false;

            aimRadius = spellDef.AimRadius;
            return true;
        }

        private static bool IsAimPointWithinMaxDistance(
            SpacetimeDB.Types.SpellDefinition? spellDef,
            Vector3 aimPoint)
        {
            if (spellDef == null)
                return false;
            if (!SpellDefinitionContracts.UsesPointTargeting(spellDef))
                return true;
            if (spellDef.MaxDistance <= 0f)
                return true;

            PlayerEntity? localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
            if (localPlayer == null)
                return false;

            Vector3 playerPos = GetLocalPlayerPosition(localPlayer);
            float dx = aimPoint.x - playerPos.x;
            float dz = aimPoint.z - playerPos.z;
            float maxDistance = spellDef.MaxDistance + 0.001f;
            return dx * dx + dz * dz <= maxDistance * maxDistance;
        }

        private static float LerpAngle(float a, float b, float t)
        {
            float diff = b - a;
            while (diff > Mathf.PI) diff -= Mathf.PI * 2f;
            while (diff < -Mathf.PI) diff += Mathf.PI * 2f;
            return a + diff * t;
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Arena.Debugging;
#endif
using Arena.Entity;
using Arena.Input;
using Arena.Network;
using Arena.Presentation.VFX;
using Arena.Simulation;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    public sealed class CombatVFXDispatcher : MonoBehaviour
    {
        private const string TriggerMeleeCast = "MELEE_CAST";
        private const string TriggerMeleeImpact = "MELEE_IMPACT";
        private const string TriggerMeleeBlock = "MELEE_BLOCK";
        private const string TriggerMeleeParry = "MELEE_PARRY";
        private const string TriggerAreaImpact = "AREA_IMPACT";
        private const string TriggerSpellCast = "SPELL_CAST";
        private const string TriggerSpellRelease = "SPELL_RELEASE";
        private const string TriggerSpellImpact = "SPELL_IMPACT";
        private const string TriggerSpellBlock = "SPELL_BLOCK";
        private const string TriggerSpellParry = "SPELL_PARRY";
        private const string TriggerSpellFizzle = "SPELL_FIZZLE";
        private const string TriggerStatusActive = "STATUS_ACTIVE";
        private const string TriggerStatusEnd = "STATUS_END";
        private const string TriggerEmanationActive = "EMANATION_ACTIVE";
        private const string TriggerEmanationMaxStacks = "EMANATION_MAX_STACKS";
        private const string TriggerSpecialMovementStart = "SPECIAL_MOVEMENT_START";
        private const string TriggerSpecialMovementArrival = "SPECIAL_MOVEMENT_ARRIVAL";
        private const string FlamethrowerChannelVfxId = "VFX_FLAMETHROWER_CHANNEL_01";
        private const string LingeringShadeReturnMovementKind = "LINGERING_SHADE_RETURN";
        private const string LingeringShadeAbilityId = "SUBTLETY_LINGERING_SHADE";
        private const string AttachModeSpawnWorld = "SPAWN_WORLD";
        private const string AttachModeFollowAnchor = "FOLLOW_ANCHOR";
        private const string AttachModeFollowGroundPosition = "FOLLOW_GROUND_POSITION";
        private const string AttachModeWorldAlignedToFacing = "WORLD_ALIGNED_TO_FACING";
        private const string VfxRoleProjectileBody = "PROJECTILE_BODY";
        private const string VfxRoleProjectileTrail = "PROJECTILE_TRAIL";
        private const string VfxRoleTravelBody = "TRAVEL_BODY";
        private const string OwnerKindAbility = "ABILITY";
        private const string OwnerKindSpell = "SPELL";
        private const string AnchorTarget = "TARGET";
        private const string AnchorGroundUnderTarget = "GROUND_UNDER_TARGET";
        private const string AnchorLeftHand = "LEFT_HAND";
        private const string AnchorRightHand = "RIGHT_HAND";
        private const string AnchorWeaponMainHand = "WEAPON_MAIN_HAND";
        private const string AnchorWeaponOffHand = "WEAPON_OFF_HAND";
        // Forward nudge for a socket-anchored projectile launch so it reads as leaving the
        // fingertips instead of the hand bone. Tunable — raise for a longer reach in front.
        private const float HandMuzzleForwardMeters = 0.6f;
        // Upward lift so the launch sits at the palm rather than the (lower) wrist bone.
        // Tunable — raise if the origin still reads too low.
        private const float HandMuzzleUpMeters = 0.2f;
        private const string SpellBehaviorProjectile = "PROJECTILE";
        private const string SpellBehaviorArea = "AREA";
        private const string ProjectileMotionBoomerangCaster = "BOOMERANG_CASTER";
        private const string ProjectileMotionTravelingArea = "TRAVELING_AREA";
        private const long PredictedSpellVfxTtlMs = 5000L;
        private const float DefaultPlayerChestHeight = 1.15f;
        private const float PlayerChestHeightFraction = 0.68f;

        private static CombatVFXDispatcher? _instance;

        private List<CombatVfxCueCatalog>? _matchingCues;
        private CombatVfxCueResolver.Index? _cueResolver;
        private CombatProjectileVisualController? _projectileVisuals;
        private CombatTravelVisualController? _travelVisuals;
        private CombatVFXLifecycleRegistry? _lifecycle;
        private DbConnection? _subscribedConnection;
        private readonly Dictionary<string, PendingPredictedSpellVfx> _pendingSpellVfxByToken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _spellVfxTokenByActionInstance = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _projectileDeliveredSpellImpactByActionKind = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ActiveRadialEffectVfxState> _activeRadialEffectVfxByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ActivePersistentAreaVfxState> _activePersistentAreaVfxByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<ulong, ActiveStatusEffectVfxState> _activeStatusEffectVfxById = new();

        private readonly struct ActiveRadialEffectVfxState : IEquatable<ActiveRadialEffectVfxState>
        {
            public ActiveRadialEffectVfxState(string trigger, string abilityId)
            {
                Trigger = trigger;
                AbilityId = abilityId;
            }

            public string Trigger { get; }
            public string AbilityId { get; }

            public bool Equals(ActiveRadialEffectVfxState other)
                => string.Equals(Trigger, other.Trigger, StringComparison.Ordinal)
                    && string.Equals(AbilityId, other.AbilityId, StringComparison.Ordinal);
        }

        private readonly struct ActiveStatusEffectVfxState : IEquatable<ActiveStatusEffectVfxState>
        {
            public ActiveStatusEffectVfxState(
                string spellId,
                string abilityId,
                Identity target,
                uint stacks)
            {
                SpellId = spellId;
                AbilityId = abilityId;
                Target = target;
                Stacks = stacks;
            }

            public string SpellId { get; }
            public string AbilityId { get; }
            public Identity Target { get; }
            public uint Stacks { get; }

            public bool Equals(ActiveStatusEffectVfxState other)
                => MatchesExceptStacks(other) && Stacks == other.Stacks;

            /// <summary>True when a stack tick is the only thing that moved.</summary>
            public bool MatchesExceptStacks(ActiveStatusEffectVfxState other)
                => string.Equals(SpellId, other.SpellId, StringComparison.Ordinal)
                    && string.Equals(AbilityId, other.AbilityId, StringComparison.Ordinal)
                    && Target.Equals(other.Target);
        }

        private readonly struct ActivePersistentAreaVfxState : IEquatable<ActivePersistentAreaVfxState>
        {
            public ActivePersistentAreaVfxState(string spellInstanceId, string abilityId)
            {
                SpellInstanceId = spellInstanceId;
                AbilityId = abilityId;
            }

            public string SpellInstanceId { get; }
            public string AbilityId { get; }

            public bool Equals(ActivePersistentAreaVfxState other)
                => string.Equals(SpellInstanceId, other.SpellInstanceId, StringComparison.Ordinal)
                    && string.Equals(AbilityId, other.AbilityId, StringComparison.Ordinal);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            CombatVFXDispatcher? existing = FindAnyObjectByType<CombatVFXDispatcher>();
            if (existing != null)
            {
                _instance = existing;
                DontDestroyOnLoad(existing.gameObject);
                return;
            }

            var go = new GameObject("CombatVFXDispatcher");
            DontDestroyOnLoad(go);
            go.AddComponent<CombatVFXDispatcher>();
        }

        private List<CombatVfxCueCatalog> MatchingCues => _matchingCues ??= new List<CombatVfxCueCatalog>();
        private CombatVfxCueResolver.Index CueResolver => _cueResolver ??= new CombatVfxCueResolver.Index();
        private CombatProjectileVisualController ProjectileVisuals => _projectileVisuals ??= new CombatProjectileVisualController();
        private CombatTravelVisualController TravelVisuals => _travelVisuals ??= new CombatTravelVisualController();

        private readonly struct PendingPredictedSpellVfx
        {
            public PendingPredictedSpellVfx(
                string tokenKey,
                string spellId,
                string predictedActionInstanceId,
                string predictedProjectileKey,
                bool suppressAuthoritativeRelease,
                bool suppressAuthoritativeAreaImpact,
                long expiresAtMs)
            {
                TokenKey = tokenKey;
                SpellId = WireIdentifier.Normalize(spellId);
                PredictedActionInstanceId = predictedActionInstanceId;
                PredictedProjectileKey = predictedProjectileKey;
                SuppressAuthoritativeRelease = suppressAuthoritativeRelease;
                SuppressAuthoritativeAreaImpact = suppressAuthoritativeAreaImpact;
                ExpiresAtMs = expiresAtMs;
            }

            public string TokenKey { get; }
            public string SpellId { get; }
            public string PredictedActionInstanceId { get; }
            public string PredictedProjectileKey { get; }
            public bool SuppressAuthoritativeRelease { get; }
            public bool SuppressAuthoritativeAreaImpact { get; }
            public long ExpiresAtMs { get; }

            public PendingPredictedSpellVfx WithProjectileKey(string predictedProjectileKey)
            {
                return new PendingPredictedSpellVfx(
                    TokenKey,
                    SpellId,
                    PredictedActionInstanceId,
                    predictedProjectileKey,
                    SuppressAuthoritativeRelease,
                    SuppressAuthoritativeAreaImpact,
                    ExpiresAtMs);
            }

            public PendingPredictedSpellVfx WithSuppressedAuthoritativeTrigger(string trigger)
            {
                return new PendingPredictedSpellVfx(
                    TokenKey,
                    SpellId,
                    PredictedActionInstanceId,
                    PredictedProjectileKey,
                    SuppressAuthoritativeRelease
                        || string.Equals(trigger, TriggerSpellRelease, StringComparison.Ordinal),
                    SuppressAuthoritativeAreaImpact
                        || string.Equals(trigger, TriggerAreaImpact, StringComparison.Ordinal),
                    ExpiresAtMs);
            }
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
            EnsureRuntimeCaches();
        }

        private void EnsureRuntimeCaches()
        {
            _lifecycle ??= new CombatVFXLifecycleRegistry(this);
            _projectileVisuals ??= new CombatProjectileVisualController();
            _travelVisuals ??= new CombatTravelVisualController();
        }

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                UnsubscribeFromConnection();
                return;
            }

            EnsureRuntimeCaches();
            _lifecycle!.Tick(Time.deltaTime);
            TravelVisuals.Tick(Time.deltaTime);
            ProjectileVisuals.Tick(Time.deltaTime);
            PrunePredictedSpellVfx(System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var conn = NetworkManager.Instance?.Conn;
            if (_subscribedConnection != null && !ReferenceEquals(_subscribedConnection, conn))
                UnsubscribeFromConnection();
            if (_subscribedConnection != null)
            {
                RefreshActiveStatusEffectVfx();
                RefreshActivePersistentAreaVfx();
                return;
            }
            if (conn == null)
                return;

            _subscribedConnection = conn;
            CueResolver.MarkDirty();
            _projectileDeliveredSpellImpactByActionKind.Clear();
            conn.Db.CombatEvent.OnInsert += OnCombatEventInsert;
            conn.Db.SpecialMovementRuntime.OnInsert += OnSpecialMovementRuntimeInsertForVfx;
            conn.Db.ProjectilePresentationEvent.OnInsert += OnProjectilePresentationEventInsert;
            conn.Db.PredictedActionResult.OnInsert += OnPredictedActionResultInsert;
            conn.Db.ActiveCast.OnDelete += OnActiveCastDeleteForVfx;
            conn.Db.ActiveRadialEffect.OnInsert += OnActiveRadialEffectInsertForVfx;
            conn.Db.ActiveRadialEffect.OnUpdate += OnActiveRadialEffectUpdateForVfx;
            conn.Db.ActiveRadialEffect.OnDelete += OnActiveRadialEffectDeleteForVfx;
            conn.Db.ActivePersistentArea.OnInsert += OnActivePersistentAreaInsertForVfx;
            conn.Db.ActivePersistentArea.OnUpdate += OnActivePersistentAreaUpdateForVfx;
            conn.Db.ActivePersistentArea.OnDelete += OnActivePersistentAreaDeleteForVfx;
            conn.Db.StatusEffect.OnInsert += OnStatusEffectInsertForVfx;
            conn.Db.StatusEffect.OnUpdate += OnStatusEffectUpdateForVfx;
            conn.Db.StatusEffect.OnDelete += OnStatusEffectDeleteForVfx;
            conn.Db.SpellDefinition.OnInsert += OnSpellDefinitionInsertForVfx;
            conn.Db.SpellDefinition.OnUpdate += OnSpellDefinitionUpdateForVfx;
            conn.Db.SpellDefinition.OnDelete += OnSpellDefinitionDeleteForVfx;
            conn.Db.CombatVfxCueCatalog.OnInsert += OnCombatVfxCueCatalogInsert;
            conn.Db.CombatVfxCueCatalog.OnUpdate += OnCombatVfxCueCatalogUpdate;
            conn.Db.CombatVfxCueCatalog.OnDelete += OnCombatVfxCueCatalogDelete;

            foreach (ActiveRadialEffect row in conn.Db.ActiveRadialEffect.Iter())
                SpawnActiveRadialEffectVfx(row);
            foreach (ActivePersistentArea row in conn.Db.ActivePersistentArea.Iter())
                SpawnActivePersistentAreaVfx(row);
            foreach (StatusEffect row in conn.Db.StatusEffect.Iter())
                SpawnStatusEffectVfx(row);
        }

        private void OnActivePersistentAreaInsertForVfx(EventContext ctx, ActivePersistentArea row)
        {
            _ = ctx;
            SpawnActivePersistentAreaVfx(row);
        }

        private void OnActivePersistentAreaUpdateForVfx(
            EventContext ctx,
            ActivePersistentArea oldRow,
            ActivePersistentArea newRow)
        {
            _ = ctx;
            if (!string.Equals(oldRow.Key, newRow.Key, StringComparison.Ordinal)
                || !string.Equals(oldRow.SpellInstanceId, newRow.SpellInstanceId, StringComparison.Ordinal))
            {
                _lifecycle?.DestroyForRadialEffectEnd(oldRow.SpellInstanceId);
                _activePersistentAreaVfxByKey.Remove(oldRow.Key);
            }
            SpawnActivePersistentAreaVfx(newRow);
        }

        private void OnActivePersistentAreaDeleteForVfx(EventContext ctx, ActivePersistentArea row)
        {
            _ = ctx;
            _lifecycle?.DestroyForRadialEffectEnd(row.SpellInstanceId);
            _activePersistentAreaVfxByKey.Remove(row.Key);
        }

        private void RefreshActivePersistentAreaVfx()
        {
            var conn = _subscribedConnection ?? NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            foreach (ActivePersistentArea row in conn.Db.ActivePersistentArea.Iter())
            {
                if (!_activePersistentAreaVfxByKey.ContainsKey(row.Key))
                    SpawnActivePersistentAreaVfx(row);
            }
        }

        private void SpawnActivePersistentAreaVfx(ActivePersistentArea row)
        {
            string abilityId = WireIdentifier.Normalize(row.AbilityId);
            var desiredState = new ActivePersistentAreaVfxState(row.SpellInstanceId, abilityId);
            if (_activePersistentAreaVfxByKey.TryGetValue(row.Key, out ActivePersistentAreaVfxState currentState))
            {
                if (currentState.Equals(desiredState))
                    return;

                _lifecycle?.DestroyForRadialEffectEnd(currentState.SpellInstanceId);
                _activePersistentAreaVfxByKey.Remove(row.Key);
            }

            Vector3 point = new(row.AreaX, row.AreaY, row.AreaZ);
            var fact = new CombatVfxFact(
                TriggerSpellImpact,
                WireIdentifier.Normalize(row.Kind),
                abilityId,
                string.Empty,
                -1,
                row.Caster,
                row.Target,
                row.SpellInstanceId,
                row.Kind,
                point,
                Vector3.forward,
                point,
                0f,
                0f,
                CombatEventScalarKinds.None,
                0f,
                0,
                1,
                isSpell: true);
            if (DispatchFact(fact))
                _activePersistentAreaVfxByKey[row.Key] = desiredState;
        }

        // Channel/cast end: tear down any UNTIL_CAST_END cues bound to this cast. The cue's
        // action_instance_id equals ActiveCast.cast_id (server uses cast_id as the CAST
        // event's action_instance_id), so this matches the glow to its channel.
        private void OnActiveCastDeleteForVfx(EventContext ctx, ActiveCast row)
        {
            _ = ctx;
            _lifecycle?.DestroyForCastEnd(row.CastId);
        }

        private void OnActiveRadialEffectInsertForVfx(EventContext ctx, ActiveRadialEffect row)
        {
            _ = ctx;
            SpawnActiveRadialEffectVfx(row);
        }

        private void OnActiveRadialEffectUpdateForVfx(
            EventContext ctx,
            ActiveRadialEffect oldRow,
            ActiveRadialEffect newRow)
        {
            _ = ctx;
            // Pulse scheduling updates this row every interval. Only presentation identity changes
            // should replace the persistent visual.
            if (string.Equals(oldRow.Key, newRow.Key, StringComparison.Ordinal)
                && oldRow.Owner.Equals(newRow.Owner)
                && string.Equals(oldRow.SpellId, newRow.SpellId, StringComparison.Ordinal))
            {
                SpawnActiveRadialEffectVfx(newRow);
                return;
            }

            _lifecycle?.DestroyForRadialEffectEnd(oldRow.Key);
            _activeRadialEffectVfxByKey.Remove(oldRow.Key);
            SpawnActiveRadialEffectVfx(newRow);
        }

        private void OnActiveRadialEffectDeleteForVfx(EventContext ctx, ActiveRadialEffect row)
        {
            _ = ctx;
            _lifecycle?.DestroyForRadialEffectEnd(row.Key);
            _activeRadialEffectVfxByKey.Remove(row.Key);
        }

        private void SpawnActiveRadialEffectVfx(ActiveRadialEffect row)
        {
            var conn = _subscribedConnection ?? NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            string abilityId = ResolveRadialEffectAbilityId(conn, row);
            string trigger = ResolveActiveRadialEffectVfxTrigger(conn, row, abilityId);
            var desiredState = new ActiveRadialEffectVfxState(trigger, abilityId);
            if (_activeRadialEffectVfxByKey.TryGetValue(row.Key, out ActiveRadialEffectVfxState currentState))
            {
                if (currentState.Equals(desiredState))
                    return;

                _lifecycle?.DestroyForRadialEffectEnd(row.Key);
                _activeRadialEffectVfxByKey.Remove(row.Key);
            }

            if (EntityRegistry.Instance == null
                || !EntityRegistry.Instance.TryGetCombatTarget(row.Owner, out ICombatTargetEntity caster))
            {
                return;
            }

            Transform root = caster.GetPresentationRoot();
            Vector3 direction = root.forward;
            var fact = new CombatVfxFact(
                trigger,
                WireIdentifier.Normalize(row.SpellId),
                abilityId,
                string.Empty,
                -1,
                row.Owner,
                default,
                row.Key,
                row.SpellId,
                root.position,
                direction,
                root.position,
                0f,
                0f,
                CombatEventScalarKinds.None,
                0f,
                0,
                0,
                true);
            if (DispatchFact(fact))
                _activeRadialEffectVfxByKey[row.Key] = desiredState;
        }

        private void OnStatusEffectInsertForVfx(EventContext ctx, StatusEffect row)
        {
            _ = ctx;
            SpawnStatusEffectVfx(row);
            RefreshActiveRadialEffectVfxForStatus(row);
        }

        private void OnStatusEffectUpdateForVfx(EventContext ctx, StatusEffect oldRow, StatusEffect newRow)
        {
            _ = ctx;
            SpawnStatusEffectVfx(newRow);
            RefreshActiveRadialEffectVfxForStatus(oldRow);
            if (!oldRow.Target.Equals(newRow.Target)
                || !string.Equals(oldRow.SpellId, newRow.SpellId, StringComparison.Ordinal))
            {
                RefreshActiveRadialEffectVfxForStatus(newRow);
            }
        }

        private void OnStatusEffectDeleteForVfx(EventContext ctx, StatusEffect row)
        {
            _ = ctx;
            string statusEffectKey = StatusEffectVfxKey(row.StatusId);
            _lifecycle?.DestroyForStatusEnd(statusEffectKey);
            _activeStatusEffectVfxById.Remove(row.StatusId);
            RefreshActiveRadialEffectVfxForStatus(row);
        }

        private void RefreshActiveStatusEffectVfx()
        {
            var conn = _subscribedConnection ?? NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            foreach (StatusEffect row in conn.Db.StatusEffect.Iter())
            {
                if (!_activeStatusEffectVfxById.ContainsKey(row.StatusId))
                    SpawnStatusEffectVfx(row);
            }
        }

        private void SpawnStatusEffectVfx(StatusEffect row)
        {
            var conn = _subscribedConnection ?? NetworkManager.Instance?.Conn;
            if (conn == null || EntityRegistry.Instance == null)
                return;

            string spellId = WireIdentifier.Normalize(row.SpellId);
            if (string.IsNullOrWhiteSpace(spellId))
                return;

            string abilityId = ResolveAbilityIdForSpell(conn, spellId);
            if (string.IsNullOrWhiteSpace(abilityId))
            {
                string authoredStatusOwner = WireIdentifier.Normalize(row.StackGroup);
                int sourceSuffix = authoredStatusOwner.IndexOf(':');
                if (sourceSuffix > 0)
                    authoredStatusOwner = authoredStatusOwner.Substring(0, sourceSuffix);
                abilityId = ResolveAbilityIdForSpell(conn, authoredStatusOwner);
            }
            var desiredState = new ActiveStatusEffectVfxState(
                spellId,
                abilityId,
                row.Target,
                row.Stacks);
            uint stackCount = row.Stacks > 0 ? row.Stacks : 1u;
            string statusEffectKey = StatusEffectVfxKey(row.StatusId);
            if (_activeStatusEffectVfxById.TryGetValue(row.StatusId, out ActiveStatusEffectVfxState currentState))
            {
                if (currentState.Equals(desiredState))
                    return;

                // A stack tick on an otherwise unchanged status retunes the live visual so a
                // ramping buff grows instead of restarting once per stack. Visuals whose stack
                // change is a discrete event (Mirror Image losing a charge) do not offer that
                // route and fall through to the rebuild below.
                if (currentState.MatchesExceptStacks(desiredState)
                    && _lifecycle != null
                    && _lifecycle.TryRouteStackCount(statusEffectKey, stackCount))
                {
                    _activeStatusEffectVfxById[row.StatusId] = desiredState;
                    return;
                }

                _lifecycle?.DestroyForStatusEnd(statusEffectKey);
                _activeStatusEffectVfxById.Remove(row.StatusId);
            }

            if (!EntityRegistry.Instance.TryGetCombatTarget(row.Target, out ICombatTargetEntity target))
                return;

            Transform root = target.GetPresentationRoot();
            var fact = new CombatVfxFact(
                TriggerStatusActive,
                spellId,
                abilityId,
                string.Empty,
                -1,
                row.Source,
                row.Target,
                statusEffectKey,
                spellId,
                root.position,
                root.forward,
                root.position,
                0f,
                0f,
                CombatEventScalarKinds.None,
                0f,
                0,
                stackCount,
                isSpell: true);
            if (DispatchFact(fact))
                _activeStatusEffectVfxById[row.StatusId] = desiredState;
        }

        private static string StatusEffectVfxKey(ulong statusId)
            => $"status-effect:{statusId}";

        private void RefreshActiveRadialEffectVfxForStatus(StatusEffect status)
        {
            var conn = _subscribedConnection ?? NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            string spellId = WireIdentifier.Normalize(status.SpellId);
            if (string.IsNullOrWhiteSpace(spellId))
                return;

            foreach (ActiveRadialEffect row in conn.Db.ActiveRadialEffect.Owner.Filter(status.Target))
            {
                if (string.Equals(WireIdentifier.Normalize(row.SpellId), spellId, StringComparison.Ordinal))
                    SpawnActiveRadialEffectVfx(row);
            }
        }

        private static string ResolveActiveRadialEffectVfxTrigger(
            DbConnection conn,
            ActiveRadialEffect row,
            string abilityId)
        {
            if (!IsAtAuthoredMaxStacks(conn, row)
                || !HasRadialEffectCue(conn, row, abilityId, TriggerEmanationMaxStacks))
            {
                return TriggerEmanationActive;
            }

            return TriggerEmanationMaxStacks;
        }

        private static bool IsAtAuthoredMaxStacks(DbConnection conn, ActiveRadialEffect row)
        {
            string spellId = WireIdentifier.Normalize(row.SpellId);
            foreach (StatusEffect status in conn.Db.StatusEffect.Target.Filter(row.Owner))
            {
                if (status.MaxStacks > 1U
                    && status.Stacks >= status.MaxStacks
                    && string.Equals(WireIdentifier.Normalize(status.SpellId), spellId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRadialEffectCue(
            DbConnection conn,
            ActiveRadialEffect row,
            string abilityId,
            string trigger)
        {
            string spellId = WireIdentifier.Normalize(row.SpellId);
            foreach (CombatVfxCueCatalog cue in conn.Db.CombatVfxCueCatalog.Iter())
            {
                if (!string.Equals(WireIdentifier.Normalize(cue.Trigger), trigger, StringComparison.Ordinal))
                    continue;

                string ownerKind = WireIdentifier.Normalize(cue.OwnerKind);
                string ownerId = WireIdentifier.Normalize(cue.OwnerId);
                if ((string.Equals(ownerKind, OwnerKindAbility, StringComparison.Ordinal)
                        && string.Equals(ownerId, abilityId, StringComparison.Ordinal))
                    || (string.Equals(ownerKind, OwnerKindSpell, StringComparison.Ordinal)
                        && string.Equals(ownerId, spellId, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            return false;
        }

        private string ResolveRadialEffectAbilityId(DbConnection conn, ActiveRadialEffect row)
        {
            string direct = WireIdentifier.Normalize(row.AbilityId);
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            // Immolation keeps its radial/status row alive for the shared stack duration after
            // toggling off and clears ability_id to represent that gameplay state. Preserve the
            // cue owner selected while it was enabled; the catalog lookup below is the reconnect
            // fallback when this client did not observe the enabled row.
            if (_activeRadialEffectVfxByKey.TryGetValue(row.Key, out ActiveRadialEffectVfxState current)
                && !string.IsNullOrWhiteSpace(current.AbilityId))
            {
                return current.AbilityId;
            }

            return ResolveAbilityIdForSpell(conn, row.SpellId);
        }

        private static string ResolveAbilityIdForSpell(DbConnection conn, string spellId)
        {
            spellId = WireIdentifier.Normalize(spellId);
            string resolved = string.Empty;
            uint resolvedSortOrder = uint.MaxValue;
            foreach (AbilityCatalog ability in conn.Db.AbilityCatalog.Iter())
            {
                if (!string.Equals(WireIdentifier.Normalize(ability.AbilityKind), CombatEventSources.Spell, StringComparison.Ordinal)
                    || !string.Equals(WireIdentifier.Normalize(ability.ActionId), spellId, StringComparison.Ordinal))
                {
                    continue;
                }

                string candidate = WireIdentifier.Normalize(ability.AbilityId);
                if (ability.SortOrder < resolvedSortOrder
                    || (ability.SortOrder == resolvedSortOrder
                        && string.CompareOrdinal(candidate, resolved) < 0))
                {
                    resolved = candidate;
                    resolvedSortOrder = ability.SortOrder;
                }
            }

            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;

            // Passive-owned statuses use their stable ability ID as spell_id because they have
            // no SpellDefinition/action row. Resolve that direct ID so STATUS_ACTIVE cues can
            // reconstruct after subscription and update when the replicated stack count changes.
            foreach (AbilityCatalog ability in conn.Db.AbilityCatalog.Iter())
            {
                string candidate = WireIdentifier.Normalize(ability.AbilityId);
                if (!string.Equals(candidate, spellId, StringComparison.Ordinal))
                    continue;
                if (ability.SortOrder < resolvedSortOrder
                    || (ability.SortOrder == resolvedSortOrder
                        && string.CompareOrdinal(candidate, resolved) < 0))
                {
                    resolved = candidate;
                    resolvedSortOrder = ability.SortOrder;
                }
            }

            return resolved;
        }

        private void OnSpellDefinitionInsertForVfx(EventContext ctx, SpellDefinition row)
        {
            _ = ctx;
            InvalidateProjectileSpellImpactClassification(row.Kind);
        }

        private void OnSpellDefinitionUpdateForVfx(
            EventContext ctx,
            SpellDefinition oldRow,
            SpellDefinition newRow)
        {
            _ = ctx;
            InvalidateProjectileSpellImpactClassification(oldRow.Kind);
            InvalidateProjectileSpellImpactClassification(newRow.Kind);
        }

        private void OnSpellDefinitionDeleteForVfx(EventContext ctx, SpellDefinition row)
        {
            _ = ctx;
            InvalidateProjectileSpellImpactClassification(row.Kind);
        }

        private void InvalidateProjectileSpellImpactClassification(string actionKind)
        {
            string key = WireIdentifier.Normalize(actionKind);
            if (!string.IsNullOrWhiteSpace(key))
                _projectileDeliveredSpellImpactByActionKind.Remove(key);
        }

        public static void PredictLocalInstantSpellRelease(
            DbConnection conn,
            string spellId,
            SpellDefinition spellDef,
            string targetId,
            Vector3? aimPoint,
            CastActionToken token)
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            _instance?.PredictLocalInstantSpellReleaseInternal(conn, spellId, spellDef, targetId, aimPoint, token);
        }

        private void PredictLocalInstantSpellReleaseInternal(
            DbConnection conn,
            string spellId,
            SpellDefinition spellDef,
            string targetId,
            Vector3? aimPoint,
            CastActionToken token)
        {
            if (!token.IsPredicted
                || spellDef.CastTimeMs > 0
                || SpellDefinitionContracts.CastsOnRelease(spellDef))
            {
                return;
            }

            PlayerEntity? caster = EntityRegistry.Instance?.LocalPlayerEntity;
            if (caster == null || caster.IsDestroyed || !caster.IsAlive)
                return;

            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            if (string.IsNullOrWhiteSpace(normalizedSpellId))
                return;

            string tokenKey = SpellTokenKey(token.PredictedCastId, token.ClientActionSeq);
            string predictedActionInstanceId = PredictedActionInstanceId(tokenKey);
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string abilityId = ResolveLocalAbilityId(conn, caster.Identity, normalizedSpellId);

            var pending = new PendingPredictedSpellVfx(
                tokenKey,
                normalizedSpellId,
                predictedActionInstanceId,
                string.Empty,
                suppressAuthoritativeRelease: false,
                suppressAuthoritativeAreaImpact: false,
                nowMs + PredictedSpellVfxTtlMs);

            if (TryBuildPredictedReleaseFact(
                    caster,
                    normalizedSpellId,
                    abilityId,
                    spellDef,
                    targetId,
                    aimPoint,
                    predictedActionInstanceId,
                    out CombatVfxFact releaseFact))
            {
                DispatchPredictedSpellFact(conn, spellDef, releaseFact, ref pending);
            }

            if (TryBuildPredictedAreaImpactFact(
                    caster,
                    normalizedSpellId,
                    abilityId,
                    spellDef,
                    aimPoint,
                    predictedActionInstanceId,
                    out CombatVfxFact areaImpactFact))
            {
                DispatchPredictedSpellFact(conn, spellDef, areaImpactFact, ref pending);
            }

            if (pending.SuppressAuthoritativeRelease
                || pending.SuppressAuthoritativeAreaImpact
                || !string.IsNullOrWhiteSpace(pending.PredictedProjectileKey))
                _pendingSpellVfxByToken[tokenKey] = pending;
        }

        private void DispatchPredictedSpellFact(
            DbConnection conn,
            SpellDefinition spellDef,
            CombatVfxFact fact,
            ref PendingPredictedSpellVfx pending)
        {
            List<CombatVfxCueCatalog> matchingCues = MatchingCues;
            CueResolver.Resolve(conn.Db.CombatVfxCueCatalog.Iter(), fact.ToResolutionFact(), matchingCues);
            if (matchingCues.Count == 0)
                return;

            foreach (CombatVfxCueCatalog cue in matchingCues)
            {
                string role = WireIdentifier.Normalize(cue.VfxRole);
                if (string.Equals(role, VfxRoleProjectileBody, StringComparison.Ordinal))
                {
                    string trailVfxId = ResolveProjectileTrailVfxId(matchingCues, cue.ProjectileSequenceIndex);
                    if (TryStartPredictedProjectile(cue, trailVfxId, fact, pending, spellDef, out string predictedProjectileKey))
                        pending = pending.WithProjectileKey(predictedProjectileKey);
                    continue;
                }
                if (string.Equals(role, VfxRoleProjectileTrail, StringComparison.Ordinal))
                    continue;
                if (string.Equals(role, VfxRoleTravelBody, StringComparison.Ordinal))
                    continue;

                DispatchCue(fact, cue);
                pending = pending.WithSuppressedAuthoritativeTrigger(fact.Trigger);
            }
        }

        /// <summary>
        /// Predicted melee contact cue (feel audit F5 slice 2): plays the
        /// authored MELEE_IMPACT cue set at rendered positions when the
        /// advisory hit test passes at the authored first hit window. The
        /// matching authoritative impact cue is suppressed as a duplicate via
        /// PredictedMeleeContactCueController; block/parry/miss presentation
        /// and everything gameplay-facing stay authoritative.
        /// </summary>
        public static void PlayPredictedLocalMeleeContactCue(
            DbConnection conn,
            PlayerEntity caster,
            ICombatTargetEntity target,
            string runtimeActionId,
            int hitIndex)
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            _instance?.PlayPredictedLocalMeleeContactCueInternal(conn, caster, target, runtimeActionId, hitIndex);
        }

        private void PlayPredictedLocalMeleeContactCueInternal(
            DbConnection conn,
            PlayerEntity caster,
            ICombatTargetEntity target,
            string runtimeActionId,
            int hitIndex)
        {
            if (caster == null || caster.IsDestroyed || target == null || target.IsDestroyed)
                return;

            string combatProfile = CombatProfileResolver.ResolveForEntity(conn, caster);
            string strikeId = WireIdentifier.Normalize(
                CombatActionIds.ResolveAuthoredStrikeId(conn, combatProfile, runtimeActionId));
            if (string.IsNullOrWhiteSpace(strikeId))
                return;

            Vector3 origin = caster.GameObject.transform.position;
            Vector3 point = target.GetPresentationRoot().position
                + Vector3.up * (Mathf.Max(target.HitHeight, 0f) * 0.5f);
            Vector3 direction = point - origin;
            direction.y = 0f;
            direction = direction.sqrMagnitude > 0.000001f
                ? direction.normalized
                : ResolveLocalCasterForward(caster);

            var fact = new CombatVfxFact(
                TriggerMeleeImpact,
                string.Empty,
                ResolveLocalAbilityId(conn, caster.Identity, runtimeActionId),
                strikeId,
                hitIndex,
                caster.Identity,
                target.TargetIdentity,
                $"predicted_melee_contact:{runtimeActionId}",
                runtimeActionId,
                origin,
                direction,
                point,
                0f,
                0f,
                CombatEventScalarKinds.None,
                0f,
                0,
                1,
                isSpell: false);
            DispatchFact(fact);
        }

        private void OnCombatVfxCueCatalogInsert(EventContext ctx, CombatVfxCueCatalog row)
        {
            _ = ctx;
            CueResolver.MarkDirty();
            _projectileDeliveredSpellImpactByActionKind.Clear();
        }

        private void OnCombatVfxCueCatalogUpdate(EventContext ctx, CombatVfxCueCatalog oldRow, CombatVfxCueCatalog newRow)
        {
            _ = ctx;
            CueResolver.MarkDirty();
            _projectileDeliveredSpellImpactByActionKind.Clear();
        }

        private void OnCombatVfxCueCatalogDelete(EventContext ctx, CombatVfxCueCatalog row)
        {
            _ = ctx;
            CueResolver.MarkDirty();
            _projectileDeliveredSpellImpactByActionKind.Clear();
        }

        private void OnSpecialMovementRuntimeInsertForVfx(
            EventContext ctx,
            SpecialMovementRuntime row)
        {
            _ = ctx;
            if (!string.Equals(
                    WireIdentifier.Normalize(row.Kind),
                    LingeringShadeReturnMovementKind,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (EntityRegistry.Instance == null
                || !EntityRegistry.Instance.TryGetEntity(row.Owner, out PlayerEntity caster))
            {
                return;
            }

            Quaternion facing = Quaternion.Euler(0f, row.FacingYawStart * Mathf.Rad2Deg, 0f);
            Vector3 chestLocalOffset = ResolvePlayerChestLocalOffset(caster);
            Vector3 origin = ResolveSpecialMovementEndpoint(
                new Vector3(row.StartX, row.StartY, row.StartZ),
                facing,
                chestLocalOffset);
            Vector3 point = ResolveSpecialMovementEndpoint(
                new Vector3(row.EndX, row.EndY, row.EndZ),
                facing,
                chestLocalOffset);
            Vector3 direction = facing * Vector3.forward;

            DispatchSpecialMovementFact(row, TriggerSpecialMovementStart, origin, direction, point);
            DispatchSpecialMovementFact(row, TriggerSpecialMovementArrival, origin, direction, point);
        }

        private void DispatchSpecialMovementFact(
            SpecialMovementRuntime row,
            string trigger,
            Vector3 origin,
            Vector3 direction,
            Vector3 point)
        {
            var fact = new CombatVfxFact(
                trigger,
                string.Empty,
                LingeringShadeAbilityId,
                string.Empty,
                -1,
                row.Owner,
                default,
                row.RuntimeId,
                row.Kind,
                origin,
                direction,
                point,
                0f,
                Vector3.Distance(origin, point),
                CombatEventScalarKinds.None,
                0f,
                0,
                1,
                isSpell: false);
            DispatchFact(fact);
        }

        private static Vector3 ResolvePlayerChestLocalOffset(PlayerEntity caster)
        {
            Transform root = caster.GetPresentationRoot();
            if ((caster.TryGetSocketTransform(HumanBodyBones.UpperChest, out Transform chest)
                    || caster.TryGetSocketTransform(HumanBodyBones.Chest, out chest))
                && root != null)
            {
                Vector3 worldOffset = chest.position - root.position;
                return Quaternion.Inverse(root.rotation) * worldOffset;
            }

            float chestHeight = caster.HitHeight > 0f
                ? caster.HitHeight * PlayerChestHeightFraction
                : DefaultPlayerChestHeight;
            return Vector3.up * chestHeight;
        }

        internal static Vector3 ResolveSpecialMovementEndpoint(
            Vector3 rootPosition,
            Quaternion facing,
            Vector3 localOffset)
        {
            return rootPosition + facing * localOffset;
        }

        private void OnPredictedActionResultInsert(EventContext ctx, PredictedActionResult row)
        {
            _ = ctx;
            if (row.Family != PredictedActionFamily.SpellCast)
                return;

            string tokenKey = SpellTokenKey(row.PredictedActionId, row.ClientActionSeq);
            if (!_pendingSpellVfxByToken.TryGetValue(tokenKey, out PendingPredictedSpellVfx pending))
                return;

            if (row.Result == ActionResultKind.Accepted)
            {
                if (!string.IsNullOrWhiteSpace(row.ActionInstanceId))
                    _spellVfxTokenByActionInstance[row.ActionInstanceId] = tokenKey;
                return;
            }

            if (!string.IsNullOrWhiteSpace(pending.PredictedProjectileKey))
                ProjectileVisuals.RemovePredicted(pending.PredictedProjectileKey);
            _pendingSpellVfxByToken.Remove(tokenKey);
        }

        private void OnCombatEventInsert(EventContext ctx, CombatEvent row)
        {
            _ = ctx;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (CombatPresentationDebugGate.ShouldSuppress(row))
                return;
#endif

            if (EntityRegistry.Instance == null)
                return;

            if (!EntityRegistry.Instance.IsIdentityVisible(row.Caster)
                && !EntityRegistry.Instance.IsIdentityVisible(row.Hit))
                return;

            if (row.EventType == CombatEventTypes.Update)
                RouteScriptedUpdate(row);

            if (ShouldSuppressPredictedLocalSpellEvent(row))
                return;

            // Feel audit F5 slice 2: feed melee contact-family rows to the
            // predicted-contact correlation before BuildFact can drop them
            // (melee COMBAT_CONTACT has no cue trigger). Suppression skips
            // only the duplicate cue dispatch; terminal routing still runs.
            bool suppressPredictedMeleeContactCue =
                PredictedMeleeContactCueController.ShouldSuppressAuthoritativeContactCue(row);

            CombatVfxFact? fact = BuildFact(row);
            if (fact == null)
                return;

            _lifecycle ??= new CombatVFXLifecycleRegistry(this);
            if (IsTerminalEvent(row))
            {
                bool fizzled =
                    string.Equals(row.EventType, CombatEventTypes.Fizzle, StringComparison.Ordinal)
                    || string.Equals(row.EventType, CombatEventTypes.Miss, StringComparison.Ordinal);
                if (fizzled)
                    TravelVisuals.Fizzle(fact.Value.ToTemplateContext(string.Empty));
                else
                    TravelVisuals.Impact(fact.Value.ToTemplateContext(string.Empty));

                _lifecycle.RouteTerminal(
                    fact.Value.ToTemplateContext(string.Empty),
                    fizzled);
            }
            else if (row.EventType == CombatEventTypes.Release)
            {
                _lifecycle.RouteRelease(fact.Value.ToTemplateContext(string.Empty));
            }

            // Projectile-delivered spells fire their SPELL_IMPACT hit cue per projectile from the
            // projectile-presentation IMPACT terminal (DispatchProjectileEvent), so the identity-less
            // combat_event impact must not also dispatch it — that would double single-projectile spells
            // and can't key per-missile for channels. Non-projectile spell impacts keep this path.
            if (!suppressPredictedMeleeContactCue && !IsProjectileDeliveredSpellImpact(row))
            {
                bool targetAnchoredSpellContact =
                    string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal)
                    && string.Equals(row.EventType, CombatEventTypes.Contact, StringComparison.Ordinal);
                DispatchFact(fact.Value, targetAnchoredSpellContact);
            }
        }

        // True for a spell's terminal IMPACT combat_event whose spell delivers projectiles — those now
        // present per-projectile from the projectile-presentation IMPACT terminal, so this combat_event
        // must not re-dispatch SPELL_IMPACT. Projectile-delivered = PROJECTILE behavior, or a CHANNEL that
        // fires projectiles (Speed > 0, which excludes beam channels like ELECTROCUTE). Direct-target,
        // area, and beam spell impacts return false and keep dispatching from the combat_event path.
        private bool IsProjectileDeliveredSpellImpact(CombatEvent row)
        {
            if (!string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal))
                return false;
            if (!string.Equals(row.EventType, CombatEventTypes.Impact, StringComparison.Ordinal))
                return false;

            string actionKind = WireIdentifier.Normalize(row.ActionKind);
            if (_projectileDeliveredSpellImpactByActionKind.TryGetValue(actionKind, out bool cached))
                return cached;

            SpellDefinition? def = NetworkManager.Instance?.Conn?.Db.SpellDefinition.Kind.Find(actionKind);
            if (def == null)
                return false;

            string behavior = WireIdentifier.Normalize(def.Behavior);
            bool result = string.Equals(behavior, SpellBehaviorProjectile, StringComparison.Ordinal)
                || (string.Equals(behavior, SpellDefinitionContracts.BehaviorChannel, StringComparison.Ordinal)
                    && def.Speed > 0f);
            _projectileDeliveredSpellImpactByActionKind[actionKind] = result;
            return result;
        }

        private void OnProjectilePresentationEventInsert(EventContext ctx, ProjectilePresentationEvent row)
        {
            _ = ctx;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (CombatPresentationDebugGate.ShouldSuppress(row))
                return;
#endif

            if (EntityRegistry.Instance == null)
                return;

            if (!EntityRegistry.Instance.IsIdentityVisible(row.Caster)
                && !EntityRegistry.Instance.IsIdentityVisible(row.Hit))
                return;

            if (TryAdoptPredictedLocalProjectileRelease(row))
                return;

            DispatchProjectileEvent(row);
        }

        private void RouteScriptedUpdate(CombatEvent row)
        {
            if (!string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal))
                return;

            CombatVfxFact? fact = BuildFact(row, allowMissingTrigger: true);
            if (fact == null)
                return;

            _lifecycle ??= new CombatVFXLifecycleRegistry(this);
            _lifecycle.RouteUpdate(fact.Value.ToTemplateContext(string.Empty));
        }

        private void DispatchProjectileEvent(ProjectilePresentationEvent row)
        {
            switch (row.EventType)
            {
                case CombatEventTypes.Cast:
                case CombatEventTypes.Release:
                    ProjectileVisuals.Start(row, TryResolveProjectileHandLaunchOrigin(row));
                    break;
                case CombatEventTypes.Update:
                    ProjectileVisuals.Update(row);
                    break;
                case CombatEventTypes.Contact:
                    DispatchProjectileContactCue(row);
                    break;
                case CombatEventTypes.Impact:
                    if (row.Terminal)
                    {
                        ProjectileVisuals.Impact(row);
                        // Fire the authored SPELL_IMPACT hit cue per projectile, at this projectile's own
                        // impact point — the same per-projectile presentation path CONTACT already uses
                        // (e.g. ORBITING_BLADES). Multi-projectile channels (MAGIC_MISSILE/FROZEN_SPLINTERS)
                        // now hit per missile instead of collapsing to one; single-projectile spells fire
                        // exactly once. The duplicate combat_event SPELL_IMPACT dispatch is suppressed for
                        // projectile-delivered spells in OnCombatEventInsert (IsProjectileDeliveredSpellImpact).
                        //
                        // Skip a self-return terminal: a boomerang emits its terminal IMPACT at its own
                        // caster when it comes home (hit == caster, damage 0) — that's the projectile
                        // ending, not an enemy hit, so it must not spawn a hit burst on the caster. The
                        // boomerang's enemy hits are non-terminal CONTACTs, already dispatched above.
                        if (!row.Hit.Equals(row.Caster))
                            DispatchProjectileContactCue(row);
                    }
                    break;
                case CombatEventTypes.Block:
                case CombatEventTypes.Parry:
                    if (row.Terminal)
                        ProjectileVisuals.Impact(row);
                    break;
                case CombatEventTypes.Miss:
                case CombatEventTypes.Fizzle:
                    ProjectileVisuals.Fizzle(row);
                    break;
            }
        }

        // A projectile-body cue can anchor to a caster socket (e.g. LEFT_HAND). The
        // authoritative projectile spawns from the server origin (a caster-relative forward
        // point), so resolve that socket here and hand it to the visual controller, which
        // launches the body from the socket and decays onto the authoritative curve. Returns
        // null when the spell has no hand/weapon-anchored projectile body (keep server origin).
        private Vector3? TryResolveProjectileHandLaunchOrigin(ProjectilePresentationEvent row)
        {
            if (!string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal))
                return null;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return null;

            string abilityId = WireIdentifier.Normalize(row.AbilityId);
            if (string.IsNullOrWhiteSpace(abilityId))
                return null;

            foreach (CombatVfxCueCatalog cue in conn.Db.CombatVfxCueCatalog.Iter())
            {
                if (!string.Equals(WireIdentifier.Normalize(cue.VfxRole), VfxRoleProjectileBody, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(WireIdentifier.Normalize(cue.OwnerKind), OwnerKindAbility, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(WireIdentifier.Normalize(cue.OwnerId), abilityId, StringComparison.Ordinal))
                    continue;
                if (cue.ProjectileSequenceIndex != (int)row.SequenceIndex)
                    continue;

                string anchor = WireIdentifier.Normalize(cue.Anchor);
                if (!IsCasterSocketAnchor(anchor))
                    return null;

                var fact = new CombatVfxAnchorFact(
                    row.Caster,
                    row.Hit,
                    new Vector3(row.OriginX, row.OriginY, row.OriginZ),
                    new Vector3(row.PointX, row.PointY, row.PointZ));
                Transform? socket = CombatVFXAnchorResolver.ResolveFollowAnchor(fact, cue);
                if (socket == null)
                    return null;

                // Nudge the launch point slightly in front of and above the hand so missiles
                // read as leaving the fingertips rather than the wrist bone. Use only the
                // horizontal aim for the forward reach so aiming at low targets doesn't drag
                // the muzzle toward the ground, then lift to palm height.
                Vector3 dir = new Vector3(row.DirX, 0f, row.DirZ);
                Vector3 forward = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.zero;
                return socket.position
                    + forward * HandMuzzleForwardMeters
                    + Vector3.up * HandMuzzleUpMeters;
            }

            return null;
        }

        private static bool IsCasterSocketAnchor(string anchor)
        {
            return string.Equals(anchor, AnchorLeftHand, StringComparison.Ordinal)
                || string.Equals(anchor, AnchorRightHand, StringComparison.Ordinal)
                || string.Equals(anchor, AnchorWeaponMainHand, StringComparison.Ordinal)
                || string.Equals(anchor, AnchorWeaponOffHand, StringComparison.Ordinal);
        }

        private void DispatchProjectileContactCue(ProjectilePresentationEvent row)
        {
            if (!string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal))
                return;

            CombatVfxFact? fact = BuildProjectileContactFact(row);
            if (fact == null)
                return;

            DispatchFact(fact.Value);
        }

        private bool DispatchFact(CombatVfxFact fact, bool targetAnchoredOnly = false)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return false;

            List<CombatVfxCueCatalog> matchingCues = MatchingCues;
            CueResolver.Resolve(conn.Db.CombatVfxCueCatalog.Iter(), fact.ToResolutionFact(), matchingCues);

            if (matchingCues.Count == 0)
                return false;

            bool dispatched = false;
            foreach (CombatVfxCueCatalog cue in matchingCues)
            {
                if (targetAnchoredOnly && !IsTargetAnchoredCue(cue))
                    continue;

                DispatchCue(fact, cue);
                dispatched = true;
            }
            return dispatched;
        }

        private static bool IsTargetAnchoredCue(CombatVfxCueCatalog cue)
        {
            string anchor = WireIdentifier.Normalize(cue.Anchor);
            return string.Equals(anchor, AnchorTarget, StringComparison.Ordinal)
                || string.Equals(anchor, AnchorGroundUnderTarget, StringComparison.Ordinal);
        }

        private static CombatVfxFact? BuildFact(CombatEvent row, bool allowMissingTrigger = false)
        {
            bool isSpell = string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal);
            string trigger = ResolveTrigger(row.EventType, isSpell);
            if (string.IsNullOrWhiteSpace(trigger) && !allowMissingTrigger)
                return null;

            var conn = NetworkManager.Instance?.Conn;
            string spellId = isSpell ? WireIdentifier.Normalize(row.ActionKind) : string.Empty;
            string abilityId = WireIdentifier.Normalize(row.AbilityId);
            string strikeId = !isSpell && conn != null
                ? ResolveAuthoredStrikeId(conn, row)
                : string.Empty;
            if (isSpell && string.IsNullOrWhiteSpace(spellId))
                return null;
            if (!isSpell && string.IsNullOrWhiteSpace(strikeId))
                return null;

            return new CombatVfxFact(
                row,
                trigger,
                spellId,
                abilityId,
                strikeId,
                isSpell ? -1 : row.HitIndex);
        }

        private static CombatVfxFact? BuildProjectileContactFact(ProjectilePresentationEvent row)
        {
            string spellId = WireIdentifier.Normalize(row.ActionKind);
            string abilityId = WireIdentifier.Normalize(row.AbilityId);
            if (string.IsNullOrWhiteSpace(spellId))
                return null;

            return new CombatVfxFact(
                row,
                TriggerSpellImpact,
                spellId,
                abilityId,
                string.Empty,
                -1);
        }

        private bool ShouldSuppressPredictedLocalSpellEvent(CombatEvent row)
        {
            if (!IsLocalAuthoritativePredictedSpellEvent(row))
                return false;

            if (!TryResolvePredictedSpellVfx(row.ActionInstanceId, row.ActionKind, out PendingPredictedSpellVfx pending))
                return false;

            if (!string.IsNullOrWhiteSpace(row.ActionInstanceId))
                _spellVfxTokenByActionInstance[row.ActionInstanceId] = pending.TokenKey;

            if (string.Equals(row.EventType, CombatEventTypes.AreaImpact, StringComparison.Ordinal))
                return pending.SuppressAuthoritativeAreaImpact;

            return pending.SuppressAuthoritativeRelease;
        }

        private bool TryAdoptPredictedLocalProjectileRelease(ProjectilePresentationEvent row)
        {
            if (!IsLocalAuthoritativeSpellProjectileRelease(row))
                return false;

            if (!TryResolvePredictedSpellVfx(row.ActionInstanceId, row.ActionKind, out PendingPredictedSpellVfx pending))
                return false;

            if (string.IsNullOrWhiteSpace(pending.PredictedProjectileKey))
                return false;

            if (!ProjectileVisuals.TryAdoptPredictedRelease(pending.PredictedProjectileKey, row))
                return false;

            if (!string.IsNullOrWhiteSpace(row.ActionInstanceId))
                _spellVfxTokenByActionInstance[row.ActionInstanceId] = pending.TokenKey;

            _pendingSpellVfxByToken.Remove(pending.TokenKey);
            return true;
        }

        private bool TryResolvePredictedSpellVfx(
            string actionInstanceId,
            string spellId,
            out PendingPredictedSpellVfx pending)
        {
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!string.IsNullOrWhiteSpace(actionInstanceId)
                && _spellVfxTokenByActionInstance.TryGetValue(actionInstanceId, out string tokenKey)
                && _pendingSpellVfxByToken.TryGetValue(tokenKey, out pending)
                && nowMs <= pending.ExpiresAtMs)
            {
                return true;
            }

            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            foreach (PendingPredictedSpellVfx candidate in _pendingSpellVfxByToken.Values)
            {
                if (nowMs > candidate.ExpiresAtMs)
                    continue;
                if (!string.Equals(candidate.SpellId, normalizedSpellId, StringComparison.Ordinal))
                    continue;

                pending = candidate;
                return true;
            }

            pending = default;
            return false;
        }

        private static bool IsLocalAuthoritativePredictedSpellEvent(CombatEvent row)
        {
            PlayerEntity? local = EntityRegistry.Instance?.LocalPlayerEntity;
            return local != null
                && row.Caster == local.Identity
                && string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal)
                && (string.Equals(row.EventType, CombatEventTypes.Release, StringComparison.Ordinal)
                    || string.Equals(row.EventType, CombatEventTypes.AreaImpact, StringComparison.Ordinal));
        }

        private static bool IsLocalAuthoritativeSpellProjectileRelease(ProjectilePresentationEvent row)
        {
            PlayerEntity? local = EntityRegistry.Instance?.LocalPlayerEntity;
            return local != null
                && row.Caster == local.Identity
                && string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal)
                && string.Equals(row.EventType, CombatEventTypes.Release, StringComparison.Ordinal);
        }

        private static bool TryBuildPredictedReleaseFact(
            PlayerEntity caster,
            string spellId,
            string abilityId,
            SpellDefinition spellDef,
            string targetId,
            Vector3? aimPoint,
            string predictedActionInstanceId,
            out CombatVfxFact fact)
        {
            Vector3 origin = ResolveLocalCasterPosition(caster);
            Vector3 point = aimPoint ?? origin;
            Vector3 direction = ResolveLocalCasterForward(caster);

            if (string.Equals(WireIdentifier.Normalize(spellDef.Behavior), SpellBehaviorProjectile, StringComparison.Ordinal))
            {
                if (!TryResolvePredictedProjectileVectors(caster, spellId, spellDef, targetId, out origin, out direction, out point))
                {
                    fact = default;
                    return false;
                }
            }
            else if (string.Equals(WireIdentifier.Normalize(spellDef.Behavior), SpellBehaviorArea, StringComparison.Ordinal))
            {
                if (!aimPoint.HasValue && !SpellDefinitionContracts.UsesSelfTargeting(spellDef))
                {
                    fact = default;
                    return false;
                }
            }

            fact = new CombatVfxFact(
                TriggerSpellRelease,
                spellId,
                abilityId,
                string.Empty,
                -1,
                caster.Identity,
                default,
                predictedActionInstanceId,
                spellId,
                origin,
                direction,
                point,
                spellDef.Speed,
                ResolvePredictedMaxDistance(spellDef),
                CombatEventScalarKinds.None,
                0f,
                0,
                1,
                isSpell: true);
            return true;
        }

        private static bool TryBuildPredictedAreaImpactFact(
            PlayerEntity caster,
            string spellId,
            string abilityId,
            SpellDefinition spellDef,
            Vector3? aimPoint,
            string predictedActionInstanceId,
            out CombatVfxFact fact)
        {
            bool isSelfArea = SpellDefinitionContracts.UsesSelfTargeting(spellDef);
            bool isPointArea = SpellDefinitionContracts.UsesPointTargeting(spellDef);
            if (!string.Equals(WireIdentifier.Normalize(spellDef.Behavior), SpellBehaviorArea, StringComparison.Ordinal)
                || (!isSelfArea && !(isPointArea && aimPoint.HasValue)))
            {
                fact = default;
                return false;
            }

            Vector3 point = isPointArea && aimPoint.HasValue
                ? aimPoint.Value
                : ResolveLocalCasterGroundPosition(caster);
            Vector3 origin = isPointArea ? point : ResolveLocalCasterGroundPosition(caster);
            Vector3 direction = ResolveLocalCasterForward(caster);

            fact = new CombatVfxFact(
                TriggerAreaImpact,
                spellId,
                abilityId,
                string.Empty,
                -1,
                caster.Identity,
                default,
                predictedActionInstanceId,
                spellId,
                origin,
                direction,
                point,
                0f,
                ResolvePredictedMaxDistance(spellDef),
                CombatEventScalarKinds.None,
                0f,
                0,
                1,
                isSpell: true);
            return true;
        }

        private bool TryStartPredictedProjectile(
            CombatVfxCueCatalog cue,
            string projectileTrailVfxId,
            CombatVfxFact fact,
            PendingPredictedSpellVfx pending,
            SpellDefinition spellDef,
            out string predictedProjectileKey)
        {
            predictedProjectileKey = $"{pending.PredictedActionInstanceId}:p{Mathf.Max(0, cue.ProjectileSequenceIndex)}";
            var row = new ProjectilePresentationEvent
            {
                ActionInstanceId = pending.PredictedActionInstanceId,
                ActionKind = fact.ActionKind,
                AbilityId = fact.AbilityId,
                SourceKind = CombatEventSources.Spell,
                ProjectileId = cue.VfxId,
                ProjectileTrailVfxId = projectileTrailVfxId,
                ProjectileInstanceId = predictedProjectileKey,
                HitIndex = -1,
                EventType = CombatEventTypes.Release,
                Caster = fact.Caster,
                Hit = default,
                IntendedTarget = fact.Hit,
                OriginX = fact.Origin.x,
                OriginY = fact.Origin.y,
                OriginZ = fact.Origin.z,
                DirX = fact.Direction.x,
                DirY = fact.Direction.y,
                DirZ = fact.Direction.z,
                PointX = fact.Point.x,
                PointY = fact.Point.y,
                PointZ = fact.Point.z,
                Speed = spellDef.Speed,
                MaxDistance = ResolvePredictedMaxDistance(spellDef),
                Radius = spellDef.Radius,
                MotionKind = ResolvePredictedProjectileMotionKind(fact.ActionKind),
                UpdateIntervalSeconds = spellDef.UpdateInterval,
                SequenceIndex = (uint)Mathf.Max(0, cue.ProjectileSequenceIndex),
                SequenceCount = 1,
                Terminal = false,
            };

            ProjectileVisuals.Start(row);
            return true;
        }

        private static string ResolveProjectileTrailVfxId(
            IEnumerable<CombatVfxCueCatalog> cues,
            int projectileSequenceIndex)
        {
            foreach (CombatVfxCueCatalog cue in cues)
            {
                if (cue.ProjectileSequenceIndex == projectileSequenceIndex
                    && string.Equals(
                        WireIdentifier.Normalize(cue.VfxRole),
                        VfxRoleProjectileTrail,
                        StringComparison.Ordinal))
                {
                    return cue.VfxId;
                }
            }

            return string.Empty;
        }

        private static Vector3 ResolveLocalCasterPosition(PlayerEntity caster)
        {
            LocalPlayerStateProvider? stateProvider = caster.GetLocalStateProvider();
            if (stateProvider != null && stateProvider.HasPredictedState)
                return stateProvider.PredictedPosition;

            return caster.GetPresentationRoot().position;
        }

        private static Vector3 ResolveLocalCasterGroundPosition(PlayerEntity caster)
        {
            Vector3 position = ResolveLocalCasterPosition(caster);
            if (EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetLocalPredictionEnvironment(out IMovementEnvironment? environment)
                && environment != null)
            {
                position.y = environment.SampleGroundHeight(position.x, position.z, position.y);
            }

            return position;
        }

        private static Vector3 ResolveLocalCasterForward(PlayerEntity caster)
        {
            Vector3 forward = caster.GameObject.transform.forward;
            var horizontal = new Vector3(forward.x, 0f, forward.z);
            return horizontal.sqrMagnitude > 0.000001f
                ? horizontal.normalized
                : Vector3.forward;
        }

        private static bool TryResolvePredictedProjectileVectors(
            PlayerEntity caster,
            string spellId,
            SpellDefinition spellDef,
            string targetId,
            out Vector3 origin,
            out Vector3 direction,
            out Vector3 point)
        {
            Vector3 casterPosition = ResolveLocalCasterPosition(caster);
            Vector3 basePosition = casterPosition + Vector3.up * spellDef.SpawnHeight;
            string motionKind = ResolvePredictedProjectileMotionKind(spellId);
            if (SpellDefinitionContracts.UsesSelfTargeting(spellDef)
                && (string.Equals(
                        motionKind,
                        ProjectileMotionBoomerangCaster,
                        StringComparison.Ordinal)
                    || string.Equals(
                        motionKind,
                        ProjectileMotionTravelingArea,
                        StringComparison.Ordinal)))
            {
                direction = ResolveLocalCasterForward(caster);
                origin = basePosition + direction * spellDef.SpawnForward;
                point = origin;
                return true;
            }

            ICombatTargetEntity? target = TargetSelector.Instance?.SelectedTarget;
            if (target == null
                || (!string.IsNullOrWhiteSpace(targetId)
                    && !string.Equals(target.TargetIdentity.ToString(), targetId, StringComparison.Ordinal)))
            {
                origin = default;
                direction = default;
                point = default;
                return false;
            }

            Vector3 targetPosition = target.GetPresentationRoot().position + Vector3.up * spellDef.SpawnHeight;
            direction = targetPosition - basePosition;
            if (direction.sqrMagnitude <= 0.0001f)
                direction = caster.GameObject.transform.forward;
            direction = direction.normalized;
            origin = basePosition + direction * spellDef.SpawnForward;
            point = origin;
            return true;
        }

        private static float ResolvePredictedMaxDistance(SpellDefinition spellDef)
            => spellDef.MaxDistance > 0f ? spellDef.MaxDistance : spellDef.Speed;

        private static string ResolvePredictedProjectileMotionKind(string spellId)
        {
            string normalized = WireIdentifier.Normalize(spellId);
            if (string.Equals(normalized, "GRAVEWAKE", StringComparison.Ordinal)
                || string.Equals(normalized, "FISSURE", StringComparison.Ordinal))
                return ProjectileMotionTravelingArea;

            return normalized.IndexOf("BOOMERANG", StringComparison.Ordinal) >= 0
                || string.Equals(normalized, "VAMPIRIC_ORB", StringComparison.Ordinal)
                || string.Equals(normalized, "GRIM_WHEEL", StringComparison.Ordinal)
                ? ProjectileMotionBoomerangCaster
                : string.Empty;
        }

        private static string ResolveLocalAbilityId(DbConnection conn, Identity caster, string spellId)
        {
            ActiveActionBarAction action =
                ActiveActionBarResolver.ResolveActiveSelectableActionForAction(conn, caster, spellId);
            return WireIdentifier.Normalize(action.AbilityId);
        }

        private void PrunePredictedSpellVfx(long nowMs)
        {
            var staleTokens = new List<string>();
            foreach (var entry in _pendingSpellVfxByToken)
            {
                if (nowMs > entry.Value.ExpiresAtMs)
                    staleTokens.Add(entry.Key);
            }

            foreach (string tokenKey in staleTokens)
            {
                if (_pendingSpellVfxByToken.TryGetValue(tokenKey, out PendingPredictedSpellVfx pending)
                    && !string.IsNullOrWhiteSpace(pending.PredictedProjectileKey))
                {
                    ProjectileVisuals.RemovePredicted(pending.PredictedProjectileKey);
                }
                _pendingSpellVfxByToken.Remove(tokenKey);
            }

            var staleInstances = new List<string>();
            foreach (var entry in _spellVfxTokenByActionInstance)
            {
                if (!_pendingSpellVfxByToken.ContainsKey(entry.Value))
                    staleInstances.Add(entry.Key);
            }
            foreach (string actionInstanceId in staleInstances)
                _spellVfxTokenByActionInstance.Remove(actionInstanceId);
        }

        private static string SpellTokenKey(string predictedActionId, ulong clientActionSeq)
            => $"{predictedActionId}:{clientActionSeq}";

        private static string PredictedActionInstanceId(string tokenKey)
            => $"predicted_spell_vfx:{tokenKey}";

        private static string ResolveTrigger(string eventType, bool isSpell)
        {
            if (string.Equals(eventType, CombatEventTypes.AreaImpact, StringComparison.Ordinal))
                return TriggerAreaImpact;

            if (isSpell)
            {
                return eventType switch
                {
                    CombatEventTypes.Cast => TriggerSpellCast,
                    CombatEventTypes.Release => TriggerSpellRelease,
                    CombatEventTypes.Contact => TriggerSpellImpact,
                    CombatEventTypes.Impact => TriggerSpellImpact,
                    CombatEventTypes.Block => TriggerSpellBlock,
                    CombatEventTypes.Parry => TriggerSpellParry,
                    CombatEventTypes.Miss => TriggerSpellFizzle,
                    CombatEventTypes.Fizzle => TriggerSpellFizzle,
                    CombatEventTypes.StatusEnd => TriggerStatusEnd,
                    _ => string.Empty,
                };
            }

            return eventType switch
            {
                CombatEventTypes.Cast => TriggerMeleeCast,
                CombatEventTypes.Impact => TriggerMeleeImpact,
                CombatEventTypes.Block => TriggerMeleeBlock,
                CombatEventTypes.Parry => TriggerMeleeParry,
                CombatEventTypes.Miss => string.Empty,
                _ => string.Empty,
            };
        }

        private static string ResolveAuthoredStrikeId(DbConnection conn, CombatEvent row)
        {
            string direct = WireIdentifier.Normalize(row.ActionKind);
            if (EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetEntity(row.Caster, out PlayerEntity caster))
            {
                string combatProfile = CombatProfileResolver.ResolveForEntity(conn, caster);
                string authored = CombatActionIds.ResolveAuthoredStrikeId(conn, combatProfile, row.ActionKind);
                if (!string.IsNullOrWhiteSpace(authored))
                    return WireIdentifier.Normalize(authored);
            }

            return direct;
        }

        private static bool IsTerminalEvent(CombatEvent row)
        {
            return row.EventType == CombatEventTypes.Impact
                || row.EventType == CombatEventTypes.Block
                || row.EventType == CombatEventTypes.Parry
                || row.EventType == CombatEventTypes.Miss
                || row.EventType == CombatEventTypes.Fizzle;
        }

        private void DispatchCue(CombatVfxFact fact, CombatVfxCueCatalog cue)
        {
            string vfxRole = WireIdentifier.Normalize(cue.VfxRole);
            if (string.Equals(vfxRole, VfxRoleProjectileBody, StringComparison.Ordinal)
                || string.Equals(vfxRole, VfxRoleProjectileTrail, StringComparison.Ordinal))
                return;
            if (string.Equals(vfxRole, VfxRoleTravelBody, StringComparison.Ordinal))
            {
                TravelVisuals.Start(cue, fact.ToTemplateContext(cue.Key));
                return;
            }

            string attachMode = WireIdentifier.Normalize(cue.AttachMode);
            if (string.IsNullOrWhiteSpace(attachMode))
                attachMode = AttachModeSpawnWorld;
            if (!string.Equals(attachMode, AttachModeSpawnWorld, StringComparison.Ordinal)
                && !string.Equals(attachMode, AttachModeFollowAnchor, StringComparison.Ordinal)
                && !string.Equals(attachMode, AttachModeFollowGroundPosition, StringComparison.Ordinal)
                && !string.Equals(attachMode, AttachModeWorldAlignedToFacing, StringComparison.Ordinal))
                return;

            Vector3 position = CombatVFXAnchorResolver.ResolvePosition(fact.ToAnchorFact(), cue);
            bool followsTransform = string.Equals(attachMode, AttachModeFollowAnchor, StringComparison.Ordinal);
            bool followsGroundPosition = string.Equals(
                attachMode,
                AttachModeFollowGroundPosition,
                StringComparison.Ordinal);
            Transform? followAnchor = followsTransform || followsGroundPosition
                ? CombatVFXAnchorResolver.ResolveFollowAnchor(fact.ToAnchorFact(), cue)
                : null;
            if ((followsTransform || followsGroundPosition) && followAnchor == null)
                return;

            bool directionalFlamethrower = followsTransform
                && string.Equals(
                    WireIdentifier.Normalize(cue.VfxId),
                    FlamethrowerChannelVfxId,
                    StringComparison.Ordinal);
            Quaternion rotation = string.Equals(attachMode, AttachModeWorldAlignedToFacing, StringComparison.Ordinal)
                || directionalFlamethrower
                ? ResolveWorldAlignedFacingRotation(fact.Direction)
                : Quaternion.identity;

            _lifecycle ??= new CombatVFXLifecycleRegistry(this);
            _lifecycle.Spawn(
                cue,
                fact.ToTemplateContext(cue.Key, followAnchor),
                position,
                rotation,
                followAnchor,
                followsGroundPosition);
        }

        private static Quaternion ResolveWorldAlignedFacingRotation(Vector3 direction)
        {
            var horizontal = new Vector3(direction.x, 0f, direction.z);
            return horizontal.sqrMagnitude > 0.000001f
                ? Quaternion.LookRotation(horizontal.normalized, Vector3.up)
                : Quaternion.identity;
        }

        private void OnDestroy()
        {
            UnsubscribeFromConnection();
            _lifecycle?.Dispose();
            _lifecycle = null;
            _travelVisuals?.Dispose();
            _travelVisuals = null;
            _projectileVisuals?.Dispose();
            _projectileVisuals = null;
            _matchingCues?.Clear();
            _cueResolver = null;
            if (_instance == this)
                _instance = null;
        }

        private void UnsubscribeFromConnection()
        {
            var conn = _subscribedConnection;
            if (conn == null)
                return;

            conn.Db.CombatEvent.OnInsert -= OnCombatEventInsert;
            conn.Db.SpecialMovementRuntime.OnInsert -= OnSpecialMovementRuntimeInsertForVfx;
            conn.Db.ProjectilePresentationEvent.OnInsert -= OnProjectilePresentationEventInsert;
            conn.Db.PredictedActionResult.OnInsert -= OnPredictedActionResultInsert;
            conn.Db.ActiveCast.OnDelete -= OnActiveCastDeleteForVfx;
            conn.Db.ActiveRadialEffect.OnInsert -= OnActiveRadialEffectInsertForVfx;
            conn.Db.ActiveRadialEffect.OnUpdate -= OnActiveRadialEffectUpdateForVfx;
            conn.Db.ActiveRadialEffect.OnDelete -= OnActiveRadialEffectDeleteForVfx;
            conn.Db.ActivePersistentArea.OnInsert -= OnActivePersistentAreaInsertForVfx;
            conn.Db.ActivePersistentArea.OnUpdate -= OnActivePersistentAreaUpdateForVfx;
            conn.Db.ActivePersistentArea.OnDelete -= OnActivePersistentAreaDeleteForVfx;
            conn.Db.StatusEffect.OnInsert -= OnStatusEffectInsertForVfx;
            conn.Db.StatusEffect.OnUpdate -= OnStatusEffectUpdateForVfx;
            conn.Db.StatusEffect.OnDelete -= OnStatusEffectDeleteForVfx;
            conn.Db.SpellDefinition.OnInsert -= OnSpellDefinitionInsertForVfx;
            conn.Db.SpellDefinition.OnUpdate -= OnSpellDefinitionUpdateForVfx;
            conn.Db.SpellDefinition.OnDelete -= OnSpellDefinitionDeleteForVfx;
            conn.Db.CombatVfxCueCatalog.OnInsert -= OnCombatVfxCueCatalogInsert;
            conn.Db.CombatVfxCueCatalog.OnUpdate -= OnCombatVfxCueCatalogUpdate;
            conn.Db.CombatVfxCueCatalog.OnDelete -= OnCombatVfxCueCatalogDelete;
            _subscribedConnection = null;
            _cueResolver?.MarkDirty();
            _projectileDeliveredSpellImpactByActionKind.Clear();
            _lifecycle?.DestroyAllRadialEffects();
            _activeRadialEffectVfxByKey.Clear();
            _activePersistentAreaVfxByKey.Clear();
            _lifecycle?.DestroyAllStatusEffects();
            _activeStatusEffectVfxById.Clear();
        }

        private readonly struct CombatVfxFact
        {
            public readonly string Trigger;
            public readonly string SpellId;
            public readonly string AbilityId;
            public readonly string StrikeId;
            public readonly int HitIndex;
            public readonly Identity Caster;
            public readonly Identity Hit;
            public readonly string ActionInstanceId;
            public readonly string ActionKind;
            public readonly Vector3 Origin;
            public readonly Vector3 Direction;
            public readonly Vector3 Point;
            public readonly float Speed;
            public readonly float MaxDistance;
            public readonly string ScalarKind;
            public readonly float ScalarValue;
            public readonly uint SequenceIndex;
            public readonly uint SequenceCount;
            public readonly bool IsSpell;

            public CombatVfxFact(
                string trigger,
                string spellId,
                string abilityId,
                string strikeId,
                int hitIndex,
                Identity caster,
                Identity hit,
                string actionInstanceId,
                string actionKind,
                Vector3 origin,
                Vector3 direction,
                Vector3 point,
                float speed,
                float maxDistance,
                string scalarKind,
                float scalarValue,
                uint sequenceIndex,
                uint sequenceCount,
                bool isSpell)
            {
                Trigger = trigger;
                SpellId = spellId;
                AbilityId = abilityId;
                StrikeId = strikeId;
                HitIndex = hitIndex;
                Caster = caster;
                Hit = hit;
                ActionInstanceId = actionInstanceId;
                ActionKind = actionKind;
                Origin = origin;
                Direction = direction;
                Point = point;
                Speed = speed;
                MaxDistance = maxDistance;
                ScalarKind = scalarKind;
                ScalarValue = scalarValue;
                SequenceIndex = sequenceIndex;
                SequenceCount = sequenceCount;
                IsSpell = isSpell;
            }

            public CombatVfxFact(
                CombatEvent row,
                string trigger,
                string spellId,
                string abilityId,
                string strikeId,
                int hitIndex)
            {
                Trigger = trigger;
                SpellId = spellId;
                AbilityId = abilityId;
                StrikeId = strikeId;
                HitIndex = hitIndex;
                Caster = row.Caster;
                Hit = row.Hit;
                ActionInstanceId = row.ActionInstanceId;
                ActionKind = row.ActionKind;
                Origin = new Vector3(row.OriginX, row.OriginY, row.OriginZ);
                Direction = new Vector3(row.DirX, row.DirY, row.DirZ);
                Point = new Vector3(row.PointX, row.PointY, row.PointZ);
                Speed = row.Speed;
                MaxDistance = row.MaxDistance;
                ScalarKind = row.ScalarKind;
                ScalarValue = row.ScalarValue;
                SequenceIndex = row.SequenceIndex;
                SequenceCount = row.SequenceCount;
                IsSpell = string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal);
            }

            public CombatVfxFact(
                ProjectilePresentationEvent row,
                string trigger,
                string spellId,
                string abilityId,
                string strikeId,
                int hitIndex)
            {
                Trigger = trigger;
                SpellId = spellId;
                AbilityId = abilityId;
                StrikeId = strikeId;
                HitIndex = hitIndex;
                Caster = row.Caster;
                Hit = row.Hit;
                ActionInstanceId = row.ActionInstanceId;
                ActionKind = row.ActionKind;
                Origin = new Vector3(row.OriginX, row.OriginY, row.OriginZ);
                Direction = new Vector3(row.DirX, row.DirY, row.DirZ);
                Point = new Vector3(row.PointX, row.PointY, row.PointZ);
                Speed = row.Speed;
                MaxDistance = row.MaxDistance;
                ScalarKind = CombatEventScalarKinds.None;
                ScalarValue = 0f;
                SequenceIndex = row.SequenceIndex;
                SequenceCount = row.SequenceCount;
                IsSpell = string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal);
            }

            public CombatVfxResolutionFact ToResolutionFact()
            {
                return new CombatVfxResolutionFact(IsSpell, Trigger, SpellId, AbilityId, StrikeId, HitIndex);
            }

            public CombatVfxAnchorFact ToAnchorFact()
            {
                return new CombatVfxAnchorFact(Caster, Hit, Origin, Point);
            }

            public CombatVFXTemplateContext ToTemplateContext(
                string cueKey,
                Transform? followAnchor = null,
                string? actionInstanceIdOverride = null)
            {
                return new CombatVFXTemplateContext(
                    cueKey,
                    actionInstanceIdOverride ?? ActionInstanceId,
                    ActionKind,
                    AbilityId,
                    Trigger,
                    Caster,
                    Hit,
                    Origin,
                    Direction,
                    Point,
                    Speed,
                    MaxDistance,
                    ScalarKind,
                    ScalarValue,
                    SequenceIndex,
                    SequenceCount,
                    followAnchor);
            }
        }
    }
}

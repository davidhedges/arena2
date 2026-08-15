#nullable enable
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using SpacetimeDB;
using SpacetimeDB.Types;
using Arena.Network;
using Arena.Simulation;
using Arena.Input;
using Arena.Combat;
using Arena.Presentation;
using Arena.Presentation.Appearance;
using Arena.Debugging;

namespace Arena.Entity
{
    /// <summary>
    /// Manages the lifecycle of player GameObjects from the currently scoped client cache.
    ///
    /// Receives raw table events from NetworkManager and translates them:
    ///   OnInsert  → spawn PlayerEntity
    ///   OnUpdate  → push new snapshot into SimState ONLY
    ///   OnDelete  → destroy PlayerEntity
    ///
    /// For the local player, configures the local movement stack:
    ///   - Adds LocalPlayerMotor (collision-aware local locomotion)
    ///   - Adds MovementNetDriver (input → server, reconciliation)
    ///   - Adds SpellInputHandler (spell keybinds)
    ///   - Configures Cinemachine to follow the local player
    ///
    /// INVARIANT: The ONLY class that creates or destroys player GameObjects.
    ///            Does NOT own cross-player visibility policy. Visibility now comes from
    ///            NetworkManager's runtime subscription scope.
    /// </summary>
    public class EntityRegistry : MonoBehaviour, IScopedPlayerCacheSink
    {
        private const string KnockdownStatusKind = "KNOCKDOWN";
        private const string StunStatusKind = "STUN";
        private const string FreezeStatusKind = "FREEZE";
        private const string IntimidatedStatusKind = "INTIMIDATED";
        private const string FearStatusKind = "FEAR";
        private const string DefenseBlockKind = "BLOCK";
        private const string DefenseParryKind = "PARRY";
        private const string CoupDeGraceGapCloseKind = "MELEE_GAP_CLOSE:DAGGER_COUP_DE_GRACE";
        private const float NpcVisualUnloadIdleDelaySeconds = 2f;

        public static EntityRegistry Instance { get; private set; } = null!;

        [Header("Player Prefab")]
        [Tooltip("Runtime player avatar prefab")]
        [SerializeField] private GameObject? playerPrefab;

        [Header("Animation")]
        [Tooltip("Fallback combat animation set used until per-class sets are configured")]
        [SerializeField] private CombatAnimationSet? _defaultAnimationSet;
        [Tooltip("Optional shared action profile used across combat styles")]
        [SerializeField] private SharedActionProfile? _sharedActionProfile;

        private readonly Dictionary<Identity, PlayerEntity> _players = new();
        private readonly Dictionary<Identity, NpcEntity> _npcs = new();
        private readonly Dictionary<Identity, PendingNpcSpawn> _pendingNpcSpawns = new();
        private readonly Dictionary<Identity, NpcVisualResourceCache.Lease> _npcVisualLeases = new();
        private readonly NpcVisualResourceCache _npcVisualCache = new();
        private readonly Dictionary<Identity, ActiveWorldInteraction>
            _activeWorldInteractions = new();
        private readonly Dictionary<Identity, long> _lastDefenseStartMicros = new();
        private readonly List<PendingHitReaction> _pendingHitReactions = new();
        private readonly LocalWorldRuntimeCoordinator _localWorldCoordinator = new(new LocalMovementWorldContext());
        private readonly ScopedPlayerCacheHydrator _scopedPlayerCacheHydrator = new();

        private PlayerEntity? _localPlayerEntity;
        private EquipmentAppearanceCatalog? _equipmentAppearanceCatalog;
        private Coroutine? _npcVisualUnloadCoroutine;

        public PlayerEntity? LocalPlayerEntity
        {
            get
            {
                if (_localPlayerEntity != null && _localPlayerEntity.IsDestroyed)
                    _localPlayerEntity = null;
                return _localPlayerEntity;
            }
            private set => _localPlayerEntity = value;
        }

        public IEnumerable<PlayerEntity> AllPlayers
        {
            get
            {
                foreach (var entity in _players.Values)
                {
                    if (!entity.IsDestroyed)
                        yield return entity;
                }
            }
        }

        public IEnumerable<NpcEntity> AllNpcs
        {
            get
            {
                foreach (var entity in _npcs.Values)
                {
                    if (!entity.IsDestroyed)
                        yield return entity;
                }
            }
        }

        internal bool TryGetLocalPredictionEnvironment(out IMovementEnvironment? environment)
            => _localWorldCoordinator.WorldContext.TryGetPredictionEnvironment(out environment);

        private Identity _localIdentity;
        private bool _hasLocalIdentity;

        private readonly struct PendingHitReaction
        {
            public readonly Identity Target;
            public readonly Vector3 Direction;
            public readonly int FrameQueued;

            public PendingHitReaction(Identity target, Vector3 direction, int frameQueued)
            {
                Target = target;
                Direction = direction;
                FrameQueued = frameQueued;
            }
        }

        private sealed class PendingNpcSpawn
        {
            internal NpcInstance LatestInstance;
            internal readonly string VisualId;

            internal PendingNpcSpawn(NpcInstance instance)
            {
                LatestInstance = instance;
                VisualId = NormalizeVisualId(instance.VisualId);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("EntityRegistry");
            DontDestroyOnLoad(go);
            var reg = go.AddComponent<EntityRegistry>();
            reg.playerPrefab = RuntimeAvatarPrefabResolver.LoadRuntimePlayerPrefab();
            if (reg.playerPrefab == null)
                Debug.LogWarning("[EntityRegistry] No runtime player avatar prefab could be resolved from Resources — using capsule fallback.");
            else if (RuntimeAvatarPrefabResolver.ResolvedPrefabUsesFallback())
                Debug.LogWarning(
                    "[EntityRegistry] Resources/PlayerArmature is not a valid runtime avatar prefab; " +
                    "falling back to Resources/PlayerArmature 1.");

            reg._defaultAnimationSet = CombatAnimationSetCatalog.Resolve(CombatProfileIds.Default);
            if (reg._defaultAnimationSet == null)
                Debug.LogWarning("[EntityRegistry] Default combat animation set not found in Resources — animation set playback will use controller defaults.");
            reg._sharedActionProfile = Resources.Load<SharedActionProfile>("ActionProfiles/SharedActions");
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            if (CharacterAppearanceCatalogSet.TryLoadDefault(out CharacterAppearanceCatalogSet catalogs, out string catalogError))
                _equipmentAppearanceCatalog = catalogs.EquipmentAppearanceCatalog;
            else
                Debug.LogWarning($"[EntityRegistry] Could not preload character appearance catalogs: {catalogError}");

            PurgeScenePlacedPlayers();

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ClearAllPlayers();
            CancelScheduledNpcVisualUnload();
            ClearAllNpcs(scheduleVisualUnload: false);
            _npcVisualCache.ReleaseUnusedProfiles();
            if (Instance == this)
                Instance = null!;
        }

        private void Update()
        {
            FlushPendingHitReactions();
            TickNpcPresentation(Time.deltaTime);
        }

        private void TickNpcPresentation(float dt)
        {
            foreach (var entity in _npcs.Values)
            {
                if (!entity.IsDestroyed)
                    entity.TickPresentation(dt);
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            PurgeScenePlacedPlayers();
            if (ShouldSuppressPresentationInScene(scene.name))
            {
                ClearAllPlayers();
                ClearAllNpcs();
                return;
            }

            RehydratePlayersFromScopedCache();
            RehydrateNpcsFromCache();
            RefreshPlayerPresentationForScene(scene.name);
            RefreshNpcPresentationForScene(scene.name);

            // Re-target the Cinemachine camera in the new scene to the local player.
            if (LocalPlayerEntity != null && !LocalPlayerEntity.IsDestroyed && !ShouldSuppressPresentationInScene(scene.name))
                LocalPlayerCamera.SetTarget(LocalPlayerEntity.GetPresentationRoot());
        }

        public void SetLocalIdentity(Identity identity)
        {
            _localIdentity = identity;
            _hasLocalIdentity = true;
            _localWorldCoordinator.SetLocalIdentity(identity);
        }

        internal void ClearForNetworkReconnect()
        {
            _hasLocalIdentity = false;
            _localWorldCoordinator.ClearForNetworkReconnect();
            _lastDefenseStartMicros.Clear();
            _activeWorldInteractions.Clear();
            ClearAllPlayers();
            ClearAllNpcs();
        }

        // -------------------------------------------------------------------
        // PlayerPhysics table callbacks
        // -------------------------------------------------------------------

        public void OnPlayerPhysicsInsert(EventContext ctx, PlayerPhysics row)
        {
            ArenaServerClock.RecordObservedServerTimestampMicros(row.UpdatedAt.MicrosecondsSinceUnixEpoch);
            SpawnOrUpdatePlayer(row);
        }

        private void SetupLocalPlayer(PlayerEntity entity)
        {
            var go = entity.GameObject;
            var stateProvider = go.AddComponent<LocalPlayerStateProvider>();

            LocalPlayerInputSource inputSource = go.AddComponent<LocalPlayerInputSource>();

            // Starter Assets controller is optional local prefab authoring, not a
            // checked-in project dependency.
            if (StarterAssetsRuntimeStripper.TryReadThirdPersonCameraConfig(go, out var cameraConfig))
            {
                var cameraTarget = cameraConfig.CameraTarget;
                if (cameraTarget != null)
                {
                    var orbit = go.AddComponent<Presentation.CameraOrbitController>();
                    orbit.TopClamp = cameraConfig.TopClamp;
                    orbit.BottomClamp = cameraConfig.BottomClamp;
                    orbit.CameraAngleOverride = cameraConfig.CameraAngleOverride;
                    orbit.CameraSensitivity = cameraConfig.CameraSensitivity * 5.0f;
                    orbit.Initialize(cameraTarget, inputSource, stateProvider);
                }
            }

            // Strip Starter Assets runtime input/movement after extracting camera config.
            PlayerEntity.DisablePlayerInput(go);

            var motor = go.AddComponent<LocalPlayerMotor>();
            var commandHistory = new MovementCommandBuffer(MovementNetcodeConfig.MaxPendingCommands);
            var leadController = new InputLeadController();
            motor.Initialize(inputSource, GameplayTuning.DefaultHitRadius, GameplayTuning.DefaultHitHeight);

            var netDriver = go.AddComponent<MovementNetDriver>();
            netDriver.Initialize(entity.SimState, commandHistory, leadController);

            var presentationDriver = go.AddComponent<LocalPresentationDriver>();
            presentationDriver.Initialize(entity.SimState, entity.GetPresentationRoot());

            motor.EnablePredictedAuthority(stateProvider);
            var predictionDriver = go.AddComponent<LocalMovementPredictionDriver>();
            predictionDriver.Initialize(
                entity.SimState,
                motor,
                _localWorldCoordinator.WorldContext,
                stateProvider,
                commandHistory,
                leadController,
                presentationDriver);

            // Add SpellInputHandler for spell keybinds.
            go.AddComponent<SpellInputHandler>();
            go.AddComponent<MeleeInputHandler>();
            go.AddComponent<DefensePredictionReconciler>();
            go.AddComponent<MeleeRangeGuideIndicator>().Initialize(entity);

            // Notify any camera follower that the local player has spawned.
            if (!ShouldSuppressPresentationInCurrentScene())
                LocalPlayerCamera.SetTarget(go.transform);
        }

        public void OnPlayerPhysicsUpdate(EventContext ctx, PlayerPhysics oldRow, PlayerPhysics newRow)
        {
            ArenaServerClock.RecordObservedServerTimestampMicros(newRow.UpdatedAt.MicrosecondsSinceUnixEpoch);
            SpawnOrUpdatePlayer(newRow);
        }

        public void OnPlayerPhysicsDelete(EventContext ctx, PlayerPhysics row)
        {
            if (!_players.TryGetValue(row.Identity, out var entity)) return;
            entity.Destroy();
            RemovePlayerReference(row.Identity, entity);
            Debug.Log($"[EntityRegistry] Removed {row.Identity}");
        }

        // -------------------------------------------------------------------
        // Player table callbacks (username)
        // -------------------------------------------------------------------

        public void OnPlayerInsert(EventContext ctx, Player row)
        {
            ApplyUsername(row);
        }

        public void OnPlayerUpdate(EventContext ctx, Player oldRow, Player newRow)
        {
            ApplyUsername(newRow);
        }

        public void OnPlayerDelete(EventContext ctx, Player row) { }

        public void OnCharacterAppearanceInsert(EventContext ctx, CharacterAppearance row)
        {
            ApplyCharacterAppearance(row);
        }

        public void OnCharacterAppearanceUpdate(EventContext ctx, CharacterAppearance oldRow, CharacterAppearance newRow)
        {
            if (HasSameVisualAppearance(oldRow, newRow))
                return;

            ApplyCharacterAppearance(newRow);
        }

        public void OnCharacterAppearanceDelete(EventContext ctx, CharacterAppearance row) { }

        // -------------------------------------------------------------------
        // PlayerState table callbacks (HP, alive)
        // -------------------------------------------------------------------

        public void OnPlayerStateInsert(EventContext ctx, PlayerState row)
        {
            ApplyState(row);
        }

        public void OnPlayerStateUpdate(EventContext ctx, PlayerState oldRow, PlayerState newRow)
        {
            ApplyState(newRow);
        }

        public void OnPlayerStateDelete(EventContext ctx, PlayerState row) { }

        public void OnEquipmentLoadoutInsert(EventContext ctx, EquipmentLoadout row)
        {
            ApplyOwnerCombatProfile(row.Owner);
        }

        public void OnEquipmentLoadoutUpdate(EventContext ctx, EquipmentLoadout oldRow, EquipmentLoadout newRow)
        {
            ApplyOwnerCombatProfile(newRow.Owner);
        }

        public void OnEquipmentLoadoutDelete(EventContext ctx, EquipmentLoadout row)
        {
            ApplyOwnerCombatProfile(row.Owner);
        }

        public void OnPlayerEquipmentPresentationInsert(EventContext ctx, PlayerEquipmentPresentation row)
        {
            ApplyPlayerEquipmentPresentation(row);
        }

        public void OnPlayerEquipmentPresentationUpdate(EventContext ctx, PlayerEquipmentPresentation oldRow, PlayerEquipmentPresentation newRow)
        {
            bool weaponsChanged = !HasSameWeaponPresentation(oldRow, newRow);
            bool armorChanged = !HasSameArmorPresentation(oldRow, newRow);
            if (weaponsChanged)
                ApplyOwnerWeaponPresentation(newRow.Owner, newRow);
            if (armorChanged)
                ApplyOwnerArmorPresentation(newRow.Owner, newRow);
        }

        public void OnPlayerEquipmentPresentationDelete(EventContext ctx, PlayerEquipmentPresentation row)
        {
            if (!TryGetLivePlayer(row.Owner, out var entity))
                return;

            entity.SetEquippedWeaponVisuals(System.Array.Empty<EquippedWeaponVisual>());
            entity.SetEquippedArmorItemDefIdsBySlot(new Dictionary<string, string>(System.StringComparer.Ordinal));
        }

        public void OnActiveCombatDisciplineInsert(EventContext ctx, ActiveCombatDiscipline row)
        {
            ApplyOwnerCombatProfile(row.Owner);
        }

        public void OnActiveCombatDisciplineUpdate(EventContext ctx, ActiveCombatDiscipline oldRow, ActiveCombatDiscipline newRow)
        {
            ApplyOwnerCombatProfile(oldRow.Owner);
            if (oldRow.Owner != newRow.Owner)
                ApplyOwnerCombatProfile(newRow.Owner);
        }

        public void OnActiveCombatDisciplineDelete(EventContext ctx, ActiveCombatDiscipline row)
        {
            ApplyOwnerCombatProfile(row.Owner);
        }

        public void OnActiveCombatModeInsert(EventContext ctx, ActiveCombatMode row)
        {
            ApplyOwnerCombatMode(row.Owner);
        }

        public void OnActiveCombatModeUpdate(EventContext ctx, ActiveCombatMode oldRow, ActiveCombatMode newRow)
        {
            ApplyOwnerCombatMode(oldRow.Owner);
            if (oldRow.Owner != newRow.Owner)
                ApplyOwnerCombatMode(newRow.Owner);
        }

        public void OnActiveCombatModeDelete(EventContext ctx, ActiveCombatMode row)
        {
            ApplyOwnerCombatMode(row.Owner);
        }

        public void OnItemInstanceInsert(EventContext ctx, ItemInstance row)
        {
            if (IsCombatProfileItemReference(row.CurrentOwner, row.ItemInstanceId))
                ApplyCombatProfileForNullableOwner(row.CurrentOwner);
        }

        public void OnItemInstanceUpdate(EventContext ctx, ItemInstance oldRow, ItemInstance newRow)
        {
            bool combatResolutionChanged = oldRow.CurrentOwner != newRow.CurrentOwner
                || !string.Equals(oldRow.ItemDefId, newRow.ItemDefId, System.StringComparison.Ordinal);
            if (!combatResolutionChanged)
                return;

            bool oldOwnerAffected = IsCombatProfileItemReference(oldRow.CurrentOwner, oldRow.ItemInstanceId);
            bool newOwnerAffected = IsCombatProfileItemReference(newRow.CurrentOwner, newRow.ItemInstanceId);
            if (oldOwnerAffected)
                ApplyCombatProfileForNullableOwner(oldRow.CurrentOwner);
            if (newOwnerAffected && (!oldOwnerAffected || oldRow.CurrentOwner != newRow.CurrentOwner))
                ApplyCombatProfileForNullableOwner(newRow.CurrentOwner);
        }

        public void OnItemInstanceDelete(EventContext ctx, ItemInstance row)
        {
            if (IsCombatProfileItemReference(row.CurrentOwner, row.ItemInstanceId))
                ApplyCombatProfileForNullableOwner(row.CurrentOwner);
        }

        public void OnItemDefinitionInsert(EventContext ctx, ItemDefinition row)
        {
            ApplyAllEquipmentPresentations();
        }

        public void OnItemDefinitionUpdate(EventContext ctx, ItemDefinition oldRow, ItemDefinition newRow)
        {
            ApplyAllEquipmentPresentations();
        }

        public void OnCombatEngagementInsert(EventContext ctx, CombatEngagement row)
        {
            ApplyCombatEngagement(row);
        }

        public void OnCombatEngagementUpdate(EventContext ctx, CombatEngagement oldRow, CombatEngagement newRow)
        {
            ApplyCombatEngagement(newRow);
        }

        public void OnCombatEngagementDelete(EventContext ctx, CombatEngagement row)
        {
            if (TryGetLivePlayer(row.Owner, out var entity))
                entity.ClearGameplayCombatEngagement();
        }

        // -------------------------------------------------------------------
        // PlayerWorld table callbacks (local authoritative world context)
        // -------------------------------------------------------------------

        public void OnPlayerWorldInsert(EventContext ctx, PlayerWorld row)
        {
            _localWorldCoordinator.OnPlayerWorldInsert(row);
        }

        public void OnPlayerWorldUpdate(EventContext ctx, PlayerWorld oldRow, PlayerWorld newRow)
        {
            _localWorldCoordinator.OnPlayerWorldUpdate(newRow);
        }

        public void OnPlayerWorldDelete(EventContext ctx, PlayerWorld row)
        {
            _localWorldCoordinator.OnPlayerWorldDelete(row);
        }

        // -------------------------------------------------------------------
        // NPC table callbacks
        // -------------------------------------------------------------------

        public void OnNpcInstanceInsert(EventContext ctx, NpcInstance row)
        {
            SpawnOrUpdateNpc(row);
        }

        public void OnNpcInstanceUpdate(EventContext ctx, NpcInstance oldRow, NpcInstance newRow)
        {
            SpawnOrUpdateNpc(newRow);
        }

        public void OnNpcInstanceDelete(EventContext ctx, NpcInstance row)
        {
            RemoveNpcPresentation(row.Identity);
        }

        public void OnNpcPhysicsInsert(EventContext ctx, NpcPhysics row)
        {
            ArenaServerClock.RecordObservedServerTimestampMicros(row.UpdatedAt.MicrosecondsSinceUnixEpoch);
            ApplyNpcPhysics(row);
        }

        public void OnNpcPhysicsUpdate(EventContext ctx, NpcPhysics oldRow, NpcPhysics newRow)
        {
            ArenaServerClock.RecordObservedServerTimestampMicros(newRow.UpdatedAt.MicrosecondsSinceUnixEpoch);
            ApplyNpcPhysics(newRow);
        }

        public void OnNpcPhysicsDelete(EventContext ctx, NpcPhysics row)
        {
            RemoveNpcPresentation(row.Identity);
        }

        public void OnNpcStateInsert(EventContext ctx, NpcState row)
        {
            ApplyNpcState(row);
        }

        public void OnNpcStateUpdate(EventContext ctx, NpcState oldRow, NpcState newRow)
        {
            ApplyNpcState(newRow);
        }

        public void OnNpcStateDelete(EventContext ctx, NpcState row)
        {
            RemoveNpcPresentation(row.Identity);
        }

        public void OnPlayerOpenWorldSceneInsert(EventContext ctx, PlayerOpenWorldScene row)
        {
            _localWorldCoordinator.OnPlayerOpenWorldSceneInsert(row);
        }

        public void OnPlayerOpenWorldSceneUpdate(EventContext ctx, PlayerOpenWorldScene oldRow, PlayerOpenWorldScene newRow)
        {
            _localWorldCoordinator.OnPlayerOpenWorldSceneUpdate(newRow);
        }

        public void OnPlayerOpenWorldSceneDelete(EventContext ctx, PlayerOpenWorldScene row)
        {
            _localWorldCoordinator.OnPlayerOpenWorldSceneDelete(row);
        }

        // -------------------------------------------------------------------
        // ArenaInstance table callbacks (seed lookup for local instance prediction)
        // -------------------------------------------------------------------

        public void OnArenaInstanceInsert(EventContext ctx, ArenaInstance row)
        {
            _localWorldCoordinator.OnArenaInstanceInsert(row);
        }

        public void OnArenaInstanceUpdate(EventContext ctx, ArenaInstance oldRow, ArenaInstance newRow)
        {
            _localWorldCoordinator.OnArenaInstanceUpdate(newRow);
        }

        public void OnArenaInstanceDelete(EventContext ctx, ArenaInstance row)
        {
            _localWorldCoordinator.OnArenaInstanceDelete(row);
        }

        // -------------------------------------------------------------------
        // StatusEffect table callbacks (visual tinting)
        // -------------------------------------------------------------------

        public void OnStatusEffectInsert(EventContext ctx, StatusEffect row)
        {
            ApplyStatusEffect(row);
            RefreshStatusPresentation(row.Target);
            TriggerStaggerPresentation(row);
        }

        public void OnStatusEffectUpdate(EventContext ctx, StatusEffect oldRow, StatusEffect newRow)
        {
            if (oldRow.EffectKind != newRow.EffectKind)
            {
                if (TryGetLivePlayer(oldRow.Target, out var e)) e.RemoveStatusEffect(oldRow.EffectKind);
                else if (TryGetLiveNpc(oldRow.Target, out var n)) n.RemoveStatusEffect(oldRow.EffectKind);
                if (TryGetLivePlayer(newRow.Target, out var e2)) e2.ApplyStatusEffect(newRow.EffectKind);
                else if (TryGetLiveNpc(newRow.Target, out var n2)) n2.ApplyStatusEffect(newRow.EffectKind);
            }
            RefreshStatusPresentation(oldRow.Target);
            if (oldRow.Target != newRow.Target)
                RefreshStatusPresentation(newRow.Target);

            if (IsStaggerStatus(newRow)
                && (oldRow.AppliedAt.MicrosecondsSinceUnixEpoch != newRow.AppliedAt.MicrosecondsSinceUnixEpoch
                    || oldRow.ExpiresAt.MicrosecondsSinceUnixEpoch != newRow.ExpiresAt.MicrosecondsSinceUnixEpoch
                    || oldRow.Source != newRow.Source))
            {
                TriggerStaggerPresentation(newRow);
            }
        }

        public void OnStatusEffectDelete(EventContext ctx, StatusEffect row)
        {
            if (TryGetLivePlayer(row.Target, out var entity))
                entity.RemoveStatusEffect(row.EffectKind);
            else if (TryGetLiveNpc(row.Target, out var npc))
                npc.RemoveStatusEffect(row.EffectKind);
            RefreshStatusPresentation(row.Target, row.StatusId);
        }

        // -------------------------------------------------------------------
        // ActiveCast table callbacks
        // -------------------------------------------------------------------
        //
        // Instant spell presentation is routed from authoritative COMBAT_CAST
        // CombatEvents. Cast-time and authored-hold spell scheduling is routed
        // from ActiveCast so release/cancel timing stays tied to the server row.

        public void OnActiveCastInsert(EventContext ctx, ActiveCast row)
        {
            if (TryGetLiveNpc(row.Caster, out var npc))
            {
                npc.RequestCombatAnimation(CombatAnimationRequest.AuthoritativeSpell(
                    row.Kind,
                    row.StartedAt.MicrosecondsSinceUnixEpoch / 1000L,
                    CombatSpellAnimationPhase.HoldStart));
                return;
            }
            if (TryGetCastTimeMs(row.Kind, out ulong castTimeMs)
                && TryGetLivePlayer(row.Caster, out var entity)
                && ShouldForwardSpellActiveCast(entity, row.Kind, castTimeMs))
            {
                entity.OnActiveCastInsert(row, castTimeMs);
            }
        }

        public void OnActiveCastUpdate(EventContext ctx, ActiveCast oldRow, ActiveCast newRow)
        {
            if (TryGetCastTimeMs(newRow.Kind, out ulong castTimeMs)
                && TryGetLivePlayer(newRow.Caster, out var entity)
                && ShouldForwardSpellActiveCast(entity, newRow.Kind, castTimeMs))
            {
                entity.OnActiveCastUpdate(newRow, castTimeMs);
            }
        }

        public void OnActiveCastDelete(EventContext ctx, ActiveCast row)
        {
            if (TryGetLivePlayer(row.Caster, out var entity))
                entity.OnActiveCastDelete(row);
        }

        private static bool ShouldForwardSpellActiveCast(PlayerEntity entity, string spellActionId, ulong castTimeMs)
        {
            return castTimeMs > 0UL || entity.UsesSpellCastHoldPresentation(spellActionId);
        }

        public void OnPredictedActionResultInsert(EventContext ctx, PredictedActionResult row)
        {
            _ = ctx;
            if (!_hasLocalIdentity
                || row.Family != PredictedActionFamily.SpellCast
                || row.Owner != _localIdentity)
            {
                return;
            }

            LocalPlayerEntity?.OnPredictedActionResultInsert(row);
        }

        public void OnMovementActionStateInsert(EventContext ctx, MovementActionState row)
        {
            ApplyMovementActionState(row, true);
        }

        public void OnMovementActionStateUpdate(EventContext ctx, MovementActionState oldRow, MovementActionState newRow)
        {
            bool shouldTriggerStart = oldRow.StartedAt.MicrosecondsSinceUnixEpoch != newRow.StartedAt.MicrosecondsSinceUnixEpoch
                || !string.Equals(oldRow.Kind, newRow.Kind, System.StringComparison.OrdinalIgnoreCase);
            ApplyMovementActionState(newRow, shouldTriggerStart);
        }

        public void OnMovementActionStateDelete(EventContext ctx, MovementActionState row)
        {
            if (TryGetLivePlayer(row.Owner, out var entity))
            {
                entity.ClearMovementActionState();
            }
        }

        public void OnSpecialMovementRuntimeInsert(EventContext ctx, SpecialMovementRuntime row)
        {
            ApplySpecialMovementRuntime(row);
        }

        public void OnSpecialMovementRuntimeUpdate(EventContext ctx, SpecialMovementRuntime oldRow, SpecialMovementRuntime newRow)
        {
            ApplySpecialMovementRuntime(newRow);
        }

        public void OnSpecialMovementRuntimeDelete(EventContext ctx, SpecialMovementRuntime row)
        {
            if (TryGetLivePlayer(row.Owner, out var entity))
                entity.ClearSpecialMovementRuntime();
        }

        private void ApplySpecialMovementRuntime(SpecialMovementRuntime row)
        {
            if (!TryGetLivePlayer(row.Owner, out var entity))
                return;

            entity.SetSpecialMovementRuntime(row);
            if (entity.IsLocalPlayer
                && string.Equals(
                    row.Kind,
                    CoupDeGraceGapCloseKind,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                entity.GameObject.GetComponent<CameraOrbitController>()?.AlignBehind(
                    row.FacingYawStart);
            }
        }

        public void OnLingeringShadeStateInsert(EventContext ctx, LingeringShadeState row)
        {
            ApplyLingeringShadeState(row);
        }

        public void OnLingeringShadeStateUpdate(
            EventContext ctx,
            LingeringShadeState oldRow,
            LingeringShadeState newRow)
        {
            ApplyLingeringShadeState(newRow);
        }

        public void OnLingeringShadeStateDelete(EventContext ctx, LingeringShadeState row)
        {
            if (TryGetLivePlayer(row.Owner, out var entity))
                entity.ClearLingeringShadeState();
        }

        private void ApplyLingeringShadeState(LingeringShadeState row)
        {
            ArenaServerClock.RecordObservedServerTimestampMicros(row.CreatedAt.MicrosecondsSinceUnixEpoch);
            if (TryGetLivePlayer(row.Owner, out var entity))
                entity.SetLingeringShadeState(row);
        }

        // -------------------------------------------------------------------
        // PlayerResource table callbacks (class resources)
        // -------------------------------------------------------------------

        public void OnPlayerResourceInsert(EventContext ctx, PlayerResource row)
        {
            ApplyPlayerResource(row);
        }

        public void OnPlayerResourceUpdate(EventContext ctx, PlayerResource oldRow, PlayerResource newRow)
        {
            ApplyPlayerResource(newRow);
        }

        public void OnPlayerResourceDelete(EventContext ctx, PlayerResource row)
        {
            if (!TryGetLivePlayer(row.Owner, out var entity))
                return;

            entity.ClearResource(row.Kind);
        }

        // -------------------------------------------------------------------
        // DefenseState table callbacks (block/parry presentation)
        // -------------------------------------------------------------------

        public void OnDefenseStateInsert(EventContext ctx, DefenseState row)
        {
            ApplyDefenseState(row, true);
        }

        public void OnDefenseStateUpdate(EventContext ctx, DefenseState oldRow, DefenseState newRow)
        {
            ApplyDefenseState(newRow, oldRow.StartedAt.MicrosecondsSinceUnixEpoch != newRow.StartedAt.MicrosecondsSinceUnixEpoch);
        }

        public void OnDefenseStateDelete(EventContext ctx, DefenseState row)
        {
            if (TryGetLivePlayer(row.Owner, out var entity))
            {
                entity.SetBlocking(false);
                entity.SetParryArmed(false);
            }
            _lastDefenseStartMicros.Remove(row.Owner);
        }

        // -------------------------------------------------------------------

        public bool TryGetEntity(Identity id, out PlayerEntity entity)
            => TryGetLivePlayer(id, out entity);

        public void OnActiveWorldInteractionInsert(
            EventContext context,
            ActiveWorldInteraction row)
        {
            _ = context;
            ApplyActiveWorldInteraction(row);
        }

        public void OnActiveWorldInteractionUpdate(
            EventContext context,
            ActiveWorldInteraction oldRow,
            ActiveWorldInteraction row)
        {
            _ = context;
            if (!string.Equals(
                    oldRow.ActionInstanceId,
                    row.ActionInstanceId,
                    System.StringComparison.Ordinal)
                && TryGetLivePlayer(oldRow.Actor, out PlayerEntity oldEntity))
            {
                oldEntity.EndWorldInteractionAnimation(
                    oldRow.ActionInstanceId,
                    completed: false);
            }
            ApplyActiveWorldInteraction(row);
        }

        public void OnActiveWorldInteractionDelete(
            EventContext context,
            ActiveWorldInteraction row)
        {
            _ = context;
            if (_activeWorldInteractions.TryGetValue(
                    row.Actor,
                    out ActiveWorldInteraction active)
                && string.Equals(
                    active.ActionInstanceId,
                    row.ActionInstanceId,
                    System.StringComparison.Ordinal))
            {
                _activeWorldInteractions.Remove(row.Actor);
            }

            if (!TryGetLivePlayer(row.Actor, out PlayerEntity entity))
                return;

            WorldDoorState? committedDoor =
                context.Db.WorldDoorState.DoorStateId.Find(row.TargetStateId);
            bool completed = committedDoor != null
                && committedDoor.IsOpen == row.DesiredOpen
                && committedDoor.Revision > row.ObservedRevision;
            entity.EndWorldInteractionAnimation(
                row.ActionInstanceId,
                completed);
        }

        private void ApplyActiveWorldInteraction(ActiveWorldInteraction row)
        {
            _activeWorldInteractions[row.Actor] = row;
            if (TryGetLivePlayer(row.Actor, out PlayerEntity entity))
                entity.BeginWorldInteractionAnimation(row);
        }

        public bool TryGetEntityByHex(string identityHex, out PlayerEntity entity)
        {
            foreach (var pair in _players)
            {
                if (string.Equals(pair.Key.ToString(), identityHex, System.StringComparison.OrdinalIgnoreCase))
                {
                    return TryGetLivePlayer(pair.Key, out entity);
                }
            }

            entity = null!;
            return false;
        }

        public bool TryGetNpc(Identity id, out NpcEntity entity)
            => TryGetLiveNpc(id, out entity);

        public bool TryGetCombatTarget(Identity id, out ICombatTargetEntity entity)
        {
            if (TryGetLivePlayer(id, out var player))
            {
                entity = player;
                return true;
            }

            if (TryGetLiveNpc(id, out var npc))
            {
                entity = npc;
                return true;
            }

            entity = null!;
            return false;
        }

        public bool TryGetCombatTargetByHex(string identityHex, out ICombatTargetEntity entity)
        {
            foreach (var pair in _players)
            {
                if (string.Equals(pair.Key.ToString(), identityHex, System.StringComparison.OrdinalIgnoreCase)
                    && TryGetLivePlayer(pair.Key, out var player))
                {
                    entity = player;
                    return true;
                }
            }

            foreach (var pair in _npcs)
            {
                if (string.Equals(pair.Key.ToString(), identityHex, System.StringComparison.OrdinalIgnoreCase)
                    && TryGetLiveNpc(pair.Key, out var npc))
                {
                    entity = npc;
                    return true;
                }
            }

            entity = null!;
            return false;
        }

        public bool IsIdentityVisible(Identity id)
        {
            if (_players.ContainsKey(id))
                return true;
            if (_npcs.ContainsKey(id))
                return true;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return false;

            return _scopedPlayerCacheHydrator.IsIdentityTrackedInScopedCache(
                _scopedPlayerCacheHydrator.Capture(conn),
                id);
        }

        public void RehydratePlayersFromScopedCache()
        {
            if (ShouldSuppressPresentationInCurrentScene())
            {
                ClearAllPlayers();
                return;
            }

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                ClearAllPlayers();
                return;
            }

            _scopedPlayerCacheHydrator.RehydratePlayersFromScopedCache(
                _scopedPlayerCacheHydrator.Capture(conn),
                ClearAllPlayers,
                SpawnOrUpdatePlayer);
        }

        private static PlayerSnapshot SnapshotFrom(PlayerPhysics row)
            => new PlayerSnapshot(
                row.PosX,
                row.PosY,
                row.PosZ,
                row.VelX,
                row.VelY,
                row.VelZ,
                row.Yaw,
                row.Grounded,
                row.LastProcessedTick,
                Time.realtimeSinceStartup,
                RemotePresentationBuffer.QuantizeServerTimeMicros(row.UpdatedAt.MicrosecondsSinceUnixEpoch),
                row.LastTickConsumedCommand,
                (int)row.BufferedCommandCount);

        private void SpawnOrUpdateNpc(NpcInstance row)
        {
            if (ShouldSuppressPresentationInCurrentScene())
            {
                if (_npcs.Count > 0 || _pendingNpcSpawns.Count > 0)
                    ClearAllNpcs();
                return;
            }

            if (TryGetLiveNpc(row.Identity, out NpcEntity existing))
            {
                if (string.Equals(
                        NormalizeVisualId(existing.VisualId),
                        NormalizeVisualId(row.VisualId),
                        System.StringComparison.Ordinal))
                {
                    existing.ApplyInstance(row);
                    return;
                }

                RemoveNpcPresentation(row.Identity);
            }

            string visualId = NormalizeVisualId(row.VisualId);
            if (_pendingNpcSpawns.TryGetValue(row.Identity, out PendingNpcSpawn? pending))
            {
                if (string.Equals(pending.VisualId, visualId, System.StringComparison.Ordinal))
                {
                    pending.LatestInstance = row;
                    return;
                }

                // Replacing the dictionary value invalidates the old load
                // coroutine without trying to cancel Unity's shared request.
                _pendingNpcSpawns.Remove(row.Identity);
            }

            CancelScheduledNpcVisualUnload();
            pending = new PendingNpcSpawn(row);
            _pendingNpcSpawns[row.Identity] = pending;
            StartCoroutine(SpawnNpcWhenVisualReady(row.Identity, pending));
        }

        private IEnumerator SpawnNpcWhenVisualReady(
            Identity identity,
            PendingNpcSpawn pending)
        {
            if (!_npcVisualCache.TryBeginLoad(
                    pending.VisualId,
                    out string normalizedVisualId,
                    out ResourceRequest request,
                    out string loadError))
            {
                Debug.LogWarning(
                    $"[EntityRegistry] Cannot load NPC '{pending.LatestInstance.TemplateId}': {loadError}");
                RemovePendingNpcSpawn(identity, pending);
                yield break;
            }

            yield return request;

            if (!IsCurrentPendingNpcSpawn(identity, pending))
            {
                ScheduleNpcVisualUnloadIfIdle();
                yield break;
            }

            if (!_npcVisualCache.TryAcquireCompleted(
                    normalizedVisualId,
                    out NpcVisualResourceCache.Lease lease,
                    out loadError))
            {
                Debug.LogWarning(
                    $"[EntityRegistry] Cannot load NPC '{pending.LatestInstance.TemplateId}' " +
                    $"visual '{pending.VisualId}': {loadError}");
                RemovePendingNpcSpawn(identity, pending);
                yield break;
            }

            if (ShouldSuppressPresentationInCurrentScene())
            {
                lease.Dispose();
                RemovePendingNpcSpawn(identity, pending);
                yield break;
            }

            var conn = NetworkManager.Instance?.Conn;
            NpcInstance latest = pending.LatestInstance;
            if (conn != null)
            {
                NpcInstance? authoritative = conn.Db.NpcInstance.Identity.Find(identity);
                if (authoritative == null)
                {
                    lease.Dispose();
                    RemovePendingNpcSpawn(identity, pending);
                    yield break;
                }
                latest = authoritative;
            }

            if (!string.Equals(
                    NormalizeVisualId(latest.VisualId),
                    pending.VisualId,
                    System.StringComparison.Ordinal))
            {
                lease.Dispose();
                RemovePendingNpcSpawn(identity, pending, scheduleUnload: false);
                SpawnOrUpdateNpc(latest);
                yield break;
            }

            NpcEntity? entity = null;
            bool leaseTransferred = false;
            try
            {
                NpcPhysics? physics = conn?.Db.NpcPhysics.Identity.Find(identity);
                NpcState? state = conn?.Db.NpcState.Identity.Find(identity);
                entity = new NpcEntity(latest, physics, state, lease.Profile);

                if (conn != null)
                {
                    foreach (StatusEffect effect in conn.Db.StatusEffect.Target.Filter(identity))
                        entity.ApplyStatusEffect(effect.EffectKind);
                }

                entity.GameObject.SetActive(true);
                RemovePendingNpcSpawn(identity, pending, scheduleUnload: false);
                _npcs[identity] = entity;
                _npcVisualLeases[identity] = lease;
                leaseTransferred = true;
                Debug.Log(
                    $"[EntityRegistry] Spawned fully loaded NPC {latest.DisplayName} {identity} " +
                    $"template={latest.TemplateId} visual={latest.VisualId}");
            }
            catch (System.Exception error)
            {
                entity?.Destroy();
                Debug.LogWarning(
                    $"[EntityRegistry] Cannot spawn NPC '{pending.LatestInstance.TemplateId}' " +
                    $"visual: {error.Message}");
            }
            finally
            {
                if (!leaseTransferred)
                    lease.Dispose();
                RemovePendingNpcSpawn(identity, pending);
            }
        }

        private void ApplyNpcPhysics(NpcPhysics row)
        {
            if (_npcs.TryGetValue(row.Identity, out var entity) && !entity.IsDestroyed)
            {
                entity.ApplyPhysics(row);
                return;
            }

            var instance = NetworkManager.Instance?.Conn?.Db.NpcInstance.Identity.Find(row.Identity);
            if (instance != null)
                SpawnOrUpdateNpc(instance);
        }

        private void ApplyNpcState(NpcState row)
        {
            if (_npcs.TryGetValue(row.Identity, out var entity) && !entity.IsDestroyed)
            {
                entity.ApplyState(row);
                return;
            }

            var instance = NetworkManager.Instance?.Conn?.Db.NpcInstance.Identity.Find(row.Identity);
            if (instance != null)
                SpawnOrUpdateNpc(instance);
        }

        private void RehydrateNpcsFromCache()
        {
            if (ShouldSuppressPresentationInCurrentScene())
            {
                ClearAllNpcs();
                return;
            }

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                ClearAllNpcs();
                return;
            }

            foreach (var row in conn.Db.NpcInstance.Iter())
                SpawnOrUpdateNpc(row);
        }

        private void PurgeScenePlacedPlayers()
        {
            // Scene-authored Starter Assets players conflict with the runtime entity registry.
            foreach (var component in FindObjectsByType<Component>(FindObjectsSortMode.None))
            {
                if (!StarterAssetsRuntimeStripper.IsThirdPersonController(component))
                    continue;

                var root = component.transform.root;
                if (root.name == "HubSceneRoot")
                    continue;

                PlayerEntity.DisablePlayerInput(root.gameObject);
                Destroy(root.gameObject);
            }
        }

        private void SpawnOrUpdatePlayer(PlayerPhysics row)
        {
            if (ShouldSuppressPresentationInCurrentScene())
            {
                if (_players.Count > 0)
                    ClearAllPlayers();
                return;
            }

            if (!TryGetLivePlayer(row.Identity, out var entity))
            {
                bool isLocal = _hasLocalIdentity && row.Identity == _localIdentity;
                entity = new PlayerEntity(row.Identity, isLocal, playerPrefab);
                if (_defaultAnimationSet != null)
                    entity.SetCombatAnimationSet(_defaultAnimationSet);
                if (_sharedActionProfile != null)
                    entity.SetSharedActionProfile(_sharedActionProfile);
                _players[row.Identity] = entity;

                if (isLocal)
                {
                    LocalPlayerEntity = entity;
                    SetupLocalPlayer(entity);
                }

                entity.GameObject.SetActive(!ShouldSuppressPresentationInCurrentScene());

                Debug.Log($"[EntityRegistry] Spawned {row.Identity} local={isLocal} pos=({row.PosX:F1},{row.PosY:F1},{row.PosZ:F1})");
                var conn = NetworkManager.Instance?.Conn;
                if (conn != null)
                {
                    _scopedPlayerCacheHydrator.ApplyCachedRowsForPlayer(
                        _scopedPlayerCacheHydrator.Capture(conn),
                        row.Identity,
                        this);
                }

                ApplyOwnerEquipmentPresentation(row.Identity);
                ApplyOwnerCombatProfile(row.Identity);
                if (_activeWorldInteractions.TryGetValue(
                        row.Identity,
                        out ActiveWorldInteraction activeInteraction))
                {
                    entity.BeginWorldInteractionAnimation(activeInteraction);
                }
            }

            entity.SimState.PushSnapshot(SnapshotFrom(row));
        }

        private void ApplyUsername(Player row)
        {
            if (!TryGetLivePlayer(row.Identity, out var entity))
                return;

            entity.SetUsername(row.Username);
            ApplyOwnerCombatProfile(row.Identity);
            if (_sharedActionProfile != null)
                entity.SetSharedActionProfile(_sharedActionProfile);
        }

        private void ApplyEquipmentLoadout(EquipmentLoadout row)
        {
            ApplyOwnerCombatProfile(row.Owner);
        }

        private void ApplyPlayerEquipmentPresentation(PlayerEquipmentPresentation row)
        {
            ApplyOwnerEquipmentPresentation(row.Owner, row);
        }

        private static bool HasSameWeaponPresentation(
            PlayerEquipmentPresentation oldRow,
            PlayerEquipmentPresentation newRow)
        {
            return string.Equals(oldRow.MainHandItemDefId, newRow.MainHandItemDefId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.OffHandItemDefId, newRow.OffHandItemDefId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.MainHandColorId, newRow.MainHandColorId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.OffHandColorId, newRow.OffHandColorId, System.StringComparison.Ordinal);
        }

        private static bool HasSameArmorPresentation(
            PlayerEquipmentPresentation oldRow,
            PlayerEquipmentPresentation newRow)
        {
            return string.Equals(oldRow.HeadItemDefId, newRow.HeadItemDefId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.ShoulderItemDefId, newRow.ShoulderItemDefId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.CapeItemDefId, newRow.CapeItemDefId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.ChestItemDefId, newRow.ChestItemDefId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.LegsItemDefId, newRow.LegsItemDefId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.BootsItemDefId, newRow.BootsItemDefId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.GlovesItemDefId, newRow.GlovesItemDefId, System.StringComparison.Ordinal);
        }

        private void ApplyCombatProfileForNullableOwner(Identity? owner)
        {
            if (!owner.HasValue)
                return;

            ApplyOwnerCombatProfile(owner.Value);
        }

        private static bool IsCombatProfileItemReference(Identity? owner, string? itemInstanceId)
        {
            if (!owner.HasValue || string.IsNullOrWhiteSpace(itemInstanceId))
                return false;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return false;

            string normalizedItemInstanceId = itemInstanceId.Trim();
            EquipmentLoadout? equipment = conn.Db.EquipmentLoadout.Owner.Find(owner.Value);
            if (equipment != null
                && (ItemInstanceIdsMatch(equipment.MainHandItemId, normalizedItemInstanceId)
                    || ItemInstanceIdsMatch(equipment.OffHandItemId, normalizedItemInstanceId)))
            {
                return true;
            }

            foreach (CharacterCombatDisciplineWeaponLoadout loadout in
                     conn.Db.CharacterCombatDisciplineWeaponLoadout.Owner.Filter(owner.Value))
            {
                if (ItemInstanceIdsMatch(loadout.MainHandItemId, normalizedItemInstanceId)
                    || ItemInstanceIdsMatch(loadout.OffHandItemId, normalizedItemInstanceId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ItemInstanceIdsMatch(string? candidate, string expected)
        {
            return !string.IsNullOrWhiteSpace(candidate)
                && string.Equals(candidate.Trim(), expected, System.StringComparison.Ordinal);
        }

        private void ApplyAllEquipmentPresentations()
        {
            foreach (var entity in AllPlayers)
            {
                ApplyOwnerEquipmentPresentation(entity.Identity);
                ApplyOwnerCombatProfile(entity.Identity);
            }
        }

        private void ApplyOwnerEquipmentPresentation(Identity owner, PlayerEquipmentPresentation? presentation = null)
        {
            if (!TryGetLivePlayer(owner, out var entity))
                return;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            presentation ??= conn.Db.PlayerEquipmentPresentation.Owner.Find(owner);
            if (presentation == null)
                return;

            ApplyOwnerWeaponPresentation(owner, presentation);
            ApplyOwnerArmorPresentation(owner, presentation);
        }

        private void ApplyOwnerWeaponPresentation(Identity owner, PlayerEquipmentPresentation presentation)
        {
            if (!TryGetLivePlayer(owner, out var entity))
                return;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            entity.SetEquippedWeaponVisuals(BuildEquippedWeaponVisuals(conn, presentation));
        }

        private void ApplyOwnerArmorPresentation(Identity owner, PlayerEquipmentPresentation presentation)
        {
            if (!TryGetLivePlayer(owner, out var entity))
                return;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            entity.SetEquippedArmorItemDefIdsBySlot(BuildEquippedArmorItemDefIdsBySlot(conn, presentation));
        }

        private List<EquippedWeaponVisual> BuildEquippedWeaponVisuals(
            DbConnection? conn,
            PlayerEquipmentPresentation presentation)
        {
            var visuals = new List<EquippedWeaponVisual>();
            if (conn == null)
                return visuals;

            AddWeaponVisuals(
                conn,
                _equipmentAppearanceCatalog,
                presentation.MainHandItemDefId,
                presentation.MainHandColorId,
                visuals);
            AddWeaponVisuals(
                conn,
                _equipmentAppearanceCatalog,
                presentation.OffHandItemDefId,
                presentation.OffHandColorId,
                visuals);
            return visuals;
        }

        private static Dictionary<string, string> BuildEquippedArmorItemDefIdsBySlot(
            DbConnection? conn,
            PlayerEquipmentPresentation presentation)
        {
            var itemDefIdsBySlot = new Dictionary<string, string>(System.StringComparer.Ordinal);
            if (conn == null)
                return itemDefIdsBySlot;

            AddArmorItemDefId(conn, "HEAD", presentation.HeadItemDefId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "SHOULDER", presentation.ShoulderItemDefId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "CAPE", presentation.CapeItemDefId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "CHEST", presentation.ChestItemDefId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "LEGS", presentation.LegsItemDefId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "BOOTS", presentation.BootsItemDefId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "GLOVES", presentation.GlovesItemDefId, itemDefIdsBySlot);
            return itemDefIdsBySlot;
        }

        private static void AddArmorItemDefId(
            DbConnection conn,
            string slotId,
            string? itemDefId,
            Dictionary<string, string> itemDefIdsBySlot)
        {
            if (string.IsNullOrWhiteSpace(itemDefId))
                return;

            ItemDefinition? definition = conn.Db.ItemDefinition.ItemDefId.Find(itemDefId.Trim());
            if (definition == null
                || !string.Equals(WireIdentifier.Normalize(definition.ItemKind), "ARMOR", System.StringComparison.Ordinal))
            {
                return;
            }

            itemDefIdsBySlot[slotId] = definition.ItemDefId;
        }

        private static void AddWeaponVisuals(
            DbConnection conn,
            EquipmentAppearanceCatalog? equipmentAppearanceCatalog,
            string? itemDefId,
            string? colorId,
            List<EquippedWeaponVisual> visuals)
        {
            if (string.IsNullOrWhiteSpace(itemDefId))
                return;

            ItemDefinition? definition = conn.Db.ItemDefinition.ItemDefId.Find(itemDefId.Trim());
            if (definition == null
                || !string.Equals(WireIdentifier.Normalize(definition.ItemKind), "WEAPON", System.StringComparison.Ordinal))
                return;

            foreach (string roleId in EquipmentAppearanceCatalog.WeaponVisualRoleIdsForKind(definition.WeaponKind))
            {
                if (equipmentAppearanceCatalog == null
                    || !equipmentAppearanceCatalog.TryGetWeaponVisual(
                        definition.ItemDefId,
                        colorId,
                        roleId,
                        CharacterAppearanceIds.RaceHuman,
                        CharacterAppearanceIds.SexMale,
                        out EquipmentAppearanceCatalog.WeaponVisualEntry entry)
                    || entry.prefab == null)
                {
                    continue;
                }

                visuals.Add(new EquippedWeaponVisual(
                    roleId,
                    definition.ItemDefId,
                    entry.prefab,
                    entry.placementProfile));
            }
        }

        private void ApplyOwnerCombatProfile(Identity owner)
        {
            if (!TryGetLivePlayer(owner, out var entity))
                return;

            var conn = NetworkManager.Instance?.Conn;
            string combatProfile = CombatProfileResolver.ResolveForOwner(conn, owner);
            entity.SetCombatProfile(combatProfile);

            CombatAnimationSet? animationSet = ResolveAnimationSet(combatProfile);
            if (animationSet != null && !entity.UsesCombatAnimationSet(animationSet))
            {
                entity.SetCombatAnimationSet(animationSet);
            }

            ApplyOwnerCombatMode(owner);
        }

        private void ApplyOwnerCombatMode(Identity owner)
        {
            if (!TryGetLivePlayer(owner, out var entity))
                return;

            var conn = NetworkManager.Instance?.Conn;
            ActiveCombatMode? active = conn?.Db.ActiveCombatMode.Owner.Find(owner);
            string modeId = string.Empty;
            if (active != null
                && string.Equals(
                    WireIdentifier.Normalize(active.CombatProfileId),
                    CombatProfileIds.Normalize(entity.CombatProfile),
                    System.StringComparison.Ordinal))
            {
                modeId = active.ModeId;
            }

            entity.SetCombatAnimationMode(modeId);
        }

        private void ApplyCharacterAppearance(CharacterAppearance row)
        {
            if (!TryGetLivePlayer(row.Owner, out var entity))
                return;

            entity.ApplyAppearance(row);
        }

        private static bool HasSameVisualAppearance(CharacterAppearance oldRow, CharacterAppearance newRow)
        {
            return oldRow.Owner == newRow.Owner
                && string.Equals(oldRow.RaceId, newRow.RaceId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.SexId, newRow.SexId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.BodyId, newRow.BodyId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.HeadId, newRow.HeadId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.FaceId, newRow.FaceId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.HairId, newRow.HairId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.EyesId, newRow.EyesId, System.StringComparison.Ordinal)
                && string.Equals(oldRow.OutfitId, newRow.OutfitId, System.StringComparison.Ordinal);
        }

        private CombatAnimationSet? ResolveAnimationSet(string combatProfile)
        {
            string normalizedProfile = CombatProfileIds.Normalize(combatProfile);
            var loaded = CombatAnimationSetCatalog.Resolve(normalizedProfile);
            if (loaded == null && normalizedProfile != CombatProfileIds.Default)
            {
                Debug.LogWarning(
                    $"[EntityRegistry] Animation set not found for combat profile '{normalizedProfile}'. Falling back to default.");
            }

            return loaded ?? _defaultAnimationSet;
        }

        // Called by NetworkManager on CombatEvent insert.
        public void OnCombatEventInsert(EventContext ctx, CombatEvent row)
        {
            ArenaServerClock.RecordObservedServerTimestampMicros(row.CreatedAt.MicrosecondsSinceUnixEpoch);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (CombatPresentationDebugGate.ShouldSuppress(row))
                return;
#endif

            if (row.EventType == CombatEventTypes.Block)
            {
                if (TryGetLivePlayer(row.Hit, out var blockingEntity))
                    blockingEntity.TriggerBlockHit();
                return;
            }

            if (row.EventType == CombatEventTypes.Parry)
            {
                if (TryGetLivePlayer(row.Hit, out var parryingEntity))
                {
                    if (string.Equals(row.SourceKind, CombatEventSources.NpcMelee, System.StringComparison.Ordinal))
                        parryingEntity.SetParryArmed(false);
                    else
                        parryingEntity.TriggerParryHit();
                }
                return;
            }

            // Hit reactions: directional flinch on the player who was struck.
            if (ShouldTriggerHitReaction(row))
            {
                QueueHitReaction(row.Hit, row.DirX, row.DirZ);
                return;
            }

            if (row.EventType == CombatEventTypes.Release
                && string.Equals(row.SourceKind, CombatEventSources.Spell, System.StringComparison.Ordinal)
                && TryGetLivePlayer(row.Caster, out var releaseCaster))
            {
                releaseCaster.OnSpellCombatRelease(row);
                return;
            }

            bool meleeLifecycleEnd = row.EventType == CombatEventTypes.Release
                || row.EventType == CombatEventTypes.Fizzle;
            if (meleeLifecycleEnd
                && !string.Equals(row.SourceKind, CombatEventSources.Spell, System.StringComparison.Ordinal)
                && TryGetLivePlayer(row.Caster, out var meleeLifecycleCaster)
                && meleeLifecycleCaster.RequestCombatLifecycleDrivenPhasedMeleeEnd(row.ActionKind))
            {
                return;
            }

            if (TryGetLiveNpc(row.Caster, out var npcCaster))
            {
                bool npcMeleeCast = row.EventType == CombatEventTypes.Cast
                    && string.Equals(row.SourceKind, CombatEventSources.NpcMelee, System.StringComparison.Ordinal);
                bool npcInstantSpellCast = row.EventType == CombatEventTypes.Cast
                    && string.Equals(row.SourceKind, CombatEventSources.Spell, System.StringComparison.Ordinal)
                    && !IsCastTimeSpellEvent(row);
                bool npcSpellRelease = row.EventType == CombatEventTypes.Release
                    && string.Equals(row.SourceKind, CombatEventSources.Spell, System.StringComparison.Ordinal);
                bool npcSpellCancel = row.EventType == CombatEventTypes.Fizzle
                    && string.Equals(row.SourceKind, CombatEventSources.Spell, System.StringComparison.Ordinal);
                if (npcMeleeCast || npcInstantSpellCast)
                {
                    npcCaster.RequestCombatAnimation(
                        CombatAnimationRequestTranslator.BuildActorNeutralAuthoritativeFromCombatEvent(row));
                    return;
                }
                if (npcSpellRelease || npcSpellCancel)
                {
                    npcCaster.RequestCombatAnimation(CombatAnimationRequest.AuthoritativeSpell(
                        row.ActionKind,
                        row.CreatedAt.MicrosecondsSinceUnixEpoch / 1000L,
                        npcSpellCancel ? CombatSpellAnimationPhase.Cancel : CombatSpellAnimationPhase.Release,
                        row.SourceKind,
                        new Vector3(row.PointX, row.PointY, row.PointZ)));
                    return;
                }
            }

            if (row.EventType != CombatEventTypes.Cast)
                return;

            if (!CombatAnimationRequestTranslator.IsAnimationStartEvent(row))
            {
                if (_hasLocalIdentity && row.Caster == _localIdentity)
                {
                    ActionBarTrace.Trace(
                        $"skipped combat animation for non-start COMBAT_CAST: {row.ActionKind} "
                        + $"source={row.SourceKind} hitIndex={row.HitIndex}");
                }
                return;
            }

            if (string.Equals(
                    row.MetadataKind,
                    CombatEventMetadataKinds.FlurryProc,
                    System.StringComparison.Ordinal))
            {
                if (TryGetLivePlayer(row.Caster, out var flurryCaster))
                {
                    flurryCaster.PlayAutoAttackGhost(
                        row.ActionKind,
                        row.CreatedAt.MicrosecondsSinceUnixEpoch / 1000L,
                        new Vector3(row.PointX, row.PointY, row.PointZ));
                }
                return;
            }

            if (_hasLocalIdentity && row.Caster == _localIdentity)
            {
                ActionBarTrace.Trace(
                    $"authoritative COMBAT_CAST received for local caster: {row.ActionKind} source={row.SourceKind}");
            }

            if (IsCastTimeSpellEvent(row))
            {
                if (_hasLocalIdentity && row.Caster == _localIdentity)
                {
                    ActionBarTrace.Trace(
                        $"suppressed immediate release animation for cast-time spell COMBAT_CAST: {row.ActionKind}");
                }
                return;
            }

            // Translate authoritative cast events into combat animation requests.
            // Local predicted visuals are filtered by the replay policy before dispatch.
            var conn = NetworkManager.Instance?.Conn;
            if (TryGetLivePlayer(row.Caster, out var casterEntity))
            {
                // HoldOnly channel spells own their entire presentation through the
                // ActiveCast row (enter/idle loop until the ActiveCast ends) and never play
                // a release. Dispatching the COMBAT_CAST as a Spell animation request reaches
                // PlayerAnimator.RequestCombatAnimation while the cast hold is active, which
                // preempts (InterruptWithoutGhost) and clears it — cutting the channel's hold
                // loop. Cast-time spells are already excluded above via IsCastTimeSpellEvent;
                // this covers zero-cast-time HoldOnly channels. HoldThenRelease spells keep
                // the COMBAT_CAST — it drives their authored hold->release handoff — so gate
                // on "no release presentation" (HoldOnly) rather than "uses hold".
                if (!casterEntity.PlaysSpellReleasePresentation(row.ActionKind))
                {
                    return;
                }

                var request = CombatAnimationRequestTranslator.BuildAuthoritativeFromCombatEvent(
                    conn,
                    casterEntity,
                    row);

                if (_hasLocalIdentity && row.Caster == _localIdentity)
                {
                    long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (CombatAnimationReplayPolicy.ShouldSuppressPredictedLocalReplay(
                            conn,
                            casterEntity,
                            request,
                            nowMs,
                            row.ActionInstanceId))
                    {
                        return;
                    }
                }

                casterEntity.RequestCombatAnimation(request);
            }
        }

        private static bool ShouldTriggerHitReaction(CombatEvent row)
        {
            if (row.Damage <= 0)
                return false;

            return row.EventType == CombatEventTypes.Impact
                || row.EventType == CombatEventTypes.Contact;
        }

        public void OnProjectilePresentationEventInsert(EventContext ctx, ProjectilePresentationEvent row)
        {
            _ = ctx;
            ArenaServerClock.RecordObservedServerTimestampMicros(row.CreatedAt.MicrosecondsSinceUnixEpoch);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (CombatPresentationDebugGate.ShouldSuppress(row))
                return;
#endif

            if (!ShouldTriggerProjectileContactHitReaction(row))
                return;

            QueueHitReaction(row.Hit, row.DirX, row.DirZ);
        }

        private static bool ShouldTriggerProjectileContactHitReaction(ProjectilePresentationEvent row)
        {
            if (!string.Equals(row.SourceKind, CombatEventSources.Spell, System.StringComparison.Ordinal))
                return false;

            if (row.Terminal || row.EventType != CombatEventTypes.Contact)
                return false;

            return row.Damage > 0;
        }

        private void QueueHitReaction(Identity target, float dirX, float dirZ)
        {
            if (!TryGetLivePlayer(target, out _))
                return;

            // Negate travel dir to get the direction from which the hit arrived.
            var hitDir = new Vector3(-dirX, 0f, -dirZ);
            if (hitDir.sqrMagnitude > 0.001f)
                hitDir.Normalize();

            // Directionless impacts (notably floor traps) use the existing
            // reaction controller's forward-hit fallback.
            _pendingHitReactions.Add(new PendingHitReaction(target, hitDir, Time.frameCount));
        }

        private void ApplyState(PlayerState row)
        {
            if (TryGetLivePlayer(row.PlayerId, out var entity))
                entity.SetState(row);
        }

        private static bool IsCastTimeSpellEvent(CombatEvent row)
        {
            return string.Equals(row.SourceKind, CombatEventSources.Spell, System.StringComparison.Ordinal)
                && TryGetCastTimeMs(row.ActionKind, out ulong castTimeMs)
                && castTimeMs > 0UL;
        }

        private static bool TryGetCastTimeMs(string spellActionId, out ulong castTimeMs)
        {
            castTimeMs = 0UL;
            if (TryGetSpellDefinition(spellActionId, out SpellDefinition definition))
            {
                castTimeMs = definition.CastTimeMs;
                return true;
            }

            return false;
        }

        private static bool TryGetSpellDefinition(string spellActionId, out SpellDefinition definition)
        {
            definition = null!;
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null || string.IsNullOrWhiteSpace(spellActionId))
                return false;

            SpellDefinition? found = conn.Db.SpellDefinition.Kind.Find(spellActionId);
            if (found == null)
            {
                Debug.LogWarning($"[EntityRegistry] Missing SpellDefinition for spell action '{spellActionId}'. Spell presentation routing cannot be resolved.");
                return false;
            }

            definition = found;
            return true;
        }

        private void ApplyCombatEngagement(CombatEngagement row)
        {
            if (TryGetLivePlayer(row.Owner, out var entity))
                entity.SetGameplayCombatEngagement(row);
        }

        private void ApplyPlayerResource(PlayerResource row)
        {
            if (!TryGetLivePlayer(row.Owner, out var entity))
                return;

            entity.SetResource(row.Kind, row.Current, row.Max);
        }

        private void ApplyStatusEffect(StatusEffect row)
        {
            if (TryGetLivePlayer(row.Target, out var entity))
            {
                entity.ApplyStatusEffect(row.EffectKind);
                if (_hasLocalIdentity
                    && row.Target == _localIdentity
                    && string.Equals(row.EffectKind, "MOVE_SLOW_IMMUNITY", System.StringComparison.OrdinalIgnoreCase))
                {
                    ActionBarTrace.Trace("local MOVE_SLOW_IMMUNITY status applied");
                }
            }
            else if (TryGetLiveNpc(row.Target, out var npc))
            {
                npc.ApplyStatusEffect(row.EffectKind);
            }
        }

        private void TriggerStaggerPresentation(StatusEffect row)
        {
            if (!IsStaggerStatus(row) || !TryGetLivePlayer(row.Target, out var targetEntity))
                return;

            Vector3 hitDirection = targetEntity.GameObject.transform.forward;
            if (TryGetLivePlayer(row.Source, out var sourceEntity)
                && sourceEntity != targetEntity)
            {
                Vector3 delta = targetEntity.GameObject.transform.position - sourceEntity.GameObject.transform.position;
                delta.y = 0f;
                if (delta.sqrMagnitude > 0.0001f)
                    hitDirection = delta.normalized;
            }

            targetEntity.TriggerStagger(hitDirection);
        }

        private static bool IsStaggerStatus(StatusEffect row) =>
            string.Equals(row.EffectKind, "STAGGER", System.StringComparison.OrdinalIgnoreCase);

        private void RefreshStatusPresentation(Identity target, ulong ignoredStatusId = 0UL)
        {
            if (!TryGetLivePlayer(target, out var entity))
                return;

            bool isKnockedDown = false;
            string? hardCrowdControlStatusKind = null;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            foreach (var effect in conn.Db.StatusEffect.Target.Filter(target))
            {
                if (ignoredStatusId != 0UL && effect.StatusId == ignoredStatusId)
                    continue;

                isKnockedDown |= IsStatusKind(effect.EffectKind, KnockdownStatusKind);
                hardCrowdControlStatusKind = SelectHardCrowdControlStatusKind(
                    hardCrowdControlStatusKind,
                    effect.EffectKind);

                if (isKnockedDown && !string.IsNullOrEmpty(hardCrowdControlStatusKind))
                    break;
            }

            entity.SetKnockedDown(isKnockedDown);
            entity.SetHardCrowdControl(hardCrowdControlStatusKind);
        }

        private void FlushPendingHitReactions()
        {
            if (_pendingHitReactions.Count == 0)
                return;

            for (int i = 0; i < _pendingHitReactions.Count;)
            {
                PendingHitReaction pending = _pendingHitReactions[i];
                if (pending.FrameQueued >= Time.frameCount)
                {
                    i++;
                    continue;
                }

                _pendingHitReactions.RemoveAt(i);

                if (!TryGetLivePlayer(pending.Target, out var entity))
                    continue;

                if (HasSuppressingReactionStatus(pending.Target))
                    continue;

                entity.TriggerHit(pending.Direction);
            }
        }

        private static bool HasSuppressingReactionStatus(Identity target)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return false;

            foreach (var effect in conn.Db.StatusEffect.Target.Filter(target))
            {
                if (IsStatusKind(effect.EffectKind, KnockdownStatusKind)
                    || IsHardCrowdControlStatusKind(effect.EffectKind))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsStatusKind(string actual, string expected) =>
            string.Equals(actual, expected, System.StringComparison.OrdinalIgnoreCase);

        private static bool IsHardCrowdControlStatusKind(string statusKind) =>
            IsStatusKind(statusKind, StunStatusKind)
            || IsStatusKind(statusKind, FreezeStatusKind)
            || IsStatusKind(statusKind, IntimidatedStatusKind)
            || IsStatusKind(statusKind, FearStatusKind);

        private static string? SelectHardCrowdControlStatusKind(string? current, string candidate)
        {
            if (!IsHardCrowdControlStatusKind(candidate))
                return current;

            string normalizedCandidate = candidate.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(current))
                return normalizedCandidate;

            if (IsStatusKind(current, StunStatusKind))
                return StunStatusKind;
            if (IsStatusKind(normalizedCandidate, StunStatusKind))
                return StunStatusKind;

            if (IsStatusKind(current, FreezeStatusKind))
                return FreezeStatusKind;
            if (IsStatusKind(normalizedCandidate, FreezeStatusKind))
                return FreezeStatusKind;

            return current;
        }

        private void ApplyDefenseState(DefenseState row, bool allowParryTrigger)
        {
            if (!TryGetLivePlayer(row.Owner, out var entity))
                return;

            long nowMicros = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            bool blockActive = string.Equals(row.Kind, DefenseBlockKind, System.StringComparison.OrdinalIgnoreCase)
                && row.ActiveUntil.MicrosecondsSinceUnixEpoch > nowMicros;
            entity.SetBlocking(blockActive);
            bool parryArmed = string.Equals(row.Kind, DefenseParryKind, System.StringComparison.OrdinalIgnoreCase)
                && row.ActiveUntil.MicrosecondsSinceUnixEpoch > nowMicros
                && row.RecoveryUntil.MicrosecondsSinceUnixEpoch <= row.ActiveUntil.MicrosecondsSinceUnixEpoch;
            entity.SetParryArmed(parryArmed);

            long startedMicros = row.StartedAt.MicrosecondsSinceUnixEpoch;
            if (!allowParryTrigger
                || !string.Equals(row.Kind, DefenseParryKind, System.StringComparison.OrdinalIgnoreCase)
                || row.ActiveUntil.MicrosecondsSinceUnixEpoch <= nowMicros
                || (_lastDefenseStartMicros.TryGetValue(row.Owner, out var previous) && previous == startedMicros))
            {
                _lastDefenseStartMicros[row.Owner] = startedMicros;
                return;
            }

            _lastDefenseStartMicros[row.Owner] = startedMicros;
            if (LocalPlayerEntity?.Identity == entity.Identity
                && LocalDefensePrediction.ConsumePredictedParryPresentation())
                return;
            entity.StartParry();
        }

        private void ApplyMovementActionState(MovementActionState row, bool allowStartTrigger)
        {
            if (!TryGetLivePlayer(row.Owner, out var entity))
                return;

            entity.SetMovementActionState(row);
            if (!allowStartTrigger)
                return;

            if (string.Equals(row.Kind, "DODGE", System.StringComparison.OrdinalIgnoreCase))
                entity.TriggerDodge(row);
        }

        private void ClearAllPlayers()
        {
            foreach (var entity in _players.Values)
                entity.Destroy();

            _players.Clear();
            _pendingHitReactions.Clear();
            LocalPlayerEntity = null;
            LocalDefensePrediction.Reset();
        }

        private void ClearAllNpcs(bool scheduleVisualUnload = true)
        {
            foreach (var entity in _npcs.Values)
                entity.Destroy();

            _npcs.Clear();
            _pendingNpcSpawns.Clear();
            foreach (NpcVisualResourceCache.Lease lease in _npcVisualLeases.Values)
                lease.Dispose();
            _npcVisualLeases.Clear();

            if (scheduleVisualUnload)
                ScheduleNpcVisualUnloadIfIdle();
        }

        private void RefreshPlayerPresentationForScene(string sceneName)
        {
            bool visible = !ShouldSuppressPresentationInScene(sceneName);
            foreach (var entity in _players.Values)
            {
                if (!entity.IsDestroyed)
                    entity.GameObject.SetActive(visible);
            }
        }

        private void RefreshNpcPresentationForScene(string sceneName)
        {
            bool visible = !ShouldSuppressPresentationInScene(sceneName);
            foreach (var entity in _npcs.Values)
            {
                if (!entity.IsDestroyed)
                    entity.GameObject.SetActive(visible);
            }
        }

        private bool TryGetLivePlayer(Identity id, out PlayerEntity entity)
        {
            if (!_players.TryGetValue(id, out entity!))
                return false;

            if (!entity.IsDestroyed)
                return true;

            RemovePlayerReference(id, entity);
            entity = null!;
            return false;
        }

        private bool TryGetLiveNpc(Identity id, out NpcEntity entity)
        {
            if (!_npcs.TryGetValue(id, out entity!))
                return false;

            if (!entity.IsDestroyed)
                return true;

            RemoveNpcPresentation(id, destroyEntity: false);
            entity = null!;
            return false;
        }

        private void RemoveNpcPresentation(Identity identity, bool destroyEntity = true)
        {
            _pendingNpcSpawns.Remove(identity);

            if (_npcs.TryGetValue(identity, out NpcEntity? entity))
            {
                if (destroyEntity)
                    entity.Destroy();
                _npcs.Remove(identity);
            }

            if (_npcVisualLeases.TryGetValue(
                    identity,
                    out NpcVisualResourceCache.Lease? lease))
            {
                lease.Dispose();
                _npcVisualLeases.Remove(identity);
            }

            ScheduleNpcVisualUnloadIfIdle();
        }

        private bool IsCurrentPendingNpcSpawn(
            Identity identity,
            PendingNpcSpawn pending)
        {
            return _pendingNpcSpawns.TryGetValue(identity, out PendingNpcSpawn? current)
                && ReferenceEquals(current, pending);
        }

        private void RemovePendingNpcSpawn(
            Identity identity,
            PendingNpcSpawn pending,
            bool scheduleUnload = true)
        {
            if (IsCurrentPendingNpcSpawn(identity, pending))
                _pendingNpcSpawns.Remove(identity);

            if (scheduleUnload)
                ScheduleNpcVisualUnloadIfIdle();
        }

        private void ScheduleNpcVisualUnloadIfIdle()
        {
            if (_npcs.Count != 0 || _pendingNpcSpawns.Count != 0)
                return;

            CancelScheduledNpcVisualUnload();
            if (isActiveAndEnabled)
                _npcVisualUnloadCoroutine = StartCoroutine(UnloadNpcVisualsAfterIdleDelay());
            else
                _npcVisualCache.ReleaseUnusedProfiles();
        }

        private IEnumerator UnloadNpcVisualsAfterIdleDelay()
        {
            yield return new WaitForSecondsRealtime(NpcVisualUnloadIdleDelaySeconds);
            _npcVisualUnloadCoroutine = null;
            if (_npcs.Count != 0 || _pendingNpcSpawns.Count != 0)
                yield break;

            _npcVisualCache.ReleaseUnusedProfiles();
            yield return Resources.UnloadUnusedAssets();
        }

        private void CancelScheduledNpcVisualUnload()
        {
            if (_npcVisualUnloadCoroutine == null)
                return;

            StopCoroutine(_npcVisualUnloadCoroutine);
            _npcVisualUnloadCoroutine = null;
        }

        private static string NormalizeVisualId(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();

        private void RemovePlayerReference(Identity id, PlayerEntity entity)
        {
            _players.Remove(id);
            if (LocalPlayerEntity == entity)
                LocalPlayerEntity = null;
        }

        private static bool ShouldSuppressPresentationInCurrentScene()
        {
            return ShouldSuppressPresentationInScene(SceneManager.GetActiveScene().name);
        }

        private static bool ShouldSuppressPresentationInScene(string sceneName)
        {
            return LocalWorldSceneDecider.SuppressesGameplayPresentation(sceneName);
        }

        void IScopedPlayerCacheSink.ApplyUsername(Player row) => ApplyUsername(row);
        void IScopedPlayerCacheSink.ApplyCharacterAppearance(CharacterAppearance row) => ApplyCharacterAppearance(row);
        void IScopedPlayerCacheSink.ApplyEquipmentLoadout(EquipmentLoadout row) => ApplyEquipmentLoadout(row);
        void IScopedPlayerCacheSink.ApplyPlayerEquipmentPresentation(PlayerEquipmentPresentation row) => ApplyPlayerEquipmentPresentation(row);
        void IScopedPlayerCacheSink.ApplyState(PlayerState row) => ApplyState(row);
        void IScopedPlayerCacheSink.ApplyCombatEngagement(CombatEngagement row) => ApplyCombatEngagement(row);
        void IScopedPlayerCacheSink.ApplyPlayerResource(PlayerResource row) => ApplyPlayerResource(row);
        void IScopedPlayerCacheSink.ApplyDefenseState(DefenseState row, bool allowParryTrigger) => ApplyDefenseState(row, allowParryTrigger);
        void IScopedPlayerCacheSink.ApplyStatusEffect(StatusEffect row) => ApplyStatusEffect(row);
        void IScopedPlayerCacheSink.RefreshStatusPresentation(Identity target) => RefreshStatusPresentation(target);
        void IScopedPlayerCacheSink.ApplyActiveCast(ActiveCast row) => OnActiveCastInsert(default!, row);
        void IScopedPlayerCacheSink.ApplyMovementActionState(MovementActionState row) => OnMovementActionStateInsert(default!, row);
        void IScopedPlayerCacheSink.ApplySpecialMovementRuntime(SpecialMovementRuntime row) => OnSpecialMovementRuntimeInsert(default!, row);
        void IScopedPlayerCacheSink.ApplyLingeringShadeState(LingeringShadeState row) => OnLingeringShadeStateInsert(default!, row);
    }
}

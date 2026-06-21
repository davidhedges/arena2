#nullable enable
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
        private readonly Dictionary<Identity, long> _lastDefenseStartMicros = new();
        private readonly List<PendingHitReaction> _pendingHitReactions = new();
        private readonly LocalWorldRuntimeCoordinator _localWorldCoordinator = new(new LocalMovementWorldContext());
        private readonly ScopedPlayerCacheHydrator _scopedPlayerCacheHydrator = new();

        private PlayerEntity? _localPlayerEntity;

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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
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

            PurgeScenePlacedPlayers();

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ClearAllPlayers();
            ClearAllNpcs();
            if (Instance == this)
                Instance = null!;
        }

        private void Update()
        {
            FlushPendingHitReactions();
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
            motor.Initialize(inputSource, GameplayTuning.DefaultHitRadius, GameplayTuning.DefaultHitHeight);

            var netDriver = go.AddComponent<MovementNetDriver>();
            netDriver.Initialize(entity.SimState, commandHistory);

            motor.EnablePredictedAuthority(stateProvider);
            var predictionDriver = go.AddComponent<LocalMovementPredictionDriver>();
            predictionDriver.Initialize(entity.SimState, motor, _localWorldCoordinator.WorldContext, stateProvider, commandHistory);

            var presentationDriver = go.AddComponent<LocalPresentationDriver>();
            presentationDriver.Initialize(entity.SimState, entity.GetPresentationRoot());

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
            ApplyEquipmentLoadout(row);
        }

        public void OnEquipmentLoadoutUpdate(EventContext ctx, EquipmentLoadout oldRow, EquipmentLoadout newRow)
        {
            ApplyEquipmentLoadout(newRow);
        }

        public void OnEquipmentLoadoutDelete(EventContext ctx, EquipmentLoadout row)
        {
            if (!TryGetLivePlayer(row.Owner, out var entity))
                return;

            entity.SetEquippedWeaponVisualItemIds(System.Array.Empty<string>());
            entity.SetEquippedArmorItemDefIdsBySlot(new Dictionary<string, string>(System.StringComparer.Ordinal));
            ApplyOwnerCombatProfile(row.Owner);
        }

        public void OnItemInstanceInsert(EventContext ctx, ItemInstance row)
        {
            ApplyEquipmentForNullableOwner(row.CurrentOwner);
        }

        public void OnItemInstanceUpdate(EventContext ctx, ItemInstance oldRow, ItemInstance newRow)
        {
            ApplyEquipmentForNullableOwner(oldRow.CurrentOwner);
            ApplyEquipmentForNullableOwner(newRow.CurrentOwner);
        }

        public void OnItemInstanceDelete(EventContext ctx, ItemInstance row)
        {
            ApplyEquipmentForNullableOwner(row.CurrentOwner);
        }

        public void OnItemDefinitionInsert(EventContext ctx, ItemDefinition row)
        {
            ApplyAllEquipmentLoadouts();
        }

        public void OnItemDefinitionUpdate(EventContext ctx, ItemDefinition oldRow, ItemDefinition newRow)
        {
            ApplyAllEquipmentLoadouts();
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
            if (!_npcs.TryGetValue(row.Identity, out var entity))
                return;

            entity.Destroy();
            _npcs.Remove(row.Identity);
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
            if (!_npcs.TryGetValue(row.Identity, out var entity))
                return;

            entity.Destroy();
            _npcs.Remove(row.Identity);
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
            if (_npcs.TryGetValue(row.Identity, out var entity))
                entity.Destroy();
            _npcs.Remove(row.Identity);
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
        // CombatEvents. Cast-time spell hold/release scheduling is routed from
        // ActiveCast so the authored release frame can line up with ends_at.

        public void OnActiveCastInsert(EventContext ctx, ActiveCast row)
        {
            if (TryGetCastTimeMs(row.Kind, out ulong castTimeMs)
                && castTimeMs > 0UL
                && TryGetLivePlayer(row.Caster, out var entity))
            {
                entity.OnActiveCastInsert(row, castTimeMs);
            }
        }

        public void OnActiveCastUpdate(EventContext ctx, ActiveCast oldRow, ActiveCast newRow)
        {
            if (TryGetCastTimeMs(newRow.Kind, out ulong castTimeMs)
                && castTimeMs > 0UL
                && TryGetLivePlayer(newRow.Caster, out var entity))
            {
                entity.OnActiveCastUpdate(newRow, castTimeMs);
            }
        }

        public void OnActiveCastDelete(EventContext ctx, ActiveCast row)
        {
            if (TryGetLivePlayer(row.Caster, out var entity))
                entity.OnActiveCastDelete(row);
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
            if (TryGetLivePlayer(row.Owner, out var entity))
                entity.SetSpecialMovementRuntime(row);
        }

        public void OnSpecialMovementRuntimeUpdate(EventContext ctx, SpecialMovementRuntime oldRow, SpecialMovementRuntime newRow)
        {
            if (TryGetLivePlayer(newRow.Owner, out var entity))
                entity.SetSpecialMovementRuntime(newRow);
        }

        public void OnSpecialMovementRuntimeDelete(EventContext ctx, SpecialMovementRuntime row)
        {
            if (TryGetLivePlayer(row.Owner, out var entity))
                entity.ClearSpecialMovementRuntime();
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

            if (string.Equals(
                    row.Kind,
                    entity.PrimaryResourceKind,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                entity.ClearPrimaryResource();
            }
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
                row.LastProcessedTick);

        private void SpawnOrUpdateNpc(NpcInstance row)
        {
            if (ShouldSuppressPresentationInCurrentScene())
            {
                if (_npcs.Count > 0)
                    ClearAllNpcs();
                return;
            }

            if (_npcs.TryGetValue(row.Identity, out var existing) && !existing.IsDestroyed)
            {
                existing.ApplyInstance(row);
                return;
            }

            var conn = NetworkManager.Instance?.Conn;
            NpcPhysics? physics = conn?.Db.NpcPhysics.Identity.Find(row.Identity);
            NpcState? state = conn?.Db.NpcState.Identity.Find(row.Identity);

            if (!NpcVisualCatalog.TryLoadDefault(out var catalog, out string catalogError))
            {
                Debug.LogWarning($"[EntityRegistry] Cannot spawn NPC '{row.TemplateId}': {catalogError}");
                return;
            }

            if (!catalog.TryGetPrefab(row.TemplateId, out var prefab))
            {
                Debug.LogWarning($"[EntityRegistry] Cannot spawn NPC '{row.TemplateId}': template has no visual prefab.");
                return;
            }

            try
            {
                var entity = new NpcEntity(row, physics, state, prefab);
                entity.GameObject.SetActive(!ShouldSuppressPresentationInCurrentScene());
                _npcs[row.Identity] = entity;
                Debug.Log($"[EntityRegistry] Spawned NPC {row.DisplayName} {row.Identity} template={row.TemplateId}");
            }
            catch (System.Exception error)
            {
                Debug.LogWarning($"[EntityRegistry] Cannot spawn NPC '{row.TemplateId}' visual: {error.Message}");
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
            ApplyOwnerEquipmentPresentation(row.Owner, row);
            ApplyOwnerCombatProfile(row.Owner);
        }

        private void ApplyEquipmentForNullableOwner(Identity? owner)
        {
            if (!owner.HasValue)
                return;

            ApplyOwnerEquipmentPresentation(owner.Value);
            ApplyOwnerCombatProfile(owner.Value);
        }

        private void ApplyAllEquipmentLoadouts()
        {
            foreach (var entity in AllPlayers)
            {
                ApplyOwnerEquipmentPresentation(entity.Identity);
                ApplyOwnerCombatProfile(entity.Identity);
            }
        }

        private void ApplyOwnerEquipmentPresentation(Identity owner, EquipmentLoadout? loadout = null)
        {
            if (!TryGetLivePlayer(owner, out var entity))
                return;

            var conn = NetworkManager.Instance?.Conn;
            entity.SetEquippedWeaponVisualItemIds(BuildEquippedWeaponVisualIds(conn, owner, loadout));
            entity.SetEquippedArmorItemDefIdsBySlot(BuildEquippedArmorItemDefIdsBySlot(conn, owner, loadout));
        }

        private static HashSet<string> BuildEquippedWeaponVisualIds(
            DbConnection? conn,
            Identity owner,
            EquipmentLoadout? loadout)
        {
            var visualIds = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (conn == null)
                return visualIds;

            loadout ??= conn.Db.EquipmentLoadout.Owner.Find(owner);
            if (loadout == null)
                return visualIds;

            AddWeaponVisualIds(conn, loadout.MainHandItemId, visualIds);
            AddWeaponVisualIds(conn, loadout.OffHandItemId, visualIds);
            return visualIds;
        }

        private static Dictionary<string, string> BuildEquippedArmorItemDefIdsBySlot(
            DbConnection? conn,
            Identity owner,
            EquipmentLoadout? loadout)
        {
            var itemDefIdsBySlot = new Dictionary<string, string>(System.StringComparer.Ordinal);
            if (conn == null)
                return itemDefIdsBySlot;

            loadout ??= conn.Db.EquipmentLoadout.Owner.Find(owner);
            if (loadout == null)
                return itemDefIdsBySlot;

            AddArmorItemDefId(conn, "HEAD", loadout.HeadItemId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "SHOULDER", loadout.ShoulderItemId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "CAPE", loadout.CapeItemId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "CHEST", loadout.ChestItemId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "LEGS", loadout.LegsItemId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "BOOTS", loadout.BootsItemId, itemDefIdsBySlot);
            AddArmorItemDefId(conn, "GLOVES", loadout.GlovesItemId, itemDefIdsBySlot);
            return itemDefIdsBySlot;
        }

        private static void AddArmorItemDefId(
            DbConnection conn,
            string slotId,
            string? itemInstanceId,
            Dictionary<string, string> itemDefIdsBySlot)
        {
            if (string.IsNullOrWhiteSpace(itemInstanceId))
                return;

            ItemInstance? item = conn.Db.ItemInstance.ItemInstanceId.Find(itemInstanceId.Trim());
            if (item == null)
                return;

            ItemDefinition? definition = conn.Db.ItemDefinition.ItemDefId.Find(item.ItemDefId);
            if (definition == null
                || !string.Equals(WireIdentifier.Normalize(definition.ItemKind), "ARMOR", System.StringComparison.Ordinal))
            {
                return;
            }

            itemDefIdsBySlot[slotId] = definition.ItemDefId;
        }

        private static void AddWeaponVisualIds(DbConnection conn, string? itemInstanceId, HashSet<string> visualIds)
        {
            if (string.IsNullOrWhiteSpace(itemInstanceId))
                return;

            ItemInstance? item = conn.Db.ItemInstance.ItemInstanceId.Find(itemInstanceId.Trim());
            if (item == null)
                return;

            ItemDefinition? definition = conn.Db.ItemDefinition.ItemDefId.Find(item.ItemDefId);
            if (definition == null)
                return;

            if (TryAddWeaponVisualIdsForItemDefinition(definition.ItemDefId, visualIds))
                return;

            AddWeaponVisualIdsForKind(definition.WeaponKind, visualIds);
        }

        private static bool TryAddWeaponVisualIdsForItemDefinition(string? itemDefId, HashSet<string> visualIds)
        {
            switch (WireIdentifier.Normalize(itemDefId))
            {
                case "TRAINING_TWO_HAND_SWORD":
                case "NEWBIE_TWO_HAND_SWORD_01":
                case "NEWBIE_TWO_HAND_SWORD_02":
                case "NEWBIE_TWO_HAND_AXE_01":
                case "NEWBIE_STAFF_01":
                case "NEWBIE_STAFF_02":
                case "NEWBIE_STAFF_03":
                case "NEWBIE_STAFF_04":
                    visualIds.Add("greatsword");
                    return true;
                case "TRAINING_ONE_HAND_SWORD":
                case "NEWBIE_ONE_HAND_SWORD_01":
                case "NEWBIE_ONE_HAND_SWORD_02":
                case "NEWBIE_ONE_HAND_AXE_02":
                case "NEWBIE_ONE_HAND_AXE_03":
                case "TRAINING_DAGGER_PAIR":
                case "NEWBIE_DAGGER_PAIR_01":
                case "NEWBIE_DAGGER_PAIR_02":
                case "NEWBIE_DAGGER_PAIR_03":
                    visualIds.Add("sword");
                    return true;
                case "TRAINING_SHIELD":
                case "NEWBIE_SHIELD_01":
                case "NEWBIE_SHIELD_02":
                case "NEWBIE_SHIELD_03":
                    visualIds.Add("shield");
                    return true;
                case "TRAINING_BOW":
                case "NEWBIE_BOW_01":
                case "NEWBIE_BOW_02":
                case "NEWBIE_BOW_03":
                    visualIds.Add("bow_drawn");
                    visualIds.Add("bow_stowed");
                    visualIds.Add("quiver");
                    return true;
                default:
                    return false;
            }
        }

        private static void AddWeaponVisualIdsForKind(string? weaponKind, HashSet<string> visualIds)
        {
            switch (WireIdentifier.Normalize(weaponKind))
            {
                case "TWO_HAND_SWORD":
                case "TWO_HANDED_SWORD":
                case "TWO_HAND_AXE":
                case "STAFF":
                    visualIds.Add("greatsword");
                    break;
                case "ONE_HAND_SWORD":
                case "ONE_HAND_AXE":
                case "DAGGER_PAIR":
                    visualIds.Add("sword");
                    break;
                case "SHIELD":
                    visualIds.Add("shield");
                    break;
                case "BOW":
                    visualIds.Add("bow_drawn");
                    visualIds.Add("bow_stowed");
                    visualIds.Add("quiver");
                    break;
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
            if (animationSet != null)
            {
                entity.SetCombatAnimationSet(animationSet);
            }
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

            if (row.EventType == CombatEventTypes.Cast
                && string.Equals(row.SourceKind, CombatEventSources.NpcMelee, System.StringComparison.Ordinal)
                && TryGetLiveNpc(row.Caster, out var npcCaster))
            {
                npcCaster.PlayAttack();
                return;
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

            return TryGetSpellDamage(row.ActionKind, out int damage) && damage > 0;
        }

        private void QueueHitReaction(Identity target, float dirX, float dirZ)
        {
            if (!TryGetLivePlayer(target, out _))
                return;

            // Negate travel dir to get the direction from which the hit arrived.
            var hitDir = new Vector3(-dirX, 0f, -dirZ);
            if (hitDir.sqrMagnitude > 0.001f)
                _pendingHitReactions.Add(new PendingHitReaction(target, hitDir.normalized, Time.frameCount));
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

        private static bool TryGetSpellDamage(string spellActionId, out int damage)
        {
            damage = 0;
            if (TryGetSpellDefinition(spellActionId, out SpellDefinition definition))
            {
                damage = definition.Damage;
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

            entity.SetPrimaryResource(row.Kind, row.Current, row.Max);
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

        private void ClearAllNpcs()
        {
            foreach (var entity in _npcs.Values)
                entity.Destroy();

            _npcs.Clear();
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

            _npcs.Remove(id);
            entity = null!;
            return false;
        }

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
        void IScopedPlayerCacheSink.ApplyState(PlayerState row) => ApplyState(row);
        void IScopedPlayerCacheSink.ApplyCombatEngagement(CombatEngagement row) => ApplyCombatEngagement(row);
        void IScopedPlayerCacheSink.ApplyPlayerResource(PlayerResource row) => ApplyPlayerResource(row);
        void IScopedPlayerCacheSink.ApplyDefenseState(DefenseState row, bool allowParryTrigger) => ApplyDefenseState(row, allowParryTrigger);
        void IScopedPlayerCacheSink.ApplyStatusEffect(StatusEffect row) => ApplyStatusEffect(row);
        void IScopedPlayerCacheSink.RefreshStatusPresentation(Identity target) => RefreshStatusPresentation(target);
        void IScopedPlayerCacheSink.ApplyActiveCast(ActiveCast row) => OnActiveCastInsert(default!, row);
        void IScopedPlayerCacheSink.ApplyMovementActionState(MovementActionState row) => OnMovementActionStateInsert(default!, row);
        void IScopedPlayerCacheSink.ApplySpecialMovementRuntime(SpecialMovementRuntime row) => OnSpecialMovementRuntimeInsert(default!, row);
    }
}

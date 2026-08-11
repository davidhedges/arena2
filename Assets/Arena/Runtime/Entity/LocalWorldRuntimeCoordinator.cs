#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using SpacetimeDB;
using SpacetimeDB.Types;
using Arena.Input;
using Arena.Match;
using Arena.Network;
using Arena.World;

namespace Arena.Entity
{
    internal static class LocalWorldScopeResolver
    {
        internal static NetworkManager.GameplayScope Resolve(PlayerWorld? row, string? openWorldSceneName)
            => row == null
                ? NetworkManager.GameplayScope.None
                : NetworkManager.GameplayScope.FromPlayerWorld(row, openWorldSceneName);
    }

    internal static class LocalWorldSceneDecider
    {
        internal const string SurvivalInstanceKind = "SURVIVAL";

        private static readonly HashSet<string> NonGameplayScenes = new(StringComparer.Ordinal)
        {
            "TrainingGround",
            "Hub",
            "CharacterCreation",
            "CharacterCustomization",
            "VFXGraph_GroundSlash",
        };

        internal static string? DetermineTargetScene(
            string activeSceneName,
            ulong? instanceId,
            string? instanceKind,
            string openWorldSceneName,
            string matchSceneName,
            string survivalSceneName)
        {
            if (!ArenaRuntimeSceneGate.IsArenaRuntimeScene(activeSceneName, string.Empty))
                return null;

            bool isHub = string.Equals(activeSceneName, "Hub", StringComparison.Ordinal);
            if (NonGameplayScenes.Contains(activeSceneName) && !isHub)
                return null;

            if (instanceId.HasValue)
            {
                // PlayerWorld and ArenaInstance arrive through different
                // subscriptions. Wait for the authoritative kind instead of
                // briefly loading the generic match scene on that race.
                if (string.IsNullOrEmpty(instanceKind))
                    return null;

                string targetSceneName = string.Equals(
                    instanceKind,
                    SurvivalInstanceKind,
                    StringComparison.OrdinalIgnoreCase)
                    ? survivalSceneName
                    : matchSceneName;
                return activeSceneName == targetSceneName ? null : targetSceneName;
            }

            // Hub intentionally presents the destination screen even though
            // PlayerWorld still records an open-world location. Only an
            // explicit, authoritative instance assignment may transition out
            // of Hub through this coordinator.
            if (isHub)
                return null;

            if (OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(activeSceneName))
                return null;

            return activeSceneName == openWorldSceneName ? null : openWorldSceneName;
        }

        internal static bool SuppressesGameplayPresentation(string sceneName)
            => !ArenaRuntimeSceneGate.IsArenaRuntimeScene(sceneName, string.Empty)
               || NonGameplayScenes.Contains(sceneName);
    }

    internal sealed class LocalWorldRuntimeCoordinator
    {
        private readonly LocalMovementWorldContext _worldContext;
        private readonly Dictionary<ulong, ulong> _arenaSeedsById = new();
        private readonly Dictionary<ulong, string> _arenaKindsById = new();
        private readonly Action<ulong?> _onLocalPlayerWorldUpdate;
        private readonly Action<NetworkManager.GameplayScope> _setGameplayScope;
        private readonly Func<string> _getActiveSceneName;
        private readonly Action<string> _requestSceneLoad;
        private readonly string _matchSceneName;
        private readonly string _survivalSceneName;

        private Identity _localIdentity;
        private bool _hasLocalIdentity;
        private PlayerWorld? _localPlayerWorld;
        private string _preferredOpenWorldSceneName = OpenWorldTravelCatalog.DefaultSceneName;

        internal LocalMovementWorldContext WorldContext => _worldContext;

        internal LocalWorldRuntimeCoordinator(
            LocalMovementWorldContext worldContext,
            Action<ulong?>? onLocalPlayerWorldUpdate = null,
            Action<NetworkManager.GameplayScope>? setGameplayScope = null,
            Func<string>? getActiveSceneName = null,
            Action<string>? requestSceneLoad = null,
            string matchSceneName = ArenaMapCatalog.DefaultSceneName,
            string survivalSceneName = ArenaMapCatalog.DefaultSceneName)
        {
            _worldContext = worldContext;
            _onLocalPlayerWorldUpdate = onLocalPlayerWorldUpdate ?? MatchStateCache.Instance.OnLocalPlayerWorldUpdate;
            _setGameplayScope = setGameplayScope ?? (scope => NetworkManager.Instance?.SetGameplayScope(scope));
            _getActiveSceneName = getActiveSceneName ?? (() => SceneManager.GetActiveScene().name);
            // PlayerWorld updates are delivered inside DbConnection.FrameTick.
            // A Survival death can enqueue Object.Destroy calls later in that
            // same dispatch, so loading synchronously here races Unity's
            // delayed destruction against its scene preload thread.
            _requestSceneLoad = requestSceneLoad ?? RuntimeSceneTransitionQueue.Request;
            _matchSceneName = matchSceneName;
            _survivalSceneName = survivalSceneName;
        }

        internal void SetLocalIdentity(Identity identity)
        {
            _localIdentity = identity;
            _hasLocalIdentity = true;
        }

        internal void ClearForNetworkReconnect()
        {
            _arenaSeedsById.Clear();
            _arenaKindsById.Clear();
            _worldContext.Clear();
            _hasLocalIdentity = false;
            _localPlayerWorld = null;
            _preferredOpenWorldSceneName = OpenWorldTravelCatalog.DefaultSceneName;
        }

        internal void OnPlayerWorldInsert(PlayerWorld row)
        {
            UpdateLocalWorldContext(row);
        }

        internal void OnPlayerWorldUpdate(PlayerWorld row)
        {
            UpdateLocalWorldContext(row);
        }

        internal void OnPlayerWorldDelete(PlayerWorld row)
        {
            if (!_hasLocalIdentity || row.Identity != _localIdentity)
                return;

            _worldContext.Clear();
            _localPlayerWorld = null;
            _onLocalPlayerWorldUpdate(null);
            _setGameplayScope(LocalWorldScopeResolver.Resolve(null, null));
        }

        internal void OnPlayerOpenWorldSceneInsert(PlayerOpenWorldScene row)
        {
            UpdateLocalOpenWorldScene(row);
        }

        internal void OnPlayerOpenWorldSceneUpdate(PlayerOpenWorldScene row)
        {
            UpdateLocalOpenWorldScene(row);
        }

        internal void OnPlayerOpenWorldSceneDelete(PlayerOpenWorldScene row)
        {
            if (!_hasLocalIdentity || row.Identity != _localIdentity)
                return;

            _preferredOpenWorldSceneName = OpenWorldTravelCatalog.DefaultSceneName;
            OpenWorldTravelCatalog.SetCurrentScene(_preferredOpenWorldSceneName);
            if (HasAuthoritativeCurrentWorld(_localPlayerWorld))
                return;

            RefreshLocalWorldRuntime();
        }

        internal void OnArenaInstanceInsert(ArenaInstance row)
        {
            _arenaSeedsById[row.Id] = row.Seed;
            _arenaKindsById[row.Id] = row.InstanceKind;
            _worldContext.SetArenaSeedForInstance(row.Id, row.Seed);
            _worldContext.SetInstanceKindForInstance(row.Id, row.InstanceKind);
            RefreshIfLocalPlayerUsesInstance(row.Id);
        }

        internal void OnArenaInstanceUpdate(ArenaInstance row)
        {
            _arenaSeedsById[row.Id] = row.Seed;
            _arenaKindsById[row.Id] = row.InstanceKind;
            _worldContext.SetArenaSeedForInstance(row.Id, row.Seed);
            _worldContext.SetInstanceKindForInstance(row.Id, row.InstanceKind);
            RefreshIfLocalPlayerUsesInstance(row.Id);
        }

        internal void OnArenaInstanceDelete(ArenaInstance row)
        {
            _arenaSeedsById.Remove(row.Id);
            _arenaKindsById.Remove(row.Id);
            if (_worldContext.InstanceId == row.Id)
                _worldContext.Clear();
        }

        private bool UpdateLocalWorldContext(PlayerWorld row)
        {
            if (!_hasLocalIdentity || row.Identity != _localIdentity)
                return false;

            _localPlayerWorld = row;
            return RefreshLocalWorldRuntime();
        }

        private bool UpdateLocalOpenWorldScene(PlayerOpenWorldScene row)
        {
            if (!_hasLocalIdentity || row.Identity != _localIdentity)
                return false;

            _preferredOpenWorldSceneName = OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(row.SceneName)
                ? row.SceneName
                : OpenWorldTravelCatalog.DefaultSceneName;
            OpenWorldTravelCatalog.SetCurrentScene(_preferredOpenWorldSceneName);
            if (HasAuthoritativeCurrentWorld(_localPlayerWorld))
                return false;

            return RefreshLocalWorldRuntime();
        }

        private static bool HasAuthoritativeCurrentWorld(PlayerWorld? row)
        {
            if (row == null)
                return false;

            if (string.Equals(row.WorldKind, "INSTANCE", StringComparison.OrdinalIgnoreCase)
                && row.InstanceId.HasValue)
            {
                return true;
            }

            return string.Equals(row.WorldKind, "OPEN", StringComparison.OrdinalIgnoreCase)
                   && OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(row.OpenWorldSceneName);
        }

        private bool RefreshLocalWorldRuntime()
        {
            if (_localPlayerWorld == null)
                return false;

            PlayerWorld row = _localPlayerWorld;
            ulong? arenaSeed = null;
            string? instanceKind = null;
            if (row.InstanceId.HasValue && _arenaSeedsById.TryGetValue(row.InstanceId.Value, out ulong seed))
                arenaSeed = seed;
            if (row.InstanceId.HasValue && _arenaKindsById.TryGetValue(row.InstanceId.Value, out string kind))
                instanceKind = kind;
            string currentOpenWorldSceneName = ResolveCurrentOpenWorldSceneName(row);
            string activeSceneName = _getActiveSceneName();

            _worldContext.SetWorldWithInstanceKind(row.WorldKind, row.InstanceId, arenaSeed, instanceKind);
            if (string.Equals(row.WorldKind, "OPEN", StringComparison.OrdinalIgnoreCase))
                OpenWorldTravelCatalog.SetCurrentScene(currentOpenWorldSceneName);
            _onLocalPlayerWorldUpdate(row.InstanceId);
            _setGameplayScope(LocalWorldScopeResolver.Resolve(row, currentOpenWorldSceneName));

            // Legacy/direct instances first commit LeaveInstance and then
            // explicitly load Hub. Provisioned PvP uses the same guard while
            // its handoff coordinator disconnects the disposable database.
            // Suppress the ordinary OPEN-world redirect during either return.
            string? targetScene = RuntimeSceneTransitionQueue.IsExplicitHubReturnPending
                                  && !row.InstanceId.HasValue
                ? null
                : LocalWorldSceneDecider.DetermineTargetScene(
                    activeSceneName,
                    row.InstanceId,
                    instanceKind,
                    currentOpenWorldSceneName,
                    _matchSceneName,
                    _survivalSceneName);
            if (targetScene == null)
                return false;

            if (row.InstanceId.HasValue)
                Debug.Log($"[EntityRegistry] Queueing {targetScene} for {instanceKind} instance {row.InstanceId.Value}.");
            else
                Debug.Log($"[EntityRegistry] Queueing {targetScene} (open world).");

            _requestSceneLoad(targetScene);
            return true;
        }

        private void RefreshIfLocalPlayerUsesInstance(ulong instanceId)
        {
            if (_localPlayerWorld?.InstanceId == instanceId)
                RefreshLocalWorldRuntime();
        }

        private string ResolveCurrentOpenWorldSceneName(PlayerWorld row)
        {
            if (OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(row.OpenWorldSceneName))
                return row.OpenWorldSceneName!;

            return _preferredOpenWorldSceneName;
        }
    }
}

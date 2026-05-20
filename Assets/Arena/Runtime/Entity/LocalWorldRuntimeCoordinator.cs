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
            string openWorldSceneName,
            string matchSceneName)
        {
            if (NonGameplayScenes.Contains(activeSceneName))
                return null;

            if (instanceId.HasValue)
                return activeSceneName == matchSceneName ? null : matchSceneName;

            if (OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(activeSceneName))
                return null;

            return activeSceneName == openWorldSceneName ? null : openWorldSceneName;
        }

        internal static bool SuppressesGameplayPresentation(string sceneName)
            => NonGameplayScenes.Contains(sceneName);
    }

    internal sealed class LocalWorldRuntimeCoordinator
    {
        private readonly LocalMovementWorldContext _worldContext;
        private readonly Dictionary<ulong, ulong> _arenaSeedsById = new();
        private readonly Action<ulong?> _onLocalPlayerWorldUpdate;
        private readonly Action<NetworkManager.GameplayScope> _setGameplayScope;
        private readonly Func<string> _getActiveSceneName;
        private readonly Action<string> _loadScene;
        private readonly string _matchSceneName;

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
            Action<string>? loadScene = null,
            string matchSceneName = "ArenaMatch")
        {
            _worldContext = worldContext;
            _onLocalPlayerWorldUpdate = onLocalPlayerWorldUpdate ?? MatchStateCache.Instance.OnLocalPlayerWorldUpdate;
            _setGameplayScope = setGameplayScope ?? (scope => NetworkManager.Instance?.SetGameplayScope(scope));
            _getActiveSceneName = getActiveSceneName ?? (() => SceneManager.GetActiveScene().name);
            _loadScene = loadScene ?? SceneManager.LoadScene;
            _matchSceneName = matchSceneName;
        }

        internal void SetLocalIdentity(Identity identity)
        {
            _localIdentity = identity;
            _hasLocalIdentity = true;
        }

        internal void ClearForNetworkReconnect()
        {
            _arenaSeedsById.Clear();
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
            _worldContext.SetArenaSeedForInstance(row.Id, row.Seed);
        }

        internal void OnArenaInstanceUpdate(ArenaInstance row)
        {
            _arenaSeedsById[row.Id] = row.Seed;
            _worldContext.SetArenaSeedForInstance(row.Id, row.Seed);
        }

        internal void OnArenaInstanceDelete(ArenaInstance row)
        {
            _arenaSeedsById.Remove(row.Id);
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
            if (row.InstanceId.HasValue && _arenaSeedsById.TryGetValue(row.InstanceId.Value, out ulong seed))
                arenaSeed = seed;
            string currentOpenWorldSceneName = ResolveCurrentOpenWorldSceneName(row);
            string activeSceneName = _getActiveSceneName();

            _worldContext.SetWorld(row.WorldKind, row.InstanceId, arenaSeed);
            if (string.Equals(row.WorldKind, "OPEN", StringComparison.OrdinalIgnoreCase))
                OpenWorldTravelCatalog.SetCurrentScene(currentOpenWorldSceneName);
            _onLocalPlayerWorldUpdate(row.InstanceId);
            _setGameplayScope(LocalWorldScopeResolver.Resolve(row, currentOpenWorldSceneName));

            string? targetScene = LocalWorldSceneDecider.DetermineTargetScene(
                activeSceneName,
                row.InstanceId,
                currentOpenWorldSceneName,
                _matchSceneName);
            if (targetScene == null)
                return false;

            if (row.InstanceId.HasValue)
                Debug.Log($"[EntityRegistry] Loading {_matchSceneName} for instance {row.InstanceId.Value}");
            else
                Debug.Log("[EntityRegistry] Loading Arena (open world).");

            _loadScene(targetScene);
            return true;
        }

        private string ResolveCurrentOpenWorldSceneName(PlayerWorld row)
        {
            if (OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(row.OpenWorldSceneName))
                return row.OpenWorldSceneName!;

            return _preferredOpenWorldSceneName;
        }
    }
}

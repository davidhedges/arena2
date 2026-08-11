#nullable enable

using System;

namespace Arena.World
{
    internal readonly struct ArenaMapProfile
    {
        internal ArenaMapProfile(
            string mapId,
            string sceneName,
            string dataKey,
            string layoutResourcePath,
            string movementCollisionResourcePath,
            string queryCollisionResourcePath)
        {
            MapId = mapId;
            SceneName = sceneName;
            DataKey = dataKey;
            LayoutResourcePath = layoutResourcePath;
            MovementCollisionResourcePath = movementCollisionResourcePath;
            QueryCollisionResourcePath = queryCollisionResourcePath;
        }

        internal string MapId { get; }
        internal string SceneName { get; }
        internal string DataKey { get; }
        internal string LayoutResourcePath { get; }
        internal string MovementCollisionResourcePath { get; }
        internal string QueryCollisionResourcePath { get; }
    }

    /// <summary>
    /// Stable identities for authored competitive/training maps.
    /// Match rules select a mode; this catalog selects the Unity map asset.
    /// </summary>
    internal static class ArenaMapCatalog
    {
        internal const string ArenaMap01Id = "ARENA_MAP_01";
        internal const string ArenaMap01SceneName = "Arena_Map_01";
        internal const string ArenaMap01DataKey = "arena_map_01";
        internal const string ArenaMap01LayoutResourcePath =
            "SharedData/Maps/arena_map_01.layout.shared";
        internal const string ArenaMap01MovementCollisionResourcePath =
            "SharedData/Maps/arena_map_01.collision.shared";
        internal const string ArenaMap01QueryCollisionResourcePath =
            "SharedData/Maps/arena_map_01.query_collision.shared";

        internal const string DefaultMapId = ArenaMap01Id;
        internal const string DefaultSceneName = ArenaMap01SceneName;

        internal static bool IsRegisteredSceneName(string? sceneName)
            => string.Equals(sceneName, ArenaMap01SceneName, StringComparison.Ordinal);

        internal static bool TryResolveSceneName(string? mapId, out string sceneName)
        {
            if (TryResolve(mapId, out ArenaMapProfile profile))
            {
                sceneName = profile.SceneName;
                return true;
            }

            sceneName = string.Empty;
            return false;
        }

        internal static bool TryResolve(string? mapId, out ArenaMapProfile profile)
        {
            if (string.Equals(mapId, ArenaMap01Id, StringComparison.OrdinalIgnoreCase))
            {
                profile = new ArenaMapProfile(
                    ArenaMap01Id,
                    ArenaMap01SceneName,
                    ArenaMap01DataKey,
                    ArenaMap01LayoutResourcePath,
                    ArenaMap01MovementCollisionResourcePath,
                    ArenaMap01QueryCollisionResourcePath);
                return true;
            }

            profile = default;
            return false;
        }

        internal static ArenaMapProfile DefaultProfile
            => TryResolve(DefaultMapId, out ArenaMapProfile profile)
                ? profile
                : throw new InvalidOperationException("The default authored arena map is not registered.");
    }
}

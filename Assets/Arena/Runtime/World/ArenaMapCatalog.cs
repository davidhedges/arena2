#nullable enable

using System;

namespace Arena.World
{
    /// <summary>
    /// Stable identities for authored competitive/training maps.
    /// Match rules select a mode; this catalog selects the Unity map asset.
    /// </summary>
    internal static class ArenaMapCatalog
    {
        internal const string ArenaMap01Id = "ARENA_MAP_01";
        internal const string ArenaMap01SceneName = "Arena_Map_01";

        internal const string DefaultMapId = ArenaMap01Id;
        internal const string DefaultSceneName = ArenaMap01SceneName;

        internal static bool IsRegisteredSceneName(string? sceneName)
            => string.Equals(sceneName, ArenaMap01SceneName, StringComparison.Ordinal);

        internal static bool TryResolveSceneName(string? mapId, out string sceneName)
        {
            if (string.Equals(mapId, ArenaMap01Id, StringComparison.OrdinalIgnoreCase))
            {
                sceneName = ArenaMap01SceneName;
                return true;
            }

            sceneName = string.Empty;
            return false;
        }
    }
}

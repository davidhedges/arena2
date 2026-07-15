#nullable enable

using Arena.World;
using UnityEngine.SceneManagement;

namespace Arena
{
    internal static class ArenaRuntimeSceneGate
    {
        private const string ArenaScenePathPrefix = "Assets/Arena/Content/Scenes/";

        // Authoring/demo scenes (shader demos, art review, etc.) live with the
        // other scenes but must not boot the networked runtime in play mode.
        private const string AuthoringScenePathPrefix = "Assets/Arena/Content/Scenes/Authoring/";

        internal static bool IsArenaRuntimeScene(Scene scene)
            => IsArenaRuntimeScene(scene.name, scene.path);

        internal static bool IsArenaRuntimeScene(string sceneName, string scenePath)
        {
            if (!string.IsNullOrEmpty(scenePath))
                return scenePath.StartsWith(ArenaScenePathPrefix, System.StringComparison.Ordinal)
                       && !scenePath.StartsWith(AuthoringScenePathPrefix, System.StringComparison.Ordinal);

            return string.Equals(sceneName, "Arena", System.StringComparison.Ordinal)
                   || string.Equals(sceneName, "Arena_VerdantStand_Blockout", System.StringComparison.Ordinal)
                   || string.Equals(sceneName, "ArenaMatch", System.StringComparison.Ordinal)
                   || string.Equals(sceneName, "TrainingGround", System.StringComparison.Ordinal)
                   || string.Equals(sceneName, "Hub", System.StringComparison.Ordinal)
                   || string.Equals(sceneName, "CharacterCreation", System.StringComparison.Ordinal)
                   || string.Equals(sceneName, "CharacterCustomization", System.StringComparison.Ordinal)
                   || OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(sceneName);
        }

        internal static bool ShouldRunArenaRuntimeInActiveScene()
            => IsArenaRuntimeScene(SceneManager.GetActiveScene());
    }
}

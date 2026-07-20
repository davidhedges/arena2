#nullable enable

using System;
using UnityEngine;

namespace Arena.World
{
    public static class OpenWorldTravelCatalog
    {
        public readonly struct Destination
        {
            public Destination(string sceneName, string displayName)
            {
                SceneName = sceneName;
                DisplayName = displayName;
            }

            public string SceneName { get; }
            public string DisplayName { get; }
        }

        private const string PreferredScenePlayerPrefsKey = "arena.open_world.preferred_scene";
        public const string DefaultSceneName = "Oasis_Day";
        public const string RandomDungeonSceneName = "RandomDungeon";

        private static readonly Destination[] Destinations =
        {
            new("Adventure_Island", "Adventure Island"),
            new("Desert_Day", "Desert Day"),
            new("Docks_Day", "Docks Day"),
            new("Giant_Skeleton", "Giant Skeleton"),
            new("Golden_Valley_Overcast", "Golden Valley Overcast"),
            new("Golden_Valley_Sunny", "Golden Valley Sunny"),
            new("Great_Hall_Day", "Great Hall Day"),
            new("Idol_Day", "Idol Day"),
            new("Oasis_Day", "Oasis Day"),
            new(RandomDungeonSceneName, "Random Dungeon"),
            new("Temple_Gardens", "Temple Gardens"),
        };

        public static Destination[] All => Destinations;

        public static string CurrentSceneName
        {
            get
            {
                string stored = PlayerPrefs.GetString(PreferredScenePlayerPrefsKey, DefaultSceneName);
                return IsRegisteredOpenWorldScene(stored) ? stored : DefaultSceneName;
            }
        }

        public static string CurrentDisplayName => DisplayNameForScene(CurrentSceneName);

        public static bool IsRegisteredOpenWorldScene(string? sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
                return false;

            foreach (Destination destination in Destinations)
            {
                if (string.Equals(destination.SceneName, sceneName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        public static string DisplayNameForScene(string? sceneName)
        {
            if (!string.IsNullOrWhiteSpace(sceneName))
            {
                foreach (Destination destination in Destinations)
                {
                    if (string.Equals(destination.SceneName, sceneName, StringComparison.Ordinal))
                        return destination.DisplayName;
                }
            }

            return string.IsNullOrWhiteSpace(sceneName) ? "Unknown" : sceneName!;
        }

        public static void SetCurrentScene(string sceneName)
        {
            if (!IsRegisteredOpenWorldScene(sceneName))
                throw new ArgumentException($"Unknown open-world scene '{sceneName}'.", nameof(sceneName));

            PlayerPrefs.SetString(PreferredScenePlayerPrefsKey, sceneName);
            PlayerPrefs.Save();
        }
    }
}

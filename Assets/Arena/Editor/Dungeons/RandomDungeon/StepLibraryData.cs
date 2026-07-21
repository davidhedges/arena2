using System;
using System.IO;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    internal sealed class StairConnectorSettings
    {
        public const string Path =
            "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/stair_connector_settings.json";

        private readonly string stairConnectorDirectory;
        private readonly string primaryStair;
        private readonly string primaryStairPath;

        private StairConnectorSettings(string stairConnectorDirectory, string primaryStair)
        {
            this.stairConnectorDirectory = NormalizeAssetDirectory(stairConnectorDirectory);
            this.primaryStair = primaryStair;
            primaryStairPath = ResolvePrefabPath(this.stairConnectorDirectory, primaryStair);
        }

        public static StairConnectorSettings Load()
        {
            if (!File.Exists(Path))
            {
                throw new FileNotFoundException(Path);
            }

            JObject root = JObject.Parse(File.ReadAllText(Path));
            if (!(root["stairConnectors"] is JObject connectors))
            {
                throw new InvalidOperationException($"{Path} is missing a 'stairConnectors' object.");
            }

            string directory = connectors.Value<string>("directory");
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException($"{Path} stairConnectors is missing directory.");
            }

            string primaryStair = connectors.Value<string>("primaryStair");
            if (string.IsNullOrWhiteSpace(primaryStair))
            {
                throw new InvalidOperationException($"{Path} stairConnectors is missing primaryStair.");
            }

            return new StairConnectorSettings(directory, primaryStair);
        }

        public string StairConnectorDirectory => stairConnectorDirectory;

        public string PrimaryStair => primaryStair;

        public string PrimaryStairPath => primaryStairPath;

        private static string ResolvePrefabPath(string directory, string prefabNameOrPath)
        {
            string normalized = NormalizeAssetPath(prefabNameOrPath);
            if (normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                    ? normalized
                    : normalized + ".prefab";
            }

            string fileName = normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : normalized + ".prefab";
            return $"{directory}/{fileName}";
        }

        private static string NormalizeAssetDirectory(string path)
        {
            return NormalizeAssetPath(path).TrimEnd('/');
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim();
        }
    }
}

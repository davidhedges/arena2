#nullable enable

using System;
using System.Globalization;
using System.Linq;
using DungeonLab.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.Editor
{
    public static class WorldInteractionFoundationBuilder
    {
        private const string RandomDungeonScenePath =
            "Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity";
        private const string SeedPrefix = "Random Dungeon Seed ";

        [MenuItem("Arena/Interaction/Rebuild Approved Foundation Assets", false, 10)]
        public static void RebuildApprovedFoundationAssets()
        {
            int seed = ReadCheckedInRandomDungeonSeed();
            ThirdPartyAnimationExtractor.ExtractHumanoidUseProfile();
            InteractiveGatewayPrefabBuilder.BuildAll();
            RandomDungeonSceneBuilder.RebuildWithSeed(seed);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"[{nameof(WorldInteractionFoundationBuilder)}] Rebuilt interaction assets and RandomDungeon seed {seed}.");
        }

        private static int ReadCheckedInRandomDungeonSeed()
        {
            Scene scene = EditorSceneManager.OpenScene(
                RandomDungeonScenePath,
                OpenSceneMode.Single);
            GameObject? marker = scene.GetRootGameObjects()
                .FirstOrDefault(root => root.name.StartsWith(
                    SeedPrefix,
                    StringComparison.Ordinal));
            if (marker == null)
            {
                throw new InvalidOperationException(
                    $"{RandomDungeonScenePath} has no '{SeedPrefix}<number>' marker.");
            }

            string value = marker.name.Substring(SeedPrefix.Length);
            if (!int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int seed))
            {
                throw new InvalidOperationException(
                    $"Could not parse random dungeon seed from '{marker.name}'.");
            }

            return seed;
        }
    }
}

#nullable enable

using System.IO;
using NUnit.Framework;
using UnityEditor;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase7ProductionHardeningTests
    {
        private const string GeneratorPath =
            "Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonLabGenerator.cs";

        [Test]
        public void RequiredProductionSettingsAndAssets_AreResolvable()
        {
            string[] requiredPaths =
            {
                "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/generation_profile.asset",
                "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/package_inventory.json",
                "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/stair_proof_contracts.json",
                "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/forged_stair_contracts.json",
                "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/stair_connector_settings.json",
                "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/step_piece_library.json",
                "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/Catalog/dungeon_recipe_catalog.asset",
                "Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity",
                "Assets/Arena/Content/Scenes/OpenWorld/Great_Hall_Day.unity"
            };

            foreach (string path in requiredPaths)
            {
                Assert.That(File.Exists(path), Is.True, $"Missing required production asset '{path}'.");
                Assert.That(
                    AssetDatabase.LoadMainAssetAtPath(path),
                    Is.Not.Null,
                    $"Unity could not resolve required production asset '{path}'.");
            }

            UnityEngine.Object catalog = AssetDatabase.LoadMainAssetAtPath(
                "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/Catalog/dungeon_recipe_catalog.asset");
            var serializedCatalog = new SerializedObject(catalog);
            Assert.That(serializedCatalog.FindProperty("schemaVersion").intValue, Is.EqualTo(1));
            SerializedProperty recipes = serializedCatalog.FindProperty("recipes");
            Assert.That(recipes.arraySize, Is.EqualTo(3));
            for (int index = 0; index < recipes.arraySize; index++)
            {
                Assert.That(recipes.GetArrayElementAtIndex(index).objectReferenceValue, Is.Not.Null);
            }
        }

        [Test]
        public void MissingProductionProfile_HasAnExplicitCodedFailureInsteadOfDefaults()
        {
            string source = File.ReadAllText(GeneratorPath);

            Assert.That(source, Does.Contain("[GENERATION_PROFILE] missing required production profile"));
            Assert.That(
                source,
                Does.Not.Contain("profile != null ? profile.ToSettings() : DungeonGenerationSettings.Default"));
        }

        [Test]
        public void FinalDeletionLedger_HasNoParkedLatePassOrLegacyRendererScaffolding()
        {
            string source = File.ReadAllText(GeneratorPath) +
                File.ReadAllText("Assets/Arena/Editor/Dungeons/RandomDungeon/ElevationEdgeModel.cs") +
                File.ReadAllText("Assets/Arena/Editor/Dungeons/RandomDungeon/StepLibraryData.cs");

            foreach (string retiredSymbol in new[]
                     {
                         "ActiveStepFormationPlacementEnabled",
                         "TryPlaceActiveStepFormation",
                         "StepFormationPlacement",
                         "PlacementValidationState",
                         "DungeonPrefabContractCatalog",
                         "MeasuredDungeonContracts",
                         "LoadWeightedStraightStairConnectorOptions",
                         "StepLibraryIndexPath",
                         "StepFormationModeTable",
                         "StepLibraryIndex",
                         "StepLibraryRecord"
                     })
            {
                Assert.That(source, Does.Not.Contain(retiredSymbol), $"Retired symbol '{retiredSymbol}' survived.");
            }

            string connectorSettings = File.ReadAllText(
                "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/stair_connector_settings.json");
            Assert.That(connectorSettings, Does.Not.Contain("\"formations\""));
            Assert.That(connectorSettings, Does.Not.Contain("\"contracts\""));
        }
    }
}

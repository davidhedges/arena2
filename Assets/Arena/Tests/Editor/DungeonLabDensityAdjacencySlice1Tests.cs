#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabDensityAdjacencySlice1Tests
    {
        private const string GeneratorPath =
            "Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonLabGenerator.cs";
        private const string BatchPath =
            "Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonLabGenerator.Batch.cs";
        private const string ProfileSourcePath =
            "Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonGenerationProfile.cs";
        private const string SpaciousProfilePath =
            "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/generation_profile.asset";
        private const string DenseProfilePath =
            "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/generation_profile_dense.asset";
        private const string ProfileEnvironmentVariable = "ARENA_DUNGEON_GENERATION_PROFILE";
        private const string ProfileEditorPreferenceKey = "Arena.DungeonLab.GenerationProfileId";

        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void ProfileResolver_HasTwoExplicitIdentitiesAndNoFallbackAssetCreation()
        {
            string generator = File.ReadAllText(GeneratorPath);
            string batch = File.ReadAllText(BatchPath);
            string profileSource = File.ReadAllText(ProfileSourcePath);

            Assert.That(File.Exists(SpaciousProfilePath), Is.True);
            Assert.That(File.Exists(DenseProfilePath), Is.True);
            Assert.That(
                Count(generator, "private static DungeonGenerationSettings LoadActiveGenerationSettings(string profileId)"),
                Is.EqualTo(1));
            Assert.That(generator + batch, Does.Not.Contain("LoadActiveGenerationSettings()"));
            Assert.That(Count(generator, "profile.ToSettings()"), Is.EqualTo(1));
            Assert.That(generator, Does.Contain("ARENA_DUNGEON_GENERATION_PROFILE"));
            Assert.That(generator, Does.Contain("case \"spacious\":"));
            Assert.That(generator, Does.Contain("case \"dense\":"));
            Assert.That(generator, Does.Contain("unknown profile id"));
            Assert.That(generator, Does.Not.Contain("AssetDatabase.CreateAsset(profile"));
            Assert.That(generator + profileSource, Does.Not.Contain("DungeonGenerationSettings.Default"));
        }

        [Test]
        public void UnityProfileSelector_PersistsTheChoiceAndKeepsTheEnvironmentOverride()
        {
            string generator = File.ReadAllText(GeneratorPath);
            MethodInfo select = GeneratorType.GetMethod(
                "SelectEditorGenerationProfile",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            MethodInfo resolve = GeneratorType.GetMethod(
                "ResolveRequestedGenerationProfileId",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            MethodInfo resolveEditor = GeneratorType.GetMethod(
                "ResolveEditorGenerationProfileId",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            string? originalEnvironment = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable);
            bool hadOriginalPreference = EditorPrefs.HasKey(ProfileEditorPreferenceKey);
            string originalPreference = EditorPrefs.GetString(ProfileEditorPreferenceKey, string.Empty);

            try
            {
                Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, null);
                select.Invoke(null, new object[] { "dense" });
                Assert.That(EditorPrefs.GetString(ProfileEditorPreferenceKey), Is.EqualTo("dense"));
                Assert.That((string)resolveEditor.Invoke(null, Array.Empty<object>())!, Is.EqualTo("dense"));
                Assert.That(
                    (string)resolve.Invoke(null, Array.Empty<object>())!,
                    Is.EqualTo(Application.isBatchMode ? "spacious" : "dense"));

                Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, "spacious");
                Assert.That((string)resolve.Invoke(null, Array.Empty<object>())!, Is.EqualTo("spacious"));

                Assert.That(generator, Does.Contain("Arena/Dungeons/Generation Profile/Spacious"));
                Assert.That(generator, Does.Contain("Arena/Dungeons/Generation Profile/Dense"));
                Assert.That(Count(generator, "Menu.SetChecked("), Is.EqualTo(1));
                Assert.That(Count(generator, "EditorPrefs.SetString(GenerationProfileEditorPreferenceKey"), Is.EqualTo(1));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, originalEnvironment);
                if (hadOriginalPreference)
                {
                    EditorPrefs.SetString(ProfileEditorPreferenceKey, originalPreference);
                }
                else
                {
                    EditorPrefs.DeleteKey(ProfileEditorPreferenceKey);
                }
            }
        }

        [Test]
        public void Profiles_AreDistinctIdentitiesAndExposeAllCurrentSettingsValues()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["profiles.spaciousId"], Is.EqualTo("spacious"));
            Assert.That(values["profiles.denseId"], Is.EqualTo("dense"));
            Assert.That(values["profiles.spaciousDigest"], Has.Length.EqualTo(64));
            Assert.That(values["profiles.denseDigest"], Has.Length.EqualTo(64));
            Assert.That(values["profiles.digestDistinct"], Is.EqualTo("True"));
            Assert.That(values["profiles.behaviorValuesEqual"], Is.EqualTo("False"));
            Assert.That(values["profiles.valueCount"], Is.EqualTo("9"));
        }

        [Test]
        public void PerSeedMeasurements_CoverGraphCorridorsDoorsAndVoids()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["processional.topology"], Is.EqualTo("processional-spine"));
            Assert.That(values["processional.degreeSamples"], Is.EqualTo("13"));
            Assert.That(
                values["processional.connectionLengthSamples"],
                Is.EqualTo(values["processional.connectionCount"]));
            Assert.That(int.Parse(values["processional.sharedWallDoors"]), Is.GreaterThanOrEqualTo(0));
            Assert.That(int.Parse(values["processional.reservedVistaCells"]), Is.GreaterThan(0));
            Assert.That(values["processional.atriumCenterVoidIsNull"], Is.EqualTo("True"));

            Assert.That(values["atrium.topology"], Is.EqualTo("atrium-ring"));
            Assert.That(int.Parse(values["atrium.centerVoidCells"]), Is.GreaterThan(0));
            Assert.That(values["twinWing.topology"], Is.EqualTo("twin-wing-keep"));
            Assert.That(values["twinWing.atriumCenterVoidIsNull"], Is.EqualTo("True"));
        }

        [Test]
        public void SettingsAndMeasurements_AreDeterministicForARepeatedSeed()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["determinism.settingsDigest"], Is.EqualTo("True"));
            Assert.That(values["determinism.canonical"], Is.EqualTo("True"));
            Assert.That(values["determinism.measurements"], Is.EqualTo("True"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildDensityAdjacencySlice1Snapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing density/adjacency Slice 1 diagnostic.");
            return Parse((string)method.Invoke(null, Array.Empty<object>())!);
        }

        private static Dictionary<string, string> Parse(string snapshot)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in snapshot.Split('\n'))
            {
                int separator = line.IndexOf('=');
                if (separator >= 0)
                {
                    result[line.Substring(0, separator)] = line.Substring(separator + 1);
                }
            }

            return result;
        }

        private static int Count(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }
    }
}

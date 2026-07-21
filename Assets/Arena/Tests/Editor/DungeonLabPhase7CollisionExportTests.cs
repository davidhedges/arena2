#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase7CollisionExportTests
    {
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void LockedSelector_ProducesSixControlsAndEightSeedsPerTopology()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["selection.count"], Is.EqualTo("30"));
            Assert.That(values["selection.unique"], Is.EqualTo("30"));
            Assert.That(values["selection.sentinels"], Is.EqualTo("6"));
            Assert.That(values["selection.processional"], Is.EqualTo("8"));
            Assert.That(values["selection.atrium"], Is.EqualTo("8"));
            Assert.That(values["selection.twinWing"], Is.EqualTo("8"));
            Assert.That(values["selection.slots"], Is.EqualTo("24"));
        }

        [Test]
        public void ValidationDestinations_CannotOverwriteProductionArtifacts()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["guard.productionScene"], Is.EqualTo("True"));
            Assert.That(values["guard.productionData"], Is.EqualTo("True"));
        }

        [Test]
        public void SourceParity_IsExactAndDetectsAnOmittedCollider()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["parity.exactNames"], Is.EqualTo("True"));
            Assert.That(values["parity.missingDetected"], Is.EqualTo("True"));
        }

        [Test]
        public void RenderExportPerformanceBudget_RemainsLocked()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["performance.includedStageCount"], Is.EqualTo("7"));
            Assert.That(values["performance.excludedStageCount"], Is.EqualTo("4"));
            Assert.That(values["performance.includesPlanAndRender"], Is.EqualTo("True"));
            Assert.That(values["performance.includesExport"], Is.EqualTo("True"));
            Assert.That(values["performance.excludesSceneSaves"], Is.EqualTo("True"));
            Assert.That(values["performance.p95Ms"], Is.EqualTo("2500"));
            Assert.That(values["performance.maxMs"], Is.EqualTo("5000"));
            Assert.That(values["report.version"], Is.EqualTo("phase7-collision-export-v1"));
        }

        [Test]
        public void ProductionAndValidationEntryPoints_ShareOneRebuildCore()
        {
            string source = File.ReadAllText(
                "Assets/Arena/Editor/Dungeons/RandomDungeon/RandomDungeonSceneBuilder.cs");

            Assert.That(Count(source, "DungeonLabGenerator.GenerateWithSeed(seed);"), Is.EqualTo(1));
            Assert.That(
                Count(source, "GameplayCollisionExporter.ExportActiveSceneSharedCollisionData(dataKey);"),
                Is.EqualTo(1));
            Assert.That(source, Does.Contain("RebuildWithSeedForValidation"));
            Assert.That(source, Does.Contain("addSceneToBuildSettings: false"));
            Assert.That(source, Does.Contain("addSceneToBuildSettings: true"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildPhase7CollisionSupportSnapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing Phase 7 collision-export support diagnostic.");
            return Parse((string)method.Invoke(null, Array.Empty<object>())!);
        }

        private static Dictionary<string, string> Parse(string snapshot)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in snapshot.Split('\n'))
            {
                int separator = line.IndexOf('=');
                if (separator >= 0)
                    result[line.Substring(0, separator)] = line.Substring(separator + 1);
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

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabDensityAdjacencySlice6Tests
    {
        private const string RouteSourcePath =
            "Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonLabGenerator.RouteFirstPilot.cs";
        private const string ProfileSourcePath =
            "Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonGenerationProfile.cs";
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void PatternSpatialSettings_OwnTheValidatedCountAndEligibilityPolicy()
        {
            Dictionary<string, string> values = Snapshot.Value;
            string profileSource = File.ReadAllText(ProfileSourcePath);

            Assert.That(values["profiles.spaciousPolicy"], Is.EqualTo("2@8"));
            Assert.That(values["profiles.densePolicy"], Is.EqualTo("2@8"));
            Assert.That(values["profiles.atriumPolicy"], Is.EqualTo("0@8"));
            Assert.That(values["profiles.twinWingPolicy"], Is.EqualTo("0@8"));
            Assert.That(profileSource, Does.Contain("public DungeonTierSeamAdjacencySettings tierSeamAdjacency"));
            Assert.That(profileSource, Does.Contain("value.requestedCount = Mathf.Max(0, value.requestedCount)"));
            Assert.That(profileSource, Does.Contain("value.maximumRiseLevels = value.maximumRiseLevels >= 8 ? 8 : 4"));
        }

        [Test]
        public void Policy_SelectsTheExactRequestedEligibleNonTraversalPairs()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(
                values["policy.productionPairs"],
                Is.EqualTo("approach>vista-source:r4|rejoin>branch-passage:r8"));
            Assert.That(values["policy.zeroCount"], Is.EqualTo("0"));
            Assert.That(
                values["policy.fourAndEightPairs"],
                Is.EqualTo("approach>vista-source:r4|rejoin>branch-passage:r8"));
        }

        [Test]
        public void UnsatisfiedCount_FailsExplicitlyWithoutAFallbackPair()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["policy.unsatisfiedRejected"], Is.EqualTo("True"));
            Assert.That(values["policy.unsatisfiedError"], Does.StartWith("[TIER_SEAM_ADJACENCY]"));
            Assert.That(values["policy.unsatisfiedError"], Does.Contain("requested 3"));
            Assert.That(values["policy.unsatisfiedError"], Does.Contain("declared 2"));
        }

        [Test]
        public void DefaultProcessionalPairs_AbutThroughTheExistingAppendagePath()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["probe.accepted"], Is.EqualTo("True"), values["probe.rejection"]);
            Assert.That(values["probe.everyDefaultPairAbuts"], Is.EqualTo("True"), values["probe.seams"]);
            Assert.That(values["probe.seams"], Does.Contain("approach>vista-source:r4:e"));
            Assert.That(values["probe.seams"], Does.Contain("rejoin>branch-passage:r8:e"));
        }

        [Test]
        public void DefaultPolicies_ApplyTheApprovedChangeAndPreserveNonProcessionalIsolation()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["spacious.changedFromSlice5"], Is.EqualTo("True"));
            Assert.That(values["spacious.deterministic"], Is.EqualTo("True"));
            Assert.That(values["dense.changedFromSlice5"], Is.EqualTo("True"));
            Assert.That(values["dense.deterministic"], Is.EqualTo("True"));
            Assert.That(values["atrium.canonicalSame"], Is.EqualTo("True"));
            Assert.That(values["atrium.measurementsSame"], Is.EqualTo("True"));
            Assert.That(values["twinWing.canonicalSame"], Is.EqualTo("True"));
            Assert.That(values["twinWing.measurementsSame"], Is.EqualTo("True"));
        }

        [Test]
        public void OnePolicyProducer_FeedsTheExistingOverlookConsumers()
        {
            string routeSource = File.ReadAllText(RouteSourcePath);

            Assert.That(
                Count(routeSource, "private static RouteOverlookIntent[] BuildPlannedOverlooks("),
                Is.EqualTo(1));
            Assert.That(
                Count(routeSource, "plannedOverlooks: BuildPlannedOverlooks("),
                Is.EqualTo(3));
            Assert.That(routeSource, Does.Not.Contain("plannedOverlooks: new[]"));
            Assert.That(routeSource, Does.Not.Contain("plannedOverlooks: Array.Empty<RouteOverlookIntent>()"));
            Assert.That(routeSource, Does.Contain("AddPlannedOverlookAppendages("));
            Assert.That(routeSource, Does.Contain("foreach (RouteOverlookIntent pair in intent.plannedOverlooks)"));
            Assert.That(routeSource, Does.Not.Contain("profileName"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildDensityAdjacencySlice6Snapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing density/adjacency Slice 6 diagnostic.");
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

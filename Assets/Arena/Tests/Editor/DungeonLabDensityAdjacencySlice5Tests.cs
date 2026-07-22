#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabDensityAdjacencySlice5Tests
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
        public void Profiles_ExposeOneValidatedProcessionalNeighborBiasKnob()
        {
            Dictionary<string, string> values = Snapshot.Value;
            string profileSource = File.ReadAllText(ProfileSourcePath);

            Assert.That(values["profiles.spaciousBias"], Is.EqualTo("0"));
            Assert.That(values["profiles.denseBias"], Is.EqualTo("1"));
            Assert.That(profileSource, Does.Contain("neighborBiasStrengthCells"));
            Assert.That(profileSource, Does.Contain("Mathf.Max(0, value.neighborBiasStrengthCells)"));
        }

        [Test]
        public void ZeroBias_RemainsAcceptedAndDeterministic()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["spacious.accepted"], Is.EqualTo("True"));
            Assert.That(values["spacious.valid"], Is.EqualTo("True"));
            Assert.That(values["spacious.canonical"], Is.Not.Empty);
            Assert.That(values["spacious.deterministic"], Is.EqualTo("True"));
        }

        [Test]
        public void Bias_ProducesAWorkingSharedWallDoorFromStableAnchors()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["probe.connected"], Is.EqualTo("True"), values["probe.rejection"]);
            Assert.That(values["probe.centers"], Is.EqualTo("1,0|9,0|-9,0"));
            Assert.That(values["probe.anchorsInside"], Is.EqualTo("True"));
            Assert.That(values["probe.overlaps"], Is.EqualTo("False"));
            Assert.That(values["probe.levelExterior"], Is.EqualTo("0"));
            Assert.That(values["probe.doorways"], Is.EqualTo("1"));
            Assert.That(values["probe.doorwayJoinsRooms"], Is.EqualTo("True"));
            Assert.That(values["probe.rendererRejected"], Is.EqualTo("0"));
            Assert.That(int.Parse(values["probe.collisionSources"]), Is.GreaterThan(0));
            Assert.That(values["probe.collisionMissingMeshes"], Is.EqualTo("0"));
        }

        [Test]
        public void Bias_DoesNotConsumeTheStairBearingEdgeGap()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(
                int.Parse(values["probe.stairExteriorAfter"]),
                Is.GreaterThanOrEqualTo(int.Parse(values["probe.stairExteriorBefore"])));
        }

        [Test]
        public void DenseSentinels_GainSharedWallDoorsAndRemainHardValid()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["dense.accepted"], Is.EqualTo("True"));
            Assert.That(values["dense.valid"], Is.EqualTo("True"));
            Assert.That(values["dense.deterministic"], Is.EqualTo("True"));
            Assert.That(values["sentinels.validPairs"], Is.EqualTo("3"), values["sentinels.results"]);
            Assert.That(
                int.Parse(values["sentinels.denseDoors"]),
                Is.GreaterThan(int.Parse(values["sentinels.spaciousDoors"])),
                values["sentinels.results"]);
        }

        [Test]
        public void AtriumAndTwinWingStayOnTheUnbiasedPath_AndOneCompilerUsesNodeAnchors()
        {
            Dictionary<string, string> values = Snapshot.Value;
            string routeSource = File.ReadAllText(RouteSourcePath);

            Assert.That(values["atrium.canonicalSame"], Is.EqualTo("True"));
            Assert.That(values["atrium.measurementsSame"], Is.EqualTo("True"));
            Assert.That(values["twinWing.canonicalSame"], Is.EqualTo("True"));
            Assert.That(values["twinWing.measurementsSame"], Is.EqualTo("True"));
            Assert.That(routeSource, Does.Contain("Vector2Int delta = nodeCenters[edge.toNode] - nodeCenters[edge.fromNode]"));
            Assert.That(routeSource, Does.Contain("Vector2Int pathStart = nodeCenters[edge.fromNode]"));
            Assert.That(routeSource, Does.Contain("TryGetRecipeSlot(intent.recipeSlots, nodeIndex, out _)"));
            Assert.That(routeSource, Does.Not.Contain("Vector2Int delta = toRoom.Center - fromRoom.Center"));
            Assert.That(routeSource, Does.Not.Contain("profileName"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildDensityAdjacencySlice5Snapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing density/adjacency Slice 5 diagnostic.");
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
    }
}

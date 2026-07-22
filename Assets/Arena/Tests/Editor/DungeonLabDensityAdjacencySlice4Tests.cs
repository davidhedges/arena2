#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabDensityAdjacencySlice4Tests
    {
        private const string RouteSourcePath =
            "Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonLabGenerator.RouteFirstPilot.cs";
        private const string BatchSourcePath =
            "Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonLabGenerator.Batch.cs";
        private const string ProfileSourcePath =
            "Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonGenerationProfile.cs";
        private const string SpaciousBaselineCanonical =
            "af4bce4800980db2d44ae2502600790a31cb0df287ed31100943f21baca5c4d9";
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void Profiles_ExposeOneValidatedProcessionalSpatialConfiguration()
        {
            Dictionary<string, string> values = Snapshot.Value;
            string profileSource = File.ReadAllText(ProfileSourcePath);
            string routeSource = File.ReadAllText(RouteSourcePath);

            Assert.That(values["profiles.valueCount"], Is.EqualTo("9"));
            Assert.That(
                values["profiles.spaciousSpatial"],
                Is.EqualTo("9x9:r4:b0:5-5x7-7|5-5x5-6|4-5x5-5"));
            Assert.That(
                values["profiles.denseSpatial"],
                Is.EqualTo("9x8:r4:b1:7-7x7-7|7-7x7-7|7-7x7-7"));
            Assert.That(
                Count(profileSource, "public DungeonPatternSpatialSettings processionalSpatial"),
                Is.EqualTo(2));
            Assert.That(
                Count(routeSource, "CurrentGenerationSettings.Validated().processionalSpatial"),
                Is.EqualTo(1));
            Assert.That(routeSource, Does.Not.Contain("profileName"));
        }

        [Test]
        public void EverySpatialConsumer_UsesTheResolvedConfiguration()
        {
            string routeSource = File.ReadAllText(RouteSourcePath);
            string batchSource = File.ReadAllText(BatchSourcePath);

            Assert.That(routeSource + batchSource, Does.Not.Contain("Phase1RoomEnvelopeRadius"));
            Assert.That(routeSource, Does.Not.Contain("horizontalSpacing:"));
            Assert.That(routeSource, Does.Not.Contain("verticalSpacing:"));
            Assert.That(routeSource, Does.Contain("coarseEmbedding[node].x * spatial.horizontalPitchCells"));
            Assert.That(routeSource, Does.Contain("coarseEmbedding[node].y * spatial.verticalPitchCells"));
            Assert.That(routeSource, Does.Contain("spatial.roomEnvelopeRadiusCells * 2 + 1"));
            Assert.That(routeSource, Does.Contain("center.x - spatial.roomEnvelopeRadiusCells"));
            Assert.That(batchSource, Does.Contain("ResolvePatternSpatialSettings(phase1LastRouteIntent.patternId)"));
            Assert.That(batchSource, Does.Contain("RoomEnvelope(phase1LastNodeCenters[node], spatial)"));
        }

        [Test]
        public void SpaciousConfiguration_PreservesExactProcessionalOutput()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["processional.spaciousAccepted"], Is.EqualTo("True"));
            Assert.That(values["processional.spaciousValid"], Is.EqualTo("True"));
            Assert.That(values["processional.spaciousCanonical"], Is.EqualTo(SpaciousBaselineCanonical));
            Assert.That(values["processional.spaciousDeterministic"], Is.EqualTo("True"));
            Assert.That(values["processional.spaciousHorizontalPitch"], Is.EqualTo("9"));
            Assert.That(values["processional.spaciousVerticalPitch"], Is.EqualTo("9"));
            Assert.That(values["processional.spaciousEnvelope"], Is.EqualTo("9x9"));
        }

        [Test]
        public void DensePitch_PacksProcessionalSentinelsAndRemainsHardValid()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["processional.denseAccepted"], Is.EqualTo("True"));
            Assert.That(values["processional.denseValid"], Is.EqualTo("True"));
            Assert.That(values["processional.denseCanonical"], Is.Not.EqualTo(SpaciousBaselineCanonical));
            Assert.That(values["processional.denseDeterministic"], Is.EqualTo("True"));
            Assert.That(values["processional.denseHorizontalPitch"], Is.EqualTo("9"));
            Assert.That(values["processional.denseVerticalPitch"], Is.EqualTo("8"));
            Assert.That(values["processional.denseEnvelope"], Is.EqualTo("9x9"));
            Assert.That(
                values["sentinels.profileValidPairs"],
                Is.EqualTo("3"),
                values["sentinels.results"]);
            Assert.That(int.Parse(values["sentinels.shortened"]), Is.GreaterThan(0));
            Assert.That(
                int.Parse(values["sentinels.denseExterior"]),
                Is.LessThan(int.Parse(values["sentinels.spaciousExterior"])));
        }

        [Test]
        public void AtriumAndTwinWing_KeepTheirExactSpatialConfigurationsAndPlans()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(
                values["profiles.atriumSpatial"],
                Is.EqualTo("7x9:r4:b0:5-5x7-7|5-5x5-6|4-5x5-5"));
            Assert.That(
                values["profiles.twinWingSpatial"],
                Is.EqualTo("1x1:r4:b0:5-5x7-7|5-5x5-6|4-5x5-5"));
            Assert.That(values["atrium.accepted"], Is.EqualTo("True"));
            Assert.That(values["atrium.canonicalSame"], Is.EqualTo("True"));
            Assert.That(values["atrium.measurementsSame"], Is.EqualTo("True"));
            Assert.That(values["twinWing.accepted"], Is.EqualTo("True"));
            Assert.That(values["twinWing.canonicalSame"], Is.EqualTo("True"));
            Assert.That(values["twinWing.measurementsSame"], Is.EqualTo("True"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildDensityAdjacencySlice4Snapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing density/adjacency Slice 4 diagnostic.");
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

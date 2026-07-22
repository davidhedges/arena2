#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabDensityAdjacencySlice3Tests
    {
        private const string RouteSourcePath =
            "Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonLabGenerator.RouteFirstPilot.cs";
        private const string SpaciousBaselineCanonical =
            "af4bce4800980db2d44ae2502600790a31cb0df287ed31100943f21baca5c4d9";
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void Profiles_ExposeThreeProcessionalRoleRangesThroughOneSettingsPath()
        {
            Dictionary<string, string> values = Snapshot.Value;
            string routeSource = File.ReadAllText(RouteSourcePath);

            Assert.That(values["profiles.valueCount"], Is.EqualTo("11"));
            Assert.That(values["profiles.spaciousTerminal"], Is.EqualTo("5-5x7-7"));
            Assert.That(values["profiles.spaciousHall"], Is.EqualTo("5-5x5-6"));
            Assert.That(values["profiles.spaciousConnector"], Is.EqualTo("4-5x5-5"));
            Assert.That(values["profiles.denseTerminal"], Is.EqualTo("7-7x7-7"));
            Assert.That(values["profiles.denseHall"], Is.EqualTo("7-7x7-7"));
            Assert.That(values["profiles.denseConnector"], Is.EqualTo("7-7x7-7"));
            Assert.That(routeSource, Does.Not.Contain("profileName"));
            Assert.That(routeSource, Does.Contain("ResolveGenericRoomDimensions("));
        }

        [Test]
        public void SpaciousProfile_PreservesTheExactProcessionalBaseline()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["processional.spaciousAccepted"], Is.EqualTo("True"));
            Assert.That(values["processional.spaciousValid"], Is.EqualTo("True"));
            Assert.That(values["processional.spaciousCanonical"], Is.EqualTo(SpaciousBaselineCanonical));
            Assert.That(values["processional.spaciousDeterministic"], Is.EqualTo("True"));
        }

        [Test]
        public void DenseProcessionalRooms_ShortenCorridorsAndRemainHardValid()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["processional.denseAccepted"], Is.EqualTo("True"));
            Assert.That(values["processional.denseValid"], Is.EqualTo("True"));
            Assert.That(values["processional.denseCanonical"], Is.Not.EqualTo(values["processional.spaciousCanonical"]));
            Assert.That(values["sentinels.profileValidPairs"], Is.EqualTo("3"));
            Assert.That(int.Parse(values["sentinels.shortened"]), Is.GreaterThan(0));
            Assert.That(
                int.Parse(values["sentinels.denseExterior"]),
                Is.LessThan(int.Parse(values["sentinels.spaciousExterior"])));
            Assert.That(values["processional.denseDeterministic"], Is.EqualTo("True"));
        }

        [Test]
        public void DenseSizeSettings_DoNotChangeAtriumOrTwinWingPlans()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["atrium.accepted"], Is.EqualTo("True"));
            Assert.That(values["atrium.canonicalSame"], Is.EqualTo("True"));
            Assert.That(values["atrium.measurementsSame"], Is.EqualTo("True"));
            Assert.That(values["twinWing.accepted"], Is.EqualTo("True"));
            Assert.That(values["twinWing.canonicalSame"], Is.EqualTo("True"));
            Assert.That(values["twinWing.measurementsSame"], Is.EqualTo("True"));
        }

        [Test]
        public void NonLevelEdges_CapOnlyTheirIncidentRoomAxisToTheSpaciousBaseline()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["cap.level"], Is.EqualTo("7x7"));
            Assert.That(values["cap.stairX"], Is.EqualTo("5x7"));
            Assert.That(values["cap.bridgeY"], Is.EqualTo("7x5"));
            Assert.That(values["cap.stairwellX"], Is.EqualTo("5x7"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildDensityAdjacencySlice3Snapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing density/adjacency Slice 3 diagnostic.");
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

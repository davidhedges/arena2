#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabTwinWingTests
    {
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void Selector_DrawsThisTopologyByWeightRatherThanBySeedResidue()
        {
            Dictionary<string, string> snapshot = TwinWingIntentSnapshot();

            Assert.That(snapshot["selector.weights"], Does.Contain("twin-wing-keep:1"));
            Assert.That(
                snapshot["selector.distribution"],
                Does.Not.Contain("twin-wing-keep:0"),
                snapshot["selector.distribution"]);
        }

        [Test]
        public void TwinWingIntent_ComposesTwoEqualCyclesAroundSharedJunctions()
        {
            Dictionary<string, string> snapshot = TwinWingIntentSnapshot();

            Assert.That(snapshot["graph.pattern"], Is.EqualTo("twin-wing-keep"));
            Assert.That(snapshot["graph.nodeCount"], Is.EqualTo("13"));
            Assert.That(snapshot["graph.edgeCount"], Is.EqualTo("14"));
            Assert.That(snapshot["graph.mainRouteCount"], Is.EqualTo("7"));
            Assert.That(snapshot["graph.branchNodeCount"], Is.EqualTo("6"));
            Assert.That(snapshot["graph.loopEdges"], Is.EqualTo("2"));
            Assert.That(snapshot["graph.cycleCoreNodes"], Is.EqualTo("10"));
            // Both wings fork off one hub and rejoin at one node, which is why
            // a single attach/rejoin pair never described this graph.
            Assert.That(snapshot["graph.junctions"], Is.EqualTo("wing-hub:4|wing-rejoin:4"));
            Assert.That(snapshot["route.valid"], Is.EqualTo("True"), snapshot["route.validationError"]);
        }

        [Test]
        public void TwinWingIntent_PreservesExactNodeEdgeAndTransitionOrder()
        {
            Dictionary<string, string> snapshot = TwinWingIntentSnapshot();

            Assert.That(snapshot["graph.nodeIds"], Is.EqualTo(
                "keep-arrival|keep-threshold|wing-hub|keep-crossing|keep-landmark|wing-rejoin|" +
                "keep-culmination|wing-a-entry|wing-overlook|wing-a-return|wing-b-entry|" +
                "wing-b-reward|wing-b-return"));
            Assert.That(snapshot["graph.edgeDetails"], Is.EqualTo(
                "A-B:keep-arrival>keep-threshold:LevelCorridor:0|" +
                "B-C:keep-threshold>wing-hub:LevelCorridor:0|" +
                "C-D:wing-hub>keep-crossing:LevelCorridor:0|" +
                "D-E:keep-crossing>keep-landmark:Stair:8|" +
                "E-F:keep-landmark>wing-rejoin:Stairwell:8|" +
                "F-G:wing-rejoin>keep-culmination:Stair:8|" +
                "C-H:wing-hub>wing-a-entry:Bridge:8|" +
                "H-I:wing-a-entry>wing-overlook:Stair:4|" +
                "I-J:wing-overlook>wing-a-return:Stair:4|" +
                "J-F:wing-a-return>wing-rejoin:LevelCorridor:0|" +
                "C-K:wing-hub>wing-b-entry:Stair:4|" +
                "K-L:wing-b-entry>wing-b-reward:Stair:4|" +
                "L-M:wing-b-reward>wing-b-return:Stair:4|" +
                "M-F:wing-b-return>wing-rejoin:Bridge:4"));
        }

        [Test]
        public void TwinWingEmbedding_AlignsTheDeclaredVistaInsideTheExpandedProfile()
        {
            Dictionary<string, string> intent = TwinWingIntentSnapshot();
            Dictionary<string, string> report = TwinWingProductionSnapshot();

            Assert.That(intent["embedding.succeeded"], Is.EqualTo("True"), intent["embedding.error"]);
            Assert.That(intent["profile.mapWidthMaxCells"], Is.EqualTo("52"));
            Assert.That(intent["profile.mapDepthMaxCells"], Is.EqualTo("52"));
            Assert.That(intent["vista.source"], Is.EqualTo("wing-overlook"));
            Assert.That(intent["vista.target"], Is.EqualTo("keep-landmark"));
            Assert.That(intent["vista.centerCardinallyAligned"], Is.EqualTo("True"));
            // One lattice step on the row axis, whose authored gap is 10 with
            // rubber-sheet headroom on top.
            Assert.That(int.Parse(intent["vista.centerDistanceCells"]), Is.GreaterThanOrEqualTo(10));
            Assert.That(report["vista.facingOpposed"], Is.EqualTo("true"), SnapshotText("BuildRouteCharacterizationSnapshot", TwinWingSeed()));
            Assert.That(report["vista.unobstructed"], Is.EqualTo("true"));
            Assert.That(report["vista.finalValid"], Is.EqualTo("true"));
            Assert.That(int.Parse(report["vista.finalReservedVoidCells"]), Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void ASelectedSeed_ProducesOneDeterministicHardValidTwinWingPlan()
        {
            int seed = TwinWingSeed();
            string firstText = SnapshotText("BuildRouteCharacterizationSnapshot", seed);
            string secondText = SnapshotText("BuildRouteCharacterizationSnapshot", seed);
            Dictionary<string, string> report = ParseSnapshot(firstText);

            Assert.That(report["accepted"], Is.EqualTo("true"), firstText);
            Assert.That(report["route.pattern"], Is.EqualTo("twin-wing-keep"));
            Assert.That(report["route.nodeCount"], Is.EqualTo("13"));
            Assert.That(report["route.mainRouteCount"], Is.EqualTo("7"));
            Assert.That(report["route.branchNodeCount"], Is.EqualTo("6"));
            Assert.That(report["route.loopEdges"], Is.EqualTo("2"));
            Assert.That(report["vertical.routeClimb"], Is.EqualTo("24"));
            Assert.That(report["vertical.requirementsSatisfied"], Is.EqualTo("true"));
            Assert.That(report["validation.recipes"], Is.EqualTo("true"));
            Assert.That(report["validation.passed"], Is.EqualTo("true"));
            Assert.That(report["hash.routeIntent"], Is.Not.Empty);
            Assert.That(firstText, Is.EqualTo(secondText));
        }

        [Test]
        public void ExistingRendererAndCollision_ConsumeTheTwinWingWithoutRepair()
        {
            string snapshotText = SnapshotText("BuildRendererProbeSnapshot", TwinWingSeed());
            Dictionary<string, string> report = ParseSnapshot(snapshotText);

            Assert.That(report["accepted"], Is.EqualTo("true"), snapshotText);
            Assert.That(report["boundary"], Is.EqualTo("true"), snapshotText);
            Assert.That(report["renderer.passed"], Is.EqualTo("true"), snapshotText);
            Assert.That(int.Parse(report["renderer.rejectedPlacements"]), Is.Zero);
            Assert.That(report["collision.passed"], Is.EqualTo("true"), snapshotText);
        }

        // A weighted draw means no seed is guaranteed to be a twin-wing seed,
        // so the snapshot reports the first one that is.
        private static int TwinWingSeed()
        {
            return int.Parse(TwinWingIntentSnapshot()["selector.firstSeed"]);
        }

        private static Dictionary<string, string> TwinWingIntentSnapshot()
        {
            return ParseSnapshot(SnapshotText("BuildTwinWingSnapshot", 2026072100));
        }

        private static Dictionary<string, string> TwinWingProductionSnapshot()
        {
            return ParseSnapshot(SnapshotText("BuildRouteCharacterizationSnapshot", TwinWingSeed()));
        }

        private static string SnapshotText(string methodName, int seed)
        {
            MethodInfo method = GeneratorType.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, $"Missing diagnostic method {methodName}.");
            return (string)method.Invoke(null, new object[] { seed })!;
        }

        private static Dictionary<string, string> ParseSnapshot(string snapshot)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in snapshot.Split('\n'))
            {
                int separator = line.IndexOf('=');
                if (separator < 0)
                    continue;

                result[line.Substring(0, separator)] = line.Substring(separator + 1);
            }

            return result;
        }
    }
}

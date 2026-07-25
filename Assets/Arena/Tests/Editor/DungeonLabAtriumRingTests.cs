#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabAtriumRingTests
    {
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void Selector_DrawsEveryTopologyByWeightRatherThanBySeedResidue()
        {
            Dictionary<string, string> snapshot = AtriumIntentSnapshot();

            Assert.That(snapshot["selector.weights"], Is.EqualTo(
                "atrium-ring:1|descent-shaft:1|processional-spine:1|ridge-ravine:1|" +
                "sunken-basin:1|terraced-cascade:1|twin-wing-keep:1"));
            // Every weighted topology has to actually appear over a 200-seed
            // window, or the draw is not doing what the weights say.
            foreach (string entry in snapshot["selector.distribution"].Split('|'))
            {
                Assert.That(int.Parse(entry.Split(':')[1]), Is.GreaterThan(0), entry);
            }

            Assert.That(snapshot["processional.cycleLength"], Is.EqualTo("10"));
        }

        [Test]
        public void AtriumIntent_ComposesTheDistinctEightNodeRingCycle()
        {
            Dictionary<string, string> snapshot = AtriumIntentSnapshot();

            Assert.That(snapshot["graph.pattern"], Is.EqualTo("atrium-ring"));
            Assert.That(snapshot["graph.nodeCount"], Is.EqualTo("13"));
            Assert.That(snapshot["graph.edgeCount"], Is.EqualTo("13"));
            Assert.That(snapshot["graph.loopEdges"], Is.EqualTo("1"));
            Assert.That(snapshot["graph.cycleLength"], Is.EqualTo("8"));
            Assert.That(snapshot["graph.junctions"], Is.EqualTo("ring-entry:3|ring-rejoin:3"));
            Assert.That(snapshot["route.valid"], Is.EqualTo("True"), snapshot["route.validationError"]);
        }

        [Test]
        public void AtriumIntent_PreservesExactNodeEdgeAndTransitionOrder()
        {
            Dictionary<string, string> snapshot = AtriumIntentSnapshot();

            Assert.That(snapshot["graph.nodeIds"], Is.EqualTo(
                "atrium-arrival|atrium-threshold|outer-approach|ring-entry|atrium-landmark|ring-ascent|" +
                "ring-rejoin|upper-approach|atrium-culmination|lower-ring-gallery|ring-overlook|" +
                "far-ring-gallery|upper-ring-gallery"));
            Assert.That(snapshot["graph.edgeDetails"], Is.EqualTo(
                "A-B:atrium-arrival>atrium-threshold:LevelCorridor:0|" +
                "B-C:atrium-threshold>outer-approach:Stair:4|" +
                "C-D:outer-approach>ring-entry:LevelCorridor:0|" +
                "D-E:ring-entry>atrium-landmark:LevelCorridor:0|" +
                "E-F:atrium-landmark>ring-ascent:Stair:4|" +
                "F-G:ring-ascent>ring-rejoin:Stairwell:8|" +
                "G-H:ring-rejoin>upper-approach:Stair:4|" +
                "H-I:upper-approach>atrium-culmination:Stair:4|" +
                "D-J:ring-entry>lower-ring-gallery:Bridge:4|" +
                "J-K:lower-ring-gallery>ring-overlook:LevelCorridor:0|" +
                "K-L:ring-overlook>far-ring-gallery:Stair:4|" +
                "L-M:far-ring-gallery>upper-ring-gallery:Stair:4|" +
                "M-G:upper-ring-gallery>ring-rejoin:LevelCorridor:0"));
        }

        [Test]
        public void AtriumEmbedding_AlignsTheDeclaredVistaAcrossTheCentralVoid()
        {
            Dictionary<string, string> intent = AtriumIntentSnapshot();
            Dictionary<string, string> report = AtriumProductionSnapshot();

            Assert.That(intent["embedding.succeeded"], Is.EqualTo("True"), intent["embedding.error"]);
            Assert.That(intent["vista.source"], Is.EqualTo("ring-overlook"));
            Assert.That(intent["vista.target"], Is.EqualTo("atrium-landmark"));
            Assert.That(intent["vista.centerCardinallyAligned"], Is.EqualTo("True"));
            // Two lattice steps at a minimum 9-cell lane gap, plus whatever the
            // rubber sheet spent on those two lanes.
            Assert.That(int.Parse(intent["vista.centerDistanceCells"]), Is.GreaterThanOrEqualTo(18));
            Assert.That(report["vista.facingOpposed"], Is.EqualTo("true"), SnapshotText("BuildRouteCharacterizationSnapshot", AtriumSeed()));
            Assert.That(report["vista.unobstructed"], Is.EqualTo("true"));
            Assert.That(report["vista.finalValid"], Is.EqualTo("true"));
            Assert.That(int.Parse(report["vista.finalReservedVoidCells"]), Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void ASelectedSeed_ProducesOneDeterministicHardValidAtriumPlan()
        {
            int seed = AtriumSeed();
            string firstText = SnapshotText("BuildRouteCharacterizationSnapshot", seed);
            string secondText = SnapshotText("BuildRouteCharacterizationSnapshot", seed);
            Dictionary<string, string> report = ParseSnapshot(firstText);

            Assert.That(report["accepted"], Is.EqualTo("true"), firstText);
            Assert.That(report["route.pattern"], Is.EqualTo("atrium-ring"));
            Assert.That(report["route.nodeCount"], Is.EqualTo("13"));
            Assert.That(report["route.mainRouteCount"], Is.EqualTo("9"));
            Assert.That(report["route.branchNodeCount"], Is.EqualTo("4"));
            Assert.That(report["route.loopEdges"], Is.EqualTo("1"));
            Assert.That(report["vertical.routeClimb"], Is.EqualTo("24"));
            Assert.That(report["vertical.requirementsSatisfied"], Is.EqualTo("true"));
            Assert.That(report["validation.recipes"], Is.EqualTo("true"));
            Assert.That(report["validation.passed"], Is.EqualTo("true"));
            Assert.That(report["hash.routeIntent"], Is.Not.Empty);
            Assert.That(firstText, Is.EqualTo(secondText));
        }

        [Test]
        public void ExistingRendererAndCollision_ConsumeTheAtriumWithoutRepair()
        {
            string snapshotText = SnapshotText("BuildRendererProbeSnapshot", AtriumSeed());
            Dictionary<string, string> report = ParseSnapshot(snapshotText);

            Assert.That(report["accepted"], Is.EqualTo("true"), snapshotText);
            Assert.That(report["boundary"], Is.EqualTo("true"), snapshotText);
            Assert.That(report["renderer.passed"], Is.EqualTo("true"), snapshotText);
            Assert.That(int.Parse(report["renderer.rejectedPlacements"]), Is.Zero);
            Assert.That(report["collision.passed"], Is.EqualTo("true"), snapshotText);
            Assert.That(int.Parse(report["collision.missingMeshes"]), Is.Zero);
        }

        // A weighted draw means no seed is guaranteed to be an atrium seed, so
        // the snapshot reports the first one that is.
        private static int AtriumSeed()
        {
            return int.Parse(AtriumIntentSnapshot()["selector.firstSeed"]);
        }

        private static Dictionary<string, string> AtriumIntentSnapshot()
        {
            return ParseSnapshot(SnapshotText("BuildAtriumRingSnapshot", 2026072100));
        }

        private static Dictionary<string, string> AtriumProductionSnapshot()
        {
            return ParseSnapshot(SnapshotText("BuildRouteCharacterizationSnapshot", AtriumSeed()));
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

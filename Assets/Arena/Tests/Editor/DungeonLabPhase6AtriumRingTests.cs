#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase6AtriumRingTests
    {
        private const int AtriumSeed = 2026072101;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void Selector_UsesStableParityWithoutReplacingTheProcessionalVersion()
        {
            Dictionary<string, string> snapshot = AtriumIntentSnapshot();

            Assert.That(snapshot["selector.evenPattern"], Is.EqualTo("processional-spine"));
            Assert.That(snapshot["selector.oddPattern"], Is.EqualTo("atrium-ring"));
            Assert.That(snapshot["processional.plannerVersion"], Is.EqualTo("processional-spine-v5"));
            Assert.That(snapshot["processional.cycleLength"], Is.EqualTo("10"));
        }

        [Test]
        public void AtriumIntent_ComposesTheDistinctEightNodeRingCycle()
        {
            Dictionary<string, string> snapshot = AtriumIntentSnapshot();

            Assert.That(snapshot["graph.pattern"], Is.EqualTo("atrium-ring"));
            Assert.That(snapshot["graph.plannerVersion"], Is.EqualTo("atrium-ring-v2"));
            Assert.That(snapshot["graph.nodeCount"], Is.EqualTo("13"));
            Assert.That(snapshot["graph.edgeCount"], Is.EqualTo("13"));
            Assert.That(snapshot["graph.loopEdges"], Is.EqualTo("1"));
            Assert.That(snapshot["graph.cycleLength"], Is.EqualTo("8"));
            Assert.That(snapshot["graph.branchAttach"], Is.EqualTo("ring-entry"));
            Assert.That(snapshot["graph.branchRejoin"], Is.EqualTo("ring-rejoin"));
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
                "main-0-1:atrium-arrival>atrium-threshold:LevelCorridor:0|" +
                "main-1-2:atrium-threshold>outer-approach:Stair:4|" +
                "main-2-3:outer-approach>ring-entry:LevelCorridor:0|" +
                "main-3-4:ring-entry>atrium-landmark:LevelCorridor:0|" +
                "main-4-5:atrium-landmark>ring-ascent:Stair:4|" +
                "main-5-6:ring-ascent>ring-rejoin:Stairwell:8|" +
                "main-6-7:ring-rejoin>upper-approach:Stair:4|" +
                "main-7-8:upper-approach>atrium-culmination:Stair:4|" +
                "branch-3-9:ring-entry>lower-ring-gallery:Bridge:4|" +
                "branch-9-10:lower-ring-gallery>ring-overlook:LevelCorridor:0|" +
                "branch-10-11:ring-overlook>far-ring-gallery:Stair:4|" +
                "branch-11-12:far-ring-gallery>upper-ring-gallery:Stair:4|" +
                "rejoin-12-6:upper-ring-gallery>ring-rejoin:LevelCorridor:0"));
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
            Assert.That(intent["vista.centerDistanceCells"], Is.EqualTo("14"));
            Assert.That(report["vista.facingOpposed"], Is.EqualTo("true"), SnapshotText("BuildRouteCharacterizationSnapshot", AtriumSeed));
            Assert.That(report["vista.unobstructed"], Is.EqualTo("true"));
            Assert.That(report["vista.finalValid"], Is.EqualTo("true"));
            Assert.That(int.Parse(report["vista.finalReservedVoidCells"]), Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void OddSeed_ProducesOneDeterministicHardValidAtriumPlan()
        {
            string firstText = SnapshotText("BuildRouteCharacterizationSnapshot", AtriumSeed);
            string secondText = SnapshotText("BuildRouteCharacterizationSnapshot", AtriumSeed);
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
            string snapshotText = SnapshotText("BuildRendererProbeSnapshot", AtriumSeed);
            Dictionary<string, string> report = ParseSnapshot(snapshotText);

            Assert.That(report["accepted"], Is.EqualTo("true"), snapshotText);
            Assert.That(report["boundary"], Is.EqualTo("true"), snapshotText);
            Assert.That(report["renderer.passed"], Is.EqualTo("true"), snapshotText);
            Assert.That(int.Parse(report["renderer.rejectedPlacements"]), Is.Zero);
            Assert.That(report["collision.passed"], Is.EqualTo("true"), snapshotText);
            Assert.That(int.Parse(report["collision.missingMeshes"]), Is.Zero);
        }

        private static Dictionary<string, string> AtriumIntentSnapshot()
        {
            return ParseSnapshot(SnapshotText("BuildPhase6bAtriumRingSnapshot", AtriumSeed));
        }

        private static Dictionary<string, string> AtriumProductionSnapshot()
        {
            return ParseSnapshot(SnapshotText("BuildRouteCharacterizationSnapshot", AtriumSeed));
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

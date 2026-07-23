#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase6TwinWingTests
    {
        private const int TwinWingSeed = 2026072103;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void Selector_UsesStableModuloFourAndPreservesExistingPlannerVersions()
        {
            Dictionary<string, string> snapshot = TwinWingIntentSnapshot();

            Assert.That(snapshot["selector.residue0Pattern"], Is.EqualTo("processional-spine"));
            Assert.That(snapshot["selector.residue1Pattern"], Is.EqualTo("atrium-ring"));
            Assert.That(snapshot["selector.residue2Pattern"], Is.EqualTo("processional-spine"));
            Assert.That(snapshot["selector.residue3Pattern"], Is.EqualTo("twin-wing-keep"));
            Assert.That(snapshot["processional.plannerVersion"], Is.EqualTo("processional-spine-v6"));
            Assert.That(snapshot["atrium.plannerVersion"], Is.EqualTo("atrium-ring-v3"));
        }

        [Test]
        public void TwinWingIntent_ComposesTwoEqualCyclesAroundSharedJunctions()
        {
            Dictionary<string, string> snapshot = TwinWingIntentSnapshot();

            Assert.That(snapshot["graph.pattern"], Is.EqualTo("twin-wing-keep"));
            Assert.That(snapshot["graph.plannerVersion"], Is.EqualTo("twin-wing-keep-v3"));
            Assert.That(snapshot["graph.nodeCount"], Is.EqualTo("13"));
            Assert.That(snapshot["graph.edgeCount"], Is.EqualTo("14"));
            Assert.That(snapshot["graph.mainRouteCount"], Is.EqualTo("7"));
            Assert.That(snapshot["graph.branchNodeCount"], Is.EqualTo("6"));
            Assert.That(snapshot["graph.loopEdges"], Is.EqualTo("2"));
            Assert.That(snapshot["graph.cycleCoreNodes"], Is.EqualTo("10"));
            Assert.That(snapshot["graph.branchAttach"], Is.EqualTo("wing-hub"));
            Assert.That(snapshot["graph.branchAttachDegree"], Is.EqualTo("4"));
            Assert.That(snapshot["graph.branchRejoin"], Is.EqualTo("wing-rejoin"));
            Assert.That(snapshot["graph.branchRejoinDegree"], Is.EqualTo("4"));
            Assert.That(snapshot["graph.wingPathLengths"], Is.EqualTo("4|4"));
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
                "main-0-1:keep-arrival>keep-threshold:LevelCorridor:0|" +
                "main-1-2:keep-threshold>wing-hub:LevelCorridor:0|" +
                "main-2-3:wing-hub>keep-crossing:LevelCorridor:0|" +
                "main-3-4:keep-crossing>keep-landmark:Stair:8|" +
                "main-4-5:keep-landmark>wing-rejoin:Stairwell:8|" +
                "main-5-6:wing-rejoin>keep-culmination:Stair:8|" +
                "wing-a-2-7:wing-hub>wing-a-entry:Bridge:8|" +
                "wing-a-7-8:wing-a-entry>wing-overlook:Stair:4|" +
                "wing-a-8-9:wing-overlook>wing-a-return:Stair:4|" +
                "wing-a-rejoin-9-5:wing-a-return>wing-rejoin:LevelCorridor:0|" +
                "wing-b-2-10:wing-hub>wing-b-entry:Stair:4|" +
                "wing-b-10-11:wing-b-entry>wing-b-reward:Stair:4|" +
                "wing-b-11-12:wing-b-reward>wing-b-return:Stair:4|" +
                "wing-b-rejoin-12-5:wing-b-return>wing-rejoin:Bridge:4"));
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
            Assert.That(intent["vista.centerDistanceCells"], Is.EqualTo("10"));
            Assert.That(report["vista.facingOpposed"], Is.EqualTo("true"), SnapshotText("BuildRouteCharacterizationSnapshot", TwinWingSeed));
            Assert.That(report["vista.unobstructed"], Is.EqualTo("true"));
            Assert.That(report["vista.finalValid"], Is.EqualTo("true"));
            Assert.That(int.Parse(report["vista.finalReservedVoidCells"]), Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void ResidueThreeSeed_ProducesOneDeterministicHardValidTwinWingPlan()
        {
            string firstText = SnapshotText("BuildRouteCharacterizationSnapshot", TwinWingSeed);
            string secondText = SnapshotText("BuildRouteCharacterizationSnapshot", TwinWingSeed);
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
            string snapshotText = SnapshotText("BuildRendererProbeSnapshot", TwinWingSeed);
            Dictionary<string, string> report = ParseSnapshot(snapshotText);

            Assert.That(report["accepted"], Is.EqualTo("true"), snapshotText);
            Assert.That(report["boundary"], Is.EqualTo("true"), snapshotText);
            Assert.That(report["renderer.passed"], Is.EqualTo("true"), snapshotText);
            Assert.That(int.Parse(report["renderer.rejectedPlacements"]), Is.Zero);
            Assert.That(report["collision.passed"], Is.EqualTo("true"), snapshotText);
            Assert.That(int.Parse(report["collision.missingMeshes"]), Is.Zero);
        }

        private static Dictionary<string, string> TwinWingIntentSnapshot()
        {
            return ParseSnapshot(SnapshotText("BuildPhase6cTwinWingSnapshot", TwinWingSeed));
        }

        private static Dictionary<string, string> TwinWingProductionSnapshot()
        {
            return ParseSnapshot(SnapshotText("BuildRouteCharacterizationSnapshot", TwinWingSeed));
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

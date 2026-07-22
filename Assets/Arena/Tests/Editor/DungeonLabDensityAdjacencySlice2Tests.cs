#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabDensityAdjacencySlice2Tests
    {
        private const string PlanPath = "docs/dungeon-builder/DENSITY_ADJACENCY_PLAN.md";
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void ExistingTwinWingPlan_AlreadyContainsBoundaryBackedSharedWallDoors()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["twin.accepted"], Is.EqualTo("True"));
            Assert.That(values["twin.topology"], Is.EqualTo("twin-wing-keep"));
            Assert.That(values["twin.validation"], Is.EqualTo("True"));
            Assert.That(int.Parse(values["twin.zeroExteriorConnections"]), Is.GreaterThan(0));
            Assert.That(
                values["twin.zeroExteriorDoorways"],
                Is.EqualTo(values["twin.zeroExteriorConnections"]));
        }

        [Test]
        public void TouchingRooms_CompileToOneDoorwayWithoutExteriorCorridorCells()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["touching.connected"], Is.EqualTo("True"), values["touching.rejection"]);
            Assert.That(int.Parse(values["touching.pathCells"]), Is.GreaterThan(1));
            Assert.That(values["touching.exteriorCells"], Is.EqualTo("0"));
            Assert.That(values["touching.boundaryBuilt"], Is.EqualTo("True"));
            Assert.That(values["touching.doorways"], Is.EqualTo("1"));
            Assert.That(values["touching.doorwayJoinsRooms"], Is.EqualTo("True"));
        }

        [Test]
        public void ExistingRendererAndCollisionInputs_AcceptTheTraversableSameLevelSeam()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["touching.rendererRejected"], Is.EqualTo("0"));
            Assert.That(values["touching.rendererDoorways"], Is.EqualTo("1"));
            Assert.That(int.Parse(values["touching.collisionSources"]), Is.GreaterThan(0));
            Assert.That(values["touching.collisionMissingMeshes"], Is.EqualTo("0"));
        }

        [Test]
        public void BiasedJunction_CurrentFootprintCenterFailsAtCardinalAlignmentFirst()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["junction.logicalAnchorInsideBiasedRoom"], Is.EqualTo("True"));
            Assert.That(values["junction.footprintCenter"], Is.EqualTo("1,0"));
            Assert.That(values["junction.connected"], Is.EqualTo("False"));
            Assert.That(values["junction.connectionsBeforeFailure"], Is.EqualTo("0"));
            Assert.That(values["junction.rejection"], Does.Contain("endpoints were not cardinally aligned"));
        }

        [Test]
        public void PlanLocksOneStableEmbeddedNodeAnchorModelAndItsJunctionInvariants()
        {
            string plan = File.ReadAllText(PlanPath);
            string normalizedPlan = string.Join(
                " ",
                plan.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));

            Assert.That(normalizedPlan, Does.Contain("Anchor model decision: stable embedded-node anchors"));
            Assert.That(normalizedPlan, Does.Contain("one immutable logical anchor per embedded route node"));
            Assert.That(normalizedPlan, Does.Contain("Doorway thresholds are derived"));
            Assert.That(normalizedPlan, Does.Contain("Junction edges derive their thresholds independently"));
            Assert.That(normalizedPlan, Does.Not.Contain("Anchor model decision: per-edge threshold anchors"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildDensityAdjacencySlice2Snapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing density/adjacency Slice 2 diagnostic.");
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

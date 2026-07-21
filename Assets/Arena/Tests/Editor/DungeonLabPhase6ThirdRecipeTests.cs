#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase6ThirdRecipeTests
    {
        private const int Seed = 2026072100;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void ReviewedCatalog_ContainsTheCurrentCornerReturnContract()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["catalog.valid"], Is.EqualTo("True"));
            Assert.That(values["catalog.reviewedCount"], Is.EqualTo("3"));
            Assert.That(values["catalog.digest"], Has.Length.EqualTo(64));
            Assert.That(values["recipe.id"], Is.EqualTo("connector_corner_return_01"));
            Assert.That(values["recipe.schema"], Is.EqualTo("1"));
            Assert.That(values["recipe.lifecycle"], Is.EqualTo("Reviewed"));
            Assert.That(values["recipe.reviewCurrent"], Is.EqualTo("True"));
            Assert.That(values["recipe.role"], Is.EqualTo("connector"));
            Assert.That(values["recipe.beat"], Is.EqualTo("return"));
        }

        [Test]
        public void CornerReturn_UsesTheLockedGeometryAndExistingStairMotif()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["recipe.zoneCount"], Is.EqualTo("4"));
            Assert.That(values["recipe.portCount"], Is.EqualTo("2"));
            Assert.That(values["recipe.transitionCount"], Is.EqualTo("1"));
            Assert.That(values["recipe.walkableCells"], Is.EqualTo("25"));
            Assert.That(values["recipe.elevatedCells"], Is.EqualTo("6"));
            Assert.That(values["recipe.protectedCells"], Is.EqualTo("5"));
            Assert.That(values["recipe.localPortsPerpendicular"], Is.EqualTo("True"));
            Assert.That(values["recipe.transitionImplementation"], Is.EqualTo("seam-rise-1"));
            Assert.That(values["recipe.transitionRise"], Is.EqualTo("1"));
            Assert.That(values["recipe.transitionLanes"], Is.EqualTo("1"));
            Assert.That(values["recipe.transitionHeadroom"], Is.EqualTo("3"));
            Assert.That(values["recipe.contract"], Is.EqualTo("True"));
            Assert.That(values["recipe.schemaValid"], Is.EqualTo("True"));
            Assert.That(values["recipe.structureValid"], Is.EqualTo("True"));
            Assert.That(values["recipe.variationValid"], Is.EqualTo("True"));
            Assert.That(values["recipe.neighborValid"], Is.EqualTo("True"));
        }

        [Test]
        public void AuthoringWorkflow_CoversRotationsMirrorsNeighborsAndAllConsumers()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["gallery.firstPassed"], Is.EqualTo("True"), values["gallery.message"]);
            Assert.That(values["gallery.secondPassed"], Is.EqualTo("True"), values["gallery.secondMessage"]);
            Assert.That(values["gallery.samePath"], Is.EqualTo("True"));
            Assert.That(values["gallery.sameHash"], Is.EqualTo("True"));
            Assert.That(values["gallery.entryCount"], Is.EqualTo("34"));
            Assert.That(values["gallery.requiredViews"], Is.EqualTo("True"));
            Assert.That(values["gallery.mirrorStateCount"], Is.EqualTo("2"));
            Assert.That(values["gallery.fullDungeon"], Is.EqualTo("True"));
            Assert.That(values["gallery.renderer"], Is.EqualTo("True"));
            Assert.That(values["gallery.abyss"], Is.EqualTo("True"));
            Assert.That(values["gallery.collision"], Is.EqualTo("True"));
        }

        [TestCase("processional", "processional-spine-v5", "branch-11-12", "rejoin-12-7")]
        [TestCase("atrium", "atrium-ring-v2", "branch-11-12", "rejoin-12-6")]
        [TestCase("twinWing", "twin-wing-keep-v2", "wing-b-11-12", "wing-b-rejoin-12-5")]
        public void EveryTopology_BindsAndResolvesTheSharedCornerReturn(
            string prefix,
            string plannerVersion,
            string entryEdge,
            string exitEdge)
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values[$"{prefix}.accepted"], Is.EqualTo("True"));
            Assert.That(values[$"{prefix}.validation"], Is.EqualTo("True"));
            Assert.That(values[$"{prefix}.plannerVersion"], Is.EqualTo(plannerVersion));
            Assert.That(values[$"{prefix}.recipeCount"], Is.EqualTo("3"));
            Assert.That(values[$"{prefix}.slotNode"], Is.EqualTo("12"));
            Assert.That(values[$"{prefix}.nodeRole"], Is.EqualTo("connector"));
            Assert.That(values[$"{prefix}.nodeBeat"], Is.EqualTo("return"));
            Assert.That(values[$"{prefix}.orientation"], Is.EqualTo("RouteForward"));
            Assert.That(values[$"{prefix}.entryEdge"], Is.EqualTo(entryEdge));
            Assert.That(values[$"{prefix}.exitEdge"], Is.EqualTo(exitEdge));
            Assert.That(values[$"{prefix}.atomic"], Is.EqualTo("True"));
            Assert.That(values[$"{prefix}.roomIndex"], Is.EqualTo("12"));
            Assert.That(values[$"{prefix}.transitionCount"], Is.EqualTo("1"));
            Assert.That(int.Parse(values[$"{prefix}.protectedCount"]), Is.GreaterThan(0));
            Assert.That(values[$"{prefix}.portsPerpendicular"], Is.EqualTo("True"));
            Assert.That(values[$"{prefix}.axisMatchesExit"], Is.EqualTo("True"));
        }

        [Test]
        public void ExitEdgeOrientation_IsCardinalAndRejectsInvalidIdentity()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["axis.validResolved"], Is.EqualTo("True"));
            Assert.That(values["axis.validCardinal"], Is.EqualTo("True"));
            Assert.That(values["axis.missingExitRejected"], Is.EqualTo("True"));
            Assert.That(values["axis.unrelatedExitRejected"], Is.EqualTo("True"));
        }

        [Test]
        public void VersionsAdvanceWithoutChangingSchemaOrSpatialRandomness()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["versions.summary"], Is.EqualTo("dungeon-plan-v8"));
            Assert.That(values["versions.generator"], Is.EqualTo("route-topologies-v8"));
            Assert.That(values["versions.spatialRandom"], Is.EqualTo("processional-spine-v1"));
            Assert.That(values["recipe.schema"], Is.EqualTo("1"));
            Assert.That(values["lifecycle.staleDetected"], Is.EqualTo("True"));
            Assert.That(values["lifecycle.staleExcluded"], Is.EqualTo("True"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildPhase6fCornerReturnSnapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing Phase 6f third-recipe diagnostic.");
            return Parse((string)method.Invoke(null, new object[] { Seed })!);
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

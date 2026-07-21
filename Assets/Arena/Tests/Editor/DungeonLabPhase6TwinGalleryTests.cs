#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase6TwinGalleryTests
    {
        private const int Seed = 2026072100;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void ReviewedCatalog_ContainsTheCurrentTwinGalleryConnector()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["catalog.valid"], Is.EqualTo("True"));
            Assert.That(values["catalog.reviewedCount"], Is.EqualTo("4"));
            Assert.That(values["catalog.digest"], Has.Length.EqualTo(64));
            Assert.That(values["recipe.id"], Is.EqualTo("connector_twin_gallery_01"));
            Assert.That(values["recipe.kind"], Is.EqualTo("Connector"));
            Assert.That(values["recipe.schema"], Is.EqualTo("1"));
            Assert.That(values["recipe.lifecycle"], Is.EqualTo("Reviewed"));
            Assert.That(values["recipe.reviewCurrent"], Is.EqualTo("True"));
            Assert.That(values["recipe.role"], Is.EqualTo("connector"));
            Assert.That(values["recipe.beat"], Is.EqualTo("branch"));
        }

        [Test]
        public void TwinGallery_UsesTheLockedClearLaneAndSymmetricRiseGeometry()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["recipe.zoneCount"], Is.EqualTo("4"));
            Assert.That(values["recipe.portCount"], Is.EqualTo("2"));
            Assert.That(values["recipe.walkableCells"], Is.EqualTo("49"));
            Assert.That(values["recipe.elevatedCells"], Is.EqualTo("20"));
            Assert.That(values["recipe.circulationCells"], Is.EqualTo("7"));
            Assert.That(values["recipe.protectedCells"], Is.EqualTo("7"));
            Assert.That(values["recipe.portsOpposed"], Is.EqualTo("True"));
            Assert.That(values["recipe.allowMirror"], Is.EqualTo("True"));
            Assert.That(values["recipe.transitionCount"], Is.EqualTo("2"));
            Assert.That(values["recipe.transitionsComplete"], Is.EqualTo("True"));
            Assert.That(values["recipe.motifCount"], Is.EqualTo("1"));
            Assert.That(values["recipe.variationCount"], Is.EqualTo("0"));
            Assert.That(values["recipe.symmetryCount"], Is.EqualTo("1"));
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

        [Test]
        public void ProcessionalBranchPassage_BindsAndResolvesTheTwinGallery()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["processional.accepted"], Is.EqualTo("True"));
            Assert.That(values["processional.validation"], Is.EqualTo("True"));
            Assert.That(values["processional.recipeCount"], Is.EqualTo("4"));
            Assert.That(values["processional.slotNode"], Is.EqualTo("10"));
            Assert.That(values["processional.nodeRole"], Is.EqualTo("connector"));
            Assert.That(values["processional.nodeBeat"], Is.EqualTo("branch"));
            Assert.That(values["processional.orientation"], Is.EqualTo("RouteForward"));
            Assert.That(values["processional.entryEdge"], Is.EqualTo("branch-9-10"));
            Assert.That(values["processional.exitEdge"], Is.EqualTo("branch-10-11"));
            Assert.That(values["processional.atomic"], Is.EqualTo("True"));
            Assert.That(values["processional.roomIndex"], Is.EqualTo("10"));
            Assert.That(values["processional.transitionCount"], Is.EqualTo("2"));
            Assert.That(values["processional.protectedCount"], Is.EqualTo("7"));
            Assert.That(values["processional.axisMatchesExit"], Is.EqualTo("True"));
            Assert.That(values["processional.entryOpposesExit"], Is.EqualTo("True"));
            Assert.That(values["processional.finalVista"], Is.EqualTo("True"));
            Assert.That(int.Parse(values["processional.reservedVoid"]), Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void StructurallyDifferentPatterns_RetainTheirThreeRecipeSets()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["atrium.recipeCount"], Is.EqualTo("3"));
            Assert.That(values["atrium.galleryAbsent"], Is.EqualTo("True"));
            Assert.That(values["twin.recipeCount"], Is.EqualTo("3"));
            Assert.That(values["twin.galleryAbsent"], Is.EqualTo("True"));
        }

        [Test]
        public void RouteForwardAxisResolver_UsesBothDeclaredEndpoints()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["axis.routeResolved"], Is.EqualTo("True"));
            Assert.That(values["axis.routeCardinal"], Is.EqualTo("True"));
            Assert.That(values["axis.missingExitRejected"], Is.EqualTo("True"));
        }

        [Test]
        public void VersionsAdvanceWithoutChangingOtherPatternVersionsOrSpatialRandomness()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["versions.summary"], Is.EqualTo("dungeon-plan-v9"));
            Assert.That(values["versions.generator"], Is.EqualTo("route-topologies-v9"));
            Assert.That(values["versions.processional"], Is.EqualTo("processional-spine-v6"));
            Assert.That(values["versions.atrium"], Is.EqualTo("atrium-ring-v2"));
            Assert.That(values["versions.twin"], Is.EqualTo("twin-wing-keep-v2"));
            Assert.That(values["versions.spatialRandom"], Is.EqualTo("processional-spine-v1"));
            Assert.That(values["lifecycle.staleDetected"], Is.EqualTo("True"));
            Assert.That(values["lifecycle.staleExcluded"], Is.EqualTo("True"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildPhase6gTwinGallerySnapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing Phase 6g twin-gallery diagnostic.");
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

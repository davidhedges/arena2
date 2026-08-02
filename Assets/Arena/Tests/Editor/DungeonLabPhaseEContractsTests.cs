#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhaseEContractsTests
    {
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void PhaseE_CeilingAbyssAndVoidContractsStayExplicit()
        {
            Dictionary<string, string> snapshot = InvokeSnapshot("BuildPhaseEContractSnapshot");

            Assert.That(snapshot["ceiling.default"], Is.EqualTo("24"));
            Assert.That(snapshot["ceiling.globalCap"], Is.EqualTo("40"));
            Assert.That(snapshot["abyss.depth"], Is.EqualTo("20"));
            Assert.That(snapshot["abyss.baseAtMin0"], Is.EqualTo("-20"));
            Assert.That(snapshot["abyss.baseAtMin12"], Is.EqualTo("-8"));
            Assert.That(snapshot["opening.apertureIsZero"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.voidProducerCarriesKind"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.voidSchemaAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.clearFallColumnAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.obstructedFallColumnRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.obstructedFailureCode"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.maxSurvivableFall"], Is.EqualTo("8"));
            Assert.That(snapshot["opening.apertureNoCatchRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureNoCatchFailureCode"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureLegalAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureShallowRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureShallowFailureCode"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureDeepRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureDeepFailureCode"], Is.EqualTo("True"));
        }

        [Test]
        public void PhaseE_NavigationEdgesCarryPhysicalWitnesses()
        {
            Dictionary<string, string> snapshot =
                InvokeSnapshot("BuildNavigationSurfaceContractSnapshot");

            Assert.That(snapshot["graph.valid"], Is.EqualTo("True"), snapshot["graph.failure"]);
            Assert.That(snapshot["graph.nodeCount"], Is.EqualTo("3"));
            Assert.That(snapshot["graph.edgeCount"], Is.EqualTo("2"));
            Assert.That(snapshot["graph.unwitnessedTransitionRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["graph.wrongTransitionWitnessRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["graph.witnessedTransitionEdges"], Is.EqualTo("1"));
            Assert.That(snapshot["graph.sealedPartitionRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["graph.doorwayAccepted"], Is.EqualTo("True"));
            Assert.That(
                snapshot["graph.isolatedFallPruned"],
                Is.EqualTo("True"),
                snapshot["graph.isolatedFallFailure"]);
            Assert.That(snapshot["collision.triangleEdgeAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.outsideTriangleRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.exactHeightAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.captureWindowDriftRejected"], Is.EqualTo("True"));
        }

        [Test]
        public void PhaseE_NavigationCollisionSamplerCachesAndSpatiallyPrunes()
        {
            Dictionary<string, string> snapshot =
                InvokeSnapshot("BuildNavigationCollisionSamplerContractSnapshot");

            Assert.That(snapshot["collision.layerPresent"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.boxAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.boxHeightExact"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.triangleAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.triangleHeightExact"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.outsideTriangleRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.captureWindowDriftRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["cache.boxPrimitives"], Is.EqualTo("2"));
            Assert.That(snapshot["cache.trianglePrimitives"], Is.EqualTo("2"));
            Assert.That(snapshot["cache.meshBufferReads"], Is.EqualTo("1"));
            Assert.That(snapshot["cache.repeatedQueriesAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["cache.queryCount"], Is.EqualTo("128"));
            Assert.That(snapshot["cache.boxCandidateChecks"], Is.EqualTo("0"));
            Assert.That(snapshot["cache.triangleCandidateChecks"], Is.EqualTo("128"));
            Assert.That(snapshot["cache.spatiallyPruned"], Is.EqualTo("True"));
        }

        [Test]
        public void Slice2_OpeningsAndOpenVolumesHavePlanLevelOwnership()
        {
            Dictionary<string, string> snapshot =
                InvokeSnapshot("BuildSlice2OwnershipContractSnapshot");

            Assert.That(snapshot["opening.singlePlanListCount"], Is.EqualTo("3"));
            Assert.That(snapshot["opening.generatedAndRecipeAccepted"], Is.EqualTo("True"), snapshot["opening.combinedFailure"]);
            Assert.That(snapshot["opening.generatedPassagesColumnScoped"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.recipeApertureSurfaceScoped"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.recipeReportEquivalentFromPlanList"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.fallNavigationUsesPlanList"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.generatedSurfacePassagePreserved"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.duplicateRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.duplicateFailureCode"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.missingRimRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.missingRimFailureCode"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.recipeResolutionStorageRemoved"], Is.EqualTo("True"));

            Assert.That(snapshot["volume.generatedOwnerGroupCount"], Is.EqualTo("1"));
            Assert.That(int.Parse(snapshot["volume.unprotectedAtriumPartitions"]), Is.GreaterThan(1));
            Assert.That(snapshot["volume.protectedAtriumPartitions"], Is.EqualTo("1"));
            Assert.That(snapshot["volume.chamberSubdivisionContinues"], Is.EqualTo("True"));
            Assert.That(snapshot["volume.generatedAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["volume.penetrationRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["volume.penetrationFailureCode"], Is.EqualTo("True"));
        }

        [Test]
        public void Slice3_NoRecipeGenericRoomRealizesNavigableStructuralStorey()
        {
            Dictionary<string, string> snapshot =
                InvokeSnapshot("BuildSlice3GenericStructuralLayerSnapshot");

            Assert.That(snapshot["patterns.full"], Is.EqualTo("True"));
            Assert.That(snapshot["patterns.balcony"], Is.EqualTo("True"));
            Assert.That(snapshot["patterns.partialGallery"], Is.EqualTo("True"));
            Assert.That(snapshot["patterns.perimeterRing"], Is.EqualTo("True"));

            Assert.That(snapshot["producer.noRecipe"], Is.EqualTo("True"), snapshot["producer.failure"]);
            Assert.That(snapshot["producer.basePreserved"], Is.EqualTo("True"));
            Assert.That(snapshot["producer.boundLayerRealized"], Is.EqualTo("True"));
            Assert.That(snapshot["producer.canonicalThresholdBound"], Is.EqualTo("True"));
            Assert.That(int.Parse(snapshot["producer.stackedSurfaces"]), Is.GreaterThan(0));
            Assert.That(snapshot["producer.generatedOwner"], Is.EqualTo("Room:generic-room#gallery"));
            Assert.That(int.Parse(snapshot["producer.occupiedCells"]), Is.GreaterThan(0));
            Assert.That(int.Parse(snapshot["producer.supportCells"]), Is.GreaterThan(0));
            Assert.That(int.Parse(snapshot["producer.clearanceCells"]), Is.GreaterThan(0));
            Assert.That(int.Parse(snapshot["producer.openVolumeCells"]), Is.GreaterThan(0));
            Assert.That(snapshot["producer.openVolumesValid"], Is.EqualTo("True"), snapshot["producer.volumeFailure"]);
            Assert.That(snapshot["producer.vistaAnchorVoidAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["producer.headroomValid"], Is.EqualTo("True"), snapshot["producer.headroomFailure"]);
            Assert.That(snapshot["producer.boundLandingAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["producer.unboundedLandingStillRejected"], Is.EqualTo("True"));

            Assert.That(snapshot["navigation.graphBuilt"], Is.EqualTo("True"), snapshot["navigation.reachability"]);
            Assert.That(snapshot["navigation.fallFreeConnected"], Is.EqualTo("True"), snapshot["navigation.reachability"]);
            Assert.That(snapshot["validator.slotlessLayerAccepted"], Is.EqualTo("True"));
        }

        [Test]
        public void Slice5_PlannedConnectionsAndSharedSpacesResolveOnce()
        {
            Dictionary<string, string> snapshot =
                InvokeSnapshot("BuildSlice5ConnectionRealizationSnapshot");

            Assert.That(snapshot["level.directDoorway"], Is.EqualTo("True"));
            Assert.That(snapshot["level.routedCorridor"], Is.EqualTo("True"));
            Assert.That(snapshot["identity.exactAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["identity.duplicateRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["identity.missingRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["identity.inventedRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["vertical.fourUnitAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["vertical.eightUnitAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["vertical.stairClass"], Is.EqualTo("embedded"));
            Assert.That(snapshot["vertical.stairwellClass"], Is.EqualTo("stairwell"));

            Assert.That(snapshot["bridge.class"], Is.EqualTo("externalSpan"));
            Assert.That(snapshot["bridge.volumeRegistered"], Is.EqualTo("True"));
            Assert.That(snapshot["bridge.volumeValid"], Is.EqualTo("True"));
            Assert.That(snapshot["bridge.fillRejected"], Is.EqualTo("True"));

            Assert.That(int.Parse(snapshot["shared.balconyRimEdges"]), Is.GreaterThan(0));
            Assert.That(int.Parse(snapshot["shared.atriumRimEdges"]), Is.GreaterThan(1));
            Assert.That(int.Parse(snapshot["shared.apertureCandidates"]), Is.GreaterThan(1));
            Assert.That(snapshot["shared.openingsBuilt"], Is.EqualTo("True"), snapshot["shared.openingFailure"]);
            Assert.That(snapshot["shared.apertures"], Is.EqualTo("1"));
            Assert.That(snapshot["shared.surfaceScopedAperture"], Is.EqualTo("True"));

            Assert.That(snapshot["navigation.graphBuilt"], Is.EqualTo("True"), snapshot["navigation.reachability"]);
            Assert.That(snapshot["navigation.directedFalls"], Is.EqualTo("1"));
            Assert.That(snapshot["navigation.fallFreeConnected"], Is.EqualTo("True"), snapshot["navigation.reachability"]);
        }

        private static Dictionary<string, string> InvokeSnapshot(string methodName)
        {
            MethodInfo method = GeneratorType.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, $"Missing diagnostic method {methodName}.");
            string text = (string)method.Invoke(null, Array.Empty<object>())!;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in text.Split('\n'))
            {
                int separator = line.IndexOf('=');
                if (separator >= 0)
                    result[line.Substring(0, separator)] = line.Substring(separator + 1);
            }

            return result;
        }
    }
}

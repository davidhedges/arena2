#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabRecipeWorkflowTests
    {
        private const int Seed = 2026072100;
        private static readonly Assembly EditorAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor");
        private static readonly Type GeneratorType = EditorAssembly.GetType(
            "DungeonLab.Editor.DungeonLabGenerator",
            throwOnError: true)!;

        [Test]
        public void ActiveCatalog_ContainsEveryEnabledValidVersionedRecipe()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildRecipeContractSnapshot");
            string throne = RecipePrefix(snapshot, "episode_throne_twin_stairs_01");
            string vestibule = RecipePrefix(snapshot, "connector_flexible_vestibule_01");
            string cornerReturn = RecipePrefix(snapshot, "connector_corner_return_01");
            string example = RecipePrefix(snapshot, "connector_example_01");

            Assert.That(snapshot["catalog.valid"], Is.EqualTo("True"));
            Assert.That(snapshot["catalog.activeCount"], Is.EqualTo("10"));
            Assert.That(snapshot["catalog.digest"], Has.Length.EqualTo(64));
            Assert.That(snapshot["route.recipeSlotCount"], Is.EqualTo("3"));
            Assert.That(snapshot["route.catalogDigestMatches"], Is.EqualTo("True"));
            Assert.That(snapshot[$"{throne}.schema"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{vestibule}.schema"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{cornerReturn}.schema"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{example}.schema"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{throne}.disabledForGeneration"], Is.EqualTo("False"));
            Assert.That(snapshot[$"{vestibule}.disabledForGeneration"], Is.EqualTo("False"));
            Assert.That(snapshot[$"{cornerReturn}.disabledForGeneration"], Is.EqualTo("False"));
            Assert.That(snapshot[$"{example}.disabledForGeneration"], Is.EqualTo("False"));
            Assert.That(snapshot[$"{throne}.currentValid"], Is.EqualTo("True"));
            Assert.That(snapshot[$"{vestibule}.currentValid"], Is.EqualTo("True"));
            Assert.That(snapshot[$"{cornerReturn}.currentValid"], Is.EqualTo("True"));
            Assert.That(snapshot[$"{example}.currentValid"], Is.EqualTo("True"));
        }

        [Test]
        public void ContractValidators_PassSchemaStructureVariationAndNeighbors()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildRecipeContractSnapshot");
            string throne = RecipePrefix(snapshot, "episode_throne_twin_stairs_01");
            string vestibule = RecipePrefix(snapshot, "connector_flexible_vestibule_01");
            string cornerReturn = RecipePrefix(snapshot, "connector_corner_return_01");
            string example = RecipePrefix(snapshot, "connector_example_01");

            foreach (string prefix in new[] { throne, vestibule, cornerReturn, example })
            {
                Assert.That(snapshot[$"{prefix}.schemaValid"], Is.EqualTo("True"));
                Assert.That(snapshot[$"{prefix}.structureValid"], Is.EqualTo("True"));
                Assert.That(snapshot[$"{prefix}.variationValid"], Is.EqualTo("True"));
                Assert.That(snapshot[$"{prefix}.neighborValid"], Is.EqualTo("True"));
                Assert.That(snapshot[$"{prefix}.ports"], Is.EqualTo("2"));
                Assert.That(snapshot[$"{prefix}.isolatedOrientationCount"], Is.EqualTo("4"));
                Assert.That(snapshot[$"{prefix}.isolatedGeometryValid"], Is.EqualTo("True"));
                Assert.That(snapshot[$"{prefix}.isolatedVisualAssetsValid"], Is.EqualTo("True"));
            }

            Assert.That(snapshot[$"{throne}.isolatedAlternativeCount"], Is.EqualTo("2"));
            Assert.That(snapshot[$"{throne}.isolatedCombinationCount"], Is.EqualTo("8"));
            Assert.That(snapshot[$"{vestibule}.isolatedAlternativeCount"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{vestibule}.isolatedCombinationCount"], Is.EqualTo("4"));
            Assert.That(snapshot[$"{cornerReturn}.isolatedAlternativeCount"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{cornerReturn}.isolatedCombinationCount"], Is.EqualTo("4"));
            Assert.That(snapshot[$"{example}.isolatedAlternativeCount"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{example}.isolatedCombinationCount"], Is.EqualTo("4"));

            Assert.That(snapshot["schema.fieldCount"], Is.EqualTo("19"));
            Assert.That(snapshot["schema.allFieldsConsumed"], Is.EqualTo("True"));
        }

        [Test]
        public void ContrastRecipe_IsStructurallyDifferentWithoutSpecialCaseSchema()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildRecipeContractSnapshot");
            string throne = RecipePrefix(snapshot, "episode_throne_twin_stairs_01");
            string vestibule = RecipePrefix(snapshot, "connector_flexible_vestibule_01");
            string cornerReturn = RecipePrefix(snapshot, "connector_corner_return_01");
            string example = RecipePrefix(snapshot, "connector_example_01");

            Assert.That(snapshot[$"{throne}.transitions"], Is.EqualTo("2"));
            Assert.That(snapshot[$"{throne}.symmetryPairs"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{throne}.variations"], Is.EqualTo("2"));
            Assert.That(snapshot[$"{vestibule}.transitions"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{vestibule}.symmetryPairs"], Is.EqualTo("0"));
            Assert.That(snapshot[$"{vestibule}.variations"], Is.EqualTo("0"));
            Assert.That(snapshot[$"{cornerReturn}.transitions"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{cornerReturn}.symmetryPairs"], Is.EqualTo("0"));
            Assert.That(snapshot[$"{cornerReturn}.variations"], Is.EqualTo("0"));
            Assert.That(snapshot[$"{example}.transitions"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{example}.symmetryPairs"], Is.EqualTo("0"));
            Assert.That(snapshot[$"{example}.variations"], Is.EqualTo("0"));

            Type schemaType = EditorAssembly.GetType("DungeonLab.Editor.DungeonRecipeAsset", true)!;
            foreach (FieldInfo field in schemaType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.That(field.Name, Does.Not.Contain("throne").IgnoreCase);
                Assert.That(field.Name, Does.Not.Contain("vestibule").IgnoreCase);
                Assert.That(field.Name, Does.Not.Contain("corner").IgnoreCase);
                Assert.That(field.Name, Does.Not.Contain("example").IgnoreCase);
            }
        }

        [Test]
        public void FullDungeon_ResolvesGeneratedRecipeOpportunitiesThroughCanonicalConsumers()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildRecipeFullDungeonSnapshot");

            Assert.That(snapshot["accepted"], Is.EqualTo("true"));
            Assert.That(snapshot["validation.passed"], Is.EqualTo("true"));
            Assert.That(snapshot["validation.recipes"], Is.EqualTo("true"));
            int recipeCount = int.Parse(snapshot["recipes.count"]);
            Assert.That(recipeCount, Is.InRange(0, 3));
            for (int recipe = 0; recipe < recipeCount; recipe++)
            {
                string prefix = $"recipe{recipe}";
                Assert.That(snapshot[$"{prefix}.atomic"], Is.EqualTo("true"));
                Assert.That(int.Parse(snapshot[$"{prefix}.transitions"]), Is.GreaterThan(0));
                Assert.That(int.Parse(snapshot[$"{prefix}.protected"]), Is.GreaterThan(0));
            }
        }

        [Test]
        public void RendererAbyssAndCollision_ConsumeAllRecipesWithoutRepair()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildRecipeFullDungeonSnapshot");

            Assert.That(snapshot["renderer.passed"], Is.EqualTo("true"));
            Assert.That(snapshot["renderer.rejectedPlacements"], Is.EqualTo("0"));
            Assert.That(snapshot["abyss.passed"], Is.EqualTo("true"));
            Assert.That(snapshot["collision.passed"], Is.EqualTo("true"));
        }

        [Test]
        public void RecipeStepStripLandings_DoNotOpenRemoteAbyssEdges()
        {
            HashSet<string> daisOpenEdges = TransitionOpenEdges("dais");
            HashSet<string> embeddedOpenEdges = TransitionOpenEdges("embedded");

            Assert.That(daisOpenEdges, Is.Empty,
                "A seam/dais landing is traversal metadata; only its transition-cell face is filled by the step strip.");
            Assert.That(embeddedOpenEdges, Does.Contain("2,0:2"),
                "An actual embedded stair mouth must keep opening its upper landing's east face.");
            Assert.That(embeddedOpenEdges, Does.Contain("0,-1:8"),
                "An actual embedded stair mouth must keep opening its lower landing's west face.");
        }

        [Test]
        public void AvailabilityExcludesDisabled_AndExplicitlyRejectsEnabledInvalidContent()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildRecipeAvailabilitySnapshot");

            Assert.That(snapshot["validation.passed"], Is.EqualTo("True"));
            Assert.That(snapshot["validation.nonMutating"], Is.EqualTo("True"));
            Assert.That(snapshot["source.enabled"], Is.EqualTo("True"));
            Assert.That(snapshot["source.digestLength"], Is.EqualTo("64"));
            Assert.That(snapshot["edited.digestChanged"], Is.EqualTo("True"));
            Assert.That(snapshot["disabled.catalogValid"], Is.EqualTo("True"));
            Assert.That(snapshot["disabled.activeCount"], Is.EqualTo("0"));
            Assert.That(snapshot["invalid.catalogValid"], Is.EqualTo("False"));
            Assert.That(snapshot["invalid.catalogReason"], Does.Contain("enabled recipe"));
            Assert.That(snapshot["invalid.catalogReason"], Does.Contain("RECIPE_TRANSITION_CONTRACT"));
            Assert.That(snapshot["invalid.structurePassed"], Is.EqualTo("False"));
            Assert.That(snapshot["fresh.disabledForGeneration"], Is.EqualTo("True"));
        }

        [Test]
        public void AuthoringPreviewGallery_IsDeterministicAndCoversRequiredWorkflowViews()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildRecipeWorkflowSnapshot");

            Assert.That(snapshot["gallery.firstPassed"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.secondPassed"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.samePath"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.sameHash"], Is.EqualTo("True"));
            Assert.That(int.Parse(snapshot["gallery.entryCount"]), Is.GreaterThanOrEqualTo(66));
            Assert.That(snapshot["gallery.contract"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.topDown"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.playerHeight"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.belowFloor"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.neighbor"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.mirrorStateCount"], Is.EqualTo("2"));
            Assert.That(snapshot["gallery.fullDungeon"], Is.EqualTo("True"));
            Assert.That(snapshot["contrast.firstPassed"], Is.EqualTo("True"));
            Assert.That(snapshot["contrast.secondPassed"], Is.EqualTo("True"));
            Assert.That(snapshot["contrast.samePath"], Is.EqualTo("True"));
            Assert.That(snapshot["contrast.sameHash"], Is.EqualTo("True"));
            Assert.That(int.Parse(snapshot["contrast.entryCount"]), Is.GreaterThanOrEqualTo(34));
            Assert.That(snapshot["contrast.requiredViews"], Is.EqualTo("True"));
            Assert.That(snapshot["contrast.mirrorStateCount"], Is.EqualTo("2"));
            Assert.That(snapshot["contrast.fullDungeon"], Is.EqualTo("True"));
            Assert.That(snapshot["third.firstPassed"], Is.EqualTo("True"));
            Assert.That(snapshot["third.secondPassed"], Is.EqualTo("True"));
            Assert.That(snapshot["third.samePath"], Is.EqualTo("True"));
            Assert.That(snapshot["third.sameHash"], Is.EqualTo("True"));
            Assert.That(int.Parse(snapshot["third.entryCount"]), Is.GreaterThanOrEqualTo(34));
            Assert.That(snapshot["third.requiredViews"], Is.EqualTo("True"));
            Assert.That(snapshot["third.mirrorStateCount"], Is.EqualTo("2"));
            Assert.That(snapshot["third.fullDungeon"], Is.EqualTo("True"));
        }

        [Test]
        public void SelectedVistaRecipe_ExpandsLatticeBeforeRoomInflation()
        {
            Dictionary<string, string> snapshot = Snapshot(
                "BuildSelectedVistaRecipeSpacingSnapshot",
                2026072165);

            Assert.That(snapshot["catalog.error"], Is.Empty);
            Assert.That(snapshot["selection.error"], Is.Empty);
            Assert.That(snapshot["target.recipe"], Is.EqualTo("episode_hanging_bridge_court_01"));
            Assert.That(snapshot["target.contractValid"], Is.EqualTo("True"));
            Assert.That(snapshot["target.portsMatchTopology"], Is.EqualTo("True"));
            Assert.That(snapshot["target.incompatibleAxisRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["cornerReturn.selected"], Is.EqualTo("True"));
            Assert.That(snapshot["cornerReturn.reservedFootprintCount"], Is.EqualTo("1"));
            Assert.That(snapshot["cornerReturn.realizedBodyCount"], Is.EqualTo("0"));
            Assert.That(snapshot["cornerReturn.atomicMatch"], Is.EqualTo("True"));
            Assert.That(snapshot["vista.required"], Is.EqualTo("3"));
            for (int layoutAttempt = 1; layoutAttempt <= 2; layoutAttempt++)
            {
                string prefix = $"attempt{layoutAttempt}";
                Assert.That(int.Parse(snapshot[$"{prefix}.beforeClear"]), Is.LessThan(3));
                Assert.That(int.Parse(snapshot[$"{prefix}.added"]), Is.GreaterThan(0));
                Assert.That(int.Parse(snapshot[$"{prefix}.afterClear"]), Is.GreaterThanOrEqualTo(3));
                Assert.That(snapshot[$"{prefix}.idempotent"], Is.EqualTo("True"));
            }
        }

        [Test]
        public void RecipePoolSelection_UsesStableSlotsAndCompatibleCandidatePool()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildRecipePoolSelectionSnapshot");

            Assert.That(snapshot["catalog.activeCount"], Is.EqualTo("10"));
            Assert.That(snapshot["catalog.digest"], Has.Length.EqualTo(64));
            Assert.That(snapshot["route.recipeSlotCount"], Is.EqualTo("3"));
            Assert.That(snapshot["report.repeatable"], Is.EqualTo("True"));
            Assert.That(snapshot["report.hash"], Has.Length.EqualTo(64));

            AssertSlot(
                snapshot,
                "required-compression",
                "connector",
                "compression",
                "connector_example_01,connector_flexible_vestibule_01",
                snapshot["slot.required-compression.selected"],
                "connector_corner_return_01:BEAT_INELIGIBLE,connector_generic_room_01:BEAT_INELIGIBLE,episode_throne_twin_stairs_01:ROLE_INELIGIBLE");
            Assert.That(
                snapshot["slot.required-compression.selected"],
                Is.EqualTo("connector_example_01")
                    .Or.EqualTo("connector_flexible_vestibule_01"));
            AssertSlot(
                snapshot,
                "required-landmark",
                "landmark",
                "landmark",
                "episode_throne_twin_stairs_01",
                "episode_throne_twin_stairs_01",
                "connector_corner_return_01:ROLE_INELIGIBLE,connector_example_01:ROLE_INELIGIBLE,connector_flexible_vestibule_01:ROLE_INELIGIBLE,connector_generic_room_01:ROLE_INELIGIBLE");
            AssertSlot(
                snapshot,
                "required-return",
                "connector",
                "return",
                "connector_corner_return_01,connector_generic_room_01",
                snapshot["slot.required-return.selected"],
                "connector_example_01:BEAT_INELIGIBLE,connector_flexible_vestibule_01:BEAT_INELIGIBLE,episode_throne_twin_stairs_01:ROLE_INELIGIBLE");
            // Two connector/return recipes are eligible since
            // `connector_generic_room_01` was enabled, so the slot draws between
            // them; pinning one would assert the candidate pool away.
            Assert.That(
                snapshot["slot.required-return.selected"],
                Is.EqualTo("connector_corner_return_01")
                    .Or.EqualTo("connector_generic_room_01"));

            Assert.That(snapshot["noCandidate.resolved"], Is.EqualTo("True"));
            Assert.That(snapshot["noCandidate.selected"], Is.EqualTo("1"));
            Assert.That(snapshot["noCandidate.reason"], Is.Empty);
        }

        [Test]
        public void GeneratedOpportunities_FallBackToACompleteGenericDungeon()
        {
            Dictionary<string, string> snapshot =
                Snapshot("BuildRecipeOpportunityFallbackSnapshot");

            Assert.That(int.Parse(snapshot["generic.opportunities"]), Is.GreaterThan(0));
            Assert.That(snapshot["generic.selected"], Is.EqualTo("0"));
            Assert.That(snapshot["generic.repeatSelected"], Is.EqualTo("0"));
            Assert.That(int.Parse(snapshot["generic.allCompatibleSelected"]), Is.GreaterThan(0));
            Assert.That(snapshot["generic.accepted"], Is.EqualTo("true"));
            Assert.That(snapshot["generic.validation"], Is.EqualTo("true"));
            Assert.That(snapshot["generic.recipes"], Is.EqualTo("true"));
            Assert.That(snapshot["generic.richLayering"], Is.EqualTo("true"));
            Assert.That(snapshot["generic.verticalTraversal"], Is.EqualTo("true"));
            Assert.That(snapshot["generic.bottomToTopTraversal"], Is.EqualTo("true"));
            Assert.That(int.Parse(snapshot["generic.stackedSurfaces"]), Is.GreaterThan(0));
            Assert.That(snapshot["generic.recipeResolutionHash"], Has.Length.EqualTo(64));
            Assert.That(snapshot["generic.canonicalHash"], Has.Length.EqualTo(64));
            Assert.That(snapshot["generic.renderer"], Is.EqualTo("true"));
            Assert.That(snapshot["generic.boundary"], Is.EqualTo("true"));
            Assert.That(snapshot["generic.collision"], Is.EqualTo("true"));
            Assert.That(snapshot["emptyCatalog.resolved"], Is.EqualTo("True"));
            Assert.That(snapshot["emptyCatalog.selected"], Is.EqualTo("0"));
            Assert.That(snapshot["emptyCatalog.reason"], Is.Empty);
            Assert.That(snapshot["emptyCatalog.intentValid"], Is.EqualTo("True"));
            Assert.That(snapshot["emptyCatalog.intentReason"], Is.Empty);
        }

        [Test]
        public void EmptyRecipeCollections_AreCompletePipelineResults()
        {
            Dictionary<string, string> snapshot =
                Snapshot("BuildEmptyRecipePipelineSnapshot");

            Assert.That(int.Parse(snapshot["opportunities.count"]), Is.GreaterThan(0));
            Assert.That(snapshot["opportunities.resolved"], Is.EqualTo("True"));
            Assert.That(snapshot["opportunities.selected"], Is.EqualTo("0"));
            Assert.That(snapshot["opportunities.reason"], Is.Empty);
            Assert.That(snapshot["intent.valid"], Is.EqualTo("True"));
            Assert.That(snapshot["intent.reason"], Is.Empty);
            Assert.That(snapshot["empty.realized"], Is.EqualTo("True"));
            Assert.That(snapshot["empty.realizationReason"], Is.Empty);
            Assert.That(snapshot["empty.baseLevels"], Is.EqualTo("0"));
            Assert.That(snapshot["empty.validated"], Is.EqualTo("True"));
            Assert.That(snapshot["empty.validationReason"], Is.Empty);
            Assert.That(snapshot["empty.resolutions"], Is.EqualTo("0"));
        }

        [Test]
        public void RecipePoolSelection_DeletesRecipeIdentityFromRouteSlotDeclarations()
        {
            Assert.That(
                EditorAssembly.GetType("DungeonLab.Editor.DungeonRecipeIds", throwOnError: false),
                Is.Null);

            Type routeNodeType = GeneratorType.GetNestedType(
                "RouteNodeIntent",
                BindingFlags.NonPublic)!;
            Assert.That(routeNodeType, Is.Not.Null);
            Assert.That(
                routeNodeType.GetField(
                    "recipeSlotId",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                Is.Not.Null);
            Assert.That(
                routeNodeType.GetField(
                    "landmarkSlotId",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                Is.Null);
        }

        [Test]
        public void UnknownDisabledRecipe_PreviewsInForcedCanonicalContextWithoutLeaking()
        {
            Dictionary<string, string> snapshot =
                Snapshot("BuildRecipeAuthoringPreviewIsolationSnapshot");

            Assert.That(
                snapshot["preview.recipeId"],
                Is.EqualTo("preview_disabled_connector_slice_c_01"));
            Assert.That(snapshot["preview.disabledForGeneration"], Is.EqualTo("True"));
            Assert.That(snapshot["preview.catalogMember"], Is.EqualTo("False"));
            Assert.That(
                snapshot["preview.firstPassed"],
                Is.EqualTo("True"),
                snapshot["preview.firstMessage"]);
            Assert.That(
                snapshot["preview.secondPassed"],
                Is.EqualTo("True"),
                snapshot["preview.secondMessage"]);
            Assert.That(snapshot["preview.samePath"], Is.EqualTo("True"));
            Assert.That(snapshot["preview.sameHash"], Is.EqualTo("True"));
            Assert.That(snapshot["preview.isolatedEvidence"], Is.EqualTo("True"));
            Assert.That(snapshot["preview.neighborEvidence"], Is.EqualTo("True"));
            Assert.That(snapshot["context.forced"], Is.EqualTo("True"));
            Assert.That(
                snapshot["context.recipeId"],
                Is.EqualTo("preview_disabled_connector_slice_c_01"));
            // Not pinned to a topology: the forced-preview context takes whatever
            // topology its seed draws, and that has moved twice. What the test is
            // about is that the context is forced onto the right recipe and slot,
            // asserted either side of this.
            Assert.That(snapshot["context.topologyId"], Is.Not.Empty);
            Assert.That(snapshot["context.recipeSlotId"], Is.EqualTo("required-compression"));
            Assert.That(snapshot["context.routeNodeId"], Is.Not.Empty);
            Assert.That(snapshot["fullDungeon.canonical"], Is.EqualTo("True"));
            Assert.That(snapshot["fullDungeon.renderer"], Is.EqualTo("True"));
            Assert.That(snapshot["fullDungeon.abyss"], Is.EqualTo("True"));
            Assert.That(snapshot["fullDungeon.collision"], Is.EqualTo("True"));
            Assert.That(snapshot["incompatible.passed"], Is.EqualTo("False"));
            Assert.That(
                snapshot["incompatible.message"],
                Does.Contain("had no compatible generated opportunity"));
            Assert.That(snapshot["ordinary.catalogValid"], Is.EqualTo("True"));
            Assert.That(snapshot["ordinary.activeCount"], Is.EqualTo("10"));
            Assert.That(snapshot["ordinary.catalogDigestPreserved"], Is.EqualTo("True"));
            Assert.That(snapshot["ordinary.previewAbsentBefore"], Is.EqualTo("True"));
            Assert.That(snapshot["ordinary.previewAbsentAfter"], Is.EqualTo("True"));
            Assert.That(snapshot["ordinary.routeHashPreserved"], Is.EqualTo("True"));
            Assert.That(snapshot["ordinary.canonicalHashPreserved"], Is.EqualTo("True"));
        }

        [Test]
        public void SliceD_ApprovedCompressionRecipeProvesDeterministicPoolAndDisableBehavior()
        {
            Dictionary<string, string> snapshot =
                Snapshot("BuildRecipePoolProofSnapshot");

            Assert.That(snapshot["catalog.activeCount"], Is.EqualTo("10"));
            Assert.That(snapshot["catalog.digest"], Has.Length.EqualTo(64));
            Assert.That(snapshot["recipe.id"], Is.EqualTo("connector_example_01"));
            Assert.That(snapshot["recipe.kind"], Is.EqualTo("Connector"));
            Assert.That(snapshot["recipe.disabledForGeneration"], Is.EqualTo("False"));
            Assert.That(snapshot["recipe.contract"], Is.EqualTo("True"));
            Assert.That(snapshot["recipe.schema"], Is.EqualTo("True"));
            Assert.That(snapshot["recipe.structure"], Is.EqualTo("True"));
            Assert.That(snapshot["recipe.variation"], Is.EqualTo("True"));
            Assert.That(snapshot["recipe.neighbor"], Is.EqualTo("True"));
            Assert.That(snapshot["recipe.transitionImplementation"], Is.EqualTo("seam-rise-1"));

            Assert.That(
                snapshot["gallery.firstPassed"],
                Is.EqualTo("True"),
                snapshot["gallery.message"]);
            Assert.That(
                snapshot["gallery.secondPassed"],
                Is.EqualTo("True"),
                snapshot["gallery.secondMessage"]);
            Assert.That(snapshot["gallery.samePath"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.sameHash"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.isolated"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.neighbor"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.canonical"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.renderer"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.abyss"], Is.EqualTo("True"));
            Assert.That(snapshot["gallery.collision"], Is.EqualTo("True"));
            Assert.That(snapshot["context.forced"], Is.EqualTo("True"));
            Assert.That(snapshot["context.recipeId"], Is.EqualTo("connector_example_01"));
            Assert.That(snapshot["context.slotId"], Is.EqualTo("required-compression"));

            Assert.That(snapshot["corpus.seedCount"], Is.EqualTo("50"));
            Assert.That(snapshot["corpus.firstAccepted"], Is.EqualTo("50"));
            Assert.That(snapshot["corpus.secondAccepted"], Is.EqualTo("50"));
            Assert.That(
                snapshot["corpus.candidates"],
                Is.EqualTo("connector_example_01,connector_flexible_vestibule_01"));
            Assert.That(
                snapshot["corpus.firstSelections"],
                Is.EqualTo("connector_example_01,connector_flexible_vestibule_01"));
            Assert.That(
                snapshot["corpus.secondSelections"],
                Is.EqualTo("connector_example_01,connector_flexible_vestibule_01"));
            Assert.That(snapshot["corpus.firstDigest"], Has.Length.EqualTo(64));
            Assert.That(snapshot["corpus.firstDigest"], Is.EqualTo(snapshot["corpus.secondDigest"]));
            Assert.That(snapshot["corpus.repeatable"], Is.EqualTo("True"));
            Assert.That(snapshot["corpus.nonTargetSelectionsPreserved"], Is.EqualTo("True"));

            Assert.That(snapshot["withoutExample.resolved"], Is.EqualTo("True"));
            Assert.That(
                snapshot["withoutExample.compression"],
                Is.EqualTo("connector_flexible_vestibule_01"));
            Assert.That(
                snapshot["withoutExample.landmark"],
                Is.EqualTo("episode_throne_twin_stairs_01"));
            Assert.That(
                snapshot["withoutExample.return"],
                Is.EqualTo("connector_corner_return_01"));
            Assert.That(snapshot["withoutVestibule.resolved"], Is.EqualTo("True"));
            Assert.That(
                snapshot["withoutVestibule.compression"],
                Is.EqualTo("connector_example_01"));
            Assert.That(
                snapshot["withoutVestibule.landmark"],
                Is.EqualTo("episode_throne_twin_stairs_01"));
            Assert.That(
                snapshot["withoutVestibule.return"],
                Is.EqualTo("connector_corner_return_01"));
            Assert.That(snapshot["withoutBoth.resolved"], Is.EqualTo("True"));
            Assert.That(snapshot["withoutBoth.compression"], Is.Empty);
            Assert.That(snapshot["withoutBoth.reason"], Is.Empty);

            Assert.That(
                GeneratorType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                    .Count(method => method.Name == "TryResolveRecipeOpportunities"),
                Is.EqualTo(1));
            Assert.That(
                GeneratorType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                    .Count(method => method.Name == "TryPlaceRouteRecipes"),
                Is.EqualTo(1));
            Assert.That(
                GeneratorType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                    .Count(method => method.Name == "TryRealizeRecipes"),
                Is.EqualTo(1));
        }

        [Test]
        public void FixedSeed_RecipeResolutionsRemainDeterministic()
        {
            string first = Invoke("BuildRecipeFullDungeonSnapshot");
            string second = Invoke("BuildRecipeFullDungeonSnapshot");
            Assert.That(first, Is.EqualTo(second));
        }

        private static void AssertSlot(
            IReadOnlyDictionary<string, string> snapshot,
            string slotId,
            string role,
            string beat,
            string candidates,
            string selected,
            string rejected)
        {
            string prefix = $"slot.{slotId}";
            Assert.That(snapshot[$"{prefix}.node"], Is.Not.Empty);
            Assert.That(snapshot[$"{prefix}.role"], Is.EqualTo(role));
            Assert.That(snapshot[$"{prefix}.beat"], Is.EqualTo(beat));
            Assert.That(snapshot[$"{prefix}.catalogDigestMatches"], Is.EqualTo("True"));
            Assert.That(snapshot[$"{prefix}.candidates"], Is.EqualTo(candidates));
            foreach (string expectedRejection in rejected.Split(','))
            {
                Assert.That(
                    snapshot[$"{prefix}.rejected"],
                    Does.Contain(expectedRejection),
                    $"{slotId} did not retain rejection evidence for {expectedRejection}");
            }
            Assert.That(snapshot[$"{prefix}.selected"], Is.EqualTo(selected));
            Assert.That(snapshot[$"{prefix}.stream"], Is.EqualTo("recipe-selection-v2"));
        }

        private static Dictionary<string, string> Snapshot(string methodName)
        {
            return Parse(Invoke(methodName));
        }

        private static Dictionary<string, string> Snapshot(string methodName, int seed)
        {
            return Parse(Invoke(methodName, seed));
        }

        private static HashSet<string> TransitionOpenEdges(string placementClass)
        {
            Type edgeModelType = EditorAssembly.GetType(
                "DungeonLab.Editor.ElevationEdgeModel",
                throwOnError: true)!;
            Type transitionType = edgeModelType.GetNestedType(
                "TransitionEdge",
                BindingFlags.Public)!;
            ConstructorInfo constructor = transitionType.GetConstructor(new[]
            {
                typeof(Vector2Int),
                typeof(Vector2Int),
                typeof(string),
                typeof(Vector2Int[]),
                typeof(Vector2Int[]),
                typeof(Vector2Int[]),
                typeof(int),
                typeof(int),
                typeof(string)
            })!;
            Assert.That(constructor, Is.Not.Null, "Missing transition constructor used by recipe realization.");

            object transition = constructor.Invoke(new object[]
            {
                new Vector2Int(1, 0),
                new Vector2Int(0, 0),
                "step-strip",
                new[] { new Vector2Int(0, -1) },
                new[] { new Vector2Int(2, 0) },
                new[] { new Vector2Int(0, 0) },
                2, // east
                8, // west
                placementClass
            });
            Array transitions = Array.CreateInstance(transitionType, 1);
            transitions.SetValue(transition, 0);
            var levels = new Dictionary<Vector2Int, int>
            {
                [new Vector2Int(0, 0)] = 12,
                [new Vector2Int(1, 0)] = 13,
                [new Vector2Int(0, -1)] = 12,
                [new Vector2Int(2, 0)] = 13
            };
            MethodInfo buildTransitionKeys = edgeModelType.GetMethod(
                "BuildTransitionKeys",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(buildTransitionKeys, Is.Not.Null, "Missing renderer transition projection.");

            object[] arguments = { levels, transitions, null!, null!, null! };
            buildTransitionKeys.Invoke(null, arguments);

            Type openEdgeType = edgeModelType.GetNestedType(
                "OpenEdgeKey",
                BindingFlags.NonPublic)!;
            FieldInfo cellField = openEdgeType.GetField(
                "cell",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo directionField = openEdgeType.GetField(
                "direction",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (object edge in (IEnumerable)arguments[2])
            {
                Vector2Int cell = (Vector2Int)cellField.GetValue(edge)!;
                int direction = (int)directionField.GetValue(edge)!;
                result.Add($"{cell.x},{cell.y}:{direction}");
            }

            return result;
        }

        private static string RecipePrefix(Dictionary<string, string> snapshot, string recipeId)
        {
            int recipeCount = snapshot.TryGetValue("catalog.activeCount", out string count) ||
                snapshot.TryGetValue("recipes.count", out count)
                    ? int.Parse(count)
                    : 0;
            for (int index = 0; index < recipeCount; index++)
            {
                string prefix = $"recipe{index}";
                if (snapshot.TryGetValue($"{prefix}.id", out string id) &&
                    string.Equals(id, recipeId, StringComparison.Ordinal))
                {
                    return prefix;
                }
            }

            Assert.Fail($"Recipe '{recipeId}' was absent from the generic recipe diagnostics.");
            return string.Empty;
        }

        private static string Invoke(string methodName)
        {
            return Invoke(methodName, Seed);
        }

        private static string Invoke(string methodName, int seed)
        {
            MethodInfo method = GeneratorType.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, $"Missing diagnostic method {methodName}.");
            return (string)method.Invoke(null, new object[] { seed })!;
        }

        private static Dictionary<string, string> Parse(string snapshot)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in snapshot.Split('\n'))
            {
                int separator = line.IndexOf('=');
                if (separator >= 0)
                    values[line.Substring(0, separator)] = line.Substring(separator + 1);
            }

            return values;
        }
    }
}

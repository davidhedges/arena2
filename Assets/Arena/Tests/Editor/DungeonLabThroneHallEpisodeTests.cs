#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabThroneHallEpisodeTests
    {
        private const int EpisodeSeed = 2026072100;
        private const int HallwayEndClearanceSeed = 2062860779;
        private const int ShowpieceFitRegressionSeed = -2078245253;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void RouteIntent_DeclaresOneNarrowLandmarkEpisodeBeforeCoordinates()
        {
            Dictionary<string, string> intent = ParseSnapshot(
                InvokeSnapshot("BuildRouteIntentOnlySnapshot", EpisodeSeed));

            Assert.That(intent["episode.id"], Is.EqualTo("episode_throne_twin_stairs_01"));
            Assert.That(intent["episode.slotNode"], Is.EqualTo("4"));
            Assert.That(intent["episode.focalAxisBinding"], Is.EqualTo("VistaSourceToTarget"));
            Assert.That(intent["episode.coupledStairCount"], Is.EqualTo("2"));
            Assert.That(intent["episode.allowedFocalVariations"], Is.EqualTo("2"));
            Assert.That(intent["episode.thresholdCount"], Is.EqualTo("2"));
            Assert.That(intent["containsSpatialCoordinates"], Is.EqualTo("False"));
        }

        [Test]
        public void IsolatedProbe_CoversEveryAllowedOrientationAndFocalVariation()
        {
            Dictionary<string, string> isolated = ParseSnapshot(
                InvokeSnapshot("BuildRecipeContractSnapshot", EpisodeSeed));
            string recipe = RecipePrefix(isolated);

            Assert.That(isolated[$"{recipe}.id"], Is.EqualTo("episode_throne_twin_stairs_01"));
            Assert.That(isolated[$"{recipe}.isolatedOrientationCount"], Is.EqualTo("4"));
            Assert.That(isolated[$"{recipe}.isolatedAlternativeCount"], Is.EqualTo("2"));
            Assert.That(isolated[$"{recipe}.isolatedCombinationCount"], Is.EqualTo("8"));
            Assert.That(isolated[$"{recipe}.isolatedGeometryValid"], Is.EqualTo("True"));
            Assert.That(isolated[$"{recipe}.isolatedVisualAssetsValid"], Is.EqualTo("True"));
            Assert.That(isolated["schema.allFieldsConsumed"], Is.EqualTo("True"));
            Assert.That(int.Parse(isolated["schema.fieldCount"]), Is.GreaterThan(0));
        }

        [Test]
        public void FixedSeed_ResolvesTheWholeEpisodeDeterministically()
        {
            string firstText = InvokeSnapshot("BuildThroneEpisodeCharacterizationSnapshot", EpisodeSeed);
            string secondText = InvokeSnapshot("BuildThroneEpisodeCharacterizationSnapshot", EpisodeSeed);
            Dictionary<string, string> first = ParseSnapshot(firstText);
            Dictionary<string, string> second = ParseSnapshot(secondText);
            string recipe = RecipePrefix(first);

            Assert.That(first["accepted"], Is.EqualTo("true"), firstText);
            Assert.That(first[$"{recipe}.atomic"], Is.EqualTo("true"), firstText);
            Assert.That(first["hash.routeIntent"], Is.EqualTo(second["hash.routeIntent"]));
            Assert.That(first["hash.layout"], Is.EqualTo(second["hash.layout"]));
            Assert.That(first["hash.tieredLevelPlan"], Is.EqualTo(second["hash.tieredLevelPlan"]));
            Assert.That(first["hash.canonical"], Is.EqualTo(second["hash.canonical"]));
            Assert.That(firstText, Is.EqualTo(secondText));
        }

        [Test]
        public void GenericRouteConnections_EndAtTheTwoDeclaredTypedThresholds()
        {
            Dictionary<string, string> report = EpisodeSnapshot();
            string recipe = RecipePrefix(report);

            Assert.That(report[$"{recipe}.ports"], Is.EqualTo("2"));
            Assert.That(report[$"{recipe}.portsBound"], Is.EqualTo("true"));
            Assert.That(report["validation.recipes"], Is.EqualTo("true"));
        }

        [Test]
        public void TwinStairs_Landings_GalleriesAndFocalZoneRemainCoupledAndProtected()
        {
            Dictionary<string, string> report = EpisodeSnapshot();
            Dictionary<string, string> contract = ParseSnapshot(
                InvokeSnapshot("BuildRecipeContractSnapshot", EpisodeSeed));
            string recipe = RecipePrefix(report);
            string contractRecipe = RecipePrefix(contract);

            Assert.That(report[$"{recipe}.protectedFocalCells"], Is.EqualTo("15"));
            Assert.That(report[$"{recipe}.elevatedZones"], Is.EqualTo("2"));
            Assert.That(report[$"{recipe}.transitions"], Is.EqualTo("2"));
            // The absolute level is a property of whichever node this seed's
            // topology hands the episode - sunken-basin puts its landmark on the
            // basin floor at 0 - so what is worth pinning is the coupling: the
            // raised zone sits exactly one level above the recipe's own floor.
            int baseLevel = int.Parse(report[$"{recipe}.baseLevel"]);
            Assert.That(report[$"{recipe}.elevatedLevel"], Is.EqualTo((baseLevel + 1).ToString()));
            Assert.That(report[$"{recipe}.reservationsComplete"], Is.EqualTo("true"));
            Assert.That(contract[$"{contractRecipe}.symmetryPairs"], Is.EqualTo("1"));
            Assert.That(report[$"{recipe}.protectedZonesValid"], Is.EqualTo("true"));
            Assert.That(report["schema.allFieldsConsumed"], Is.EqualTo("true"));
        }

        [Test]
        public void FullDungeon_PreservesEveryPhase3HardGate()
        {
            Dictionary<string, string> report = EpisodeSnapshot();

            Assert.That(report["vertical.routeClimb"], Is.EqualTo("24"));
            Assert.That(report["vertical.requirementsSatisfied"], Is.EqualTo("true"));
            Assert.That(report["vista.finalValid"], Is.EqualTo("true"));
            Assert.That(report["validation.layoutConnectivity"], Is.EqualTo("true"));
            Assert.That(report["validation.roomGraphConnectivity"], Is.EqualTo("true"));
            Assert.That(report["validation.verticalTraversal"], Is.EqualTo("true"));
            Assert.That(report["validation.routeRequirements"], Is.EqualTo("true"));
            Assert.That(report["validation.headroom"], Is.EqualTo("true"));
            Assert.That(report["validation.passed"], Is.EqualTo("true"));
        }

        [Test]
        public void ExistingRendererAndCollision_ConsumeTheEpisodeWithoutRepair()
        {
            string snapshot = InvokeSnapshot("BuildThroneEpisodeRendererProbeSnapshot", EpisodeSeed);
            Dictionary<string, string> report = ParseSnapshot(snapshot);

            Assert.That(report["accepted"], Is.EqualTo("true"), snapshot);
            Assert.That(report["boundary"], Is.EqualTo("true"), snapshot);
            Assert.That(report["renderer.passed"], Is.EqualTo("true"), snapshot);
            Assert.That(report["renderer.selectedShowpieces"], Is.EqualTo("1"));
            Assert.That(int.Parse(report["renderer.rejectedPlacements"]), Is.Zero);
            Assert.That(report["collision.passed"], Is.EqualTo("true"), snapshot);
            Assert.That(int.Parse(report["collision.enabledNonTriggerColliders"]), Is.GreaterThan(0));
        }

        [Test]
        public void HallwayEndRegression_PortApproachesAndBackedWallFitAreReservedBeforeStairs()
        {
            string snapshot = InvokeSnapshot(
                "BuildThroneEpisodeCharacterizationSnapshot",
                HallwayEndClearanceSeed);
            Dictionary<string, string> report = ParseSnapshot(snapshot);
            string recipe = RecipePrefix(report);

            Assert.That(report["accepted"], Is.EqualTo("true"), snapshot);
            Assert.That(report[$"{recipe}.atomic"], Is.EqualTo("true"), snapshot);
            Assert.That(report[$"{recipe}.reservationsComplete"], Is.EqualTo("true"), snapshot);
            Assert.That(report[$"{recipe}.approachCells"], Is.EqualTo("2"), snapshot);
            Assert.That(report[$"{recipe}.showpieceRequiredFloorCells"], Is.EqualTo("15"), snapshot);
            Assert.That(report[$"{recipe}.showpieceWallMarginCells"], Is.EqualTo("2"), snapshot);
            Assert.That(report[$"{recipe}.showpieceBackdropVoidCells"], Is.EqualTo("7"), snapshot);
            Assert.That(report["recipe.approachTransitionConflicts"], Is.EqualTo("0"), snapshot);
        }

        [Test]
        public void HallwayEndRegression_RendererAndCollisionConsumeOnlyTheValidatedPlan()
        {
            string snapshot = InvokeSnapshot(
                "BuildThroneEpisodeRendererProbeSnapshot",
                HallwayEndClearanceSeed);
            Dictionary<string, string> report = ParseSnapshot(snapshot);

            Assert.That(report["accepted"], Is.EqualTo("true"), snapshot);
            Assert.That(report["renderer.passed"], Is.EqualTo("true"), snapshot);
            Assert.That(int.Parse(report["renderer.rejectedPlacements"]), Is.Zero, snapshot);
            Assert.That(report["collision.passed"], Is.EqualTo("true"), snapshot);
        }

        [Test]
        public void ShowpieceFitRegression_RenderedVisualStaysInsideItsReservedFloorEnvelope()
        {
            MethodInfo method = GeneratorType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(candidate =>
                    candidate.Name == "BuildThroneEpisodeRenderedSeed" &&
                    candidate.GetParameters().Length == 6);
            object?[] arguments = { ShowpieceFitRegressionSeed, null, null, null, null, null };
            GameObject? root = null;
            try
            {
                root = (GameObject?)method.Invoke(null, arguments);
                Assert.That(
                    root,
                    Is.Not.Null,
                    $"Seed {ShowpieceFitRegressionSeed} did not produce a rendered dungeon.");

                object renderedPlan = arguments[5]!;
                Array resolutions = (Array)ReadField(renderedPlan, "recipeResolutions");
                object resolution = resolutions.Cast<object>().Single(candidate =>
                    !string.IsNullOrEmpty(
                        (string)ReadField(candidate, "selectedVisualImplementationId")));
                string selectedDesign =
                    (string)ReadField(resolution, "selectedVisualImplementationId");
                Vector2Int showpieceOrigin =
                    (Vector2Int)ReadField(resolution, "showpieceOriginCell");
                object reservation = ReadField(resolution, "showpieceReservation");
                Vector2Int[] requiredFloorCells =
                    (Vector2Int[])ReadField(reservation, "requiredFloorCells");
                Assert.That(requiredFloorCells, Has.Length.EqualTo(15));

                string rootName =
                    $"dais_showpiece_{selectedDesign}_{showpieceOrigin.x}_{showpieceOrigin.y}";
                Transform showpiece = root!.GetComponentsInChildren<Transform>(includeInactive: true)
                    .Single(transform => string.Equals(transform.name, rootName, StringComparison.Ordinal));
                Renderer[] renderers =
                    showpiece.GetComponentsInChildren<Renderer>(includeInactive: true);
                Assert.That(renderers, Is.Not.Empty);
                Bounds visualBounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers.Skip(1))
                {
                    visualBounds.Encapsulate(renderer.bounds);
                }

                const float cellSize = 4f;
                const float boundsTolerance = 0.1f;
                Vector3 levelFieldOrigin = (Vector3)arguments[4]!;
                float supportMinX =
                    levelFieldOrigin.x + requiredFloorCells.Min(cell => cell.x) * cellSize;
                float supportMaxX =
                    levelFieldOrigin.x + (requiredFloorCells.Max(cell => cell.x) + 1) * cellSize;
                float supportMinZ =
                    levelFieldOrigin.z + requiredFloorCells.Min(cell => cell.y) * cellSize;
                float supportMaxZ =
                    levelFieldOrigin.z + (requiredFloorCells.Max(cell => cell.y) + 1) * cellSize;

                Assert.That(visualBounds.min.x, Is.GreaterThanOrEqualTo(supportMinX - boundsTolerance));
                Assert.That(visualBounds.max.x, Is.LessThanOrEqualTo(supportMaxX + boundsTolerance));
                Assert.That(visualBounds.min.z, Is.GreaterThanOrEqualTo(supportMinZ - boundsTolerance));
                Assert.That(visualBounds.max.z, Is.LessThanOrEqualTo(supportMaxZ + boundsTolerance));
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
        }

        private static Dictionary<string, string> EpisodeSnapshot()
        {
            return ParseSnapshot(InvokeSnapshot("BuildThroneEpisodeCharacterizationSnapshot", EpisodeSeed));
        }

        private static object ReadField(object instance, string fieldName)
        {
            FieldInfo? field = instance.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(
                field,
                Is.Not.Null,
                $"'{instance.GetType().Name}' did not expose expected field '{fieldName}'.");
            return field!.GetValue(instance)!;
        }

        private static string RecipePrefix(Dictionary<string, string> snapshot)
        {
            int recipeCount = snapshot.TryGetValue("catalog.activeCount", out string count) ||
                snapshot.TryGetValue("recipes.count", out count)
                    ? int.Parse(count)
                    : 0;
            for (int index = 0; index < recipeCount; index++)
            {
                string prefix = $"recipe{index}";
                if (snapshot.TryGetValue($"{prefix}.id", out string id) &&
                    string.Equals(id, "episode_throne_twin_stairs_01", StringComparison.Ordinal))
                {
                    return prefix;
                }
            }

            Assert.Fail("The Phase 4 recipe probe was absent from the generic recipe diagnostics.");
            return string.Empty;
        }

        private static string InvokeSnapshot(string methodName, int seed)
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

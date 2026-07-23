#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase5RecipeWorkflowTests
    {
        private const int Seed = 2026072100;
        private static readonly Assembly EditorAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor");
        private static readonly Type GeneratorType = EditorAssembly.GetType(
            "DungeonLab.Editor.DungeonLabGenerator",
            throwOnError: true)!;

        [Test]
        public void ReviewedCatalog_ContainsThreeCurrentVersionedRecipes()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildPhase5RecipeContractSnapshot");
            string throne = RecipePrefix(snapshot, "episode_throne_twin_stairs_01");
            string vestibule = RecipePrefix(snapshot, "connector_flexible_vestibule_01");
            string cornerReturn = RecipePrefix(snapshot, "connector_corner_return_01");

            Assert.That(snapshot["catalog.valid"], Is.EqualTo("True"));
            Assert.That(snapshot["catalog.reviewedCount"], Is.EqualTo("3"));
            Assert.That(snapshot["catalog.digest"], Has.Length.EqualTo(64));
            Assert.That(snapshot["route.recipeSlotCount"], Is.EqualTo("3"));
            Assert.That(snapshot["route.catalogDigestMatches"], Is.EqualTo("True"));
            Assert.That(snapshot[$"{throne}.schema"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{vestibule}.schema"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{cornerReturn}.schema"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{throne}.reviewCurrent"], Is.EqualTo("True"));
            Assert.That(snapshot[$"{vestibule}.reviewCurrent"], Is.EqualTo("True"));
            Assert.That(snapshot[$"{cornerReturn}.reviewCurrent"], Is.EqualTo("True"));
        }

        [Test]
        public void ContractValidators_PassSchemaStructureVariationAndNeighbors()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildPhase5RecipeContractSnapshot");
            string throne = RecipePrefix(snapshot, "episode_throne_twin_stairs_01");
            string vestibule = RecipePrefix(snapshot, "connector_flexible_vestibule_01");
            string cornerReturn = RecipePrefix(snapshot, "connector_corner_return_01");

            foreach (string prefix in new[] { throne, vestibule, cornerReturn })
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

            Assert.That(snapshot["schema.fieldCount"], Is.EqualTo("17"));
            Assert.That(snapshot["schema.allFieldsConsumed"], Is.EqualTo("True"));
        }

        [Test]
        public void ContrastRecipe_IsStructurallyDifferentWithoutSpecialCaseSchema()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildPhase5RecipeContractSnapshot");
            string throne = RecipePrefix(snapshot, "episode_throne_twin_stairs_01");
            string vestibule = RecipePrefix(snapshot, "connector_flexible_vestibule_01");
            string cornerReturn = RecipePrefix(snapshot, "connector_corner_return_01");

            Assert.That(snapshot[$"{throne}.transitions"], Is.EqualTo("2"));
            Assert.That(snapshot[$"{throne}.symmetryPairs"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{throne}.variations"], Is.EqualTo("2"));
            Assert.That(snapshot[$"{vestibule}.transitions"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{vestibule}.symmetryPairs"], Is.EqualTo("0"));
            Assert.That(snapshot[$"{vestibule}.variations"], Is.EqualTo("0"));
            Assert.That(snapshot[$"{cornerReturn}.transitions"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{cornerReturn}.symmetryPairs"], Is.EqualTo("0"));
            Assert.That(snapshot[$"{cornerReturn}.variations"], Is.EqualTo("0"));

            Type schemaType = EditorAssembly.GetType("DungeonLab.Editor.DungeonRecipeAsset", true)!;
            foreach (FieldInfo field in schemaType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.That(field.Name, Does.Not.Contain("throne").IgnoreCase);
                Assert.That(field.Name, Does.Not.Contain("vestibule").IgnoreCase);
                Assert.That(field.Name, Does.Not.Contain("corner").IgnoreCase);
            }
        }

        [Test]
        public void FullDungeon_ResolvesAllThreeRecipesThroughCanonicalConsumers()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildPhase5FullDungeonSnapshot");
            string throne = RecipePrefix(snapshot, "episode_throne_twin_stairs_01");
            string vestibule = RecipePrefix(snapshot, "connector_flexible_vestibule_01");
            string cornerReturn = RecipePrefix(snapshot, "connector_corner_return_01");

            Assert.That(snapshot["accepted"], Is.EqualTo("true"));
            Assert.That(snapshot["validation.passed"], Is.EqualTo("true"));
            Assert.That(snapshot["validation.recipes"], Is.EqualTo("true"));
            Assert.That(snapshot["recipes.count"], Is.EqualTo("3"));
            Assert.That(snapshot[$"{throne}.atomic"], Is.EqualTo("true"));
            Assert.That(snapshot[$"{vestibule}.atomic"], Is.EqualTo("true"));
            Assert.That(snapshot[$"{cornerReturn}.atomic"], Is.EqualTo("true"));
            Assert.That(snapshot[$"{throne}.transitions"], Is.EqualTo("2"));
            Assert.That(snapshot[$"{vestibule}.transitions"], Is.EqualTo("1"));
            Assert.That(snapshot[$"{cornerReturn}.transitions"], Is.EqualTo("1"));
            Assert.That(int.Parse(snapshot[$"{throne}.protected"]), Is.GreaterThan(0));
            Assert.That(int.Parse(snapshot[$"{vestibule}.protected"]), Is.GreaterThan(0));
            Assert.That(int.Parse(snapshot[$"{cornerReturn}.protected"]), Is.GreaterThan(0));
        }

        [Test]
        public void RendererAbyssAndCollision_ConsumeAllRecipesWithoutRepair()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildPhase5FullDungeonSnapshot");

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
        public void ValidationIsNonMutating_StaleReviewIsExcluded_AndInvalidCannotPromote()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildPhase5LifecycleSnapshot");

            Assert.That(snapshot["validation.passed"], Is.EqualTo("True"));
            Assert.That(snapshot["validation.nonMutating"], Is.EqualTo("True"));
            Assert.That(snapshot["stale.detected"], Is.EqualTo("True"));
            Assert.That(snapshot["stale.eligible"], Is.EqualTo("False"));
            Assert.That(snapshot["invalid.promoted"], Is.EqualTo("False"));
            Assert.That(snapshot["invalid.structurePassed"], Is.EqualTo("False"));
            Assert.That(snapshot["draft.promoted"], Is.EqualTo("True"));
            Assert.That(snapshot["draft.allLayersPassed"], Is.EqualTo("True"));
            Assert.That(snapshot["draft.reviewCurrent"], Is.EqualTo("True"));
            Assert.That(snapshot["draft.reviewMetadataRecorded"], Is.EqualTo("True"));
            Assert.That(snapshot["draft.ordinaryGenerationEligible"], Is.EqualTo("True"));
        }

        [Test]
        public void AuthoringGallery_IsDeterministicAndCoversRequiredWorkflowViews()
        {
            Dictionary<string, string> snapshot = Snapshot("BuildPhase5WorkflowSnapshot");

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
        public void FixedSeed_RecipeResolutionsRemainDeterministic()
        {
            string first = Invoke("BuildPhase5FullDungeonSnapshot");
            string second = Invoke("BuildPhase5FullDungeonSnapshot");
            Assert.That(first, Is.EqualTo(second));
        }

        private static Dictionary<string, string> Snapshot(string methodName)
        {
            return Parse(Invoke(methodName));
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
            for (int index = 0; index < 8; index++)
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
            MethodInfo method = GeneratorType.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, $"Missing diagnostic method {methodName}.");
            return (string)method.Invoke(null, new object[] { Seed })!;
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

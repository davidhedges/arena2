#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabStairBoundaryCompatibilityTests
    {
        private const int RegressionSeed = 2062860779;
        private const string TargetStairName =
            "transition_stair_curved_stair_180_R_bridge_d4_18_14_to_19_14";
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        private GameObject? root;
        private object? buildReport;

        [OneTimeSetUp]
        public void BuildRegressionSeed()
        {
            MethodInfo method = GeneratorType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(candidate =>
                    candidate.Name == "BuildPhase0RenderedSeed" &&
                    candidate.GetParameters().Length == 4);
            object?[] arguments = { RegressionSeed, null, null, null };
            root = (GameObject?)method.Invoke(null, arguments);
            buildReport = arguments[3];

            Assert.That(root, Is.Not.Null, $"Seed {RegressionSeed} did not produce a rendered dungeon.");
            Assert.That(buildReport, Is.Not.Null, $"Seed {RegressionSeed} did not produce a renderer report.");
        }

        [OneTimeTearDown]
        public void DestroyRegressionSeed()
        {
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CurvedBridgeRegression_DoesNotPlaceTierCornerKitInsideItsFootprint()
        {
            Transform[] transforms = AllTransforms();

            Assert.That(
                transforms.Any(transform => transform.name == TargetStairName),
                Is.True,
                "The regression must keep the valid curved bridge stair rather than hiding the conflict by dropping it.");
            Assert.That(
                transforms.Any(transform =>
                    transform.name.StartsWith("tier_corner_18_13_c", StringComparison.Ordinal)),
                Is.False,
                "A round/angle tier corner must not replace the stair-owned square wall faces.");
            Assert.That(
                ReportInt("rejected"),
                Is.Zero,
                "The corrected boundary composition should render without rejecting a valid stair.");
        }

        [Test]
        public void CurvedBridgeRegression_PreservesSquareStructuralWallsAtTheFormerCorner()
        {
            Transform walls = root!.transform.Find("Elevation Edge Walls");
            Assert.That(walls, Is.Not.Null);

            string[] wallNames = walls.GetComponentsInChildren<Transform>(includeInactive: true)
                .Select(transform => transform.name)
                .ToArray();
            Assert.That(
                wallNames.Any(name =>
                    name.StartsWith("cliff_", StringComparison.Ordinal) &&
                    (name.Contains("_18_14_", StringComparison.Ordinal) ||
                     name.Contains("_17_13_", StringComparison.Ordinal))),
                Is.True,
                "The fix must retain the square support faces instead of carving away the bridge landing wall.");
        }

        [Test]
        public void CurvedBridgeRegression_SquareSupportDoesNotOverlapTheStair()
        {
            Transform stair = AllTransforms().Single(transform => transform.name == TargetStairName);
            Transform walls = root!.transform.Find("Elevation Edge Walls");
            Collider[] stairColliders = stair.GetComponentsInChildren<Collider>(includeInactive: true);
            Collider[] formerCornerWalls = walls.GetComponentsInChildren<Collider>(includeInactive: true)
                .Where(collider =>
                    collider.transform.name.Contains("_18_14_", StringComparison.Ordinal) ||
                    collider.transform.name.Contains("_17_13_", StringComparison.Ordinal))
                .ToArray();

            Assert.That(formerCornerWalls.Length, Is.GreaterThan(0));
            string[] overlaps =
                (from stairCollider in stairColliders
                 from wallCollider in formerCornerWalls
                 where BoundsOverlapWithPositiveVolume(stairCollider.bounds, wallCollider.bounds)
                 select $"{stairCollider.transform.name}/{wallCollider.transform.name}")
                .ToArray();
            Assert.That(
                overlaps,
                Is.Empty,
                $"The restored square support walls overlap the curved bridge: {string.Join(", ", overlaps)}");
        }

        [Test]
        public void AngledAndRoundedTierGeometryAwayFromStairOwnedEdges_RemainsEligible()
        {
            Transform corners = root!.transform.Find("Elevation Edge Corners");
            Assert.That(corners, Is.Not.Null);

            Transform[] tierCornerRoots = corners.Cast<Transform>()
                .Where(transform =>
                    transform.name.StartsWith("tier_corner_", StringComparison.Ordinal))
                .ToArray();
            Assert.That(
                tierCornerRoots.Length,
                Is.GreaterThan(0),
                "The stair-local compatibility rule must not globally disable angled and rounded tier geometry.");
            Assert.That(
                tierCornerRoots.Any(transform =>
                {
                    string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject);
                    return path.Contains("_angle_", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("_convex_", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("_concave_", StringComparison.OrdinalIgnoreCase);
                }),
                Is.True,
                "At least one unrelated reviewed angle/round corner kit should remain in the accepted dungeon.");
        }

        [Test]
        public void CornerCompatibility_DoesNotInferStairGeometryFromAssetNames()
        {
            string source = File.ReadAllText(
                "Assets/Arena/Editor/Dungeons/RandomDungeon/ElevationEdgeModel.cs");

            Assert.That(source, Does.Not.Contain("IndexOf(\"curve\""));
            Assert.That(source, Does.Contain("[STAIR_BOUNDARY_CONFLICT]"));
        }

        private Transform[] AllTransforms()
        {
            return root!.GetComponentsInChildren<Transform>(includeInactive: true);
        }

        private int ReportInt(string fieldName)
        {
            FieldInfo field = buildReport!.GetType().GetField(fieldName)!;
            Assert.That(field, Is.Not.Null, $"Renderer report did not contain '{fieldName}'.");
            return (int)field.GetValue(buildReport)!;
        }

        private static bool BoundsOverlapWithPositiveVolume(Bounds first, Bounds second)
        {
            Vector3 size = Vector3.Min(first.max, second.max) - Vector3.Max(first.min, second.min);
            return size.x > 0.01f && size.y > 0.01f && size.z > 0.01f;
        }
    }
}

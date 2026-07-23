#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
        public void OuterShellCorners_UseTheStraightWallsPivotMiddleFamily()
        {
            Transform shells = root!.transform.Find("Outer Shell Walls");
            Assert.That(shells, Is.Not.Null);

            (Transform transform, string path)[] shellPieces = shells.Cast<Transform>()
                .Where(transform => transform.name.StartsWith("shell_", StringComparison.Ordinal))
                .Select(transform => (
                    transform,
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject)))
                .ToArray();

            Assert.That(shellPieces, Is.Not.Empty);
            Assert.That(
                shellPieces.All(piece =>
                    piece.path.Contains("/PivotMiddle/", StringComparison.Ordinal) &&
                    Path.GetFileNameWithoutExtension(piece.path)
                        .StartsWith("COMP_Wall_01_M_", StringComparison.Ordinal)),
                Is.True,
                "Above-floor straight and corner shells must use one PivotMiddle/M component family.");
            Assert.That(
                shellPieces.Any(piece => piece.path.Contains("_straight_", StringComparison.Ordinal)),
                Is.True,
                "The regression seed must exercise straight shell pieces.");
            Assert.That(
                shellPieces.Any(piece => piece.path.Contains("_angle_1_", StringComparison.Ordinal)),
                Is.True,
                "The regression seed must exercise angled shell pieces.");
            Assert.That(
                shellPieces.Any(piece => piece.path.Contains("_concave_", StringComparison.Ordinal)),
                Is.True,
                "The regression seed must exercise the centered curve used by rounded shell corners.");
        }

        [Test]
        public void AngledOuterShells_RemapTheFullCellPivotForTheirCorrectedYaw()
        {
            Transform shells = root!.transform.Find("Outer Shell Walls");
            Transform corners = root.transform.Find("Elevation Edge Corners");
            Assert.That(shells, Is.Not.Null);
            Assert.That(corners, Is.Not.Null);

            Transform[] angledShells = shells.Cast<Transform>()
                .Where(transform =>
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject)
                        .Contains("_M_angle_1_", StringComparison.Ordinal))
                .ToArray();
            Assert.That(angledShells, Is.Not.Empty, "The regression seed must exercise angled outer shells.");

            var exercisedStructuralYaws = new bool[4];
            foreach (Transform angledShell in angledShells)
            {
                Match match = Regex.Match(
                    angledShell.name,
                    @"^shell_corner_(-?\d+)_(-?\d+)_\d+$",
                    RegexOptions.CultureInvariant);
                Assert.That(match.Success, Is.True, $"Unexpected angled-shell name '{angledShell.name}'.");

                string tierCornerName = $"tier_corner_{match.Groups[1].Value}_{match.Groups[2].Value}_c0";
                Transform tierCorner = corners.Find(tierCornerName);
                Assert.That(
                    tierCorner,
                    Is.Not.Null,
                    $"Angled shell '{angledShell.name}' has no matching structural corner '{tierCornerName}'.");
                exercisedStructuralYaws[
                    Mathf.RoundToInt(Mathf.Repeat(tierCorner.eulerAngles.y, 360f) / 90f) & 3] = true;
                Assert.That(
                    Mathf.Abs(Mathf.DeltaAngle(angledShell.eulerAngles.y, tierCorner.eulerAngles.y)),
                    Is.EqualTo(180f).Within(0.01f),
                    "The PivotMiddle angled shell must face exactly opposite the structural tier-corner kit.");

                Vector3 expectedShellPosition =
                    tierCorner.position +
                    tierCorner.rotation * new Vector3(-4f, 0f, 4f);
                Assert.That(
                    Vector2.Distance(
                        new Vector2(angledShell.position.x, angledShell.position.z),
                        new Vector2(expectedShellPosition.x, expectedShellPosition.z)),
                    Is.LessThan(0.001f),
                    "The 180-degree angle correction must remap the (-x,+z) full-cell pivot, " +
                    "not rotate the shell around the structural corner's old root.");
            }

            Assert.That(
                exercisedStructuralYaws.All(exercised => exercised),
                Is.True,
                "The regression seed must prove the angled-shell pivot remap in all four cardinal orientations.");
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

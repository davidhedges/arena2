#nullable enable

using System;
using System.Collections.Generic;
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
        private const int RoundedCornerRegressionSeed = 2026072100;
        private const int North = 1;
        private const int East = 2;
        private const int South = 4;
        private const int West = 8;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Type ElevationEdgeModelType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.ElevationEdgeModel", throwOnError: true)!;

        private GameObject? root;
        private GameObject? roundedCornerRoot;
        private object? buildReport;

        [OneTimeSetUp]
        public void BuildRegressionSeed()
        {
            MethodInfo method = GeneratorType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(candidate =>
                    candidate.Name == "BuildRenderedSeed" &&
                    candidate.GetParameters().Length == 4);
            object?[] arguments = { RegressionSeed, null, null, null };
            root = (GameObject?)method.Invoke(null, arguments);
            buildReport = arguments[3];

            Assert.That(root, Is.Not.Null, $"Seed {RegressionSeed} did not produce a rendered dungeon.");
            Assert.That(buildReport, Is.Not.Null, $"Seed {RegressionSeed} did not produce a renderer report.");
            root!.name = "Stair Boundary Regression Probe";

            object?[] roundedCornerArguments = { RoundedCornerRegressionSeed, null, null, null };
            roundedCornerRoot = (GameObject?)method.Invoke(null, roundedCornerArguments);
            Assert.That(
                roundedCornerRoot,
                Is.Not.Null,
                $"Seed {RoundedCornerRegressionSeed} did not produce a rounded-corner regression dungeon.");
            roundedCornerRoot!.name = "Rounded Corner Regression Probe";
        }

        [OneTimeTearDown]
        public void DestroyRegressionSeed()
        {
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
            if (roundedCornerRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(roundedCornerRoot);
            }
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
                shellPieces.Any(piece =>
                    piece.path.Contains("_angle_1_", StringComparison.Ordinal) ||
                    piece.path.Contains("_angle_2_", StringComparison.Ordinal)),
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
                {
                    string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject);
                    return path.Contains("_M_angle_1_", StringComparison.Ordinal) ||
                        path.Contains("_M_angle_2_", StringComparison.Ordinal);
                })
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
        public void RoundedOuterShells_PreserveConvexAndConcaveOrientationInScreenshotSeed()
        {
            Transform shells = roundedCornerRoot!.transform.Find("Outer Shell Walls");
            Transform corners = roundedCornerRoot.transform.Find("Elevation Edge Corners");
            Assert.That(shells, Is.Not.Null);
            Assert.That(corners, Is.Not.Null);

            Transform[] roundedShells = shells.Cast<Transform>()
                .Where(transform =>
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject)
                        .Contains("_M_concave_", StringComparison.Ordinal))
                .ToArray();
            Assert.That(roundedShells, Is.Not.Empty, "The regression seed must exercise rounded outer shells.");

            int convexCount = 0;
            int concaveCount = 0;
            foreach (Transform roundedShell in roundedShells)
            {
                Match match = Regex.Match(
                    roundedShell.name,
                    @"^shell_corner_(-?\d+)_(-?\d+)_\d+$",
                    RegexOptions.CultureInvariant);
                Assert.That(match.Success, Is.True, $"Unexpected rounded-shell name '{roundedShell.name}'.");

                string cell = $"{match.Groups[1].Value}_{match.Groups[2].Value}";
                Transform tierCorner = corners.Find($"tier_corner_{cell}_c0");
                Assert.That(
                    tierCorner,
                    Is.Not.Null,
                    $"Rounded shell '{roundedShell.name}' has no matching structural corner.");

                bool concave = corners.Find($"tier_corner_floor_{cell}") != null;
                if (concave)
                {
                    concaveCount++;
                }
                else
                {
                    convexCount++;
                }

                float expectedYawOffset = concave ? 0f : 180f;
                Assert.That(
                    Mathf.Abs(Mathf.DeltaAngle(roundedShell.eulerAngles.y, tierCorner.eulerAngles.y)),
                    Is.EqualTo(expectedYawOffset).Within(0.01f),
                    $"The shared PivotMiddle concave shell must keep the calibrated concave yaw and " +
                    $"flip only convex uses; '{roundedShell.name}' was classified " +
                    $"{(concave ? "concave" : "convex")}.");

                Vector3 expectedShellPosition = concave
                    ? tierCorner.position
                    : tierCorner.position + tierCorner.rotation * new Vector3(-4f, 0f, 4f);
                Assert.That(
                    Vector2.Distance(
                        new Vector2(roundedShell.position.x, roundedShell.position.z),
                        new Vector2(expectedShellPosition.x, expectedShellPosition.z)),
                    Is.LessThan(0.001f),
                    $"Rounded shell '{roundedShell.name}' must recompute the full-cell pivot when its yaw flips.");
            }

            Assert.That(
                convexCount,
                Is.GreaterThan(0),
                "The integration seed must exercise convex rounded-shell placement.");
            Assert.That(
                concaveCount,
                Is.GreaterThan(0),
                "The integration seed must exercise concave rounded-shell placement.");
        }

        [Test]
        public void OuterShellCornerYawContract_CoversBothRoundPolaritiesAndAnglesAtEveryCardinalYaw()
        {
            MethodInfo method = ElevationEdgeModelType.GetMethod(
                "CalculateOuterShellCornerYaw",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null);

            foreach (float structuralYaw in new[] { 0f, 90f, 180f, 270f })
            {
                Assert.That(
                    InvokeOuterShellCornerYaw(method, structuralYaw, angleStyle: false, concave: true),
                    Is.EqualTo(structuralYaw).Within(0.01f),
                    $"Concave rounded shell at structural yaw {structuralYaw} must keep its calibrated orientation.");
                Assert.That(
                    InvokeOuterShellCornerYaw(method, structuralYaw, angleStyle: false, concave: false),
                    Is.EqualTo(Mathf.Repeat(structuralYaw + 180f, 360f)).Within(0.01f),
                    $"Convex use of the shared concave shell at structural yaw {structuralYaw} must flip 180 degrees.");

                float expectedAngleYaw = Mathf.Repeat(structuralYaw + 180f, 360f);
                Assert.That(
                    InvokeOuterShellCornerYaw(method, structuralYaw, angleStyle: true, concave: true),
                    Is.EqualTo(expectedAngleYaw).Within(0.01f),
                    $"Concave angle shell at structural yaw {structuralYaw} must preserve its existing flip.");
                Assert.That(
                    InvokeOuterShellCornerYaw(method, structuralYaw, angleStyle: true, concave: false),
                    Is.EqualTo(expectedAngleYaw).Within(0.01f),
                    $"Convex angle shell at structural yaw {structuralYaw} must preserve its existing flip.");
            }
        }

        [Test]
        public void AngledOuterShells_UseAuthoredVariantForCornerPolarity()
        {
            Transform shells = root!.transform.Find("Outer Shell Walls");
            Transform corners = root.transform.Find("Elevation Edge Corners");
            Assert.That(shells, Is.Not.Null);
            Assert.That(corners, Is.Not.Null);

            Transform[] angledShells = shells.Cast<Transform>()
                .Where(transform =>
                {
                    string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject);
                    return path.Contains("_M_angle_1_", StringComparison.Ordinal) ||
                        path.Contains("_M_angle_2_", StringComparison.Ordinal);
                })
                .ToArray();
            Assert.That(angledShells, Is.Not.Empty, "The regression seed must exercise angled outer shells.");

            foreach (Transform angledShell in angledShells)
            {
                Match match = Regex.Match(
                    angledShell.name,
                    @"^shell_corner_(-?\d+)_(-?\d+)_\d+$",
                    RegexOptions.CultureInvariant);
                Assert.That(match.Success, Is.True, $"Unexpected angled-shell name '{angledShell.name}'.");

                string cell = $"{match.Groups[1].Value}_{match.Groups[2].Value}";
                bool concave = corners.Find($"tier_corner_floor_{cell}") != null;
                string expectedVariant = concave ? "_M_angle_2_" : "_M_angle_1_";
                string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(angledShell.gameObject);
                Assert.That(
                    path,
                    Does.Contain(expectedVariant),
                    $"Angled shell '{angledShell.name}' must use {expectedVariant.Trim('_')} for its " +
                    $"{(concave ? "concave" : "convex")} authored endpoint profile.");
            }

            Assert.That(
                angledShells.Select(transform =>
                        PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject))
                    .Any(path => path.Contains("_M_angle_1_", StringComparison.Ordinal)),
                Is.True,
                "The regression seed must exercise angle_1.");
            Assert.That(
                angledShells.Select(transform =>
                        PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject))
                    .Any(path => path.Contains("_M_angle_2_", StringComparison.Ordinal)),
                Is.True,
                "The regression seed must exercise angle_2.");
        }

        [Test]
        public void ConsecutiveAngledOuterShells_AlternateCornerPolarityAndPrefabVariant()
        {
            Transform shells = root!.transform.Find("Outer Shell Walls");
            Transform corners = root.transform.Find("Elevation Edge Corners");
            Assert.That(shells, Is.Not.Null);
            Assert.That(corners, Is.Not.Null);

            var courseZeroAngles = shells.Cast<Transform>()
                .Select(transform => (
                    transform,
                    match: Regex.Match(
                        transform.name,
                        @"^shell_corner_(-?\d+)_(-?\d+)_0$",
                        RegexOptions.CultureInvariant),
                    path: PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject)))
                .Where(item =>
                    item.match.Success &&
                    (item.path.Contains("_M_angle_1_", StringComparison.Ordinal) ||
                     item.path.Contains("_M_angle_2_", StringComparison.Ordinal)))
                .Select(item => (
                    item.transform,
                    cell: new Vector2Int(
                        int.Parse(item.match.Groups[1].Value),
                        int.Parse(item.match.Groups[2].Value)),
                    item.path))
                .ToArray();

            int consecutivePairs = 0;
            for (int firstIndex = 0; firstIndex < courseZeroAngles.Length; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1; secondIndex < courseZeroAngles.Length; secondIndex++)
                {
                    var first = courseZeroAngles[firstIndex];
                    var second = courseZeroAngles[secondIndex];
                    Vector2Int delta = second.cell - first.cell;
                    if (Mathf.Abs(delta.x) != 1 ||
                        Mathf.Abs(delta.y) != 1 ||
                        Mathf.Abs(first.transform.position.y - second.transform.position.y) > 0.01f ||
                        Mathf.Abs(Mathf.DeltaAngle(first.transform.eulerAngles.y, second.transform.eulerAngles.y)) > 0.01f)
                    {
                        continue;
                    }

                    bool firstConcave = corners.Find($"tier_corner_floor_{first.cell.x}_{first.cell.y}") != null;
                    bool secondConcave = corners.Find($"tier_corner_floor_{second.cell.x}_{second.cell.y}") != null;
                    Assert.That(
                        firstConcave,
                        Is.Not.EqualTo(secondConcave),
                        $"Consecutive angled shells {first.transform.name} and {second.transform.name} must alternate corner polarity.");
                    Assert.That(
                        first.path.Contains("_M_angle_2_", StringComparison.Ordinal),
                        Is.Not.EqualTo(second.path.Contains("_M_angle_2_", StringComparison.Ordinal)),
                        $"Consecutive angled shells {first.transform.name} and {second.transform.name} must alternate prefab variants.");
                    consecutivePairs++;
                }
            }

            Assert.That(
                consecutivePairs,
                Is.GreaterThan(0),
                "The regression seed must exercise at least one consecutive angled-shell pair.");
        }

        [Test]
        public void LandingShellContinuity_PrunesParallelSingletonsAtOneCellLanding()
        {
            var landing = new Vector2Int(23, 10);
            var shellEdges = new List<(Vector2Int cell, int direction, int higherLevel)>
            {
                (landing, West, 24),
                (landing, East, 24)
            };

            HashSet<(Vector2Int cell, int direction, int higherLevel)> orphanEdges =
                FindOrphanLandingShellEdges(shellEdges, landing);

            Assert.That(
                orphanEdges,
                Is.EquivalentTo(shellEdges),
                "Parallel walls on opposite sides of a one-cell landing do not form a wall run; both are orphan shells.");
        }

        [Test]
        public void LandingShellContinuity_PrunesLandingOnlyCornerIsland()
        {
            var upperLanding = new Vector2Int(15, 9);
            var shellEdges = new List<(Vector2Int cell, int direction, int higherLevel)>
            {
                (upperLanding, South, 16),
                (upperLanding, East, 16)
            };

            HashSet<(Vector2Int cell, int direction, int higherLevel)> orphanEdges =
                FindOrphanLandingShellEdges(shellEdges, upperLanding);

            Assert.That(
                orphanEdges,
                Is.EquivalentTo(shellEdges),
                "The named walled-well180 regression is a two-edge L component on its upper landing; sharing one corner must not make that island a valid shell run.");
        }

        [Test]
        public void LandingShellContinuity_KeepsLandingWallJoinedToRunOrCorner()
        {
            var landing = new Vector2Int(4, 4);
            var collinearLandingEdge = (cell: landing, direction: North, higherLevel: 12);
            var cornerLandingEdge = (cell: landing, direction: East, higherLevel: 12);
            var unrelatedSingleton = (
                cell: new Vector2Int(20, 20),
                direction: South,
                higherLevel: 12);
            var shellEdges = new List<(Vector2Int cell, int direction, int higherLevel)>
            {
                collinearLandingEdge,
                (new Vector2Int(5, 4), North, 12),
                cornerLandingEdge,
                unrelatedSingleton
            };

            HashSet<(Vector2Int cell, int direction, int higherLevel)> orphanEdges =
                FindOrphanLandingShellEdges(shellEdges, landing);

            Assert.That(orphanEdges.Contains(collinearLandingEdge), Is.False);
            Assert.That(orphanEdges.Contains(cornerLandingEdge), Is.False);
            Assert.That(
                orphanEdges.Contains(unrelatedSingleton),
                Is.False,
                "Singleton pruning must remain scoped to stair landing cells.");
        }

        [Test]
        public void LandingShellContinuity_DifferentElevationDoesNotCreateFalseConnection()
        {
            var landing = new Vector2Int(4, 4);
            var landingEdge = (cell: landing, direction: North, higherLevel: 12);
            var shellEdges = new List<(Vector2Int cell, int direction, int higherLevel)>
            {
                landingEdge,
                (new Vector2Int(5, 4), North, 11)
            };

            HashSet<(Vector2Int cell, int direction, int higherLevel)> orphanEdges =
                FindOrphanLandingShellEdges(shellEdges, landing);

            Assert.That(
                orphanEdges.Contains(landingEdge),
                Is.True,
                "Walls that only touch in plan at different floor elevations are not one continuous shell run.");
        }

        [Test]
        public void OrphanLandingEdge_SuppressesFallbackRailingAndTrim()
        {
            var orphanLandingEdge = (x: 23, z: 10, direction: West);
            var bareLandingEdges = new HashSet<(int x, int z, int direction)>
            {
                orphanLandingEdge
            };

            Assert.That(
                SuppressesGeneratedTopGuard(
                    false,
                    orphanLandingEdge,
                    new HashSet<(int x, int z, int direction)>(),
                    bareLandingEdges),
                Is.True,
                "An orphan landing edge must not replace its pruned shell with a generated railing or trim.");
            Assert.That(
                SuppressesGeneratedTopGuard(
                    false,
                    (24, 10, West),
                    new HashSet<(int x, int z, int direction)>(),
                    bareLandingEdges),
                Is.False,
                "The bare treatment must remain scoped to the exact orphan landing edge.");
        }

        [Test]
        public void BarredGateway_DisablesOnlyMetalDoorLeaf()
        {
            Transform[] barredGateways = AllTransforms()
                .Where(transform => transform.name.StartsWith(
                    "gateway_barred_",
                    StringComparison.Ordinal))
                .ToArray();

            Assert.That(
                barredGateways,
                Is.Not.Empty,
                $"Seed {RegressionSeed} did not produce a barred gateway.");
            foreach (Transform gateway in barredGateways)
            {
                Transform[] descendants =
                    gateway.GetComponentsInChildren<Transform>(includeInactive: true);
                Transform doorAssembly = descendants.Single(transform =>
                    transform.name == "P_MOD_Gateway_Door_01_med_01");
                Transform doorLeaf = descendants.Single(transform =>
                    transform.name == "MOD_Gateway_Door_01_med_01_door");

                Assert.That(
                    doorAssembly.gameObject.activeSelf,
                    Is.True,
                    $"{gateway.name} must preserve the authored metal doorway assembly.");
                Assert.That(
                    doorLeaf.gameObject.activeSelf,
                    Is.False,
                    $"{gateway.name} must suppress only the metal door leaf behind its bars.");
            }
        }

        [TestCase(false, 0, false, 0)]
        [TestCase(true, 4, false, 0)]
        [TestCase(false, 0, true, 4)]
        public void GatewayFlankInvariant_RejectsMissingWallPairs(
            bool hasFirstFlank,
            int firstHeight,
            bool hasSecondFlank,
            int secondHeight)
        {
            (bool accepted, int wallHeight) = TryResolveGatewayWallHeight(
                hasFirstFlank,
                firstHeight,
                hasSecondFlank,
                secondHeight);

            Assert.That(accepted, Is.False);
            Assert.That(
                wallHeight,
                Is.Zero,
                "A rejected gateway candidate must not receive a fabricated wall height.");
        }

        [TestCase(4)]
        [TestCase(6)]
        [TestCase(8)]
        [TestCase(10)]
        [TestCase(12)]
        public void GatewayFlankInvariant_AcceptsEqualSupportedWallPairs(int height)
        {
            (bool accepted, int wallHeight) = TryResolveGatewayWallHeight(
                true,
                height,
                true,
                height);

            Assert.That(accepted, Is.True);
            Assert.That(wallHeight, Is.EqualTo(height));
        }

        [Test]
        public void GatewaySocketResolver_SkipsBroadAndPartialEdgesForNearestTwoFlankThroat()
        {
            List<Vector2Int> path = StraightPath(0, 4);
            Dictionary<Vector2Int, int> levels = LevelPath(path, 4);
            var supports =
                new Dictionary<string, (int baseLevel, int heightUnits)>
                {
                    [EdgeKey(new Vector2Int(2, 0), new Vector2Int(2, 1))] =
                        (4, 4),
                    [EdgeKey(new Vector2Int(3, 0), new Vector2Int(3, 1))] =
                        (4, 4),
                    [EdgeKey(new Vector2Int(3, 0), new Vector2Int(3, -1))] =
                        (4, 4)
                };

            (bool accepted, string socketEdge, _, _, int distance, int height) =
                ResolveGatewaySocket(path, levels, supports);

            Assert.That(accepted, Is.True);
            Assert.That(
                socketEdge,
                Is.EqualTo(EdgeKey(path[2], path[3])),
                "The 0/2 room threshold and next 1/2 path edge must remain open; the nearest 2/2 corridor throat owns the socket.");
            Assert.That(distance, Is.EqualTo(2));
            Assert.That(height, Is.EqualTo(4));
        }

        [Test]
        public void GatewaySocketResolver_KeepsRoomThresholdDistinctFromMovedSocket()
        {
            List<Vector2Int> path = StraightPath(10, 14);
            Dictionary<Vector2Int, int> levels = LevelPath(path, 8);
            var supports =
                new Dictionary<string, (int baseLevel, int heightUnits)>
                {
                    [EdgeKey(new Vector2Int(13, 0), new Vector2Int(13, 1))] =
                        (8, 6),
                    [EdgeKey(new Vector2Int(13, 0), new Vector2Int(13, -1))] =
                        (8, 6)
                };
            string roomThreshold = EdgeKey(path[0], path[1]);

            (bool accepted, string socketEdge, _, _, _, _) =
                ResolveGatewaySocket(path, levels, supports);

            Assert.That(accepted, Is.True);
            Assert.That(roomThreshold, Is.EqualTo("10,0|11,0"));
            Assert.That(socketEdge, Is.EqualTo("12,0|13,0"));
            Assert.That(socketEdge, Is.Not.EqualTo(roomThreshold));
        }

        // Owner ruling 2026-07-26: unequal flanks are allowed, and the SHORTER
        // side sets the opening so the door fits inside the lower wall.
        [Test]
        public void GatewaySocketResolver_TakesTheShorterOfUnequalFlankHeights()
        {
            List<Vector2Int> path = StraightPath(0, 3);
            Dictionary<Vector2Int, int> levels = LevelPath(path, 4);
            var supports =
                new Dictionary<string, (int baseLevel, int heightUnits)>
                {
                    [EdgeKey(new Vector2Int(2, 0), new Vector2Int(2, 1))] =
                        (4, 4),
                    [EdgeKey(new Vector2Int(2, 0), new Vector2Int(2, -1))] =
                        (4, 6)
                };

            (bool accepted, _, _, _, _, int wallHeight) =
                ResolveGatewaySocket(path, levels, supports);

            Assert.That(accepted, Is.True);
            Assert.That(wallHeight, Is.EqualTo(4));
        }

        [Test]
        public void GatewaySocketResolver_ReturnsNoSocketWhenNoTwoFlankThroatExists()
        {
            List<Vector2Int> path = StraightPath(0, 4);
            Dictionary<Vector2Int, int> levels = LevelPath(path, 4);

            Assert.That(
                ResolveGatewaySocket(
                    path,
                    levels,
                    new Dictionary<
                        string,
                        (int baseLevel, int heightUnits)>()).accepted,
                Is.False);
        }

        [Test]
        public void GatewaySocketResolver_RejectsCandidateWithMissingFloor()
        {
            List<Vector2Int> path = StraightPath(0, 4);
            Dictionary<Vector2Int, int> levels = LevelPath(path, 4);
            levels.Remove(new Vector2Int(3, 0));
            Dictionary<string, (int baseLevel, int heightUnits)> supports =
                CorridorFlanks(new Vector2Int(3, 0), 4, 4);

            Assert.That(
                ResolveGatewaySocket(path, levels, supports).accepted,
                Is.False);
        }

        [Test]
        public void GatewaySocketResolver_RejectsStairTransitionAndReservedGeometryConflicts()
        {
            List<Vector2Int> path = StraightPath(0, 4);
            Dictionary<Vector2Int, int> levels = LevelPath(path, 4);
            Dictionary<string, (int baseLevel, int heightUnits)> supports =
                CorridorFlanks(new Vector2Int(3, 0), 4, 4);
            string candidateEdge = EdgeKey(path[2], path[3]);

            Assert.That(
                ResolveGatewaySocket(
                    path,
                    levels,
                    supports,
                    blockedCells: new HashSet<Vector2Int>
                    {
                        path[3]
                    }).accepted,
                Is.False,
                "A stair or reserved cell occupying the throat must block it.");
            Assert.That(
                ResolveGatewaySocket(
                    path,
                    levels,
                    supports,
                    blockedCells: new HashSet<Vector2Int>
                    {
                        new Vector2Int(3, 1)
                    }).accepted,
                Is.False,
                "A stair, transition, or reservation touching a required flank must block the throat.");
            Assert.That(
                ResolveGatewaySocket(
                    path,
                    levels,
                    supports,
                    blockedEdges: new HashSet<string>
                    {
                        candidateEdge
                    }).accepted,
                Is.False,
                "A transition or planned-open edge at the throat must block it.");
        }

        [Test]
        public void GatewaySocketResolver_UsesDeterministicFlankOrientationTieBreak()
        {
            List<Vector2Int> path = StraightPath(0, 2);
            Dictionary<Vector2Int, int> levels = LevelPath(path, 4);
            var supports =
                new Dictionary<string, (int baseLevel, int heightUnits)>
                {
                    [EdgeKey(new Vector2Int(0, 1), new Vector2Int(1, 1))] =
                        (4, 4),
                    [EdgeKey(new Vector2Int(0, -1), new Vector2Int(1, -1))] =
                        (4, 4),
                    [EdgeKey(new Vector2Int(1, 0), new Vector2Int(1, 1))] =
                        (4, 4),
                    [EdgeKey(new Vector2Int(1, 0), new Vector2Int(1, -1))] =
                        (4, 4)
                };

            (
                bool accepted,
                _,
                string firstFlank,
                string secondFlank,
                _,
                _) = ResolveGatewaySocket(path, levels, supports);

            Assert.That(accepted, Is.True);
            Assert.That(
                firstFlank,
                Is.EqualTo("0,1|1,1"));
            Assert.That(
                secondFlank,
                Is.EqualTo("0,-1|1,-1"),
                "When both compatible flank orientations exist, the resolver must always prefer the threshold-plane pair.");
        }

        [Test]
        public void GatewayPlanning_DoesNotSuppressAnEligibleAngledCorner()
        {
            Type platformEdgeType = ElevationEdgeModelType.GetNestedType(
                "PlatformEdge",
                BindingFlags.NonPublic)!;
            Type wallEdgeType = ElevationEdgeModelType.GetNestedType(
                "WallEdge",
                BindingFlags.NonPublic)!;
            Vector2Int cell = FindNonSquareTierCornerCell();
            object northEdge = Activator.CreateInstance(
                platformEdgeType,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic,
                binder: null,
                args: new object[] { cell.x, cell.y, North },
                culture: null)!;
            object eastEdge = Activator.CreateInstance(
                platformEdgeType,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic,
                binder: null,
                args: new object[] { cell.x, cell.y, East },
                culture: null)!;
            object northWall = Activator.CreateInstance(
                wallEdgeType,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic,
                binder: null,
                args: new[] { northEdge, -20, 4, false, false },
                culture: null)!;
            object eastWall = Activator.CreateInstance(
                wallEdgeType,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic,
                binder: null,
                args: new[] { eastEdge, -20, 4, false, false },
                culture: null)!;
            Type wallListType = typeof(List<>).MakeGenericType(wallEdgeType);
            var wallEdges = (System.Collections.IList)Activator.CreateInstance(
                wallListType)!;
            wallEdges.Add(northWall);
            wallEdges.Add(eastWall);
            var levels = new Dictionary<Vector2Int, int>
            {
                [cell] = 4
            };
            MethodInfo findCorners = ElevationEdgeModelType.GetMethod(
                "FindRoundTierCorners",
                BindingFlags.Static | BindingFlags.NonPublic)!;

            // The last two arguments are the stair's own claims — corner
            // selection keeps a corner square where a stair owns the cell or one
            // of the edges it would replace, which is the condition
            // ValidateTierCornerCompatibility used to throw on. This fixture has
            // no stairs, so both are empty and the corner stays eligible.
            Type transitionEdgeType = ElevationEdgeModelType.GetNestedType(
                "TransitionEdge",
                BindingFlags.Public | BindingFlags.NonPublic)!;
            object footprintOwners = Activator.CreateInstance(
                typeof(Dictionary<,>).MakeGenericType(typeof(Vector2Int), transitionEdgeType))!;
            object portEdgeOwners = Activator.CreateInstance(
                typeof(Dictionary<,>).MakeGenericType(
                    typeof(ValueTuple<int, int, int>),
                    transitionEdgeType))!;
            object corners = findCorners.Invoke(
                null,
                new object[]
                {
                    wallEdges,
                    levels,
                    new HashSet<Vector2Int>(),
                    footprintOwners,
                    portEdgeOwners
                })!;

            Assert.That(
                CollectionCount(corners),
                Is.GreaterThan(0),
                "Normal corner selection must remain authoritative; gateway planning cannot reserve raw faces and turn this eligible corner square.");
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

        private static float InvokeOuterShellCornerYaw(
            MethodInfo method,
            float structuralYaw,
            bool angleStyle,
            bool concave)
        {
            return (float)method.Invoke(null, new object[] { structuralYaw, angleStyle, concave })!;
        }

        private static HashSet<(Vector2Int cell, int direction, int higherLevel)> FindOrphanLandingShellEdges(
            IReadOnlyList<(Vector2Int cell, int direction, int higherLevel)> shellEdges,
            params Vector2Int[] landingCells)
        {
            MethodInfo method = ElevationEdgeModelType.GetMethod(
                "FindOrphanLandingShellEdges",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null);
            return (HashSet<(Vector2Int cell, int direction, int higherLevel)>)method.Invoke(
                null,
                new object[]
                {
                    shellEdges,
                    new HashSet<Vector2Int>(landingCells)
                })!;
        }

        private static bool SuppressesGeneratedTopGuard(
            bool wallSuppressesRailing,
            (int x, int z, int direction) edge,
            ISet<(int x, int z, int direction)> shellGuardEdges,
            ISet<(int x, int z, int direction)> bareLandingEdges)
        {
            MethodInfo method = ElevationEdgeModelType.GetMethod(
                "SuppressesGeneratedTopGuard",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(
                null,
                new object[]
                {
                    wallSuppressesRailing,
                    edge,
                    shellGuardEdges,
                    bareLandingEdges
                })!;
        }

        private static (bool accepted, int wallHeight) TryResolveGatewayWallHeight(
            bool hasFirstFlank,
            int firstHeight,
            bool hasSecondFlank,
            int secondHeight)
        {
            MethodInfo method = ElevationEdgeModelType.GetMethod(
                "TryResolveGatewayWallHeight",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null);
            object[] arguments =
            {
                hasFirstFlank,
                firstHeight,
                hasSecondFlank,
                secondHeight,
                0
            };
            bool accepted = (bool)method.Invoke(null, arguments)!;
            return (accepted, (int)arguments[4]);
        }

        private static (
            bool accepted,
            string socketEdge,
            string firstFlank,
            string secondFlank,
            int distance,
            int height) ResolveGatewaySocket(
                IReadOnlyList<Vector2Int> path,
                IReadOnlyDictionary<Vector2Int, int> levels,
                IReadOnlyDictionary<
                    string,
                    (int baseLevel, int heightUnits)> supports,
                ISet<Vector2Int>? blockedCells = null,
                ISet<string>? blockedEdges = null,
                float socketWidth = 4f)
        {
            MethodInfo method = ElevationEdgeModelType.GetMethod(
                "TryResolveGatewaySocket",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null);
            object?[] arguments =
            {
                path,
                levels,
                supports,
                blockedCells ?? new HashSet<Vector2Int>(),
                blockedEdges ?? new HashSet<string>(StringComparer.Ordinal),
                socketWidth,
                null,
                null
            };
            bool accepted = (bool)method.Invoke(null, arguments)!;
            if (!accepted)
            {
                return (false, string.Empty, string.Empty, string.Empty, -1, 0);
            }

            object candidate = arguments[6]!;
            Type candidateType = candidate.GetType();
            return (
                true,
                (string)candidateType.GetField("edgeKey")!.GetValue(candidate)!,
                (string)candidateType.GetField("firstFlankKey")!.GetValue(candidate)!,
                (string)candidateType.GetField("secondFlankKey")!.GetValue(candidate)!,
                (int)candidateType.GetField("pathDistance")!.GetValue(candidate)!,
                (int)candidateType.GetField("wallHeightUnits")!.GetValue(candidate)!);
        }

        private static List<Vector2Int> StraightPath(int firstX, int lastX)
        {
            var path = new List<Vector2Int>();
            for (int x = firstX; x <= lastX; x++)
            {
                path.Add(new Vector2Int(x, 0));
            }
            return path;
        }

        private static Dictionary<Vector2Int, int> LevelPath(
            IEnumerable<Vector2Int> path,
            int level)
        {
            return path.ToDictionary(cell => cell, _ => level);
        }

        private static Dictionary<
            string,
            (int baseLevel, int heightUnits)> CorridorFlanks(
                Vector2Int cell,
                int baseLevel,
                int heightUnits)
        {
            return new Dictionary<
                string,
                (int baseLevel, int heightUnits)>
            {
                [EdgeKey(cell, cell + Vector2Int.up)] =
                    (baseLevel, heightUnits),
                [EdgeKey(cell, cell + Vector2Int.down)] =
                    (baseLevel, heightUnits)
            };
        }

        private static string EdgeKey(Vector2Int first, Vector2Int second)
        {
            if (first.x > second.x ||
                first.x == second.x && first.y > second.y)
            {
                (first, second) = (second, first);
            }

            return $"{first.x},{first.y}|{second.x},{second.y}";
        }

        private static Vector2Int FindNonSquareTierCornerCell()
        {
            MethodInfo chooseStyle = ElevationEdgeModelType.GetMethod(
                "ChooseTierCornerStyle",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            for (int x = 0; x < 32; x++)
            {
                var cell = new Vector2Int(x, 0);
                int style = (int)chooseStyle.Invoke(
                    null,
                    new object[]
                    {
                        cell,
                        new Dictionary<Vector2Int, int>
                        {
                            [cell] = 4
                        }
                    })!;
                if (style != 0)
                {
                    return cell;
                }
            }

            Assert.Fail("Could not find a deterministic non-square tier-corner cell.");
            return default;
        }

        private static int CollectionCount(object collection)
        {
            return (int)collection.GetType()
                .GetProperty("Count")!
                .GetValue(collection)!;
        }

        private static bool BoundsOverlapWithPositiveVolume(Bounds first, Bounds second)
        {
            Vector3 size = Vector3.Min(first.max, second.max) - Vector3.Max(first.min, second.min);
            return size.x > 0.01f && size.y > 0.01f && size.z > 0.01f;
        }
    }

    public sealed class DungeonTrapPlacementTests
    {
        private static readonly Type ElevationEdgeModelType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.ElevationEdgeModel", throwOnError: true)!;

        [Test]
        public void PartialFloorCell_RejectsSpikesWithoutRejectingSawPost()
        {
            var cell = new Vector2Int(4, 7);
            object context = CreateContext(cell, partial: true);

            Assert.That(
                TryResolvePlacement(CreateSettings(spikesWeight: 1, sawPostWeight: 0), context, cell),
                Is.False,
                "A full-cell spike field must not overhang a rounded or chamfered floor tile.");
            Assert.That(
                TryResolvePlacement(CreateSettings(spikesWeight: 0, sawPostWeight: 1), context, cell),
                Is.True,
                "The partial-floor restriction is specific to the full-cell spike field.");
        }

        [Test]
        public void CompleteFloorCell_AcceptsSpikes()
        {
            var cell = new Vector2Int(4, 7);

            Assert.That(
                TryResolvePlacement(
                    CreateSettings(spikesWeight: 1, sawPostWeight: 0),
                    CreateContext(cell, partial: false),
                    cell),
                Is.True);
        }

        private static object CreateSettings(int spikesWeight, int sawPostWeight)
        {
            Type settingsType = ElevationEdgeModelType.GetNestedType(
                "TrapPlacementSettings",
                BindingFlags.Public)!;
            return Activator.CreateInstance(
                settingsType,
                new object[]
                {
                    1234,
                    true,
                    1,
                    1,
                    1,
                    0,
                    spikesWeight,
                    sawPostWeight,
                    0,
                    0
                })!;
        }

        private static object CreateContext(Vector2Int cell, bool partial)
        {
            Type contextType = ElevationEdgeModelType.GetNestedType(
                "TrapPlacementContext",
                BindingFlags.NonPublic)!;
            object context = Activator.CreateInstance(contextType, nonPublic: true)!;
            SetField(context, "levels", new Dictionary<Vector2Int, int> { [cell] = 4 });
            SetField(context, "excluded", new HashSet<Vector2Int>());
            SetField(context, "corridorCells", new HashSet<Vector2Int>());
            SetField(context, "taken", new HashSet<Vector2Int>());
            SetField(
                context,
                "partialFloorCells",
                partial
                    ? new HashSet<Vector2Int> { cell }
                    : new HashSet<Vector2Int>());
            return context;
        }

        private static bool TryResolvePlacement(object settings, object context, Vector2Int cell)
        {
            MethodInfo method = ElevationEdgeModelType.GetMethod(
                "TryResolveTrapPlacement",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Type placementType = ElevationEdgeModelType.GetNestedType(
                "TrapPlacement",
                BindingFlags.NonPublic)!;
            object?[] arguments =
            {
                settings,
                context,
                cell,
                Vector3.zero,
                4f,
                Activator.CreateInstance(placementType)
            };
            return (bool)method.Invoke(null, arguments)!;
        }

        private static void SetField(object target, string fieldName, object value)
        {
            target.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(target, value);
        }
    }
}

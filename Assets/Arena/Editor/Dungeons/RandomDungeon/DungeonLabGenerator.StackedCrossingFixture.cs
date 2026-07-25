using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // Focused optional-crossing evidence only. Production generation continues
    // to use AddAerialBridges; this fixture supplies deterministic eligible
    // geometry to that existing method and inspects its normal renderer/export.
    internal sealed partial class DungeonLabGenerator
    {
        private sealed class StackedCrossingFixture
        {
            public Dictionary<Vector2Int, int> levels;
            public List<ElevationEdgeModel.TransitionEdge> transitions;
            public Dictionary<Vector2Int, int> spanDeckLevels;
            public Vector2Int stackedCell;
            public Vector2Int lowerStart;
            public Vector2Int lowerEnd;
            public ElevationEdgeModel.TransitionEdge bridge;
            public GameObject root;
            public ElevationEdgeModel.BuildReport buildReport;
            public bool lowerRouteTraversable;
            public bool upperBridgeTraversable;
            public bool positiveHeadroomPassed;
            public bool negativeHeadroomRejected;
            public bool lowerClearanceOpen;
            public List<string> lowerSurfaceColliderNames = new List<string>();
            public List<string> upperSurfaceColliderNames = new List<string>();
        }

        private static string BuildStackedCrossingSnapshot()
        {
            StackedCrossingFixture fixture = null;
            try
            {
                fixture = BuildStackedCrossingFixture();
                return string.Join("\n", new[]
                {
                    $"fixture.transitionCount={fixture.transitions.Count}",
                    $"fixture.placementClass={fixture.bridge.placementClass}",
                    $"fixture.stackedCoordinateCount={fixture.bridge.footprintCells.Count(cell => fixture.levels.ContainsKey(cell))}",
                    $"fixture.lowerTraversable={fixture.lowerRouteTraversable}",
                    $"fixture.upperTraversable={fixture.upperBridgeTraversable}",
                    $"fixture.positiveHeadroom={fixture.positiveHeadroomPassed}",
                    $"fixture.negativeHeadroomRejected={fixture.negativeHeadroomRejected}",
                    $"fixture.rendererRejected={fixture.buildReport.rejected}",
                    $"fixture.lowerClearanceOpen={fixture.lowerClearanceOpen}",
                    $"fixture.lowerSurfaceColliders={fixture.lowerSurfaceColliderNames.Count}",
                    $"fixture.upperSurfaceColliders={fixture.upperSurfaceColliderNames.Count}"
                });
            }
            finally
            {
                if (fixture?.root != null)
                    DestroyImmediate(fixture.root);
            }
        }

        private static StackedCrossingFixture BuildStackedCrossingFixture()
        {
            RoomFootprint west = RoomFootprint.FromRect(new RectInt(-4, -1, 2, 3));
            RoomFootprint east = RoomFootprint.FromRect(new RectInt(3, -1, 2, 3));
            var floorCells = new HashSet<Vector2Int>();
            floorCells.UnionWith(west.cells);
            floorCells.UnionWith(east.cells);
            var levels = new Dictionary<Vector2Int, int>();
            foreach (Vector2Int cell in floorCells)
                levels[cell] = 4;

            Vector2Int lowerStart = new Vector2Int(0, -4);
            Vector2Int lowerEnd = new Vector2Int(0, 4);
            for (int y = lowerStart.y; y <= lowerEnd.y; y++)
            {
                var cell = new Vector2Int(0, y);
                floorCells.Add(cell);
                levels[cell] = 0;
            }

            var layout = new DungeonLayout(
                floorCells,
                new List<RoomFootprint> { west, east },
                new List<RoomConnection>());
            var transitions = new List<ElevationEdgeModel.TransitionEdge>();
            var spanDeckLevels = new Dictionary<Vector2Int, int>();
            AddAerialBridges(
                layout,
                levels,
                new System.Random(7),
                transitions,
                new HashSet<string>(),
                new StairPlacementLedger(),
                spanDeckLevels,
                new List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)>(),
                new HashSet<Vector2Int>());
            ElevationEdgeModel.TransitionEdge bridge = transitions.Single(transition =>
                string.Equals(
                    transition.placementClass,
                    ExternalSpanStairPlacementClass,
                    StringComparison.Ordinal));
            List<Vector2Int> stackedCells = bridge.footprintCells
                .Where(cell => levels.TryGetValue(cell, out int lowerLevel) &&
                    spanDeckLevels.TryGetValue(cell, out int deckLevel) &&
                    deckLevel - lowerLevel >= MinHeadroomLevels)
                .ToList();
            if (stackedCells.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Focused fixture expected one exact stacked coordinate; found {stackedCells.Count}.");
            }

            bool positiveHeadroom = TryValidateSpanHeadroom(
                levels,
                spanDeckLevels,
                out _);
            var negativeLevels = new Dictionary<Vector2Int, int>(levels)
            {
                [stackedCells[0]] = spanDeckLevels[stackedCells[0]] - 2
            };
            bool negativeRejected = !TryValidateSpanHeadroom(
                negativeLevels,
                spanDeckLevels,
                out _);
            bool lowerTraversable = EqualLevelPathExists(
                levels,
                lowerStart,
                lowerEnd,
                0);
            var upperLevels = new Dictionary<Vector2Int, int>();
            foreach (Vector2Int cell in west.cells)
                upperLevels[cell] = 4;
            foreach (Vector2Int cell in east.cells)
                upperLevels[cell] = 4;
            bool upperGraphBuilt = TryBuildFloorStairPortGraph(
                upperLevels,
                transitions,
                out FloorStairPortGraph upperGraph,
                out _);
            bool upperTraversable = upperGraphBuilt && upperGraph.IsGloballyConnected(out _);

            GameObject root = ElevationEdgeModel.BuildLevelField(
                Vector3.zero,
                levels,
                transitions,
                null,
                null,
                null,
                null,
                "Corrective Stacked Crossing Fixture",
                out ElevationEdgeModel.BuildReport buildReport,
                out _);
            Physics.SyncTransforms();
            CollectStackedSurfaceEvidence(
                root,
                stackedCells[0],
                spanDeckLevels[stackedCells[0]],
                buildReport.levelHeight,
                out bool lowerClearanceOpen,
                out List<string> lowerSurfaceColliders,
                out List<string> upperSurfaceColliders);

            return new StackedCrossingFixture
            {
                levels = levels,
                transitions = transitions,
                spanDeckLevels = spanDeckLevels,
                stackedCell = stackedCells[0],
                lowerStart = lowerStart,
                lowerEnd = lowerEnd,
                bridge = bridge,
                root = root,
                buildReport = buildReport,
                lowerRouteTraversable = lowerTraversable,
                upperBridgeTraversable = upperTraversable,
                positiveHeadroomPassed = positiveHeadroom,
                negativeHeadroomRejected = negativeRejected,
                lowerClearanceOpen = lowerClearanceOpen,
                lowerSurfaceColliderNames = lowerSurfaceColliders,
                upperSurfaceColliderNames = upperSurfaceColliders
            };
        }

        private static void CollectStackedSurfaceEvidence(
            GameObject root,
            Vector2Int stackedCell,
            int deckLevel,
            float levelHeight,
            out bool lowerClearanceOpen,
            out List<string> lowerSurfaceColliderNames,
            out List<string> upperSurfaceColliderNames)
        {
            Vector3 center = new Vector3(
                (stackedCell.x + 0.5f) * 4f,
                0f,
                (stackedCell.y + 0.5f) * 4f);
            Collider[] colliders = root
                .GetComponentsInChildren<Collider>(includeInactive: false)
                .Where(collider => collider != null && collider.enabled && !collider.isTrigger)
                .ToArray();
            var clearance = new Bounds(
                center + Vector3.up * 1.5f * levelHeight,
                new Vector3(1f, 2.6f * levelHeight, 1f));
            lowerClearanceOpen = !colliders.Any(collider =>
                collider.bounds.Intersects(clearance));

            bool CoversHorizontalPoint(Collider collider)
            {
                Bounds bounds = collider.bounds;
                return center.x >= bounds.min.x && center.x <= bounds.max.x &&
                    center.z >= bounds.min.z && center.z <= bounds.max.z;
            }

            lowerSurfaceColliderNames = colliders
                .Where(collider => CoversHorizontalPoint(collider) &&
                    collider.bounds.min.y <= 0.1f && collider.bounds.max.y >= -0.1f)
                .Select(collider => ColliderHierarchyPath(collider.transform))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            float deckY = deckLevel * levelHeight;
            upperSurfaceColliderNames = colliders
                .Where(collider => CoversHorizontalPoint(collider) &&
                    collider.bounds.min.y <= deckY + 0.1f &&
                    collider.bounds.max.y >= deckY - 0.1f &&
                    ColliderHierarchyPath(collider.transform)
                        .Contains("Transition Stairs"))
                .Select(collider => ColliderHierarchyPath(collider.transform))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }

        private static bool EqualLevelPathExists(
            IReadOnlyDictionary<Vector2Int, int> levels,
            Vector2Int start,
            Vector2Int end,
            int level)
        {
            var visited = new HashSet<Vector2Int> { start };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                if (cell == end)
                    return true;
                foreach (Vector2Int neighbor in CardinalNeighbors(cell))
                {
                    if (levels.TryGetValue(neighbor, out int neighborLevel) &&
                        neighborLevel == level &&
                        visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            return false;
        }


        private static string ColliderHierarchyPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = $"{transform.name}/{path}";
            }

            return path;
        }

    }
}

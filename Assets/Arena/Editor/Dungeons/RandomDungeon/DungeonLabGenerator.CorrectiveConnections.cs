using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // Bounded production correction: every accepted tier plan receives one to
    // four explicit, outward-facing connection stubs. Selection is isolated
    // from every existing random stream and runs only after the established
    // route, recipe, stair, bridge, dais, sweep, and scenic work is complete.
    internal sealed partial class DungeonLabGenerator
    {
        private const string ExternalConnectorPromontoryPolicyVersion =
            "external-connector-promontory-v1";
        private const int ExternalConnectorAppendageCells = 2;
        private const string ExternalConnectorRejectionCode =
            "EXTERNAL_CONNECTOR_PROMONTORY";

        private sealed class ExternalConnectorCandidate
        {
            public int direction;
            public Vector2Int anchorCell;
            public Vector2Int terminalCell;
            public Vector2Int throatCell;
            public int level;
            public Vector2Int[] occupiedCells;
            public uint priority;
        }

        private readonly struct ExternalConnectorPromontoryResolution
        {
            public readonly string id;
            public readonly int direction;
            public readonly Vector2Int anchorCell;
            public readonly Vector2Int terminalCell;
            public readonly int level;
            // Canonical order is anchor, appendage cell, terminal cell.
            public readonly Vector2Int[] occupiedCells;

            public ExternalConnectorPromontoryResolution(
                string id,
                int direction,
                Vector2Int anchorCell,
                Vector2Int terminalCell,
                int level,
                Vector2Int[] occupiedCells)
            {
                this.id = id ?? string.Empty;
                this.direction = direction;
                this.anchorCell = anchorCell;
                this.terminalCell = terminalCell;
                this.level = level;
                this.occupiedCells = occupiedCells ?? Array.Empty<Vector2Int>();
            }
        }

        private static bool TryResolveExternalConnectorPromontories(
            int dungeonSeed,
            DungeonLayout layout,
            Dictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            IReadOnlyCollection<Vector2Int> protectedStructuralCells,
            IReadOnlyCollection<Vector2Int> doorwayCells,
            StairPlacementLedger plannedStairLedger,
            IReadOnlyList<NamedVistaPromontoryResolution> namedPromontories,
            out ExternalConnectorPromontoryResolution[] resolutions,
            out string rejectionReason)
        {
            resolutions = Array.Empty<ExternalConnectorPromontoryResolution>();
            rejectionReason = string.Empty;
            int desiredCount = ExternalConnectorDesiredCount(dungeonSeed);
            if (layout.floorCells == null || layout.floorCells.Count == 0)
            {
                return RejectExternalConnectors(
                    "core floor extent was empty",
                    out rejectionReason);
            }

            var excluded = new HashSet<Vector2Int>();
            if (protectedStructuralCells != null)
                excluded.UnionWith(protectedStructuralCells);
            if (doorwayCells != null)
                excluded.UnionWith(doorwayCells);
            if (plannedStairLedger != null)
            {
                excluded.UnionWith(plannedStairLedger.footprintCells);
                excluded.UnionWith(plannedStairLedger.landingCells);
            }

            foreach (ElevationEdgeModel.TransitionEdge transition in
                     transitions ?? Array.Empty<ElevationEdgeModel.TransitionEdge>())
            {
                excluded.UnionWith(transition.footprintCells);
                excluded.UnionWith(transition.lowerLandingCells);
                excluded.UnionWith(transition.upperLandingCells);
            }

            foreach (NamedVistaPromontoryResolution promontory in
                     namedPromontories ?? Array.Empty<NamedVistaPromontoryResolution>())
            {
                excluded.UnionWith(promontory.cells);
            }

            HashSet<Vector2Int> exteriorVoid = BuildExternalConnectorExteriorVoid(cellLevels);
            var candidatesByDirection = new Dictionary<int, List<ExternalConnectorCandidate>>();
            foreach (int direction in Direction.Cardinals)
            {
                candidatesByDirection[direction] = BuildExternalConnectorCandidates(
                    dungeonSeed,
                    direction,
                    layout.floorCells,
                    cellLevels,
                    exteriorVoid,
                    excluded);
            }

            List<int> directionPriority = BuildExternalDirectionPriority(dungeonSeed);
            foreach (int[] directionSubset in EnumerateExternalDirectionSubsets(
                         directionPriority,
                         desiredCount))
            {
                var chosen = new List<ExternalConnectorCandidate>(desiredCount);
                var claimed = new HashSet<Vector2Int>();
                if (!TryChooseExternalConnectorCandidates(
                        directionSubset,
                        0,
                        candidatesByDirection,
                        claimed,
                        chosen))
                {
                    continue;
                }

                var planned = new ExternalConnectorPromontoryResolution[chosen.Count];
                for (int index = 0; index < chosen.Count; index++)
                {
                    ExternalConnectorCandidate candidate = chosen[index];
                    planned[index] = new ExternalConnectorPromontoryResolution(
                        ExternalConnectorId(candidate.direction),
                        candidate.direction,
                        candidate.anchorCell,
                        candidate.terminalCell,
                        candidate.level,
                        candidate.occupiedCells);
                }

                // The full set is selected and conflict-checked before the first
                // mutation, so a failed subset can never leave a partial result.
                foreach (ExternalConnectorPromontoryResolution resolution in planned)
                {
                    for (int index = 1; index < resolution.occupiedCells.Length; index++)
                    {
                        cellLevels.Add(resolution.occupiedCells[index], resolution.level);
                    }
                }

                resolutions = planned;
                return true;
            }

            return RejectExternalConnectors(
                $"could not realize exact count {desiredCount} with distinct directions on the final core extent",
                out rejectionReason);
        }

        private static List<ExternalConnectorCandidate> BuildExternalConnectorCandidates(
            int dungeonSeed,
            int direction,
            IReadOnlyCollection<Vector2Int> coreFloorCells,
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            HashSet<Vector2Int> exteriorVoid,
            HashSet<Vector2Int> excluded)
        {
            var candidates = new List<ExternalConnectorCandidate>();
            Vector2Int outward = CardinalVector(direction);
            foreach (Vector2Int anchor in coreFloorCells)
            {
                if (excluded.Contains(anchor) ||
                    !cellLevels.TryGetValue(anchor, out int level))
                {
                    continue;
                }

                Vector2Int appendage = anchor + outward;
                Vector2Int terminal = appendage + outward;
                Vector2Int throat = terminal + outward;
                if (cellLevels.ContainsKey(appendage) ||
                    cellLevels.ContainsKey(terminal) ||
                    cellLevels.ContainsKey(throat) ||
                    excluded.Contains(appendage) ||
                    excluded.Contains(terminal) ||
                    excluded.Contains(throat) ||
                    !exteriorVoid.Contains(appendage) ||
                    !exteriorVoid.Contains(terminal) ||
                    !exteriorVoid.Contains(throat))
                {
                    continue;
                }

                candidates.Add(new ExternalConnectorCandidate
                {
                    direction = direction,
                    anchorCell = anchor,
                    terminalCell = terminal,
                    throatCell = throat,
                    level = level,
                    occupiedCells = new[] { anchor, appendage, terminal },
                    priority = ExternalConnectorStableHash(
                        dungeonSeed,
                        $"anchor:{direction}:{anchor.x}:{anchor.y}")
                });
            }

            candidates.Sort((first, second) =>
            {
                int priority = first.priority.CompareTo(second.priority);
                return priority != 0
                    ? priority
                    : CompareCells(first.anchorCell, second.anchorCell);
            });
            return candidates;
        }

        private static HashSet<Vector2Int> BuildExternalConnectorExteriorVoid(
            IReadOnlyDictionary<Vector2Int, int> cellLevels)
        {
            var occupied = new HashSet<Vector2Int>(cellLevels.Keys);
            RectInt occupiedExtent = GetCellRect(occupied);
            int margin = ExternalConnectorAppendageCells + 1;
            int minX = occupiedExtent.xMin - margin;
            int maxX = occupiedExtent.xMax - 1 + margin;
            int minY = occupiedExtent.yMin - margin;
            int maxY = occupiedExtent.yMax - 1 + margin;
            var exterior = new HashSet<Vector2Int>();
            var queue = new Queue<Vector2Int>();

            void Enqueue(Vector2Int cell)
            {
                if (!occupied.Contains(cell) && exterior.Add(cell))
                    queue.Enqueue(cell);
            }

            for (int x = minX; x <= maxX; x++)
            {
                Enqueue(new Vector2Int(x, minY));
                Enqueue(new Vector2Int(x, maxY));
            }
            for (int y = minY; y <= maxY; y++)
            {
                Enqueue(new Vector2Int(minX, y));
                Enqueue(new Vector2Int(maxX, y));
            }

            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                foreach (Vector2Int neighbor in CardinalNeighbors(cell))
                {
                    if (neighbor.x < minX || neighbor.x > maxX ||
                        neighbor.y < minY || neighbor.y > maxY ||
                        occupied.Contains(neighbor) ||
                        !exterior.Add(neighbor))
                    {
                        continue;
                    }

                    queue.Enqueue(neighbor);
                }
            }

            return exterior;
        }

        private static bool TryChooseExternalConnectorCandidates(
            IReadOnlyList<int> directions,
            int directionIndex,
            IReadOnlyDictionary<int, List<ExternalConnectorCandidate>> candidatesByDirection,
            HashSet<Vector2Int> claimed,
            List<ExternalConnectorCandidate> chosen)
        {
            if (directionIndex >= directions.Count)
                return true;

            foreach (ExternalConnectorCandidate candidate in candidatesByDirection[directions[directionIndex]])
            {
                bool conflicts = false;
                foreach (Vector2Int cell in candidate.occupiedCells)
                    conflicts |= claimed.Contains(cell);
                conflicts |= claimed.Contains(candidate.throatCell);
                if (conflicts)
                    continue;

                foreach (Vector2Int cell in candidate.occupiedCells)
                    claimed.Add(cell);
                claimed.Add(candidate.throatCell);
                chosen.Add(candidate);
                if (TryChooseExternalConnectorCandidates(
                        directions,
                        directionIndex + 1,
                        candidatesByDirection,
                        claimed,
                        chosen))
                {
                    return true;
                }

                chosen.RemoveAt(chosen.Count - 1);
                foreach (Vector2Int cell in candidate.occupiedCells)
                    claimed.Remove(cell);
                claimed.Remove(candidate.throatCell);
            }

            return false;
        }

        private static IEnumerable<int[]> EnumerateExternalDirectionSubsets(
            IReadOnlyList<int> priority,
            int count)
        {
            var indices = new int[count];
            foreach (int[] subset in EnumerateExternalDirectionSubsets(priority, indices, 0, 0))
                yield return subset;
        }

        private static IEnumerable<int[]> EnumerateExternalDirectionSubsets(
            IReadOnlyList<int> priority,
            int[] indices,
            int depth,
            int firstIndex)
        {
            if (depth == indices.Length)
            {
                var subset = new int[indices.Length];
                for (int index = 0; index < indices.Length; index++)
                    subset[index] = priority[indices[index]];
                yield return subset;
                yield break;
            }

            int remaining = indices.Length - depth;
            for (int index = firstIndex; index <= priority.Count - remaining; index++)
            {
                indices[depth] = index;
                foreach (int[] subset in EnumerateExternalDirectionSubsets(
                             priority,
                             indices,
                             depth + 1,
                             index + 1))
                {
                    yield return subset;
                }
            }
        }

        private static List<int> BuildExternalDirectionPriority(int dungeonSeed)
        {
            var directions = new List<int>(Direction.Cardinals);
            directions.Sort((first, second) =>
            {
                uint firstHash = ExternalConnectorStableHash(dungeonSeed, $"direction:{first}");
                uint secondHash = ExternalConnectorStableHash(dungeonSeed, $"direction:{second}");
                int hash = firstHash.CompareTo(secondHash);
                return hash != 0 ? hash : first.CompareTo(second);
            });
            return directions;
        }

        private static int ExternalConnectorDesiredCount(int dungeonSeed)
        {
            int bucket = (int)(ExternalConnectorStableHash(dungeonSeed, "count") & 15u);
            if (bucket < 8)
                return 1;
            if (bucket < 13)
                return 2;
            return bucket < 15 ? 3 : 4;
        }

        private static uint ExternalConnectorStableHash(int dungeonSeed, string purpose)
        {
            unchecked
            {
                uint hash = 2166136261u;
                MixExternalConnectorHash(ref hash, dungeonSeed.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                MixExternalConnectorHash(ref hash, ExternalConnectorPromontoryPolicyVersion);
                MixExternalConnectorHash(ref hash, purpose ?? string.Empty);
                return hash;
            }
        }

        private static void MixExternalConnectorHash(ref uint hash, string value)
        {
            unchecked
            {
                foreach (char character in value)
                {
                    hash ^= character;
                    hash *= 16777619u;
                }

                hash ^= 0xffu;
                hash *= 16777619u;
            }
        }

        private static bool RejectExternalConnectors(
            string detail,
            out string rejectionReason)
        {
            rejectionReason = $"[{ExternalConnectorRejectionCode}] {detail}";
            return false;
        }

        private static string ExternalConnectorId(int direction)
        {
            return direction == Direction.North ? "external-north"
                : direction == Direction.East ? "external-east"
                : direction == Direction.South ? "external-south"
                : "external-west";
        }

        private static List<Vector2Int> CollectExternalConnectorPierCells(
            IReadOnlyList<ExternalConnectorPromontoryResolution> resolutions)
        {
            var result = new List<Vector2Int>();
            foreach (ExternalConnectorPromontoryResolution resolution in
                     resolutions ?? Array.Empty<ExternalConnectorPromontoryResolution>())
            {
                for (int index = 1; index < resolution.occupiedCells.Length; index++)
                    result.Add(resolution.occupiedCells[index]);
            }

            return result;
        }

        private static List<Vector2Int> CollectRenderedPromontoryCells(
            IReadOnlyList<NamedVistaPromontoryResolution> namedPromontories,
            IReadOnlyList<ExternalConnectorPromontoryResolution> externalConnectors)
        {
            var result = CollectNamedPromontoryCells(namedPromontories);
            var unique = new HashSet<Vector2Int>(result);
            foreach (Vector2Int cell in CollectExternalConnectorPierCells(externalConnectors))
            {
                if (unique.Add(cell))
                    result.Add(cell);
            }

            return result;
        }

        private static List<ElevationEdgeModel.OpenFloorEdge> BuildExternalConnectorOpenEdges(
            IReadOnlyList<ExternalConnectorPromontoryResolution> resolutions)
        {
            var result = new List<ElevationEdgeModel.OpenFloorEdge>();
            foreach (ExternalConnectorPromontoryResolution resolution in
                     resolutions ?? Array.Empty<ExternalConnectorPromontoryResolution>())
            {
                result.Add(new ElevationEdgeModel.OpenFloorEdge(
                    resolution.anchorCell,
                    resolution.direction));
                result.Add(new ElevationEdgeModel.OpenFloorEdge(
                    resolution.terminalCell,
                    resolution.direction));
            }

            return result;
        }
    }
}

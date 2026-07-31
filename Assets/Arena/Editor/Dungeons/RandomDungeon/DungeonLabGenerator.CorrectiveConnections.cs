using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // Every accepted tier plan receives one to four long, straight,
    // outward-facing promontories, with at most one per cardinal direction.
    // Selection is isolated
    // from every existing random stream and runs only after the established
    // route, recipe/showpiece, stair, bridge, sweep, and scenic work is complete.
    internal sealed partial class DungeonLabGenerator
    {
        private const string ExternalConnectorPromontoryPolicyVersion =
            "external-connector-promontory-v3-outer-long-straight";
        // Eight 4u grid cells make each promontory project 32 world units beyond
        // its core-floor anchor. The next cell remains a clear terminal throat.
        private const int ExternalConnectorAppendageCells = 8;
        private const string ExternalConnectorRejectionCode =
            "EXTERNAL_CONNECTOR_PROMONTORY";

        // The shipped contract is 1-4 external promontories. The seed draws a
        // preferred count; this is the floor it may be walked down to.
        private const int MinimumExternalConnectorCount = 1;

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
            // Canonical order is anchor followed by each straight outward cell.
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
            SurfaceField surfaces,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            IReadOnlyCollection<Vector2Int> protectedStructuralCells,
            IReadOnlyCollection<Vector2Int> doorwayCells,
            PrismLedger plannedStairLedger,
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
                excluded.UnionWith(plannedStairLedger.CellsOfKind(PrismKind.Footprint));
                excluded.UnionWith(plannedStairLedger.CellsOfKind(PrismKind.Landing));
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

            // Candidate search is a pure reader of the surface field, and every
            // question it asks is a COLUMN question: "is this column surfaced at
            // all" for the run-clear and throat tests, and "what is its floor"
            // for the anchor. Both answer identically however many surfaces a
            // column carries, so a pier still anchors on the ground-backed floor
            // of the outer face — attaching one to a gallery instead is a design
            // extension, not something this migration decides by accident.
            HashSet<Vector2Int> exteriorVoid = BuildExternalConnectorExteriorVoid(surfaces);
            RectInt coreExtent = GetCellRect(new HashSet<Vector2Int>(layout.floorCells));
            var candidatesByDirection = new Dictionary<int, List<ExternalConnectorCandidate>>();
            foreach (int direction in Direction.Cardinals)
            {
                candidatesByDirection[direction] = BuildExternalConnectorCandidates(
                    dungeonSeed,
                    direction,
                    coreExtent,
                    layout.floorCells,
                    surfaces,
                    exteriorVoid,
                    excluded);
            }

            List<int> directionPriority = BuildExternalDirectionPriority(dungeonSeed);
            // The seed's count is a PREFERENCE, tried first and then walked down.
            // The shipped contract is "1-4 external promontories"; requiring the
            // exact drawn number atomically made a seed fail outright when the
            // core no longer offered that many anchors — which is what cost
            // `2026072187` at density 0 and `2026072198` a whole layout attempt.
            // Fewer connectors is a smaller dungeon mouth, not an invalid one.
            for (int count = desiredCount; count >= MinimumExternalConnectorCount; count--)
            {
                foreach (int[] directionSubset in EnumerateExternalDirectionSubsets(
                             directionPriority,
                             count))
                {
                    var chosen = new List<ExternalConnectorCandidate>(count);
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
                            // Index 1 on purpose: cell 0 is the anchor, which is
                            // already surfaced. Every cell past it was proved
                            // unsurfaced by the run-clear test in
                            // BuildExternalConnectorCandidates.
                            surfaces.AddFloorLevel(
                                resolution.occupiedCells[index],
                                resolution.level);
                        }
                    }

                    resolutions = planned;
                    return true;
                }
            }

            return RejectExternalConnectors(
                $"could not realize even {MinimumExternalConnectorCount} connector " +
                $"(preferred {desiredCount}) with distinct directions on the final core extent",
                out rejectionReason);
        }

        private static List<ExternalConnectorCandidate> BuildExternalConnectorCandidates(
            int dungeonSeed,
            int direction,
            RectInt coreExtent,
            IReadOnlyCollection<Vector2Int> coreFloorCells,
            SurfaceField surfaces,
            HashSet<Vector2Int> exteriorVoid,
            HashSet<Vector2Int> excluded)
        {
            var candidates = new List<ExternalConnectorCandidate>();
            Vector2Int outward = CardinalVector(direction);
            foreach (Vector2Int anchor in coreFloorCells)
            {
                if (excluded.Contains(anchor) ||
                    !IsOnExternalConnectorOuterFace(coreExtent, anchor, direction) ||
                    !surfaces.TryGetFloorLevel(anchor, out int level))
                {
                    continue;
                }

                var occupiedCells = new Vector2Int[ExternalConnectorAppendageCells + 1];
                occupiedCells[0] = anchor;
                bool runIsClear = true;
                for (int distance = 1;
                     distance <= ExternalConnectorAppendageCells + 1;
                     distance++)
                {
                    Vector2Int cell = anchor + outward * distance;
                    if (surfaces.HasFloor(cell) ||
                        excluded.Contains(cell) ||
                        !exteriorVoid.Contains(cell))
                    {
                        runIsClear = false;
                        break;
                    }

                    if (distance <= ExternalConnectorAppendageCells)
                        occupiedCells[distance] = cell;
                }

                if (!runIsClear)
                    continue;

                Vector2Int terminal = occupiedCells[ExternalConnectorAppendageCells];
                Vector2Int throat = anchor + outward * (ExternalConnectorAppendageCells + 1);
                candidates.Add(new ExternalConnectorCandidate
                {
                    direction = direction,
                    anchorCell = anchor,
                    terminalCell = terminal,
                    throatCell = throat,
                    level = level,
                    occupiedCells = occupiedCells,
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

        private static bool IsOnExternalConnectorOuterFace(
            RectInt extent,
            Vector2Int cell,
            int direction)
        {
            return direction == Direction.North
                ? cell.y == extent.yMax - 1
                : direction == Direction.East
                    ? cell.x == extent.xMax - 1
                    : direction == Direction.South
                        ? cell.y == extent.yMin
                        : direction == Direction.West && cell.x == extent.xMin;
        }

        private static HashSet<Vector2Int> BuildExternalConnectorExteriorVoid(
            SurfaceField surfaces)
        {
            // PlanCells(), not Surfaces(): the exterior flood is a PLAN-space
            // question, so a stacked column occupies one cell here, not two.
            var occupied = surfaces.FlooredPlanCells();
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
                MixDerivedSeedHash(ref hash, dungeonSeed.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                MixDerivedSeedHash(ref hash, ExternalConnectorPromontoryPolicyVersion);
                MixDerivedSeedHash(ref hash, purpose ?? string.Empty);
                return hash;
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

        /// <summary>
        /// Every rim the PLAN declares bare: the external connectors' throats,
        /// plus any aperture an authored recipe opened.
        /// </summary>
        /// <remarks>
        /// One list, built at both render call sites, so a plan cannot render
        /// with half its declared openings. The connector edges are
        /// column-scoped (the sentinel level) and the recipe rims are
        /// surface-scoped, which is exactly the distinction
        /// <c>OpenFloorEdge.IsSurfaceScoped</c> exists to carry: a connector
        /// throat opens the ground it stands on, an aperture rim opens one
        /// storey and leaves the floor below it fully guarded.
        /// </remarks>
        private static List<ElevationEdgeModel.OpenFloorEdge> BuildPlannedOpenEdges(
            TieredLevelPlan plan)
        {
            List<ElevationEdgeModel.OpenFloorEdge> edges =
                BuildExternalConnectorOpenEdges(plan.externalConnectors);
            foreach (RecipeResolution resolution in
                     plan.recipeResolutions ?? Array.Empty<RecipeResolution>())
            {
                foreach (RecipeOpeningPlacement opening in resolution.openings)
                {
                    edges.Add(new ElevationEdgeModel.OpenFloorEdge(
                        opening.cell,
                        resolution.baseLevel +
                            opening.layerRelativeLevel +
                            ResolvedRecipeLayerRelativeLevel(
                                resolution.zones,
                                opening.cell,
                                opening.layerId),
                        opening.direction));
                }
            }

            return edges;
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

        private static void RequireRenderedPromontoryDecks(
            TieredLevelPlan plan,
            ElevationEdgeModel.BuildReport report)
        {
            int externalCount = plan.externalConnectors?.Length ?? 0;
            if (externalCount < 1 || externalCount > 4)
            {
                throw new InvalidOperationException(
                    $"Rendered dungeon required 1-4 external promontories; plan carried {externalCount}.");
            }

            int expectedDeckCells = CollectRenderedPromontoryCells(
                plan.namedPromontories,
                plan.externalConnectors).Count;
            if (report.promontoryDeckCells != expectedDeckCells)
            {
                throw new InvalidOperationException(
                    $"Rendered dungeon produced {report.promontoryDeckCells} of " +
                    $"{expectedDeckCells} required promontory deck cells.");
            }
        }
    }
}

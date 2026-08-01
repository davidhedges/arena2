using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // Tools > Dungeon Lab > Validate Topologies.
    //
    // Hand-verifying a topology draft against the authoring rules is the
    // expensive part of adding one. This answers in one second instead of an
    // afternoon: every rule, by node/edge key, with the offending value; the map
    // re-rendered with its edges drawn; the vista lane with its worst-case clear
    // cell count at each profile; the plan envelope across all eight
    // orientations; and the derived graph metrics.
    //
    // It is author-time only. It never runs during generation and it never
    // decides anything: a topology that fails here still fails at generation,
    // loudly, through TryValidateRouteIntent.
    internal sealed partial class DungeonLabGenerator
    {
        private const string RouteTopologyValidationReportPath =
            "DungeonLabReports/route_topology_validation.txt";
        // Every topology is checked at every density (design §6): a topology that
        // cannot take a setting is then a data problem this report names by file,
        // fixable without generator C#. Achieved fill and max void component per
        // level are reported alongside the rules — they are a measurement rather
        // than a rule, but §6 asks this tool to answer "which topology cannot
        // reach density 5", and a rule check alone cannot.
        private static readonly int[] RouteTopologyValidationDensityLevels = { 0, 1, 2, 3, 4, 5 };

        // Public so -executeMethod can reach it: the report is now part of the
        // evidence a batch run produces, not only something clicked in-editor.
        [MenuItem("Tools/Dungeon Lab/Validate Topologies")]
        public static void ValidateTopologies()
        {
            string report;
            bool passed;
            int topologyCount;
            try
            {
                report = BuildRouteTopologyValidationReport(out passed, out topologyCount);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[ROUTE_TOPOLOGY] validation could not run: {exception.Message}");
                EditorUtility.DisplayDialog(
                    "Validate Topologies",
                    $"Validation could not run:\n\n{exception.Message}",
                    "Close");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(RouteTopologyValidationReportPath));
            File.WriteAllText(RouteTopologyValidationReportPath, report);
            if (passed)
            {
                Debug.Log(report);
            }
            else
            {
                Debug.LogError(report);
            }

            EditorUtility.DisplayDialog(
                "Validate Topologies",
                (passed
                    ? $"All {topologyCount} topologies satisfy every authoring rule."
                    : $"At least one of {topologyCount} topologies violates an authoring rule. See the console.") +
                $"\n\nReport written to {RouteTopologyValidationReportPath}",
                "Close");
        }

        private static string BuildRouteTopologyValidationReport(out bool passed, out int topologyCount)
        {
            // Restore whatever profile the rest of the editor session was using.
            DungeonGenerationSettings restoreSettings = CurrentGenerationSettings;
            var report = new StringBuilder();
            passed = true;
            try
            {
                List<DungeonRouteTopology> topologies = AllRouteTopologiesByFileOrder();
                topologyCount = topologies.Count;
                report.Append("Route topology validation — ")
                    .Append(RouteTopologyDirectory)
                    .Append(", ")
                    .Append(topologyCount)
                    .AppendLine(" topologies");
                if (topologyCount == 0)
                {
                    passed = false;
                    report.AppendLine("FAIL: no topology files found.");
                    return report.ToString();
                }

                foreach (DungeonRouteTopology topology in topologies)
                {
                    var violations = new List<string>();
                    var notes = new List<string>();
                    AppendRouteTopologyGraphRules(topology, violations);
                    AppendRouteTopologyLatticeRules(topology, violations);
                    AppendRouteTopologyRhythmRules(topology, violations);
                    // Densities that resolve to the same spatial settings would
                    // print the same block twice, so the first one to produce a
                    // given resolution is checked and the rest say who they
                    // match.
                    var checkedDensitiesByResolution = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (int densityLevel in RouteTopologyValidationDensityLevels)
                    {
                        CurrentGenerationSettings = LoadActiveGenerationSettings(densityLevel);
                        string label = $"density {densityLevel}";
                        string resolution = DescribeResolvedSpatialSettings(topology);
                        if (checkedDensitiesByResolution.TryGetValue(resolution, out int firstLevel))
                        {
                            notes.Add($"{label}: resolves identically to density {firstLevel}");
                            continue;
                        }

                        checkedDensitiesByResolution[resolution] = densityLevel;
                        AppendRouteTopologyProfileRules(topology, label, violations, notes);
                    }

                    // Rules are static; the dial's effect is not. §6 wants this
                    // tool to name the FILE when a topology cannot reach a
                    // density, and that needs achieved numbers — so a few of
                    // this topology's own seeds are generated at every level.
                    // Outside the resolution dedup above, because annex and
                    // mop-up move fill without moving the spatial settings.
                    AppendRouteTopologyAchievedDensity(topology, notes);

                    passed &= violations.Count == 0;
                    report.AppendLine()
                        .Append(violations.Count == 0 ? "PASS  " : "FAIL  ")
                        .Append(topology.id)
                        .Append("  (")
                        .Append(topology.displayName)
                        .Append(", ")
                        .Append(topology.plannerVersion)
                        .AppendLine(")");
                    report.Append(RenderRouteTopologyMap(topology));
                    report.AppendLine(RenderRouteTopologyMetrics(topology));
                    foreach (string note in notes)
                    {
                        report.Append("    ").AppendLine(note);
                    }

                    foreach (string violation in violations)
                    {
                        report.Append("    VIOLATION: ").AppendLine(violation);
                    }
                }

                passed &= AppendLayerSchemaLoaderChecks(report);
            }
            finally
            {
                CurrentGenerationSettings = restoreSettings;
            }

            report.AppendLine()
                .AppendLine(passed
                    ? "RESULT: every topology satisfies every authoring rule."
                    : "RESULT: at least one topology violates an authoring rule.");
            return report.ToString();
        }

        // Enough seeds to see whether a topology reaches a level, few enough
        // that the whole tool stays a thing you run while thinking. The seeds
        // are this topology's own: SelectRouteTopologyId is a pure function of
        // the seed, so they are found by scanning rather than by generating.
        private const int RouteTopologyAchievedDensitySeeds = 3;

        private static void AppendRouteTopologyAchievedDensity(
            DungeonRouteTopology topology,
            List<string> notes)
        {
            int[] seeds = FindSeedsForTopology(topology.id, RouteTopologyAchievedDensitySeeds);
            if (seeds.Length == 0)
            {
                notes.Add(
                    "achieved density: no seed in the baseline window draws this topology, so there is " +
                    "nothing to measure — check its registry weight");
                return;
            }

            var line = new StringBuilder("achieved fill / max void component, ")
                .Append(seeds.Length)
                .Append(" seeds: ");
            for (int densityLevel = DungeonDensity.MinLevel;
                 densityLevel <= DungeonDensity.MaxLevel;
                 densityLevel++)
            {
                var fills = new List<float>();
                var maxVoids = new List<int>();
                int rejected = 0;
                foreach (int seed in seeds)
                {
                    JObject report = BuildSeedReport(seed, densityLevel);
                    if (report.Value<bool?>("accepted") != true)
                    {
                        rejected++;
                        continue;
                    }

                    if (report["measurements"]?["density"] is JObject density &&
                        density.Value<bool?>("available") == true)
                    {
                        fills.Add(density.Value<float>("latticeEnvelopeFillPercent"));
                        maxVoids.Add(density["voidComponents"].Value<int>("maxComponentCells"));
                    }
                }

                if (densityLevel > DungeonDensity.MinLevel)
                {
                    line.Append("  ");
                }

                line.Append('d').Append(densityLevel).Append(' ');
                if (fills.Count == 0)
                {
                    line.Append("none accepted");
                    continue;
                }

                line.Append(MedianOf(fills).ToString("0", CultureInfo.InvariantCulture))
                    .Append("%/")
                    .Append(MedianOf(maxVoids).ToString("0", CultureInfo.InvariantCulture));
                if (rejected > 0)
                {
                    line.Append(" (").Append(rejected).Append(" rejected)");
                }
            }

            notes.Add(line.ToString());
        }

        private static int[] FindSeedsForTopology(string topologyId, int wanted)
        {
            var seeds = new List<int>(wanted);
            for (int seed = BaselineFirstSeed;
                 seeds.Count < wanted && seed < BaselineFirstSeed + BaselineSeedCount;
                 seed++)
            {
                if (string.Equals(SelectRouteTopologyId(seed), topologyId, StringComparison.Ordinal))
                {
                    seeds.Add(seed);
                }
            }

            return seeds.ToArray();
        }

        private static double MedianOf<T>(List<T> values) where T : IConvertible
        {
            var ordered = new List<double>(values.Count);
            foreach (T value in values)
            {
                ordered.Add(value.ToDouble(CultureInfo.InvariantCulture));
            }

            ordered.Sort();
            int middle = ordered.Count / 2;
            return ordered.Count % 2 == 1
                ? ordered[middle]
                : (ordered[middle - 1] + ordered[middle]) / 2d;
        }

        // ---- rules --------------------------------------------------------

        private static void AppendRouteTopologyGraphRules(
            DungeonRouteTopology topology,
            List<string> violations)
        {
            if (topology.nodes.Length < MinRouteNodeCount || topology.nodes.Length > MaxRouteNodeCount)
            {
                violations.Add(
                    $"node count is {topology.nodes.Length}; the generator accepts " +
                    $"{MinRouteNodeCount}..{MaxRouteNodeCount}");
            }


            var mainRoute = new List<RouteTopologyNode>();
            var branchNodes = new List<RouteTopologyNode>();
            for (int nodeIndex = 0; nodeIndex < topology.nodes.Length; nodeIndex++)
            {
                RouteTopologyNode node = topology.nodes[nodeIndex];
                (node.IsOnMainRoute ? mainRoute : branchNodes).Add(node);
                if (node.level < 0 || node.level > MaxGeneratedLevel)
                {
                    violations.Add(
                        $"node '{node.key}' ({node.id}) level {node.level} is outside 0..{MaxGeneratedLevel}");
                }

                if (node.level % MajorRiseLevels != 0)
                {
                    violations.Add(
                        $"node '{node.key}' ({node.id}) level {node.level} is not a multiple of {MajorRiseLevels}");
                }

                ValidateNodeLayers(nodeIndex, node, topology, violations);
            }

            for (int index = 0; index < mainRoute.Count; index++)
            {
                if (mainRoute[index].mainRouteOrder != index)
                {
                    violations.Add(
                        $"main-route orders are not contiguous from 0: position {index} is " +
                        $"'{mainRoute[index].key}' with order {mainRoute[index].mainRouteOrder}");
                }
            }

            for (int index = 0; index < branchNodes.Count; index++)
            {
                if (branchNodes[index].branchOrder != index)
                {
                    violations.Add(
                        $"branch orders are not contiguous from 0: position {index} is " +
                        $"'{branchNodes[index].key}' with order {branchNodes[index].branchOrder}");
                }
            }

            var kinds = new HashSet<RouteTransitionKind>();
            foreach (RouteTopologyEdge edge in topology.edges)
            {
                kinds.Add(edge.transitionKind);
                // Between the BOUND elevations, not between the node levels
                // (design §8.1: "the existing derivation, one term wider"). An
                // unbound end resolves to its node's own level, so an
                // unlayered graph reads exactly as it did before.
                topology.nodes[edge.fromNode].TryGetAbsoluteLevel(edge.fromLayerId, out int fromLevel);
                topology.nodes[edge.toNode].TryGetAbsoluteLevel(edge.toLayerId, out int toLevel);
                int rise = toLevel - fromLevel;
                string label = $"edge '{edge.id}' " +
                    $"({topology.nodes[edge.fromNode].key}{DescribeLayerBinding(edge.fromLayerId)}" +
                    $"->{topology.nodes[edge.toNode].key}{DescribeLayerBinding(edge.toLayerId)})";
                int riseMagnitude = Mathf.Abs(rise);
                if (edge.transitionKind == RouteTransitionKind.LevelCorridor)
                {
                    if (riseMagnitude != 0)
                    {
                        violations.Add($"{label} is a LevelCorridor across a {rise}u rise; must be 0");
                    }
                }
                else if (riseMagnitude != MajorRiseLevels && riseMagnitude != DoubleMajorRiseLevels)
                {
                    violations.Add(
                        $"{label} is a {edge.transitionKind} across a {rise}u rise; the generator accepts " +
                        $"+/-{MajorRiseLevels} or +/-{DoubleMajorRiseLevels}, so an edge may be written " +
                        "in travel order in either direction");
                }
            }

            foreach (RouteTransitionKind required in new[]
                     {
                         RouteTransitionKind.Stair,
                         RouteTransitionKind.Bridge,
                         RouteTransitionKind.Stairwell
                     })
            {
                if (!kinds.Contains(required))
                {
                    violations.Add($"no edge declares a {required}; the generator requires at least one");
                }
            }

            if (topology.nodes[topology.bottomNode].level != 0)
            {
                violations.Add(
                    $"bottom anchor '{topology.nodes[topology.bottomNode].key}' is at level " +
                    $"{topology.nodes[topology.bottomNode].level}; must be 0");
            }

            if (topology.nodes[topology.topNode].level != MaxGeneratedLevel)
            {
                violations.Add(
                    $"top anchor '{topology.nodes[topology.topNode].key}' is at level " +
                    $"{topology.nodes[topology.topNode].level}; must be {MaxGeneratedLevel}");
            }

            int vistaDrop = topology.nodes[topology.vistaSourceNode].level -
                topology.nodes[topology.vistaTargetNode].level;
            if (vistaDrop < MajorRiseLevels)
            {
                violations.Add(
                    $"vista source '{topology.nodes[topology.vistaSourceNode].key}' is only {vistaDrop}u " +
                    $"above its target; must be at least {MajorRiseLevels}u");
            }

            List<int>[] adjacency = topology.BuildAdjacency();
            var visited = new HashSet<int> { topology.bottomNode };
            var queue = new Queue<int>();
            queue.Enqueue(topology.bottomNode);
            while (queue.Count > 0)
            {
                foreach (int neighbor in adjacency[queue.Dequeue()])
                {
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            if (visited.Count != topology.nodes.Length)
            {
                var unreached = new List<string>();
                for (int node = 0; node < topology.nodes.Length; node++)
                {
                    if (!visited.Contains(node))
                    {
                        unreached.Add(topology.nodes[node].key);
                    }
                }

                violations.Add($"nodes unreachable from the bottom anchor: {string.Join(", ", unreached)}");
            }

            int cycleRank = topology.edges.Length - (topology.nodes.Length - 1);
            if (cycleRank < 1)
            {
                violations.Add($"cycle rank is {cycleRank}; a route needs at least one loop");
            }

            int junctionCount = 0;
            foreach (List<int> incident in adjacency)
            {
                junctionCount += incident.Count >= 3 ? 1 : 0;
            }

            if (junctionCount < 2)
            {
                violations.Add($"only {junctionCount} nodes have degree >= 3; a route needs at least two branch points");
            }

            AppendRouteTopologySlotRules(topology, adjacency, violations);
        }

        private static void AppendRouteTopologySlotRules(
            DungeonRouteTopology topology,
            List<int>[] adjacency,
            List<string> violations)
        {
            if (topology.slots.Length != 3)
            {
                violations.Add(
                    $"{topology.slots.Length} recipe slots are declared; the generator requires exactly 3");
            }

            foreach (string required in new[]
                     {
                         CompressionRecipeSlotId,
                         LandmarkRecipeSlotId,
                         ReturnRecipeSlotId
                     })
            {
                bool found = false;
                foreach (RouteTopologySlot slot in topology.slots)
                {
                    found |= string.Equals(slot.slotId, required, StringComparison.Ordinal);
                }

                if (!found)
                {
                    violations.Add($"no slot declares id '{required}'");
                }
            }

            foreach (RouteTopologySlot slot in topology.slots)
            {
                RouteTopologyNode node = topology.nodes[slot.node];
                if (adjacency[slot.node].Count != 2)
                {
                    violations.Add(
                        $"slot '{slot.slotId}' sits on '{node.key}' with degree " +
                        $"{adjacency[slot.node].Count}; a two-port recipe room needs degree 2");
                }

                foreach (string edgeId in new[] { slot.entryEdgeId, slot.exitEdgeId })
                {
                    if (!topology.TryGetEdgeIndex(edgeId, out int edge) ||
                        topology.edges[edge].fromNode != slot.node &&
                        topology.edges[edge].toNode != slot.node)
                    {
                        violations.Add(
                            $"slot '{slot.slotId}' bound edge '{edgeId}', which is not incident to '{node.key}'");
                    }
                }

                if (slot.orientationBinding == RecipeOrientationBinding.VistaSourceToTarget &&
                    slot.node != topology.vistaTargetNode &&
                    slot.node != topology.vistaSourceNode)
                {
                    violations.Add(
                        $"slot '{slot.slotId}' orients off the vista axis but '{node.key}' is neither " +
                        "vista endpoint");
                }
            }
        }

        private static void AppendRouteTopologyLatticeRules(
            DungeonRouteTopology topology,
            List<string> violations)
        {
            foreach (RouteTopologyEdge edge in topology.edges)
            {
                Vector2Int from = topology.nodes[edge.fromNode].lattice;
                Vector2Int to = topology.nodes[edge.toNode].lattice;
                if (from.x != to.x && from.y != to.y)
                {
                    violations.Add(
                        $"edge '{edge.id}' joins '{topology.nodes[edge.fromNode].key}' at {from} to " +
                        $"'{topology.nodes[edge.toNode].key}' at {to}, which is not cardinally aligned; " +
                        "the corridor builder cannot route it");
                }
            }

            // A corridor is a straight cardinal run between its endpoints, so any
            // third node on the same lane between them is a room it would cross.
            //
            // D2: unless it passes OVER that room. This is the author-time mirror
            // of `PathCrossesThirdRoom`, and it goes through the same predicate
            // on purpose — a rule stated twice is a rule that drifts, and the two
            // disagreeing is what cost C2 a whole rejected corpus.
            foreach (RouteTopologyEdge edge in topology.edges)
            {
                foreach (int blocking in RouteTopologyNodesBetween(topology, edge.fromNode, edge.toNode))
                {
                    if (RouteTopologyEdgeClearsNode(topology, edge, blocking))
                    {
                        continue;
                    }

                    violations.Add(
                        $"edge '{edge.id}' runs straight through node " +
                        $"'{topology.nodes[blocking].key}' ({topology.nodes[blocking].id})");
                }
            }

            Vector2Int sourceCell = topology.nodes[topology.vistaSourceNode].lattice;
            Vector2Int targetCell = topology.nodes[topology.vistaTargetNode].lattice;
            if (sourceCell.x != targetCell.x && sourceCell.y != targetCell.y)
            {
                violations.Add(
                    $"vista '{topology.vistaId}' joins {sourceCell} to {targetCell}, which is not " +
                    "cardinally aligned");
                return;
            }

            int steps = Mathf.Abs(targetCell.x - sourceCell.x) + Mathf.Abs(targetCell.y - sourceCell.y);
            if (steps < 1)
            {
                violations.Add($"vista '{topology.vistaId}' has identical endpoints");
                return;
            }

            foreach (int blocking in RouteTopologyNodesBetween(
                         topology,
                         topology.vistaSourceNode,
                         topology.vistaTargetNode))
            {
                violations.Add(
                    $"vista '{topology.vistaId}' looks through node " +
                    $"'{topology.nodes[blocking].key}' ({topology.nodes[blocking].id})");
            }

            // A corridor that shares the vista lane consumes the reserved void.
            foreach (RouteTopologyEdge edge in topology.edges)
            {
                if (RouteTopologyEdgeSharesVistaLane(topology, edge, sourceCell, targetCell))
                {
                    violations.Add(
                        $"edge '{edge.id}' runs inside the vista lane between {sourceCell} and " +
                        $"{targetCell}; the reservation forbids a corridor there");
                }
            }
        }

        /// <summary>
        /// Does this edge pass OVER the node blocking its lattice lane?
        /// </summary>
        /// <remarks>
        /// Authorized by the edge's layer binding and decided by the absolute
        /// bands, exactly as `PathCrossesThirdRoom` is — same predicate, and the
        /// elevations agree by construction because `RouteNodeIntent` carries
        /// this node's level and layer table verbatim.
        /// </remarks>
        private static bool RouteTopologyEdgeClearsNode(
            DungeonRouteTopology topology,
            RouteTopologyEdge edge,
            int blockingNode)
        {
            if (!edge.IsLayerBound)
            {
                return false;
            }

            topology.nodes[edge.fromNode].TryGetAbsoluteLevel(edge.fromLayerId, out int fromLevel);
            topology.nodes[edge.toNode].TryGetAbsoluteLevel(edge.toLayerId, out int toLevel);
            return CorridorClearsRoomVertically(
                LevelBand.SpanningEndpoints(fromLevel, toLevel),
                layerBound: true,
                RouteTopologyNodeDeclaredElevations(topology.nodes[blockingNode]));
        }

        /// <summary>A node's own level first, then one per declared layer.</summary>
        private static int[] RouteTopologyNodeDeclaredElevations(RouteTopologyNode node)
        {
            var declared = new int[node.layers.Length + 1];
            declared[0] = node.level;
            for (int layer = 0; layer < node.layers.Length; layer++)
            {
                declared[layer + 1] = node.level + node.layers[layer].relativeLevel;
            }

            return declared;
        }

        private static IEnumerable<int> RouteTopologyNodesBetween(
            DungeonRouteTopology topology,
            int firstNode,
            int secondNode)
        {
            Vector2Int first = topology.nodes[firstNode].lattice;
            Vector2Int second = topology.nodes[secondNode].lattice;
            if (first.x != second.x && first.y != second.y)
            {
                yield break;
            }

            for (int node = 0; node < topology.nodes.Length; node++)
            {
                if (node == firstNode || node == secondNode)
                {
                    continue;
                }

                Vector2Int cell = topology.nodes[node].lattice;
                bool onLane = first.x == second.x
                    ? cell.x == first.x &&
                      cell.y > Mathf.Min(first.y, second.y) &&
                      cell.y < Mathf.Max(first.y, second.y)
                    : cell.y == first.y &&
                      cell.x > Mathf.Min(first.x, second.x) &&
                      cell.x < Mathf.Max(first.x, second.x);
                if (onLane)
                {
                    yield return node;
                }
            }
        }

        private static bool RouteTopologyEdgeSharesVistaLane(
            DungeonRouteTopology topology,
            RouteTopologyEdge edge,
            Vector2Int sourceCell,
            Vector2Int targetCell)
        {
            Vector2Int from = topology.nodes[edge.fromNode].lattice;
            Vector2Int to = topology.nodes[edge.toNode].lattice;
            if (from.x != to.x && from.y != to.y)
            {
                return false;
            }

            bool vistaRunsOnY = sourceCell.x == targetCell.x;
            bool edgeRunsOnY = from.x == to.x;
            if (vistaRunsOnY != edgeRunsOnY)
            {
                return false;
            }

            if (vistaRunsOnY)
            {
                if (from.x != sourceCell.x)
                {
                    return false;
                }

                return Mathf.Min(from.y, to.y) < Mathf.Max(sourceCell.y, targetCell.y) &&
                    Mathf.Max(from.y, to.y) > Mathf.Min(sourceCell.y, targetCell.y);
            }

            if (from.y != sourceCell.y)
            {
                return false;
            }

            return Mathf.Min(from.x, to.x) < Mathf.Max(sourceCell.x, targetCell.x) &&
                Mathf.Max(from.x, to.x) > Mathf.Min(sourceCell.x, targetCell.x);
        }

        private static void AppendRouteTopologyRhythmRules(
            DungeonRouteTopology topology,
            List<string> violations)
        {
            var mainRoute = new List<RouteTopologyNode>();
            foreach (RouteTopologyNode node in topology.nodes)
            {
                if (node.IsOnMainRoute)
                {
                    mainRoute.Add(node);
                }
            }

            var roleOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            int previousSlotOrder = -1;
            for (int index = 0; index < mainRoute.Count; index++)
            {
                RouteTopologyNode node = mainRoute[index];
                roleOccurrences.TryGetValue(node.role, out int seen);
                roleOccurrences[node.role] = seen + 1;
                if (seen + 1 > MaxMainRouteRoleOccurrences)
                {
                    violations.Add(
                        $"main route uses role '{node.role}' {seen + 1} times (at '{node.key}'); " +
                        $"the limit is {MaxMainRouteRoleOccurrences}");
                }

                if (index > 0)
                {
                    RouteTopologyNode previous = mainRoute[index - 1];
                    if (string.Equals(previous.role, node.role, StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"main-route nodes '{previous.key}' and '{node.key}' repeat role '{node.role}'");
                    }

                    if (string.Equals(previous.beat, node.beat, StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"main-route nodes '{previous.key}' and '{node.key}' repeat beat '{node.beat}'");
                    }
                }

                if (string.IsNullOrEmpty(node.recipeSlotId))
                {
                    continue;
                }

                if (previousSlotOrder >= 0 &&
                    node.mainRouteOrder - previousSlotOrder - 1 < MinimumMainRouteNodesBetweenRecipeSlots)
                {
                    violations.Add(
                        $"recipe-bearing node '{node.key}' is only " +
                        $"{node.mainRouteOrder - previousSlotOrder - 1} main-route nodes after the " +
                        $"previous one; the minimum is {MinimumMainRouteNodesBetweenRecipeSlots}");
                }

                previousSlotOrder = node.mainRouteOrder;
            }
        }

        private static string DescribeLayerBinding(string layerId)
        {
            return string.IsNullOrEmpty(layerId) ? string.Empty : $"#{layerId}";
        }

        // ---- layer schema, loader self-check --------------------------------

        /// <summary>
        /// A three-node graph whose middle node declares an upper storey and
        /// whose exit edge binds to it. Every check below mutates exactly one
        /// thing in this string.
        /// </summary>
        /// <remarks>
        /// The layer schema has NO site in the shipped corpus — that is the
        /// property that makes Phase D's neutrality provable — so nothing else
        /// in the project executes the parser's layer branch. Without this, the
        /// first exercise of the schema would be the first topology to declare
        /// one, which is the arrangement that cost C2 two rejected corpora.
        /// It lives in the topology validator rather than in the orphaned
        /// snapshot family in `.Batch.cs`, because those have no callers.
        /// </remarks>
        private const string LayerSchemaProbeJson = @"{
  ""id"": ""layer-probe"",
  ""displayName"": ""Layer Schema Probe"",
  ""plannerVersion"": ""layer-probe-v1"",
  ""map"": [""A  B  C""],
  ""spatial"": { ""columnGapDeltaCells"": 0, ""rowGapDeltaCells"": 0 },
  ""nodes"": {
    ""A"": [""probe-a"", ""arrival"", ""arrival"", 0, { ""main"": 0 }],
    ""B"": [""probe-b"", ""connector"", ""compression"", 4, { ""main"": 1 }, { ""layers"": { ""gallery"": 4 } }],
    ""C"": [""probe-c"", ""culmination"", ""culmination"", 8, { ""main"": 2 }]
  },
  ""edges"": [[""A"", ""B"", ""Stair""], [""B"", ""C"", ""LevelCorridor"", { ""fromLayer"": ""gallery"" }]],
  ""slots"": [{ ""id"": ""probe-slot"", ""at"": ""B"", ""entry"": ""A-B"", ""exit"": ""B-C"" }],
  ""vista"": { ""id"": ""probe-vista"", ""from"": ""C"", ""to"": ""A"", ""minVoidCells"": 3 },
  ""anchors"": { ""bottom"": ""A"", ""top"": ""C"" }
}";

        private static bool TryParseLayerSchemaProbe(string json, out DungeonRouteTopology topology)
        {
            return TryParseRouteTopology(json, "<probe>/layer-probe.json", out topology, out _);
        }

        // A mutation that silently failed to apply would pass vacuously, so each
        // one reports whether it changed the text at all.
        private static bool LayerSchemaProbeRejects(string find, string replaceWith)
        {
            return LayerSchemaProbeMutationRejected(LayerSchemaProbeJson.Replace(find, replaceWith));
        }

        /// <summary>The same guarantee for a case that has to edit two members.</summary>
        private static bool LayerSchemaProbeMutationRejected(string mutated)
        {
            return !string.Equals(mutated, LayerSchemaProbeJson, StringComparison.Ordinal) &&
                !TryParseLayerSchemaProbe(mutated, out _);
        }

        /// <summary>
        /// Does the lattice-lane rule report a node blocking an edge in this
        /// topology? Asked through the real rule pass, not by re-implementing it.
        /// </summary>
        private static bool LaneRuleReportsBlockingNode(string json, out bool parsed)
        {
            parsed = TryParseLayerSchemaProbe(json, out DungeonRouteTopology topology);
            if (!parsed)
            {
                return false;
            }

            var violations = new List<string>();
            AppendRouteTopologyLatticeRules(topology, violations);
            return violations.Exists(violation => violation.Contains("runs straight through node"));
        }

        private static bool AppendLayerSchemaLoaderChecks(StringBuilder report)
        {
            var results = new List<(string name, bool passed)>();

            bool loaded = TryParseLayerSchemaProbe(LayerSchemaProbeJson, out DungeonRouteTopology probe);
            results.Add(("probeLoads", loaded));

            // B's gallery is +4 from its own level of 4, so the bound edge leaves
            // at absolute 8 and arrives at C's 8 — a LevelCorridor across a 0u
            // rise. Unbound, the same edge would be a 4u rise and the validator
            // would reject it as a LevelCorridor. That contrast is the whole
            // point: the binding, not the node level, decides the elevation.
            bool bindingResolves =
                loaded &&
                probe.nodes[1].DeclaresLayers &&
                probe.nodes[1].TryGetAbsoluteLevel("gallery", out int galleryLevel) &&
                galleryLevel == 8 &&
                probe.nodes[1].TryGetAbsoluteLevel(string.Empty, out int baseLevel) &&
                baseLevel == 4 &&
                probe.DeclaresLayers;
            results.Add(("bindingResolvesToAbsoluteLevel", bindingResolves));

            bool unboundEdgeUnchanged =
                loaded &&
                !probe.edges[0].IsLayerBound &&
                probe.edges[1].IsLayerBound &&
                string.Equals(probe.edges[1].fromLayerId, "gallery", StringComparison.Ordinal) &&
                probe.edges[1].toLayerId.Length == 0;
            results.Add(("unboundEdgeCarriesNoBinding", unboundEdgeUnchanged));

            // A graph declaring no layers must parse to exactly what it parsed to
            // before the schema existed.
            bool unlayeredIsInert =
                TryParseLayerSchemaProbe(
                    LayerSchemaProbeJson
                        .Replace(", { \"layers\": { \"gallery\": 4 } }", string.Empty)
                        .Replace("\"LevelCorridor\", { \"fromLayer\": \"gallery\" }", "\"Stair\""),
                    out DungeonRouteTopology unlayered) &&
                !unlayered.DeclaresLayers &&
                !unlayered.edges[1].IsLayerBound;
            results.Add(("unlayeredGraphIsInert", unlayeredIsInert));

            results.Add(("offPitchLayerRejected",
                LayerSchemaProbeRejects("\"gallery\": 4", "\"gallery\": 3")));
            results.Add(("outOfEnvelopeLayerRejected",
                LayerSchemaProbeRejects("\"gallery\": 4", "\"gallery\": -8")));
            results.Add(("duplicateLayerLevelRejected",
                LayerSchemaProbeRejects("\"gallery\": 4", "\"gallery\": 4, \"balcony\": 4")));
            results.Add(("twoBaseLayersRejected",
                LayerSchemaProbeRejects("\"gallery\": 4", "\"gallery\": 4, \"floor\": 0, \"ground\": 0")));
            results.Add(("emptyLayerTableRejected",
                LayerSchemaProbeRejects("{ \"layers\": { \"gallery\": 4 } }", "{ \"layers\": { } }")));
            results.Add(("unknownNodeOptionRejected",
                LayerSchemaProbeRejects("{ \"layers\": { \"gallery\": 4 } }", "{ \"storeys\": { \"gallery\": 4 } }")));
            results.Add(("undeclaredBindingRejected",
                LayerSchemaProbeRejects("\"fromLayer\": \"gallery\"", "\"fromLayer\": \"catwalk\"")));
            results.Add(("bindingOnUndeclaringNodeRejected",
                LayerSchemaProbeRejects("\"fromLayer\": \"gallery\"", "\"toLayer\": \"gallery\"")));
            results.Add(("nonObjectEdgeOptionRejected",
                LayerSchemaProbeRejects("{ \"fromLayer\": \"gallery\" }", "\"gallery\"")));
            results.Add(("fiveFieldEdgeRejected",
                LayerSchemaProbeRejects(
                    "{ \"fromLayer\": \"gallery\" }",
                    "{ \"fromLayer\": \"gallery\" }, \"extra\"")));

            // The two rules the loader cannot state, checked through the
            // validator itself rather than by re-implementing them here.
            bool unboundLayerReported = false;
            bool slotlessLayerReported = false;
            if (TryParseLayerSchemaProbe(
                    LayerSchemaProbeJson.Replace(
                        "\"LevelCorridor\", { \"fromLayer\": \"gallery\" }",
                        "\"Stair\""),
                    out DungeonRouteTopology unbound))
            {
                var violations = new List<string>();
                ValidateNodeLayers(1, unbound.nodes[1], unbound, violations);
                unboundLayerReported = violations.Exists(v => v.Contains("which no edge binds"));
            }

            // Move the storey onto C, which carries no slot, and bind the same
            // edge to it from the other end. C's node index is 2 — main-route
            // order, which is what the loader sorts by.
            if (TryParseLayerSchemaProbe(
                    LayerSchemaProbeJson
                        .Replace(", { \"layers\": { \"gallery\": 4 } }", string.Empty)
                        .Replace(
                            "\"C\": [\"probe-c\", \"culmination\", \"culmination\", 8, { \"main\": 2 }]",
                            "\"C\": [\"probe-c\", \"culmination\", \"culmination\", 8, { \"main\": 2 }, { \"layers\": { \"gallery\": 4 } }]")
                        .Replace("\"fromLayer\": \"gallery\"", "\"toLayer\": \"gallery\""),
                    out DungeonRouteTopology slotless))
            {
                var violations = new List<string>();
                ValidateNodeLayers(2, slotless.nodes[2], slotless, violations);
                slotlessLayerReported = violations.Exists(v => v.Contains("carries no recipe slot")) &&
                    !violations.Exists(v => v.Contains("which no edge binds"));
            }

            // D2: a BASE-ONLY table declares no storey, so it needs no producer
            // and no recipe slot — it exists to give an edge something to bind,
            // which is how a topology authorizes a stacked corridor crossing.
            // Same mutation as the slotless case above, with the storey moved to
            // the node's own elevation.
            bool baseOnlyLayerAccepted = false;
            if (TryParseLayerSchemaProbe(
                    LayerSchemaProbeJson
                        .Replace(", { \"layers\": { \"gallery\": 4 } }", string.Empty)
                        .Replace(
                            "\"C\": [\"probe-c\", \"culmination\", \"culmination\", 8, { \"main\": 2 }]",
                            "\"C\": [\"probe-c\", \"culmination\", \"culmination\", 8, { \"main\": 2 }, { \"layers\": { \"floor\": 0 } }]")
                        .Replace("\"fromLayer\": \"gallery\"", "\"toLayer\": \"floor\""),
                    out DungeonRouteTopology baseOnly))
            {
                var violations = new List<string>();
                ValidateNodeLayers(2, baseOnly.nodes[2], baseOnly, violations);
                baseOnlyLayerAccepted =
                    violations.Count == 0 &&
                    baseOnly.nodes[2].DeclaresLayers &&
                    !baseOnly.nodes[2].DeclaresStoreys &&
                    baseOnly.edges[1].IsLayerBound &&
                    baseOnly.nodes[2].TryGetAbsoluteLevel("floor", out int boundBaseLevel) &&
                    boundBaseLevel == 8;
            }

            // D2: the lattice-lane rule, author-time mirror of
            // `PathCrossesThirdRoom`. A's storey at +8 puts the A-C edge's band
            // at [8, 11) and B keeps only its own level 4, so the bound edge
            // clears B and the unbound one — band [0, 11) — does not. One
            // variable between the two.
            string laneProbe = LayerSchemaProbeJson
                .Replace(", { \"layers\": { \"gallery\": 4 } }", string.Empty)
                .Replace(
                    "\"A\": [\"probe-a\", \"arrival\", \"arrival\", 0, { \"main\": 0 }]",
                    "\"A\": [\"probe-a\", \"arrival\", \"arrival\", 0, { \"main\": 0 }, { \"layers\": { \"sky\": 8 } }]");
            bool laneNodeBlocksUnboundEdge = LaneRuleReportsBlockingNode(
                laneProbe.Replace(
                    "[\"B\", \"C\", \"LevelCorridor\", { \"fromLayer\": \"gallery\" }]",
                    "[\"B\", \"C\", \"LevelCorridor\"], [\"A\", \"C\", \"LevelCorridor\"]"),
                out bool unboundLaneParsed);
            bool laneNodeClearedByBoundEdge = !LaneRuleReportsBlockingNode(
                laneProbe.Replace(
                    "[\"B\", \"C\", \"LevelCorridor\", { \"fromLayer\": \"gallery\" }]",
                    "[\"B\", \"C\", \"LevelCorridor\"], [\"A\", \"C\", \"LevelCorridor\", { \"fromLayer\": \"sky\" }]"),
                out bool boundLaneParsed);

            // D3: the slot's storey mapping. The node already declares 'gallery'
            // and the probe's slot sits on that node, so every case here is one
            // edit to the slot object.
            const string bareSlot =
                "{ \"id\": \"probe-slot\", \"at\": \"B\", \"entry\": \"A-B\", \"exit\": \"B-C\" }";
            const string mappedSlot =
                "{ \"id\": \"probe-slot\", \"at\": \"B\", \"entry\": \"A-B\", \"exit\": \"B-C\", " +
                "\"layers\": { \"gallery\": \"upper\" } }";
            bool slotMapParses =
                TryParseLayerSchemaProbe(
                    LayerSchemaProbeJson.Replace(bareSlot, mappedSlot),
                    out DungeonRouteTopology mapped) &&
                mapped.slots[0].DeclaresLayers &&
                mapped.slots[0].layers.Length == 1 &&
                string.Equals(
                    mapped.slots[0].layers[0].topologyLayerId,
                    "gallery",
                    StringComparison.Ordinal) &&
                string.Equals(
                    mapped.slots[0].layers[0].recipeLayerId,
                    "upper",
                    StringComparison.Ordinal);
            results.Add(("slotLayerMapParses", slotMapParses));

            // The shipped shape: no mapping member at all, and therefore no
            // bindings. This is what makes the whole of D3's recipe side inert
            // on every topology in the corpus.
            results.Add(("unmappedSlotCarriesNoBinding", loaded && !probe.slots[0].DeclaresLayers));

            results.Add(("slotLayerMapUndeclaredRejected",
                LayerSchemaProbeRejects(
                    bareSlot,
                    mappedSlot.Replace("\"gallery\": \"upper\"", "\"catwalk\": \"upper\""))));
            results.Add(("slotLayerMapEmptyRejected",
                LayerSchemaProbeRejects(
                    bareSlot,
                    mappedSlot.Replace("{ \"gallery\": \"upper\" }", "{ }"))));
            results.Add(("slotLayerMapNonObjectRejected",
                LayerSchemaProbeRejects(
                    bareSlot,
                    mappedSlot.Replace("{ \"gallery\": \"upper\" }", "\"upper\""))));
            results.Add(("slotLayerMapNonStringTargetRejected",
                LayerSchemaProbeRejects(
                    bareSlot,
                    mappedSlot.Replace("\"gallery\": \"upper\"", "\"gallery\": 4"))));
            // Two storeys onto one recipe storey would collapse two elevations
            // into one place. 'floor' has to be declared on B for the mapping to
            // reach the duplicate check at all, so this one edits two members.
            results.Add(("slotLayerMapDuplicateTargetRejected",
                LayerSchemaProbeMutationRejected(
                    LayerSchemaProbeJson
                        .Replace(
                            "{ \"layers\": { \"gallery\": 4 } }",
                            "{ \"layers\": { \"gallery\": 4, \"floor\": 0 } }")
                        .Replace(
                            bareSlot,
                            mappedSlot.Replace(
                                "\"gallery\": \"upper\"",
                                "\"floor\": \"upper\", \"gallery\": \"upper\"")))));

            results.Add(("unboundLayerReported", unboundLayerReported));
            results.Add(("slotlessLayerReported", slotlessLayerReported));
            results.Add(("baseOnlyLayerNeedsNoSlot", baseOnlyLayerAccepted));
            results.Add(("laneNodeBlocksUnboundEdge", unboundLaneParsed && laneNodeBlocksUnboundEdge));
            results.Add(("laneNodeClearedByBoundEdge", boundLaneParsed && laneNodeClearedByBoundEdge));

            bool allPassed = true;
            report.AppendLine().AppendLine("Layer schema — loader self-check (design §8.1)");
            foreach ((string name, bool checkPassed) in results)
            {
                allPassed &= checkPassed;
                report.Append("    ")
                    .Append(checkPassed ? "ok   " : "FAIL ")
                    .AppendLine(name);
            }

            report.Append("    ")
                .Append(allPassed ? "PASS" : "FAIL")
                .Append("  ")
                .Append(results.Count)
                .AppendLine(" layer schema checks");
            return allPassed;
        }

        /// <summary>
        /// The two layer rules the LOADER cannot state, because both are about a
        /// node's relationship to the rest of the graph rather than about its own
        /// syntax.
        /// </summary>
        /// <remarks>
        /// The loader already rejects a malformed table, an off-pitch offset, an
        /// out-of-envelope absolute level, two ids at one elevation and an edge
        /// binding a layer its endpoint does not declare. What is left is
        /// authoring intent: a storey nothing routes to, and a storey nothing can
        /// build.
        /// </remarks>
        private static void ValidateNodeLayers(
            int nodeIndex,
            RouteTopologyNode node,
            DungeonRouteTopology topology,
            List<string> violations)
        {
            if (!node.DeclaresLayers)
            {
                return;
            }

            // A storey no edge binds is an authored intent nothing consumes. It
            // is exactly the class of mistake a beat typo was in C2 — the graph
            // validates, generation runs, and the feature is silently absent.
            foreach (RouteTopologyLayer layer in node.layers)
            {
                if (layer.relativeLevel == 0)
                {
                    continue;
                }

                bool bound = false;
                foreach (RouteTopologyEdge edge in topology.edges)
                {
                    bound |= edge.fromNode == nodeIndex &&
                        string.Equals(edge.fromLayerId, layer.layerId, StringComparison.Ordinal);
                    bound |= edge.toNode == nodeIndex &&
                        string.Equals(edge.toLayerId, layer.layerId, StringComparison.Ordinal);
                }

                if (!bound)
                {
                    violations.Add(
                        $"node '{node.key}' ({node.id}) declares layer '{layer.layerId}' at " +
                        $"+{layer.relativeLevel}u, which no edge binds; a storey no route reaches " +
                        "generates as nothing");
                }
            }

            // Nothing in the generator builds a stacked surface for a GENERIC
            // room: the producers are the aerial-span deck, a recipe's non-base
            // storey, and (D2) a layer-bound corridor's crossing cells. The
            // first two build a ROOM's storey; the third does not, so a node
            // that declares a real storey must still carry a recipe slot or its
            // storeys have no author. Relax the rest of this when a generic
            // multi-layer ROOM producer exists (design §13 Phase D, owner
            // decision 9).
            //
            // D2: a table of nothing but base layers declares no storey at all.
            // It names the node's own elevation so an edge can bind it, which is
            // how a topology AUTHORIZES a stacked corridor crossing — and the
            // "no edge binds" rule above already exempts a base layer for the
            // same reason. Requiring a recipe slot for it would make the
            // authorization unreachable outside a slot node.
            if (!node.DeclaresStoreys)
            {
                return;
            }

            if (string.IsNullOrEmpty(node.recipeSlotId))
            {
                violations.Add(
                    $"node '{node.key}' ({node.id}) declares layers but carries no recipe slot; " +
                    "only a recipe's non-base storey can build one today, so the layers would " +
                    "have no producer");
            }
        }

        // ---- per-density rules --------------------------------------------

        // Everything the per-density rules below actually read. Two densities
        // with the same description produce the same report block.
        private static string DescribeResolvedSpatialSettings(DungeonRouteTopology topology)
        {
            DungeonGenerationSettings settings = CurrentGenerationSettings.Validated();
            DungeonPatternSpatialSettings spatial = ResolveTopologySpatialSettings(topology);
            return string.Join(
                "|",
                settings.mapWidthMaxCells,
                settings.mapDepthMaxCells,
                spatial.horizontalPitchCells,
                spatial.verticalPitchCells,
                spatial.roomEnvelopeRadiusCells,
                spatial.neighborBiasStrengthCells,
                spatial.latticeSlackMaxCells,
                spatial.tierSeamAdjacency.requestedCount,
                spatial.tierSeamAdjacency.maximumRiseLevels,
                DescribeRoomSizeRange(spatial.terminalRoomSize),
                DescribeRoomSizeRange(spatial.hallRoomSize),
                DescribeRoomSizeRange(spatial.connectorRoomSize));
        }

        private static string DescribeRoomSizeRange(DungeonRoomSizeRange range)
        {
            return $"{range.minWidthCells},{range.maxWidthCells},{range.minDepthCells},{range.maxDepthCells}";
        }

        private static void AppendRouteTopologyProfileRules(
            DungeonRouteTopology topology,
            string densityLabel,
            List<string> violations,
            List<string> notes)
        {
            DungeonGenerationSettings settings = CurrentGenerationSettings.Validated();
            DungeonPatternSpatialSettings spatial = ResolveTopologySpatialSettings(topology);
            // The tightest lattice the rubber sheet can produce. Every other
            // rule below is worst-cased against it: more slack only ever moves
            // rooms apart, so the minimum lattice is where a vista lane is
            // shortest and rooms are most likely to collide.
            int[] columnOffsets = MinimumLatticeLaneOffsets(
                topology.columnGaps,
                spatial.horizontalPitchCells,
                topology.latticeColumnCount);
            int[] rowOffsets = MinimumLatticeLaneOffsets(
                topology.rowGaps,
                spatial.verticalPitchCells,
                topology.latticeRowCount);
            AppendRouteTopologyRubberSheetNotes(topology, densityLabel, spatial, columnOffsets, rowOffsets, notes);
            AppendRouteTopologyRoleSizeClassRules(topology, densityLabel, settings, violations);

            // Across the 8 orientations a quarter turn swaps the two spans, so
            // the worst case on each axis is simply the wider of them — plus
            // whatever slack the rubber sheet may spend on that axis.
            int envelopeSpan = spatial.roomEnvelopeRadiusCells * 2 + 1;
            int columnSpan = columnOffsets[columnOffsets.Length - 1] +
                LatticeSlackBudget(topology.columnGaps, spatial.horizontalPitchCells, spatial);
            int rowSpan = rowOffsets[rowOffsets.Length - 1] +
                LatticeSlackBudget(topology.rowGaps, spatial.verticalPitchCells, spatial);
            int worstAxis = Mathf.Max(columnSpan, rowSpan) + envelopeSpan;
            notes.Add(
                $"{densityLabel}: envelope worst case {worstAxis}x{worstAxis} of " +
                $"{settings.mapWidthMaxCells}x{settings.mapDepthMaxCells} across all 8 orientations " +
                $"at the widest lattice; minimum lane offsets x[{string.Join(",", columnOffsets)}] " +
                $"y[{string.Join(",", rowOffsets)}]");
            if (worstAxis > settings.mapWidthMaxCells || worstAxis > settings.mapDepthMaxCells)
            {
                violations.Add(
                    $"{densityLabel}: plan reaches {worstAxis} cells against " +
                    $"{settings.mapWidthMaxCells}x{settings.mapDepthMaxCells}; " +
                    "TryTransformCoarseEmbedding only tries 4 quarter-turns against one mirror choice, " +
                    "so an overflow here can reject the whole layout attempt");
            }

            AppendRouteTopologyOverlookRules(topology, densityLabel, spatial, violations, notes);
            AppendRouteTopologyRoomSizeRules(
                topology,
                densityLabel,
                spatial,
                columnOffsets,
                rowOffsets,
                violations,
                notes);
            AppendRouteTopologyVistaLaneRules(
                topology,
                densityLabel,
                spatial,
                columnOffsets,
                rowOffsets,
                violations,
                notes);
        }

        // The rubber sheet is invisible in the map, so the report says how much
        // it can actually move — and where the ceiling comes from, since the
        // envelope and the profile cap both bite before the authored range does.
        private static void AppendRouteTopologyRubberSheetNotes(
            DungeonRouteTopology topology,
            string densityLabel,
            DungeonPatternSpatialSettings spatial,
            int[] columnOffsets,
            int[] rowOffsets,
            List<string> notes)
        {
            int columnSlack = LatticeSlackBudget(
                topology.columnGaps,
                spatial.horizontalPitchCells,
                spatial);
            int rowSlack = LatticeSlackBudget(topology.rowGaps, spatial.verticalPitchCells, spatial);
            int columnHeadroom = LatticeAuthoredHeadroom(topology.columnGaps, spatial.horizontalPitchCells);
            int rowHeadroom = LatticeAuthoredHeadroom(topology.rowGaps, spatial.verticalPitchCells);
            notes.Add(
                $"{densityLabel}: rubber sheet may add {columnSlack} cells across the columns and " +
                $"{rowSlack} across the rows (authored headroom {columnHeadroom}/{rowHeadroom}, " +
                $"profile cap {spatial.latticeSlackMaxCells}); " +
                (columnSlack == 0 && rowSlack == 0
                    ? "every lane is fixed, so this topology has one lattice"
                    : $"lattices per axis are combinatorial in that slack"));
            if (columnSlack < columnHeadroom || rowSlack < rowHeadroom)
            {
                notes.Add(
                    $"{densityLabel}: the authored gap ranges are wider than the budget — the map envelope " +
                    $"({columnOffsets[columnOffsets.Length - 1]}/{rowOffsets[rowOffsets.Length - 1]} " +
                    $"minimum span) or latticeSlackMaxCells is the binding constraint, not the ranges");
            }
        }

        // Every role a topology declares has to name a size class, or its rooms
        // would silently render at the hall size. Reported per profile because
        // the map is a profile field.
        private static void AppendRouteTopologyRoleSizeClassRules(
            DungeonRouteTopology topology,
            string densityLabel,
            DungeonGenerationSettings settings,
            List<string> violations)
        {
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (RouteTopologyNode node in topology.nodes)
            {
                if (!string.IsNullOrEmpty(node.recipeSlotId) || !reported.Add(node.role))
                {
                    continue;
                }

                if (!settings.TryResolveRoomSizeClass(node.role, out _))
                {
                    violations.Add(
                        $"{densityLabel}: node '{node.key}' ({node.id}) declares role '{node.role}', which the " +
                        "profile's roleSizeClasses map does not cover; add it there with a size class");
                }
            }
        }

        private static void AppendRouteTopologyRoomSizeRules(
            DungeonRouteTopology topology,
            string densityLabel,
            DungeonPatternSpatialSettings spatial,
            int[] columnOffsets,
            int[] rowOffsets,
            List<string> violations,
            List<string> notes)
        {
            int envelopeSpan = spatial.roomEnvelopeRadiusCells * 2 + 1;
            int largestExtent = 0;
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (RouteTopologyNode node in topology.nodes)
            {
                if (!string.IsNullOrEmpty(node.recipeSlotId) || !reported.Add(node.role))
                {
                    continue;
                }

                DungeonRoomSizeRange range = RoomSizeRangeForRole(spatial, node.role).Validated();
                largestExtent = Mathf.Max(
                    largestExtent,
                    Mathf.Max(range.maxWidthCells, range.maxDepthCells));

                // The envelope is a hard check: RoomFitsEnvelope rejects a room
                // that leaves it, and a recipe room that does so fails the whole
                // layout attempt rather than retrying.
                if (range.maxWidthCells > envelopeSpan || range.maxDepthCells > envelopeSpan)
                {
                    violations.Add(
                        $"{densityLabel}: role '{node.role}' may reach " +
                        $"{range.maxWidthCells}x{range.maxDepthCells}, outside the {envelopeSpan}-cell " +
                        "placement envelope");
                }
            }

            // Lane gap vs room extent is a pressure reading, not a rule: a room
            // is clamped to its own adjacent lanes at inflation time, so a tight
            // lane costs the rooms beside it a cell or two rather than failing —
            // and costs the rest of the axis nothing. Reported so an author can
            // see which lane is doing the squeezing.
            int tightestGap = int.MaxValue;
            string tightestLane = string.Empty;
            for (int lane = 1; lane < columnOffsets.Length; lane++)
            {
                int gap = columnOffsets[lane] - columnOffsets[lane - 1];
                if (gap < tightestGap)
                {
                    tightestGap = gap;
                    tightestLane = $"x{lane - 1}->x{lane}";
                }
            }

            for (int lane = 1; lane < rowOffsets.Length; lane++)
            {
                int gap = rowOffsets[lane] - rowOffsets[lane - 1];
                if (gap < tightestGap)
                {
                    tightestGap = gap;
                    tightestLane = $"y{lane - 1}->y{lane}";
                }
            }

            if (tightestGap != int.MaxValue)
            {
                notes.Add(
                    $"{densityLabel}: tightest lane gap {tightestGap} cells ({tightestLane}) against a " +
                    $"largest generic room extent of {largestExtent}" +
                    (largestExtent >= tightestGap
                        ? $" — the rooms on that lane are clamped to {tightestGap} there, and only there"
                        : string.Empty));
            }
        }

        private static void AppendRouteTopologyVistaLaneRules(
            DungeonRouteTopology topology,
            string densityLabel,
            DungeonPatternSpatialSettings spatial,
            int[] columnOffsets,
            int[] rowOffsets,
            List<string> violations,
            List<string> notes)
        {
            Vector2Int sourceCell = topology.nodes[topology.vistaSourceNode].lattice;
            Vector2Int targetCell = topology.nodes[topology.vistaTargetNode].lattice;
            if (sourceCell.x != targetCell.x && sourceCell.y != targetCell.y)
            {
                return;
            }

            int laneCells = sourceCell.x == targetCell.x
                ? Mathf.Abs(rowOffsets[targetCell.y] - rowOffsets[sourceCell.y])
                : Mathf.Abs(columnOffsets[targetCell.x] - columnOffsets[sourceCell.x]);
            // Both ends are capped against the lane's own required clear run by
            // ResolveGenericRoomDimensions before they are ever drawn, so the
            // worst case is the capped extent rather than the range maximum.
            // Modelling only the range made the rule fire on twin-wing-keep at
            // densities 3-5 for a lane the generator was already protecting —
            // the corpus generates 200/200 there. A rule that does not model the
            // generator reports the generator's caps as violations.
            int sourceReach = RouteTopologyVistaHalfExtent(
                topology,
                topology.vistaSourceNode,
                spatial,
                laneCells);
            int targetReach = RouteTopologyVistaHalfExtent(
                topology,
                topology.vistaTargetNode,
                spatial,
                laneCells);
            // TryReserveProcessionalVista walks from the source room's boundary
            // cell to the target's, so the clear count is the gap between faces.
            int clearCells = laneCells - sourceReach - targetReach - 1;
            int steps = Mathf.Abs(targetCell.x - sourceCell.x) + Mathf.Abs(targetCell.y - sourceCell.y);
            notes.Add(
                $"{densityLabel}: vista '{topology.vistaId}' {steps} lattice step(s), {laneCells} cells " +
                $"centre to centre, worst-case reach {sourceReach}+{targetReach} => " +
                $"{clearCells} clear cell(s), required {topology.vistaMinimumVoidCells}" +
                (clearCells == topology.vistaMinimumVoidCells ? " (zero margin)" : string.Empty));
            if (clearCells < topology.vistaMinimumVoidCells)
            {
                violations.Add(
                    $"{densityLabel}: vista '{topology.vistaId}' can shrink to {clearCells} clear cell(s), " +
                    $"below the required {topology.vistaMinimumVoidCells}; move the pair at least one more " +
                    "lattice step apart");
            }
        }

        // A vista endpoint as the GENERATOR will actually size it: the role
        // range, then capped against the lane's own required clear run exactly
        // as ResolveGenericRoomDimensions caps it. A recipe room is authored and
        // takes no cap, which is why a recipe on a vista endpoint is still worth
        // flagging.
        private static int RouteTopologyVistaHalfExtent(
            DungeonRouteTopology topology,
            int node,
            DungeonPatternSpatialSettings spatial,
            int laneCells)
        {
            int reach = RouteTopologyWorstCaseHalfExtent(topology, node, spatial);
            if (!string.IsNullOrEmpty(topology.nodes[node].recipeSlotId))
            {
                return reach;
            }

            int capped = MaxRoomExtentForTransition(laneCells, topology.vistaMinimumVoidCells);
            return Mathf.Min(reach, capped / 2);
        }

        // The largest number of cells a room at this node can put between its
        // centre and the vista lane, taken over every orientation. Recipe rooms
        // use their authored zones; generic rooms use the profile's role range,
        // narrowed by the planned-overlook force-shrink.
        private static int RouteTopologyWorstCaseHalfExtent(
            DungeonRouteTopology topology,
            int node,
            DungeonPatternSpatialSettings spatial)
        {
            RouteTopologyNode declared = topology.nodes[node];
            if (!string.IsNullOrEmpty(declared.recipeSlotId))
            {
                return RouteTopologyWorstCaseRecipeReach(topology, node, declared, spatial);
            }

            int width;
            int depth;
            if (RouteTopologyHasOverlookAppendage(topology, node))
            {
                // BuildProcessionalRoomParts forces this footprint.
                width = 4;
                depth = 5;
            }
            else
            {
                DungeonRoomSizeRange range = RoomSizeRangeForRole(spatial, declared.role).Validated();
                width = range.maxWidthCells;
                depth = range.maxDepthCells;
            }

            // CenteredRect puts the far face at ceil(size / 2) - 1 on the plus
            // side and size / 2 on the minus side; a rotation can face either.
            return Mathf.Max(width / 2, depth / 2);
        }

        private static int RouteTopologyWorstCaseRecipeReach(
            DungeonRouteTopology topology,
            int node,
            RouteTopologyNode declared,
            DungeonPatternSpatialSettings spatial)
        {
            // A slot oriented off the vista axis puts its recipe's PRIMARY axis
            // along the lane, so only the primary extent faces the vista. A
            // route-forward slot's axis follows its exit edge, which may be
            // either, so both extents count.
            bool primaryAxisOnly = false;
            foreach (RouteTopologySlot slot in topology.slots)
            {
                if (slot.node == node &&
                    slot.orientationBinding == RecipeOrientationBinding.VistaSourceToTarget)
                {
                    primaryAxisOnly = true;
                }
            }

            int reach = 0;
            bool sawCandidate = false;
            if (DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out _))
            {
                foreach (DungeonRecipeAsset candidate in catalog.recipes)
                {
                    if (candidate == null ||
                        Array.IndexOf(candidate.eligibleRoles, declared.role) < 0 ||
                        Array.IndexOf(candidate.eligibleBeats, declared.beat) < 0)
                    {
                        continue;
                    }

                    sawCandidate = true;
                    foreach (DungeonRecipeZone zone in candidate.zones ?? Array.Empty<DungeonRecipeZone>())
                    {
                        if (zone == null ||
                            zone.kind != DungeonRecipeZoneKind.Walkable &&
                            zone.kind != DungeonRecipeZoneKind.Elevated)
                        {
                            continue;
                        }

                        reach = Mathf.Max(reach, Mathf.Abs(zone.offset.x));
                        reach = Mathf.Max(reach, Mathf.Abs(zone.offset.x + zone.size.x - 1));
                        if (primaryAxisOnly)
                        {
                            continue;
                        }

                        reach = Mathf.Max(reach, Mathf.Abs(zone.offset.y));
                        reach = Mathf.Max(reach, Mathf.Abs(zone.offset.y + zone.size.y - 1));
                    }
                }
            }

            // No readable catalog, or no eligible recipe yet: fall back to the
            // hard cap every recipe footprint has to fit inside.
            return sawCandidate ? reach : spatial.roomEnvelopeRadiusCells;
        }

        // BuildPlannedOverlooks accepts only non-traversal pairs whose rise is a
        // whole number of majors inside the profile's ceiling, and it throws when
        // fewer than the requested count survive. Same filter, reported instead.
        private static List<RouteOverlookIntent> RouteTopologySelectedOverlooks(
            DungeonRouteTopology topology,
            DungeonTierSeamAdjacencySettings seams)
        {
            var selected = new List<RouteOverlookIntent>(seams.requestedCount);
            foreach (RouteOverlookIntent pair in topology.overlooks)
            {
                if (selected.Count >= seams.requestedCount)
                {
                    break;
                }

                bool isTraversal = false;
                foreach (RouteTopologyEdge edge in topology.edges)
                {
                    isTraversal |=
                        edge.fromNode == pair.firstNode && edge.toNode == pair.secondNode ||
                        edge.fromNode == pair.secondNode && edge.toNode == pair.firstNode;
                }

                int rise = Mathf.Abs(
                    topology.nodes[pair.firstNode].level - topology.nodes[pair.secondNode].level);
                if (!isTraversal &&
                    rise >= MajorRiseLevels &&
                    rise <= seams.maximumRiseLevels &&
                    rise % MajorRiseLevels == 0)
                {
                    selected.Add(pair);
                }
            }

            return selected;
        }

        private static void AppendRouteTopologyOverlookRules(
            DungeonRouteTopology topology,
            string densityLabel,
            DungeonPatternSpatialSettings spatial,
            List<string> violations,
            List<string> notes)
        {
            DungeonTierSeamAdjacencySettings seams = spatial.tierSeamAdjacency.Validated();
            List<RouteOverlookIntent> selected = RouteTopologySelectedOverlooks(topology, seams);
            if (selected.Count != seams.requestedCount)
            {
                violations.Add(
                    $"{densityLabel}: the profile requests {seams.requestedCount} tier-seam adjacencies but " +
                    $"only {selected.Count} of the {topology.overlooks.Length} declared 'overlooks' pairs " +
                    $"are eligible (non-traversal, rise a multiple of {MajorRiseLevels} up to " +
                    $"{seams.maximumRiseLevels}u); generation throws on this");
                return;
            }

            if (selected.Count == 0)
            {
                return;
            }

            var described = new List<string>(selected.Count);
            foreach (RouteOverlookIntent pair in selected)
            {
                described.Add(
                    $"{topology.nodes[pair.firstNode].key}/{topology.nodes[pair.secondNode].key} " +
                    $"({Mathf.Abs(topology.nodes[pair.firstNode].level - topology.nodes[pair.secondNode].level)}u)");
            }

            notes.Add($"{densityLabel}: tier seams {string.Join(", ", described)} (these rooms shrink to 4x5)");
        }

        private static bool RouteTopologyHasOverlookAppendage(DungeonRouteTopology topology, int node)
        {
            DungeonPatternSpatialSettings spatial = ResolveTopologySpatialSettings(topology);
            foreach (RouteOverlookIntent pair in RouteTopologySelectedOverlooks(
                         topology,
                         spatial.tierSeamAdjacency.Validated()))
            {
                if (pair.firstNode == node || pair.secondNode == node)
                {
                    return true;
                }
            }

            return false;
        }

        // ---- rendering ----------------------------------------------------

        // The map with its edges drawn between cells, so a misalignment or a
        // corridor through a third room is visible rather than inferred.
        private static string RenderRouteTopologyMap(DungeonRouteTopology topology)
        {
            const int cellWidth = 4;
            int width = topology.latticeColumnCount * cellWidth;
            int height = topology.latticeRowCount * 2 - 1;
            var canvas = new char[height, width];
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    canvas[row, column] = ' ';
                }
            }

            foreach (RouteTopologyNode node in topology.nodes)
            {
                int row = (topology.latticeRowCount - 1 - node.lattice.y) * 2;
                canvas[row, node.lattice.x * cellWidth] = node.key.Length > 0 ? node.key[0] : '?';
            }

            foreach (RouteTopologyEdge edge in topology.edges)
            {
                Vector2Int from = topology.nodes[edge.fromNode].lattice;
                Vector2Int to = topology.nodes[edge.toNode].lattice;
                char glyph = RouteTopologyEdgeGlyph(edge.transitionKind, from.y == to.y);
                if (from.y == to.y && from.x != to.x)
                {
                    int row = (topology.latticeRowCount - 1 - from.y) * 2;
                    int firstColumn = Mathf.Min(from.x, to.x) * cellWidth + 1;
                    int lastColumn = Mathf.Max(from.x, to.x) * cellWidth - 1;
                    for (int column = firstColumn; column <= lastColumn; column++)
                    {
                        canvas[row, column] = canvas[row, column] == ' ' ? glyph : canvas[row, column];
                    }
                }
                else if (from.x == to.x && from.y != to.y)
                {
                    int column = from.x * cellWidth;
                    int firstRow = (topology.latticeRowCount - 1 - Mathf.Max(from.y, to.y)) * 2 + 1;
                    int lastRow = (topology.latticeRowCount - 1 - Mathf.Min(from.y, to.y)) * 2 - 1;
                    for (int row = firstRow; row <= lastRow; row++)
                    {
                        canvas[row, column] = canvas[row, column] == ' ' ? glyph : canvas[row, column];
                    }
                }
            }

            var rendered = new StringBuilder();
            for (int row = 0; row < height; row++)
            {
                rendered.Append("    ");
                for (int column = 0; column < width; column++)
                {
                    rendered.Append(canvas[row, column]);
                }

                rendered.AppendLine();
            }

            rendered.AppendLine("    legend  - | level  = : stair  ~ ! stairwell  + bridge");
            return rendered.ToString();
        }

        private static char RouteTopologyEdgeGlyph(RouteTransitionKind kind, bool horizontal)
        {
            switch (kind)
            {
                case RouteTransitionKind.Stair:
                    return horizontal ? '=' : ':';
                case RouteTransitionKind.Stairwell:
                    return horizontal ? '~' : '!';
                case RouteTransitionKind.Bridge:
                    return '+';
                default:
                    return horizontal ? '-' : '|';
            }
        }

        private static string RenderRouteTopologyMetrics(DungeonRouteTopology topology)
        {
            List<int>[] adjacency = topology.BuildAdjacency();
            var junctions = new List<string>();
            for (int node = 0; node < topology.nodes.Length; node++)
            {
                if (adjacency[node].Count >= 3)
                {
                    junctions.Add($"{topology.nodes[node].key}:{adjacency[node].Count}");
                }
            }

            var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (RouteTopologyEdge edge in topology.edges)
            {
                string kind = edge.transitionKind.ToString();
                kinds.TryGetValue(kind, out int seen);
                kinds[kind] = seen + 1;
            }

            var kindSummary = new List<string>(kinds.Count);
            foreach (KeyValuePair<string, int> entry in kinds)
            {
                kindSummary.Add($"{entry.Key} {entry.Value}");
            }

            int mainRouteCount = 0;
            int minLevel = int.MaxValue;
            int maxLevel = int.MinValue;
            foreach (RouteTopologyNode node in topology.nodes)
            {
                mainRouteCount += node.IsOnMainRoute ? 1 : 0;
                minLevel = Mathf.Min(minLevel, node.level);
                maxLevel = Mathf.Max(maxLevel, node.level);
            }

            var metrics = new StringBuilder();
            metrics.Append("    derived  ")
                .Append(topology.nodes.Length).Append(" nodes (")
                .Append(mainRouteCount).Append(" main), ")
                .Append(topology.edges.Length).Append(" edges, cycle rank ")
                .Append(topology.edges.Length - (topology.nodes.Length - 1))
                .Append(", cycle core ").Append(CountCycleCoreNodes(adjacency))
                .Append(", levels ").Append(minLevel).Append("..").Append(maxLevel)
                .AppendLine();
            metrics.Append("    junctions  ")
                .AppendLine(junctions.Count > 0 ? string.Join(", ", junctions) : "none");
            metrics.Append("    edge kinds  ").AppendLine(string.Join(", ", kindSummary));
            metrics.Append("    anchors  bottom ")
                .Append(topology.nodes[topology.bottomNode].key)
                .Append(" @0u, top ")
                .Append(topology.nodes[topology.topNode].key)
                .Append(" @").Append(topology.nodes[topology.topNode].level).AppendLine("u");
            metrics.Append("    selection  weight ")
                .Append(topology.weight)
                .Append(topology.weight == 0 ? " (disabled)" : string.Empty)
                .Append(topology.spatialOverrides.DeclaresAnything
                    ? ", declares per-topology spatial overrides"
                    : ", takes the profile's spatial settings verbatim");
            return metrics.ToString();
        }
    }
}

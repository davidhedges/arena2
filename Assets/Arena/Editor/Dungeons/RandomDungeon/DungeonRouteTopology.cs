using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Plastic.Newtonsoft.Json.Linq;
using UnityEngine;

namespace DungeonLab.Editor
{
    // A route topology is an authored diagram, not code: an ASCII lattice map
    // plus node/edge/slot tables. This file owns loading, structural parsing,
    // and the derived graph metrics that used to be declared per pattern.
    //
    // Everything derivable is derived: edge ids default to "{from}-{to}", edge
    // rise comes from the two node levels, and cycle rank, cycle-core size and
    // junction degrees come from the adjacency. Nothing about a graph is
    // asserted twice.
    internal sealed partial class DungeonLabGenerator
    {
        private const string RouteTopologyDirectory =
            "Assets/Arena/Editor/Dungeons/RandomDungeon/Topologies";
        private const string DefaultRouteEmbeddingFailureCode = "ROUTE_MAIN_EMBEDDING_EXHAUSTED";
        private const string RouteForwardOrientationToken = "route-forward";
        private const string VistaOrientationToken = "vista-source-to-target";
        private const string BaselineSpatialToken = "baseline";
        private const string ProfileSpatialToken = "profile";

        private static Dictionary<string, DungeonRouteTopology> routeTopologyCache;
        private static string routeTopologyCacheSignature = string.Empty;

        private sealed class RouteTopologyNode
        {
            public readonly string key;
            public readonly string id;
            public readonly string role;
            public readonly string beat;
            public readonly int level;
            public readonly int mainRouteOrder;
            public readonly int branchOrder;
            public readonly Vector2Int lattice;
            public string recipeSlotId = string.Empty;

            public RouteTopologyNode(
                string key,
                string id,
                string role,
                string beat,
                int level,
                int mainRouteOrder,
                int branchOrder,
                Vector2Int lattice)
            {
                this.key = key;
                this.id = id;
                this.role = role;
                this.beat = beat;
                this.level = level;
                this.mainRouteOrder = mainRouteOrder;
                this.branchOrder = branchOrder;
                this.lattice = lattice;
            }

            public bool IsOnMainRoute => mainRouteOrder >= 0;
        }

        private sealed class RouteTopologyEdge
        {
            public readonly string id;
            public readonly int fromNode;
            public readonly int toNode;
            public readonly RouteTransitionKind transitionKind;

            public RouteTopologyEdge(
                string id,
                int fromNode,
                int toNode,
                RouteTransitionKind transitionKind)
            {
                this.id = id;
                this.fromNode = fromNode;
                this.toNode = toNode;
                this.transitionKind = transitionKind;
            }
        }

        private sealed class RouteTopologySlot
        {
            public readonly string slotId;
            public readonly int node;
            public readonly string entryEdgeId;
            public readonly string exitEdgeId;
            public readonly RecipeOrientationBinding orientationBinding;

            public RouteTopologySlot(
                string slotId,
                int node,
                string entryEdgeId,
                string exitEdgeId,
                RecipeOrientationBinding orientationBinding)
            {
                this.slotId = slotId;
                this.node = node;
                this.entryEdgeId = entryEdgeId;
                this.exitEdgeId = exitEdgeId;
                this.orientationBinding = orientationBinding;
            }
        }

        private sealed class DungeonRouteTopology
        {
            public readonly string id;
            public readonly string displayName;
            public readonly string plannerVersion;
            public readonly string sourcePath;
            public readonly RouteTopologyNode[] nodes;
            public readonly RouteTopologyEdge[] edges;
            public readonly RouteTopologySlot[] slots;
            public readonly string vistaId;
            public readonly int vistaSourceNode;
            public readonly int vistaTargetNode;
            public readonly int vistaMinimumVoidCells;
            public readonly RouteOverlookIntent[] overlooks;
            public readonly int bottomNode;
            public readonly int topNode;
            public readonly bool allowGenericRoomWings;
            public readonly bool useProfileSpatial;
            public readonly int baselineColumnPitchCells;
            public readonly int baselineRowPitchCells;
            // Null means "uniform lane gaps at the resolved spatial pitch".
            public readonly int[] columnGapCells;
            public readonly int[] rowGapCells;
            public readonly int latticeColumnCount;
            public readonly int latticeRowCount;
            public readonly string orientationStreamId;
            public readonly string embeddingFailureCode;
            public readonly int legacyBranchSearchExpansions;

            public DungeonRouteTopology(
                string id,
                string displayName,
                string plannerVersion,
                string sourcePath,
                RouteTopologyNode[] nodes,
                RouteTopologyEdge[] edges,
                RouteTopologySlot[] slots,
                string vistaId,
                int vistaSourceNode,
                int vistaTargetNode,
                int vistaMinimumVoidCells,
                RouteOverlookIntent[] overlooks,
                int bottomNode,
                int topNode,
                bool allowGenericRoomWings,
                bool useProfileSpatial,
                int baselineColumnPitchCells,
                int baselineRowPitchCells,
                int[] columnGapCells,
                int[] rowGapCells,
                int latticeColumnCount,
                int latticeRowCount,
                string orientationStreamId,
                string embeddingFailureCode,
                int legacyBranchSearchExpansions)
            {
                this.id = id;
                this.displayName = displayName;
                this.plannerVersion = plannerVersion;
                this.sourcePath = sourcePath;
                this.nodes = nodes;
                this.edges = edges;
                this.slots = slots;
                this.vistaId = vistaId;
                this.vistaSourceNode = vistaSourceNode;
                this.vistaTargetNode = vistaTargetNode;
                this.vistaMinimumVoidCells = vistaMinimumVoidCells;
                this.overlooks = overlooks;
                this.bottomNode = bottomNode;
                this.topNode = topNode;
                this.allowGenericRoomWings = allowGenericRoomWings;
                this.useProfileSpatial = useProfileSpatial;
                this.baselineColumnPitchCells = baselineColumnPitchCells;
                this.baselineRowPitchCells = baselineRowPitchCells;
                this.columnGapCells = columnGapCells;
                this.rowGapCells = rowGapCells;
                this.latticeColumnCount = latticeColumnCount;
                this.latticeRowCount = latticeRowCount;
                this.orientationStreamId = orientationStreamId;
                this.embeddingFailureCode = embeddingFailureCode;
                this.legacyBranchSearchExpansions = legacyBranchSearchExpansions;
            }

            public bool TryGetEdgeIndex(string edgeId, out int index)
            {
                for (int edge = 0; edge < edges.Length; edge++)
                {
                    if (string.Equals(edges[edge].id, edgeId, StringComparison.Ordinal))
                    {
                        index = edge;
                        return true;
                    }
                }

                index = -1;
                return false;
            }

            public List<int>[] BuildAdjacency()
            {
                var adjacency = new List<int>[nodes.Length];
                for (int node = 0; node < adjacency.Length; node++)
                {
                    adjacency[node] = new List<int>();
                }

                foreach (RouteTopologyEdge edge in edges)
                {
                    adjacency[edge.fromNode].Add(edge.toNode);
                    adjacency[edge.toNode].Add(edge.fromNode);
                }

                return adjacency;
            }
        }

        // ---- registry -----------------------------------------------------

        private static Dictionary<string, DungeonRouteTopology> LoadRouteTopologyRegistry()
        {
            string signature = ComputeRouteTopologyDirectorySignature(out string[] paths);
            if (routeTopologyCache != null &&
                string.Equals(routeTopologyCacheSignature, signature, StringComparison.Ordinal))
            {
                return routeTopologyCache;
            }

            var registry = new Dictionary<string, DungeonRouteTopology>(StringComparer.Ordinal);
            var failures = new List<string>();
            foreach (string path in paths)
            {
                if (!TryLoadRouteTopology(path, out DungeonRouteTopology topology, out List<string> errors))
                {
                    failures.Add($"{path}: {string.Join("; ", errors)}");
                    continue;
                }

                if (registry.ContainsKey(topology.id))
                {
                    failures.Add($"{path}: duplicate topology id '{topology.id}'");
                    continue;
                }

                registry[topology.id] = topology;
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "[ROUTE_TOPOLOGY] one or more topology files are invalid:\n  " +
                    string.Join("\n  ", failures));
            }

            routeTopologyCache = registry;
            routeTopologyCacheSignature = signature;
            return registry;
        }

        private static string ComputeRouteTopologyDirectorySignature(out string[] paths)
        {
            if (!Directory.Exists(RouteTopologyDirectory))
            {
                paths = Array.Empty<string>();
                return "missing";
            }

            paths = Directory.GetFiles(RouteTopologyDirectory, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(paths, StringComparer.Ordinal);
            var signature = new System.Text.StringBuilder();
            foreach (string path in paths)
            {
                signature.Append(path)
                    .Append('@')
                    .Append(File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
            }

            return signature.ToString();
        }

        private static DungeonRouteTopology RequireRouteTopology(string topologyId)
        {
            if (LoadRouteTopologyRegistry().TryGetValue(topologyId, out DungeonRouteTopology topology))
            {
                return topology;
            }

            throw new InvalidOperationException(
                $"[ROUTE_TOPOLOGY] no topology file declares id '{topologyId}' under {RouteTopologyDirectory}");
        }

        private static List<DungeonRouteTopology> AllRouteTopologiesByFileOrder()
        {
            var ordered = new List<DungeonRouteTopology>(LoadRouteTopologyRegistry().Values);
            ordered.Sort((first, second) => StringComparer.Ordinal.Compare(first.sourcePath, second.sourcePath));
            return ordered;
        }

        // ---- parsing ------------------------------------------------------

        private static bool TryLoadRouteTopology(
            string path,
            out DungeonRouteTopology topology,
            out List<string> errors)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception exception)
            {
                topology = null;
                errors = new List<string> { $"could not be read ({exception.Message})" };
                return false;
            }

            return TryParseRouteTopology(text, path, out topology, out errors);
        }

        private static bool TryParseRouteTopology(
            string json,
            string path,
            out DungeonRouteTopology topology,
            out List<string> errors)
        {
            topology = null;
            errors = new List<string>();
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception exception)
            {
                errors.Add($"could not be parsed as JSON ({exception.Message})");
                return false;
            }

            string id = root.Value<string>("id") ?? string.Empty;
            string displayName = root.Value<string>("displayName") ?? id;
            string plannerVersion = root.Value<string>("plannerVersion") ?? string.Empty;
            if (string.IsNullOrEmpty(id))
            {
                errors.Add("'id' is missing");
            }

            if (string.IsNullOrEmpty(plannerVersion))
            {
                errors.Add("'plannerVersion' is missing");
            }

            string expectedId = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(id) && !string.Equals(id, expectedId, StringComparison.Ordinal))
            {
                errors.Add($"'id' is '{id}' but the file is named '{expectedId}.json'");
            }

            if (!TryParseRouteTopologyMap(
                    root["map"] as JArray,
                    errors,
                    out Dictionary<string, Vector2Int> lattice,
                    out int columnCount,
                    out int rowCount))
            {
                return false;
            }

            if (!TryParseRouteTopologyNodes(
                    root["nodes"] as JObject,
                    lattice,
                    errors,
                    out RouteTopologyNode[] nodes))
            {
                return false;
            }

            var nodeIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int node = 0; node < nodes.Length; node++)
            {
                nodeIndexByKey[nodes[node].key] = node;
            }

            if (!TryParseRouteTopologyEdges(
                    root["edges"] as JArray,
                    nodeIndexByKey,
                    errors,
                    out RouteTopologyEdge[] edges))
            {
                return false;
            }

            if (!TryParseRouteTopologySlots(
                    root["slots"] as JArray,
                    nodeIndexByKey,
                    edges,
                    nodes,
                    errors,
                    out RouteTopologySlot[] slots))
            {
                return false;
            }

            if (!TryParseRouteTopologyVista(
                    root["vista"] as JObject,
                    nodeIndexByKey,
                    errors,
                    out string vistaId,
                    out int vistaSourceNode,
                    out int vistaTargetNode,
                    out int vistaMinimumVoidCells) ||
                !TryParseRouteTopologyOverlooks(
                    root["overlooks"] as JArray,
                    nodeIndexByKey,
                    errors,
                    out RouteOverlookIntent[] overlooks) ||
                !TryParseRouteTopologyAnchors(
                    root["anchors"] as JObject,
                    nodeIndexByKey,
                    errors,
                    out int bottomNode,
                    out int topNode) ||
                !TryParseRouteTopologySpatial(
                    root["spatial"] as JObject,
                    columnCount,
                    rowCount,
                    errors,
                    out bool useProfileSpatial,
                    out int baselineColumnPitchCells,
                    out int baselineRowPitchCells,
                    out int[] columnGapCells,
                    out int[] rowGapCells))
            {
                return false;
            }

            JObject legacy = root["legacy"] as JObject ?? new JObject();
            topology = new DungeonRouteTopology(
                id,
                displayName,
                plannerVersion,
                path,
                nodes,
                edges,
                slots,
                vistaId,
                vistaSourceNode,
                vistaTargetNode,
                vistaMinimumVoidCells,
                overlooks,
                bottomNode,
                topNode,
                root.Value<bool?>("allowGenericRoomWings") ?? false,
                useProfileSpatial,
                baselineColumnPitchCells,
                baselineRowPitchCells,
                columnGapCells,
                rowGapCells,
                columnCount,
                rowCount,
                legacy.Value<string>("orientationStreamId") ?? id,
                legacy.Value<string>("embeddingFailureCode") ?? DefaultRouteEmbeddingFailureCode,
                legacy.Value<int?>("branchSearchExpansions") ?? 0);
            return errors.Count == 0;
        }

        private static bool TryParseRouteTopologyMap(
            JArray map,
            List<string> errors,
            out Dictionary<string, Vector2Int> lattice,
            out int columnCount,
            out int rowCount)
        {
            lattice = new Dictionary<string, Vector2Int>(StringComparer.Ordinal);
            columnCount = 0;
            rowCount = 0;
            if (map == null || map.Count == 0)
            {
                errors.Add("'map' is missing or empty");
                return false;
            }

            rowCount = map.Count;
            var rows = new string[rowCount][];
            for (int row = 0; row < rowCount; row++)
            {
                rows[row] = (map[row].Value<string>() ?? string.Empty)
                    .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (row == 0)
                {
                    columnCount = rows[row].Length;
                }
                else if (rows[row].Length != columnCount)
                {
                    errors.Add(
                        $"'map' row {row} has {rows[row].Length} cells but row 0 has {columnCount}");
                    return false;
                }
            }

            if (columnCount == 0)
            {
                errors.Add("'map' declared no lattice columns");
                return false;
            }

            for (int row = 0; row < rowCount; row++)
            {
                // Row 0 of the map is the TOP row, so it carries the highest lattice y.
                int latticeY = rowCount - 1 - row;
                for (int column = 0; column < columnCount; column++)
                {
                    string token = rows[row][column];
                    if (string.Equals(token, ".", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (lattice.ContainsKey(token))
                    {
                        errors.Add($"'map' places node key '{token}' more than once");
                        return false;
                    }

                    lattice[token] = new Vector2Int(column, latticeY);
                }
            }

            return true;
        }

        private static bool TryParseRouteTopologyNodes(
            JObject declared,
            Dictionary<string, Vector2Int> lattice,
            List<string> errors,
            out RouteTopologyNode[] nodes)
        {
            nodes = Array.Empty<RouteTopologyNode>();
            if (declared == null || !declared.HasValues)
            {
                errors.Add("'nodes' is missing or empty");
                return false;
            }

            var parsed = new List<RouteTopologyNode>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JProperty property in declared.Properties())
            {
                string key = property.Name;
                if (!(property.Value is JArray fields) || fields.Count < 5)
                {
                    errors.Add($"node '{key}' must be [id, role, beat, level, order]");
                    return false;
                }

                if (!lattice.TryGetValue(key, out Vector2Int cell))
                {
                    errors.Add($"node '{key}' has no cell in 'map'");
                    return false;
                }

                string nodeId = fields[0].Value<string>() ?? string.Empty;
                if (string.IsNullOrEmpty(nodeId) || !ids.Add(nodeId))
                {
                    errors.Add($"node '{key}' has a missing or duplicate id '{nodeId}'");
                    return false;
                }

                var order = fields[4] as JObject;
                int mainRouteOrder = order?.Value<int?>("main") ?? -1;
                int branchOrder = order?.Value<int?>("branch") ?? -1;
                if (mainRouteOrder >= 0 && branchOrder >= 0)
                {
                    errors.Add($"node '{key}' declared both a main and a branch order");
                    return false;
                }

                if (mainRouteOrder < 0 && branchOrder < 0)
                {
                    errors.Add($"node '{key}' declared neither a main nor a branch order");
                    return false;
                }

                parsed.Add(new RouteTopologyNode(
                    key,
                    nodeId,
                    fields[1].Value<string>() ?? string.Empty,
                    fields[2].Value<string>() ?? string.Empty,
                    fields[3].Value<int>(),
                    mainRouteOrder,
                    branchOrder,
                    cell));
            }

            foreach (string key in lattice.Keys)
            {
                if (!declared.ContainsKey(key))
                {
                    errors.Add($"'map' cell '{key}' has no entry in 'nodes'");
                    return false;
                }
            }

            // The node index order carried into RouteIntent is derived, never
            // authored: main-route nodes in journey order, then off-main nodes
            // in branch order. Reformatting the file cannot renumber the graph.
            parsed.Sort((first, second) =>
            {
                if (first.IsOnMainRoute != second.IsOnMainRoute)
                {
                    return first.IsOnMainRoute ? -1 : 1;
                }

                return first.IsOnMainRoute
                    ? first.mainRouteOrder.CompareTo(second.mainRouteOrder)
                    : first.branchOrder.CompareTo(second.branchOrder);
            });

            var mainOrders = new HashSet<int>();
            var branchOrders = new HashSet<int>();
            foreach (RouteTopologyNode node in parsed)
            {
                bool unique = node.IsOnMainRoute
                    ? mainOrders.Add(node.mainRouteOrder)
                    : branchOrders.Add(node.branchOrder);
                if (!unique)
                {
                    errors.Add(
                        $"node '{node.key}' repeats an order already used by another node " +
                        $"({(node.IsOnMainRoute ? "main" : "branch")} " +
                        $"{(node.IsOnMainRoute ? node.mainRouteOrder : node.branchOrder)})");
                    return false;
                }
            }

            nodes = parsed.ToArray();
            return true;
        }

        private static bool TryParseRouteTopologyEdges(
            JArray declared,
            Dictionary<string, int> nodeIndexByKey,
            List<string> errors,
            out RouteTopologyEdge[] edges)
        {
            edges = Array.Empty<RouteTopologyEdge>();
            if (declared == null || declared.Count == 0)
            {
                errors.Add("'edges' is missing or empty");
                return false;
            }

            var parsed = new List<RouteTopologyEdge>(declared.Count);
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            var endpointPairs = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < declared.Count; index++)
            {
                if (!(declared[index] is JArray fields) || fields.Count < 3)
                {
                    errors.Add($"edge {index} must be [from, to, kind] with an optional legacy id");
                    return false;
                }

                string fromKey = fields[0].Value<string>() ?? string.Empty;
                string toKey = fields[1].Value<string>() ?? string.Empty;
                if (!nodeIndexByKey.TryGetValue(fromKey, out int fromNode) ||
                    !nodeIndexByKey.TryGetValue(toKey, out int toNode))
                {
                    errors.Add($"edge {index} references unknown node keys '{fromKey}'->'{toKey}'");
                    return false;
                }

                if (fromNode == toNode)
                {
                    errors.Add($"edge {index} '{fromKey}-{toKey}' is a self edge");
                    return false;
                }

                string kindToken = fields[2].Value<string>() ?? string.Empty;
                if (!Enum.TryParse(kindToken, false, out RouteTransitionKind kind) ||
                    !Enum.IsDefined(typeof(RouteTransitionKind), kind))
                {
                    errors.Add(
                        $"edge '{fromKey}-{toKey}' declared unknown kind '{kindToken}'; expected one of " +
                        string.Join(", ", Enum.GetNames(typeof(RouteTransitionKind))));
                    return false;
                }

                string edgeId = fields.Count > 3
                    ? fields[3].Value<string>() ?? string.Empty
                    : $"{fromKey}-{toKey}";
                if (string.IsNullOrEmpty(edgeId) || !edgeIds.Add(edgeId))
                {
                    errors.Add($"edge '{fromKey}-{toKey}' has a missing or duplicate id '{edgeId}'");
                    return false;
                }

                string pair = fromNode < toNode ? $"{fromNode}:{toNode}" : $"{toNode}:{fromNode}";
                if (!endpointPairs.Add(pair))
                {
                    errors.Add($"edge '{fromKey}-{toKey}' duplicates an existing edge between the same nodes");
                    return false;
                }

                parsed.Add(new RouteTopologyEdge(edgeId, fromNode, toNode, kind));
            }

            edges = parsed.ToArray();
            return true;
        }

        private static bool TryParseRouteTopologySlots(
            JArray declared,
            Dictionary<string, int> nodeIndexByKey,
            RouteTopologyEdge[] edges,
            RouteTopologyNode[] nodes,
            List<string> errors,
            out RouteTopologySlot[] slots)
        {
            slots = Array.Empty<RouteTopologySlot>();
            if (declared == null || declared.Count == 0)
            {
                errors.Add("'slots' is missing or empty");
                return false;
            }

            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (RouteTopologyEdge edge in edges)
            {
                edgeIds.Add(edge.id);
            }

            var parsed = new List<RouteTopologySlot>(declared.Count);
            var slotIds = new HashSet<string>(StringComparer.Ordinal);
            var slotNodes = new HashSet<int>();
            foreach (JToken token in declared)
            {
                if (!(token is JObject slot))
                {
                    errors.Add("every entry in 'slots' must be an object");
                    return false;
                }

                string slotId = slot.Value<string>("id") ?? string.Empty;
                string nodeKey = slot.Value<string>("at") ?? string.Empty;
                string entryEdgeId = slot.Value<string>("entry") ?? string.Empty;
                string exitEdgeId = slot.Value<string>("exit") ?? string.Empty;
                if (string.IsNullOrEmpty(slotId) || !slotIds.Add(slotId))
                {
                    errors.Add($"slot '{slotId}' has a missing or duplicate id");
                    return false;
                }

                if (!nodeIndexByKey.TryGetValue(nodeKey, out int node) || !slotNodes.Add(node))
                {
                    errors.Add($"slot '{slotId}' points at unknown or already-used node '{nodeKey}'");
                    return false;
                }

                if (!edgeIds.Contains(entryEdgeId) || !edgeIds.Contains(exitEdgeId) ||
                    string.Equals(entryEdgeId, exitEdgeId, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"slot '{slotId}' bound entry '{entryEdgeId}' and exit '{exitEdgeId}'; " +
                        "both must name distinct declared edges");
                    return false;
                }

                string orientationToken = slot.Value<string>("orientation") ?? RouteForwardOrientationToken;
                RecipeOrientationBinding orientation;
                if (string.Equals(orientationToken, RouteForwardOrientationToken, StringComparison.Ordinal))
                {
                    orientation = RecipeOrientationBinding.RouteForward;
                }
                else if (string.Equals(orientationToken, VistaOrientationToken, StringComparison.Ordinal))
                {
                    orientation = RecipeOrientationBinding.VistaSourceToTarget;
                }
                else
                {
                    errors.Add(
                        $"slot '{slotId}' declared unknown orientation '{orientationToken}'; expected " +
                        $"'{RouteForwardOrientationToken}' or '{VistaOrientationToken}'");
                    return false;
                }

                nodes[node].recipeSlotId = slotId;
                parsed.Add(new RouteTopologySlot(slotId, node, entryEdgeId, exitEdgeId, orientation));
            }

            slots = parsed.ToArray();
            return true;
        }

        private static bool TryParseRouteTopologyVista(
            JObject declared,
            Dictionary<string, int> nodeIndexByKey,
            List<string> errors,
            out string vistaId,
            out int sourceNode,
            out int targetNode,
            out int minimumVoidCells)
        {
            vistaId = string.Empty;
            sourceNode = -1;
            targetNode = -1;
            minimumVoidCells = 0;
            if (declared == null)
            {
                errors.Add("'vista' is missing");
                return false;
            }

            vistaId = declared.Value<string>("id") ?? string.Empty;
            string fromKey = declared.Value<string>("from") ?? string.Empty;
            string toKey = declared.Value<string>("to") ?? string.Empty;
            minimumVoidCells = declared.Value<int?>("minVoidCells") ?? 0;
            if (string.IsNullOrEmpty(vistaId))
            {
                errors.Add("'vista.id' is missing");
                return false;
            }

            if (!nodeIndexByKey.TryGetValue(fromKey, out sourceNode) ||
                !nodeIndexByKey.TryGetValue(toKey, out targetNode) ||
                sourceNode == targetNode)
            {
                errors.Add($"'vista' endpoints '{fromKey}'->'{toKey}' are unknown or identical");
                return false;
            }

            if (minimumVoidCells < 1)
            {
                errors.Add($"'vista.minVoidCells' must be at least 1 (declared {minimumVoidCells})");
                return false;
            }

            return true;
        }

        private static bool TryParseRouteTopologyOverlooks(
            JArray declared,
            Dictionary<string, int> nodeIndexByKey,
            List<string> errors,
            out RouteOverlookIntent[] overlooks)
        {
            overlooks = Array.Empty<RouteOverlookIntent>();
            if (declared == null)
            {
                return true;
            }

            var parsed = new List<RouteOverlookIntent>(declared.Count);
            foreach (JToken token in declared)
            {
                if (!(token is JArray pair) || pair.Count != 2)
                {
                    errors.Add("every entry in 'overlooks' must be a [nodeKey, nodeKey] pair");
                    return false;
                }

                string firstKey = pair[0].Value<string>() ?? string.Empty;
                string secondKey = pair[1].Value<string>() ?? string.Empty;
                if (!nodeIndexByKey.TryGetValue(firstKey, out int firstNode) ||
                    !nodeIndexByKey.TryGetValue(secondKey, out int secondNode) ||
                    firstNode == secondNode)
                {
                    errors.Add($"overlook pair '{firstKey}'/'{secondKey}' is unknown or identical");
                    return false;
                }

                parsed.Add(new RouteOverlookIntent(firstNode, secondNode));
            }

            overlooks = parsed.ToArray();
            return true;
        }

        private static bool TryParseRouteTopologyAnchors(
            JObject declared,
            Dictionary<string, int> nodeIndexByKey,
            List<string> errors,
            out int bottomNode,
            out int topNode)
        {
            bottomNode = -1;
            topNode = -1;
            if (declared == null)
            {
                errors.Add("'anchors' is missing");
                return false;
            }

            string bottomKey = declared.Value<string>("bottom") ?? string.Empty;
            string topKey = declared.Value<string>("top") ?? string.Empty;
            if (!nodeIndexByKey.TryGetValue(bottomKey, out bottomNode) ||
                !nodeIndexByKey.TryGetValue(topKey, out topNode) ||
                bottomNode == topNode)
            {
                errors.Add($"'anchors' bottom '{bottomKey}' / top '{topKey}' are unknown or identical");
                return false;
            }

            return true;
        }

        private static bool TryParseRouteTopologySpatial(
            JObject declared,
            int columnCount,
            int rowCount,
            List<string> errors,
            out bool useProfileSpatial,
            out int baselineColumnPitchCells,
            out int baselineRowPitchCells,
            out int[] columnGapCells,
            out int[] rowGapCells)
        {
            useProfileSpatial = false;
            baselineColumnPitchCells = 1;
            baselineRowPitchCells = 1;
            columnGapCells = null;
            rowGapCells = null;
            if (declared == null)
            {
                errors.Add("'spatial' is missing");
                return false;
            }

            string settingsToken = declared.Value<string>("settings") ?? string.Empty;
            if (string.Equals(settingsToken, ProfileSpatialToken, StringComparison.Ordinal))
            {
                useProfileSpatial = true;
            }
            else if (!string.Equals(settingsToken, BaselineSpatialToken, StringComparison.Ordinal))
            {
                errors.Add(
                    $"'spatial.settings' is '{settingsToken}'; expected " +
                    $"'{ProfileSpatialToken}' or '{BaselineSpatialToken}'");
                return false;
            }

            if (!TryParseRouteTopologyLaneGaps(
                    declared["columnGapCells"],
                    "columnGapCells",
                    columnCount,
                    useProfileSpatial,
                    errors,
                    out baselineColumnPitchCells,
                    out columnGapCells) ||
                !TryParseRouteTopologyLaneGaps(
                    declared["rowGapCells"],
                    "rowGapCells",
                    rowCount,
                    useProfileSpatial,
                    errors,
                    out baselineRowPitchCells,
                    out rowGapCells))
            {
                return false;
            }

            return true;
        }

        private static bool TryParseRouteTopologyLaneGaps(
            JToken declared,
            string field,
            int laneCount,
            bool useProfileSpatial,
            List<string> errors,
            out int uniformPitchCells,
            out int[] perLaneGapCells)
        {
            uniformPitchCells = 1;
            perLaneGapCells = null;
            if (declared == null || declared.Type == JTokenType.Null)
            {
                return true;
            }

            if (useProfileSpatial)
            {
                errors.Add(
                    $"'spatial.{field}' cannot be declared alongside 'settings: {ProfileSpatialToken}'; " +
                    "the profile's pitch is the lane gap");
                return false;
            }

            if (declared.Type == JTokenType.Integer)
            {
                uniformPitchCells = declared.Value<int>();
                if (uniformPitchCells < 1)
                {
                    errors.Add($"'spatial.{field}' must be at least 1 (declared {uniformPitchCells})");
                    return false;
                }

                return true;
            }

            if (!(declared is JArray gaps))
            {
                errors.Add($"'spatial.{field}' must be a number or an array of numbers");
                return false;
            }

            if (gaps.Count != laneCount - 1)
            {
                errors.Add(
                    $"'spatial.{field}' declared {gaps.Count} gaps for {laneCount} lanes; expected {laneCount - 1}");
                return false;
            }

            var parsed = new int[gaps.Count];
            for (int index = 0; index < gaps.Count; index++)
            {
                parsed[index] = gaps[index].Value<int>();
                if (parsed[index] < 1)
                {
                    errors.Add($"'spatial.{field}[{index}]' must be at least 1 (declared {parsed[index]})");
                    return false;
                }
            }

            perLaneGapCells = parsed;
            return true;
        }

        // ---- lane offsets --------------------------------------------------

        // All nodes in one lattice lane share a world offset, which is what keeps
        // every edge cardinally aligned by construction.
        private static int[] ResolveLatticeLaneOffsets(
            int[] perLaneGapCells,
            int uniformGapCells,
            int laneCount)
        {
            var offsets = new int[laneCount];
            for (int lane = 1; lane < laneCount; lane++)
            {
                int gap = perLaneGapCells != null && perLaneGapCells.Length >= lane
                    ? perLaneGapCells[lane - 1]
                    : uniformGapCells;
                offsets[lane] = offsets[lane - 1] + gap;
            }

            return offsets;
        }

        private static DungeonPatternSpatialSettings ResolveTopologySpatialSettings(
            DungeonRouteTopology topology)
        {
            if (topology.useProfileSpatial)
            {
                return CurrentGenerationSettings.Validated().processionalSpatial;
            }

            return BaselinePatternSpatialSettings(
                topology.baselineColumnPitchCells,
                topology.baselineRowPitchCells);
        }
    }
}

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
        private const string RouteEmbeddingFailureCode = "ROUTE_MAIN_EMBEDDING_EXHAUSTED";
        private const string RouteForwardOrientationToken = "route-forward";
        private const string VistaOrientationToken = "vista-source-to-target";

        private static Dictionary<string, DungeonRouteTopology> routeTopologyCache;
        private static string routeTopologyCacheSignature = string.Empty;

        // One lattice lane gap, declared as an OFFSET from the resolved pitch
        // rather than an absolute cell count.
        //
        // Absolute values fight a dial that moves pitch: a topology pinned to
        // "8 to 11 cells" keeps a density-0 lattice at density 5 while the
        // profile's rooms shrink around it. Declaring "one under the pitch, up
        // to two over" instead preserves the topology's authored character —
        // twin-wing's tight columns, descent-shaft's narrow lanes — across the
        // whole dial (density-scale design §6).
        //
        // A gap that declares no minimum sits AT the pitch, so a topology only
        // says what it wants to be different. Equal minimum and maximum is a
        // fixed lane: the rubber sheet cannot move it and draws no random number
        // for it.
        private readonly struct RouteLaneGap
        {
            public readonly bool hasMinDelta;
            public readonly int minDeltaCells;
            public readonly bool hasMaxDelta;
            public readonly int maxDeltaCells;

            private RouteLaneGap(bool hasMinDelta, int minDeltaCells, bool hasMaxDelta, int maxDeltaCells)
            {
                this.hasMinDelta = hasMinDelta;
                this.minDeltaCells = minDeltaCells;
                this.hasMaxDelta = hasMaxDelta;
                this.maxDeltaCells = maxDeltaCells;
            }

            public static RouteLaneGap AtPitch()
            {
                return new RouteLaneGap(false, 0, false, 0);
            }

            public static RouteLaneGap Fixed(int deltaCells)
            {
                return new RouteLaneGap(true, deltaCells, true, deltaCells);
            }

            public static RouteLaneGap Range(bool hasMinDelta, int minDeltaCells, bool hasMaxDelta, int maxDeltaCells)
            {
                return new RouteLaneGap(hasMinDelta, minDeltaCells, hasMaxDelta, maxDeltaCells);
            }

            // A lane is at least one cell however far the dial drives the pitch
            // down: two node centres in the same cell is not a lattice.
            public int ResolvedMinimum(int pitchCells)
            {
                return Mathf.Max(1, pitchCells + (hasMinDelta ? minDeltaCells : 0));
            }

            public int ResolvedMaximum(int pitchCells)
            {
                int minimum = ResolvedMinimum(pitchCells);
                return hasMaxDelta
                    ? Mathf.Max(minimum, pitchCells + maxDeltaCells)
                    : minimum;
            }
        }

        // A topology's room size class, as offsets from the resolved pitch.
        //
        // Room extent and lane pitch are the same quantity seen twice — the gap
        // between two rooms is pitch minus room — so a room declared relative to
        // pitch keeps its relationship to its neighbours at every density, which
        // an absolute cell count cannot. Width is measured against the
        // horizontal pitch and depth against the vertical one.
        private readonly struct RouteTopologyRoomSizeDelta
        {
            public readonly int minWidthDeltaCells;
            public readonly int maxWidthDeltaCells;
            public readonly int minDepthDeltaCells;
            public readonly int maxDepthDeltaCells;

            public RouteTopologyRoomSizeDelta(
                int minWidthDeltaCells,
                int maxWidthDeltaCells,
                int minDepthDeltaCells,
                int maxDepthDeltaCells)
            {
                this.minWidthDeltaCells = minWidthDeltaCells;
                this.maxWidthDeltaCells = maxWidthDeltaCells;
                this.minDepthDeltaCells = minDepthDeltaCells;
                this.maxDepthDeltaCells = maxDepthDeltaCells;
            }

            // DungeonRoomSizeRange.Validated() floors every bound at 3 and keeps
            // each max at or above its min, so a dial that drives the pitch far
            // down degrades to the smallest legal room rather than an invalid one.
            public DungeonRoomSizeRange Resolve(int horizontalPitchCells, int verticalPitchCells)
            {
                return new DungeonRoomSizeRange(
                    horizontalPitchCells + minWidthDeltaCells,
                    horizontalPitchCells + maxWidthDeltaCells,
                    verticalPitchCells + minDepthDeltaCells,
                    verticalPitchCells + maxDepthDeltaCells);
            }
        }

        // Per-topology spatial overrides. Every field is "unset" by default and
        // falls back to the profile, so there is one code path and no second
        // hardcoded settings table to forget to update.
        //
        // The spatial ones are density-relative (design §6): room sizes are
        // offsets from the resolved pitch, and the lattice slack cap CLAMPS the
        // profile's rather than replacing it, so a topology that wants a tighter
        // rubber sheet keeps wanting one as the dial drives the profile's own
        // budget to zero. tierSeamCount / tierSeamMaxRiseLevels are graph
        // properties, not spatial ones, and stay absolute.
        private sealed class RouteTopologySpatialOverrides
        {
            public int roomEnvelopeRadiusCells = -1;
            public int neighborBiasStrengthCells = -1;
            public int latticeSlackMaxCells = -1;
            public int tierSeamCount = -1;
            public int tierSeamMaxRiseLevels = -1;
            public RouteTopologyRoomSizeDelta? terminalRoomSizeDelta;
            public RouteTopologyRoomSizeDelta? hallRoomSizeDelta;
            public RouteTopologyRoomSizeDelta? connectorRoomSizeDelta;

            public bool DeclaresAnything =>
                roomEnvelopeRadiusCells >= 0 ||
                neighborBiasStrengthCells >= 0 ||
                latticeSlackMaxCells >= 0 ||
                tierSeamCount >= 0 ||
                tierSeamMaxRiseLevels >= 0 ||
                terminalRoomSizeDelta.HasValue ||
                hallRoomSizeDelta.HasValue ||
                connectorRoomSizeDelta.HasValue;
        }

        /// <summary>
        /// One additional walkable elevation a topology node declares, RELATIVE
        /// to that node's own <c>level</c> (design §8.1).
        /// </summary>
        /// <remarks>
        /// A layer id is ROOM-LOCAL and is never a vertical identity: one node's
        /// "gallery" and another's "floor" may sit at the same absolute level.
        /// Every rule that needs separation compares
        /// <c>node.level + relativeLevel</c>, never a name — see
        /// <c>RouteTopologyNode.AbsoluteLevelOf</c>.
        /// </remarks>
        private readonly struct RouteTopologyLayer
        {
            public readonly string layerId;
            public readonly int relativeLevel;

            public RouteTopologyLayer(string layerId, int relativeLevel)
            {
                this.layerId = layerId ?? string.Empty;
                this.relativeLevel = relativeLevel;
            }
        }

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
            // Sorted by layerId, ordinal. A dictionary's enumeration order must
            // never reach output, and these reach the route-intent projection.
            public readonly RouteTopologyLayer[] layers;
            public string recipeSlotId = string.Empty;

            public RouteTopologyNode(
                string key,
                string id,
                string role,
                string beat,
                int level,
                int mainRouteOrder,
                int branchOrder,
                Vector2Int lattice,
                RouteTopologyLayer[] layers = null)
            {
                this.key = key;
                this.id = id;
                this.role = role;
                this.beat = beat;
                this.level = level;
                this.mainRouteOrder = mainRouteOrder;
                this.branchOrder = branchOrder;
                this.lattice = lattice;
                this.layers = layers ?? Array.Empty<RouteTopologyLayer>();
            }

            public bool IsOnMainRoute => mainRouteOrder >= 0;

            public bool DeclaresLayers => layers.Length > 0;

            /// <summary>
            /// Does this node declare a STOREY — a layer at some other elevation
            /// than its own?
            /// </summary>
            /// <remarks>
            /// A table of nothing but base layers declares no storey. It names
            /// the node's own elevation so that an edge may BIND it, which is
            /// how a topology authorizes a stacked corridor crossing (D2) — and
            /// a binding is the only thing a name is for.
            /// </remarks>
            public bool DeclaresStoreys
            {
                get
                {
                    foreach (RouteTopologyLayer layer in layers)
                    {
                        if (layer.relativeLevel != 0)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            /// <summary>
            /// The absolute elevation an edge bound to <paramref name="layerId"/>
            /// meets this node at. An EMPTY id is the base layer and resolves to
            /// the node's own level, whether or not the node declares any layers
            /// — which is what makes every existing topology bind unchanged.
            /// </summary>
            public bool TryGetAbsoluteLevel(string layerId, out int absoluteLevel)
            {
                if (string.IsNullOrEmpty(layerId))
                {
                    absoluteLevel = level;
                    return true;
                }

                foreach (RouteTopologyLayer layer in layers)
                {
                    if (string.Equals(layer.layerId, layerId, StringComparison.Ordinal))
                    {
                        absoluteLevel = level + layer.relativeLevel;
                        return true;
                    }
                }

                absoluteLevel = level;
                return false;
            }
        }

        private sealed class RouteTopologyEdge
        {
            public readonly string id;
            public readonly int fromNode;
            public readonly int toNode;
            public readonly RouteTransitionKind transitionKind;
            // Which declared layer each end binds to; empty is the base layer.
            // A binding AUTHORIZES a relaxation; the absolute band it implies is
            // what DECIDES one (design §8.1).
            public readonly string fromLayerId;
            public readonly string toLayerId;

            public RouteTopologyEdge(
                string id,
                int fromNode,
                int toNode,
                RouteTransitionKind transitionKind,
                string fromLayerId = "",
                string toLayerId = "")
            {
                this.id = id;
                this.fromNode = fromNode;
                this.toNode = toNode;
                this.transitionKind = transitionKind;
                this.fromLayerId = fromLayerId ?? string.Empty;
                this.toLayerId = toLayerId ?? string.Empty;
            }

            public bool IsLayerBound =>
                !string.IsNullOrEmpty(fromLayerId) || !string.IsNullOrEmpty(toLayerId);
        }

        /// <summary>
        /// One slot's answer to "which storey of the recipe is this storey of the
        /// node?" (design §13, phase D3).
        /// </summary>
        /// <remarks>
        /// The two vocabularies are independent on purpose. A topology names the
        /// elevations its ROUTES bind to; a recipe names the storeys its own
        /// geometry is built on, and it does so without knowing which graph will
        /// place it. The slot is the only place that knows both, so it is the
        /// only place the two can be equated — and equating them by NAME would
        /// have made every recipe's layer ids part of every topology's, which is
        /// exactly the room-local coupling §8.1 spends a page rejecting.
        /// </remarks>
        private readonly struct RouteTopologySlotLayer
        {
            public readonly string topologyLayerId;
            public readonly string recipeLayerId;

            public RouteTopologySlotLayer(string topologyLayerId, string recipeLayerId)
            {
                this.topologyLayerId = topologyLayerId ?? string.Empty;
                this.recipeLayerId = recipeLayerId ?? string.Empty;
            }
        }

        private sealed class RouteTopologySlot
        {
            public readonly string slotId;
            public readonly int node;
            public readonly string entryEdgeId;
            public readonly string exitEdgeId;
            public readonly RecipeOrientationBinding orientationBinding;
            // Sorted by topology layer id, ordinal, for the same reason the node
            // layer table is: a JSON object's property order is authored, and
            // this array reaches the hashed route-intent projection.
            public readonly RouteTopologySlotLayer[] layers;

            public RouteTopologySlot(
                string slotId,
                int node,
                string entryEdgeId,
                string exitEdgeId,
                RecipeOrientationBinding orientationBinding,
                RouteTopologySlotLayer[] layers = null)
            {
                this.slotId = slotId;
                this.node = node;
                this.entryEdgeId = entryEdgeId;
                this.exitEdgeId = exitEdgeId;
                this.orientationBinding = orientationBinding;
                this.layers = layers ?? Array.Empty<RouteTopologySlotLayer>();
            }

            public bool DeclaresLayers => layers.Length > 0;
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
            public readonly int weight;
            public readonly RouteTopologySpatialOverrides spatialOverrides;
            // One entry per gap between adjacent lanes, so length == lanes - 1.
            public readonly RouteLaneGap[] columnGaps;
            public readonly RouteLaneGap[] rowGaps;
            public readonly int latticeColumnCount;
            public readonly int latticeRowCount;

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
                int weight,
                RouteTopologySpatialOverrides spatialOverrides,
                RouteLaneGap[] columnGaps,
                RouteLaneGap[] rowGaps,
                int latticeColumnCount,
                int latticeRowCount)
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
                this.weight = weight;
                this.spatialOverrides = spatialOverrides;
                this.columnGaps = columnGaps;
                this.rowGaps = rowGaps;
                this.latticeColumnCount = latticeColumnCount;
                this.latticeRowCount = latticeRowCount;
            }

            /// <summary>
            /// Whether any node declares a layer. The whole layered-generation
            /// path is gated on this: a topology that declares none takes every
            /// rule exactly as it was before Phase D, which is what makes the
            /// phase output-neutral by construction rather than by measurement.
            /// </summary>
            public bool DeclaresLayers
            {
                get
                {
                    foreach (RouteTopologyNode node in nodes)
                    {
                        if (node.DeclaresLayers)
                        {
                            return true;
                        }
                    }

                    return false;
                }
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
                    nodes,
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
                    out RouteTopologySpatialOverrides spatialOverrides,
                    out RouteLaneGap[] columnGaps,
                    out RouteLaneGap[] rowGaps))
            {
                return false;
            }

            int weight = root.Value<int?>("weight") ?? 1;
            if (weight < 0)
            {
                errors.Add($"'weight' is {weight}; it must be 0 (disabled) or greater");
                return false;
            }

            if (root["legacy"] != null)
            {
                errors.Add(
                    "'legacy' was the step 1 hash-compatibility block and no longer exists; " +
                    "delete it (orientationStreamId, embeddingFailureCode, branchSearchExpansions)");
                return false;
            }

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
                weight,
                spatialOverrides,
                columnGaps,
                rowGaps,
                columnCount,
                rowCount);
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

                int level = fields[3].Value<int>();
                if (!TryParseRouteTopologyNodeLayers(
                        key,
                        level,
                        fields.Count > 5 ? fields[5] as JObject : null,
                        errors,
                        out RouteTopologyLayer[] layers))
                {
                    return false;
                }

                parsed.Add(new RouteTopologyNode(
                    key,
                    nodeId,
                    fields[1].Value<string>() ?? string.Empty,
                    fields[2].Value<string>() ?? string.Empty,
                    level,
                    mainRouteOrder,
                    branchOrder,
                    cell,
                    layers));
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

        /// <summary>
        /// The optional 6th node element, <c>{ "layers": { "gallery": 4 } }</c>
        /// (design §8.1). Values are offsets from the node's own level, so
        /// nothing acquires a global storey number.
        /// </summary>
        /// <remarks>
        /// A layer at offset 0 NAMES the base rather than adding a storey, which
        /// is why the design's own example writes <c>"floor": 0</c> beside its
        /// upper layers. At most one may do so; an edge that binds nothing also
        /// means the base, so naming it twice would give one elevation two ids.
        /// <para>
        /// The pitch rule is <see cref="MajorRiseLevels"/>, the same multiple the
        /// node's own level must be, because a layer is an elevation a route may
        /// bind to and the ±4/±8 edge grammar has to survive the widened
        /// derivation.
        /// </para>
        /// </remarks>
        private static bool TryParseRouteTopologyNodeLayers(
            string key,
            int nodeLevel,
            JObject declared,
            List<string> errors,
            out RouteTopologyLayer[] layers)
        {
            layers = Array.Empty<RouteTopologyLayer>();
            var table = declared?["layers"] as JObject;
            if (table == null)
            {
                if (declared != null && declared.HasValues)
                {
                    errors.Add(
                        $"node '{key}' declared a 6th element with no 'layers' table; " +
                        "the only option a node takes today is { \"layers\": { \"<id>\": <relative level> } }");
                    return false;
                }

                return true;
            }

            var parsed = new List<RouteTopologyLayer>(table.Count);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            bool sawBase = false;
            foreach (JProperty property in table.Properties())
            {
                string layerId = property.Name;
                if (string.IsNullOrEmpty(layerId) || !ids.Add(layerId))
                {
                    errors.Add($"node '{key}' layer '{layerId}' has a missing or duplicate id");
                    return false;
                }

                int relativeLevel = property.Value.Value<int>();
                if (relativeLevel % MajorRiseLevels != 0)
                {
                    errors.Add(
                        $"node '{key}' layer '{layerId}' is at relative level {relativeLevel}, " +
                        $"which is not a multiple of {MajorRiseLevels}");
                    return false;
                }

                int absoluteLevel = nodeLevel + relativeLevel;
                if (absoluteLevel < 0 || absoluteLevel > MaxGeneratedLevel)
                {
                    errors.Add(
                        $"node '{key}' layer '{layerId}' resolves to absolute level {absoluteLevel}, " +
                        $"outside 0..{MaxGeneratedLevel}");
                    return false;
                }

                if (relativeLevel == 0)
                {
                    if (sawBase)
                    {
                        errors.Add($"node '{key}' declares more than one layer at relative level 0");
                        return false;
                    }

                    sawBase = true;
                }

                // Two ids at one elevation is the same defect as two bases: an
                // edge binding either would resolve identically, so the graph
                // would claim a separation it does not have.
                foreach (RouteTopologyLayer existing in parsed)
                {
                    if (existing.relativeLevel == relativeLevel)
                    {
                        errors.Add(
                            $"node '{key}' layers '{existing.layerId}' and '{layerId}' are both at " +
                            $"relative level {relativeLevel}; one elevation may have one id");
                        return false;
                    }
                }

                parsed.Add(new RouteTopologyLayer(layerId, relativeLevel));
            }

            if (parsed.Count == 0)
            {
                errors.Add($"node '{key}' declared an empty 'layers' table; omit it instead");
                return false;
            }

            // Ordinal by id: a JSON object's property order is authored, and this
            // array reaches the route-intent projection, which is hashed.
            // Reformatting the file must not move a seed.
            parsed.Sort((first, second) =>
                string.CompareOrdinal(first.layerId, second.layerId));
            layers = parsed.ToArray();
            return true;
        }

        private static bool TryParseRouteTopologyEdges(
            JArray declared,
            Dictionary<string, int> nodeIndexByKey,
            IReadOnlyList<RouteTopologyNode> nodes,
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
                if (!(declared[index] is JArray fields) || fields.Count < 3 || fields.Count > 4)
                {
                    errors.Add(
                        $"edge {index} must be [from, to, kind] with an optional 4th " +
                        "{ \"fromLayer\": …, \"toLayer\": … }; the id derives as " +
                        "\"{from}-{to}\" and the rise derives from the two BOUND elevations");
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

                string edgeId = $"{fromKey}-{toKey}";
                if (!edgeIds.Add(edgeId))
                {
                    errors.Add($"edge '{fromKey}-{toKey}' has a duplicate derived id '{edgeId}'");
                    return false;
                }

                string pair = fromNode < toNode ? $"{fromNode}:{toNode}" : $"{toNode}:{fromNode}";
                if (!endpointPairs.Add(pair))
                {
                    errors.Add($"edge '{fromKey}-{toKey}' duplicates an existing edge between the same nodes");
                    return false;
                }

                var options = fields.Count > 3 ? fields[3] as JObject : null;
                if (fields.Count > 3 && options == null)
                {
                    errors.Add(
                        $"edge '{edgeId}' has a 4th element that is not an object; " +
                        "expected { \"fromLayer\": \"<id>\", \"toLayer\": \"<id>\" }");
                    return false;
                }

                string fromLayerId = options?.Value<string>("fromLayer") ?? string.Empty;
                string toLayerId = options?.Value<string>("toLayer") ?? string.Empty;
                if (!TryValidateEdgeLayerBinding(edgeId, nodes[fromNode], "fromLayer", fromLayerId, errors) ||
                    !TryValidateEdgeLayerBinding(edgeId, nodes[toNode], "toLayer", toLayerId, errors))
                {
                    return false;
                }

                parsed.Add(new RouteTopologyEdge(edgeId, fromNode, toNode, kind, fromLayerId, toLayerId));
            }

            edges = parsed.ToArray();
            return true;
        }

        /// <summary>
        /// An edge end may bind only to a layer its own endpoint declares.
        /// </summary>
        /// <remarks>
        /// Rejected at LOAD time rather than reported by the validator, because
        /// an unresolvable binding has no elevation at all — the edge's rise
        /// would silently fall back to the node's base level and the graph would
        /// generate something other than what it says.
        /// </remarks>
        private static bool TryValidateEdgeLayerBinding(
            string edgeId,
            RouteTopologyNode node,
            string field,
            string layerId,
            List<string> errors)
        {
            if (string.IsNullOrEmpty(layerId) || node.TryGetAbsoluteLevel(layerId, out _))
            {
                return true;
            }

            string declaredIds = node.DeclaresLayers
                ? string.Join(", ", Array.ConvertAll(node.layers, layer => $"'{layer.layerId}'"))
                : "none";
            errors.Add(
                $"edge '{edgeId}' binds {field} '{layerId}', which node '{node.key}' does not declare " +
                $"(declared: {declaredIds})");
            return false;
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

                if (!TryParseRouteTopologySlotLayers(
                        slotId,
                        nodes[node],
                        slot["layers"] as JObject,
                        slot,
                        errors,
                        out RouteTopologySlotLayer[] slotLayers))
                {
                    return false;
                }

                nodes[node].recipeSlotId = slotId;
                parsed.Add(new RouteTopologySlot(
                    slotId,
                    node,
                    entryEdgeId,
                    exitEdgeId,
                    orientation,
                    slotLayers));
            }

            slots = parsed.ToArray();
            return true;
        }

        /// <summary>
        /// The optional slot member
        /// <c>"layers": { "&lt;topology layer&gt;": "&lt;recipe layer&gt;" }</c>
        /// (design §13, phase D3).
        /// </summary>
        /// <remarks>
        /// Rejected at LOAD time rather than reported, on the same reasoning as
        /// an edge's layer binding: a mapping that names a storey the node does
        /// not declare has no elevation at all, so the room would be built at
        /// one height and routed to at another.
        /// <para>
        /// What is NOT checked here is the only interesting rule — that the
        /// recipe layer sits at the same relative level as the topology layer.
        /// The loader cannot know which recipe will fill the slot; that is
        /// checked per candidate, before catalog admission, in
        /// <c>TryValidateRecipeCandidate</c>.
        /// </para>
        /// </remarks>
        private static bool TryParseRouteTopologySlotLayers(
            string slotId,
            RouteTopologyNode node,
            JObject table,
            JObject slot,
            List<string> errors,
            out RouteTopologySlotLayer[] layers)
        {
            layers = Array.Empty<RouteTopologySlotLayer>();
            if (table == null)
            {
                // A slot member spelled `layer`, `storeys`, … would otherwise be
                // ignored in silence, which is the failure mode C2 paid for
                // twice: the graph validates, generation runs, and the authored
                // feature is simply absent.
                if (slot?["layers"] != null)
                {
                    errors.Add(
                        $"slot '{slotId}' declared 'layers' as something other than an object; " +
                        "expected { \"<topology layer>\": \"<recipe layer>\" }");
                    return false;
                }

                return true;
            }

            var parsed = new List<RouteTopologySlotLayer>(table.Count);
            var topologyIds = new HashSet<string>(StringComparer.Ordinal);
            var recipeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JProperty property in table.Properties())
            {
                string topologyLayerId = property.Name;
                string recipeLayerId = property.Value.Type == JTokenType.String
                    ? property.Value.Value<string>()
                    : null;
                if (string.IsNullOrEmpty(topologyLayerId) || !topologyIds.Add(topologyLayerId))
                {
                    errors.Add($"slot '{slotId}' layer mapping '{topologyLayerId}' is missing or duplicated");
                    return false;
                }

                if (string.IsNullOrEmpty(recipeLayerId))
                {
                    errors.Add(
                        $"slot '{slotId}' maps layer '{topologyLayerId}' to a missing or non-string " +
                        "recipe layer id");
                    return false;
                }

                if (!node.TryGetAbsoluteLevel(topologyLayerId, out _))
                {
                    string declaredIds = node.DeclaresLayers
                        ? string.Join(", ", Array.ConvertAll(node.layers, layer => $"'{layer.layerId}'"))
                        : "none";
                    errors.Add(
                        $"slot '{slotId}' maps layer '{topologyLayerId}', which its node '{node.key}' " +
                        $"does not declare (declared: {declaredIds})");
                    return false;
                }

                // Two storeys onto one recipe storey would collapse two
                // elevations into one place, and the graph would then claim a
                // separation the room cannot build.
                if (!recipeIds.Add(recipeLayerId))
                {
                    errors.Add(
                        $"slot '{slotId}' maps more than one layer onto recipe layer '{recipeLayerId}'");
                    return false;
                }

                parsed.Add(new RouteTopologySlotLayer(topologyLayerId, recipeLayerId));
            }

            if (parsed.Count == 0)
            {
                errors.Add($"slot '{slotId}' declared an empty 'layers' mapping; omit it instead");
                return false;
            }

            parsed.Sort((first, second) =>
                string.CompareOrdinal(first.topologyLayerId, second.topologyLayerId));
            layers = parsed.ToArray();
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
            out RouteTopologySpatialOverrides overrides,
            out RouteLaneGap[] columnGaps,
            out RouteLaneGap[] rowGaps)
        {
            overrides = new RouteTopologySpatialOverrides();
            columnGaps = Array.Empty<RouteLaneGap>();
            rowGaps = Array.Empty<RouteLaneGap>();
            // 'spatial' is optional now: a topology that wants the profile
            // verbatim, at the profile's own pitch, declares nothing.
            declared = declared ?? new JObject();
            if (declared["settings"] != null)
            {
                errors.Add(
                    "'spatial.settings' was the step 1 profile/baseline fork and no longer exists; " +
                    "the profile is always the default, and a topology overrides only what it needs");
                return false;
            }

            // The absolute-cell spatial vocabulary was replaced by pitch-relative
            // offsets when density became a dial (2026-07-27). Rejecting the old
            // names by name matters more than usual here: a bare number is legal
            // in both vocabularies and means something different in each, so a
            // silent reinterpretation would move geometry without an error.
            foreach ((string legacy, string replacement) in RetiredAbsoluteSpatialFields)
            {
                if (declared[legacy] != null)
                {
                    errors.Add(
                        $"'spatial.{legacy}' declared absolute cells; topology spatial overrides are " +
                        $"offsets from the resolved pitch since density became a dial (2026-07-27). " +
                        $"Use '{replacement}' — see docs/dungeon-builder/ROUTE_TOPOLOGY_AUTHORING.md");
                    return false;
                }
            }

            if (!TryParseRouteTopologyLaneGaps(
                    declared["columnGapDeltaCells"],
                    "columnGapDeltaCells",
                    columnCount,
                    errors,
                    out columnGaps) ||
                !TryParseRouteTopologyLaneGaps(
                    declared["rowGapDeltaCells"],
                    "rowGapDeltaCells",
                    rowCount,
                    errors,
                    out rowGaps))
            {
                return false;
            }

            return TryParseRouteTopologySpatialOverride(
                       declared,
                       "roomEnvelopeRadiusCells",
                       4,
                       errors,
                       out overrides.roomEnvelopeRadiusCells) &&
                   TryParseRouteTopologySpatialOverride(
                       declared,
                       "neighborBiasStrengthCells",
                       0,
                       errors,
                       out overrides.neighborBiasStrengthCells) &&
                   TryParseRouteTopologySpatialOverride(
                       declared,
                       "latticeSlackMaxCells",
                       0,
                       errors,
                       out overrides.latticeSlackMaxCells) &&
                   TryParseRouteTopologySpatialOverride(
                       declared,
                       "tierSeamCount",
                       0,
                       errors,
                       out overrides.tierSeamCount) &&
                   TryParseRouteTopologySpatialOverride(
                       declared,
                       "tierSeamMaxRiseLevels",
                       4,
                       errors,
                       out overrides.tierSeamMaxRiseLevels) &&
                   TryParseRouteTopologyRoomSizeDeltas(declared["roomSizeDeltaCells"], errors, overrides);
        }

        private static readonly (string legacy, string replacement)[] RetiredAbsoluteSpatialFields =
        {
            ("columnGapCells", "columnGapDeltaCells"),
            ("rowGapCells", "rowGapDeltaCells"),
            ("roomSizes", "roomSizeDeltaCells")
        };

        private static bool TryParseRouteTopologySpatialOverride(
            JObject declared,
            string field,
            int minimum,
            List<string> errors,
            out int value)
        {
            value = -1;
            JToken token = declared[field];
            if (token == null || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.Integer)
            {
                errors.Add($"'spatial.{field}' must be a whole number");
                return false;
            }

            value = token.Value<int>();
            if (value < minimum)
            {
                errors.Add($"'spatial.{field}' must be at least {minimum} (declared {value})");
                return false;
            }

            return true;
        }

        private static bool TryParseRouteTopologyRoomSizeDeltas(
            JToken declared,
            List<string> errors,
            RouteTopologySpatialOverrides overrides)
        {
            if (declared == null || declared.Type == JTokenType.Null)
            {
                return true;
            }

            if (!(declared is JObject sizes))
            {
                errors.Add("'spatial.roomSizeDeltaCells' must be an object keyed by size class");
                return false;
            }

            foreach (JProperty property in sizes.Properties())
            {
                if (!(property.Value is JArray range) || range.Count != 4)
                {
                    errors.Add(
                        $"'spatial.roomSizeDeltaCells.{property.Name}' must be " +
                        "[minWidthDelta, maxWidthDelta, minDepthDelta, maxDepthDelta], each an " +
                        "offset from the resolved pitch");
                    return false;
                }

                var parsed = new RouteTopologyRoomSizeDelta(
                    range[0].Value<int>(),
                    range[1].Value<int>(),
                    range[2].Value<int>(),
                    range[3].Value<int>());
                if (parsed.maxWidthDeltaCells < parsed.minWidthDeltaCells ||
                    parsed.maxDepthDeltaCells < parsed.minDepthDeltaCells)
                {
                    errors.Add(
                        $"'spatial.roomSizeDeltaCells.{property.Name}' has a maximum below its " +
                        "minimum; both bounds are offsets from the same pitch, so an inverted " +
                        "range is inverted at every density");
                    return false;
                }

                switch (property.Name)
                {
                    case "terminal":
                        overrides.terminalRoomSizeDelta = parsed;
                        break;
                    case "hall":
                        overrides.hallRoomSizeDelta = parsed;
                        break;
                    case "connector":
                        overrides.connectorRoomSizeDelta = parsed;
                        break;
                    default:
                        errors.Add(
                            $"'spatial.roomSizeDeltaCells' declared unknown size class '{property.Name}'; " +
                            "expected terminal, hall or connector");
                        return false;
                }
            }

            return true;
        }

        // A lane gap is a number (a fixed lane at pitch + that offset), an object
        // {minDelta?, maxDelta?} (a rubber-sheet range around the pitch), or an
        // array of one such entry per gap between adjacent lanes.
        private static bool TryParseRouteTopologyLaneGaps(
            JToken declared,
            string field,
            int laneCount,
            List<string> errors,
            out RouteLaneGap[] gaps)
        {
            int gapCount = Mathf.Max(0, laneCount - 1);
            gaps = new RouteLaneGap[gapCount];
            for (int index = 0; index < gapCount; index++)
            {
                // Unset on both bounds means "at the pitch, fixed".
                gaps[index] = RouteLaneGap.AtPitch();
            }

            if (declared == null || declared.Type == JTokenType.Null)
            {
                return true;
            }

            if (declared is JArray perLane)
            {
                if (perLane.Count != gapCount)
                {
                    errors.Add(
                        $"'spatial.{field}' declared {perLane.Count} gaps for {laneCount} lanes; " +
                        $"expected {gapCount}");
                    return false;
                }

                for (int index = 0; index < gapCount; index++)
                {
                    if (!TryParseRouteTopologyLaneGap(
                            perLane[index],
                            $"{field}[{index}]",
                            errors,
                            out gaps[index]))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (!TryParseRouteTopologyLaneGap(declared, field, errors, out RouteLaneGap uniform))
            {
                return false;
            }

            for (int index = 0; index < gapCount; index++)
            {
                gaps[index] = uniform;
            }

            return true;
        }

        private static bool TryParseRouteTopologyLaneGap(
            JToken declared,
            string field,
            List<string> errors,
            out RouteLaneGap gap)
        {
            gap = RouteLaneGap.AtPitch();
            if (declared.Type == JTokenType.Integer)
            {
                gap = RouteLaneGap.Fixed(declared.Value<int>());
                return true;
            }

            if (!(declared is JObject range))
            {
                errors.Add(
                    $"'spatial.{field}' must be a number (a fixed lane, pitch + that offset), " +
                    "an object { minDelta, maxDelta }, or an array of those");
                return false;
            }

            foreach (JProperty property in range.Properties())
            {
                if (!string.Equals(property.Name, "minDelta", StringComparison.Ordinal) &&
                    !string.Equals(property.Name, "maxDelta", StringComparison.Ordinal))
                {
                    errors.Add(
                        $"'spatial.{field}' declared unknown bound '{property.Name}'; expected " +
                        "minDelta / maxDelta. Absolute 'min' / 'max' cell counts were replaced by " +
                        "pitch-relative offsets when density became a dial (2026-07-27): a lane at " +
                        "8 cells against a 9-cell pitch is now { \"minDelta\": -1 }");
                    return false;
                }
            }

            bool hasMinDelta = range["minDelta"] != null && range["minDelta"].Type != JTokenType.Null;
            bool hasMaxDelta = range["maxDelta"] != null && range["maxDelta"].Type != JTokenType.Null;
            if (hasMinDelta && range["minDelta"].Type != JTokenType.Integer ||
                hasMaxDelta && range["maxDelta"].Type != JTokenType.Integer)
            {
                errors.Add($"'spatial.{field}' bounds must be whole numbers");
                return false;
            }

            int minDelta = hasMinDelta ? range.Value<int>("minDelta") : 0;
            int maxDelta = hasMaxDelta ? range.Value<int>("maxDelta") : 0;
            // Both bounds are offsets from the same pitch, so comparing them
            // needs no pitch: an inverted range is inverted at every density.
            if (hasMinDelta && hasMaxDelta && maxDelta < minDelta)
            {
                errors.Add($"'spatial.{field}' has maxDelta {maxDelta} below minDelta {minDelta}");
                return false;
            }

            if (hasMaxDelta && !hasMinDelta && maxDelta < 0)
            {
                errors.Add(
                    $"'spatial.{field}' has maxDelta {maxDelta} below its implied minDelta of 0; " +
                    "a gap that declares no minimum sits at the pitch");
                return false;
            }

            gap = RouteLaneGap.Range(hasMinDelta, minDelta, hasMaxDelta, maxDelta);
            return true;
        }

        // ---- lane offsets --------------------------------------------------

        // All nodes in one lattice lane share a world offset, which is what keeps
        // every edge cardinally aligned by construction — the one invariant a
        // general graph embedder would have to solve for.
        //
        // The rubber sheet spends slack on top of that: each lane's authored
        // minimum is the floor, its authored maximum the ceiling, and the total
        // spend is capped by the map envelope and by the profile's
        // latticeSlackMaxCells. A topology whose gaps are all fixed draws no
        // random number at all.
        private static int[] ResolveLatticeLaneOffsets(
            int dungeonSeed,
            int layoutAttempt,
            DungeonRouteTopology topology,
            RouteLaneGap[] gaps,
            int pitchCells,
            int laneCount,
            DungeonPatternSpatialSettings spatial,
            string streamPurpose,
            out int spentSlackCells,
            out int availableSlackCells)
        {
            var offsets = new int[laneCount];
            var minimums = new int[gaps.Length];
            var headroom = new int[gaps.Length];
            int baseSpan = 0;
            int totalHeadroom = 0;
            for (int gap = 0; gap < gaps.Length; gap++)
            {
                minimums[gap] = gaps[gap].ResolvedMinimum(pitchCells);
                headroom[gap] = gaps[gap].ResolvedMaximum(pitchCells) - minimums[gap];
                baseSpan += minimums[gap];
                totalHeadroom += headroom[gap];
            }

            availableSlackCells = ResolveLatticeSlackBudget(baseSpan, totalHeadroom, spatial);
            spentSlackCells = 0;
            var extra = new int[gaps.Length];
            if (availableSlackCells > 0)
            {
                System.Random random = DerivedRandom(
                    dungeonSeed,
                    layoutAttempt,
                    topology.id,
                    streamPurpose);
                var eligible = new List<int>(gaps.Length);
                for (int gap = 0; gap < gaps.Length; gap++)
                {
                    if (headroom[gap] > 0)
                    {
                        eligible.Add(gap);
                    }
                }

                while (spentSlackCells < availableSlackCells && eligible.Count > 0)
                {
                    int pick = random.Next(eligible.Count);
                    int gap = eligible[pick];
                    extra[gap]++;
                    spentSlackCells++;
                    if (extra[gap] >= headroom[gap])
                    {
                        eligible.RemoveAt(pick);
                    }
                }
            }

            for (int lane = 1; lane < laneCount; lane++)
            {
                offsets[lane] = offsets[lane - 1] + minimums[lane - 1] + extra[lane - 1];
            }

            return offsets;
        }

        // The lattice with every lane at its authored minimum. This is the
        // tightest the rubber sheet can make a topology, and therefore the case
        // every author-time rule is checked against.
        private static int[] MinimumLatticeLaneOffsets(
            RouteLaneGap[] gaps,
            int pitchCells,
            int laneCount)
        {
            var offsets = new int[laneCount];
            for (int lane = 1; lane < laneCount; lane++)
            {
                offsets[lane] = offsets[lane - 1] + gaps[lane - 1].ResolvedMinimum(pitchCells);
            }

            return offsets;
        }

        private static int LatticeAuthoredHeadroom(RouteLaneGap[] gaps, int pitchCells)
        {
            int headroom = 0;
            foreach (RouteLaneGap gap in gaps)
            {
                headroom += gap.ResolvedMaximum(pitchCells) - gap.ResolvedMinimum(pitchCells);
            }

            return headroom;
        }

        private static int LatticeSlackBudget(
            RouteLaneGap[] gaps,
            int pitchCells,
            DungeonPatternSpatialSettings spatial)
        {
            int baseSpan = 0;
            foreach (RouteLaneGap gap in gaps)
            {
                baseSpan += gap.ResolvedMinimum(pitchCells);
            }

            return ResolveLatticeSlackBudget(
                baseSpan,
                LatticeAuthoredHeadroom(gaps, pitchCells),
                spatial);
        }

        // Slack is bounded three ways: what the authored ranges allow, what the
        // map envelope has room for in EVERY orientation (a quarter turn swaps
        // the axes, so each axis is held to the smaller of the two maxima), and
        // the profile's own cap. The envelope term is what keeps a widened
        // lattice from being rejected by TryTransformCoarseEmbedding instead of
        // simply being narrower.
        private static int ResolveLatticeSlackBudget(
            int baseSpanCells,
            int totalHeadroomCells,
            DungeonPatternSpatialSettings spatial)
        {
            DungeonGenerationSettings settings = CurrentGenerationSettings.Validated();
            int axisMaxCells = Mathf.Min(settings.mapWidthMaxCells, settings.mapDepthMaxCells);
            int envelopeSlack = axisMaxCells -
                (baseSpanCells + spatial.roomEnvelopeRadiusCells * 2 + 1);
            return Mathf.Max(
                0,
                Mathf.Min(totalHeadroomCells, Mathf.Min(envelopeSlack, spatial.latticeSlackMaxCells)));
        }

        // Applied per node at inflation time (ResolveAdjacentLaneGaps), against
        // that node's own adjacent lanes rather than the axis's tightest.
        internal static DungeonRoomSizeRange ClampRoomSize(
            DungeonRoomSizeRange size,
            int widestCells,
            int deepestCells)
        {
            var value = size;
            value.maxWidthCells = Mathf.Min(value.maxWidthCells, widestCells);
            value.minWidthCells = Mathf.Min(value.minWidthCells, value.maxWidthCells);
            value.maxDepthCells = Mathf.Min(value.maxDepthCells, deepestCells);
            value.minDepthCells = Mathf.Min(value.minDepthCells, value.maxDepthCells);
            return value;
        }

        // One profile default plus per-topology overrides. There is no second
        // settings table, so a widened profile cannot reach only some topologies.
        private static DungeonPatternSpatialSettings ResolveTopologySpatialSettings(
            DungeonRouteTopology topology)
        {
            DungeonPatternSpatialSettings spatial =
                CurrentGenerationSettings.Validated().processionalSpatial;
            RouteTopologySpatialOverrides overrides = topology.spatialOverrides;
            if (overrides.roomEnvelopeRadiusCells >= 0)
            {
                spatial.roomEnvelopeRadiusCells = overrides.roomEnvelopeRadiusCells;
            }

            if (overrides.neighborBiasStrengthCells >= 0)
            {
                spatial.neighborBiasStrengthCells = overrides.neighborBiasStrengthCells;
            }

            // A CLAMP, not a replacement. A topology that wants a tighter rubber
            // sheet than the profile still wants one when the dial has driven
            // the profile's own budget below the topology's number; raising the
            // profile's budget above it would be a topology quietly opting out
            // of the dial.
            if (overrides.latticeSlackMaxCells >= 0)
            {
                spatial.latticeSlackMaxCells = Mathf.Min(
                    spatial.latticeSlackMaxCells,
                    overrides.latticeSlackMaxCells);
            }

            if (overrides.tierSeamCount >= 0)
            {
                spatial.tierSeamAdjacency.requestedCount = overrides.tierSeamCount;
            }

            if (overrides.tierSeamMaxRiseLevels >= 0)
            {
                spatial.tierSeamAdjacency.maximumRiseLevels = overrides.tierSeamMaxRiseLevels;
            }

            // Room size deltas resolve against the pitch AFTER any pitch override
            // above, so a topology that moves both stays self-consistent — and
            // are then PACKED BY THE DIAL, exactly as the profile's own sizes
            // are. Without that last step a pitch-relative override is a
            // constant, because the dial holds the pitch fixed, and a topology
            // that declares one silently opts out of density altogether.
            int densityLevel = CurrentGenerationSettings.Validated().densityLevel;
            if (overrides.terminalRoomSizeDelta.HasValue)
            {
                spatial.terminalRoomSize = DungeonGenerationProfile.PackAuthoredRoomSize(
                    overrides.terminalRoomSizeDelta.Value.Resolve(
                        spatial.horizontalPitchCells,
                        spatial.verticalPitchCells),
                    spatial,
                    densityLevel);
            }

            if (overrides.hallRoomSizeDelta.HasValue)
            {
                spatial.hallRoomSize = DungeonGenerationProfile.PackAuthoredRoomSize(
                    overrides.hallRoomSizeDelta.Value.Resolve(
                        spatial.horizontalPitchCells,
                        spatial.verticalPitchCells),
                    spatial,
                    densityLevel);
            }

            if (overrides.connectorRoomSizeDelta.HasValue)
            {
                spatial.connectorRoomSize = DungeonGenerationProfile.PackAuthoredRoomSize(
                    overrides.connectorRoomSizeDelta.Value.Resolve(
                        spatial.horizontalPitchCells,
                        spatial.verticalPitchCells),
                    spatial,
                    densityLevel);
            }

            // A room may never be wider than the lane gap it sits in — but that
            // gap is a property of the NODE, not of the topology, so the clamp
            // lives at inflation time in ResolveAdjacentLaneGaps where the
            // node's embedded position is known. Clamping here instead pinned
            // every room on an axis to the tightest lane anywhere on it, which
            // cost twin-wing-keep (lanes 6,5,6,8,8,9) three cells on every room
            // at every density.
            return spatial.Validated();
        }
    }
}

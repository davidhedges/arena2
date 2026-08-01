using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Plastic.Newtonsoft.Json.Linq;
using UnityEngine;

namespace DungeonLab.Editor
{
    internal sealed partial class DungeonLabGenerator
    {
        private const int ProceduralBranchSearchExpansionLimit = 96;
        private const string ProceduralUpperLayerId = "upper";
        private static int lastProceduralCompositionSearchExpansions;

        private readonly struct ComposedRouteEdge
        {
            public readonly string id;
            public readonly int fromNode;
            public readonly int toNode;
            public readonly RouteTransitionKind transitionKind;
            public readonly string fromLayerId;
            public readonly string toLayerId;

            public ComposedRouteEdge(
                string id,
                int fromNode,
                int toNode,
                RouteTransitionKind transitionKind,
                string fromLayerId = "",
                string toLayerId = "")
            {
                this.id = id ?? string.Empty;
                this.fromNode = fromNode;
                this.toNode = toNode;
                this.transitionKind = transitionKind;
                this.fromLayerId = fromLayerId ?? string.Empty;
                this.toLayerId = toLayerId ?? string.Empty;
            }
        }

        private readonly struct ComposedRouteEdgeTemplate
        {
            public readonly RouteTransitionKind transitionKind;
            public readonly string fromLayerId;
            public readonly string toLayerId;

            public ComposedRouteEdgeTemplate(
                RouteTransitionKind transitionKind,
                string fromLayerId = "",
                string toLayerId = "")
            {
                this.transitionKind = transitionKind;
                this.fromLayerId = fromLayerId ?? string.Empty;
                this.toLayerId = toLayerId ?? string.Empty;
            }
        }

        // The recovered graph seam: add one critical spine, add bounded branches,
        // close every branch with a rejoin, then publish immutable collections.
        // It deliberately knows nothing about coarse cells or room footprints.
        private sealed class RouteGraphComposer
        {
            private readonly List<RouteNodeIntent> nodes = new List<RouteNodeIntent>();
            private readonly List<ComposedRouteEdge> edges = new List<ComposedRouteEdge>();
            private readonly HashSet<string> nodeIds = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> edgeIds = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<int> openBranchTails = new HashSet<int>();
            private bool spineAdded;
            private bool published;

            public bool TryAddSpine(
                IReadOnlyList<RouteNodeIntent> spineNodes,
                IReadOnlyList<ComposedRouteEdgeTemplate> edgeTemplates,
                out int[] nodeIndices,
                out string rejectionReason)
            {
                nodeIndices = Array.Empty<int>();
                rejectionReason = string.Empty;
                if (published || spineAdded || nodes.Count != 0)
                {
                    rejectionReason = published
                        ? "the graph was already published"
                        : "the graph already contains a spine";
                    return false;
                }

                if (spineNodes == null || spineNodes.Count < 2 ||
                    edgeTemplates == null || edgeTemplates.Count != spineNodes.Count - 1 ||
                    !TryValidateNodeBatch(spineNodes, out rejectionReason))
                {
                    rejectionReason = string.IsNullOrEmpty(rejectionReason)
                        ? "a spine needs at least two nodes and one edge template between each pair"
                        : rejectionReason;
                    return false;
                }

                nodeIndices = AppendNodes(spineNodes);
                for (int edge = 0; edge < edgeTemplates.Count; edge++)
                {
                    AppendEdge(
                        $"main-{edge}-{edge + 1}",
                        nodeIndices[edge],
                        nodeIndices[edge + 1],
                        edgeTemplates[edge]);
                }

                spineAdded = true;
                return true;
            }

            public bool TryAddBranch(
                string branchId,
                int attachNode,
                IReadOnlyList<RouteNodeIntent> branchNodes,
                IReadOnlyList<ComposedRouteEdgeTemplate> edgeTemplates,
                out int[] nodeIndices,
                out string rejectionReason)
            {
                nodeIndices = Array.Empty<int>();
                rejectionReason = string.Empty;
                if (published || !spineAdded || attachNode < 0 || attachNode >= nodes.Count)
                {
                    rejectionReason = published
                        ? "the graph was already published"
                        : $"branch attach node {attachNode} does not exist in the composed spine";
                    return false;
                }

                if (string.IsNullOrEmpty(branchId) || branchNodes == null || branchNodes.Count == 0 ||
                    edgeTemplates == null || edgeTemplates.Count != branchNodes.Count ||
                    !TryValidateNodeBatch(branchNodes, out rejectionReason))
                {
                    rejectionReason = string.IsNullOrEmpty(rejectionReason)
                        ? "a branch needs an id, at least one node, and one edge template per node"
                        : rejectionReason;
                    return false;
                }

                nodeIndices = AppendNodes(branchNodes);
                int previous = attachNode;
                for (int edge = 0; edge < edgeTemplates.Count; edge++)
                {
                    AppendEdge(
                        $"{branchId}-{edge}",
                        previous,
                        nodeIndices[edge],
                        edgeTemplates[edge]);
                    previous = nodeIndices[edge];
                }

                openBranchTails.Add(previous);
                return true;
            }

            public bool TryRejoin(
                string branchId,
                int branchTail,
                int targetNode,
                ComposedRouteEdgeTemplate edgeTemplate,
                out string rejectionReason)
            {
                rejectionReason = string.Empty;
                if (published || !openBranchTails.Contains(branchTail) ||
                    targetNode < 0 || targetNode >= nodes.Count || branchTail == targetNode)
                {
                    rejectionReason = published
                        ? "the graph was already published"
                        : $"branch tail {branchTail} cannot rejoin node {targetNode}";
                    return false;
                }

                AppendEdge($"{branchId}-rejoin", branchTail, targetNode, edgeTemplate);
                openBranchTails.Remove(branchTail);
                return true;
            }

            public bool TryPublish(
                out RouteNodeIntent[] publishedNodes,
                out ComposedRouteEdge[] publishedEdges,
                out string rejectionReason)
            {
                publishedNodes = Array.Empty<RouteNodeIntent>();
                publishedEdges = Array.Empty<ComposedRouteEdge>();
                rejectionReason = string.Empty;
                if (published || !spineAdded || openBranchTails.Count != 0)
                {
                    rejectionReason = published
                        ? "the graph was already published"
                        : !spineAdded
                            ? "the graph has no spine"
                            : $"the graph has {openBranchTails.Count} unclosed branch tails";
                    return false;
                }

                publishedNodes = nodes.ToArray();
                publishedEdges = edges.ToArray();
                published = true;
                return true;
            }

            private bool TryValidateNodeBatch(
                IReadOnlyList<RouteNodeIntent> candidates,
                out string rejectionReason)
            {
                rejectionReason = string.Empty;
                var batch = new HashSet<string>(StringComparer.Ordinal);
                foreach (RouteNodeIntent candidate in candidates)
                {
                    if (string.IsNullOrEmpty(candidate.id) ||
                        nodeIds.Contains(candidate.id) || !batch.Add(candidate.id))
                    {
                        rejectionReason = $"node id '{candidate.id}' is missing or duplicated";
                        return false;
                    }
                }

                return true;
            }

            private int[] AppendNodes(IReadOnlyList<RouteNodeIntent> additions)
            {
                var indices = new int[additions.Count];
                for (int index = 0; index < additions.Count; index++)
                {
                    indices[index] = nodes.Count;
                    nodes.Add(additions[index]);
                    nodeIds.Add(additions[index].id);
                }

                return indices;
            }

            private void AppendEdge(
                string id,
                int fromNode,
                int toNode,
                ComposedRouteEdgeTemplate template)
            {
                if (!edgeIds.Add(id))
                {
                    throw new InvalidOperationException($"composed duplicate edge id '{id}'");
                }

                edges.Add(new ComposedRouteEdge(
                    id,
                    fromNode,
                    toNode,
                    template.transitionKind,
                    template.fromLayerId,
                    template.toLayerId));
            }
        }

        private static DungeonRouteTopology ComposeRouteTopologyFamily(
            DungeonRouteTopology definition,
            int dungeonSeed)
        {
            if (definition == null || !definition.IsFamilyDefinition)
            {
                return definition;
            }

            RouteTopologyFamilyConstraints family = definition.family;
            int criticalPathNodes = family.criticalPathNodes.Choose(
                DerivedRandom(dungeonSeed, 0, definition.id, "compose-critical-path-count"));
            int branchNodesPerLoop = family.branchNodes.Choose(
                DerivedRandom(dungeonSeed, 0, definition.id, "compose-branch-node-count"));
            int loopCount = family.loopCount.Choose(
                DerivedRandom(dungeonSeed, 0, definition.id, "compose-loop-count"));
            if (criticalPathNodes != 9 || branchNodesPerLoop != 4 || loopCount < 1 || loopCount > 2)
            {
                throw new InvalidOperationException(
                    $"[ROUTE_COMPOSITION] family '{definition.id}' resolved outside the supported " +
                    "9-node spine / 4-node branch / 1..2-loop grammar");
            }

            var opportunityNames = loopCount == 1
                ? new List<string> { "compression", "return-0" }
                : new List<string> { "landmark", "return-0", "compression-branch" };
            System.Random opportunityRandom = DerivedRandom(
                dungeonSeed,
                0,
                definition.id,
                "compose-recipe-opportunities");
            if (family.recipeOpportunities.minimum > opportunityNames.Count)
            {
                throw new InvalidOperationException(
                    $"[ROUTE_COMPOSITION] family '{definition.id}' requires at least " +
                    $"{family.recipeOpportunities.minimum} opportunities but its composed " +
                    $"{loopCount}-loop graph exposes only {opportunityNames.Count}");
            }

            int opportunityCount = new RouteTopologyIntRange(
                family.recipeOpportunities.minimum,
                Mathf.Min(family.recipeOpportunities.maximum, opportunityNames.Count))
                .Choose(opportunityRandom);
            for (int index = opportunityNames.Count - 1; index > 0; index--)
            {
                int swap = opportunityRandom.Next(index + 1);
                (opportunityNames[index], opportunityNames[swap]) =
                    (opportunityNames[swap], opportunityNames[index]);
            }

            var selectedOpportunities = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < opportunityCount; index++)
            {
                selectedOpportunities.Add(opportunityNames[index]);
            }

            int[] mainLevels = BuildProceduralMainLevels(family.ceilingLevels);
            RouteTopologyLayer[] upperLayer =
            {
                new RouteTopologyLayer(ProceduralUpperLayerId, MajorRiseLevels)
            };
            var mainNodes = new[]
            {
                ProceduralNode(definition.id, "arrival", "arrival", "arrival", 0, -1, mainLevels[0]),
                ProceduralNode(
                    definition.id,
                    loopCount == 1 ? "threshold" : "outer-choice",
                    "connector",
                    loopCount == 1 ? "compression" : "choice",
                    1,
                    -1,
                    mainLevels[1],
                    selectedOpportunities.Contains("compression") ? "opportunity-compression" : string.Empty,
                    null),
                ProceduralNode(
                    definition.id,
                    "lower-approach",
                    "processional-hall",
                    "approach",
                    2,
                    -1,
                    mainLevels[2],
                    layers: loopCount == 2 ? upperLayer : null),
                ProceduralNode(definition.id, "loop-choice", "junction", "choice", 3, -1, mainLevels[3]),
                ProceduralNode(
                    definition.id,
                    "hanging-landmark",
                    "landmark",
                    "hanging-court",
                    4,
                    -1,
                    mainLevels[4],
                    selectedOpportunities.Contains("landmark") ? "opportunity-landmark" : string.Empty,
                    upperLayer),
                ProceduralNode(definition.id, "central-ascent", "connector", "ascent", 5, -1, mainLevels[5]),
                ProceduralNode(definition.id, "loop-rejoin", "junction", "rejoin", 6, -1, mainLevels[6]),
                ProceduralNode(definition.id, "upper-approach", "processional-hall", "approach", 7, -1, mainLevels[7]),
                ProceduralNode(definition.id, "culmination", "culmination", "culmination", 8, -1, mainLevels[8])
            };

            var mainTemplates = new ComposedRouteEdgeTemplate[mainNodes.Length - 1];
            for (int edge = 0; edge < mainTemplates.Length; edge++)
            {
                string fromLayer = edge == 2 && loopCount == 2 || edge == 4
                    ? ProceduralUpperLayerId
                    : string.Empty;
                int fromAbsolute = BoundAbsoluteLevel(mainNodes[edge], fromLayer);
                int toAbsolute = mainNodes[edge + 1].relativeElevationLevels;
                RouteTransitionKind preferred = edge == 5
                    ? RouteTransitionKind.Stairwell
                    : RouteTransitionKind.Stair;
                mainTemplates[edge] = new ComposedRouteEdgeTemplate(
                    ProceduralTransitionKind(fromAbsolute, toAbsolute, preferred),
                    fromLayer);
            }

            var composer = new RouteGraphComposer();
            if (!composer.TryAddSpine(
                    mainNodes,
                    mainTemplates,
                    out int[] mainIndices,
                    out string compositionError))
            {
                throw new InvalidOperationException(
                    $"[ROUTE_COMPOSITION] family '{definition.id}' spine failed: {compositionError}");
            }

            var branchIndices = new List<int[]>();
            int branchOrder = 0;
            for (int loop = 0; loop < loopCount; loop++)
            {
                int attachNode = loop == 0 ? mainIndices[3] : mainIndices[1];
                int rejoinNode = loop == 0 ? mainIndices[6] : mainIndices[7];
                int[] branchLevels = BuildProceduralBranchLevels(
                    mainNodes[attachNode].relativeElevationLevels,
                    mainNodes[rejoinNode].relativeElevationLevels);
                string opportunityName = $"return-{loop}";
                var branchNodes = new[]
                {
                    ProceduralNode(
                        definition.id,
                        $"loop-{loop}-entry",
                        "connector",
                        loop == 1 && selectedOpportunities.Contains("compression-branch")
                            ? "compression"
                            : "branch",
                        -1,
                        branchOrder++,
                        branchLevels[0],
                        loop == 1 && selectedOpportunities.Contains("compression-branch")
                            ? "opportunity-compression-branch"
                            : string.Empty),
                    ProceduralNode(definition.id, $"loop-{loop}-overlook", "overlook", "reveal", -1, branchOrder++, branchLevels[1]),
                    ProceduralNode(definition.id, $"loop-{loop}-reward", "optional-room", "reward", -1, branchOrder++, branchLevels[2]),
                    ProceduralNode(
                        definition.id,
                        $"loop-{loop}-return",
                        "connector",
                        "return",
                        -1,
                        branchOrder++,
                        branchLevels[3],
                        selectedOpportunities.Contains(opportunityName)
                            ? $"opportunity-{opportunityName}"
                            : string.Empty)
                };

                var branchTemplates = new ComposedRouteEdgeTemplate[branchNodes.Length];
                int previousAbsolute = mainNodes[attachNode].relativeElevationLevels;
                for (int edge = 0; edge < branchTemplates.Length; edge++)
                {
                    int nextAbsolute = branchNodes[edge].relativeElevationLevels;
                    branchTemplates[edge] = new ComposedRouteEdgeTemplate(
                        ProceduralTransitionKind(
                            previousAbsolute,
                            nextAbsolute,
                            edge == 0 ? RouteTransitionKind.Bridge : RouteTransitionKind.Stair));
                    previousAbsolute = nextAbsolute;
                }

                string branchId = $"loop-{loop}";
                if (!composer.TryAddBranch(
                        branchId,
                        attachNode,
                        branchNodes,
                        branchTemplates,
                        out int[] addedBranch,
                        out compositionError) ||
                    !composer.TryRejoin(
                        branchId,
                        addedBranch[addedBranch.Length - 1],
                        rejoinNode,
                        new ComposedRouteEdgeTemplate(RouteTransitionKind.LevelCorridor),
                        out compositionError))
                {
                    throw new InvalidOperationException(
                        $"[ROUTE_COMPOSITION] family '{definition.id}' loop {loop} failed: {compositionError}");
                }

                branchIndices.Add(addedBranch);
            }

            if (!composer.TryPublish(
                    out RouteNodeIntent[] composedNodes,
                    out ComposedRouteEdge[] composedEdges,
                    out compositionError))
            {
                throw new InvalidOperationException(
                    $"[ROUTE_COMPOSITION] family '{definition.id}' publish failed: {compositionError}");
            }

            if (!TryEmbedProceduralCoarseGraph(
                    dungeonSeed,
                    definition.id,
                    mainIndices,
                    branchIndices,
                    out Vector2Int[] lattice,
                    out compositionError))
            {
                throw new InvalidOperationException(
                    $"[ROUTE_COMPOSITION] family '{definition.id}' coarse embedding failed: {compositionError}");
            }

            var topologyNodes = new RouteTopologyNode[composedNodes.Length];
            for (int node = 0; node < topologyNodes.Length; node++)
            {
                RouteNodeIntent composed = composedNodes[node];
                topologyNodes[node] = new RouteTopologyNode(
                    $"N{node:00}",
                    composed.id,
                    composed.role,
                    composed.beat,
                    composed.relativeElevationLevels,
                    composed.mainRouteOrder,
                    composed.branchOrder,
                    lattice[node],
                    composed.layers)
                {
                    recipeSlotId = composed.recipeSlotId
                };
            }

            var topologyEdges = new RouteTopologyEdge[composedEdges.Length];
            for (int edge = 0; edge < topologyEdges.Length; edge++)
            {
                ComposedRouteEdge composed = composedEdges[edge];
                topologyEdges[edge] = new RouteTopologyEdge(
                    composed.id,
                    composed.fromNode,
                    composed.toNode,
                    composed.transitionKind,
                    composed.fromLayerId,
                    composed.toLayerId);
            }

            RouteTopologySlot[] slots = BuildProceduralRecipeSlots(topologyNodes, topologyEdges);
            int structuralLayers = 0;
            foreach (RouteTopologyNode node in topologyNodes)
            {
                structuralLayers += node.DeclaresStoreys ? 1 : 0;
            }

            int cycleRank = topologyEdges.Length - (topologyNodes.Length - 1);
            if (structuralLayers < family.minimumStructuralLayers || cycleRank < family.minimumCycleRank)
            {
                throw new InvalidOperationException(
                    $"[ROUTE_COMPOSITION] family '{definition.id}' composed {structuralLayers} structural " +
                    $"layers and cycle rank {cycleRank}, below goals {family.minimumStructuralLayers}/{family.minimumCycleRank}");
            }

            int vistaSourceNode = branchIndices[0][1];
            int vistaTargetNode = mainIndices[4];
            return new DungeonRouteTopology(
                definition.id,
                definition.displayName,
                definition.plannerVersion,
                definition.sourcePath,
                topologyNodes,
                topologyEdges,
                slots,
                $"{definition.id}-generated-vista",
                vistaSourceNode,
                vistaTargetNode,
                family.minimumVistaVoidCells,
                Array.Empty<RouteOverlookIntent>(),
                mainIndices[0],
                mainIndices[mainIndices.Length - 1],
                family.allowGenericRoomWings,
                deprecated: false,
                definition.weight,
                family.ceilingLevels,
                declaresCeiling: true,
                definition.spatialOverrides,
                definition.columnGaps,
                definition.rowGaps,
                definition.latticeColumnCount,
                definition.latticeRowCount,
                family);
        }

        private static RouteNodeIntent ProceduralNode(
            string familyId,
            string semanticId,
            string role,
            string beat,
            int mainRouteOrder,
            int branchOrder,
            int level,
            string recipeSlotId = "",
            RouteTopologyLayer[] layers = null)
        {
            return new RouteNodeIntent(
                $"{familyId}-{semanticId}",
                role,
                beat,
                mainRouteOrder,
                branchOrder,
                level,
                recipeSlotId,
                layers);
        }

        private static int BoundAbsoluteLevel(RouteNodeIntent node, string layerId)
        {
            if (!node.TryGetAbsoluteLevel(layerId, out int absolute))
            {
                throw new InvalidOperationException(
                    $"[ROUTE_COMPOSITION] node '{node.id}' does not declare layer '{layerId}'");
            }

            return absolute;
        }

        private static RouteTransitionKind ProceduralTransitionKind(
            int fromAbsolute,
            int toAbsolute,
            RouteTransitionKind preferred)
        {
            int rise = Mathf.Abs(toAbsolute - fromAbsolute);
            if (rise == 0)
            {
                return RouteTransitionKind.LevelCorridor;
            }

            if (rise != MajorRiseLevels && rise != DoubleMajorRiseLevels)
            {
                throw new InvalidOperationException(
                    $"[ROUTE_COMPOSITION] generated unsupported transition rise {rise}");
            }

            return preferred;
        }

        private static int[] BuildProceduralMainLevels(int ceilingLevels)
        {
            var levels = new[] { 0, 0, 4, 4, 8, 12, 16, 20, 24 };
            int extraSteps = (ceilingLevels - DefaultTopologyCeilingLevels) / MajorRiseLevels;
            int[] widenedEdges = { 7, 6, 5, 4 };
            for (int step = 0; step < extraSteps; step++)
            {
                int edge = widenedEdges[step];
                for (int node = edge + 1; node < levels.Length; node++)
                {
                    levels[node] += MajorRiseLevels;
                }
            }

            return levels;
        }

        private static int[] BuildProceduralBranchLevels(int attachLevel, int rejoinLevel)
        {
            int riseSteps = (rejoinLevel - attachLevel) / MajorRiseLevels;
            if (riseSteps < 3 || riseSteps > 8)
            {
                throw new InvalidOperationException(
                    $"[ROUTE_COMPOSITION] branch from {attachLevel}u to {rejoinLevel}u cannot fit four nodes");
            }

            var increments = new int[4];
            for (int step = 0; step < riseSteps; step++)
            {
                increments[step % increments.Length]++;
            }

            var levels = new int[4];
            int current = attachLevel;
            for (int node = 0; node < levels.Length; node++)
            {
                current += increments[node] * MajorRiseLevels;
                levels[node] = current;
            }

            return levels;
        }

        private static bool TryEmbedProceduralCoarseGraph(
            int dungeonSeed,
            string familyId,
            IReadOnlyList<int> mainIndices,
            IReadOnlyList<int[]> branchIndices,
            out Vector2Int[] lattice,
            out string rejectionReason)
        {
            lattice = new Vector2Int[mainIndices.Count + branchIndices.Count * 4];
            rejectionReason = string.Empty;
            Vector2Int[] mainCoarse =
            {
                new Vector2Int(4, 2),
                new Vector2Int(4, 1),
                new Vector2Int(4, 0),
                new Vector2Int(2, 0),
                new Vector2Int(2, 1),
                new Vector2Int(2, 2),
                new Vector2Int(2, 3),
                new Vector2Int(3, 3),
                new Vector2Int(3, 2)
            };
            var occupied = new HashSet<Vector2Int>();
            for (int node = 0; node < mainIndices.Count; node++)
            {
                lattice[mainIndices[node]] = mainCoarse[node];
                occupied.Add(mainCoarse[node]);
            }

            lastProceduralCompositionSearchExpansions = 0;
            for (int branch = 0; branch < branchIndices.Count; branch++)
            {
                Vector2Int start = mainCoarse[branch == 0 ? 3 : 1];
                Vector2Int goal = mainCoarse[branch == 0 ? 6 : 7];
                if (!TryFindBoundedProceduralCoarsePath(
                        start,
                        goal,
                        occupied,
                        DerivedRandom(dungeonSeed, 0, familyId, $"compose-coarse-branch-{branch}"),
                        out List<Vector2Int> pathIncludingGoal,
                        out int expansions) ||
                    pathIncludingGoal.Count - 1 != branchIndices[branch].Length)
                {
                    rejectionReason =
                        $"bounded branch {branch} did not resolve to {branchIndices[branch].Length} nodes";
                    return false;
                }

                lastProceduralCompositionSearchExpansions += expansions;
                for (int node = 0; node < branchIndices[branch].Length; node++)
                {
                    Vector2Int cell = pathIncludingGoal[node];
                    lattice[branchIndices[branch][node]] = cell;
                    occupied.Add(cell);
                }
            }

            return true;
        }

        // Historical bounded BFS, now used after semantic graph publication.
        // It searches only the six-by-four coarse grammar and never sees world
        // cells, room envelopes, recipes, or the rubber sheet.
        private static bool TryFindBoundedProceduralCoarsePath(
            Vector2Int start,
            Vector2Int goal,
            HashSet<Vector2Int> occupied,
            System.Random random,
            out List<Vector2Int> pathIncludingGoal,
            out int expansionCount)
        {
            pathIncludingGoal = new List<Vector2Int>();
            expansionCount = 0;
            var queue = new Queue<Vector2Int>();
            var parent = new Dictionary<Vector2Int, Vector2Int>();
            var visited = new HashSet<Vector2Int> { start };
            var neighbors = new List<Vector2Int>
            {
                Vector2Int.up,
                Vector2Int.left,
                Vector2Int.right,
                Vector2Int.down
            };
            for (int index = neighbors.Count - 1; index > 0; index--)
            {
                int swap = random.Next(index + 1);
                (neighbors[index], neighbors[swap]) = (neighbors[swap], neighbors[index]);
            }

            queue.Enqueue(start);
            while (queue.Count > 0 && expansionCount < ProceduralBranchSearchExpansionLimit)
            {
                Vector2Int current = queue.Dequeue();
                expansionCount++;
                if (current == goal)
                {
                    var reverse = new List<Vector2Int>();
                    while (current != start)
                    {
                        reverse.Add(current);
                        current = parent[current];
                    }

                    reverse.Reverse();
                    pathIncludingGoal = reverse;
                    return true;
                }

                foreach (Vector2Int offset in neighbors)
                {
                    Vector2Int next = current + offset;
                    if (next.x < 0 || next.x >= ProceduralTopologyLatticeColumnCount ||
                        next.y < 0 || next.y >= ProceduralTopologyLatticeRowCount ||
                        !visited.Add(next) || occupied.Contains(next) && next != goal)
                    {
                        continue;
                    }

                    parent[next] = current;
                    queue.Enqueue(next);
                }
            }

            return false;
        }

        private static RouteTopologySlot[] BuildProceduralRecipeSlots(
            IReadOnlyList<RouteTopologyNode> nodes,
            IReadOnlyList<RouteTopologyEdge> edges)
        {
            var slots = new List<RouteTopologySlot>();
            for (int node = 0; node < nodes.Count; node++)
            {
                RouteTopologyNode declared = nodes[node];
                if (string.IsNullOrEmpty(declared.recipeSlotId))
                {
                    continue;
                }

                string entryEdgeId = string.Empty;
                string exitEdgeId = string.Empty;
                foreach (RouteTopologyEdge edge in edges)
                {
                    if (edge.toNode == node)
                    {
                        entryEdgeId = edge.id;
                    }
                    else if (edge.fromNode == node)
                    {
                        exitEdgeId = edge.id;
                    }
                }

                if (string.IsNullOrEmpty(entryEdgeId) || string.IsNullOrEmpty(exitEdgeId))
                {
                    throw new InvalidOperationException(
                        $"[ROUTE_COMPOSITION] opportunity '{declared.recipeSlotId}' is not a two-port traversal node");
                }

                slots.Add(new RouteTopologySlot(
                    declared.recipeSlotId,
                    node,
                    entryEdgeId,
                    exitEdgeId,
                    string.Equals(declared.role, "landmark", StringComparison.Ordinal)
                        ? RecipeOrientationBinding.VistaSourceToTarget
                        : RecipeOrientationBinding.RouteForward,
                    declared.DeclaresStoreys
                        ? new[]
                        {
                            new RouteTopologySlotLayer(
                                ProceduralUpperLayerId,
                                ProceduralUpperLayerId)
                        }
                        : Array.Empty<RouteTopologySlotLayer>()));
            }

            return slots.ToArray();
        }

        // Focused Slice 4 evidence. It deliberately stops before catalog or room
        // placement so it measures only the replaced producer and its seam.
        private static string BuildProceduralRouteCompositionSnapshot(int seed)
        {
            DungeonRouteTopology definition = RequireRouteTopology(SelectRouteTopologyId(seed));
            DungeonRouteTopology first = ComposeRouteTopologyFamily(definition, seed);
            int firstSearchExpansions = lastProceduralCompositionSearchExpansions;
            DungeonRouteTopology second = ComposeRouteTopologyFamily(definition, seed);
            string firstSignature = ProceduralTopologySignature(first);
            string secondSignature = ProceduralTopologySignature(second);

            int productionFamilies = 0;
            int authoredExactFields = 0;
            int minimumOpportunities = int.MaxValue;
            int maximumOpportunities = int.MinValue;
            bool sawOneLoop = false;
            bool sawTwoLoops = false;
            bool allGeneratedSurfaceGraphsConnected = true;
            bool allOpportunityContractsValid = true;
            bool allStaticTopologyRulesPass = true;
            bool allSearchesBounded = true;
            int maximumSearchExpansions = 0;
            foreach (DungeonRouteTopology candidate in AllRouteTopologiesByFileOrder())
            {
                if (!candidate.IsFamilyDefinition || candidate.weight <= 0)
                {
                    continue;
                }

                productionFamilies++;
                JObject authored = JObject.Parse(File.ReadAllText(candidate.sourcePath));
                foreach (string exact in new[]
                         {
                             "map", "nodes", "edges", "slots", "vista", "overlooks", "anchors", "ceiling"
                         })
                {
                    authoredExactFields += authored[exact] != null ? 1 : 0;
                }

                for (int sample = 0; sample < 128; sample++)
                {
                    DungeonRouteTopology composed = ComposeRouteTopologyFamily(
                        candidate,
                        seed + sample);
                    minimumOpportunities = Mathf.Min(minimumOpportunities, composed.slots.Length);
                    maximumOpportunities = Mathf.Max(maximumOpportunities, composed.slots.Length);
                    int rank = composed.edges.Length - (composed.nodes.Length - 1);
                    maximumSearchExpansions = Mathf.Max(
                        maximumSearchExpansions,
                        lastProceduralCompositionSearchExpansions);
                    allSearchesBounded &= lastProceduralCompositionSearchExpansions > 0 &&
                        lastProceduralCompositionSearchExpansions <=
                        ProceduralBranchSearchExpansionLimit * rank;
                    sawOneLoop |= rank == 1;
                    sawTwoLoops |= rank == 2;
                    allGeneratedSurfaceGraphsConnected &= ProceduralSurfaceGraphConnected(composed);
                    List<int>[] adjacency = composed.BuildAdjacency();
                    var semanticContracts = new HashSet<string>(StringComparer.Ordinal);
                    foreach (RouteTopologySlot slot in composed.slots)
                    {
                        RouteTopologyNode node = composed.nodes[slot.node];
                        allOpportunityContractsValid &= adjacency[slot.node].Count == 2 &&
                            semanticContracts.Add($"{node.role}:{node.beat}");
                    }

                    var violations = new List<string>();
                    AppendRouteTopologyGraphRules(composed, violations);
                    AppendRouteTopologyLatticeRules(composed, violations);
                    AppendRouteTopologyRhythmRules(composed, violations);
                    allStaticTopologyRulesPass &= violations.Count == 0;
                }
            }

            bool connected = ProceduralTopologyConnected(first);
            bool surfaceGraphConnected = ProceduralSurfaceGraphConnected(first);
            bool cardinal = true;
            bool structural = true;
            bool bindingsResolve = true;
            var kinds = new HashSet<RouteTransitionKind>();
            foreach (RouteTopologyNode node in first.nodes)
            {
                structural &= IsStructuralLevel(node.level);
                foreach (RouteTopologyLayer layer in node.layers)
                {
                    structural &= IsStructuralLevel(layer.relativeLevel) &&
                        IsStructuralLevel(node.level + layer.relativeLevel);
                }
            }

            foreach (RouteTopologyEdge edge in first.edges)
            {
                Vector2Int from = first.nodes[edge.fromNode].lattice;
                Vector2Int to = first.nodes[edge.toNode].lattice;
                cardinal &= from.x == to.x || from.y == to.y;
                bool fromResolved = first.nodes[edge.fromNode].TryGetAbsoluteLevel(
                    edge.fromLayerId,
                    out int fromLevel);
                bool toResolved = first.nodes[edge.toNode].TryGetAbsoluteLevel(
                    edge.toLayerId,
                    out int toLevel);
                bindingsResolve &= fromResolved && toResolved;
                int rise = Mathf.Abs(toLevel - fromLevel);
                bindingsResolve &= edge.transitionKind == RouteTransitionKind.LevelCorridor
                    ? rise == 0
                    : rise == MajorRiseLevels || rise == DoubleMajorRiseLevels;
                kinds.Add(edge.transitionKind);
            }

            int layeredNodes = 0;
            foreach (RouteTopologyNode node in first.nodes)
            {
                layeredNodes += node.DeclaresStoreys ? 1 : 0;
            }

            int cycleRank = first.edges.Length - (first.nodes.Length - 1);
            var lines = new[]
            {
                $"family.selected={definition.id}",
                $"family.definition={definition.IsFamilyDefinition}",
                $"family.productionCount={productionFamilies}",
                $"family.authoredExactFieldCount={authoredExactFields}",
                $"families.allStaticRulesPass={allStaticTopologyRulesPass}",
                $"composer.deterministic={firstSignature == secondSignature}",
                $"composer.nodes={first.nodes.Length}",
                $"composer.edges={first.edges.Length}",
                $"composer.connected={connected}",
                $"composer.surfaceGraphConnected={surfaceGraphConnected}",
                $"composer.cycleRank={cycleRank}",
                $"composer.layeredNodes={layeredNodes}",
                $"composer.recipeOpportunities={first.slots.Length}",
                $"composer.searchExpansions={firstSearchExpansions}",
                $"composer.searchWithinBound={allSearchesBounded}",
                $"composer.cardinalCoarseEmbedding={cardinal}",
                $"composer.structuralLattice={structural}",
                $"composer.bindingsResolve={bindingsResolve}",
                $"composer.hasLevelCorridor={kinds.Contains(RouteTransitionKind.LevelCorridor)}",
                $"composer.hasStair={kinds.Contains(RouteTransitionKind.Stair)}",
                $"composer.hasBridge={kinds.Contains(RouteTransitionKind.Bridge)}",
                $"composer.hasStairwell={kinds.Contains(RouteTransitionKind.Stairwell)}",
                $"opportunities.minimumObserved={minimumOpportunities}",
                $"opportunities.maximumObserved={maximumOpportunities}",
                $"opportunities.contractsValid={allOpportunityContractsValid}",
                $"search.maximumObserved={maximumSearchExpansions}",
                $"surfaces.allSamplesConnected={allGeneratedSurfaceGraphsConnected}",
                $"loops.sawOne={sawOneLoop}",
                $"loops.sawTwo={sawTwoLoops}"
            };
            return string.Join("\n", lines);
        }

        private static bool ProceduralTopologyConnected(DungeonRouteTopology topology)
        {
            if (topology.nodes.Length == 0)
            {
                return false;
            }

            List<int>[] adjacency = topology.BuildAdjacency();
            var visited = new HashSet<int> { 0 };
            var queue = new Queue<int>();
            queue.Enqueue(0);
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

            return visited.Count == topology.nodes.Length;
        }

        private static bool ProceduralSurfaceGraphConnected(DungeonRouteTopology topology)
        {
            var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            for (int node = 0; node < topology.nodes.Length; node++)
            {
                AddProceduralSurfaceVertex(adjacency, ProceduralSurfaceVertex(node, string.Empty));
                foreach (RouteTopologyLayer layer in topology.nodes[node].layers)
                {
                    AddProceduralSurfaceVertex(adjacency, ProceduralSurfaceVertex(node, layer.layerId));
                }
            }

            foreach (RouteTopologyEdge edge in topology.edges)
            {
                string from = ProceduralSurfaceVertex(edge.fromNode, edge.fromLayerId);
                string to = ProceduralSurfaceVertex(edge.toNode, edge.toLayerId);
                if (!adjacency.ContainsKey(from) || !adjacency.ContainsKey(to))
                {
                    return false;
                }

                adjacency[from].Add(to);
                adjacency[to].Add(from);
            }

            if (adjacency.Count == 0)
            {
                return false;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            using (Dictionary<string, HashSet<string>>.KeyCollection.Enumerator enumerator =
                   adjacency.Keys.GetEnumerator())
            {
                enumerator.MoveNext();
                visited.Add(enumerator.Current);
                queue.Enqueue(enumerator.Current);
            }

            while (queue.Count > 0)
            {
                foreach (string neighbor in adjacency[queue.Dequeue()])
                {
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            return visited.Count == adjacency.Count;
        }

        private static string ProceduralSurfaceVertex(int node, string layerId)
        {
            return $"{node}:{layerId ?? string.Empty}";
        }

        private static void AddProceduralSurfaceVertex(
            IDictionary<string, HashSet<string>> adjacency,
            string vertex)
        {
            if (!adjacency.ContainsKey(vertex))
            {
                adjacency[vertex] = new HashSet<string>(StringComparer.Ordinal);
            }
        }

        private static string ProceduralTopologySignature(DungeonRouteTopology topology)
        {
            var signature = new StringBuilder();
            foreach (RouteTopologyNode node in topology.nodes)
            {
                signature.Append(node.id).Append(':')
                    .Append(node.level).Append('@')
                    .Append(node.lattice.x).Append(',').Append(node.lattice.y).Append(':')
                    .Append(node.recipeSlotId).Append('|');
                foreach (RouteTopologyLayer layer in node.layers)
                {
                    signature.Append(layer.layerId).Append('+').Append(layer.relativeLevel).Append(',');
                }
            }

            foreach (RouteTopologyEdge edge in topology.edges)
            {
                signature.Append(edge.id).Append(':')
                    .Append(edge.fromNode).Append('>').Append(edge.toNode).Append(':')
                    .Append(edge.transitionKind).Append(':')
                    .Append(edge.fromLayerId).Append('>').Append(edge.toLayerId).Append('|');
            }

            return signature.ToString();
        }
    }
}

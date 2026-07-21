using System;
using System.Collections.Generic;

namespace DungeonLab.Editor
{
    internal sealed partial class DungeonLabGenerator
    {
        // This composer is intentionally narrower than a macro-topology framework.
        // Phase 6a exposes only the operations already consumed by the production
        // processional graph and publishes the existing RouteIntent collections.
        private sealed class RouteGraphComposer
        {
            private readonly List<RouteNodeIntent> nodes = new List<RouteNodeIntent>();
            private readonly List<RouteTraversalIntent> edges = new List<RouteTraversalIntent>();
            private readonly HashSet<string> nodeIds = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> edgeIds = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<int> openBranchTails = new HashSet<int>();
            private bool spineAdded;
            private bool published;

            public int NodeCount => nodes.Count;

            public int EdgeCount => edges.Count;

            public bool TryAddSpine(
                RouteNodeIntent[] spineNodes,
                string[] traversalEdgeIds,
                RouteTransitionKind[] transitionKinds,
                out int[] nodeIndices,
                out string rejectionReason)
            {
                nodeIndices = Array.Empty<int>();
                rejectionReason = string.Empty;
                if (!TryBeginOperation(out rejectionReason))
                {
                    return false;
                }

                if (spineAdded || nodes.Count != 0)
                {
                    rejectionReason = "the graph already contains a spine";
                    return false;
                }

                if (spineNodes == null || spineNodes.Length < 2 ||
                    traversalEdgeIds == null || traversalEdgeIds.Length != spineNodes.Length - 1 ||
                    transitionKinds == null || transitionKinds.Length != traversalEdgeIds.Length)
                {
                    rejectionReason = "a spine requires at least two nodes and one typed edge between each consecutive pair";
                    return false;
                }

                if (!TryValidateNodeBatch(spineNodes, out rejectionReason) ||
                    !TryValidateEdgeIdBatch(traversalEdgeIds, out rejectionReason))
                {
                    return false;
                }

                nodeIndices = AppendNodes(spineNodes);
                for (int edge = 0; edge < traversalEdgeIds.Length; edge++)
                {
                    AppendEdge(
                        traversalEdgeIds[edge],
                        nodeIndices[edge],
                        nodeIndices[edge + 1],
                        transitionKinds[edge]);
                }

                spineAdded = true;
                return true;
            }

            public bool TryAddBranch(
                int attachNode,
                RouteNodeIntent[] branchNodes,
                string[] traversalEdgeIds,
                RouteTransitionKind[] transitionKinds,
                out int[] nodeIndices,
                out string rejectionReason)
            {
                nodeIndices = Array.Empty<int>();
                rejectionReason = string.Empty;
                if (!TryBeginOperation(out rejectionReason))
                {
                    return false;
                }

                if (!spineAdded || attachNode < 0 || attachNode >= nodes.Count)
                {
                    rejectionReason = $"branch attach node {attachNode} does not exist in the composed spine";
                    return false;
                }

                if (branchNodes == null || branchNodes.Length == 0 ||
                    traversalEdgeIds == null || traversalEdgeIds.Length != branchNodes.Length ||
                    transitionKinds == null || transitionKinds.Length != traversalEdgeIds.Length)
                {
                    rejectionReason = "a branch requires at least one node and one typed edge from its attachment through each branch node";
                    return false;
                }

                if (!TryValidateNodeBatch(branchNodes, out rejectionReason) ||
                    !TryValidateEdgeIdBatch(traversalEdgeIds, out rejectionReason))
                {
                    return false;
                }

                nodeIndices = AppendNodes(branchNodes);
                int previous = attachNode;
                for (int edge = 0; edge < traversalEdgeIds.Length; edge++)
                {
                    AppendEdge(
                        traversalEdgeIds[edge],
                        previous,
                        nodeIndices[edge],
                        transitionKinds[edge]);
                    previous = nodeIndices[edge];
                }

                openBranchTails.Add(previous);
                return true;
            }

            public bool TryRejoin(
                int branchTail,
                int targetNode,
                string traversalEdgeId,
                RouteTransitionKind transitionKind,
                out string rejectionReason)
            {
                rejectionReason = string.Empty;
                if (!TryBeginOperation(out rejectionReason))
                {
                    return false;
                }

                if (!openBranchTails.Contains(branchTail))
                {
                    rejectionReason = $"node {branchTail} is not an open branch tail";
                    return false;
                }

                if (targetNode < 0 || targetNode >= nodes.Count)
                {
                    rejectionReason = $"rejoin target node {targetNode} does not exist";
                    return false;
                }

                if (branchTail == targetNode)
                {
                    rejectionReason = "a branch cannot rejoin itself";
                    return false;
                }

                if (string.IsNullOrEmpty(traversalEdgeId) || edgeIds.Contains(traversalEdgeId))
                {
                    rejectionReason = $"rejoin edge id '{traversalEdgeId}' is missing or duplicated";
                    return false;
                }

                AppendEdge(traversalEdgeId, branchTail, targetNode, transitionKind);
                openBranchTails.Remove(branchTail);
                return true;
            }

            public bool TryPublish(
                out RouteNodeIntent[] publishedNodes,
                out RouteTraversalIntent[] publishedEdges,
                out string rejectionReason)
            {
                publishedNodes = Array.Empty<RouteNodeIntent>();
                publishedEdges = Array.Empty<RouteTraversalIntent>();
                rejectionReason = string.Empty;
                if (published)
                {
                    rejectionReason = "the graph was already published";
                    return false;
                }

                if (!spineAdded || openBranchTails.Count != 0)
                {
                    rejectionReason = !spineAdded
                        ? "the graph has no spine"
                        : $"the graph has {openBranchTails.Count} branch tails without a rejoin";
                    return false;
                }

                publishedNodes = nodes.ToArray();
                publishedEdges = edges.ToArray();
                published = true;
                return true;
            }

            private bool TryBeginOperation(out string rejectionReason)
            {
                rejectionReason = published ? "the graph was already published" : string.Empty;
                return !published;
            }

            private bool TryValidateNodeBatch(
                IReadOnlyList<RouteNodeIntent> candidates,
                out string rejectionReason)
            {
                rejectionReason = string.Empty;
                var batchIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (RouteNodeIntent candidate in candidates)
                {
                    if (string.IsNullOrEmpty(candidate.id) ||
                        nodeIds.Contains(candidate.id) ||
                        !batchIds.Add(candidate.id))
                    {
                        rejectionReason = $"node id '{candidate.id}' is missing or duplicated";
                        return false;
                    }
                }

                return true;
            }

            private bool TryValidateEdgeIdBatch(
                IReadOnlyList<string> candidates,
                out string rejectionReason)
            {
                rejectionReason = string.Empty;
                var batchIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (string candidate in candidates)
                {
                    if (string.IsNullOrEmpty(candidate) ||
                        edgeIds.Contains(candidate) ||
                        !batchIds.Add(candidate))
                    {
                        rejectionReason = $"edge id '{candidate}' is missing or duplicated";
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
                    RouteNodeIntent node = additions[index];
                    indices[index] = nodes.Count;
                    nodes.Add(node);
                    nodeIds.Add(node.id);
                }

                return indices;
            }

            private void AppendEdge(
                string id,
                int fromNode,
                int toNode,
                RouteTransitionKind transitionKind)
            {
                edges.Add(new RouteTraversalIntent(
                    id,
                    fromNode,
                    toNode,
                    nodes[toNode].relativeElevationLevels - nodes[fromNode].relativeElevationLevels,
                    transitionKind));
                edgeIds.Add(id);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // The semantic graph exists before any room coordinates, then this solver
    // compiles it directly into the existing DungeonLayout.
    internal sealed partial class DungeonLabGenerator
    {
        private const string Phase1PlannerVersion = "processional-spine-v1";
        private const string Phase1PatternId = "processional-spine";
        private const int Phase1LayoutAttemptLimit = 2;
        private const int Phase1MainNodeCount = 9;
        private const int Phase1BranchNodeCount = 4;
        private const int Phase1BranchAttachNode = 2;
        private const int Phase1BranchRejoinNode = 7;
        private const int Phase1VistaSourceNode = 9;
        private const int Phase1VistaTargetNode = 4;
        private const int Phase1RoomEnvelopeRadius = 4;
        private const int Phase1BranchSearchExpansionLimit = 24;
        private const int Phase1RoomInflationAttemptLimit = 6;

        // Ephemeral diagnostic evidence for the most recent attempt.
        // It is never consumed by generation or carried into DungeonLayout.
        private static RouteIntent phase1LastRouteIntent;
        private static Vector2Int[] phase1LastNodeCenters = Array.Empty<Vector2Int>();
        private static Vector2Int[] phase1LastVistaCells = Array.Empty<Vector2Int>();
        private static Vector2Int phase1LastVistaSourceFacing;
        private static Vector2Int phase1LastVistaTargetFacing;
        private static int phase1LastLayoutAttempt;
        private static int phase1LastMainEmbeddingAttempts;
        private static int phase1LastBranchSearchExpansions;
        private static int phase1LastRoomInflationAttempts;
        private static string phase1LastFailureCode = string.Empty;

        private sealed class RouteIntent
        {
            public readonly int seed;
            public readonly string plannerVersion;
            public readonly string patternId;
            public readonly RouteNodeIntent[] nodes;
            public readonly RouteTraversalIntent[] traversalEdges;
            public readonly RouteVistaIntent vista;
            public readonly int bottomNode;
            public readonly int topNode;

            public RouteIntent(
                int seed,
                RouteNodeIntent[] nodes,
                RouteTraversalIntent[] traversalEdges,
                RouteVistaIntent vista,
                int bottomNode,
                int topNode)
            {
                this.seed = seed;
                plannerVersion = Phase1PlannerVersion;
                patternId = Phase1PatternId;
                this.nodes = nodes;
                this.traversalEdges = traversalEdges;
                this.vista = vista;
                this.bottomNode = bottomNode;
                this.topNode = topNode;
            }
        }

        private readonly struct RouteNodeIntent
        {
            public readonly string id;
            public readonly string role;
            public readonly string beat;
            public readonly int mainRouteOrder;
            public readonly int branchOrder;

            public RouteNodeIntent(
                string id,
                string role,
                string beat,
                int mainRouteOrder,
                int branchOrder)
            {
                this.id = id;
                this.role = role;
                this.beat = beat;
                this.mainRouteOrder = mainRouteOrder;
                this.branchOrder = branchOrder;
            }

            public bool IsOnMainRoute => mainRouteOrder >= 0;
        }

        private readonly struct RouteTraversalIntent
        {
            public readonly string id;
            public readonly int fromNode;
            public readonly int toNode;
            public readonly int laneCount;

            public RouteTraversalIntent(string id, int fromNode, int toNode)
            {
                this.id = id;
                this.fromNode = fromNode;
                this.toNode = toNode;
                laneCount = 1;
            }
        }

        private readonly struct RouteVistaIntent
        {
            public readonly string id;
            public readonly int sourceNode;
            public readonly int targetNode;
            public readonly int minimumReservedVoidCells;

            public RouteVistaIntent(
                string id,
                int sourceNode,
                int targetNode,
                int minimumReservedVoidCells)
            {
                this.id = id;
                this.sourceNode = sourceNode;
                this.targetNode = targetNode;
                this.minimumReservedVoidCells = minimumReservedVoidCells;
            }
        }

        private static void ResetPhase1RouteDiagnostics()
        {
            phase1LastRouteIntent = null;
            phase1LastNodeCenters = Array.Empty<Vector2Int>();
            phase1LastVistaCells = Array.Empty<Vector2Int>();
            phase1LastVistaSourceFacing = Vector2Int.zero;
            phase1LastVistaTargetFacing = Vector2Int.zero;
            phase1LastLayoutAttempt = 0;
            phase1LastMainEmbeddingAttempts = 0;
            phase1LastBranchSearchExpansions = 0;
            phase1LastRoomInflationAttempts = 0;
            phase1LastFailureCode = string.Empty;
        }

        private static bool TryBuildProcessionalSpineDungeonLayout(
            int dungeonSeed,
            int layoutAttempt,
            out DungeonLayout layout,
            out string rejectionReason)
        {
            layout = default;
            rejectionReason = string.Empty;
            ResetPhase1RouteDiagnostics();
            phase1LastLayoutAttempt = layoutAttempt;

            RouteIntent intent = BuildProcessionalRouteIntent(dungeonSeed);
            phase1LastRouteIntent = intent;
            if (!TryValidateProcessionalRouteIntent(intent, out rejectionReason))
            {
                return RejectPhase1Route("ROUTE_INTENT_INVALID", rejectionReason, out rejectionReason);
            }

            if (!TryEmbedProcessionalRoute(
                    dungeonSeed,
                    layoutAttempt,
                    intent,
                    out Vector2Int[] nodeCenters,
                    out string embeddingFailureCode,
                    out rejectionReason))
            {
                return RejectPhase1Route(embeddingFailureCode, rejectionReason, out rejectionReason);
            }

            phase1LastNodeCenters = nodeCenters;
            DungeonGenerationSettings settings = CurrentGenerationSettings.Validated();
            var roomEnvelopes = new RectInt[intent.nodes.Length];
            for (int node = 0; node < nodeCenters.Length; node++)
            {
                roomEnvelopes[node] = new RectInt(
                    nodeCenters[node].x - Phase1RoomEnvelopeRadius,
                    nodeCenters[node].y - Phase1RoomEnvelopeRadius,
                    Phase1RoomEnvelopeRadius * 2 + 1,
                    Phase1RoomEnvelopeRadius * 2 + 1);
            }

            if (!TryInflateProcessionalRooms(
                    dungeonSeed,
                    layoutAttempt,
                    intent,
                    nodeCenters,
                    roomEnvelopes,
                    out List<RoomFootprint> rooms,
                    out rejectionReason))
            {
                return RejectPhase1Route("ROUTE_ROOM_INFLATION_EXHAUSTED", rejectionReason, out rejectionReason);
            }

            if (!TryReserveProcessionalVista(
                    intent,
                    rooms,
                    nodeCenters,
                    out HashSet<Vector2Int> reservedVistaCells,
                    out Vector2Int sourceFacing,
                    out Vector2Int targetFacing,
                    out rejectionReason))
            {
                return RejectPhase1Route("ROUTE_VISTA_RESERVATION_BLOCKED", rejectionReason, out rejectionReason);
            }

            phase1LastVistaCells = SortedCells(reservedVistaCells).ToArray();
            phase1LastVistaSourceFacing = sourceFacing;
            phase1LastVistaTargetFacing = targetFacing;

            if (!TryConnectProcessionalRooms(
                    intent,
                    rooms,
                    reservedVistaCells,
                    out HashSet<Vector2Int> floorCells,
                    out List<RoomConnection> connections,
                    out rejectionReason))
            {
                return RejectPhase1Route("ROUTE_CORRIDOR_EMBEDDING_EXHAUSTED", rejectionReason, out rejectionReason);
            }

            if (!IsConnected(floorCells))
            {
                return RejectPhase1Route(
                    "ROUTE_FLOOR_DISCONNECTED",
                    "compiled route-first floor mask was disconnected",
                    out rejectionReason);
            }

            if (rooms.Count < settings.denseFloorplanMinRooms ||
                CalculateFloorFillPercent(floorCells) < settings.denseFloorplanMinFillPercent)
            {
                return RejectPhase1Route(
                    "ROUTE_DENSITY_PRECONDITION",
                    $"compiled {rooms.Count} rooms at {CalculateFloorFillPercent(floorCells) * 100f:0.#}% fill; " +
                    $"profile requires {settings.denseFloorplanMinRooms} rooms and {settings.denseFloorplanMinFillPercent * 100f:0.#}% fill",
                    out rejectionReason);
            }

            Dictionary<int, HashSet<Vector2Int>> thresholds = BuildRoomThresholdCells(rooms, connections);
            System.Random zoneRandom = Phase1Random(
                dungeonSeed,
                layoutAttempt,
                "layout",
                "room-zones");
            List<RoomZonePlan> roomZones = ChooseRoomZoneSplits(
                rooms,
                thresholds,
                zoneRandom,
                settings);
            StepFormationModeTable connectorTable = LoadAuthoredStairConnectorTableForGeneration();
            int connectorCandidateCount = connectorTable != null
                ? CountConfiguredStairConnectorPrefabs(connectorTable)
                : 0;
            layout = new DungeonLayout(
                floorCells,
                rooms,
                connections,
                roomZones,
                connectorCandidateCount);
            phase1LastFailureCode = string.Empty;
            return true;
        }

        private static RouteIntent BuildProcessionalRouteIntent(int dungeonSeed)
        {
            var nodes = new[]
            {
                new RouteNodeIntent("arrival", "arrival", "arrival", 0, -1),
                new RouteNodeIntent("threshold", "connector", "compression", 1, -1),
                new RouteNodeIntent("choice", "junction", "choice", 2, -1),
                new RouteNodeIntent("reveal", "grand-room", "reveal", 3, -1),
                new RouteNodeIntent("vista-target", "landmark", "landmark", 4, -1),
                new RouteNodeIntent("ascent", "connector", "ascent", 5, -1),
                new RouteNodeIntent("approach", "processional-hall", "approach", 6, -1),
                new RouteNodeIntent("rejoin", "return-hall", "rejoin", 7, -1),
                new RouteNodeIntent("culmination", "culmination", "culmination", 8, -1),
                new RouteNodeIntent("vista-source", "overlook", "reveal", -1, 0),
                new RouteNodeIntent("branch-passage", "connector", "branch", -1, 1),
                new RouteNodeIntent("branch-reward", "optional-room", "reward", -1, 2),
                new RouteNodeIntent("branch-return", "connector", "return", -1, 3)
            };

            var edges = new List<RouteTraversalIntent>();
            for (int node = 0; node < Phase1MainNodeCount - 1; node++)
            {
                edges.Add(new RouteTraversalIntent($"main-{node}-{node + 1}", node, node + 1));
            }

            int previous = Phase1BranchAttachNode;
            for (int branch = 0; branch < Phase1BranchNodeCount; branch++)
            {
                int current = Phase1MainNodeCount + branch;
                edges.Add(new RouteTraversalIntent($"branch-{previous}-{current}", previous, current));
                previous = current;
            }

            edges.Add(new RouteTraversalIntent(
                $"rejoin-{previous}-{Phase1BranchRejoinNode}",
                previous,
                Phase1BranchRejoinNode));
            return new RouteIntent(
                dungeonSeed,
                nodes,
                edges.ToArray(),
                new RouteVistaIntent(
                    "branch-overlook-to-landmark",
                    Phase1VistaSourceNode,
                    Phase1VistaTargetNode,
                    minimumReservedVoidCells: 3),
                bottomNode: 0,
                topNode: Phase1MainNodeCount - 1);
        }

        private static bool TryValidateProcessionalRouteIntent(RouteIntent intent, out string rejectionReason)
        {
            rejectionReason = string.Empty;
            if (intent == null || intent.nodes == null || intent.traversalEdges == null)
            {
                rejectionReason = "route intent or its graph collections were null";
                return false;
            }

            if (intent.nodes.Length != Phase1MainNodeCount + Phase1BranchNodeCount)
            {
                rejectionReason = $"route intent had {intent.nodes.Length} nodes instead of {Phase1MainNodeCount + Phase1BranchNodeCount}";
                return false;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (RouteNodeIntent node in intent.nodes)
            {
                if (string.IsNullOrEmpty(node.id) || !ids.Add(node.id))
                {
                    rejectionReason = $"route intent contained a missing or duplicate node id '{node.id}'";
                    return false;
                }
            }

            var adjacency = new List<int>[intent.nodes.Length];
            for (int node = 0; node < adjacency.Length; node++)
            {
                adjacency[node] = new List<int>();
            }

            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                if (edge.fromNode < 0 || edge.fromNode >= intent.nodes.Length ||
                    edge.toNode < 0 || edge.toNode >= intent.nodes.Length ||
                    edge.fromNode == edge.toNode ||
                    edge.laneCount != 1)
                {
                    rejectionReason = $"route edge '{edge.id}' had invalid endpoints or lane count";
                    return false;
                }

                adjacency[edge.fromNode].Add(edge.toNode);
                adjacency[edge.toNode].Add(edge.fromNode);
            }

            var visited = new HashSet<int> { intent.bottomNode };
            var queue = new Queue<int>();
            queue.Enqueue(intent.bottomNode);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int neighbor in adjacency[current])
                {
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            int loopEdges = intent.traversalEdges.Length - (intent.nodes.Length - 1);
            if (visited.Count != intent.nodes.Length ||
                loopEdges != 1 ||
                adjacency[Phase1BranchAttachNode].Count != 3 ||
                adjacency[Phase1BranchRejoinNode].Count != 3)
            {
                rejectionReason =
                    $"route graph reached {visited.Count}/{intent.nodes.Length} nodes with {loopEdges} loop edges; " +
                    "the branch attach and rejoin nodes must each have degree 3";
                return false;
            }

            if (intent.vista.sourceNode < 0 || intent.vista.sourceNode >= intent.nodes.Length ||
                intent.vista.targetNode < 0 || intent.vista.targetNode >= intent.nodes.Length ||
                intent.vista.sourceNode == intent.vista.targetNode ||
                intent.vista.minimumReservedVoidCells < 1)
            {
                rejectionReason = "route vista intent had invalid endpoints or reservation length";
                return false;
            }

            return true;
        }

        private static bool TryEmbedProcessionalRoute(
            int dungeonSeed,
            int layoutAttempt,
            RouteIntent intent,
            out Vector2Int[] nodeCenters,
            out string failureCode,
            out string rejectionReason)
        {
            nodeCenters = Array.Empty<Vector2Int>();
            failureCode = "ROUTE_MAIN_EMBEDDING_EXHAUSTED";
            rejectionReason = string.Empty;
            var mainCoarse = new[]
            {
                new Vector2Int(0, 0),
                new Vector2Int(1, 0),
                new Vector2Int(2, 0),
                new Vector2Int(3, 0),
                new Vector2Int(3, 1),
                new Vector2Int(3, 2),
                new Vector2Int(2, 2),
                new Vector2Int(1, 2),
                new Vector2Int(1, 3)
            };

            var occupied = new HashSet<Vector2Int>(mainCoarse);
            if (occupied.Count != mainCoarse.Length)
            {
                failureCode = "ROUTE_MAIN_EMBEDDING_INVALID";
                rejectionReason = "main coarse route was not self-avoiding";
                return false;
            }

            if (!TryFindBoundedCoarsePath(
                    mainCoarse[Phase1BranchAttachNode],
                    new Vector2Int(0, 1),
                    occupied,
                    allowGoal: true,
                    out List<Vector2Int> firstBranchSegment,
                    out int firstExpansions))
            {
                failureCode = "ROUTE_BRANCH_EMBEDDING_EXHAUSTED";
                rejectionReason = "bounded branch search could not reach the reward cell";
                return false;
            }

            foreach (Vector2Int cell in firstBranchSegment)
            {
                occupied.Add(cell);
            }

            if (!TryFindBoundedCoarsePath(
                    new Vector2Int(0, 1),
                    mainCoarse[Phase1BranchRejoinNode],
                    occupied,
                    allowGoal: true,
                    out List<Vector2Int> secondBranchSegment,
                    out int secondExpansions))
            {
                failureCode = "ROUTE_BRANCH_EMBEDDING_EXHAUSTED";
                rejectionReason = "bounded branch search could not reach the rejoin beat";
                return false;
            }

            phase1LastBranchSearchExpansions = firstExpansions + secondExpansions;
            var branchCoarse = new List<Vector2Int>(firstBranchSegment);
            for (int i = 0; i < secondBranchSegment.Count - 1; i++)
            {
                branchCoarse.Add(secondBranchSegment[i]);
            }

            if (branchCoarse.Count != Phase1BranchNodeCount)
            {
                failureCode = "ROUTE_BRANCH_EMBEDDING_INVALID";
                rejectionReason = $"bounded branch search produced {branchCoarse.Count} branch nodes instead of {Phase1BranchNodeCount}";
                return false;
            }

            System.Random placementRandom = Phase1Random(
                dungeonSeed,
                layoutAttempt,
                "route",
                "orientation");
            int firstQuarterTurn = placementRandom.Next(4);
            bool mirror = placementRandom.Next(2) == 0;
            for (int orientationAttempt = 0; orientationAttempt < 4; orientationAttempt++)
            {
                phase1LastMainEmbeddingAttempts = orientationAttempt + 1;
                int quarterTurns = (firstQuarterTurn + orientationAttempt) % 4;
                var transformed = new Vector2Int[intent.nodes.Length];
                for (int node = 0; node < Phase1MainNodeCount; node++)
                {
                    transformed[node] = TransformCoarseCell(mainCoarse[node], quarterTurns, mirror);
                }

                for (int branch = 0; branch < Phase1BranchNodeCount; branch++)
                {
                    transformed[Phase1MainNodeCount + branch] = TransformCoarseCell(
                        branchCoarse[branch],
                        quarterTurns,
                        mirror);
                }

                int minX = int.MaxValue;
                int minY = int.MaxValue;
                int maxX = int.MinValue;
                int maxY = int.MinValue;
                foreach (Vector2Int cell in transformed)
                {
                    minX = Mathf.Min(minX, cell.x);
                    minY = Mathf.Min(minY, cell.y);
                    maxX = Mathf.Max(maxX, cell.x);
                    maxY = Mathf.Max(maxY, cell.y);
                }

                const int horizontalSpacing = 9;
                const int verticalSpacing = 9;
                int cellWidth = (maxX - minX) * horizontalSpacing + Phase1RoomEnvelopeRadius * 2 + 1;
                int cellDepth = (maxY - minY) * verticalSpacing + Phase1RoomEnvelopeRadius * 2 + 1;
                DungeonGenerationSettings settings = CurrentGenerationSettings.Validated();
                if (cellWidth > settings.mapWidthMaxCells || cellDepth > settings.mapDepthMaxCells)
                {
                    continue;
                }

                var centers = new Vector2Int[transformed.Length];
                for (int node = 0; node < transformed.Length; node++)
                {
                    centers[node] = new Vector2Int(
                        Phase1RoomEnvelopeRadius + 1 + (transformed[node].x - minX) * horizontalSpacing,
                        Phase1RoomEnvelopeRadius + 1 + (transformed[node].y - minY) * verticalSpacing);
                }

                nodeCenters = centers;
                return true;
            }

            rejectionReason = "all four route orientations exceeded the active map envelope";
            return false;
        }

        private static bool TryFindBoundedCoarsePath(
            Vector2Int start,
            Vector2Int goal,
            HashSet<Vector2Int> occupied,
            bool allowGoal,
            out List<Vector2Int> pathExcludingStart,
            out int expansionCount)
        {
            pathExcludingStart = new List<Vector2Int>();
            expansionCount = 0;
            var queue = new Queue<Vector2Int>();
            var parent = new Dictionary<Vector2Int, Vector2Int>();
            var visited = new HashSet<Vector2Int> { start };
            queue.Enqueue(start);
            Vector2Int[] neighborOrder =
            {
                Vector2Int.up,
                Vector2Int.left,
                Vector2Int.right,
                Vector2Int.down
            };

            while (queue.Count > 0 && expansionCount < Phase1BranchSearchExpansionLimit)
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
                    pathExcludingStart = reverse;
                    return true;
                }

                foreach (Vector2Int offset in neighborOrder)
                {
                    Vector2Int next = current + offset;
                    if (next.x < 0 || next.x > 3 || next.y < 0 || next.y > 2 ||
                        !visited.Add(next) ||
                        occupied.Contains(next) && !(allowGoal && next == goal))
                    {
                        continue;
                    }

                    parent[next] = current;
                    queue.Enqueue(next);
                }
            }

            return false;
        }

        private static Vector2Int TransformCoarseCell(Vector2Int cell, int quarterTurns, bool mirror)
        {
            Vector2Int transformed = mirror ? new Vector2Int(-cell.x, cell.y) : cell;
            for (int turn = 0; turn < quarterTurns; turn++)
            {
                transformed = new Vector2Int(-transformed.y, transformed.x);
            }

            return transformed;
        }

        private static bool TryInflateProcessionalRooms(
            int dungeonSeed,
            int layoutAttempt,
            RouteIntent intent,
            IReadOnlyList<Vector2Int> nodeCenters,
            IReadOnlyList<RectInt> envelopes,
            out List<RoomFootprint> rooms,
            out string rejectionReason)
        {
            rooms = new List<RoomFootprint>(intent.nodes.Length);
            rejectionReason = string.Empty;
            phase1LastRoomInflationAttempts = 0;
            for (int nodeIndex = 0; nodeIndex < intent.nodes.Length; nodeIndex++)
            {
                RouteNodeIntent node = intent.nodes[nodeIndex];
                bool placed = false;
                for (int attempt = 0; attempt < Phase1RoomInflationAttemptLimit; attempt++)
                {
                    phase1LastRoomInflationAttempts++;
                    System.Random roomRandom = Phase1Random(
                        dungeonSeed,
                        layoutAttempt,
                        node.id,
                        $"room-shape-{attempt}");
                    List<RectInt> parts = BuildProcessionalRoomParts(
                        node,
                        nodeIndex,
                        nodeCenters[nodeIndex],
                        nodeCenters,
                        roomRandom,
                        allowWing:
                            attempt < Phase1RoomInflationAttemptLimit - 1 &&
                            nodeIndex != intent.vista.sourceNode &&
                            nodeIndex != intent.vista.targetNode);
                    var candidate = new RoomFootprint(parts);
                    bool insideEnvelope = true;
                    foreach (Vector2Int cell in candidate.cells)
                    {
                        if (!envelopes[nodeIndex].Contains(cell))
                        {
                            insideEnvelope = false;
                            break;
                        }
                    }

                    if (!insideEnvelope)
                    {
                        continue;
                    }

                    bool overlaps = false;
                    foreach (RoomFootprint existing in rooms)
                    {
                        if (candidate.Overlaps(existing))
                        {
                            overlaps = true;
                            break;
                        }
                    }

                    if (overlaps)
                    {
                        continue;
                    }

                    rooms.Add(candidate);
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    rejectionReason = $"room node '{node.id}' exhausted {Phase1RoomInflationAttemptLimit} inflation alternatives";
                    return false;
                }
            }

            return true;
        }

        private static List<RectInt> BuildProcessionalRoomParts(
            RouteNodeIntent node,
            int nodeIndex,
            Vector2Int center,
            IReadOnlyList<Vector2Int> nodeCenters,
            System.Random random,
            bool allowWing)
        {
            int width;
            int depth;
            switch (node.role)
            {
                case "arrival":
                case "culmination":
                    width = 5;
                    depth = 7;
                    break;
                case "grand-room":
                case "landmark":
                case "processional-hall":
                    width = 5;
                    depth = 5 + random.Next(2);
                    break;
                case "connector":
                    width = 4 + random.Next(2);
                    depth = 5;
                    break;
                default:
                    width = 5;
                    depth = 5 + random.Next(2);
                    break;
            }

            bool hasPlannedOverlookAppendage =
                nodeIndex == 1 ||
                nodeIndex == 6 ||
                nodeIndex == 7 ||
                nodeIndex == 9 ||
                nodeIndex == 10;
            if (hasPlannedOverlookAppendage)
            {
                width = 4;
                depth = 5;
                allowWing = false;
            }

            RectInt dominant = CenteredRect(center, width, depth);
            var parts = new List<RectInt> { dominant };
            AddPlannedOverlookAppendages(nodeIndex, center, nodeCenters, dominant, parts);
            if (!allowWing ||
                node.role == "connector" ||
                node.role == "arrival" ||
                node.role == "culmination" ||
                random.NextDouble() >= 0.4)
            {
                return parts;
            }

            int side = random.Next(4);
            switch (side)
            {
                case 0:
                    parts.Add(new RectInt(center.x - 1, dominant.yMax, 2, 2));
                    break;
                case 1:
                    parts.Add(new RectInt(dominant.xMax, center.y - 1, 2, 2));
                    break;
                case 2:
                    parts.Add(new RectInt(center.x - 1, dominant.yMin - 2, 2, 2));
                    break;
                default:
                    parts.Add(new RectInt(dominant.xMin - 2, center.y - 1, 2, 2));
                    break;
            }

            return parts;
        }

        // Three non-traversal neighbor pairs receive narrow, facing room
        // appendages. They create deliberate adjacent gallery/cliff edges for
        // the unchanged elevation planner without turning those vistas into
        // traversal edges or consuming the reserved source-to-landmark void.
        private static void AddPlannedOverlookAppendages(
            int nodeIndex,
            Vector2Int center,
            IReadOnlyList<Vector2Int> nodeCenters,
            RectInt dominant,
            List<RectInt> parts)
        {
            (int first, int second)[] pairs =
            {
                (1, 10),
                (6, 9),
                (7, 10)
            };
            foreach ((int first, int second) pair in pairs)
            {
                int other = nodeIndex == pair.first
                    ? pair.second
                    : nodeIndex == pair.second
                        ? pair.first
                        : -1;
                if (other < 0)
                {
                    continue;
                }

                Vector2Int delta = nodeCenters[other] - center;
                Vector2Int direction = new Vector2Int(Math.Sign(delta.x), Math.Sign(delta.y));
                if (direction.x != 0)
                {
                    int xMin = direction.x > 0
                        ? dominant.xMax
                        : center.x - Phase1RoomEnvelopeRadius;
                    int xMax = direction.x > 0
                        ? center.x + Phase1RoomEnvelopeRadius + 1
                        : dominant.xMin;
                    parts.Add(new RectInt(xMin, center.y - 1, xMax - xMin, 3));
                }
                else
                {
                    int yMin = direction.y > 0
                        ? dominant.yMax
                        : center.y - Phase1RoomEnvelopeRadius;
                    int yMax = direction.y > 0
                        ? center.y + Phase1RoomEnvelopeRadius + 1
                        : dominant.yMin;
                    parts.Add(new RectInt(center.x - 1, yMin, 3, yMax - yMin));
                }
            }
        }

        private static RectInt CenteredRect(Vector2Int center, int width, int height)
        {
            return new RectInt(
                center.x - width / 2,
                center.y - height / 2,
                width,
                height);
        }

        private static bool TryReserveProcessionalVista(
            RouteIntent intent,
            IReadOnlyList<RoomFootprint> rooms,
            IReadOnlyList<Vector2Int> nodeCenters,
            out HashSet<Vector2Int> reservedCells,
            out Vector2Int sourceFacing,
            out Vector2Int targetFacing,
            out string rejectionReason)
        {
            reservedCells = new HashSet<Vector2Int>();
            sourceFacing = Vector2Int.zero;
            targetFacing = Vector2Int.zero;
            rejectionReason = string.Empty;
            Vector2Int sourceCenter = nodeCenters[intent.vista.sourceNode];
            Vector2Int targetCenter = nodeCenters[intent.vista.targetNode];
            Vector2Int delta = targetCenter - sourceCenter;
            if (delta.x != 0 && delta.y != 0 || delta == Vector2Int.zero)
            {
                rejectionReason = $"vista endpoints were not cardinally aligned ({sourceCenter}->{targetCenter})";
                return false;
            }

            sourceFacing = new Vector2Int(Math.Sign(delta.x), Math.Sign(delta.y));
            targetFacing = -sourceFacing;
            int transverse = sourceFacing.x != 0 ? sourceCenter.y : sourceCenter.x;
            RoomFootprint sourceRoom = rooms[intent.vista.sourceNode];
            RoomFootprint targetRoom = rooms[intent.vista.targetNode];
            if (!sourceRoom.TryGetEdgeCellTowards(sourceFacing, transverse, out Vector2Int sourceEdge) ||
                !targetRoom.TryGetEdgeCellTowards(targetFacing, transverse, out Vector2Int targetEdge))
            {
                rejectionReason = "vista endpoints did not expose aligned room boundary cells";
                return false;
            }

            Vector2Int cursor = sourceEdge + sourceFacing;
            while (cursor != targetEdge)
            {
                if (sourceRoom.Contains(cursor) || targetRoom.Contains(cursor))
                {
                    rejectionReason = $"vista reservation re-entered an endpoint room at {cursor}";
                    return false;
                }

                reservedCells.Add(cursor);
                cursor += sourceFacing;
                if (reservedCells.Count > 64)
                {
                    rejectionReason = "vista reservation exceeded its bounded cardinal search";
                    return false;
                }
            }

            if (reservedCells.Count < intent.vista.minimumReservedVoidCells)
            {
                rejectionReason =
                    $"vista reserved {reservedCells.Count} void cells; required {intent.vista.minimumReservedVoidCells}";
                return false;
            }

            foreach (RoomFootprint room in rooms)
            {
                foreach (Vector2Int cell in reservedCells)
                {
                    if (room.Contains(cell))
                    {
                        rejectionReason = $"vista reservation crossed room geometry at {cell}";
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryConnectProcessionalRooms(
            RouteIntent intent,
            IReadOnlyList<RoomFootprint> rooms,
            HashSet<Vector2Int> reservedVistaCells,
            out HashSet<Vector2Int> floorCells,
            out List<RoomConnection> connections,
            out string rejectionReason)
        {
            floorCells = new HashSet<Vector2Int>();
            connections = new List<RoomConnection>(intent.traversalEdges.Length);
            rejectionReason = string.Empty;
            foreach (RoomFootprint room in rooms)
            {
                floorCells.UnionWith(room.cells);
            }

            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                RoomFootprint fromRoom = rooms[edge.fromNode];
                RoomFootprint toRoom = rooms[edge.toNode];
                Vector2Int delta = toRoom.Center - fromRoom.Center;
                if (delta.x != 0 && delta.y != 0 || delta == Vector2Int.zero)
                {
                    rejectionReason = $"edge '{edge.id}' endpoints were not cardinally aligned";
                    return false;
                }

                List<Vector2Int> path = BuildStraightCardinalPath(fromRoom.Center, toRoom.Center);
                if (!ValidatePathCardinality(path, out string pathError) ||
                    PathCrossesThirdRoom(path, rooms, edge.fromNode, edge.toNode) ||
                    PathTouchesExistingFloorOutsideEndpointRooms(path, floorCells, fromRoom, toRoom))
                {
                    rejectionReason = $"edge '{edge.id}' could not reserve its corridor: {pathError}";
                    return false;
                }

                foreach (Vector2Int cell in path)
                {
                    if (reservedVistaCells.Contains(cell) &&
                        !fromRoom.Contains(cell) &&
                        !toRoom.Contains(cell))
                    {
                        rejectionReason = $"edge '{edge.id}' entered vista-reserved void at {cell}";
                        return false;
                    }
                }

                AddPathCells(floorCells, path);
                connections.Add(new RoomConnection(edge.fromNode, edge.toNode, path));
            }

            foreach (Vector2Int cell in reservedVistaCells)
            {
                if (floorCells.Contains(cell))
                {
                    rejectionReason = $"compiled floor occupied vista-reserved void at {cell}";
                    return false;
                }
            }

            return true;
        }

        private static List<Vector2Int> BuildStraightCardinalPath(Vector2Int start, Vector2Int end)
        {
            var path = new List<Vector2Int>();
            if (start.x == end.x)
            {
                AddVerticalPath(path, start.y, end.y, start.x);
            }
            else
            {
                AddHorizontalPath(path, start.x, end.x, start.y);
            }

            return path;
        }

        private static List<Vector2Int> SortedCells(IEnumerable<Vector2Int> cells)
        {
            var result = new List<Vector2Int>(cells);
            result.Sort(CompareCells);
            return result;
        }

        private static bool RejectPhase1Route(
            string code,
            string detail,
            out string rejectionReason)
        {
            phase1LastFailureCode = code;
            rejectionReason = $"[{code}] {detail}";
            return false;
        }

        private static System.Random Phase1Random(
            int dungeonSeed,
            int layoutAttempt,
            string stableId,
            string purpose)
        {
            unchecked
            {
                uint hash = 2166136261u;
                MixPhase1Hash(ref hash, dungeonSeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                MixPhase1Hash(ref hash, Phase1PlannerVersion);
                MixPhase1Hash(ref hash, layoutAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                MixPhase1Hash(ref hash, stableId ?? string.Empty);
                MixPhase1Hash(ref hash, purpose ?? string.Empty);
                return new System.Random((int)hash);
            }
        }

        private static void MixPhase1Hash(ref uint hash, string value)
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
    }
}

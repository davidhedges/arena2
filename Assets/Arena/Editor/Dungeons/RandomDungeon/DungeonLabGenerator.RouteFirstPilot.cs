using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // The semantic graph exists before any room coordinates, then this solver
    // compiles it directly into the existing DungeonLayout.
    internal sealed partial class DungeonLabGenerator
    {
        private const string RoutePlannerVersion = "route-topologies-v10";
        private const string RouteRhythmPolicyVersion = "route-rhythm-v1";
        private const string NamedVistaPromontoryPolicyVersion = "named-vista-promontory-v1";
        internal static string ActiveRecipePlannerVersion => RoutePlannerVersion;
        // Preserve the proven route embedding stream. Phase 5 changes only the
        // active recipe contract/ports and uses named per-recipe streams.
        private const string RouteSpatialRandomVersion = "processional-spine-v1";
        private const string ProcessionalPatternId = "processional-spine";
        private const string AtriumRingPatternId = "atrium-ring";
        private const string TwinWingPatternId = "twin-wing-keep";
        private const string RouteIntentInvalidFailureCode = "ROUTE_INTENT_INVALID";
        private const string RecipeSelectionFailureCode = "RECIPE_SELECTION";
        private const int LayoutAttemptLimit = 2;
        // The "always 13 rooms" lock. Step 2 of the topology cutover turns this
        // into a range; until then every topology file must declare 13 nodes.
        private const int RouteNodeCount = 13;
        private const int RoomInflationAttemptLimit = 6;
        private const int MaxMainRouteRoleOccurrences = 2;
        private const int MinimumMainRouteNodesBetweenRecipeSlots = 2;
        private const int MaximumNamedVistaPromontoryCells = 4;
        private const int SharedReturnRecipeNode = 12;
        private const string CompressionRecipeSlotId = "required-compression";
        private const string LandmarkRecipeSlotId = "required-landmark";
        private const string ReturnRecipeSlotId = "required-return";
        // Step 1 keeps the historical residue mapping verbatim so the port cannot
        // move output. Step 2 replaces it with a weighted draw over the registry.
        private static readonly string[] SeedResidueTopologyIds =
        {
            ProcessionalPatternId,
            AtriumRingPatternId,
            ProcessionalPatternId,
            TwinWingPatternId
        };

        // Ephemeral diagnostic evidence for the most recent attempt.
        // It is never consumed by generation or carried into DungeonLayout.
        private static RouteIntent lastRouteIntent;
        private static Vector2Int[] lastNodeCenters = Array.Empty<Vector2Int>();
        private static Vector2Int[] lastVistaCells = Array.Empty<Vector2Int>();
        private static Vector2Int lastVistaSourceFacing;
        private static Vector2Int lastVistaTargetFacing;
        private static int lastLayoutAttempt;
        private static int lastMainEmbeddingAttempts;
        private static int lastBranchSearchExpansions;
        private static int lastRoomInflationAttempts;
        private static string lastRouteFailureCode = string.Empty;

        private sealed class RouteIntent
        {
            public readonly int seed;
            public readonly string plannerVersion;
            public readonly string patternId;
            public readonly DungeonRouteTopology topology;
            public readonly RouteNodeIntent[] nodes;
            public readonly RouteTraversalIntent[] traversalEdges;
            public readonly RouteVistaIntent vista;
            public readonly RouteElevationPolicy elevationPolicy;
            public RecipeSlotIntent[] recipeSlots;
            public string catalogDigest;
            public readonly int bottomNode;
            public readonly int topNode;
            public readonly RouteOverlookIntent[] plannedOverlooks;
            public readonly bool allowGenericRoomWings;

            // Derived from the graph, never authored. Two topology files cannot
            // disagree with themselves about their own shape.
            public readonly List<int>[] adjacency;
            public readonly int[] junctionNodes;
            public readonly int cycleRank;
            public readonly int cycleCoreNodeCount;

            public RouteIntent(
                int seed,
                string plannerVersion,
                DungeonRouteTopology topology,
                RouteNodeIntent[] nodes,
                RouteTraversalIntent[] traversalEdges,
                RouteVistaIntent vista,
                RouteElevationPolicy elevationPolicy,
                RecipeSlotIntent[] recipeSlots,
                string catalogDigest,
                int bottomNode,
                int topNode,
                RouteOverlookIntent[] plannedOverlooks,
                bool allowGenericRoomWings)
            {
                this.seed = seed;
                this.plannerVersion = plannerVersion;
                this.topology = topology;
                patternId = topology.id;
                this.nodes = nodes;
                this.traversalEdges = traversalEdges;
                this.vista = vista;
                this.elevationPolicy = elevationPolicy;
                this.recipeSlots = recipeSlots ?? Array.Empty<RecipeSlotIntent>();
                this.catalogDigest = catalogDigest ?? string.Empty;
                this.bottomNode = bottomNode;
                this.topNode = topNode;
                this.plannedOverlooks = plannedOverlooks ?? Array.Empty<RouteOverlookIntent>();
                this.allowGenericRoomWings = allowGenericRoomWings;

                adjacency = new List<int>[nodes.Length];
                for (int node = 0; node < adjacency.Length; node++)
                {
                    adjacency[node] = new List<int>();
                }

                foreach (RouteTraversalIntent edge in traversalEdges)
                {
                    if (edge.fromNode < 0 || edge.fromNode >= nodes.Length ||
                        edge.toNode < 0 || edge.toNode >= nodes.Length)
                    {
                        continue;
                    }

                    adjacency[edge.fromNode].Add(edge.toNode);
                    adjacency[edge.toNode].Add(edge.fromNode);
                }

                var junctions = new List<int>();
                for (int node = 0; node < adjacency.Length; node++)
                {
                    if (adjacency[node].Count >= 3)
                    {
                        junctions.Add(node);
                    }
                }

                junctionNodes = junctions.ToArray();
                cycleRank = traversalEdges.Length - (nodes.Length - 1);
                cycleCoreNodeCount = CountCycleCoreNodes(adjacency);
            }

            // The two ends of the branch structure, reported in node order. A
            // general graph has no single attach/rejoin pair, so these are the
            // first and last of every node with degree >= 3.
            public int branchAttachNode =>
                junctionNodes.Length > 0 ? junctionNodes[0] : bottomNode;

            public int branchRejoinNode =>
                junctionNodes.Length > 0 ? junctionNodes[junctionNodes.Length - 1] : topNode;

            public void ResolveRecipeSlots(
                RecipeSlotIntent[] resolvedRecipeSlots,
                string activeCatalogDigest)
            {
                if (recipeSlots.Length != 0 || !string.IsNullOrEmpty(catalogDigest))
                {
                    throw new InvalidOperationException("route intent recipe slots were already resolved");
                }

                recipeSlots = resolvedRecipeSlots ?? Array.Empty<RecipeSlotIntent>();
                catalogDigest = activeCatalogDigest ?? string.Empty;
            }
        }

        private enum RouteTransitionKind
        {
            LevelCorridor,
            Stair,
            Bridge,
            Stairwell
        }

        private enum RouteElevationPolicy
        {
            AscendingSpine
        }

        private readonly struct RouteNodeIntent
        {
            public readonly string id;
            public readonly string role;
            public readonly string beat;
            public readonly int mainRouteOrder;
            public readonly int branchOrder;
            public readonly int relativeElevationLevels;
            public readonly string recipeSlotId;

            public RouteNodeIntent(
                string id,
                string role,
                string beat,
                int mainRouteOrder,
                int branchOrder,
                int relativeElevationLevels,
                string recipeSlotId = "")
            {
                this.id = id;
                this.role = role;
                this.beat = beat;
                this.mainRouteOrder = mainRouteOrder;
                this.branchOrder = branchOrder;
                this.relativeElevationLevels = relativeElevationLevels;
                this.recipeSlotId = recipeSlotId ?? string.Empty;
            }

            public bool IsOnMainRoute => mainRouteOrder >= 0;

            public bool HasRecipeSlot => !string.IsNullOrEmpty(recipeSlotId);
        }

        private readonly struct RouteTraversalIntent
        {
            public readonly string id;
            public readonly int fromNode;
            public readonly int toNode;
            public readonly int laneCount;
            public readonly int requiredRiseLevels;
            public readonly RouteTransitionKind transitionKind;

            public RouteTraversalIntent(
                string id,
                int fromNode,
                int toNode,
                int requiredRiseLevels,
                RouteTransitionKind transitionKind)
            {
                this.id = id;
                this.fromNode = fromNode;
                this.toNode = toNode;
                laneCount = 1;
                this.requiredRiseLevels = requiredRiseLevels;
                this.transitionKind = transitionKind;
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

        private readonly struct RouteOverlookIntent
        {
            public readonly int firstNode;
            public readonly int secondNode;

            public RouteOverlookIntent(int firstNode, int secondNode)
            {
                this.firstNode = firstNode;
                this.secondNode = secondNode;
            }
        }

        // The narrow Phase 3/4 companion value. It carries the already-produced route
        // requirements and exact 2D vista reservation into the existing tier
        // planner, then dies with the generation attempt. It is not a canonical
        // plan, renderer input, adapter, or serializable DTO family.
        private sealed class RouteTierRequirements
        {
            public readonly RouteIntent intent;
            public readonly HashSet<Vector2Int> reservedVistaCells;
            public readonly Vector2Int vistaSourceCell;
            public readonly Vector2Int vistaTargetCell;
            public readonly Vector2Int vistaSourceFacing;
            public readonly Vector2Int vistaTargetFacing;
            public readonly Vector2Int[] namedPromontoryCells;
            public readonly RecipePlacement[] recipes;

            public RouteTierRequirements(
                RouteIntent intent,
                IEnumerable<Vector2Int> reservedVistaCells,
                Vector2Int vistaSourceCell,
                Vector2Int vistaTargetCell,
                Vector2Int vistaSourceFacing,
                Vector2Int vistaTargetFacing,
                Vector2Int[] namedPromontoryCells,
                RecipePlacement[] recipes)
            {
                this.intent = intent;
                this.reservedVistaCells = new HashSet<Vector2Int>(reservedVistaCells);
                this.vistaSourceCell = vistaSourceCell;
                this.vistaTargetCell = vistaTargetCell;
                this.vistaSourceFacing = vistaSourceFacing;
                this.vistaTargetFacing = vistaTargetFacing;
                this.namedPromontoryCells = namedPromontoryCells ?? Array.Empty<Vector2Int>();
                this.recipes = recipes ?? Array.Empty<RecipePlacement>();
            }

            public bool TryGetTransition(int firstRoom, int secondRoom, out RouteTraversalIntent requirement)
            {
                foreach (RouteTraversalIntent edge in intent.traversalEdges)
                {
                    if (edge.fromNode == firstRoom && edge.toNode == secondRoom ||
                        edge.fromNode == secondRoom && edge.toNode == firstRoom)
                    {
                        requirement = edge;
                        return true;
                    }
                }

                requirement = default;
                return false;
            }
        }

        private static void ResetRouteDiagnostics()
        {
            lastRouteIntent = null;
            lastNodeCenters = Array.Empty<Vector2Int>();
            lastVistaCells = Array.Empty<Vector2Int>();
            lastVistaSourceFacing = Vector2Int.zero;
            lastVistaTargetFacing = Vector2Int.zero;
            lastLayoutAttempt = 0;
            lastMainEmbeddingAttempts = 0;
            lastBranchSearchExpansions = 0;
            lastRoomInflationAttempts = 0;
            lastRouteFailureCode = string.Empty;
        }

        private static bool TryBuildRouteFirstDungeonLayout(
            int dungeonSeed,
            int layoutAttempt,
            out DungeonLayout layout,
            out RouteTierRequirements routeRequirements,
            out string rejectionReason)
        {
            layout = default;
            routeRequirements = null;
            rejectionReason = string.Empty;
            ResetRouteDiagnostics();
            lastLayoutAttempt = layoutAttempt;

            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog recipeCatalog,
                    out rejectionReason))
            {
                return RejectRoute("RECIPE_CATALOG", rejectionReason, out rejectionReason);
            }

            RouteIntent intent = BuildSelectedRouteIntent(
                dungeonSeed,
                Array.Empty<RecipeSlotIntent>(),
                string.Empty);
            if (!TryResolveRequiredRecipeSlots(
                    recipeCatalog,
                    intent,
                    out RecipeSlotIntent[] recipeSlots,
                    out rejectionReason))
            {
                return RejectRoute(
                    RecipeSelectionFailureCode,
                    rejectionReason,
                    out rejectionReason);
            }

            intent.ResolveRecipeSlots(recipeSlots, recipeCatalog.digest);
            lastRouteIntent = intent;
            if (!TryValidateRouteIntent(intent, out rejectionReason))
            {
                return RejectRoute(RouteIntentInvalidFailureCode, rejectionReason, out rejectionReason);
            }

            DungeonPatternSpatialSettings spatial = ResolveTopologySpatialSettings(intent.topology);

            if (!TryEmbedRoute(
                    dungeonSeed,
                    layoutAttempt,
                    intent,
                    spatial,
                    out Vector2Int[] nodeCenters,
                    out string embeddingFailureCode,
                    out rejectionReason))
            {
                return RejectRoute(embeddingFailureCode, rejectionReason, out rejectionReason);
            }

            lastNodeCenters = nodeCenters;
            DungeonGenerationSettings settings = CurrentGenerationSettings.Validated();
            var roomEnvelopes = new RectInt[intent.nodes.Length];
            for (int node = 0; node < nodeCenters.Length; node++)
            {
                roomEnvelopes[node] = RoomEnvelope(nodeCenters[node], spatial);
            }

            if (!TryInflateProcessionalRooms(
                    dungeonSeed,
                    layoutAttempt,
                    intent,
                    spatial,
                    nodeCenters,
                    roomEnvelopes,
                    out List<RoomFootprint> rooms,
                    out rejectionReason))
            {
                return RejectRoute("ROUTE_ROOM_INFLATION_EXHAUSTED", rejectionReason, out rejectionReason);
            }

            if (!TryReserveProcessionalVista(
                    intent,
                    rooms,
                    nodeCenters,
                    out HashSet<Vector2Int> reservedVistaCells,
                    out Vector2Int sourceVistaCell,
                    out Vector2Int targetVistaCell,
                    out Vector2Int sourceFacing,
                    out Vector2Int targetFacing,
                    out rejectionReason))
            {
                return RejectRoute("ROUTE_VISTA_RESERVATION_BLOCKED", rejectionReason, out rejectionReason);
            }

            var protectedVistaCells = new HashSet<Vector2Int>(reservedVistaCells);
            if (!TryPlanNamedVistaPromontory(
                    intent,
                    reservedVistaCells,
                    sourceVistaCell,
                    targetVistaCell,
                    sourceFacing,
                    targetFacing,
                    out Vector2Int[] namedPromontoryCells,
                    out rejectionReason))
            {
                return RejectRoute("ROUTE_PROMONTORY_RESERVATION_INVALID", rejectionReason, out rejectionReason);
            }

            lastVistaCells = SortedCells(reservedVistaCells).ToArray();
            lastVistaSourceFacing = sourceFacing;
            lastVistaTargetFacing = targetFacing;

            if (!TryPlaceRouteRecipes(
                    dungeonSeed,
                    layoutAttempt,
                    intent,
                    rooms,
                    nodeCenters,
                    sourceFacing,
                    targetFacing,
                    out RecipePlacement[] recipePlacements,
                    out rejectionReason))
            {
                return RejectRoute("RECIPE_PLACEMENT", rejectionReason, out rejectionReason);
            }

            if (!TryConnectProcessionalRooms(
                    intent,
                    rooms,
                    nodeCenters,
                    protectedVistaCells,
                    recipePlacements,
                    out HashSet<Vector2Int> floorCells,
                    out List<RoomConnection> connections,
                    out rejectionReason))
            {
                return RejectRoute("ROUTE_CORRIDOR_EMBEDDING_EXHAUSTED", rejectionReason, out rejectionReason);
            }

            if (!IsConnected(floorCells))
            {
                return RejectRoute(
                    "ROUTE_FLOOR_DISCONNECTED",
                    "compiled route-first floor mask was disconnected",
                    out rejectionReason);
            }

            if (rooms.Count < settings.denseFloorplanMinRooms ||
                CalculateFloorFillPercent(floorCells) < settings.denseFloorplanMinFillPercent)
            {
                return RejectRoute(
                    "ROUTE_DENSITY_PRECONDITION",
                    $"compiled {rooms.Count} rooms at {CalculateFloorFillPercent(floorCells) * 100f:0.#}% fill; " +
                    $"profile requires {settings.denseFloorplanMinRooms} rooms and {settings.denseFloorplanMinFillPercent * 100f:0.#}% fill",
                    out rejectionReason);
            }

            Dictionary<int, HashSet<Vector2Int>> thresholds = BuildRoomThresholdCells(rooms, connections);
            System.Random zoneRandom = DerivedRandom(
                dungeonSeed,
                layoutAttempt,
                "layout",
                "room-zones");
            List<RoomZonePlan> roomZones = ChooseRoomZoneSplits(
                rooms,
                thresholds,
                zoneRandom,
                settings);
            // A +1 intraroom accent cannot sit above the declared 24u
            // culmination. All other route rooms retain the existing zone policy.
            roomZones.RemoveAll(zone =>
                intent.nodes[zone.roomIndex].relativeElevationLevels >= MaxGeneratedLevel ||
                zone.roomIndex == intent.vista.sourceNode ||
                zone.roomIndex == intent.vista.targetNode ||
                TryGetRecipeSlot(intent.recipeSlots, zone.roomIndex, out _));
            StairConnectorSettings connectorTable = LoadAuthoredStairConnectorTableForGeneration();
            int connectorCandidateCount = connectorTable != null
                ? CountConfiguredStairConnectorPrefabs(connectorTable)
                : 0;
            layout = new DungeonLayout(
                floorCells,
                rooms,
                connections,
                roomZones,
                connectorCandidateCount);
            routeRequirements = new RouteTierRequirements(
                intent,
                reservedVistaCells,
                sourceVistaCell,
                targetVistaCell,
                sourceFacing,
                targetFacing,
                namedPromontoryCells,
                recipePlacements);
            lastRouteFailureCode = string.Empty;
            return true;
        }

        private static bool TryPlanNamedVistaPromontory(
            RouteIntent intent,
            HashSet<Vector2Int> reservedVistaCells,
            Vector2Int sourceCell,
            Vector2Int targetCell,
            Vector2Int sourceFacing,
            Vector2Int targetFacing,
            out Vector2Int[] promontoryCells,
            out string rejectionReason)
        {
            promontoryCells = Array.Empty<Vector2Int>();
            rejectionReason = string.Empty;
            if (intent == null ||
                string.IsNullOrEmpty(intent.vista.id) ||
                intent.vista.targetNode < 0 ||
                intent.vista.targetNode >= intent.nodes.Length ||
                string.IsNullOrEmpty(intent.nodes[intent.vista.targetNode].id))
            {
                rejectionReason = $"{NamedVistaPromontoryPolicyVersion} had no named vista target";
                return false;
            }

            bool cardinalFacing = Mathf.Abs(sourceFacing.x) + Mathf.Abs(sourceFacing.y) == 1;
            if (!cardinalFacing || sourceFacing != -targetFacing)
            {
                rejectionReason = $"{NamedVistaPromontoryPolicyVersion} required cardinal opposed vista facing";
                return false;
            }

            int sourceToTargetDistance = Mathf.Abs(targetCell.x - sourceCell.x) +
                Mathf.Abs(targetCell.y - sourceCell.y);
            if (targetCell != sourceCell + sourceFacing * sourceToTargetDistance ||
                sourceToTargetDistance != reservedVistaCells.Count + 1)
            {
                rejectionReason = $"{NamedVistaPromontoryPolicyVersion} required one complete cardinal source-to-target reservation";
                return false;
            }

            for (int step = 1; step < sourceToTargetDistance; step++)
            {
                if (!reservedVistaCells.Contains(sourceCell + sourceFacing * step))
                {
                    rejectionReason = $"{NamedVistaPromontoryPolicyVersion} found a gap in the reserved vista line";
                    return false;
                }
            }

            int availableCells = reservedVistaCells.Count - intent.vista.minimumReservedVoidCells;
            int promontoryLength = Mathf.Min(MaximumNamedVistaPromontoryCells, Mathf.Max(0, availableCells));
            if (promontoryLength == 0)
            {
                return true;
            }

            var planned = new Vector2Int[promontoryLength];
            for (int index = 0; index < planned.Length; index++)
            {
                Vector2Int cell = sourceCell + sourceFacing * (index + 1);
                if (!reservedVistaCells.Contains(cell))
                {
                    rejectionReason = $"{NamedVistaPromontoryPolicyVersion} could not atomically reserve cell {cell}";
                    return false;
                }

                planned[index] = cell;
            }

            reservedVistaCells.ExceptWith(planned);

            if (reservedVistaCells.Count < intent.vista.minimumReservedVoidCells)
            {
                rejectionReason = $"{NamedVistaPromontoryPolicyVersion} left fewer than {intent.vista.minimumReservedVoidCells} void cells";
                return false;
            }

            promontoryCells = planned;
            return true;
        }

        // One generic builder: a topology file is the graph, so there is no
        // per-pattern factory to keep in sync with a per-pattern embedder.
        private static RouteIntent BuildTopologyRouteIntent(
            DungeonRouteTopology topology,
            int dungeonSeed,
            RecipeSlotIntent[] recipeSlots,
            string catalogDigest)
        {
            var nodes = new RouteNodeIntent[topology.nodes.Length];
            for (int node = 0; node < nodes.Length; node++)
            {
                RouteTopologyNode declared = topology.nodes[node];
                nodes[node] = new RouteNodeIntent(
                    declared.id,
                    declared.role,
                    declared.beat,
                    declared.mainRouteOrder,
                    declared.branchOrder,
                    declared.level,
                    declared.recipeSlotId);
            }

            var edges = new RouteTraversalIntent[topology.edges.Length];
            for (int edge = 0; edge < edges.Length; edge++)
            {
                RouteTopologyEdge declared = topology.edges[edge];
                edges[edge] = new RouteTraversalIntent(
                    declared.id,
                    declared.fromNode,
                    declared.toNode,
                    // Rise is derived, so an authored graph cannot disagree with
                    // its own elevations.
                    nodes[declared.toNode].relativeElevationLevels -
                        nodes[declared.fromNode].relativeElevationLevels,
                    declared.transitionKind);
            }

            return new RouteIntent(
                dungeonSeed,
                topology.plannerVersion,
                topology,
                nodes,
                edges,
                new RouteVistaIntent(
                    topology.vistaId,
                    topology.vistaSourceNode,
                    topology.vistaTargetNode,
                    topology.vistaMinimumVoidCells),
                RouteElevationPolicy.AscendingSpine,
                recipeSlots,
                catalogDigest,
                topology.bottomNode,
                topology.topNode,
                BuildPlannedOverlooks(
                    topology,
                    nodes,
                    edges,
                    ResolveTopologySpatialSettings(topology).tierSeamAdjacency),
                topology.allowGenericRoomWings);
        }

        private static RouteOverlookIntent[] BuildPlannedOverlooks(
            DungeonRouteTopology topology,
            IReadOnlyList<RouteNodeIntent> nodes,
            IReadOnlyList<RouteTraversalIntent> traversalEdges,
            DungeonTierSeamAdjacencySettings requestedSettings)
        {
            DungeonTierSeamAdjacencySettings settings = requestedSettings.Validated();
            var selected = new List<RouteOverlookIntent>(settings.requestedCount);
            foreach (RouteOverlookIntent candidate in topology.overlooks)
            {
                if (selected.Count >= settings.requestedCount)
                {
                    break;
                }

                if (candidate.firstNode < 0 || candidate.firstNode >= nodes.Count ||
                    candidate.secondNode < 0 || candidate.secondNode >= nodes.Count ||
                    candidate.firstNode == candidate.secondNode)
                {
                    throw new InvalidOperationException(
                        $"[TIER_SEAM_ADJACENCY] topology '{topology.id}' declared an invalid candidate");
                }

                bool isTraversal = false;
                foreach (RouteTraversalIntent edge in traversalEdges)
                {
                    if ((edge.fromNode == candidate.firstNode && edge.toNode == candidate.secondNode) ||
                        (edge.fromNode == candidate.secondNode && edge.toNode == candidate.firstNode))
                    {
                        isTraversal = true;
                        break;
                    }
                }

                int rise = Mathf.Abs(
                    nodes[candidate.firstNode].relativeElevationLevels -
                    nodes[candidate.secondNode].relativeElevationLevels);
                if (!isTraversal &&
                    rise >= MajorRiseLevels &&
                    rise <= settings.maximumRiseLevels &&
                    rise % MajorRiseLevels == 0)
                {
                    selected.Add(candidate);
                }
            }

            if (selected.Count != settings.requestedCount)
            {
                throw new InvalidOperationException(
                    $"[TIER_SEAM_ADJACENCY] topology '{topology.id}' requested {settings.requestedCount} " +
                    $"eligible adjacencies but declared {selected.Count}");
            }

            return selected.ToArray();
        }

        private static string SelectRouteTopologyId(int dungeonSeed)
        {
            int residue = dungeonSeed % SeedResidueTopologyIds.Length;
            if (residue < 0)
            {
                residue += SeedResidueTopologyIds.Length;
            }

            return SeedResidueTopologyIds[residue];
        }

        private static RouteIntent BuildSelectedRouteIntent(
            int dungeonSeed,
            RecipeSlotIntent[] recipeSlots,
            string catalogDigest)
        {
            return BuildTopologyRouteIntent(
                RequireRouteTopology(SelectRouteTopologyId(dungeonSeed)),
                dungeonSeed,
                recipeSlots,
                catalogDigest);
        }

        private static bool TryValidateRouteIntent(RouteIntent intent, out string rejectionReason)
        {
            rejectionReason = string.Empty;
            if (intent == null || intent.nodes == null || intent.traversalEdges == null)
            {
                rejectionReason = "route intent or its graph collections were null";
                return false;
            }

            if (string.IsNullOrEmpty(intent.patternId) || string.IsNullOrEmpty(intent.plannerVersion))
            {
                rejectionReason = "route intent did not declare a pattern and planner version";
                return false;
            }

            if (intent.nodes.Length != RouteNodeCount)
            {
                rejectionReason = $"route intent had {intent.nodes.Length} nodes instead of {RouteNodeCount}";
                return false;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            int recipeSlotCount = 0;
            foreach (RouteNodeIntent node in intent.nodes)
            {
                if (string.IsNullOrEmpty(node.id) || !ids.Add(node.id))
                {
                    rejectionReason = $"route intent contained a missing or duplicate node id '{node.id}'";
                    return false;
                }

                if (node.HasRecipeSlot)
                {
                    recipeSlotCount++;
                }
            }

            if (intent.recipeSlots == null ||
                intent.recipeSlots.Length != 3 ||
                recipeSlotCount != 3 ||
                string.IsNullOrEmpty(intent.catalogDigest))
            {
                rejectionReason = "route intent did not declare exactly three active recipe slots and a catalog digest";
                return false;
            }

            if (!TryValidateRouteRhythm(intent.nodes, out rejectionReason))
            {
                return false;
            }

            var resolvedSlotIds = new HashSet<string>(StringComparer.Ordinal);
            var resolvedSlotNodes = new HashSet<int>();
            foreach (RecipeSlotIntent slot in intent.recipeSlots)
            {
                if (slot == null || slot.recipe == null ||
                    slot.slotNode < 0 || slot.slotNode >= intent.nodes.Length ||
                    string.IsNullOrEmpty(slot.slotId) ||
                    !resolvedSlotIds.Add(slot.slotId) ||
                    !resolvedSlotNodes.Add(slot.slotNode) ||
                    !string.Equals(intent.nodes[slot.slotNode].recipeSlotId, slot.slotId, StringComparison.Ordinal) ||
                    !string.Equals(slot.catalogDigest, intent.catalogDigest, StringComparison.Ordinal) ||
                    !string.Equals(
                        slot.selectionStreamIdentity,
                        RecipeSelectionStreamIdentity,
                        StringComparison.Ordinal) ||
                    Array.IndexOf(slot.compatibleCandidateIds, slot.recipe.recipeId) < 0 ||
                    Array.IndexOf(slot.recipe.eligibleRoles, intent.nodes[slot.slotNode].role) < 0 ||
                    Array.IndexOf(slot.recipe.eligibleBeats, intent.nodes[slot.slotNode].beat) < 0 ||
                    !TryValidateRecipeCandidate(
                        intent,
                        slot.slotNode,
                        slot.recipe,
                        slot.orientationBinding,
                        slot.portBindings,
                        out _))
                {
                    rejectionReason = "route intent contained an incompatible recipe slot binding";
                    return false;
                }
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

                if (edge.transitionKind == RouteTransitionKind.LevelCorridor &&
                        edge.requiredRiseLevels != 0 ||
                    edge.transitionKind != RouteTransitionKind.LevelCorridor &&
                    edge.requiredRiseLevels != MajorRiseLevels &&
                    edge.requiredRiseLevels != DoubleMajorRiseLevels)
                {
                    rejectionReason = $"route edge '{edge.id}' had an incompatible elevation/transition requirement";
                    return false;
                }
            }

            var visited = new HashSet<int> { intent.bottomNode };
            var queue = new Queue<int>();
            queue.Enqueue(intent.bottomNode);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int neighbor in intent.adjacency[current])
                {
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            // Cycle rank, cycle-core size and junction degrees are derived from
            // the graph, so there is nothing to compare them against. What is
            // still a real requirement: the graph is connected and it loops.
            if (visited.Count != intent.nodes.Length ||
                intent.cycleRank < 1 ||
                intent.junctionNodes.Length < 2)
            {
                rejectionReason =
                    $"route graph reached {visited.Count}/{intent.nodes.Length} nodes with cycle rank " +
                    $"{intent.cycleRank}, a {intent.cycleCoreNodeCount}-node cycle core and " +
                    $"{intent.junctionNodes.Length} junctions; a route needs one connected graph, " +
                    "at least one loop, and at least two branch points";
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

            if (intent.nodes[intent.bottomNode].relativeElevationLevels != 0 ||
                intent.nodes[intent.topNode].relativeElevationLevels != MaxGeneratedLevel ||
                intent.nodes[intent.vista.sourceNode].relativeElevationLevels -
                    intent.nodes[intent.vista.targetNode].relativeElevationLevels < MajorRiseLevels)
            {
                rejectionReason = "route elevation story did not span 0..24u or raise the vista source at least one major above its target";
                return false;
            }

            var requiredKinds = new HashSet<RouteTransitionKind>();
            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                requiredKinds.Add(edge.transitionKind);
            }

            if (!requiredKinds.Contains(RouteTransitionKind.Stair) ||
                !requiredKinds.Contains(RouteTransitionKind.Bridge) ||
                !requiredKinds.Contains(RouteTransitionKind.Stairwell))
            {
                rejectionReason = "route intent did not declare stair, bridge, and stairwell requirements";
                return false;
            }

            return true;
        }

        private static bool TryValidateRouteRhythm(
            IReadOnlyList<RouteNodeIntent> nodes,
            out string rejectionReason)
        {
            rejectionReason = string.Empty;
            if (nodes == null)
            {
                rejectionReason = $"{RouteRhythmPolicyVersion} received a null node sequence";
                return false;
            }

            var mainRoute = new List<RouteNodeIntent>();
            foreach (RouteNodeIntent node in nodes)
            {
                if (node.IsOnMainRoute)
                {
                    mainRoute.Add(node);
                }
            }

            mainRoute.Sort((first, second) => first.mainRouteOrder.CompareTo(second.mainRouteOrder));
            if (mainRoute.Count == 0)
            {
                rejectionReason = $"{RouteRhythmPolicyVersion} found no main-route nodes";
                return false;
            }

            var roleOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            int previousRecipeOrder = -1;
            for (int index = 0; index < mainRoute.Count; index++)
            {
                RouteNodeIntent node = mainRoute[index];
                if (node.mainRouteOrder != index)
                {
                    rejectionReason =
                        $"{RouteRhythmPolicyVersion} requires contiguous unique main-route orders; " +
                        $"position {index} declared order {node.mainRouteOrder}";
                    return false;
                }

                if (!string.IsNullOrEmpty(node.role))
                {
                    int count = roleOccurrences.TryGetValue(node.role, out int current)
                        ? current + 1
                        : 1;
                    roleOccurrences[node.role] = count;
                    if (count > MaxMainRouteRoleOccurrences)
                    {
                        rejectionReason =
                            $"{RouteRhythmPolicyVersion} permits at most {MaxMainRouteRoleOccurrences} " +
                            $"main-route nodes with role '{node.role}'";
                        return false;
                    }
                }

                if (index > 0)
                {
                    RouteNodeIntent previous = mainRoute[index - 1];
                    if (!string.IsNullOrEmpty(node.role) &&
                        string.Equals(previous.role, node.role, StringComparison.Ordinal))
                    {
                        rejectionReason =
                            $"{RouteRhythmPolicyVersion} forbids adjacent main-route role '{node.role}'";
                        return false;
                    }

                    if (!string.IsNullOrEmpty(node.beat) &&
                        string.Equals(previous.beat, node.beat, StringComparison.Ordinal))
                    {
                        rejectionReason =
                            $"{RouteRhythmPolicyVersion} forbids adjacent main-route beat '{node.beat}'";
                        return false;
                    }
                }

                if (node.HasRecipeSlot)
                {
                    if (previousRecipeOrder >= 0 &&
                        node.mainRouteOrder - previousRecipeOrder - 1 <
                            MinimumMainRouteNodesBetweenRecipeSlots)
                    {
                        rejectionReason =
                            $"{RouteRhythmPolicyVersion} requires at least " +
                            $"{MinimumMainRouteNodesBetweenRecipeSlots} intervening main-route nodes " +
                            "between recipe-bearing nodes";
                        return false;
                    }

                    previousRecipeOrder = node.mainRouteOrder;
                }
            }

            return true;
        }

        private static int CountCycleCoreNodes(IReadOnlyList<List<int>> adjacency)
        {
            var remainingDegrees = new int[adjacency.Count];
            var leaves = new Queue<int>();
            for (int node = 0; node < adjacency.Count; node++)
            {
                remainingDegrees[node] = adjacency[node].Count;
                if (remainingDegrees[node] <= 1)
                {
                    leaves.Enqueue(node);
                }
            }

            while (leaves.Count > 0)
            {
                int leaf = leaves.Dequeue();
                if (remainingDegrees[leaf] == 0)
                {
                    continue;
                }

                remainingDegrees[leaf] = 0;
                foreach (int neighbor in adjacency[leaf])
                {
                    if (remainingDegrees[neighbor] > 0 && --remainingDegrees[neighbor] == 1)
                    {
                        leaves.Enqueue(neighbor);
                    }
                }
            }

            int cycleNodes = 0;
            foreach (int degree in remainingDegrees)
            {
                if (degree > 0)
                {
                    cycleNodes++;
                }
            }

            return cycleNodes;
        }

        private static DungeonPatternSpatialSettings BaselinePatternSpatialSettings(
            int horizontalPitchCells,
            int verticalPitchCells)
        {
            return new DungeonPatternSpatialSettings(
                horizontalPitchCells,
                verticalPitchCells,
                roomEnvelopeRadiusCells: 4,
                neighborBiasStrengthCells: 0,
                new DungeonTierSeamAdjacencySettings(requestedCount: 0, maximumRiseLevels: 8),
                BaselineRoomSizeRangeForRole("arrival"),
                BaselineRoomSizeRangeForRole("processional-hall"),
                BaselineRoomSizeRangeForRole("connector")).Validated();
        }

        private static RectInt RoomEnvelope(
            Vector2Int center,
            DungeonPatternSpatialSettings spatial)
        {
            return new RectInt(
                center.x - spatial.roomEnvelopeRadiusCells,
                center.y - spatial.roomEnvelopeRadiusCells,
                spatial.roomEnvelopeRadiusCells * 2 + 1,
                spatial.roomEnvelopeRadiusCells * 2 + 1);
        }

        // One embedder for every topology. The map gives each node a lattice
        // cell; every node in one lattice lane shares a world offset, which is
        // what keeps every edge cardinally aligned by construction.
        private static bool TryEmbedRoute(
            int dungeonSeed,
            int layoutAttempt,
            RouteIntent intent,
            DungeonPatternSpatialSettings spatial,
            out Vector2Int[] nodeCenters,
            out string failureCode,
            out string rejectionReason)
        {
            DungeonRouteTopology topology = intent.topology;
            nodeCenters = Array.Empty<Vector2Int>();
            failureCode = topology.embeddingFailureCode;
            rejectionReason = string.Empty;
            lastBranchSearchExpansions = topology.legacyBranchSearchExpansions;
            int[] columnOffsets = ResolveLatticeLaneOffsets(
                topology.columnGapCells,
                spatial.horizontalPitchCells,
                topology.latticeColumnCount);
            int[] rowOffsets = ResolveLatticeLaneOffsets(
                topology.rowGapCells,
                spatial.verticalPitchCells,
                topology.latticeRowCount);
            var embedding = new Vector2Int[topology.nodes.Length];
            for (int node = 0; node < embedding.Length; node++)
            {
                Vector2Int lattice = topology.nodes[node].lattice;
                embedding[node] = new Vector2Int(columnOffsets[lattice.x], rowOffsets[lattice.y]);
            }

            return TryTransformCoarseEmbedding(
                dungeonSeed,
                layoutAttempt,
                topology.orientationStreamId,
                embedding,
                spatial,
                out nodeCenters,
                out rejectionReason);
        }

        private static bool TryTransformCoarseEmbedding(
            int dungeonSeed,
            int layoutAttempt,
            string stablePatternId,
            IReadOnlyList<Vector2Int> cellEmbedding,
            DungeonPatternSpatialSettings spatial,
            out Vector2Int[] nodeCenters,
            out string rejectionReason)
        {
            nodeCenters = Array.Empty<Vector2Int>();
            rejectionReason = string.Empty;
            System.Random placementRandom = DerivedRandom(
                dungeonSeed,
                layoutAttempt,
                stablePatternId,
                "orientation");
            int firstQuarterTurn = placementRandom.Next(4);
            bool mirror = placementRandom.Next(2) == 0;
            for (int orientationAttempt = 0; orientationAttempt < 4; orientationAttempt++)
            {
                lastMainEmbeddingAttempts = orientationAttempt + 1;
                int quarterTurns = (firstQuarterTurn + orientationAttempt) % 4;
                var transformed = new Vector2Int[cellEmbedding.Count];
                for (int node = 0; node < cellEmbedding.Count; node++)
                {
                    transformed[node] = TransformCoarseCell(cellEmbedding[node], quarterTurns, mirror);
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

                int cellWidth = maxX - minX + spatial.roomEnvelopeRadiusCells * 2 + 1;
                int cellDepth = maxY - minY + spatial.roomEnvelopeRadiusCells * 2 + 1;
                DungeonGenerationSettings settings = CurrentGenerationSettings.Validated();
                if (cellWidth > settings.mapWidthMaxCells || cellDepth > settings.mapDepthMaxCells)
                {
                    continue;
                }

                var centers = new Vector2Int[transformed.Length];
                for (int node = 0; node < transformed.Length; node++)
                {
                    centers[node] = new Vector2Int(
                        spatial.roomEnvelopeRadiusCells + 1 + transformed[node].x - minX,
                        spatial.roomEnvelopeRadiusCells + 1 + transformed[node].y - minY);
                }

                nodeCenters = centers;
                return true;
            }

            rejectionReason = "all four route orientations exceeded the active map envelope";
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
            DungeonPatternSpatialSettings spatial,
            IReadOnlyList<Vector2Int> nodeCenters,
            IReadOnlyList<RectInt> envelopes,
            out List<RoomFootprint> rooms,
            out string rejectionReason)
        {
            rooms = new List<RoomFootprint>(intent.nodes.Length);
            rejectionReason = string.Empty;
            lastRoomInflationAttempts = 0;

            // Recipe rooms have authored, fixed footprints: rebuilding one with
            // six random streams does not produce six alternatives. Reserve
            // those constrained footprints first so an earlier generic room
            // cannot greedily occupy their required space.
            var placedRooms = new RoomFootprint[intent.nodes.Length];
            for (int nodeIndex = 0; nodeIndex < intent.nodes.Length; nodeIndex++)
            {
                RouteNodeIntent node = intent.nodes[nodeIndex];
                if (!node.HasRecipeSlot)
                {
                    continue;
                }

                lastRoomInflationAttempts++;
                System.Random roomRandom = DerivedRandom(
                    dungeonSeed,
                    layoutAttempt,
                    node.id,
                    "room-shape-0");
                var candidate = new RoomFootprint(BuildProcessionalRoomParts(
                    intent,
                    node,
                    nodeIndex,
                    spatial,
                    nodeCenters[nodeIndex],
                    nodeCenters,
                    intent.recipeSlots,
                    roomRandom,
                    allowWing: false));
                if (!RoomFitsEnvelope(candidate, envelopes[nodeIndex]))
                {
                    rejectionReason = $"fixed recipe room node '{node.id}' exceeded its placement envelope";
                    return false;
                }

                if (OverlapsPlacedRoom(candidate, placedRooms))
                {
                    rejectionReason = $"fixed recipe room node '{node.id}' overlapped another fixed recipe room";
                    return false;
                }

                placedRooms[nodeIndex] = candidate;
            }

            for (int nodeIndex = 0; nodeIndex < intent.nodes.Length; nodeIndex++)
            {
                if (placedRooms[nodeIndex] != null)
                {
                    continue;
                }

                RouteNodeIntent node = intent.nodes[nodeIndex];
                bool placed = false;
                for (int attempt = 0; attempt < RoomInflationAttemptLimit; attempt++)
                {
                    lastRoomInflationAttempts++;
                    System.Random roomRandom = DerivedRandom(
                        dungeonSeed,
                        layoutAttempt,
                        node.id,
                        $"room-shape-{attempt}");
                    List<RectInt> parts = BuildProcessionalRoomParts(
                        intent,
                        node,
                        nodeIndex,
                        spatial,
                        nodeCenters[nodeIndex],
                        nodeCenters,
                        intent.recipeSlots,
                        roomRandom,
                        allowWing: intent.allowGenericRoomWings &&
                            attempt < RoomInflationAttemptLimit - 1 &&
                            nodeIndex != intent.vista.sourceNode &&
                            nodeIndex != intent.vista.targetNode);
                    var candidate = new RoomFootprint(parts);
                    if (!RoomFitsEnvelope(candidate, envelopes[nodeIndex]))
                    {
                        continue;
                    }

                    if (OverlapsPlacedRoom(candidate, placedRooms))
                    {
                        continue;
                    }

                    placedRooms[nodeIndex] = candidate;
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    rejectionReason = $"room node '{node.id}' exhausted {RoomInflationAttemptLimit} inflation alternatives";
                    return false;
                }
            }

            rooms.AddRange(placedRooms);
            ApplyProcessionalNeighborBias(
                intent,
                spatial,
                nodeCenters,
                envelopes,
                rooms);
            return true;
        }

        private static bool RoomFitsEnvelope(RoomFootprint room, RectInt envelope)
        {
            foreach (Vector2Int cell in room.cells)
            {
                if (!envelope.Contains(cell))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool OverlapsPlacedRoom(
            RoomFootprint candidate,
            IReadOnlyList<RoomFootprint> placedRooms)
        {
            foreach (RoomFootprint existing in placedRooms)
            {
                if (existing != null && candidate.Overlaps(existing))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ApplyProcessionalNeighborBias(
            RouteIntent intent,
            DungeonPatternSpatialSettings spatial,
            IReadOnlyList<Vector2Int> nodeCenters,
            IReadOnlyList<RectInt> envelopes,
            List<RoomFootprint> rooms)
        {
            int strength = spatial.neighborBiasStrengthCells;
            if (strength <= 0)
            {
                return;
            }

            for (int nodeIndex = 0; nodeIndex < intent.nodes.Length; nodeIndex++)
            {
                if (TryGetRecipeSlot(intent.recipeSlots, nodeIndex, out _))
                {
                    continue;
                }

                Vector2Int directionSum = Vector2Int.zero;
                foreach (RouteTraversalIntent edge in intent.traversalEdges)
                {
                    if (edge.transitionKind != RouteTransitionKind.LevelCorridor ||
                        edge.requiredRiseLevels != 0 ||
                        (edge.fromNode != nodeIndex && edge.toNode != nodeIndex))
                    {
                        continue;
                    }

                    int otherNode = edge.fromNode == nodeIndex ? edge.toNode : edge.fromNode;
                    directionSum += CardinalUnit(nodeCenters[otherNode] - nodeCenters[nodeIndex]);
                }

                var desired = new Vector2Int(
                    Math.Sign(directionSum.x),
                    Math.Sign(directionSum.y));
                if (desired == Vector2Int.zero)
                {
                    continue;
                }

                // Inflation must never consume the known-sufficient gap on a
                // stair, bridge, or stairwell edge. Growth away from that
                // neighbor, or orthogonal to it, preserves the Slice 3 face.
                foreach (RouteTraversalIntent edge in intent.traversalEdges)
                {
                    bool eligibleLevelEdge =
                        edge.transitionKind == RouteTransitionKind.LevelCorridor &&
                        edge.requiredRiseLevels == 0;
                    if (eligibleLevelEdge ||
                        (edge.fromNode != nodeIndex && edge.toNode != nodeIndex))
                    {
                        continue;
                    }

                    int otherNode = edge.fromNode == nodeIndex ? edge.toNode : edge.fromNode;
                    Vector2Int direction = CardinalUnit(nodeCenters[otherNode] - nodeCenters[nodeIndex]);
                    if (desired.x * direction.x > 0)
                    {
                        desired.x = 0;
                    }

                    if (desired.y * direction.y > 0)
                    {
                        desired.y = 0;
                    }
                }

                // Resolve each axis and cell independently. Stable node order
                // gives an odd one-cell closure to the first endpoint while
                // preventing the second endpoint from overlapping it.
                for (int step = 0; step < strength; step++)
                {
                    if (!TryApplyProcessionalRoomInflation(
                            nodeIndex,
                            new Vector2Int(desired.x, 0),
                            nodeCenters,
                            envelopes,
                            rooms))
                    {
                        break;
                    }
                }

                for (int step = 0; step < strength; step++)
                {
                    if (!TryApplyProcessionalRoomInflation(
                            nodeIndex,
                            new Vector2Int(0, desired.y),
                            nodeCenters,
                            envelopes,
                            rooms))
                    {
                        break;
                    }
                }
            }
        }

        private static bool TryApplyProcessionalRoomInflation(
            int nodeIndex,
            Vector2Int direction,
            IReadOnlyList<Vector2Int> nodeCenters,
            IReadOnlyList<RectInt> envelopes,
            List<RoomFootprint> rooms)
        {
            if (direction == Vector2Int.zero)
            {
                return false;
            }

            var expandedParts = new List<RectInt>(rooms[nodeIndex].parts);
            RectInt dominant = expandedParts[0];
            if (direction.x < 0)
            {
                dominant = new RectInt(
                    dominant.xMin - 1,
                    dominant.yMin,
                    dominant.width + 1,
                    dominant.height);
            }
            else if (direction.x > 0)
            {
                dominant = new RectInt(
                    dominant.xMin,
                    dominant.yMin,
                    dominant.width + 1,
                    dominant.height);
            }
            else if (direction.y < 0)
            {
                dominant = new RectInt(
                    dominant.xMin,
                    dominant.yMin - 1,
                    dominant.width,
                    dominant.height + 1);
            }
            else
            {
                dominant = new RectInt(
                    dominant.xMin,
                    dominant.yMin,
                    dominant.width,
                    dominant.height + 1);
            }

            for (int partIndex = 1; partIndex < expandedParts.Count; partIndex++)
            {
                if (dominant.Overlaps(expandedParts[partIndex]))
                {
                    return false;
                }
            }

            expandedParts[0] = dominant;
            var candidate = new RoomFootprint(expandedParts);
            if (!candidate.Contains(nodeCenters[nodeIndex]))
            {
                return false;
            }

            foreach (Vector2Int cell in candidate.cells)
            {
                if (!envelopes[nodeIndex].Contains(cell))
                {
                    return false;
                }
            }

            for (int otherNode = 0; otherNode < rooms.Count; otherNode++)
            {
                if (otherNode != nodeIndex && candidate.Overlaps(rooms[otherNode]))
                {
                    return false;
                }
            }

            rooms[nodeIndex] = candidate;
            return true;
        }

        private static List<RectInt> BuildProcessionalRoomParts(
            RouteIntent intent,
            RouteNodeIntent node,
            int nodeIndex,
            DungeonPatternSpatialSettings spatial,
            Vector2Int center,
            IReadOnlyList<Vector2Int> nodeCenters,
            IReadOnlyList<RecipeSlotIntent> recipeSlots,
            System.Random random,
            bool allowWing)
        {
            if (node.HasRecipeSlot && TryGetRecipeSlot(recipeSlots, nodeIndex, out RecipeSlotIntent recipeSlot))
            {
                Vector2Int primaryAxis;
                if (recipeSlot.orientationBinding == RecipeOrientationBinding.VistaSourceToTarget)
                {
                    primaryAxis = CardinalUnit(center - nodeCenters[intent.vista.sourceNode]);
                }
                else if (!TryResolveRouteForwardRecipeAxis(
                             intent,
                             recipeSlot,
                             nodeCenters,
                             out primaryAxis))
                {
                    throw new InvalidOperationException(
                        $"Recipe '{recipeSlot.recipe?.recipeId}' had no usable named exit-edge orientation");
                }

                return BuildRecipeRoomParts(recipeSlot, center, primaryAxis, mirrored: false);
            }

            ResolveGenericRoomDimensions(
                intent,
                node,
                nodeIndex,
                spatial,
                center,
                nodeCenters,
                random,
                out int width,
                out int depth);

            bool hasPlannedOverlookAppendage = HasPlannedOverlookAppendage(intent, nodeIndex);
            if (hasPlannedOverlookAppendage)
            {
                width = 4;
                depth = 5;
                allowWing = false;
            }

            RectInt dominant = CenteredRect(center, width, depth);
            var parts = new List<RectInt> { dominant };
            AddPlannedOverlookAppendages(
                intent,
                nodeIndex,
                spatial,
                center,
                nodeCenters,
                dominant,
                parts);
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

        private static void ResolveGenericRoomDimensions(
            RouteIntent intent,
            RouteNodeIntent node,
            int nodeIndex,
            DungeonPatternSpatialSettings spatial,
            Vector2Int center,
            IReadOnlyList<Vector2Int> nodeCenters,
            System.Random random,
            out int width,
            out int depth)
        {
            // The baseline is the spacious-profile size for this role. It stays
            // load-bearing as the stair-clearance cap below, so it is sampled even
            // when the active profile is wider. What was removed on 2026-07-25 is
            // the draw-REUSE that used to fold the baseline roll into the
            // configured roll whenever a range happened to span exactly two
            // values — that existed only to keep the spacious profile
            // byte-compatible with an older hash, and it made the number of random
            // draws depend on configuration.
            DungeonRoomSizeRange baselineRange = BaselineRoomSizeRangeForRole(node.role).Validated();
            DungeonRoomSizeRange configuredRange = RoomSizeRangeForRole(spatial, node.role).Validated();
            int baselineWidth = SampleRoomDimension(baselineRange.minWidthCells, baselineRange.maxWidthCells, random);
            int baselineDepth = SampleRoomDimension(baselineRange.minDepthCells, baselineRange.maxDepthCells, random);
            width = SampleRoomDimension(configuredRange.minWidthCells, configuredRange.maxWidthCells, random);
            depth = SampleRoomDimension(configuredRange.minDepthCells, configuredRange.maxDepthCells, random);

            // The concrete stair prefab is selected later. Preserve the known-
            // sufficient face position now: any non-level incident edge caps
            // growth on its route axis to the spacious baseline. The orthogonal
            // axis may still use the profile range.
            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                if (edge.transitionKind == RouteTransitionKind.LevelCorridor ||
                    (edge.fromNode != nodeIndex && edge.toNode != nodeIndex))
                {
                    continue;
                }

                int otherNode = edge.fromNode == nodeIndex ? edge.toNode : edge.fromNode;
                Vector2Int delta = nodeCenters[otherNode] - center;
                if (delta.x != 0)
                {
                    width = Mathf.Min(width, baselineWidth);
                }

                if (delta.y != 0)
                {
                    depth = Mathf.Min(depth, baselineDepth);
                }
            }
        }

        private static DungeonRoomSizeRange RoomSizeRangeForRole(
            DungeonPatternSpatialSettings spatial,
            string role)
        {
            switch (role)
            {
                case "arrival":
                case "culmination":
                    return spatial.terminalRoomSize;
                case "connector":
                    return spatial.connectorRoomSize;
                default:
                    return spatial.hallRoomSize;
            }
        }

        private static DungeonRoomSizeRange BaselineRoomSizeRangeForRole(string role)
        {
            switch (role)
            {
                case "arrival":
                case "culmination":
                    return new DungeonRoomSizeRange(5, 5, 7, 7);
                case "connector":
                    return new DungeonRoomSizeRange(4, 5, 5, 5);
                default:
                    return new DungeonRoomSizeRange(5, 5, 5, 6);
            }
        }

        private static int SampleRoomDimension(int minimum, int maximum, System.Random random)
        {
            return maximum <= minimum
                ? minimum
                : minimum + random.Next(maximum - minimum + 1);
        }

        private static bool TryGetRecipeSlot(
            IReadOnlyList<RecipeSlotIntent> slots,
            int nodeIndex,
            out RecipeSlotIntent slot)
        {
            foreach (RecipeSlotIntent candidate in slots ?? Array.Empty<RecipeSlotIntent>())
            {
                if (candidate != null && candidate.slotNode == nodeIndex)
                {
                    slot = candidate;
                    return true;
                }
            }

            slot = null;
            return false;
        }

        // Declared non-traversal neighbor pairs receive narrow, facing room
        // appendages. They create deliberate adjacent gallery/cliff edges for
        // the unchanged elevation planner without turning those vistas into
        // traversal edges or consuming the reserved source-to-landmark void.
        private static void AddPlannedOverlookAppendages(
            RouteIntent intent,
            int nodeIndex,
            DungeonPatternSpatialSettings spatial,
            Vector2Int center,
            IReadOnlyList<Vector2Int> nodeCenters,
            RectInt dominant,
            List<RectInt> parts)
        {
            foreach (RouteOverlookIntent pair in intent.plannedOverlooks)
            {
                int other = nodeIndex == pair.firstNode
                    ? pair.secondNode
                    : nodeIndex == pair.secondNode
                        ? pair.firstNode
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
                        : center.x - spatial.roomEnvelopeRadiusCells;
                    int xMax = direction.x > 0
                        ? center.x + spatial.roomEnvelopeRadiusCells + 1
                        : dominant.xMin;
                    if (string.Equals(intent.patternId, ProcessionalPatternId, StringComparison.Ordinal) &&
                        Mathf.Abs(delta.x) < spatial.roomEnvelopeRadiusCells * 2 + 1)
                    {
                        int sharedBoundary = Mathf.Min(center.x, nodeCenters[other].x) +
                            (Mathf.Abs(delta.x) + 1) / 2;
                        if (direction.x > 0)
                        {
                            xMax = Mathf.Min(xMax, sharedBoundary);
                        }
                        else
                        {
                            xMin = Mathf.Max(xMin, sharedBoundary);
                        }
                    }

                    parts.Add(new RectInt(xMin, center.y - 1, xMax - xMin, 3));
                }
                else
                {
                    int yMin = direction.y > 0
                        ? dominant.yMax
                        : center.y - spatial.roomEnvelopeRadiusCells;
                    int yMax = direction.y > 0
                        ? center.y + spatial.roomEnvelopeRadiusCells + 1
                        : dominant.yMin;
                    if (string.Equals(intent.patternId, ProcessionalPatternId, StringComparison.Ordinal) &&
                        Mathf.Abs(delta.y) < spatial.roomEnvelopeRadiusCells * 2 + 1)
                    {
                        int sharedBoundary = Mathf.Min(center.y, nodeCenters[other].y) +
                            (Mathf.Abs(delta.y) + 1) / 2;
                        if (direction.y > 0)
                        {
                            yMax = Mathf.Min(yMax, sharedBoundary);
                        }
                        else
                        {
                            yMin = Mathf.Max(yMin, sharedBoundary);
                        }
                    }

                    parts.Add(new RectInt(center.x - 1, yMin, 3, yMax - yMin));
                }
            }
        }

        private static bool HasPlannedOverlookAppendage(RouteIntent intent, int nodeIndex)
        {
            foreach (RouteOverlookIntent pair in intent.plannedOverlooks)
            {
                if (nodeIndex == pair.firstNode || nodeIndex == pair.secondNode)
                {
                    return true;
                }
            }

            return false;
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
            out Vector2Int sourceEdge,
            out Vector2Int targetEdge,
            out Vector2Int sourceFacing,
            out Vector2Int targetFacing,
            out string rejectionReason)
        {
            reservedCells = new HashSet<Vector2Int>();
            sourceEdge = default;
            targetEdge = default;
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
            if (!sourceRoom.TryGetEdgeCellTowards(sourceFacing, transverse, out sourceEdge) ||
                !targetRoom.TryGetEdgeCellTowards(targetFacing, transverse, out targetEdge))
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
            IReadOnlyList<Vector2Int> nodeCenters,
            HashSet<Vector2Int> reservedVistaCells,
            IReadOnlyList<RecipePlacement> recipePlacements,
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
                Vector2Int delta = nodeCenters[edge.toNode] - nodeCenters[edge.fromNode];
                if (delta.x != 0 && delta.y != 0 || delta == Vector2Int.zero)
                {
                    rejectionReason = $"edge '{edge.id}' endpoints were not cardinally aligned";
                    return false;
                }

                RecipePlacement fromRecipe = FindRecipePlacement(recipePlacements, edge.fromNode);
                RecipePlacement toRecipe = FindRecipePlacement(recipePlacements, edge.toNode);
                bool fromAuthored = fromRecipe != null;
                bool toAuthored = toRecipe != null;
                Vector2Int pathStart = nodeCenters[edge.fromNode];
                Vector2Int pathEnd = nodeCenters[edge.toNode];
                if (fromAuthored || toAuthored)
                {
                    RecipePlacement authored = fromRecipe ?? toRecipe;
                    if (!authored.TryGetPort(edge.id, out RecipePortPlacement port))
                    {
                        rejectionReason = $"edge '{edge.id}' touched recipe '{authored.RecipeId}' without a declared typed port";
                        return false;
                    }

                    if (fromAuthored)
                    {
                        pathStart = port.cell;
                    }
                    else
                    {
                        pathEnd = port.cell;
                    }
                }

                List<Vector2Int> path = fromAuthored || toAuthored
                    ? BuildRecipePortPath(pathStart, pathEnd, delta, fromAuthored)
                    : BuildStraightCardinalPath(pathStart, pathEnd);
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

        private static RecipePlacement FindRecipePlacement(
            IReadOnlyList<RecipePlacement> placements,
            int roomIndex)
        {
            foreach (RecipePlacement placement in placements ?? Array.Empty<RecipePlacement>())
            {
                if (placement != null && placement.roomIndex == roomIndex)
                {
                    return placement;
                }
            }

            return null;
        }

        private static List<Vector2Int> BuildRecipePortPath(
            Vector2Int start,
            Vector2Int end,
            Vector2Int roomCenterDelta,
            bool episodeIsStart)
        {
            bool routeAlongX = roomCenterDelta.x != 0;
            Vector2Int bend;
            if (episodeIsStart)
            {
                // Leave the exact episode port along the route axis, then make
                // the one-cell transverse correction inside the generic room.
                bend = routeAlongX
                    ? new Vector2Int(end.x, start.y)
                    : new Vector2Int(start.x, end.y);
            }
            else
            {
                // Make the transverse correction inside the generic room, then
                // approach the exact episode port along its outward normal.
                bend = routeAlongX
                    ? new Vector2Int(start.x, end.y)
                    : new Vector2Int(end.x, start.y);
            }

            var path = BuildStraightCardinalPath(start, bend);
            List<Vector2Int> tail = BuildStraightCardinalPath(bend, end);
            for (int index = 1; index < tail.Count; index++)
            {
                path.Add(tail[index]);
            }

            return path;
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

        private static bool RejectRoute(
            string code,
            string detail,
            out string rejectionReason)
        {
            lastRouteFailureCode = code;
            rejectionReason = $"[{code}] {detail}";
            return false;
        }

        private static System.Random DerivedRandom(
            int dungeonSeed,
            int layoutAttempt,
            string stableId,
            string purpose)
        {
            unchecked
            {
                uint hash = 2166136261u;
                MixDerivedSeedHash(ref hash, dungeonSeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                MixDerivedSeedHash(ref hash, RouteSpatialRandomVersion);
                MixDerivedSeedHash(ref hash, layoutAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                MixDerivedSeedHash(ref hash, stableId ?? string.Empty);
                MixDerivedSeedHash(ref hash, purpose ?? string.Empty);
                return new System.Random((int)hash);
            }
        }

        private static void MixDerivedSeedHash(ref uint hash, string value)
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

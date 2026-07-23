using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // The semantic graph exists before any room coordinates, then this solver
    // compiles it directly into the existing DungeonLayout.
    internal sealed partial class DungeonLabGenerator
    {
        private const string RoutePlannerVersion = "route-topologies-v9";
        private const string ProcessionalPlannerVersion = "processional-spine-v5";
        private const string AtriumRingPlannerVersion = "atrium-ring-v2";
        private const string TwinWingPlannerVersion = "twin-wing-keep-v2";
        private const string RouteRhythmPolicyVersion = "route-rhythm-v1";
        private const string NamedVistaPromontoryPolicyVersion = "named-vista-promontory-v1";
        internal static string ActiveRecipePlannerVersion => RoutePlannerVersion;
        // Preserve the proven route embedding stream. Phase 5 changes only the
        // reviewed recipe contract/ports and uses named per-recipe streams.
        private const string RouteSpatialRandomVersion = "processional-spine-v1";
        private const string Phase1PatternId = "processional-spine";
        private const string AtriumRingPatternId = "atrium-ring";
        private const string TwinWingPatternId = "twin-wing-keep";
        private const string RouteIntentInvalidFailureCode = "ROUTE_INTENT_INVALID";
        private const int Phase1LayoutAttemptLimit = 2;
        private const int RouteMainNodeCount = 9;
        private const int RouteBranchNodeCount = 4;
        private const int Phase1BranchAttachNode = 2;
        private const int Phase1BranchRejoinNode = 7;
        private const int Phase1VistaSourceNode = 9;
        private const int Phase1VistaTargetNode = 4;
        private const int AtriumRingBranchAttachNode = 3;
        private const int AtriumRingBranchRejoinNode = 6;
        private const int AtriumRingVistaSourceNode = 10;
        private const int AtriumRingVistaTargetNode = 4;
        private const int TwinWingBranchAttachNode = 2;
        private const int TwinWingBranchRejoinNode = 5;
        private const int TwinWingVistaSourceNode = 8;
        private const int TwinWingVistaTargetNode = 4;
        private const int Phase1BranchSearchExpansionLimit = 24;
        private const int Phase1RoomInflationAttemptLimit = 6;
        private const int MaxMainRouteRoleOccurrences = 2;
        private const int MinimumMainRouteNodesBetweenRecipeSlots = 2;
        private const int MaximumNamedVistaPromontoryCells = 4;
        private const int SharedReturnRecipeNode = 12;
        private static readonly RouteOverlookIntent[] ProcessionalTierSeamCandidates =
        {
            new RouteOverlookIntent(6, 9),
            new RouteOverlookIntent(7, 10)
        };

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
            public readonly RouteElevationPolicy elevationPolicy;
            public readonly RecipeSlotIntent[] recipeSlots;
            public readonly string catalogDigest;
            public readonly int bottomNode;
            public readonly int topNode;
            public readonly int branchAttachNode;
            public readonly int branchRejoinNode;
            public readonly int requiredCycleRank;
            public readonly int requiredCycleCoreNodeCount;
            public readonly int requiredJunctionDegree;
            public readonly RouteOverlookIntent[] plannedOverlooks;
            public readonly bool allowGenericRoomWings;

            public RouteIntent(
                int seed,
                string plannerVersion,
                string patternId,
                RouteNodeIntent[] nodes,
                RouteTraversalIntent[] traversalEdges,
                RouteVistaIntent vista,
                RouteElevationPolicy elevationPolicy,
                RecipeSlotIntent[] recipeSlots,
                string catalogDigest,
                int bottomNode,
                int topNode,
                int branchAttachNode,
                int branchRejoinNode,
                int requiredCycleRank,
                int requiredCycleCoreNodeCount,
                int requiredJunctionDegree,
                RouteOverlookIntent[] plannedOverlooks,
                bool allowGenericRoomWings)
            {
                this.seed = seed;
                this.plannerVersion = plannerVersion;
                this.patternId = patternId;
                this.nodes = nodes;
                this.traversalEdges = traversalEdges;
                this.vista = vista;
                this.elevationPolicy = elevationPolicy;
                this.recipeSlots = recipeSlots ?? Array.Empty<RecipeSlotIntent>();
                this.catalogDigest = catalogDigest ?? string.Empty;
                this.bottomNode = bottomNode;
                this.topNode = topNode;
                this.branchAttachNode = branchAttachNode;
                this.branchRejoinNode = branchRejoinNode;
                this.requiredCycleRank = requiredCycleRank;
                this.requiredCycleCoreNodeCount = requiredCycleCoreNodeCount;
                this.requiredJunctionDegree = requiredJunctionDegree;
                this.plannedOverlooks = plannedOverlooks ?? Array.Empty<RouteOverlookIntent>();
                this.allowGenericRoomWings = allowGenericRoomWings;
            }
        }

        private enum RoutePatternKind
        {
            ProcessionalSpine,
            AtriumRing,
            TwinWingKeep
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
            public readonly string landmarkSlotId;

            public RouteNodeIntent(
                string id,
                string role,
                string beat,
                int mainRouteOrder,
                int branchOrder,
                int relativeElevationLevels,
                string landmarkSlotId = "")
            {
                this.id = id;
                this.role = role;
                this.beat = beat;
                this.mainRouteOrder = mainRouteOrder;
                this.branchOrder = branchOrder;
                this.relativeElevationLevels = relativeElevationLevels;
                this.landmarkSlotId = landmarkSlotId ?? string.Empty;
            }

            public bool IsOnMainRoute => mainRouteOrder >= 0;

            public bool HasLandmarkSlot => !string.IsNullOrEmpty(landmarkSlotId);
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
            ResetPhase1RouteDiagnostics();
            phase1LastLayoutAttempt = layoutAttempt;

            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog recipeCatalog,
                    out rejectionReason))
            {
                return RejectPhase1Route("RECIPE_CATALOG", rejectionReason, out rejectionReason);
            }

            RoutePatternKind pattern = SelectRoutePattern(dungeonSeed);
            int landmarkNode = LandmarkNodeForPattern(pattern);
            if (!TryBuildRequiredRecipeSlots(
                    recipeCatalog,
                    pattern,
                    landmarkNode,
                    out RecipeSlotIntent[] recipeSlots,
                    out rejectionReason))
            {
                return RejectPhase1Route("RECIPE_CATALOG", rejectionReason, out rejectionReason);
            }

            RouteIntent intent = BuildSelectedRouteIntent(
                pattern,
                dungeonSeed,
                recipeSlots,
                recipeCatalog.digest);
            phase1LastRouteIntent = intent;
            if (!TryValidateRouteIntent(intent, out rejectionReason))
            {
                return RejectPhase1Route(RouteIntentInvalidFailureCode, rejectionReason, out rejectionReason);
            }

            DungeonPatternSpatialSettings spatial = ResolvePatternSpatialSettings(intent.patternId);

            if (!TryEmbedRoute(
                    dungeonSeed,
                    layoutAttempt,
                    intent,
                    spatial,
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
                return RejectPhase1Route("ROUTE_ROOM_INFLATION_EXHAUSTED", rejectionReason, out rejectionReason);
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
                return RejectPhase1Route("ROUTE_VISTA_RESERVATION_BLOCKED", rejectionReason, out rejectionReason);
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
                return RejectPhase1Route("ROUTE_PROMONTORY_RESERVATION_INVALID", rejectionReason, out rejectionReason);
            }

            phase1LastVistaCells = SortedCells(reservedVistaCells).ToArray();
            phase1LastVistaSourceFacing = sourceFacing;
            phase1LastVistaTargetFacing = targetFacing;

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
                return RejectPhase1Route("RECIPE_PLACEMENT", rejectionReason, out rejectionReason);
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
            phase1LastFailureCode = string.Empty;
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

        private static RouteIntent BuildProcessionalRouteIntent(
            int dungeonSeed,
            RecipeSlotIntent[] recipeSlots,
            string catalogDigest)
        {
            var composer = new RouteGraphComposer();
            var mainNodes = new[]
            {
                new RouteNodeIntent("arrival", "arrival", "arrival", 0, -1, 0),
                new RouteNodeIntent("threshold", "connector", "compression", 1, -1, 0, DungeonRecipeIds.CompressionConnector),
                new RouteNodeIntent("choice", "junction", "choice", 2, -1, 4),
                new RouteNodeIntent("reveal", "grand-room", "reveal", 3, -1, 4),
                new RouteNodeIntent("vista-target", "landmark", "landmark", 4, -1, 8, DungeonRecipeIds.ProcessionalLandmark),
                new RouteNodeIntent("ascent", "connector", "ascent", 5, -1, 12),
                new RouteNodeIntent("approach", "processional-hall", "approach", 6, -1, 16),
                new RouteNodeIntent("rejoin", "return-hall", "rejoin", 7, -1, 20),
                new RouteNodeIntent("culmination", "culmination", "culmination", 8, -1, 24)
            };
            string[] mainEdgeIds =
            {
                "main-0-1",
                "main-1-2",
                "main-2-3",
                "main-3-4",
                "main-4-5",
                "main-5-6",
                "main-6-7",
                "main-7-8"
            };
            RouteTransitionKind[] mainTransitionKinds =
            {
                RouteTransitionKind.LevelCorridor,
                RouteTransitionKind.Stair,
                RouteTransitionKind.LevelCorridor,
                RouteTransitionKind.Stair,
                RouteTransitionKind.Stair,
                RouteTransitionKind.Stairwell,
                RouteTransitionKind.Stair,
                RouteTransitionKind.Stair
            };
            if (!composer.TryAddSpine(
                    mainNodes,
                    mainEdgeIds,
                    mainTransitionKinds,
                    out int[] mainNodeIndices,
                    out string compositionError))
            {
                throw new InvalidOperationException($"Invalid processional spine definition: {compositionError}");
            }

            var branchNodes = new[]
            {
                new RouteNodeIntent("vista-source", "overlook", "reveal", -1, 0, 12),
                new RouteNodeIntent("branch-passage", "connector", "branch", -1, 1, 12),
                new RouteNodeIntent("branch-reward", "optional-room", "reward", -1, 2, 16),
                new RouteNodeIntent("branch-return", "connector", "return", -1, 3, 20, DungeonRecipeIds.CornerReturnConnector)
            };
            string[] branchEdgeIds =
            {
                "branch-2-9",
                "branch-9-10",
                "branch-10-11",
                "branch-11-12"
            };
            RouteTransitionKind[] branchTransitionKinds =
            {
                RouteTransitionKind.Bridge,
                RouteTransitionKind.LevelCorridor,
                RouteTransitionKind.Stair,
                RouteTransitionKind.Stair
            };
            if (!composer.TryAddBranch(
                    mainNodeIndices[Phase1BranchAttachNode],
                    branchNodes,
                    branchEdgeIds,
                    branchTransitionKinds,
                    out int[] branchNodeIndices,
                    out compositionError))
            {
                throw new InvalidOperationException($"Invalid processional branch definition: {compositionError}");
            }

            if (!composer.TryRejoin(
                    branchNodeIndices[branchNodeIndices.Length - 1],
                    mainNodeIndices[Phase1BranchRejoinNode],
                    "rejoin-12-7",
                    RouteTransitionKind.LevelCorridor,
                    out compositionError))
            {
                throw new InvalidOperationException($"Invalid processional rejoin definition: {compositionError}");
            }

            if (!composer.TryPublish(
                    out RouteNodeIntent[] nodes,
                    out RouteTraversalIntent[] edges,
                    out compositionError))
            {
                throw new InvalidOperationException($"Invalid processional graph definition: {compositionError}");
            }

            return new RouteIntent(
                dungeonSeed,
                ProcessionalPlannerVersion,
                Phase1PatternId,
                nodes,
                edges,
                new RouteVistaIntent(
                    "branch-overlook-to-landmark",
                    Phase1VistaSourceNode,
                    Phase1VistaTargetNode,
                    minimumReservedVoidCells: 3),
                RouteElevationPolicy.AscendingSpine,
                recipeSlots,
                catalogDigest,
                bottomNode: 0,
                topNode: RouteMainNodeCount - 1,
                branchAttachNode: Phase1BranchAttachNode,
                branchRejoinNode: Phase1BranchRejoinNode,
                requiredCycleRank: 1,
                requiredCycleCoreNodeCount: 10,
                requiredJunctionDegree: 3,
                plannedOverlooks: BuildPlannedOverlooks(
                    Phase1PatternId,
                    nodes,
                    edges,
                    ResolvePatternSpatialSettings(Phase1PatternId).tierSeamAdjacency),
                allowGenericRoomWings: true);
        }

        private static RouteOverlookIntent[] BuildPlannedOverlooks(
            string patternId,
            IReadOnlyList<RouteNodeIntent> nodes,
            IReadOnlyList<RouteTraversalIntent> traversalEdges,
            DungeonTierSeamAdjacencySettings requestedSettings)
        {
            DungeonTierSeamAdjacencySettings settings = requestedSettings.Validated();
            IReadOnlyList<RouteOverlookIntent> candidates = DeclaredTierSeamCandidates(patternId);
            var selected = new List<RouteOverlookIntent>(settings.requestedCount);
            foreach (RouteOverlookIntent candidate in candidates)
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
                        $"[TIER_SEAM_ADJACENCY] pattern '{patternId}' declared an invalid candidate");
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
                    $"[TIER_SEAM_ADJACENCY] pattern '{patternId}' requested {settings.requestedCount} " +
                    $"eligible adjacencies but declared {selected.Count}");
            }

            return selected.ToArray();
        }

        private static IReadOnlyList<RouteOverlookIntent> DeclaredTierSeamCandidates(string patternId)
        {
            if (string.Equals(patternId, Phase1PatternId, StringComparison.Ordinal))
            {
                return ProcessionalTierSeamCandidates;
            }

            if (string.Equals(patternId, AtriumRingPatternId, StringComparison.Ordinal) ||
                string.Equals(patternId, TwinWingPatternId, StringComparison.Ordinal))
            {
                return Array.Empty<RouteOverlookIntent>();
            }

            throw new InvalidOperationException(
                $"[TIER_SEAM_ADJACENCY] pattern '{patternId}' has no candidate declaration");
        }

        private static RoutePatternKind SelectRoutePattern(int dungeonSeed)
        {
            int residue = dungeonSeed % 4;
            if (residue < 0)
            {
                residue += 4;
            }

            if ((residue & 1) == 0)
            {
                return RoutePatternKind.ProcessionalSpine;
            }

            return residue == 1
                ? RoutePatternKind.AtriumRing
                : RoutePatternKind.TwinWingKeep;
        }

        private static string SelectedRoutePatternId(int dungeonSeed)
        {
            RoutePatternKind pattern = SelectRoutePattern(dungeonSeed);
            if (pattern == RoutePatternKind.AtriumRing)
            {
                return AtriumRingPatternId;
            }

            return pattern == RoutePatternKind.TwinWingKeep
                ? TwinWingPatternId
                : Phase1PatternId;
        }

        private static int LandmarkNodeForPattern(RoutePatternKind pattern)
        {
            if (pattern == RoutePatternKind.AtriumRing)
            {
                return AtriumRingVistaTargetNode;
            }

            return pattern == RoutePatternKind.TwinWingKeep
                ? TwinWingVistaTargetNode
                : Phase1VistaTargetNode;
        }

        private static RouteIntent BuildSelectedRouteIntent(
            RoutePatternKind pattern,
            int dungeonSeed,
            RecipeSlotIntent[] recipeSlots,
            string catalogDigest)
        {
            if (pattern == RoutePatternKind.AtriumRing)
            {
                return BuildAtriumRingRouteIntent(dungeonSeed, recipeSlots, catalogDigest);
            }

            if (pattern == RoutePatternKind.TwinWingKeep)
            {
                return BuildTwinWingRouteIntent(dungeonSeed, recipeSlots, catalogDigest);
            }

            if (pattern == RoutePatternKind.ProcessionalSpine)
            {
                return BuildProcessionalRouteIntent(dungeonSeed, recipeSlots, catalogDigest);
            }

            throw new InvalidOperationException($"Unsupported route pattern kind '{pattern}'");
        }

        private static RouteIntent BuildAtriumRingRouteIntent(
            int dungeonSeed,
            RecipeSlotIntent[] recipeSlots,
            string catalogDigest)
        {
            var composer = new RouteGraphComposer();
            var mainNodes = new[]
            {
                new RouteNodeIntent("atrium-arrival", "arrival", "arrival", 0, -1, 0),
                new RouteNodeIntent("atrium-threshold", "connector", "compression", 1, -1, 0, DungeonRecipeIds.CompressionConnector),
                new RouteNodeIntent("outer-approach", "processional-hall", "approach", 2, -1, 4),
                new RouteNodeIntent("ring-entry", "junction", "choice", 3, -1, 4),
                new RouteNodeIntent("atrium-landmark", "landmark", "landmark", 4, -1, 4, DungeonRecipeIds.ProcessionalLandmark),
                new RouteNodeIntent("ring-ascent", "connector", "ascent", 5, -1, 8),
                new RouteNodeIntent("ring-rejoin", "junction", "rejoin", 6, -1, 16),
                new RouteNodeIntent("upper-approach", "processional-hall", "approach", 7, -1, 20),
                new RouteNodeIntent("atrium-culmination", "culmination", "culmination", 8, -1, 24)
            };
            string[] mainEdgeIds =
            {
                "main-0-1",
                "main-1-2",
                "main-2-3",
                "main-3-4",
                "main-4-5",
                "main-5-6",
                "main-6-7",
                "main-7-8"
            };
            RouteTransitionKind[] mainTransitionKinds =
            {
                RouteTransitionKind.LevelCorridor,
                RouteTransitionKind.Stair,
                RouteTransitionKind.LevelCorridor,
                RouteTransitionKind.LevelCorridor,
                RouteTransitionKind.Stair,
                RouteTransitionKind.Stairwell,
                RouteTransitionKind.Stair,
                RouteTransitionKind.Stair
            };
            if (!composer.TryAddSpine(
                    mainNodes,
                    mainEdgeIds,
                    mainTransitionKinds,
                    out int[] mainNodeIndices,
                    out string compositionError))
            {
                throw new InvalidOperationException($"Invalid atrium-ring spine definition: {compositionError}");
            }

            var branchNodes = new[]
            {
                new RouteNodeIntent("lower-ring-gallery", "connector", "branch", -1, 0, 8),
                new RouteNodeIntent("ring-overlook", "overlook", "reveal", -1, 1, 8),
                new RouteNodeIntent("far-ring-gallery", "optional-room", "reward", -1, 2, 12),
                new RouteNodeIntent("upper-ring-gallery", "connector", "return", -1, 3, 16, DungeonRecipeIds.CornerReturnConnector)
            };
            string[] branchEdgeIds =
            {
                "branch-3-9",
                "branch-9-10",
                "branch-10-11",
                "branch-11-12"
            };
            RouteTransitionKind[] branchTransitionKinds =
            {
                RouteTransitionKind.Bridge,
                RouteTransitionKind.LevelCorridor,
                RouteTransitionKind.Stair,
                RouteTransitionKind.Stair
            };
            if (!composer.TryAddBranch(
                    mainNodeIndices[AtriumRingBranchAttachNode],
                    branchNodes,
                    branchEdgeIds,
                    branchTransitionKinds,
                    out int[] branchNodeIndices,
                    out compositionError))
            {
                throw new InvalidOperationException($"Invalid atrium-ring branch definition: {compositionError}");
            }

            if (!composer.TryRejoin(
                    branchNodeIndices[branchNodeIndices.Length - 1],
                    mainNodeIndices[AtriumRingBranchRejoinNode],
                    "rejoin-12-6",
                    RouteTransitionKind.LevelCorridor,
                    out compositionError))
            {
                throw new InvalidOperationException($"Invalid atrium-ring rejoin definition: {compositionError}");
            }

            if (!composer.TryPublish(
                    out RouteNodeIntent[] nodes,
                    out RouteTraversalIntent[] edges,
                    out compositionError))
            {
                throw new InvalidOperationException($"Invalid atrium-ring graph definition: {compositionError}");
            }

            return new RouteIntent(
                dungeonSeed,
                AtriumRingPlannerVersion,
                AtriumRingPatternId,
                nodes,
                edges,
                new RouteVistaIntent(
                    "ring-overlook-to-atrium-landmark",
                    AtriumRingVistaSourceNode,
                    AtriumRingVistaTargetNode,
                    minimumReservedVoidCells: 3),
                RouteElevationPolicy.AscendingSpine,
                recipeSlots,
                catalogDigest,
                bottomNode: 0,
                topNode: RouteMainNodeCount - 1,
                branchAttachNode: AtriumRingBranchAttachNode,
                branchRejoinNode: AtriumRingBranchRejoinNode,
                requiredCycleRank: 1,
                requiredCycleCoreNodeCount: 8,
                requiredJunctionDegree: 3,
                plannedOverlooks: BuildPlannedOverlooks(
                    AtriumRingPatternId,
                    nodes,
                    edges,
                    ResolvePatternSpatialSettings(AtriumRingPatternId).tierSeamAdjacency),
                allowGenericRoomWings: false);
        }

        private static RouteIntent BuildTwinWingRouteIntent(
            int dungeonSeed,
            RecipeSlotIntent[] recipeSlots,
            string catalogDigest)
        {
            var composer = new RouteGraphComposer();
            var mainNodes = new[]
            {
                new RouteNodeIntent("keep-arrival", "arrival", "arrival", 0, -1, 0),
                new RouteNodeIntent("keep-threshold", "connector", "compression", 1, -1, 0, DungeonRecipeIds.CompressionConnector),
                new RouteNodeIntent("wing-hub", "junction", "choice", 2, -1, 0),
                new RouteNodeIntent("keep-crossing", "connector", "approach", 3, -1, 0),
                new RouteNodeIntent("keep-landmark", "landmark", "landmark", 4, -1, 8, DungeonRecipeIds.ProcessionalLandmark),
                new RouteNodeIntent("wing-rejoin", "junction", "rejoin", 5, -1, 16),
                new RouteNodeIntent("keep-culmination", "culmination", "culmination", 6, -1, 24)
            };
            string[] mainEdgeIds =
            {
                "main-0-1",
                "main-1-2",
                "main-2-3",
                "main-3-4",
                "main-4-5",
                "main-5-6"
            };
            RouteTransitionKind[] mainTransitionKinds =
            {
                RouteTransitionKind.LevelCorridor,
                RouteTransitionKind.LevelCorridor,
                RouteTransitionKind.LevelCorridor,
                RouteTransitionKind.Stair,
                RouteTransitionKind.Stairwell,
                RouteTransitionKind.Stair
            };
            if (!composer.TryAddSpine(
                    mainNodes,
                    mainEdgeIds,
                    mainTransitionKinds,
                    out int[] mainNodeIndices,
                    out string compositionError))
            {
                throw new InvalidOperationException($"Invalid twin-wing spine definition: {compositionError}");
            }

            var firstWingNodes = new[]
            {
                new RouteNodeIntent("wing-a-entry", "connector", "branch", -1, 0, 8),
                new RouteNodeIntent("wing-overlook", "overlook", "reveal", -1, 1, 12),
                new RouteNodeIntent("wing-a-return", "connector", "return", -1, 2, 16)
            };
            if (!composer.TryAddBranch(
                    mainNodeIndices[TwinWingBranchAttachNode],
                    firstWingNodes,
                    new[] { "wing-a-2-7", "wing-a-7-8", "wing-a-8-9" },
                    new[]
                    {
                        RouteTransitionKind.Bridge,
                        RouteTransitionKind.Stair,
                        RouteTransitionKind.Stair
                    },
                    out int[] firstWingIndices,
                    out compositionError) ||
                !composer.TryRejoin(
                    firstWingIndices[firstWingIndices.Length - 1],
                    mainNodeIndices[TwinWingBranchRejoinNode],
                    "wing-a-rejoin-9-5",
                    RouteTransitionKind.LevelCorridor,
                    out compositionError))
            {
                throw new InvalidOperationException($"Invalid twin-wing first branch definition: {compositionError}");
            }

            var secondWingNodes = new[]
            {
                new RouteNodeIntent("wing-b-entry", "connector", "branch", -1, 3, 4),
                new RouteNodeIntent("wing-b-reward", "optional-room", "reward", -1, 4, 8),
                new RouteNodeIntent("wing-b-return", "connector", "return", -1, 5, 12, DungeonRecipeIds.CornerReturnConnector)
            };
            if (!composer.TryAddBranch(
                    mainNodeIndices[TwinWingBranchAttachNode],
                    secondWingNodes,
                    new[] { "wing-b-2-10", "wing-b-10-11", "wing-b-11-12" },
                    new[]
                    {
                        RouteTransitionKind.Stair,
                        RouteTransitionKind.Stair,
                        RouteTransitionKind.Stair
                    },
                    out int[] secondWingIndices,
                    out compositionError) ||
                !composer.TryRejoin(
                    secondWingIndices[secondWingIndices.Length - 1],
                    mainNodeIndices[TwinWingBranchRejoinNode],
                    "wing-b-rejoin-12-5",
                    RouteTransitionKind.Bridge,
                    out compositionError))
            {
                throw new InvalidOperationException($"Invalid twin-wing second branch definition: {compositionError}");
            }

            if (!composer.TryPublish(
                    out RouteNodeIntent[] nodes,
                    out RouteTraversalIntent[] edges,
                    out compositionError))
            {
                throw new InvalidOperationException($"Invalid twin-wing graph definition: {compositionError}");
            }

            return new RouteIntent(
                dungeonSeed,
                TwinWingPlannerVersion,
                TwinWingPatternId,
                nodes,
                edges,
                new RouteVistaIntent(
                    "wing-overlook-to-keep-landmark",
                    TwinWingVistaSourceNode,
                    TwinWingVistaTargetNode,
                    minimumReservedVoidCells: 3),
                RouteElevationPolicy.AscendingSpine,
                recipeSlots,
                catalogDigest,
                bottomNode: 0,
                topNode: 6,
                branchAttachNode: TwinWingBranchAttachNode,
                branchRejoinNode: TwinWingBranchRejoinNode,
                requiredCycleRank: 2,
                requiredCycleCoreNodeCount: 10,
                requiredJunctionDegree: 4,
                plannedOverlooks: BuildPlannedOverlooks(
                    TwinWingPatternId,
                    nodes,
                    edges,
                    ResolvePatternSpatialSettings(TwinWingPatternId).tierSeamAdjacency),
                allowGenericRoomWings: false);
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

            if (intent.nodes.Length != RouteMainNodeCount + RouteBranchNodeCount)
            {
                rejectionReason = $"route intent had {intent.nodes.Length} nodes instead of {RouteMainNodeCount + RouteBranchNodeCount}";
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

                if (node.HasLandmarkSlot)
                {
                    recipeSlotCount++;
                }
            }

            if (intent.recipeSlots == null ||
                intent.recipeSlots.Length != 3 ||
                recipeSlotCount != 3 ||
                string.IsNullOrEmpty(intent.catalogDigest))
            {
                rejectionReason = "route intent did not declare exactly three reviewed recipe slots and a catalog digest";
                return false;
            }

            if (!TryValidateRouteRhythm(intent.nodes, out rejectionReason))
            {
                return false;
            }

            foreach (RecipeSlotIntent slot in intent.recipeSlots)
            {
                if (slot == null || slot.recipe == null ||
                    slot.slotNode < 0 || slot.slotNode >= intent.nodes.Length ||
                    !string.Equals(intent.nodes[slot.slotNode].landmarkSlotId, slot.recipe.recipeId, StringComparison.Ordinal) ||
                    Array.IndexOf(slot.recipe.eligibleRoles, intent.nodes[slot.slotNode].role) < 0 ||
                    Array.IndexOf(slot.recipe.eligibleBeats, intent.nodes[slot.slotNode].beat) < 0)
                {
                    rejectionReason = "route intent contained an incompatible recipe slot binding";
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

                int declaredRise = intent.nodes[edge.toNode].relativeElevationLevels -
                    intent.nodes[edge.fromNode].relativeElevationLevels;
                if (edge.requiredRiseLevels != declaredRise ||
                    edge.transitionKind == RouteTransitionKind.LevelCorridor && declaredRise != 0 ||
                    edge.transitionKind != RouteTransitionKind.LevelCorridor &&
                    declaredRise != MajorRiseLevels && declaredRise != DoubleMajorRiseLevels)
                {
                    rejectionReason = $"route edge '{edge.id}' had an incompatible elevation/transition requirement";
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

            int cycleRank = intent.traversalEdges.Length - (intent.nodes.Length - 1);
            int cycleCoreNodeCount = CountCycleCoreNodes(adjacency);
            if (visited.Count != intent.nodes.Length ||
                cycleRank != intent.requiredCycleRank ||
                intent.branchAttachNode < 0 || intent.branchAttachNode >= intent.nodes.Length ||
                intent.branchRejoinNode < 0 || intent.branchRejoinNode >= intent.nodes.Length ||
                adjacency[intent.branchAttachNode].Count != intent.requiredJunctionDegree ||
                adjacency[intent.branchRejoinNode].Count != intent.requiredJunctionDegree ||
                cycleCoreNodeCount != intent.requiredCycleCoreNodeCount)
            {
                rejectionReason =
                    $"route graph reached {visited.Count}/{intent.nodes.Length} nodes with cycle rank {cycleRank} and " +
                    $"a {cycleCoreNodeCount}-node cycle core; required rank {intent.requiredCycleRank}, " +
                    $"core {intent.requiredCycleCoreNodeCount}, and degree-{intent.requiredJunctionDegree} branch endpoints";
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

                if (node.HasLandmarkSlot)
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

        private static DungeonPatternSpatialSettings ResolvePatternSpatialSettings(string patternId)
        {
            if (string.Equals(patternId, Phase1PatternId, StringComparison.Ordinal))
            {
                return CurrentGenerationSettings.Validated().processionalSpatial;
            }

            if (string.Equals(patternId, AtriumRingPatternId, StringComparison.Ordinal))
            {
                return BaselinePatternSpatialSettings(horizontalPitchCells: 7, verticalPitchCells: 9);
            }

            if (string.Equals(patternId, TwinWingPatternId, StringComparison.Ordinal))
            {
                // Twin-wing coordinates are already expressed in final cell
                // offsets, so its transform pitch remains one cell per unit.
                return BaselinePatternSpatialSettings(horizontalPitchCells: 1, verticalPitchCells: 1);
            }

            throw new InvalidOperationException(
                $"Route pattern '{patternId}' has no spatial configuration");
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

        private static bool TryEmbedRoute(
            int dungeonSeed,
            int layoutAttempt,
            RouteIntent intent,
            DungeonPatternSpatialSettings spatial,
            out Vector2Int[] nodeCenters,
            out string failureCode,
            out string rejectionReason)
        {
            if (string.Equals(intent.patternId, AtriumRingPatternId, StringComparison.Ordinal))
            {
                return TryEmbedAtriumRingRoute(
                    dungeonSeed,
                    layoutAttempt,
                    intent,
                    spatial,
                    out nodeCenters,
                    out failureCode,
                    out rejectionReason);
            }

            if (string.Equals(intent.patternId, TwinWingPatternId, StringComparison.Ordinal))
            {
                return TryEmbedTwinWingRoute(
                    dungeonSeed,
                    layoutAttempt,
                    intent,
                    spatial,
                    out nodeCenters,
                    out failureCode,
                    out rejectionReason);
            }

            if (string.Equals(intent.patternId, Phase1PatternId, StringComparison.Ordinal))
            {
                return TryEmbedProcessionalRoute(
                    dungeonSeed,
                    layoutAttempt,
                    intent,
                    spatial,
                    out nodeCenters,
                    out failureCode,
                    out rejectionReason);
            }

            nodeCenters = Array.Empty<Vector2Int>();
            failureCode = "ROUTE_PATTERN_UNSUPPORTED";
            rejectionReason = $"route pattern '{intent.patternId}' has no production embedding";
            return false;
        }

        private static bool TryEmbedProcessionalRoute(
            int dungeonSeed,
            int layoutAttempt,
            RouteIntent intent,
            DungeonPatternSpatialSettings spatial,
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

            if (branchCoarse.Count != RouteBranchNodeCount)
            {
                failureCode = "ROUTE_BRANCH_EMBEDDING_INVALID";
                rejectionReason = $"bounded branch search produced {branchCoarse.Count} branch nodes instead of {RouteBranchNodeCount}";
                return false;
            }

            var coarseEmbedding = new Vector2Int[intent.nodes.Length];
            for (int node = 0; node < RouteMainNodeCount; node++)
            {
                coarseEmbedding[node] = mainCoarse[node];
            }

            for (int branch = 0; branch < RouteBranchNodeCount; branch++)
            {
                coarseEmbedding[RouteMainNodeCount + branch] = branchCoarse[branch];
            }

            return TryTransformCoarseEmbedding(
                dungeonSeed,
                layoutAttempt,
                "route",
                coarseEmbedding,
                spatial,
                out nodeCenters,
                out rejectionReason);
        }

        private static bool TryEmbedAtriumRingRoute(
            int dungeonSeed,
            int layoutAttempt,
            RouteIntent intent,
            DungeonPatternSpatialSettings spatial,
            out Vector2Int[] nodeCenters,
            out string failureCode,
            out string rejectionReason)
        {
            failureCode = "ATRIUM_RING_EMBEDDING_EXHAUSTED";
            phase1LastBranchSearchExpansions = 0;
            var coarseEmbedding = new[]
            {
                new Vector2Int(4, 2),
                new Vector2Int(4, 1),
                new Vector2Int(4, 0),
                new Vector2Int(2, 0),
                new Vector2Int(2, 1),
                new Vector2Int(2, 2),
                new Vector2Int(2, 3),
                new Vector2Int(3, 3),
                new Vector2Int(3, 2),
                new Vector2Int(0, 0),
                new Vector2Int(0, 1),
                new Vector2Int(0, 2),
                new Vector2Int(0, 3)
            };
            if (coarseEmbedding.Length != intent.nodes.Length ||
                new HashSet<Vector2Int>(coarseEmbedding).Count != coarseEmbedding.Length)
            {
                failureCode = "ATRIUM_RING_EMBEDDING_INVALID";
                nodeCenters = Array.Empty<Vector2Int>();
                rejectionReason = "atrium-ring coarse embedding did not match its graph or was not self-avoiding";
                return false;
            }

            return TryTransformCoarseEmbedding(
                dungeonSeed,
                layoutAttempt,
                AtriumRingPatternId,
                coarseEmbedding,
                spatial,
                out nodeCenters,
                out rejectionReason);
        }

        private static bool TryEmbedTwinWingRoute(
            int dungeonSeed,
            int layoutAttempt,
            RouteIntent intent,
            DungeonPatternSpatialSettings spatial,
            out Vector2Int[] nodeCenters,
            out string failureCode,
            out string rejectionReason)
        {
            failureCode = "TWIN_WING_EMBEDDING_EXHAUSTED";
            phase1LastBranchSearchExpansions = 0;
            var embedding = new[]
            {
                new Vector2Int(-11, 0),
                new Vector2Int(-5, 0),
                new Vector2Int(0, 0),
                new Vector2Int(6, 0),
                new Vector2Int(14, 0),
                new Vector2Int(22, 0),
                new Vector2Int(31, 0),
                new Vector2Int(0, 10),
                new Vector2Int(14, 10),
                new Vector2Int(22, 10),
                new Vector2Int(0, -9),
                new Vector2Int(14, -9),
                new Vector2Int(22, -9)
            };
            if (embedding.Length != intent.nodes.Length ||
                new HashSet<Vector2Int>(embedding).Count != embedding.Length)
            {
                failureCode = "TWIN_WING_EMBEDDING_INVALID";
                nodeCenters = Array.Empty<Vector2Int>();
                rejectionReason = "twin-wing embedding did not match its graph or was not self-avoiding";
                return false;
            }

            return TryTransformCoarseEmbedding(
                dungeonSeed,
                layoutAttempt,
                TwinWingPatternId,
                embedding,
                spatial,
                out nodeCenters,
                out rejectionReason);
        }

        private static bool TryTransformCoarseEmbedding(
            int dungeonSeed,
            int layoutAttempt,
            string stablePatternId,
            IReadOnlyList<Vector2Int> coarseEmbedding,
            DungeonPatternSpatialSettings spatial,
            out Vector2Int[] nodeCenters,
            out string rejectionReason)
        {
            nodeCenters = Array.Empty<Vector2Int>();
            rejectionReason = string.Empty;
            System.Random placementRandom = Phase1Random(
                dungeonSeed,
                layoutAttempt,
                stablePatternId,
                "orientation");
            int firstQuarterTurn = placementRandom.Next(4);
            bool mirror = placementRandom.Next(2) == 0;
            for (int orientationAttempt = 0; orientationAttempt < 4; orientationAttempt++)
            {
                phase1LastMainEmbeddingAttempts = orientationAttempt + 1;
                int quarterTurns = (firstQuarterTurn + orientationAttempt) % 4;
                var transformed = new Vector2Int[coarseEmbedding.Count];
                for (int node = 0; node < coarseEmbedding.Count; node++)
                {
                    Vector2Int scaled = new Vector2Int(
                        coarseEmbedding[node].x * spatial.horizontalPitchCells,
                        coarseEmbedding[node].y * spatial.verticalPitchCells);
                    transformed[node] = TransformCoarseCell(scaled, quarterTurns, mirror);
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
            DungeonPatternSpatialSettings spatial,
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
                        intent,
                        node,
                        nodeIndex,
                        spatial,
                        nodeCenters[nodeIndex],
                        nodeCenters,
                        intent.recipeSlots,
                        roomRandom,
                        allowWing: intent.allowGenericRoomWings &&
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

            ApplyProcessionalNeighborBias(
                intent,
                spatial,
                nodeCenters,
                envelopes,
                rooms);
            return true;
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
            if (node.HasLandmarkSlot && TryGetRecipeSlot(recipeSlots, nodeIndex, out RecipeSlotIntent recipeSlot))
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
            DungeonRoomSizeRange baselineRange = BaselineRoomSizeRangeForRole(node.role).Validated();
            DungeonRoomSizeRange configuredRange = RoomSizeRangeForRole(spatial, node.role).Validated();
            bool widthHasBaselineRoll = baselineRange.maxWidthCells > baselineRange.minWidthCells;
            bool depthHasBaselineRoll = baselineRange.maxDepthCells > baselineRange.minDepthCells;
            int baselineWidth = baselineRange.minWidthCells +
                (widthHasBaselineRoll ? random.Next(baselineRange.maxWidthCells - baselineRange.minWidthCells + 1) : 0);
            int baselineDepth = baselineRange.minDepthCells +
                (depthHasBaselineRoll ? random.Next(baselineRange.maxDepthCells - baselineRange.minDepthCells + 1) : 0);

            width = SampleConfiguredRoomDimension(
                configuredRange.minWidthCells,
                configuredRange.maxWidthCells,
                baselineWidth,
                widthHasBaselineRoll,
                baselineRange.minWidthCells,
                random);
            depth = SampleConfiguredRoomDimension(
                configuredRange.minDepthCells,
                configuredRange.maxDepthCells,
                baselineDepth,
                depthHasBaselineRoll,
                baselineRange.minDepthCells,
                random);

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

        private static int SampleConfiguredRoomDimension(
            int minimum,
            int maximum,
            int baselineValue,
            bool hasBaselineRoll,
            int baselineMinimum,
            System.Random random)
        {
            if (minimum == maximum)
            {
                return minimum;
            }

            // Reuse the already-consumed binary baseline roll for any two-value
            // range. That keeps the spacious profile byte-for-byte compatible
            // without creating a second room-size random stream.
            if (hasBaselineRoll && maximum - minimum == 1)
            {
                return minimum + baselineValue - baselineMinimum;
            }

            return minimum + random.Next(maximum - minimum + 1);
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
                    if (string.Equals(intent.patternId, Phase1PatternId, StringComparison.Ordinal) &&
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
                    if (string.Equals(intent.patternId, Phase1PatternId, StringComparison.Ordinal) &&
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
                MixPhase1Hash(ref hash, RouteSpatialRandomVersion);
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

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // The semantic graph exists before any room coordinates, then this solver
    // compiles it directly into the existing DungeonLayout.
    internal sealed partial class DungeonLabGenerator
    {
        private const string RoutePlannerVersion = "route-topologies-v11";
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
        // Room count is a property of the graph now, not a dial: a topology
        // declares as many nodes as its shape needs. The bounds are sanity
        // rails, not a target — the profile's denseFloorplanMinRooms is the
        // binding floor in practice.
        private const int MinRouteNodeCount = 9;
        private const int MaxRouteNodeCount = 20;
        private const int RoomInflationAttemptLimit = 6;
        // The route-shape attempt ceiling (design §3.2), sized from measurement
        // rather than guessed — the habit that took TierPlacementAttempts from 32
        // to 4. Started at 4, probed at 16, and the observed maximum over the
        // 200-seed corpus at density 4 was 13, so the ceiling is twice that.
        // Retries are not repetitions: the attempt index joins the stream key, so
        // attempt k draws a genuinely different shape rather than re-running an
        // impossible one. lastRouteShapeAttempts keeps the observation live.
        private const int RouteShapeAttemptLimit = 26;
        private const int MaxMainRouteRoleOccurrences = 2;
        private const int MinimumMainRouteNodesBetweenRecipeSlots = 2;
        private const int MaximumNamedVistaPromontoryCells = 4;
        private const string CompressionRecipeSlotId = "required-compression";
        private const string LandmarkRecipeSlotId = "required-landmark";
        private const string ReturnRecipeSlotId = "required-return";

        // Ephemeral diagnostic evidence for the most recent attempt.
        // It is never consumed by generation or carried into DungeonLayout.
        private static RouteIntent lastRouteIntent;
        private static Vector2Int[] lastNodeCenters = Array.Empty<Vector2Int>();
        private static Vector2Int[] lastVistaCells = Array.Empty<Vector2Int>();
        private static Vector2Int lastVistaSourceFacing;
        private static Vector2Int lastVistaTargetFacing;
        private static int lastLayoutAttempt;
        private static int lastMainEmbeddingAttempts;
        private static int lastLatticeSlackSpentCells;
        private static int lastLatticeSlackAvailableCells;
        private static int lastRoomInflationAttempts;
        private static int lastRouteShapeAttempts;
        private static string lastRouteFailureCode = string.Empty;
        // Which rung of the corridor ladder each edge landed on. Evidence, not
        // policy: it is how phase 3 sees whether the reroute is carrying the
        // packed densities or whether the straight path is still doing the work.
        private static readonly SortedDictionary<string, int> lastCorridorRungCounts =
            new SortedDictionary<string, int>(StringComparer.Ordinal);

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
            // The box this dungeon was given, fixed by the embedding. Carried
            // rather than recomputed so the tier stage's fill gate measures the
            // same denominator the layout stage did.
            public readonly RectInt latticeEnvelope;
            public readonly HashSet<Vector2Int> reservedVistaCells;
            public readonly Vector2Int vistaSourceCell;
            public readonly Vector2Int vistaTargetCell;
            public readonly Vector2Int vistaSourceFacing;
            public readonly Vector2Int vistaTargetFacing;
            public readonly Vector2Int[] namedPromontoryCells;
            public readonly RecipePlacement[] recipes;

            public RouteTierRequirements(
                RouteIntent intent,
                RectInt latticeEnvelope,
                IEnumerable<Vector2Int> reservedVistaCells,
                Vector2Int vistaSourceCell,
                Vector2Int vistaTargetCell,
                Vector2Int vistaSourceFacing,
                Vector2Int vistaTargetFacing,
                Vector2Int[] namedPromontoryCells,
                RecipePlacement[] recipes)
            {
                this.intent = intent;
                this.latticeEnvelope = latticeEnvelope;
                this.reservedVistaCells = new HashSet<Vector2Int>(reservedVistaCells);
                this.vistaSourceCell = vistaSourceCell;
                this.vistaTargetCell = vistaTargetCell;
                this.vistaSourceFacing = vistaSourceFacing;
                this.vistaTargetFacing = vistaTargetFacing;
                this.namedPromontoryCells = namedPromontoryCells ?? Array.Empty<Vector2Int>();
                this.recipes = recipes ?? Array.Empty<RecipePlacement>();
            }

            /// <summary>
            /// Resolve a corridor's route edge BY EDGE ID.
            /// </summary>
            /// <remarks>
            /// This used to key on the room pair, which is a correctness defect
            /// and not merely plumbing (design §8.1): a room pair does not
            /// identify an edge once two corridors may join the same two rooms at
            /// different elevations, and the lookup would silently hand back
            /// whichever edge it found first. Edge id is the identity the route
            /// intent actually has; `RouteTransitionResolution` already carried
            /// it, so this closes the gap on the corridor side.
            /// <para>
            /// A `SynthesizedLoop` connection carries no edge id and resolves
            /// nothing — by design, not by omission.
            /// </para>
            /// </remarks>
            public bool TryGetTransition(string edgeId, out RouteTraversalIntent requirement)
            {
                if (!string.IsNullOrEmpty(edgeId))
                {
                    foreach (RouteTraversalIntent edge in intent.traversalEdges)
                    {
                        if (string.Equals(edge.id, edgeId, StringComparison.Ordinal))
                        {
                            requirement = edge;
                            return true;
                        }
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
            lastLatticeSlackSpentCells = 0;
            lastLatticeSlackAvailableCells = 0;
            lastRoomInflationAttempts = 0;
            lastRouteShapeAttempts = 0;
            lastRouteFailureCode = string.Empty;
            lastCorridorRungCounts.Clear();
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
                    out Vector2Int[] latticeCellCenters,
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

            // The sight lane is claimed from node centres BEFORE inflation, so
            // rooms grow around it instead of it surviving on whatever void
            // inflation happened to leave (design §3).
            if (!TryPlanVistaLane(intent, nodeCenters, out HashSet<Vector2Int> vistaLaneCells, out rejectionReason))
            {
                return RejectRoute("ROUTE_VISTA_RESERVATION_BLOCKED", rejectionReason, out rejectionReason);
            }

            // THE ROUTE-SHAPE ATTEMPT (design §3.2). Inflation, vista, promontory,
            // recipes and corridors are retried as ONE unit, because there is no
            // smaller honest boundary: re-rolling a single room's inflation after
            // a corridor failure would invalidate the vista lane reserved from it,
            // the recipe ports placed against it, and every corridor an earlier
            // edge already claimed. All five stages are pure functions of
            // (intent, spatial, nodeCenters) plus their own RNG stream, so
            // re-running the suffix is a re-call rather than a rollback — nothing
            // outside it is mutated, since the layout is only published at the end.
            //
            // It nests: the corridor candidate ladder (§3.1) is the inner retry,
            // this is the middle one, and today's layout attempt — which re-runs
            // from embedding with a fresh orientation — stays the outer one.
            List<RoomFootprint> rooms = null;
            HashSet<Vector2Int> reservedVistaCells = null;
            HashSet<Vector2Int> protectedVistaCells = null;
            Vector2Int sourceVistaCell = default;
            Vector2Int targetVistaCell = default;
            Vector2Int sourceFacing = default;
            Vector2Int targetFacing = default;
            Vector2Int[] namedPromontoryCells = null;
            RecipePlacement[] recipePlacements = null;
            HashSet<Vector2Int> floorCells = null;
            List<RoomConnection> connections = null;
            string shapeFailureCode = "ROUTE_ROOM_INFLATION_EXHAUSTED";
            bool shapeCompiled = false;
            for (int shapeAttempt = 0; shapeAttempt < RouteShapeAttemptLimit && !shapeCompiled; shapeAttempt++)
            {
                lastRouteShapeAttempts = shapeAttempt + 1;
                if (!TryInflateProcessionalRooms(
                        dungeonSeed,
                        layoutAttempt,
                        shapeAttempt,
                        intent,
                        spatial,
                        nodeCenters,
                        roomEnvelopes,
                        vistaLaneCells,
                        out rooms,
                        out rejectionReason))
                {
                    shapeFailureCode = "ROUTE_ROOM_INFLATION_EXHAUSTED";
                    continue;
                }

                if (!TryReserveProcessionalVista(
                        intent,
                        rooms,
                        nodeCenters,
                        out reservedVistaCells,
                        out sourceVistaCell,
                        out targetVistaCell,
                        out sourceFacing,
                        out targetFacing,
                        out rejectionReason))
                {
                    shapeFailureCode = "ROUTE_VISTA_RESERVATION_BLOCKED";
                    continue;
                }

                protectedVistaCells = new HashSet<Vector2Int>(reservedVistaCells);
                if (!TryPlanNamedVistaPromontory(
                        intent,
                        reservedVistaCells,
                        sourceVistaCell,
                        targetVistaCell,
                        sourceFacing,
                        targetFacing,
                        out namedPromontoryCells,
                        out rejectionReason))
                {
                    shapeFailureCode = "ROUTE_PROMONTORY_RESERVATION_INVALID";
                    continue;
                }

                if (!TryPlaceRouteRecipes(
                        dungeonSeed,
                        layoutAttempt,
                        shapeAttempt,
                        intent,
                        rooms,
                        nodeCenters,
                        sourceFacing,
                        targetFacing,
                        out recipePlacements,
                        out rejectionReason))
                {
                    shapeFailureCode = "RECIPE_PLACEMENT";
                    continue;
                }

                if (!TryConnectProcessionalRooms(
                        intent,
                        rooms,
                        nodeCenters,
                        protectedVistaCells,
                        recipePlacements,
                        out floorCells,
                        out connections,
                        out rejectionReason))
                {
                    shapeFailureCode = "ROUTE_CORRIDOR_EMBEDDING_EXHAUSTED";
                    continue;
                }

                shapeCompiled = true;
            }

            if (!shapeCompiled)
            {
                return RejectRoute(shapeFailureCode, rejectionReason, out rejectionReason);
            }

            // M3 — annex the vacant lattice cells (design §4.2). It runs HERE,
            // after every claim on space has been compiled, and takes only cells
            // that are provably free. That ordering is the whole design: the
            // blind pre-inflation reservation this replaces could not know what
            // it was competing with and cost 61 seeds at density 0. It cannot
            // fail an attempt — at worst it annexes nothing and the seed is
            // exactly what M2 produced, which is what densities 0-2 ask for.
            AnnexAndMopUpLatticeVoid(
                dungeonSeed,
                layoutAttempt,
                intent,
                spatial,
                nodeCenters,
                latticeCellCenters,
                protectedVistaCells,
                recipePlacements,
                connections,
                rooms,
                floorCells);

            lastVistaCells = SortedCells(reservedVistaCells).ToArray();
            lastVistaSourceFacing = sourceFacing;
            lastVistaTargetFacing = targetFacing;

            if (!IsConnected(floorCells))
            {
                return RejectRoute(
                    "ROUTE_FLOOR_DISCONNECTED",
                    "compiled route-first floor mask was disconnected",
                    out rejectionReason);
            }

            // Measured over the LATTICE envelope, not the floor bounding box.
            // The old denominator grew whenever a promontory reached outward or a
            // room grew, so it rejected layouts for doing the two things the
            // density work wants (design §3, §5). This one is fixed by the
            // embedding, which makes it a backstop against a degenerate layout
            // rather than a hidden shaper of room size.
            RectInt latticeEnvelope = LatticeEnvelopeFor(nodeCenters, spatial);
            float latticeFill = LatticeEnvelopeFillPercent(floorCells, latticeEnvelope);
            if (rooms.Count < settings.denseFloorplanMinRooms ||
                latticeFill < settings.minLatticeEnvelopeFillPercent)
            {
                return RejectRoute(
                    "ROUTE_DENSITY_PRECONDITION",
                    $"compiled {rooms.Count} rooms at {latticeFill * 100f:0.#}% lattice-envelope fill; " +
                    $"profile requires {settings.denseFloorplanMinRooms} rooms and {settings.minLatticeEnvelopeFillPercent * 100f:0.#}% fill",
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
                connectorCandidateCount,
                lastReservedShaftCells);
            routeRequirements = new RouteTierRequirements(
                intent,
                latticeEnvelope,
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

        /// <summary>
        /// Force every seed onto one topology, ignoring its weight.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The one way to reach a weight-0 topology. An AUTHORED episode is
        /// placed by an authored graph, and an authored graph must not be in the
        /// weighted draw while it is being built — adding a weighted topology
        /// changes `totalWeight` and therefore re-rolls the topology of every
        /// seed in the corpus, which destroys the one gate that can tell a
        /// regression from a rebaseline.
        /// </para>
        /// <para>
        /// Env var so a headless run can set it (the same idiom as
        /// `ARENA_RANDOM_DUNGEON_SEED` and `ARENA_DUNGEON_DENSITY`), static
        /// override so a menu item can. Unset, this reads one environment
        /// variable and changes nothing.
        /// </para>
        /// </remarks>
        internal const string TopologyOverrideEnvironmentVariable = "ARENA_DUNGEON_TOPOLOGY";

        private static string forcedRouteTopologyId = string.Empty;

        internal static string ResolveForcedRouteTopologyId()
        {
            if (!string.IsNullOrEmpty(forcedRouteTopologyId))
            {
                return forcedRouteTopologyId;
            }

            string fromEnvironment =
                Environment.GetEnvironmentVariable(TopologyOverrideEnvironmentVariable);
            return string.IsNullOrWhiteSpace(fromEnvironment)
                ? string.Empty
                : fromEnvironment.Trim();
        }

        /// <summary>Scope a forced topology to one build.</summary>
        internal static IDisposable ForceRouteTopology(string topologyId)
        {
            // Fails loudly on a typo rather than silently generating an ordinary
            // dungeon, which is the whole failure mode of a forced build.
            RequireRouteTopology(topologyId);
            return new ForcedRouteTopologyScope(topologyId);
        }

        private sealed class ForcedRouteTopologyScope : IDisposable
        {
            private readonly string previous;
            private bool disposed;

            public ForcedRouteTopologyScope(string topologyId)
            {
                previous = forcedRouteTopologyId;
                forcedRouteTopologyId = topologyId;
            }

            public void Dispose()
            {
                if (!disposed)
                {
                    disposed = true;
                    forcedRouteTopologyId = previous;
                }
            }
        }

        // A weighted draw over the registry, keyed on the SEED ALONE — never on
        // the layout attempt. A retry must not be able to change which dungeon
        // it is retrying, and the derived-RNG doctrine wants the key to name
        // exactly what the decision depends on. weight 0 disables a topology
        // without renumbering anything.
        private static string SelectRouteTopologyId(int dungeonSeed)
        {
            string forced = ResolveForcedRouteTopologyId();
            if (!string.IsNullOrEmpty(forced))
            {
                return RequireRouteTopology(forced).id;
            }

            List<DungeonRouteTopology> candidates = AllRouteTopologiesByFileOrder();
            int totalWeight = 0;
            foreach (DungeonRouteTopology candidate in candidates)
            {
                totalWeight += candidate.weight;
            }

            if (totalWeight <= 0)
            {
                throw new InvalidOperationException(
                    $"[ROUTE_TOPOLOGY] every topology under {RouteTopologyDirectory} has weight 0, " +
                    "so no route can be selected");
            }

            int roll = DerivedRandom(dungeonSeed, 0, "topology", "select").Next(totalWeight);
            foreach (DungeonRouteTopology candidate in candidates)
            {
                roll -= candidate.weight;
                if (roll < 0)
                {
                    return candidate.id;
                }
            }

            return candidates[candidates.Count - 1].id;
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

            if (intent.nodes.Length < MinRouteNodeCount || intent.nodes.Length > MaxRouteNodeCount)
            {
                rejectionReason =
                    $"route intent had {intent.nodes.Length} nodes, outside the accepted " +
                    $"{MinRouteNodeCount}..{MaxRouteNodeCount}";
                return false;
            }

            DungeonGenerationSettings activeSettings = CurrentGenerationSettings.Validated();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int recipeSlotCount = 0;
            foreach (RouteNodeIntent node in intent.nodes)
            {
                if (string.IsNullOrEmpty(node.id) || !ids.Add(node.id))
                {
                    rejectionReason = $"route intent contained a missing or duplicate node id '{node.id}'";
                    return false;
                }

                // A role with no size class would silently render as a hall.
                // Catching it here means a new role name is an authoring error
                // with a node in the message, not a shape nobody ordered.
                if (!node.HasRecipeSlot &&
                    !activeSettings.TryResolveRoomSizeClass(node.role, out _))
                {
                    rejectionReason =
                        $"route node '{node.id}' declares role '{node.role}', which profile " +
                        $"'{activeSettings.profileName}' does not map to a room size class";
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

                // Signed +-4/+-8: an author writes an edge in travel order and
                // stops thinking about it. Every downstream consumer already
                // compares the directed rise against this same signed value or
                // takes its absolute value, so a descending edge is a rise of
                // -4 rather than a rule.
                int riseMagnitude = Mathf.Abs(edge.requiredRiseLevels);
                if (edge.transitionKind == RouteTransitionKind.LevelCorridor &&
                        riseMagnitude != 0 ||
                    edge.transitionKind != RouteTransitionKind.LevelCorridor &&
                    riseMagnitude != MajorRiseLevels &&
                    riseMagnitude != DoubleMajorRiseLevels)
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
            out Vector2Int[] latticeCellCenters,
            out string failureCode,
            out string rejectionReason)
        {
            DungeonRouteTopology topology = intent.topology;
            nodeCenters = Array.Empty<Vector2Int>();
            latticeCellCenters = Array.Empty<Vector2Int>();
            failureCode = RouteEmbeddingFailureCode;
            rejectionReason = string.Empty;
            int[] columnOffsets = ResolveLatticeLaneOffsets(
                dungeonSeed,
                layoutAttempt,
                topology,
                topology.columnGaps,
                spatial.horizontalPitchCells,
                topology.latticeColumnCount,
                spatial,
                "lattice-x",
                out int columnSlackSpent,
                out int columnSlackAvailable);
            int[] rowOffsets = ResolveLatticeLaneOffsets(
                dungeonSeed,
                layoutAttempt,
                topology,
                topology.rowGaps,
                spatial.verticalPitchCells,
                topology.latticeRowCount,
                spatial,
                "lattice-y",
                out int rowSlackSpent,
                out int rowSlackAvailable);
            lastLatticeSlackSpentCells = columnSlackSpent + rowSlackSpent;
            lastLatticeSlackAvailableCells = columnSlackAvailable + rowSlackAvailable;
            var embedding = new Vector2Int[topology.nodes.Length];
            for (int node = 0; node < embedding.Length; node++)
            {
                Vector2Int lattice = topology.nodes[node].lattice;
                embedding[node] = new Vector2Int(columnOffsets[lattice.x], rowOffsets[lattice.y]);
            }

            // Every lattice cell, occupied or not, carried through the SAME
            // transform as the nodes. The vacant ones are M3's craters, and
            // there is no other way to know where one is: a lattice cell with
            // no node contributes no node centre to reason back from.
            var latticeCells = new Vector2Int[topology.latticeColumnCount * topology.latticeRowCount];
            for (int row = 0; row < topology.latticeRowCount; row++)
            {
                for (int column = 0; column < topology.latticeColumnCount; column++)
                {
                    latticeCells[row * topology.latticeColumnCount + column] =
                        new Vector2Int(columnOffsets[column], rowOffsets[row]);
                }
            }

            return TryTransformCoarseEmbedding(
                dungeonSeed,
                layoutAttempt,
                topology.id,
                embedding,
                latticeCells,
                spatial,
                out nodeCenters,
                out latticeCellCenters,
                out rejectionReason);
        }

        private static bool TryTransformCoarseEmbedding(
            int dungeonSeed,
            int layoutAttempt,
            string stablePatternId,
            IReadOnlyList<Vector2Int> cellEmbedding,
            IReadOnlyList<Vector2Int> latticeCellEmbedding,
            DungeonPatternSpatialSettings spatial,
            out Vector2Int[] nodeCenters,
            out Vector2Int[] latticeCellCenters,
            out string rejectionReason)
        {
            nodeCenters = Array.Empty<Vector2Int>();
            latticeCellCenters = Array.Empty<Vector2Int>();
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

                // The lattice grid rides along on the accepted orientation. It
                // takes no part in choosing one: minX/minY and the envelope test
                // above are the node bounding box exactly as before, so a vacant
                // cell reaching past the nodes cannot move or reject a layout.
                var latticeCenters = new Vector2Int[latticeCellEmbedding?.Count ?? 0];
                for (int cell = 0; cell < latticeCenters.Length; cell++)
                {
                    Vector2Int mapped = TransformCoarseCell(
                        latticeCellEmbedding[cell],
                        quarterTurns,
                        mirror);
                    latticeCenters[cell] = new Vector2Int(
                        spatial.roomEnvelopeRadiusCells + 1 + mapped.x - minX,
                        spatial.roomEnvelopeRadiusCells + 1 + mapped.y - minY);
                }

                latticeCellCenters = latticeCenters;
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
            int shapeAttempt,
            RouteIntent intent,
            DungeonPatternSpatialSettings spatial,
            IReadOnlyList<Vector2Int> nodeCenters,
            IReadOnlyList<RectInt> envelopes,
            HashSet<Vector2Int> vistaLaneCells,
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
                    ShapeAttemptPurpose("room-shape-0", shapeAttempt));
                var candidate = new RoomFootprint(BuildProcessionalRoomParts(
                    intent,
                    node,
                    nodeIndex,
                    spatial,
                    nodeCenters[nodeIndex],
                    nodeCenters,
                    placedRooms,
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

                if (IntrudesOnVistaLane(intent, nodeIndex, candidate, vistaLaneCells))
                {
                    rejectionReason = $"fixed recipe room node '{node.id}' occupied the reserved vista lane";
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
                        ShapeAttemptPurpose($"room-shape-{attempt}", shapeAttempt));
                    List<RectInt> parts = BuildProcessionalRoomParts(
                        intent,
                        node,
                        nodeIndex,
                        spatial,
                        nodeCenters[nodeIndex],
                        nodeCenters,
                        placedRooms,
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

                    if (IntrudesOnVistaLane(intent, nodeIndex, candidate, vistaLaneCells))
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
                vistaLaneCells,
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
            HashSet<Vector2Int> vistaLaneCells,
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
                            intent,
                            nodeIndex,
                            new Vector2Int(desired.x, 0),
                            nodeCenters,
                            envelopes,
                            vistaLaneCells,
                            rooms))
                    {
                        break;
                    }
                }

                for (int step = 0; step < strength; step++)
                {
                    if (!TryApplyProcessionalRoomInflation(
                            intent,
                            nodeIndex,
                            new Vector2Int(0, desired.y),
                            nodeCenters,
                            envelopes,
                            vistaLaneCells,
                            rooms))
                    {
                        break;
                    }
                }
            }
        }

        private static bool TryApplyProcessionalRoomInflation(
            RouteIntent intent,
            int nodeIndex,
            Vector2Int direction,
            IReadOnlyList<Vector2Int> nodeCenters,
            IReadOnlyList<RectInt> envelopes,
            HashSet<Vector2Int> vistaLaneCells,
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

            // The bias only ever grows along a level-corridor direction, but the
            // sight lane is not an edge and would otherwise be invisible to it.
            if (IntrudesOnVistaLane(intent, nodeIndex, candidate, vistaLaneCells))
            {
                return false;
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
            IReadOnlyList<RoomFootprint> placedRooms,
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
                placedRooms,
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
            IReadOnlyList<RoomFootprint> placedRooms,
            System.Random random,
            out int width,
            out int depth)
        {
            // A room may never be wider than the lane gap it has to sit in: two
            // rooms centred one lane apart overlap the moment either exceeds
            // that gap, and inflation then burns its re-rolls and fails the
            // attempt — which is how sunken-basin and terraced-cascade lost
            // every density-5 seed before any clamp existed. The binding gap is
            // this node's OWN, measured from the embedding, not the tightest
            // lane anywhere on the axis: on twin-wing-keep (lanes 6,5,6,8,8,9)
            // the global form pinned every room to five cells at every density.
            ResolveAdjacentLaneGaps(
                nodeCenters,
                nodeIndex,
                spatial,
                out int laneWidthCells,
                out int laneDepthCells);

            // The lane gap is the right bound against another GENERIC room,
            // because both are centred the same way and two of them one gap
            // apart meet without overlapping. A recipe room is not: its
            // authored footprint reaches symmetrically from its anchor, up to
            // the full envelope radius, so a generic room the width of the lane
            // lands one cell inside it. Those rooms are already placed here —
            // fixed footprints are reserved first, precisely because they cannot
            // re-roll — so the bound is measured off the real rect rather than
            // assumed. ridge-ravine's `ridge-walk` sits one 8-cell lane from its
            // landmark recipe and lost every seed at densities 4 and 5 without
            // this.
            ClampRoomSizeToPlacedRooms(
                placedRooms,
                nodeIndex,
                center,
                spatial,
                ref laneWidthCells,
                ref laneDepthCells);
            DungeonRoomSizeRange configuredRange = ClampRoomSize(
                RoomSizeRangeForRole(spatial, node.role).Validated(),
                laneWidthCells,
                laneDepthCells);
            width = SampleRoomDimension(configuredRange.minWidthCells, configuredRange.maxWidthCells, random);
            depth = SampleRoomDimension(configuredRange.minDepthCells, configuredRange.maxDepthCells, random);

            // The concrete stair prefab is selected later, so the room has to
            // leave enough corridor for whichever one wins. That reservation is
            // now MEASURED from the static contract set per edge kind and rise
            // (M1) rather than approximated by a fixed room-size table: the old
            // rule gave up four or five cells on this axis to protect a run that
            // is one cell at rise 4 and two at rise 8. Measuring it against the
            // actual lane distance is also what lets the reservation survive the
            // density dial — the corridor requirement is constant, so rooms give
            // back exactly what a tighter pitch takes away and no more.
            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                if (edge.transitionKind == RouteTransitionKind.LevelCorridor ||
                    (edge.fromNode != nodeIndex && edge.toNode != nodeIndex))
                {
                    continue;
                }

                int otherNode = edge.fromNode == nodeIndex ? edge.toNode : edge.fromNode;
                Vector2Int delta = nodeCenters[otherNode] - center;
                int required = RequiredTransitionCorridorCells(
                    edge.transitionKind,
                    edge.requiredRiseLevels);
                if (delta.x != 0)
                {
                    width = Mathf.Min(
                        width,
                        MaxRoomExtentForTransition(Mathf.Abs(delta.x), required));
                }

                if (delta.y != 0)
                {
                    depth = Mathf.Min(
                        depth,
                        MaxRoomExtentForTransition(Mathf.Abs(delta.y), required));
                }
            }

            // The vista pair is capped the same way, against the sight lane's own
            // required clear run. Rooms other than the pair are kept out of the
            // lane entirely by TryInflateProcessionalRooms; this is what keeps
            // the pair itself from closing the gap between them.
            if (nodeIndex == intent.vista.sourceNode || nodeIndex == intent.vista.targetNode)
            {
                int otherNode = nodeIndex == intent.vista.sourceNode
                    ? intent.vista.targetNode
                    : intent.vista.sourceNode;
                Vector2Int vistaDelta = nodeCenters[otherNode] - center;
                int requiredVoid = intent.vista.minimumReservedVoidCells;
                if (vistaDelta.x != 0)
                {
                    width = Mathf.Min(
                        width,
                        MaxRoomExtentForTransition(Mathf.Abs(vistaDelta.x), requiredVoid));
                }

                if (vistaDelta.y != 0)
                {
                    depth = Mathf.Min(
                        depth,
                        MaxRoomExtentForTransition(Mathf.Abs(vistaDelta.y), requiredVoid));
                }
            }
        }

        /// <summary>
        /// Tightens a node's lane bounds against rooms already on the board.
        /// </summary>
        /// <remarks>
        /// A centred rect of width <c>w</c> at <c>c</c> covers
        /// <c>[c - w/2, c + ceil(w/2) - 1]</c>, so the bound differs by a cell
        /// depending on which side the obstacle is. An axis is only constrained
        /// when the other axis could overlap at all: two rooms whose centres are
        /// a full envelope span apart on one axis can never meet, whatever they
        /// do on the other, so a distant placement clamps nothing.
        /// </remarks>
        private static void ClampRoomSizeToPlacedRooms(
            IReadOnlyList<RoomFootprint> placedRooms,
            int nodeIndex,
            Vector2Int center,
            DungeonPatternSpatialSettings spatial,
            ref int widthCells,
            ref int depthCells)
        {
            if (placedRooms == null)
            {
                return;
            }

            int radius = spatial.roomEnvelopeRadiusCells;
            for (int room = 0; room < placedRooms.Count; room++)
            {
                RoomFootprint placed = placedRooms[room];
                if (room == nodeIndex || placed == null)
                {
                    continue;
                }

                // This room cannot leave its own envelope, so an obstacle
                // beyond that reach on an axis cannot meet it on that axis.
                RectInt bounds = placed.bounds;
                bool couldMeetOnY = bounds.yMin <= center.y + radius &&
                    bounds.yMax - 1 >= center.y - radius;
                bool couldMeetOnX = bounds.xMin <= center.x + radius &&
                    bounds.xMax - 1 >= center.x - radius;
                if (couldMeetOnY)
                {
                    if (bounds.xMin > center.x)
                    {
                        // ceil(w/2) <= bounds.xMin - center.x
                        widthCells = Mathf.Min(widthCells, (bounds.xMin - center.x) * 2);
                    }
                    else if (bounds.xMax <= center.x)
                    {
                        // floor(w/2) <= center.x - bounds.xMax
                        widthCells = Mathf.Min(widthCells, (center.x - bounds.xMax) * 2 + 1);
                    }
                }

                if (couldMeetOnX)
                {
                    if (bounds.yMin > center.y)
                    {
                        depthCells = Mathf.Min(depthCells, (bounds.yMin - center.y) * 2);
                    }
                    else if (bounds.yMax <= center.y)
                    {
                        depthCells = Mathf.Min(depthCells, (center.y - bounds.yMax) * 2 + 1);
                    }
                }
            }
        }

        /// <summary>
        /// The lane gaps immediately either side of a node, on each axis.
        /// </summary>
        /// <remarks>
        /// Every node in one lattice lane shares an embedded coordinate, so the
        /// nearest DIFFERENT coordinate on an axis is exactly the distance to
        /// the adjacent occupied lane. Any other node is either in this node's
        /// own lane — where the other axis has to do the separating — or at
        /// least that far away, so clamping each room to its own adjacent gap
        /// keeps every pair apart while letting a room beside a wide lane
        /// actually use it. An axis with no other lane is unconstrained by a
        /// neighbour and falls back to the pitch.
        /// </remarks>
        private static void ResolveAdjacentLaneGaps(
            IReadOnlyList<Vector2Int> nodeCenters,
            int nodeIndex,
            DungeonPatternSpatialSettings spatial,
            out int widthCells,
            out int depthCells)
        {
            Vector2Int center = nodeCenters[nodeIndex];
            widthCells = int.MaxValue;
            depthCells = int.MaxValue;
            foreach (Vector2Int other in nodeCenters)
            {
                int dx = Mathf.Abs(other.x - center.x);
                int dy = Mathf.Abs(other.y - center.y);
                if (dx > 0)
                {
                    widthCells = Mathf.Min(widthCells, dx);
                }

                if (dy > 0)
                {
                    depthCells = Mathf.Min(depthCells, dy);
                }
            }

            if (widthCells == int.MaxValue)
            {
                widthCells = spatial.horizontalPitchCells;
            }

            if (depthCells == int.MaxValue)
            {
                depthCells = spatial.verticalPitchCells;
            }
        }

        // Role -> size class is authored in the profile, so a topology may invent
        // a role name without it silently becoming a hall. An unmapped role is
        // rejected by TryValidateRouteIntent long before this runs; reaching it
        // here means the map and the graph disagree, which is a bug, not a shape.
        private static DungeonRoomSizeClass RequireRoomSizeClass(string role)
        {
            if (CurrentGenerationSettings.Validated().TryResolveRoomSizeClass(
                    role,
                    out DungeonRoomSizeClass sizeClass))
            {
                return sizeClass;
            }

            throw new InvalidOperationException(
                $"[ROUTE_ROOM_SIZE] role '{role}' has no size class in the active generation profile");
        }

        private static DungeonRoomSizeRange RoomSizeRangeForRole(
            DungeonPatternSpatialSettings spatial,
            string role)
        {
            return spatial.RoomSizeForClass(RequireRoomSizeClass(role));
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
                    // Two overlook rooms closer than an envelope span would grow
                    // their appendages into each other, so they meet at the
                    // shared boundary instead. This used to be gated on the
                    // processional pattern id; the distance test is the actual
                    // condition, and it holds for any topology.
                    if (Mathf.Abs(delta.x) < spatial.roomEnvelopeRadiusCells * 2 + 1)
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
                    if (Mathf.Abs(delta.y) < spatial.roomEnvelopeRadiusCells * 2 + 1)
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

        /// <summary>
        /// The vista lane, taken from node centres BEFORE any room is inflated.
        /// </summary>
        /// <remarks>
        /// Vista reservation used to run after inflation and derive its lane from
        /// the two rooms' faces, which worked only because inflation happened to
        /// leave void lying around it (density-scale design §3). At any real
        /// density there is none, so the lane is now claimed first and rooms
        /// inflate around it: no room outside the vista pair may enter the lane
        /// at all, and the pair itself is capped so it leaves the required clear
        /// run between them. <see cref="TryReserveProcessionalVista"/> still
        /// derives the exact reservation from the resulting faces — it is the
        /// same rule, now guaranteed rather than lucky.
        /// </remarks>
        private static bool TryPlanVistaLane(
            RouteIntent intent,
            IReadOnlyList<Vector2Int> nodeCenters,
            out HashSet<Vector2Int> laneCells,
            out string rejectionReason)
        {
            laneCells = new HashSet<Vector2Int>();
            rejectionReason = string.Empty;
            Vector2Int sourceCenter = nodeCenters[intent.vista.sourceNode];
            Vector2Int targetCenter = nodeCenters[intent.vista.targetNode];
            Vector2Int delta = targetCenter - sourceCenter;
            if (delta.x != 0 && delta.y != 0 || delta == Vector2Int.zero)
            {
                rejectionReason = $"vista endpoints were not cardinally aligned ({sourceCenter}->{targetCenter})";
                return false;
            }

            var step = new Vector2Int(Math.Sign(delta.x), Math.Sign(delta.y));
            for (Vector2Int cursor = sourceCenter + step; cursor != targetCenter; cursor += step)
            {
                laneCells.Add(cursor);
            }

            return true;
        }

        // The vista pair own their own ends of the lane; everyone else is out.
        private static bool IntrudesOnVistaLane(
            RouteIntent intent,
            int nodeIndex,
            RoomFootprint candidate,
            HashSet<Vector2Int> vistaLaneCells)
        {
            if (nodeIndex == intent.vista.sourceNode || nodeIndex == intent.vista.targetNode)
            {
                return false;
            }

            foreach (Vector2Int cell in candidate.cells)
            {
                if (vistaLaneCells.Contains(cell))
                {
                    return true;
                }
            }

            return false;
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
            // A corridor cell is owned by exactly one connection: two connections
            // sharing a cell would both try to level it, and one of them climbs.
            // That invariant is true at every density (design §3.1). Keeping the
            // claim set SEPARATE from floorCells is what makes it enforceable by
            // construction — the old rule tested the path against floorCells,
            // which meant it could only say "something is already here" and had
            // to abort the whole layout attempt to say it.
            var claimedCorridorCells = new HashSet<Vector2Int>();
            lastCorridorRungCounts.Clear();
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

                List<Vector2Int> accepted = null;
                string lastCandidateFailure = "no candidate was produced";
                foreach ((string rung, List<Vector2Int> candidate) in EnumerateCorridorCandidates(
                             edge,
                             fromRoom,
                             toRoom,
                             pathStart,
                             pathEnd,
                             delta,
                             fromAuthored || toAuthored,
                             fromAuthored))
                {
                    if (!TryClaimCorridor(
                            candidate,
                            rooms,
                            edge,
                            fromRoom,
                            toRoom,
                            claimedCorridorCells,
                            reservedVistaCells,
                            out string candidateFailure))
                    {
                        lastCandidateFailure = $"{rung}: {candidateFailure}";
                        continue;
                    }

                    accepted = candidate;
                    lastCorridorRungCounts.TryGetValue(rung, out int rungCount);
                    lastCorridorRungCounts[rung] = rungCount + 1;
                    break;
                }

                if (accepted == null)
                {
                    rejectionReason =
                        $"edge '{edge.id}' exhausted its corridor candidates ({lastCandidateFailure})";
                    return false;
                }

                AddPathCells(floorCells, accepted);
                foreach (Vector2Int cell in accepted)
                {
                    if (!fromRoom.Contains(cell) && !toRoom.Contains(cell))
                    {
                        claimedCorridorCells.Add(cell);
                    }
                }

                // The declared node levels are absolute and are known here, at
                // claim time, long before the level field exists — which is the
                // property that makes the band usable by the exclusivity rule
                // Phase D relaxes. `relativeElevationLevels` is absolute despite
                // its name: TryAssignRoomLevels copies it straight into
                // zoneLevels after checking it against [0, MaxGeneratedLevel].
                connections.Add(RoomConnection.ForRouteEdge(
                    edge.fromNode,
                    edge.toNode,
                    edge.id,
                    LevelBand.SpanningEndpoints(
                        intent.nodes[edge.fromNode].relativeElevationLevels,
                        intent.nodes[edge.toNode].relativeElevationLevels),
                    accepted));
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

        // Tried in this order, and deterministically: no stream is drawn, so
        // which rung an edge lands on is a property of the geometry alone.
        private static readonly int[] CorridorLateralOffsets = { 1, -1, 2, -2 };

        /// <summary>
        /// The candidate ladder for one edge's corridor (design §3.1).
        /// </summary>
        /// <remarks>
        /// The invariant being protected is corridor ownership, and the old
        /// mechanism enforced it by rejection: one grazing corridor aborted the
        /// entire layout attempt. That is rare enough not to matter at density 0
        /// and becomes the dominant failure mode from density 2, because short
        /// corridors in a packed lattice collide constantly. So each edge now
        /// gets alternatives before anything is abandoned.
        /// </remarks>
        private static IEnumerable<(string rung, List<Vector2Int> path)> EnumerateCorridorCandidates(
            RouteTraversalIntent edge,
            RoomFootprint fromRoom,
            RoomFootprint toRoom,
            Vector2Int pathStart,
            Vector2Int pathEnd,
            Vector2Int delta,
            bool authoredPort,
            bool fromAuthored)
        {
            var axis = new Vector2Int(Math.Sign(delta.x), Math.Sign(delta.y));

            // Rung 1: abutting rooms take a DOORWAY, not a corridor — zero
            // exterior cells, the connection is the two facing cells. Only a
            // level edge may: a stair needs its run plus a landing at each end
            // and cannot fit inside a shared wall, and the transition placer
            // requires three path cells before it will even look.
            if (!authoredPort &&
                edge.transitionKind == RouteTransitionKind.LevelCorridor &&
                TryFindAbuttingDoorway(fromRoom, toRoom, axis, pathStart, out List<Vector2Int> doorway))
            {
                yield return ("doorway", doorway);
            }

            // Rung 2: the straight centre-to-centre path — today's behaviour,
            // and still the first thing tried for every edge that is not already
            // touching its neighbour.
            yield return (
                "straight",
                authoredPort
                    ? BuildRecipePortPath(pathStart, pathEnd, delta, fromAuthored)
                    : BuildStraightCardinalPath(pathStart, pathEnd));

            // Rung 3: the same path offset laterally. Both rooms still contain
            // their node centre, so the anchor rule holds and only the derived
            // threshold moves. Recipe-port edges are excluded — their approach
            // depth is authored, and offsetting them fails by contract
            // (RECIPE_PORT_APPROACH), which is measured, not assumed.
            if (authoredPort)
            {
                yield break;
            }

            foreach (int offset in CorridorLateralOffsets)
            {
                yield return (
                    offset > 0 ? $"offset+{offset}" : $"offset{offset}",
                    BuildLaterallyOffsetCardinalPath(pathStart, pathEnd, offset));
            }
        }

        // Everything that makes a candidate unusable, as a reroute reason rather
        // than an attempt-abort. PathCrossesThirdRoom keeps its meaning exactly
        // — a corridor must not punch through an unrelated room, creating an
        // undeclared doorway and an unowned threshold — it just no longer kills
        // the layout when a different route around exists.
        private static bool TryClaimCorridor(
            IReadOnlyList<Vector2Int> path,
            IReadOnlyList<RoomFootprint> rooms,
            RouteTraversalIntent edge,
            RoomFootprint fromRoom,
            RoomFootprint toRoom,
            HashSet<Vector2Int> claimedCorridorCells,
            HashSet<Vector2Int> reservedVistaCells,
            out string failure)
        {
            if (!ValidatePathCardinality(path, out failure))
            {
                return false;
            }

            if (PathCrossesThirdRoom(path, rooms, edge.fromNode, edge.toNode))
            {
                failure = "crossed a third room";
                return false;
            }

            foreach (Vector2Int cell in path)
            {
                if (fromRoom.Contains(cell) || toRoom.Contains(cell))
                {
                    continue;
                }

                if (claimedCorridorCells.Contains(cell))
                {
                    failure = $"another connection already owns {cell}";
                    return false;
                }

                if (reservedVistaCells.Contains(cell))
                {
                    failure = $"entered vista-reserved void at {cell}";
                    return false;
                }
            }

            failure = string.Empty;
            return true;
        }

        // Two rooms abut when a cell of one is face-adjacent to a cell of the
        // other along the route axis. The doorway is placed on the pair nearest
        // the route line, so a packed floorplan still reads as a processional
        // route rather than as a door in an arbitrary corner.
        private static bool TryFindAbuttingDoorway(
            RoomFootprint fromRoom,
            RoomFootprint toRoom,
            Vector2Int axis,
            Vector2Int pathStart,
            out List<Vector2Int> doorway)
        {
            doorway = null;
            if (axis == Vector2Int.zero)
            {
                return false;
            }

            bool alongX = axis.x != 0;
            int preferredTransverse = alongX ? pathStart.y : pathStart.x;
            Vector2Int best = default;
            int bestDistance = int.MaxValue;
            foreach (Vector2Int cell in fromRoom.CellsRowMajor())
            {
                Vector2Int facing = cell + axis;
                if (!toRoom.Contains(facing))
                {
                    continue;
                }

                int transverse = alongX ? cell.y : cell.x;
                int distance = Mathf.Abs(transverse - preferredTransverse);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = cell;
                }
            }

            if (bestDistance == int.MaxValue)
            {
                return false;
            }

            doorway = new List<Vector2Int> { best, best + axis };
            return true;
        }

        // Proven by the phase 3 spike (design §3.1 candidate 3, §9): forcing this
        // on every generic corridor left the 200-seed corpus at 199/200 accepted
        // and 199 hard-valid, indistinguishable from straight corridors.
        private static List<Vector2Int> BuildLaterallyOffsetCardinalPath(
            Vector2Int start,
            Vector2Int end,
            int offset)
        {
            Vector2Int delta = end - start;
            var axis = new Vector2Int(Math.Sign(delta.x), Math.Sign(delta.y));
            if (axis == Vector2Int.zero || (delta.x != 0 && delta.y != 0))
            {
                return BuildStraightCardinalPath(start, end);
            }

            var lateral = new Vector2Int(-axis.y, axis.x) * (offset > 0 ? 1 : -1);
            int reach = Mathf.Abs(offset);
            var path = new List<Vector2Int> { start };
            Vector2Int cursor = start;
            for (int step = 0; step < reach; step++)
            {
                cursor += lateral;
                path.Add(cursor);
            }

            Vector2Int alignedEnd = end + lateral * reach;
            while (cursor != alignedEnd)
            {
                cursor += axis;
                path.Add(cursor);
            }

            for (int step = 0; step < reach; step++)
            {
                cursor -= lateral;
                path.Add(cursor);
            }

            return path;
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

        // The shape attempt joins the stream key the way the tier attempt does,
        // so attempt k cannot be perturbed by how many attempts preceded it.
        // Attempt 0 keeps the unsuffixed purpose on purpose: a retry that never
        // happens must not re-phase every stream in the generator.
        private static string ShapeAttemptPurpose(string purpose, int shapeAttempt)
        {
            return shapeAttempt == 0 ? purpose : $"{purpose}#shape{shapeAttempt}";
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

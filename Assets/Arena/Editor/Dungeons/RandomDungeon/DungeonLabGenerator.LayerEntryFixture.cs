using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DungeonLab.Editor
{
    // Phase D3 of the layered 3D topology design
    // (docs/dungeon-builder/layered-topology-design-2026-07-29.md §8.1, §8.2):
    // a bound edge resolves at its LAYER's elevation, and a slot says which
    // storey of the recipe that layer is.
    //
    // Nothing in the shipped corpus binds a layer or maps a storey, which is what
    // makes the slice output-neutral — and is also why the capability needs a
    // fixture. No content reaches it until D5, so without this the first exercise
    // of the entry level would be the first topology to use it, which is the
    // arrangement that cost C2 two rejected corpora.
    internal sealed partial class DungeonLabGenerator
    {
        private sealed class LayerEntryFixture
        {
            // ---- the graph ---------------------------------------------------
            public bool probeParses;
            public string probeErrors = string.Empty;
            public bool boundEdgeCarriesStoreyElevation;

            // ---- the rise check ----------------------------------------------
            // TryAssignRoomLevels, one variable apart: the SAME two nodes and the
            // SAME declared rise, with and without the binding.
            public bool boundRiseAccepted;
            public bool unboundRiseRejected;
            public string unboundRiseFailure = string.Empty;

            // ---- the entry level ---------------------------------------------
            public int nodeLevel;
            public int storeyLevel;
            public int unboundEntryLevel;
            public int boundEntryLevel;
            public bool unboundEndSitsAtZoneLevel;
            public bool boundEndSitsAtStorey;
            public int layerOffsetEnds;
            public bool loopEndsSitAtZoneLevels;

            // ---- the delta gate ----------------------------------------------
            public bool deltaGateAcceptsBound;
            public bool deltaGateRejectsUnbound;
            public string unboundDeltaFailure = string.Empty;

            // ---- the resolved corridor ---------------------------------------
            // One room, two corridors, two elevations — through the real
            // TryResolveConnectionTransition, not a re-implementation of it.
            public bool baseCorridorResolved;
            public bool galleryCorridorResolved;
            public string corridorFailure = string.Empty;
            public int roomFloorLevel;
            public int baseCorridorCellLevel;
            public int galleryCorridorCellLevel;
            public bool oneRoomMetAtTwoElevations;
            public bool roomFloorUntouched;

            // ---- the slot's storey mapping -----------------------------------
            public bool unmappedSlotAccepted;
            public bool agreeingMapAccepted;
            public bool disagreeingMapRejected;
            public bool undeclaredRecipeLayerRejected;
            public bool boundPortOnMappedStoreyAccepted;
            public bool boundPortOnBaseRejected;
            public bool unmappedBindingRejected;
            public bool socketOnStoreyRejected;

            // ---- the rule's wiring, on real catalog content -------------------
            public bool episodeLoaded;
            public bool episodeMismatchRejectedByCandidateGate;
            public string episodeMismatchReason = string.Empty;
            public bool episodeAgreementPassesLayerRule;
        }

        /// <summary>
        /// Print the D3 layer-entry fixture to the editor log.
        /// </summary>
        [MenuItem("Tools/Dungeon Lab/Print Layer Entry Fixture")]
        public static void PrintLayerEntrySnapshot()
        {
            Debug.Log($"[LAYER_ENTRY_FIXTURE]\n{BuildLayerEntrySnapshot()}");
        }

        private static string BuildLayerEntrySnapshot()
        {
            LayerEntryFixture fixture = BuildLayerEntryFixture();
            return string.Join("\n", new[]
            {
                $"graph.probeParses={fixture.probeParses}",
                $"graph.probeErrors=\"{fixture.probeErrors}\"",
                $"graph.boundEdgeCarriesStoreyElevation={fixture.boundEdgeCarriesStoreyElevation}",
                $"rise.boundAccepted={fixture.boundRiseAccepted}",
                $"rise.unboundRejected={fixture.unboundRiseRejected}",
                $"rise.unboundFailure=\"{fixture.unboundRiseFailure}\"",
                $"entry.nodeLevel={fixture.nodeLevel}",
                $"entry.storeyLevel={fixture.storeyLevel}",
                $"entry.unboundEnd={fixture.unboundEntryLevel}" +
                    $" (sitsAtZoneLevel {fixture.unboundEndSitsAtZoneLevel})",
                $"entry.boundEnd={fixture.boundEntryLevel}" +
                    $" (sitsAtStorey {fixture.boundEndSitsAtStorey})",
                $"entry.layerOffsetEnds={fixture.layerOffsetEnds}",
                $"entry.loopEndsSitAtZoneLevels={fixture.loopEndsSitAtZoneLevels}",
                $"delta.acceptsBound={fixture.deltaGateAcceptsBound}",
                $"delta.rejectsUnbound={fixture.deltaGateRejectsUnbound}",
                $"delta.unboundFailure=\"{fixture.unboundDeltaFailure}\"",
                $"corridor.baseResolved={fixture.baseCorridorResolved}",
                $"corridor.galleryResolved={fixture.galleryCorridorResolved}",
                $"corridor.failure=\"{fixture.corridorFailure}\"",
                $"corridor.roomFloorLevel={fixture.roomFloorLevel}",
                $"corridor.baseCorridorCellLevel={fixture.baseCorridorCellLevel}",
                $"corridor.galleryCorridorCellLevel={fixture.galleryCorridorCellLevel}",
                $"corridor.oneRoomMetAtTwoElevations={fixture.oneRoomMetAtTwoElevations}",
                $"corridor.roomFloorUntouched={fixture.roomFloorUntouched}",
                $"slot.unmappedAccepted={fixture.unmappedSlotAccepted}",
                $"slot.agreeingMapAccepted={fixture.agreeingMapAccepted}",
                $"slot.disagreeingMapRejected={fixture.disagreeingMapRejected}",
                $"slot.undeclaredRecipeLayerRejected={fixture.undeclaredRecipeLayerRejected}",
                $"slot.boundPortOnMappedStoreyAccepted={fixture.boundPortOnMappedStoreyAccepted}",
                $"slot.boundPortOnBaseRejected={fixture.boundPortOnBaseRejected}",
                $"slot.unmappedBindingRejected={fixture.unmappedBindingRejected}",
                $"slot.socketOnStoreyRejected={fixture.socketOnStoreyRejected}",
                $"wiring.episodeLoaded={fixture.episodeLoaded}",
                $"wiring.episodeMismatchRejectedByCandidateGate=" +
                    $"{fixture.episodeMismatchRejectedByCandidateGate}",
                $"wiring.episodeMismatchReason=\"{fixture.episodeMismatchReason}\"",
                $"wiring.episodeAgreementPassesLayerRule={fixture.episodeAgreementPassesLayerRule}"
            });
        }

        // ------------------------------------------------------------------
        // The probe graph
        // ------------------------------------------------------------------

        // Three rooms in a row. B declares a gallery one major rise above its own
        // level; the A-B corridor meets B on the ground and the B-C corridor
        // meets it on the gallery. Both edges are LevelCorridors on purpose —
        // every rise here comes from the BINDING, so the fixture needs no
        // reviewed stair contract and cannot rot when the stair pool changes.
        //
        // The `layers` mapping on the slot is D3's other half: `gallery` is what
        // the graph calls that elevation, `upper` is what the recipe calls it,
        // and the slot is the only place that knows both.
        private const string LayerEntryProbeJson = @"{
  ""id"": ""layer-entry-probe"",
  ""displayName"": ""Layer Entry Probe"",
  ""plannerVersion"": ""layer-entry-probe-v1"",
  ""map"": [""A  B  C""],
  ""spatial"": { ""columnGapDeltaCells"": 0, ""rowGapDeltaCells"": 0 },
  ""nodes"": {
    ""A"": [""probe-a"", ""arrival"", ""arrival"", 4, { ""main"": 0 }],
    ""B"": [""probe-b"", ""landmark"", ""aperture"", 4, { ""main"": 1 }, { ""layers"": { ""gallery"": 4 } }],
    ""C"": [""probe-c"", ""culmination"", ""culmination"", 8, { ""main"": 2 }]
  },
  ""edges"": [
    [""A"", ""B"", ""LevelCorridor""],
    [""B"", ""C"", ""LevelCorridor"", { ""fromLayer"": ""gallery"" }]
  ],
  ""slots"": [{ ""id"": ""probe-slot"", ""at"": ""B"", ""entry"": ""A-B"", ""exit"": ""B-C"",
               ""layers"": { ""gallery"": ""upper"" } }],
  ""vista"": { ""id"": ""probe-vista"", ""from"": ""C"", ""to"": ""A"", ""minVoidCells"": 3 },
  ""anchors"": { ""bottom"": ""A"", ""top"": ""C"" }
}";

        private const int LayerEntryProbeSeed = 20260801;
        private const string LayerEntryStoreyLayerId = "gallery";
        private const string LayerEntryRecipeLayerId = "upper";

        // Room footprints, laid out so the two corridors leave B through opposite
        // faces and neither crosses the other's room.
        private static readonly RoomFootprint LayerEntryWestRoom =
            RoomFootprint.FromRect(new RectInt(-6, -1, 2, 3));
        private static readonly RoomFootprint LayerEntryMiddleRoom =
            RoomFootprint.FromRect(new RectInt(-1, -1, 3, 3));
        private static readonly RoomFootprint LayerEntryEastRoom =
            RoomFootprint.FromRect(new RectInt(5, -1, 2, 3));

        /// <summary>
        /// The probe's layout: one room per node, one connection per edge, and
        /// the connections carrying whatever bindings their edges declared.
        /// </summary>
        private static DungeonLayout BuildLayerEntryLayout(RouteIntent intent, bool bindGallery)
        {
            var rooms = new List<RoomFootprint>
            {
                LayerEntryWestRoom, LayerEntryMiddleRoom, LayerEntryEastRoom
            };
            List<Vector2Int> westPath = StraightRun(new Vector2Int(-5, 0), new Vector2Int(0, 0));
            List<Vector2Int> eastPath = StraightRun(new Vector2Int(1, 0), new Vector2Int(6, 0));
            var floorCells = new HashSet<Vector2Int>();
            foreach (RoomFootprint room in rooms)
            {
                floorCells.UnionWith(room.cells);
            }

            floorCells.UnionWith(westPath);
            floorCells.UnionWith(eastPath);

            RouteTraversalIntent westEdge = intent.traversalEdges[0];
            RouteTraversalIntent eastEdge = intent.traversalEdges[1];
            var connections = new List<RoomConnection>
            {
                RoomConnection.ForRouteEdge(
                    westEdge.fromNode,
                    westEdge.toNode,
                    westEdge.id,
                    LevelBand.SpanningEndpoints(westEdge.fromAbsoluteLevel, westEdge.toAbsoluteLevel),
                    westPath,
                    westEdge.fromLayerId,
                    westEdge.toLayerId),
                RoomConnection.ForRouteEdge(
                    eastEdge.fromNode,
                    eastEdge.toNode,
                    eastEdge.id,
                    LevelBand.SpanningEndpoints(eastEdge.fromAbsoluteLevel, eastEdge.toAbsoluteLevel),
                    eastPath,
                    // The one variable: strip the binding and the SAME corridor
                    // between the SAME rooms resolves on B's ground instead.
                    bindGallery ? eastEdge.fromLayerId : string.Empty,
                    bindGallery ? eastEdge.toLayerId : string.Empty)
            };

            return new DungeonLayout(floorCells, rooms, connections);
        }

        /// <summary>
        /// The probe's route requirements, with the gallery binding optionally
        /// stripped off the B-C edge so the same graph can be run both ways.
        /// </summary>
        private static RouteTierRequirements BuildLayerEntryRequirements(
            DungeonRouteTopology topology,
            bool bindGallery)
        {
            RouteIntent intent = BuildTopologyRouteIntent(
                topology,
                LayerEntryProbeSeed,
                Array.Empty<RecipeSlotIntent>(),
                catalogDigest: string.Empty);
            if (!bindGallery)
            {
                RouteTraversalIntent bound = intent.traversalEdges[1];
                // Same id, same nodes, same DECLARED rise — the binding alone is
                // gone, so its two ends fall back to their node levels.
                intent.traversalEdges[1] = new RouteTraversalIntent(
                    bound.id,
                    bound.fromNode,
                    bound.toNode,
                    bound.requiredRiseLevels,
                    bound.transitionKind,
                    string.Empty,
                    string.Empty,
                    intent.nodes[bound.fromNode].relativeElevationLevels,
                    intent.nodes[bound.toNode].relativeElevationLevels);
            }

            return new RouteTierRequirements(
                intent,
                new RectInt(-8, -4, 17, 9),
                Array.Empty<Vector2Int>(),
                default,
                default,
                default,
                default,
                Array.Empty<Vector2Int>(),
                Array.Empty<RecipePlacement>());
        }

        // ------------------------------------------------------------------
        // The fixture
        // ------------------------------------------------------------------

        private static LayerEntryFixture BuildLayerEntryFixture()
        {
            var fixture = new LayerEntryFixture();
            fixture.probeParses = TryParseRouteTopology(
                LayerEntryProbeJson,
                "<probe>/layer-entry-probe.json",
                out DungeonRouteTopology topology,
                out List<string> errors);
            fixture.probeErrors = string.Join("; ", errors);
            if (!fixture.probeParses)
            {
                return fixture;
            }

            fixture.nodeLevel = topology.nodes[1].level;
            topology.nodes[1].TryGetAbsoluteLevel(LayerEntryStoreyLayerId, out int storeyLevel);
            fixture.storeyLevel = storeyLevel;

            // ---- the rise check ---------------------------------------------
            // B sits at 4 and C at 8, so an unbound B-C LevelCorridor resolves a
            // 4u rise and is rejected. Bound to B's gallery it leaves at 8 and
            // arrives at 8, which is what a LevelCorridor is. The DECLARED rise
            // is the same number in both legs; only the elevation the edge is
            // measured from moves.
            RouteTierRequirements boundRequirements = BuildLayerEntryRequirements(topology, true);
            RouteTierRequirements unboundRequirements = BuildLayerEntryRequirements(topology, false);
            fixture.boundEdgeCarriesStoreyElevation =
                boundRequirements.intent.traversalEdges[1].fromAbsoluteLevel == storeyLevel &&
                boundRequirements.intent.traversalEdges[1].requiredRiseLevels == 0;

            DungeonLayout boundLayout = BuildLayerEntryLayout(boundRequirements.intent, true);
            DungeonLayout unboundLayout = BuildLayerEntryLayout(unboundRequirements.intent, false);
            RoomZoneContext boundZones = RoomZoneContext.Build(boundLayout);
            RoomZoneContext unboundZones = RoomZoneContext.Build(unboundLayout);
            IReadOnlyList<ReviewedActiveStairOption> noStairs =
                Array.Empty<ReviewedActiveStairOption>();

            fixture.boundRiseAccepted = TryAssignRoomLevels(
                boundLayout,
                boundZones,
                boundRequirements,
                noStairs,
                out int[] zoneLevels,
                out _,
                out _);
            fixture.unboundRiseRejected = !TryAssignRoomLevels(
                unboundLayout,
                unboundZones,
                unboundRequirements,
                noStairs,
                out int[] unboundZoneLevels,
                out _,
                out fixture.unboundRiseFailure);
            if (!fixture.boundRiseAccepted)
            {
                return fixture;
            }

            // The unbound leg never produced levels, so give it the bound leg's
            // — they are the same three node levels either way, and the point of
            // the unbound leg from here on is the ENTRY level, not the rise.
            unboundZoneLevels = zoneLevels;

            // ---- the entry level ---------------------------------------------
            ConnectionEntryLevels boundEntry = ConnectionEntryLevels.Build(
                boundLayout,
                boundZones,
                zoneLevels,
                boundRequirements.intent);
            ConnectionEntryLevels unboundEntry = ConnectionEntryLevels.Build(
                unboundLayout,
                unboundZones,
                unboundZoneLevels,
                unboundRequirements.intent);
            unboundEntry.Resolve(1, out int unboundFromLevel, out _);
            boundEntry.Resolve(1, out int boundFromLevel, out _);
            fixture.unboundEntryLevel = unboundFromLevel;
            fixture.boundEntryLevel = boundFromLevel;
            fixture.unboundEndSitsAtZoneLevel = unboundFromLevel == zoneLevels[1];
            fixture.boundEndSitsAtStorey = boundFromLevel == storeyLevel;
            fixture.layerOffsetEnds = boundEntry.layerOffsetEnds;

            // A synthesized loop has no route edge, so it can never carry a
            // binding — its ends are the zone levels, always.
            var loopLayout = new DungeonLayout(
                new HashSet<Vector2Int>(boundLayout.floorCells),
                boundLayout.rooms,
                new List<RoomConnection>
                {
                    RoomConnection.ForSynthesizedLoop(
                        1,
                        2,
                        LevelBand.SpanningEndpoints(zoneLevels[1], zoneLevels[2]),
                        StraightRun(new Vector2Int(1, 0), new Vector2Int(6, 0)))
                });
            ConnectionEntryLevels loopEntry = ConnectionEntryLevels.Build(
                loopLayout,
                RoomZoneContext.Build(loopLayout),
                zoneLevels,
                boundRequirements.intent);
            loopEntry.Resolve(0, out int loopFromLevel, out int loopToLevel);
            fixture.loopEndsSitAtZoneLevels =
                loopFromLevel == zoneLevels[1] &&
                loopToLevel == zoneLevels[2] &&
                loopEntry.layerOffsetEnds == 0;

            // ---- the delta gate ----------------------------------------------
            // No reviewed stair contracts at all, so any nonzero delta is
            // rejected by name. Bound, the delta is 0 and the gate never asks.
            fixture.deltaGateAcceptsBound = TryValidateConnectedRoomLevelDeltas(
                boundLayout,
                boundEntry,
                noStairs,
                out _);
            fixture.deltaGateRejectsUnbound = !TryValidateConnectedRoomLevelDeltas(
                unboundLayout,
                unboundEntry,
                noStairs,
                out fixture.unboundDeltaFailure);

            // ---- the resolved corridor ---------------------------------------
            ResolveLayerEntryCorridors(fixture, boundLayout, boundZones, zoneLevels, boundRequirements, boundEntry);

            // ---- the slot's storey mapping -----------------------------------
            CheckLayerEntrySlotRules(fixture, topology, boundRequirements.intent);
            CheckLayerEntryCandidateGateWiring(fixture, topology);
            return fixture;
        }

        /// <summary>
        /// Both corridors, resolved through the REAL
        /// <see cref="TryResolveConnectionTransition"/>, so the claim being made
        /// is about the generator rather than about a re-statement of it.
        /// </summary>
        private static void ResolveLayerEntryCorridors(
            LayerEntryFixture fixture,
            DungeonLayout layout,
            RoomZoneContext zones,
            int[] zoneLevels,
            RouteTierRequirements requirements,
            ConnectionEntryLevels entryLevels)
        {
            var surfaces = new SurfaceField(new Dictionary<Vector2Int, int>());
            for (int roomIndex = 0; roomIndex < layout.rooms.Count; roomIndex++)
            {
                foreach (Vector2Int cell in layout.rooms[roomIndex].CellsRowMajor())
                {
                    if (!surfaces.TrySetFloorLevel(
                            cell,
                            zoneLevels[zones.NodeOfCell(roomIndex, cell)],
                            out string reason))
                    {
                        throw new InvalidOperationException($"D3 fixture room write failed: {reason}");
                    }
                }
            }

            var transitions = new List<ElevationEdgeModel.TransitionEdge>();
            var transitionKeys = new HashSet<string>();
            var ledger = new PrismLedger();
            var stairCandidateCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var doorwayCells = new HashSet<Vector2Int>();
            var spanGapCells = new HashSet<Vector2Int>();
            var synthesized = new List<(string, ElevationEdgeModel.SynthesizedStairSetPiece)>();
            var resolved = new List<RouteTransitionResolution>();
            var rng = new DungeonRandomScope(LayerEntryProbeSeed, 0, 0);
            string seamStairPrefabPath = ResolveSeamStairPrefabPath();

            bool ResolveOne(int index)
            {
                return TryResolveConnectionTransition(
                    layout.connections[index],
                    index,
                    layout,
                    zones,
                    entryLevels,
                    requirements,
                    Array.Empty<ReviewedActiveStairOption>(),
                    LayerEntryProbeSeed,
                    rng,
                    surfaces,
                    transitions,
                    transitionKeys,
                    ledger,
                    stairCandidateCounts,
                    doorwayCells,
                    spanGapCells,
                    synthesized,
                    resolved,
                    seamStairPrefabPath,
                    out fixture.corridorFailure);
            }

            fixture.baseCorridorResolved = ResolveOne(0);
            fixture.galleryCorridorResolved = ResolveOne(1);
            if (!fixture.baseCorridorResolved || !fixture.galleryCorridorResolved)
            {
                return;
            }

            // The middle room's own floor, the cell the ground-side corridor
            // wrote, and the cell the gallery-side corridor wrote.
            surfaces.TryGetFloorLevel(new Vector2Int(0, 0), out fixture.roomFloorLevel);
            surfaces.TryGetFloorLevel(new Vector2Int(-3, 0), out fixture.baseCorridorCellLevel);
            surfaces.TryGetFloorLevel(new Vector2Int(3, 0), out fixture.galleryCorridorCellLevel);
            fixture.oneRoomMetAtTwoElevations =
                fixture.baseCorridorCellLevel == zoneLevels[1] &&
                fixture.galleryCorridorCellLevel == fixture.storeyLevel &&
                fixture.baseCorridorCellLevel != fixture.galleryCorridorCellLevel;
            // A corridor bound to a storey must not RE-LEVEL the room it meets:
            // the gallery is the recipe's to build (D5), and the entry level says
            // where the corridor arrives, not what the room is.
            fixture.roomFloorUntouched = fixture.roomFloorLevel == zoneLevels[1];
        }

        // ------------------------------------------------------------------
        // The slot's storey mapping
        // ------------------------------------------------------------------

        private static DungeonRecipeAsset BuildLayerEntryProbeRecipe(
            int upperRelativeLevel,
            string entryPortLayerId,
            string exitPortLayerId,
            bool incidentSockets = false)
        {
            var recipe = ScriptableObject.CreateInstance<DungeonRecipeAsset>();
            recipe.recipeId = "layer_entry_probe_recipe";
            recipe.layers = new[]
            {
                new DungeonRecipeLayer { layerId = "base", relativeLevel = 0, isBase = true },
                new DungeonRecipeLayer
                {
                    layerId = LayerEntryRecipeLayerId,
                    relativeLevel = upperRelativeLevel,
                    isBase = false
                }
            };
            recipe.ports = new[]
            {
                new DungeonRecipePort { id = "entry", layerId = entryPortLayerId },
                new DungeonRecipePort { id = "exit", layerId = exitPortLayerId }
            };
            recipe.portBindingMode = incidentSockets
                ? DungeonRecipePortBindingMode.IncidentCardinalSockets
                : DungeonRecipePortBindingMode.ExactNamedPorts;
            return recipe;
        }

        /// <summary>
        /// The D3 rule in isolation, eleven-odd cases each one variable from its
        /// neighbour — the same shape as D2's claim-rule block, and for the same
        /// reason: the whole-candidate gate would need a contract-valid synthetic
        /// recipe, which measures the contract validator rather than this rule.
        /// </summary>
        private static void CheckLayerEntrySlotRules(
            LayerEntryFixture fixture,
            DungeonRouteTopology topology,
            RouteIntent intent)
        {
            const int slotNode = 1;
            RouteTopologySlotLayer[] mapped = topology.slots[0].layers;
            RouteTopologySlotLayer[] unmapped = Array.Empty<RouteTopologySlotLayer>();
            var entryBinding = new RecipePortBinding("entry", intent.traversalEdges[0].id);
            var exitBinding = new RecipePortBinding("exit", intent.traversalEdges[1].id);
            var boundPortBindings = new[] { entryBinding, exitBinding };

            bool Check(
                DungeonRecipeAsset recipe,
                RouteTopologySlotLayer[] layerBindings,
                RecipePortBinding[] portBindings,
                bool incidentSockets,
                out string reasonCode)
            {
                try
                {
                    return TryValidateSlotLayerBindings(
                        intent,
                        slotNode,
                        recipe,
                        portBindings,
                        layerBindings,
                        incidentSockets,
                        out reasonCode);
                }
                finally
                {
                    DestroyImmediate(recipe);
                }
            }

            // The shipped shape: no mapping, both ports on the base, and the one
            // bound edge... is still bound, because the exit edge binds `gallery`
            // in this probe. So the unmapped case has to use a graph whose edges
            // bind nothing, which is the entry edge on both ports.
            var baseOnlyBindings = new[]
            {
                new RecipePortBinding("entry", intent.traversalEdges[0].id),
                new RecipePortBinding("exit", intent.traversalEdges[0].id)
            };
            fixture.unmappedSlotAccepted = Check(
                BuildLayerEntryProbeRecipe(4, "base", "base"),
                unmapped,
                baseOnlyBindings,
                false,
                out _);

            // The agreement rule: the recipe's `upper` must sit where the graph's
            // `gallery` does. Both legs below are the same recipe with one number
            // changed.
            fixture.agreeingMapAccepted = Check(
                BuildLayerEntryProbeRecipe(4, "base", LayerEntryRecipeLayerId),
                mapped,
                boundPortBindings,
                false,
                out _);
            fixture.disagreeingMapRejected = !Check(
                BuildLayerEntryProbeRecipe(8, "base", LayerEntryRecipeLayerId),
                mapped,
                boundPortBindings,
                false,
                out string disagreementReason) &&
                string.Equals(disagreementReason, "LAYER_BINDING_LEVEL_MISMATCH", StringComparison.Ordinal);

            DungeonRecipeAsset noUpper = BuildLayerEntryProbeRecipe(4, "base", "base");
            noUpper.layers = new[]
            {
                new DungeonRecipeLayer { layerId = "base", relativeLevel = 0, isBase = true }
            };
            fixture.undeclaredRecipeLayerRejected = !Check(
                noUpper,
                mapped,
                boundPortBindings,
                false,
                out string undeclaredReason) &&
                string.Equals(undeclaredReason, "LAYER_BINDING_UNDECLARED", StringComparison.Ordinal);

            // The port rule: the edge bound to `gallery` must meet a port on the
            // storey `gallery` maps to. Accepted and rejected are one field apart.
            fixture.boundPortOnMappedStoreyAccepted = Check(
                BuildLayerEntryProbeRecipe(4, "base", LayerEntryRecipeLayerId),
                mapped,
                boundPortBindings,
                false,
                out _);
            fixture.boundPortOnBaseRejected = !Check(
                BuildLayerEntryProbeRecipe(4, "base", "base"),
                mapped,
                boundPortBindings,
                false,
                out string portReason) &&
                string.Equals(portReason, "PORT_LAYER_MISMATCH", StringComparison.Ordinal);

            // A binding the slot never mapped has no storey to arrive on.
            fixture.unmappedBindingRejected = !Check(
                BuildLayerEntryProbeRecipe(4, "base", LayerEntryRecipeLayerId),
                unmapped,
                boundPortBindings,
                false,
                out string unmappedReason) &&
                string.Equals(unmappedReason, "PORT_LAYER_UNMAPPED", StringComparison.Ordinal);

            // A socket recipe binds by DIRECTION, so nothing in the route can say
            // which storey a socket is on; keeping them on the base is the limit,
            // stated rather than assumed.
            fixture.socketOnStoreyRejected = !Check(
                BuildLayerEntryProbeRecipe(4, "base", LayerEntryRecipeLayerId, incidentSockets: true),
                mapped,
                boundPortBindings,
                true,
                out string socketReason) &&
                string.Equals(socketReason, "PORT_LAYER_MISMATCH", StringComparison.Ordinal);
        }

        private const string LayerEntryEpisodeRecipePath =
            "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/Episodes/" +
            "episode_layered_gallery_01.asset";

        /// <summary>
        /// That the rule is WIRED INTO the candidate gate, not merely present —
        /// asked of a real, contract-valid catalog recipe, because a synthetic one
        /// would be rejected by the contract validator before the layer rule was
        /// ever reached and the pass would prove nothing.
        /// </summary>
        private static void CheckLayerEntryCandidateGateWiring(
            LayerEntryFixture fixture,
            DungeonRouteTopology topology)
        {
            var episode = AssetDatabase.LoadAssetAtPath<DungeonRecipeAsset>(
                LayerEntryEpisodeRecipePath);
            fixture.episodeLoaded = episode != null;
            if (episode == null)
            {
                return;
            }

            RouteIntent intent = BuildTopologyRouteIntent(
                topology,
                LayerEntryProbeSeed,
                Array.Empty<RecipeSlotIntent>(),
                catalogDigest: string.Empty);
            const int slotNode = 1;
            var portBindings = new[]
            {
                new RecipePortBinding("entry", intent.traversalEdges[0].id),
                new RecipePortBinding("exit", intent.traversalEdges[1].id)
            };

            // The episode's `upper` sits at +4. Map the graph's `gallery` onto it
            // while declaring the gallery at +8, and the whole candidate gate must
            // say so — through TryValidateRecipeCandidate, which is the call the
            // generator makes.
            var mismatched = new[]
            {
                new RouteTopologySlotLayer(LayerEntryStoreyLayerId, LayerEntryRecipeLayerId)
            };
            fixture.episodeMismatchRejectedByCandidateGate = !TryValidateRecipeCandidate(
                MisdeclaredStoreyIntent(topology),
                slotNode,
                episode,
                RecipeOrientationBinding.RouteForward,
                portBindings,
                mismatched,
                out fixture.episodeMismatchReason) &&
                string.Equals(
                    fixture.episodeMismatchReason,
                    "LAYER_BINDING_LEVEL_MISMATCH",
                    StringComparison.Ordinal);

            // And the same mapping on the AGREEING graph clears the layer rule.
            // Deliberately asked of the rule rather than of the whole gate: the
            // episode's ports are both on its base, so the port rule below would
            // reject it, and that is correct — a room routed to on its gallery
            // needs a port there. D5's content is where that is authored.
            fixture.episodeAgreementPassesLayerRule = TryValidateSlotLayerBindings(
                intent,
                slotNode,
                episode,
                Array.Empty<RecipePortBinding>(),
                mismatched,
                incidentSockets: false,
                out _);
        }

        /// <summary>
        /// The probe graph with its gallery moved to +8, so the slot's mapping
        /// onto a recipe storey at +4 is a real disagreement.
        /// </summary>
        private static RouteIntent MisdeclaredStoreyIntent(DungeonRouteTopology topology)
        {
            RouteIntent intent = BuildTopologyRouteIntent(
                topology,
                LayerEntryProbeSeed,
                Array.Empty<RecipeSlotIntent>(),
                catalogDigest: string.Empty);
            RouteNodeIntent node = intent.nodes[1];
            intent.nodes[1] = new RouteNodeIntent(
                node.id,
                node.role,
                node.beat,
                node.mainRouteOrder,
                node.branchOrder,
                node.relativeElevationLevels,
                node.recipeSlotId,
                new[] { new RouteTopologyLayer(LayerEntryStoreyLayerId, DoubleMajorRiseLevels) });
            return intent;
        }
    }
}

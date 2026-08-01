using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    public static partial class ElevationEdgeModel
    {
        private const string PackageInventoryPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/package_inventory.json";
        private const string StairProofContractsPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/stair_proof_contracts.json";
        // Forge output (design step 6): same contract shape as the hand-authored
        // file, separate file so the reviewed hand-authored record is never touched.
        // Entries activate only after a human flips reviewStatus to "reviewed".
        private const string ForgedStairContractsPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/forged_stair_contracts.json";
        private const string StepPieceLibraryPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/step_piece_library.json";
        private const string EmbeddedStairPlacementClass = "embedded";
        private const string ExternalSpanStairPlacementClass = "externalSpan";
        // Stairwells (design decisions 26-28): 180-degree towers beside the path.
        private const string StairwellStairPlacementClass = "stairwell";
        private const string ActiveStairwellStairTopology = "stairwell";
        // Aerial decks (step 8, decisions 29-31): rise-0 flat spans.
        private const string ActiveDeckStairTopology = "deck";
        // In-room seam transition (design step 5): one rise-1 atomic step strip per
        // cell pair across a zone seam, placed from its metrology record rather than
        // a hand-authored contract.
        private const string SeamStairPlacementClass = "seam";
        // Dais rim strips (step 9, decision 37) are seam strips by construction:
        // same prefab, same rise-1 geometry, same bare-edge dressing — only the
        // placement class differs so histograms and review can tell them apart.
        private const string DaisStairPlacementClass = "dais";
        private const string ActiveStraightStairTopology = "straight";
        private const string ActiveTurningStairTopology = "turning";
        private const string PackageAssetRoot = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/";
        private const float CellSize = 4f;
        // One masonry course is 2u of world height — the pack's smallest wall/cap
        // denomination. Since the 1u level-quantum recalibration, wall drops are
        // composed from whole courses by world height instead of one course per
        // level. Odd 1u remainders need the wall-denomination change-making work
        // (stair-forge design step 4) and cannot occur while the generator emits
        // only even levels.
        private const float WallCourseHeight = 2f;
        // Magnificence decision C (2026-06-15, the underworld): void-edge cliff
        // faces drop to a single shared abyss base AbyssDepthLevels u-levels below
        // the dungeon's lowest floor, instead of bottoming at y=0. The whole
        // dungeon then reads as a unified mass rising from a deep plinth (gold is
        // "built over a void" — 84% of its floor edges face open air, walls drop
        // 16-34u). Cosmetic mass only; applies to every void edge (user choice).
        // Phase E re-derived this against the 40u route envelope: skirt depth is
        // measured below the LOWEST floor, not from the topology ceiling, so the
        // existing 20u exterior drop keeps the authored underworld silhouette.
        // A 40u plan gains height above it; it does not move the lethal datum.
        // Internal so nav/collision probes use the renderer's one value.
        internal const int AbyssDepthLevels = 20;

        internal static int AbyssBaseForMinFloor(int minimumFloorLevel)
        {
            return minimumFloorLevel - AbyssDepthLevels;
        }
        // The primary straight stair climbs one legacy tier = 2 u-levels (2u world).
        private const int PrimaryStairRiseLevels = 2;
        private const int StairRiseVariant = 3;
        private const string StairRunName = "P_MOD_Stairs_01_E_straight_3";
        private const string StairSideRailingName = "P_MOD_Stairs_01_Railing_3";
        private const string StairSideColumnName = "P_MOD_Railing_01_column";
        private const string FloorName = "P_MOD_Floor_01_O_straight_med";
        // §0.1, measured: the `_E_` family is the `_O_` family plus a bottom
        // face — a closed slab, 4u x 0.5u x 4u, hanging entirely BELOW the walk
        // surface. A ground-backed floor never shows its underside so it keeps
        // the cheaper one-sided plane; a suspended surface does, and gets this.
        private const string SuspendedFloorName = "P_MOD_Floor_01_E_straight_med";
        private const string RailingName = "P_MOD_Railing_01_straight";
        private const string RailingColumnName = "P_MOD_Railing_01_column";
        private const string AuthoredFlatRailingModuleName = "LVL_01_O_rail_straight_S";
        private const string PartitionWallMediumName = "COMP_Wall_01_M_straight_med";
        private const string PartitionWallLargeName = "COMP_Wall_01_M_straight_large";
        private const string PartitionCornerMediumName = "COMP_Wall_01_M_corner_med";
        private const string PartitionCornerLargeName = "COMP_Wall_01_M_corner_large";
        private const string GatewayMediumMetalName = "COMP_Door_01_med_01";
        private const string GatewayLargeWallMetalName = "COMP_Door_01_med_01_M";
        private const string GatewayMediumWoodName = "COMP_Door_01_med_02";
        private const string GatewayLargeWallWoodName = "COMP_Door_01_med_02_M";
        private const string GatewayLargeWoodName = "COMP_Door_01_large";
        private const string GatewayBarsName = "P_PROP_bars_doorway_dungeon_01";
        private const int GatewayPlacementPercent = 100;
        private const string AuthoredLevelModuleOneSidedStairsFolder = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/03_LEVEL_MODULES/01/OneSided/Stairs/";
        private const string AuthoredCompFloorFolder = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/02_COMPS/Floor/";
        private const string AuthoredPartStairsFolder = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/01_PARTS/Stairs/";
        private const string AuthoredPartStairsMeshFolder = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/3d/modular/Stairs/Stairs/";
        private const string AuthoredPartFloorFolder = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/01_PARTS/Floor/";
        private const float ReviewedVisualAnchorTolerance = 0.05f;

        private static readonly string[] DropFaceCandidateNames =
        {
            "COMP_Wall_01_O_straight_small",
            "P_MOD_Wall_01_O_straight_small",
            "P_MOD_Base_01_straight_small",
            "COMP_Wall_01_O_straight_med",
            "P_MOD_Wall_01_O_straight_med",
            "P_MOD_Base_01_straight_med",
            "COMP_Wall_01_O_straight_large",
            "P_MOD_Wall_01_O_straight_large",
            "P_MOD_Base_01_straight_large"
        };

        private static readonly string[] CornerCandidateNames =
        {
            "COMP_Wall_01_O_corner_small",
            "P_MOD_Wall_01_O_corner_small",
            "COMP_Wall_01_O_corner_med",
            "P_MOD_Wall_01_O_corner_med",
            "COMP_Wall_01_O_corner_large",
            "P_MOD_Wall_01_O_corner_large"
        };

        private static readonly int[] CardinalDirections =
        {
            Direction.North,
            Direction.East,
            Direction.South,
            Direction.West
        };

        public static GameObject BuildLevelField(
            Vector3 origin,
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<TransitionEdge> transitions,
            string rootName,
            out BuildReport report,
            out Bounds bounds)
        {
            return BuildLevelField(origin, levels, transitions, null, null, rootName, out report, out bounds);
        }

        public static GameObject BuildLevelField(
            Vector3 origin,
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<TransitionEdge> transitions,
            RoomBoundaryContext roomBoundaryContext,
            string rootName,
            out BuildReport report,
            out Bounds bounds)
        {
            return BuildLevelField(origin, levels, transitions, null, roomBoundaryContext, rootName, out report, out bounds);
        }

        public static GameObject BuildLevelField(
            Vector3 origin,
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<TransitionEdge> transitions,
            IReadOnlyCollection<Vector2Int> reservedSetPieceCells,
            RoomBoundaryContext roomBoundaryContext,
            string rootName,
            out BuildReport report,
            out Bounds bounds)
        {
            return BuildLevelField(
                origin,
                levels,
                transitions,
                reservedSetPieceCells,
                null,
                roomBoundaryContext,
                rootName,
                out report,
                out bounds);
        }

        public static GameObject BuildLevelField(
            Vector3 origin,
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<TransitionEdge> transitions,
            IReadOnlyCollection<Vector2Int> reservedSetPieceCells,
            IReadOnlyCollection<OpenFloorEdge> plannedOpenEdges,
            RoomBoundaryContext roomBoundaryContext,
            string rootName,
            out BuildReport report,
            out Bounds bounds)
        {
            return BuildLevelField(origin, levels, transitions, reservedSetPieceCells, plannedOpenEdges, roomBoundaryContext, null, rootName, out report, out bounds);
        }

        // Decision J: promontoryCells render as open piers (deck on a column
        // forest over the abyss) instead of solid cliffs.
        public static GameObject BuildLevelField(
            Vector3 origin,
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<TransitionEdge> transitions,
            IReadOnlyCollection<Vector2Int> reservedSetPieceCells,
            IReadOnlyCollection<OpenFloorEdge> plannedOpenEdges,
            RoomBoundaryContext roomBoundaryContext,
            IReadOnlyCollection<Vector2Int> promontoryCells,
            string rootName,
            out BuildReport report,
            out Bounds bounds)
        {
            return BuildLevelField(
                origin,
                levels,
                transitions,
                reservedSetPieceCells,
                plannedOpenEdges,
                roomBoundaryContext,
                promontoryCells,
                TrapPlacementSettings.Disabled,
                rootName,
                out report,
                out bounds);
        }

        // Traps (design 2026-07-26) are an ADDITIVE pass: they read the finished
        // plan through subject-keyed streams and add one `Traps` root. Passing
        // TrapPlacementSettings.Disabled reproduces the pre-trap output exactly.
        public static GameObject BuildLevelField(
            Vector3 origin,
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<TransitionEdge> transitions,
            IReadOnlyCollection<Vector2Int> reservedSetPieceCells,
            IReadOnlyCollection<OpenFloorEdge> plannedOpenEdges,
            RoomBoundaryContext roomBoundaryContext,
            IReadOnlyCollection<Vector2Int> promontoryCells,
            TrapPlacementSettings trapPlacement,
            string rootName,
            out BuildReport report,
            out Bounds bounds)
        {
            return BuildLevelField(
                origin,
                levels,
                null,
                transitions,
                reservedSetPieceCells,
                plannedOpenEdges,
                roomBoundaryContext,
                promontoryCells,
                trapPlacement,
                rootName,
                out report,
                out bounds);
        }

        // C1b of the layered 3D topology design: `levels` is the column FLOOR of
        // each cell and `stackedSurfaces` is whatever stands above it. Passing
        // null or an empty collection is today's plan and takes today's path —
        // no branch anywhere is conditional on being single-layer, because the
        // stacked passes simply have nothing to iterate.
        public static GameObject BuildLevelField(
            Vector3 origin,
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyCollection<StackedSurface> stackedSurfaces,
            IReadOnlyList<TransitionEdge> transitions,
            IReadOnlyCollection<Vector2Int> reservedSetPieceCells,
            IReadOnlyCollection<OpenFloorEdge> plannedOpenEdges,
            RoomBoundaryContext roomBoundaryContext,
            IReadOnlyCollection<Vector2Int> promontoryCells,
            TrapPlacementSettings trapPlacement,
            string rootName,
            out BuildReport report,
            out Bounds bounds)
        {
            if (levels == null || levels.Count == 0)
            {
                throw new InvalidOperationException("Elevation edge model needs at least one floor cell.");
            }

            var surfaceColumns = new SurfaceColumns(levels, stackedSurfaces);
            var promontorySet = promontoryCells != null ? new HashSet<Vector2Int>(promontoryCells) : new HashSet<Vector2Int>();

            string tempRootName = $"{rootName} __Build In Progress";
            ClearExistingRoot(tempRootName);
            GameObject root = null;
            try
            {
            TieredPlatformContracts contracts = BuildContracts();
            var stats = new TieredPlatformBuildStats();
            HashSet<Vector2Int> setPieceReservedCells = BuildReservedSetPieceCellSet(reservedSetPieceCells);
            ValidateRoomBoundaryContext(roomBoundaryContext, ref stats);
            var transitionKeys = BuildTransitionKeys(levels, transitions, out HashSet<OpenEdgeKey> transitionOpenEdges, out HashSet<OpenEdgeKey> bridgeSpanEdges, out Dictionary<Vector2Int, int> aerialDeckCellLevels);
            AddPlannedOpenEdges(
                transitionOpenEdges,
                plannedOpenEdges,
                levels,
                out HashSet<(Vector2Int cell, int level, int direction)> bareStackedRims);
            StairReservationSet stairReservations = BuildStairReservations(levels, transitions, contracts, origin);
            // Stair footprints suppress floors and the railings they own; set-piece
            // reservations suppress everything. Walls always render around stairs.
            var reservedCells = new HashSet<Vector2Int>(setPieceReservedCells);
            foreach (Vector2Int cell in stairReservations.floorBlockedCells)
            {
                reservedCells.Add(cell);
            }

            root = new GameObject(tempRootName);
            if (!Application.isBatchMode)
            {
                Undo.RegisterCreatedObjectUndo(root, $"Create {rootName}");
            }
            var floorRoot = CreateChild(root.transform, "Floors");
            var wallsRoot = CreateChild(root.transform, "Elevation Edge Walls");
            var cornersRoot = CreateChild(root.transform, "Elevation Edge Corners");
            var railingsRoot = CreateChild(root.transform, "Elevation Edge Top Railings");
            var stairsRoot = CreateChild(root.transform, "Transition Stairs");
            var piersRoot = CreateChild(root.transform, "Promontory Piers");
            var shellRoot = CreateChild(root.transform, "Outer Shell Walls");
            var gatewaysRoot = CreateChild(root.transform, "Gateways");
            var trapsRoot = CreateChild(root.transform, "Traps");

            bounds = new Bounds(origin, Vector3.zero);
            bool hasBounds = false;

            List<WallEdge> wallEdges = BuildWallEdges(levels, surfaceColumns, setPieceReservedCells, stairReservations.floorBlockedCells, reservedCells, transitionKeys, transitionOpenEdges, bridgeSpanEdges, bareStackedRims, aerialDeckCellLevels, roomBoundaryContext, promontorySet, out List<RimEdge> railingOnlyEdges, ref stats);
            RawOuterShellPlan rawOuterShellPlan = BuildRawOuterShellPlan(
                levels,
                wallEdges,
                transitions,
                roomBoundaryContext,
                promontorySet);
            ApplyPartitionHeightPlan(
                wallEdges,
                roomBoundaryContext,
                rawOuterShellPlan.largeWallRooms,
                ref stats);

            // Round tier corners (step 9, decision 36): eligible cliff corners
            // swap their two straight wall faces for the gallery-approved
            // quarter-shell kit. Detection runs over the full edge list; the
            // replaced edges are removed BEFORE every consumer, so wall stacks,
            // railings, corner columns and square corner stacks all suppress by
            // construction. Convex corner cells also swap their floor for the
            // rounded variant (handled in the floor loop below).
            // The stair's own cells and landing port edges, built once: the
            // corner SELECTOR keeps those corners square, and the validator
            // below stays as the assertion that it did. Before this the two
            // disagreed — the selector skipped `reservedCells` (stair
            // floor-blocked cells) while the validator tested the wider
            // footprint set, so a corner on a tower's folded footprint threw at
            // render time. Rare while dungeons were airy; at density 5 packing
            // makes tier corners common and it hit 27 of 200 seeds.
            BuildTierCornerStairClaims(
                transitions,
                out Dictionary<Vector2Int, TransitionEdge> tierCornerFootprintOwners,
                out Dictionary<(int x, int z, int direction), TransitionEdge> tierCornerPortEdgeOwners);
            List<RoundTierCorner> roundTierCorners = FindRoundTierCorners(
                wallEdges,
                levels,
                reservedCells,
                tierCornerFootprintOwners,
                tierCornerPortEdgeOwners);
            ValidateTierCornerCompatibility(
                roundTierCorners,
                tierCornerFootprintOwners,
                tierCornerPortEdgeOwners);
            var roundCornerFloorSwap = new Dictionary<Vector2Int, RoundTierCorner>();
            if (roundTierCorners.Count > 0)
            {
                // Both kinds replace their faces wholesale: the kit carries the
                // full guard line. For concave corners the sliver floor makes
                // the CURVE the walkable edge, so the curved railing + concave
                // trim follow it (user correction 2026-06-12: an arc railing is
                // an arc railing — straight guards would stand mid-floor).
                var replacedEdges = new HashSet<(int x, int z, int direction)>();
                foreach (RoundTierCorner roundCorner in roundTierCorners)
                {
                    replacedEdges.Add(roundCorner.edgeA);
                    replacedEdges.Add(roundCorner.edgeB);
                    if (!roundCorner.concave && !roundCorner.wallOnly)
                    {
                        roundCornerFloorSwap[roundCorner.cell] = roundCorner;
                    }
                }

                wallEdges.RemoveAll(edge => replacedEdges.Contains((edge.edge.x, edge.edge.z, edge.edge.direction)));
            }
            var partialFloorCells = new HashSet<Vector2Int>();

            // Gateway sockets are derived from the surviving straight-wall
            // architecture, and a corner-owned face is NOT a candidate flank.
            // A chamfer does not stand on the two faces it claims: it deletes
            // them and spans a diagonal between their far endpoints, so those
            // two edges are exactly where no wall remains. Treating them as
            // flanks plants a free-standing arch on open floor (measured
            // 2026-07-26: gateway_wood_35_16_8 had 0 of 2 real flanks,
            // gateway_wood_36_18_1 had 1 of 2).
            //
            // An entrance framed by chamfers therefore stays BARE, on purpose.
            // Both alternatives were built and rejected on looks by the owner
            // (2026-07-26): squaring the corner to make a jamb, and moving the
            // door out to the corridor mouth the chamfer opens onto. Doors
            // beside angled walls and doors in hallways both look wrong. Do not
            // re-litigate this — the missing gateway is the desired outcome.
            GatewayWallPlan gatewayWallPlan = BuildGatewayWallPlan(
                wallEdges,
                rawOuterShellPlan);
            var unresolvedGatewayEnds = new List<string>();
            GatewaySocketPlan gatewaySocketPlan = BuildGatewaySocketPlan(
                levels,
                roomBoundaryContext,
                gatewayWallPlan,
                BuildGatewayBlockedCells(reservedCells, transitions),
                BuildGatewayBlockedPathEdgeKeys(
                    roomBoundaryContext,
                    transitionKeys,
                    transitionOpenEdges,
                    bridgeSpanEdges),
                contracts.gateways.socketWidth,
                unresolvedGatewayEnds);
            ValidateGatewayCornerPlanDisjoint(
                gatewaySocketPlan,
                roundTierCorners);
            if (unresolvedGatewayEnds.Count > 0 && !Application.isBatchMode)
            {
                // Interactive only: the 200-seed batch would drown in this, and
                // an unresolved end is a design outcome, not an error.
                Debug.Log(
                    $"[GATEWAY] {unresolvedGatewayEnds.Count} room entrance(s) took no gateway:\n  " +
                    string.Join("\n  ", unresolvedGatewayEnds));
            }

            foreach (var item in SortedCells(levels))
            {
                // Embedded stair bodies fill their cells, so their floors stay
                // suppressed; bridge decks float above their cells, so the terrain
                // floor beneath them must still render (otherwise one lane of a
                // mixed void/floor span gets a ground hole and mismatched walls).
                if (reservedCells.Contains(item.Key) &&
                    !stairReservations.bridgeFloorBlockedCells.Contains(item.Key))
                {
                    continue;
                }

                if (roundCornerFloorSwap.TryGetValue(item.Key, out RoundTierCorner cornerSwap) &&
                    TryLoadTierStepPiece(
                        cornerSwap.angleStyle ? "P_MOD_Floor_01_O_angle_med" : "P_MOD_Floor_01_O_convex_med",
                        out TierStepPiece roundFloor))
                {
                    GameObject roundFloorInstance = InstantiatePrefab(
                        roundFloor.prefabPath,
                        $"floor_{item.Key.x}_{item.Key.y}_level_{item.Value}_round",
                        floorRoot.transform,
                        DaisFullCellPivotWorld(item.Key, cornerSwap.yaw, origin) + Vector3.up * (item.Value * contracts.levelHeight),
                        cornerSwap.yaw);
                    EncapsulateInstance(roundFloorInstance, ref bounds, ref hasBounds);
                    partialFloorCells.Add(item.Key);
                    continue;
                }

                PlaceFloor(
                    contracts.floor,
                    floorRoot.transform,
                    $"floor_{item.Key.x}_{item.Key.y}_level_{item.Value}",
                    CellMin(origin, item.Key.x, item.Key.y, item.Value * contracts.levelHeight),
                    ref bounds,
                    ref hasBounds);
            }

            // §7.1 step 3, the soffit pass — and it is a prefab choice, not new
            // art or a flipped quad. Every surface above its column floor has a
            // visible underside, so it takes the `_E_` closed slab instead of
            // the `_O_` one-sided plane. The slab's bottom face is genuine
            // geometry with a genuine collider, which matters because this
            // dungeon exports movement collision AS query collision: a
            // render-only soffit would let sight pass straight through a floor.
            //
            // This is the swap's first application site. Until a suspended
            // FLOOR surface existed there was nothing to apply it to — a bridge
            // deck's walk surface is authored set-piece geometry from the
            // transition prefab, not a floor tile, which is why
            // `bridgeFloorBlockedCells` exists to keep the terrain floor under
            // it rendering.
            foreach (StackedSurface surface in surfaceColumns.AllAbove())
            {
                if (reservedCells.Contains(surface.cell) &&
                    !stairReservations.bridgeFloorBlockedCells.Contains(surface.cell))
                {
                    continue;
                }

                PlaceFloor(
                    contracts.suspendedFloor,
                    floorRoot.transform,
                    $"floor_{surface.cell.x}_{surface.cell.y}_level_{surface.level}_suspended",
                    CellMin(origin, surface.cell.x, surface.cell.y, surface.level * contracts.levelHeight),
                    ref bounds,
                    ref hasBounds);
            }

            // Decision 43(c): a room side whose guard line crosses a 1u
            // floor step takes WALL guards instead of railings — measured:
            // the pack has no railing piece that can step 1u mid-run.
            HashSet<(int roomId, int direction)> wallGuardSides = FindWallGuardSides(wallEdges, roomBoundaryContext);

            // Resolve both placed shell ownership and intentionally bare orphan
            // landing edges before ordinary guards, so neither can receive a
            // fallback railing/parapet or its trim.
            OuterShellPlacementResult shellPlacement = PlaceOuterShellWalls(
                promontorySet,
                roundTierCorners,
                wallEdges,
                roomBoundaryContext,
                rawOuterShellPlan,
                gatewaySocketPlan,
                origin,
                contracts.levelHeight,
                shellRoot.transform,
                ref bounds,
                ref hasBounds,
                ref stats);
            HashSet<(int x, int z, int direction)> shellGuardEdges = shellPlacement.guardEdges;
            HashSet<(int x, int z, int direction)> bareLandingEdges =
                shellPlacement.bareLandingEdges;

            List<GatewayPlacement> gatewayPlacements = BuildGatewayPlacements(
                levels,
                wallEdges,
                shellPlacement.gatewayFlankWallHeights,
                gatewaySocketPlan,
                roomBoundaryContext,
                contracts.gateways,
                contracts.partitions);
            foreach (GatewayPlacement gateway in gatewayPlacements)
            {
                PlaceGateway(
                    gateway,
                    gatewaysRoot.transform,
                    origin,
                    contracts.levelHeight,
                    ref bounds,
                    ref hasBounds,
                    ref stats);
            }

            stats.traps = PlaceTraps(
                trapPlacement,
                trapsRoot.transform,
                origin,
                contracts.levelHeight,
                levels,
                reservedCells,
                stairReservations,
                aerialDeckCellLevels,
                transitions,
                roomBoundaryContext,
                gatewaySocketPlan,
                promontorySet,
                partialFloorCells,
                ref bounds,
                ref hasBounds);

            foreach (WallEdge wallEdge in wallEdges)
            {
                if (wallEdge.isPartition)
                {
                    PlacePartitionWall(
                        contracts.partitions,
                        wallsRoot.transform,
                        wallEdge,
                        origin,
                        contracts.levelHeight,
                        ref bounds,
                        ref hasBounds,
                        ref stats);
                    continue;
                }

                PlaceElevationWallStack(
                    contracts.dropFaceStack,
                    wallsRoot.transform,
                    wallEdge,
                    origin,
                    contracts.levelHeight,
                    ref bounds,
                    ref hasBounds);
                if (SuppressesGeneratedTopGuard(
                        wallEdge.suppressRailing,
                        (wallEdge.edge.x, wallEdge.edge.z, wallEdge.edge.direction),
                        shellGuardEdges,
                        bareLandingEdges))
                {
                    continue;
                }

                if (TryGetRoomSide(roomBoundaryContext, wallEdge, out (int roomId, int direction) side) &&
                    wallGuardSides.Contains(side))
                {
                    // Parapets run the WHOLE side, 1u drops included (a 1u
                    // floor step alongside a walled side is acceptable).
                    PlaceParapetEdge(
                        railingsRoot.transform,
                        wallEdge.edge,
                        origin,
                        wallEdge.higherLevel * contracts.levelHeight,
                        ref bounds,
                        ref hasBounds);
                    stats.railings++;
                    continue;
                }

                if (!DropGetsRailing(wallEdge.higherLevel - wallEdge.lowerLevel))
                {
                    continue;
                }

                PlaceRailingEdge(
                    contracts.floor,
                    contracts.railings,
                    railingsRoot.transform,
                    wallEdge.edge,
                    origin,
                    wallEdge.higherLevel * contracts.levelHeight,
                    ref bounds,
                    ref hasBounds);
                stats.railings++;
            }

            // A rim the plan declared BARE is dropped here rather than at the
            // producer, so it still counts in the stats and still suppresses the
            // corner columns that accompany a railing.
            //
            // The suppression sets are the ONE place §7.1's "the edge tuples
            // gain a level discriminator" bites today, and it is a narrower
            // statement than the design makes. `shellGuardEdges` and
            // `bareLandingEdges` are keyed `(x, z, direction)` with no level,
            // and both are built from the outer shell and from stair landings —
            // heightfield things, so every edge in them describes a COLUMN
            // FLOOR. Applying them to a rim eight levels up would suppress a
            // gallery's railing because the ground floor below it happens to
            // carry a shell wall on the same face. Gating on the rim's own level
            // is exactly neutral for a single-layer plan, where every rim IS at
            // the column floor.
            railingOnlyEdges.RemoveAll(rim =>
                rim.bare ||
                (IsColumnFloorRim(levels, rim) &&
                    SuppressesGeneratedTopGuard(
                        false,
                        (rim.edge.x, rim.edge.z, rim.edge.direction),
                        shellGuardEdges,
                        bareLandingEdges)));
            foreach (RimEdge railingEdge in railingOnlyEdges)
            {
                PlaceRailingEdge(
                    contracts.floor,
                    contracts.railings,
                    railingsRoot.transform,
                    railingEdge.edge,
                    origin,
                    railingEdge.level * contracts.levelHeight,
                    ref bounds,
                    ref hasBounds);
                stats.railings++;
            }

            // Railing corner columns accompany railings, so skip every edge whose
            // generated top guard is suppressed (including bare landing edges) and
            // block columns on stair cells; cliff corner stacks accompany the walls
            // themselves, so only full set-piece reservations block those.
            var railingWallEdges = new List<WallEdge>(wallEdges.Count);
            foreach (WallEdge wallEdge in wallEdges)
            {
                if (!SuppressesGeneratedTopGuard(
                        wallEdge.suppressRailing,
                        (wallEdge.edge.x, wallEdge.edge.z, wallEdge.edge.direction),
                        shellGuardEdges,
                        bareLandingEdges) &&
                    DropGetsRailing(wallEdge.higherLevel - wallEdge.lowerLevel) &&
                    !(TryGetRoomSide(roomBoundaryContext, wallEdge, out (int roomId, int direction) cornerSide) &&
                      wallGuardSides.Contains(cornerSide)))
                {
                    railingWallEdges.Add(wallEdge);
                }
            }

            foreach (var group in GroupWallEdgesByLevel(railingWallEdges, includePartitions: false))
            {
                PlaceRailingCornerColumns(
                    contracts.railings,
                    railingsRoot.transform,
                    group.Value,
                    reservedCells,
                    origin,
                    group.Key * contracts.levelHeight,
                    ref bounds,
                    ref hasBounds);
            }

            foreach (var group in GroupRailingEdgesByLevel(railingOnlyEdges))
            {
                PlaceRailingCornerColumns(
                    contracts.railings,
                    railingsRoot.transform,
                    group.Value,
                    reservedCells,
                    origin,
                    group.Key * contracts.levelHeight,
                    ref bounds,
                    ref hasBounds);
            }

            // Decision J: render promontory piers as open decks on a column forest
            // rising from the abyss (the gold bridge_look_here composition).
            if (promontorySet.Count > 0)
            {
                PlacePromontoryPiers(
                    promontorySet,
                    levels,
                    transitionOpenEdges,
                    AbyssBaseForMinFloor(MinFloorLevel(levels)),
                    origin,
                    contracts.levelHeight,
                    piersRoot.transform,
                    ref bounds,
                    ref hasBounds,
                    ref stats);
            }

            List<CornerPlacement> cornerPlacements = BuildCornerPlacements(wallEdges, setPieceReservedCells);
            stats.corners = cornerPlacements.Count;
            foreach (CornerPlacement corner in cornerPlacements)
            {
                if (corner.isPartition)
                {
                    PlacePartitionCorner(
                        contracts.partitions,
                        cornersRoot.transform,
                        corner,
                        origin,
                        contracts.levelHeight,
                        ref bounds,
                        ref hasBounds);
                }
                else
                {
                    PlaceCliffCornerStack(
                        contracts.cornerStack,
                        cornersRoot.transform,
                        corner,
                        origin,
                        contracts.levelHeight,
                        ref bounds,
                        ref hasBounds);
                }
            }

            // Sunken dais corner cells (lower in two perpendicular dais
            // transitions) render as one concave sweep, so their strips are
            // suppressed in the loop below and placed by the corner pass.
            Dictionary<Vector2Int, Vector2Int> sunkenDaisCorners = FindSunkenDaisCorners(transitions, levels);

            foreach (TransitionEdge transition in transitions)
            {
                // The edge's OWN levels (C2b-2). Looking them up by cell here
                // meant a transition ending on a stacked surface — which is not
                // in the column-floor map at all — read as a missing cell and
                // threw, so a cross-layer stair could not be rendered.
                int firstLevel = transition.firstLevel;
                int secondLevel = transition.secondLevel;
                if (!transition.HasLevels)
                {
                    // Was a logged skip until 2026-07-25. A skipped stair can
                    // strand a whole tier, and the resulting scene was still saved
                    // and exported. A named prefab is not a reason to ship a
                    // dungeon with a missing staircase.
                    stats.rejected++;
                    stats.stairSummaries.Add("multi-rise stair rejected missing-cell");
                    throw new InvalidOperationException(
                        $"Transition edge references a missing floor cell: {transition.firstCell} <-> {transition.secondCell} " +
                        $"prefab '{transition.stairPrefabPath}'.");
                }

                // Aerial decks (decisions 29-31) are legitimate rise-0 transitions;
                // anything else at equal levels is a planning error.
                if (firstLevel == secondLevel && transition.synthesizedSetPiece == null)
                {
                    stats.rejected++;
                    stats.stairSummaries.Add("multi-rise stair rejected d0");
                    throw new InvalidOperationException(
                        $"Transition edge {transition.firstCell} <-> {transition.secondCell} has no level difference " +
                        $"(prefab '{transition.stairPrefabPath}').");
                }

                Vector2Int higherCell = firstLevel > secondLevel ? transition.firstCell : transition.secondCell;
                Vector2Int lowerCell = firstLevel > secondLevel ? transition.secondCell : transition.firstCell;
                int higherLevel = Mathf.Max(firstLevel, secondLevel);
                int lowerLevel = Mathf.Min(firstLevel, secondLevel);
                int deltaLevels = higherLevel - lowerLevel;
                bool isDaisStrip = string.Equals(transition.placementClass, DaisStairPlacementClass, StringComparison.Ordinal);
                if (string.Equals(transition.placementClass, SeamStairPlacementClass, StringComparison.Ordinal) || isDaisStrip)
                {
                    // Seam strips are rise-1 by definition; dais rims may also
                    // climb 2u with the steep strip family (decision 41).
                    if (deltaLevels != 1 && !(isDaisStrip && deltaLevels == 2))
                    {
                        throw new InvalidOperationException(
                            $"Step-strip transition ({transition.placementClass}) {higherCell}(L{higherLevel}) -> {lowerCell}(L{lowerLevel}) climbs {deltaLevels}u, which no strip family covers.");
                    }

                    // A sunken dais corner cell renders as one concave sweep
                    // instead of its two strips (gallery construction); the
                    // transitions stay for walls and the port graph.
                    if (isDaisStrip && sunkenDaisCorners.ContainsKey(lowerCell))
                    {
                        stats.transitionEdges++;
                        continue;
                    }

                    PlaceSeamStepStrip(
                        transition.stairPrefabPath,
                        higherCell,
                        lowerCell,
                        higherLevel,
                        deltaLevels,
                        origin,
                        contracts.levelHeight,
                        stairsRoot.transform,
                        ref bounds,
                        ref hasBounds,
                        stats);
                    stats.transitionEdges++;
                    continue;
                }

                GameObject stairInstance = null;
                try
                {
                    int lowerDirectionId = transition.hasPortDirections
                        ? transition.lowerPortDirection
                        : AreCardinalNeighbors(higherCell, lowerCell)
                            ? EdgeFromCellToward(higherCell, lowerCell).direction
                            : DirectionFromCellToward(higherCell, lowerCell);
                    int entryDirectionId = transition.hasPortDirections ? transition.lowerPortDirection : lowerDirectionId;
                    int exitDirectionId = transition.hasPortDirections ? transition.upperPortDirection : OppositeDirection(lowerDirectionId);
                    Vector3 stairTop = TransitionExitEdgeCenter(origin, transition, higherCell, OppositeDirection(exitDirectionId), higherLevel * contracts.levelHeight);
                    Vector2 lowerDirection = DirectionVector(entryDirectionId);
                    Vector2 upperDirection = DirectionVector(exitDirectionId);
                    ConnectionPointSetPieceContract setPiece = ResolveTransitionSetPieceContract(
                        contracts,
                        transition,
                        deltaLevels);
                    ConnectionPointPlacement connectionPlacement = CalculateConnectionPointPlacement(
                        setPiece,
                        lowerCell,
                        higherCell,
                        lowerLevel,
                        higherLevel,
                        origin,
                        contracts.levelHeight,
                        stairTop,
                        lowerDirection);
                    string stairInstanceName = deltaLevels == PrimaryStairRiseLevels
                        ? $"transition_stair_{higherCell.x}_{higherCell.y}_to_{lowerCell.x}_{lowerCell.y}"
                        : $"transition_stair_{setPiece.name}_d{deltaLevels}_{higherCell.x}_{higherCell.y}_to_{lowerCell.x}_{lowerCell.y}";
                    stairInstance = transition.synthesizedSetPiece != null
                        ? InstantiateSynthesizedSetPiece(
                            transition.synthesizedSetPiece,
                            setPiece,
                            stairInstanceName,
                            stairsRoot.transform,
                            connectionPlacement.position,
                            connectionPlacement.yRotation)
                        : InstantiateConnectionPointSetPiecePrefab(
                            setPiece,
                            stairInstanceName,
                            stairsRoot.transform,
                            connectionPlacement.position,
                            connectionPlacement.yRotation);
                    ValidateConnectionPointTransition(
                        stairInstance,
                        setPiece,
                        connectionPlacement,
                        higherCell,
                        lowerCell,
                        higherLevel,
                        lowerLevel,
                        origin,
                        contracts.levelHeight,
                        stairTop,
                        lowerDirection,
                        upperDirection);

                    if (deltaLevels == PrimaryStairRiseLevels)
                    {
                        stats.stairSummaries.Add($"stair prefab {setPiece.name} rise {deltaLevels} placed, portChecks passed");
                        Debug.Log(
                            $"Dungeon Lab Elevation Edge Model: stair prefab {setPiece.name} rise {deltaLevels} placed, portChecks passed " +
                            $"{higherCell}(L{higherLevel}) -> {lowerCell}(L{lowerLevel}).");
                    }
                    else
                    {
                        stats.multiRiseStairChecks++;
                        stats.stairSummaries.Add($"stair connector {setPiece.name} rise {deltaLevels} placed, connectionChecks passed");
                        Debug.Log(
                            $"Dungeon Lab Elevation Edge Model: stair connector {setPiece.name} rise {deltaLevels} placed, connectionChecks passed " +
                            $"{higherCell}(L{higherLevel}) -> {lowerCell}(L{lowerLevel}).");
                    }
                }
                catch (Exception exception)
                {
                    if (stairInstance != null)
                    {
                        UnityEngine.Object.DestroyImmediate(stairInstance);
                    }

                    Debug.LogError(
                        $"Dungeon Lab Elevation Edge Model: rejected connection-point stair transition {higherCell}(L{higherLevel}) -> {lowerCell}(L{lowerLevel}) delta {deltaLevels}; " +
                        $"{exception.Message}.");
                    stats.rejected++;
                    stats.stairSummaries.Add(deltaLevels == PrimaryStairRiseLevels ? "primary connection-point stair rejected" : $"multi-rise stair rejected d{deltaLevels}");
                    throw new InvalidOperationException(
                        $"Rejected stair prefab transition {higherCell}(L{higherLevel}) -> {lowerCell}(L{lowerLevel}) delta {deltaLevels}: {exception.Message}",
                        exception);
                }

                stats.transitionEdges++;
                stats.stairFootprintChecks++;
                EncapsulateInstance(stairInstance, ref bounds, ref hasBounds);

                // A stairwell tower stands on VOID (decision 26): its kit dresses
                // everything from the entry level up, but there is no tier mass
                // below — the masonry must descend to the ground like every
                // neighbouring wall (user review 2026-06-12).
                if (string.Equals(transition.placementClass, StairwellStairPlacementClass, StringComparison.Ordinal) &&
                    lowerLevel > 0)
                {
                    PlaceStairwellBaseFill(transition, lowerLevel, origin, contracts.levelHeight, stairsRoot.transform, ref bounds, ref hasBounds);
                }

                // Support columns under bridge-style spans (step 9, decision 38):
                // pier stacks rise from the ground to the deck underside beneath
                // the flat deck floor slabs — the gold-scene canyon-bridge pattern.
                // Synthesized plans only: the two pool bridge prefabs carry no
                // piece plan to read corner lines from.
                if (string.Equals(transition.placementClass, ExternalSpanStairPlacementClass, StringComparison.Ordinal) &&
                    transition.synthesizedSetPiece != null)
                {
                    PlaceSpanSupportColumns(
                        transition,
                        stairInstance,
                        levels,
                        reservedCells,
                        origin,
                        AbyssBaseForMinFloor(MinFloorLevel(levels)) * contracts.levelHeight,
                        stairsRoot.transform,
                        ref bounds,
                        ref hasBounds,
                        stats);
                }
            }

            PlaceDaisCornerPieces(
                transitions,
                levels,
                sunkenDaisCorners,
                reservedCells,
                origin,
                contracts.levelHeight,
                stairsRoot.transform,
                ref bounds,
                ref hasBounds,
                stats);

            PlaceRoundTierCornerKits(
                roundTierCorners,
                shellGuardEdges,
                origin,
                contracts.levelHeight,
                cornersRoot.transform,
                ref bounds,
                ref hasBounds,
                stats);

            if (!hasBounds)
            {
                bounds = new Bounds(origin, Vector3.one);
            }

            report = new BuildReport(
                contracts.levelHeight,
                stats.floorCells,
                stats.interiorEdges,
                stats.cliffEdges,
                stats.retainingEdges,
                stats.transitionEdges,
                stats.corners,
                stats.railings,
                stats.enclosedRooms,
                stats.totalRooms,
                stats.partitionWalls,
                stats.doorways,
                stats.gateways,
                stats.largeGateways,
                stats.barredGateways,
                stats.traps,
                stats.largePerimeterRooms,
                stats.largePartitionWalls,
                stats.partitionWallChecks,
                stats.stairFootprintChecks,
                stats.multiRiseStairChecks,
                stats.internalPathEdges,
                stats.internalPathRailings,
                stats.internalPathBareEdges,
                stats.bareBoundaryEdges,
                stats.promontoryDeckCells,
                stats.stackedSurfaces,
                stats.stackedRailedRims,
                stats.stackedBareRims,
                stats.rejected,
                contracts.rejectedContracts,
                contracts.rejectedContractReasons,
                contracts.unsupportedContracts,
                contracts.unsupportedContractReasons,
                string.Join("; ", stats.stairSummaries));
            Debug.Log($"Dungeon Lab Elevation Edge Model Gate: {report.Summary}");
            ClearExistingRoot(rootName);
            root.name = rootName;
            return root;
            }
            catch
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }

                throw;
            }
        }

        private static TieredPlatformContracts BuildContracts()
        {
            PackageInventory inventory = PackageInventory.Load();
            ActiveStairContractCatalog stairCatalog = LoadReviewedStairContractsForGeneration();
            float levelHeight = stairCatalog.levelHeight;
            if (levelHeight <= 0.05f)
            {
                throw new InvalidOperationException($"Reviewed stair contract level height was {levelHeight:0.###}u.");
            }

            MeasuredPrefab floor = MeasurePrefab(inventory.GetPrefabPath(FloorName), PrefabRole.Floor);
            // Measured 2026-07-31 (design §0.1): `_E_` and `_O_` share a pivot
            // and a 4u x 4u footprint exactly, and `_E_`'s localTopY is 0 like
            // `_O_`'s, so FloorPivotForTopSurface places both flush with the
            // walk surface. The slab hangs entirely below. The swap is a
            // drop-in, and a mismatch here would have displaced every deck.
            MeasuredPrefab suspendedFloor = MeasurePrefab(
                inventory.GetPrefabPath(SuspendedFloorName),
                PrefabRole.Floor);
            if (Mathf.Abs(suspendedFloor.localTopY - floor.localTopY) > 0.001f ||
                Mathf.Abs(suspendedFloor.localPlanBounds.Min.x - floor.localPlanBounds.Min.x) > 0.001f ||
                Mathf.Abs(suspendedFloor.localPlanBounds.Min.y - floor.localPlanBounds.Min.y) > 0.001f)
            {
                throw new InvalidOperationException(
                    $"Suspended floor '{SuspendedFloorName}' does not share the walk-surface pivot of " +
                    $"'{FloorName}' (topY {suspendedFloor.localTopY:0.###} vs {floor.localTopY:0.###}, " +
                    $"min {suspendedFloor.localPlanBounds.Min} vs {floor.localPlanBounds.Min}).");
            }

            RailingContracts railings = BuildRailingContracts(inventory, floor);
            List<ConnectionPointSetPieceContract> connectionPointSetPieces = stairCatalog.contracts;
            ConnectionPointSetPieceContract connectionPointStraightStair = FindReviewedPrimaryStraightStair(connectionPointSetPieces);
            var connectionPointVariantStairs = new List<ConnectionPointSetPieceContract>();
            foreach (ConnectionPointSetPieceContract setPiece in connectionPointSetPieces)
            {
                if (!string.Equals(setPiece.prefabPath, connectionPointStraightStair.prefabPath, StringComparison.Ordinal))
                {
                    connectionPointVariantStairs.Add(setPiece);
                }
            }

            DropFaceStack dropFaceStack = BuildDropFaceStack(inventory, WallCourseHeight);
            DropFaceStack cornerStack = BuildCornerStack(inventory, WallCourseHeight);
            PartitionWallContracts partitions = BuildPartitionWallContracts(inventory);
            GatewayContracts gateways = BuildGatewayContracts(inventory);
            return new TieredPlatformContracts(
                levelHeight,
                floor,
                suspendedFloor,
                connectionPointStraightStair,
                connectionPointVariantStairs,
                dropFaceStack,
                cornerStack,
                railings,
                partitions,
                gateways,
                stairCatalog.rejectedContracts,
                stairCatalog.rejectedContractReasons,
                stairCatalog.unsupportedContracts,
                stairCatalog.unsupportedContractReasons);
        }

        /// <summary>
        /// Resolve a reviewed stair contract by NAME, for an author who wants a
        /// specific flight rather than whatever the planner would have picked.
        /// </summary>
        /// <remarks>
        /// A recipe stair is authored, not planned: the room's geometry is drawn
        /// around one particular flight, so "any contract with this rise" is the
        /// wrong question — two rise-4 contracts in the pool are a straight run
        /// and an L-turn, and they need different rooms. Selecting by name keeps
        /// the choice in the recipe and off a JSON ordering accident.
        /// <para>
        /// Reads the same two files, and applies the same reviewStatus gate, as
        /// <see cref="LoadReviewedStairContractsForGeneration"/>; it deliberately
        /// does NOT reuse that catalog, which measures prefabs and is far more
        /// than a name lookup needs.
        /// </para>
        /// </remarks>
        public static bool TryGetReviewedStairContract(
            string contractName,
            out string prefabPath,
            out int riseLevels)
        {
            prefabPath = string.Empty;
            riseLevels = 0;
            if (string.IsNullOrWhiteSpace(contractName))
            {
                return false;
            }

            foreach (string path in new[] { StairProofContractsPath, ForgedStairContractsPath })
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                if (!(JObject.Parse(File.ReadAllText(path))["contracts"] is JArray records))
                {
                    continue;
                }

                foreach (JToken token in records)
                {
                    if (!string.Equals(token.Value<string>("name"), contractName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // `reviewStatus` is absent on the hand-authored proof
                    // contracts, which are reviewed by construction; only the
                    // forge writes it, and only "reviewed" may generate.
                    string status = token.Value<string>("reviewStatus");
                    if (!string.IsNullOrEmpty(status) &&
                        !string.Equals(status, "reviewed", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    prefabPath = NormalizeAssetPath(token.Value<string>("prefab") ?? string.Empty);
                    riseLevels = token.Value<int?>("rise") ?? 0;
                    return !string.IsNullOrEmpty(prefabPath) && riseLevels > 0;
                }
            }

            return false;
        }

        private static ActiveStairContractCatalog LoadReviewedStairContractsForGeneration()
        {
            if (!File.Exists(StairProofContractsPath))
            {
                throw new FileNotFoundException(StairProofContractsPath);
            }

            JObject root = JObject.Parse(File.ReadAllText(StairProofContractsPath));
            float cellSize = root.Value<float?>("cellSize") ?? CellSize;
            if (Mathf.Abs(cellSize - CellSize) > 0.001f)
            {
                throw new InvalidOperationException(
                    $"{StairProofContractsPath} cellSize {cellSize:0.###} did not match generator cell size {CellSize:0.###}.");
            }

            float levelHeight = root.Value<float?>("levelHeight") ?? 0f;
            if (levelHeight <= 0.05f)
            {
                throw new InvalidOperationException($"{StairProofContractsPath} levelHeight must be positive.");
            }

            ValidateReviewedPortSpanBandRegression();

            var contracts = new List<ConnectionPointSetPieceContract>();
            var rejectedReasons = new List<string>();
            var unsupportedReasons = new List<string>();
            var unsupportedKeys = new HashSet<string>(StringComparer.Ordinal);
            var contractPrefabPaths = new HashSet<string>(StringComparer.Ordinal);

            if (root["unsupportedStairs"] is JObject unsupported)
            {
                foreach (JProperty property in unsupported.Properties())
                {
                    string reason = property.Value.Value<string>() ?? "no authored reviewed contract";
                    string entry = $"{property.Name}:{reason}";
                    unsupportedReasons.Add(entry);
                    unsupportedKeys.Add(property.Name);
                    Debug.LogWarning($"Dungeon Lab Generate: unsupported stair contract {entry}");
                }
            }

            if (!(root["contracts"] is JArray records))
            {
                throw new InvalidOperationException($"{StairProofContractsPath} is missing a contracts array.");
            }

            foreach (JToken token in records)
            {
                if (TryBuildReviewedConnectionPointContract(token, levelHeight, out ConnectionPointSetPieceContract contract, out string rejectedReason))
                {
                    contracts.Add(contract);
                    contractPrefabPaths.Add(contract.prefabPath);
                    Debug.Log(
                        $"Dungeon Lab Generate: loaded reviewed stair contract '{contract.name}' rise {contract.riseLevels}; " +
                        $"entry {contract.entry}, exit {contract.exit}, footprint {contract.localPlanBounds}.");
                    continue;
                }

                rejectedReasons.Add(rejectedReason);
                Debug.LogWarning($"Dungeon Lab Generate: rejected active stair contract {rejectedReason}");
            }

            // Forge output joins the pool on equal terms (design decision 11) once a
            // human flips its reviewStatus to "reviewed"; pending entries register as
            // unsupported so their prefabs do not trip the automatic unsupported scan
            // with a misleading "missing contract" reason.
            if (File.Exists(ForgedStairContractsPath))
            {
                JObject forgedRoot = JObject.Parse(File.ReadAllText(ForgedStairContractsPath));
                float forgedCellSize = forgedRoot.Value<float?>("cellSize") ?? CellSize;
                float forgedLevelHeight = forgedRoot.Value<float?>("levelHeight") ?? levelHeight;
                if (Mathf.Abs(forgedCellSize - CellSize) > 0.001f ||
                    Mathf.Abs(forgedLevelHeight - levelHeight) > 0.001f)
                {
                    throw new InvalidOperationException(
                        $"{ForgedStairContractsPath} grid (cellSize {forgedCellSize:0.###}, levelHeight {forgedLevelHeight:0.###}) " +
                        $"did not match the reviewed contract grid (cellSize {CellSize:0.###}, levelHeight {levelHeight:0.###}).");
                }

                if (forgedRoot["contracts"] is JArray forgedRecords)
                {
                    foreach (JToken token in forgedRecords)
                    {
                        string forgedName = token.Value<string>("name") ?? string.Empty;
                        string forgedStatus = token.Value<string>("reviewStatus") ?? string.Empty;
                        if (!string.Equals(forgedStatus, "reviewed", StringComparison.Ordinal))
                        {
                            string entry = $"{forgedName}:forge output pending human review";
                            unsupportedReasons.Add(entry);
                            string forgedPrefabName = Path.GetFileNameWithoutExtension(
                                NormalizeAssetPath(token.Value<string>("prefab") ?? string.Empty));
                            if (!string.IsNullOrWhiteSpace(forgedPrefabName))
                            {
                                unsupportedKeys.Add(forgedPrefabName);
                            }

                            continue;
                        }

                        if (TryBuildReviewedConnectionPointContract(token, levelHeight, out ConnectionPointSetPieceContract forgedContract, out string forgedRejectedReason))
                        {
                            contracts.Add(forgedContract);
                            contractPrefabPaths.Add(forgedContract.prefabPath);
                            Debug.Log(
                                $"Dungeon Lab Generate: loaded reviewed FORGED stair contract '{forgedContract.name}' rise {forgedContract.riseLevels}; " +
                                $"entry {forgedContract.entry}, exit {forgedContract.exit}, footprint {forgedContract.localPlanBounds}.");
                            continue;
                        }

                        rejectedReasons.Add(forgedRejectedReason);
                        Debug.LogWarning($"Dungeon Lab Generate: rejected forged stair contract {forgedRejectedReason}");
                    }
                }
            }

            AddAutomaticallyClassifiedUnsupportedStairs(contractPrefabPaths, unsupportedKeys, unsupportedReasons);

            if (contracts.Count == 0)
            {
                throw new InvalidOperationException(
                    "No reviewed stair contracts were usable for active generation. " +
                    $"rejected={FormatReasonList(rejectedReasons)} unsupported={FormatReasonList(unsupportedReasons)}");
            }

            ValidateReviewedSourceResolverRegression(contracts);

            return new ActiveStairContractCatalog(
                levelHeight,
                contracts,
                rejectedReasons.Count,
                FormatReasonList(rejectedReasons),
                unsupportedReasons.Count,
                FormatReasonList(unsupportedReasons));
        }

        private static void AddAutomaticallyClassifiedUnsupportedStairs(
            HashSet<string> contractPrefabPaths,
            HashSet<string> unsupportedKeys,
            List<string> unsupportedReasons)
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/Stairs" });
            Array.Sort(guids, StringComparer.Ordinal);
            foreach (string guid in guids)
            {
                string prefabPath = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(guid));
                if (contractPrefabPaths.Contains(prefabPath))
                {
                    continue;
                }

                string prefabName = Path.GetFileNameWithoutExtension(prefabPath);
                if (unsupportedKeys.Contains(prefabName) || unsupportedKeys.Contains(prefabPath))
                {
                    continue;
                }

                string entry = $"{prefabName}:{AutomaticUnsupportedReason()}";
                unsupportedReasons.Add(entry);
                Debug.LogWarning($"Dungeon Lab Generate: unsupported stair contract {entry}");
            }
        }

        private static string AutomaticUnsupportedReason()
        {
            return "missing authored reviewed contract; automatically classified unsupported until a reviewed contract defines rise, lane count, run length, topology, footprint, ports, and visual anchors";
        }

        private static bool TryBuildReviewedConnectionPointContract(
            JToken token,
            float levelHeight,
            out ConnectionPointSetPieceContract contract,
            out string rejectedReason)
        {
            contract = default;
            string name = token.Value<string>("name") ?? string.Empty;
            string prefabPath = NormalizeAssetPath(token.Value<string>("prefab") ?? string.Empty);
            string label = string.IsNullOrWhiteSpace(name) ? prefabPath : name;

            string source = token.Value<string>("source") ?? string.Empty;
            string reviewStatus = token.Value<string>("reviewStatus") ?? string.Empty;
            bool acceptedSource = string.Equals(source, "authored-reviewed", StringComparison.Ordinal) ||
                string.Equals(source, "forge", StringComparison.Ordinal);
            if (!acceptedSource || !string.Equals(reviewStatus, "reviewed", StringComparison.Ordinal))
            {
                rejectedReason = $"{label}:contract is not reviewed (source '{source}', status '{reviewStatus}')";
                return false;
            }

            return TryBuildConnectionPointContractCore(token, levelHeight, prefabPath, label, requirePrefabAsset: true, out contract, out rejectedReason);
        }

        // Online synthesis (step 7): a synthesized contract is source "synthesis",
        // reviewStatus "provisional", and has no prefab asset — its visual is built
        // from the in-memory piece plan riding the TransitionEdge. The geometry runs
        // through the exact same parser core as the reviewed files, so the planner
        // and renderer cannot drift.
        private static bool TryBuildSynthesizedSetPieceContract(
            JToken token,
            float levelHeight,
            out ConnectionPointSetPieceContract contract,
            out string rejectedReason)
        {
            contract = default;
            string name = token.Value<string>("name") ?? string.Empty;
            string prefabPath = NormalizeAssetPath(token.Value<string>("prefab") ?? string.Empty);
            string label = string.IsNullOrWhiteSpace(name) ? prefabPath : name;

            string source = token.Value<string>("source") ?? string.Empty;
            string reviewStatus = token.Value<string>("reviewStatus") ?? string.Empty;
            if (!string.Equals(source, "synthesis", StringComparison.Ordinal) ||
                !string.Equals(reviewStatus, "provisional", StringComparison.Ordinal))
            {
                rejectedReason = $"{label}:synthesized contract must be source 'synthesis' with reviewStatus 'provisional' (got '{source}'/'{reviewStatus}')";
                return false;
            }

            return TryBuildConnectionPointContractCore(token, levelHeight, prefabPath, label, requirePrefabAsset: false, out contract, out rejectedReason);
        }

        // Headless planning gate for a synthesized contract token: the real parser,
        // no GameObjects touched. Returns empty on success.
        internal static string ValidateSynthesizedContractToken(JToken token, float levelHeight)
        {
            return TryBuildSynthesizedSetPieceContract(token, levelHeight, out _, out string rejectedReason)
                ? string.Empty
                : rejectedReason;
        }

        private static bool TryBuildConnectionPointContractCore(
            JToken token,
            float levelHeight,
            string prefabPath,
            string label,
            bool requirePrefabAsset,
            out ConnectionPointSetPieceContract contract,
            out string rejectedReason)
        {
            contract = default;
            bool isBridge = token.Value<bool?>("bridgeAllowed") == true;

            string topology = token.Value<string>("topology") ?? string.Empty;
            if (!IsActiveGenerationStairTopology(topology))
            {
                rejectedReason = $"{label}:topology {topology} is not enabled in active generation";
                return false;
            }

            int rise = token.Value<int?>("rise") ?? 0;
            int laneCount = token.Value<int?>("laneCount") ?? 0;
            bool deckTopology = string.Equals(topology, ActiveDeckStairTopology, StringComparison.Ordinal);
            if (deckTopology ? rise != 0 : rise <= 0)
            {
                rejectedReason = deckTopology
                    ? $"{label}:a deck contract must have rise 0"
                    : $"{label}:rise must be positive";
                return false;
            }

            if (laneCount <= 0)
            {
                rejectedReason = $"{label}:lane count must be positive";
                return false;
            }

            if (requirePrefabAsset)
            {
                if (!IsReviewedStairPrefabPath(prefabPath))
                {
                    rejectedReason = $"{label}:prefab is not in Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/Stairs";
                    return false;
                }

                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                {
                    rejectedReason = $"{label}:missing prefab {prefabPath}";
                    return false;
                }
            }

            if (!(token["ports"] is JArray portArray) || portArray.Count < 2)
            {
                rejectedReason = $"{label}:contract must define at least two ports";
                return false;
            }

            if (!TryParseReviewedCells(token["footprintCells"], out Vector2Int[] floorBlockedCells) || floorBlockedCells.Length == 0)
            {
                rejectedReason = $"{label}:contract must define footprint cells";
                return false;
            }

            string[] exitSurfaceRootSources = ParseReviewedVisualAnchorSources(token["visualAnchors"], "exitSurfaceRoots");
            if (exitSurfaceRootSources.Length == 0)
            {
                rejectedReason = $"{label}:contract must define visualAnchors exitSurfaceRoots";
                return false;
            }
            Vector3[] reviewedVisualAnchorPositions = ParseReviewedVisualAnchorExpectedPositions(token["visualAnchors"]);
            ReviewedSourceRootPose[] reviewedSourceRootPoses = ParseReviewedSourceRootPoses(token["sourceRootPoses"], exitSurfaceRootSources);

            ConnectionPoint[] points = ParseReviewedPortsAsConnectionPoints(portArray);
            ConnectionPoint entry;
            ConnectionPoint exit;
            if (deckTopology && points.Length == 2 && points[0].level == points[1].level)
            {
                // Aerial decks (decision 31): rise-0 ports at equal levels
                // resolve by array order, not by level.
                entry = WithConnectionPointRole(points[0], "entry");
                exit = WithConnectionPointRole(points[1], "exit");
                points = new[] { entry, exit };
            }
            else if (!TryFindConnectionPoint(points, "entry", out entry) ||
                !TryFindConnectionPoint(points, "exit", out exit))
            {
                rejectedReason = $"{label}:contract must define one lowest entry port and one highest exit port";
                return false;
            }

            if (entry.spanCellCount != laneCount || exit.spanCellCount != laneCount)
            {
                rejectedReason = $"{label}:port span does not match lane count";
                return false;
            }

            if (entry.level != 0 || exit.level - entry.level != rise)
            {
                rejectedReason = $"{label}:entry and exit port levels do not match rise";
                return false;
            }

            Bounds localBounds = BuildReviewedLocalBounds(token, rise, levelHeight);
            if (!TryValidateReviewedPortSpan(label, localBounds, entry, out rejectedReason) ||
                !TryValidateReviewedPortSpan(label, localBounds, exit, out rejectedReason))
            {
                return false;
            }

            var localPlanBounds = new PlanBounds(localBounds.min.x, localBounds.max.x, localBounds.min.z, localBounds.max.z);
            contract = new ConnectionPointSetPieceContract(
                prefabPath,
                label,
                localPlanBounds,
                localBounds,
                localBounds,
                points,
                entry,
                exit,
                rise,
                floorBlockedCells,
                exitSurfaceRootSources,
                reviewedVisualAnchorPositions,
                reviewedSourceRootPoses,
                isBridge);
            rejectedReason = string.Empty;
            return true;
        }

        // Forge gate (step 6): round-trips an emitted contract token through the
        // exact parser and visual-alignment code that places stairs at generation
        // time, so a forged prefab/contract pair can never drift from what the
        // renderer will do with it. Review status is the caller's business — the
        // probe pretends the contract is reviewed to get past the gate and
        // validates geometry only. Returns empty on success.
        internal static string ValidateForgedContractRoundTrip(JToken token, float levelHeight)
        {
            if (!(token.DeepClone() is JObject probe))
            {
                return "contract token is not an object";
            }

            probe["source"] = "forge";
            probe["reviewStatus"] = "reviewed";
            if (!TryBuildReviewedConnectionPointContract(probe, levelHeight, out ConnectionPointSetPieceContract contract, out string rejectedReason))
            {
                return rejectedReason;
            }

            GameObject probeRoot = null;
            try
            {
                probeRoot = InstantiateConnectionPointSetPiecePrefab(contract, "forge_validation_probe", null, Vector3.zero, 0f);
                return string.Empty;
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
            finally
            {
                if (probeRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(probeRoot);
                }
            }
        }

        private static ConnectionPoint WithConnectionPointRole(ConnectionPoint point, string role)
        {
            return new ConnectionPoint(
                point.localCell,
                point.direction,
                point.level,
                role,
                point.spanCells,
                point.hasLocalPoint,
                point.localPoint);
        }

        private static bool TryFindConnectionPoint(ConnectionPoint[] points, string role, out ConnectionPoint result)
        {
            foreach (ConnectionPoint point in points)
            {
                if (string.Equals(point.role, role, StringComparison.Ordinal))
                {
                    result = point;
                    return true;
                }
            }

            result = default;
            return false;
        }

        private static bool IsActiveGenerationStairTopology(string topology)
        {
            return string.Equals(topology, ActiveStraightStairTopology, StringComparison.Ordinal) ||
                string.Equals(topology, ActiveTurningStairTopology, StringComparison.Ordinal) ||
                string.Equals(topology, ActiveStairwellStairTopology, StringComparison.Ordinal) ||
                string.Equals(topology, ActiveDeckStairTopology, StringComparison.Ordinal);
        }

        private static void ValidateReviewedSourceResolverRegression(IReadOnlyList<ConnectionPointSetPieceContract> contracts)
        {
            ValidateReviewedSourceResolverRegression(contracts, "straight_stair_90_L");
            ValidateReviewedSourceResolverRegression(contracts, "straight_stair_90_R");
        }

        private static void ValidateReviewedSourceResolverRegression(
            IReadOnlyList<ConnectionPointSetPieceContract> contracts,
            string contractName)
        {
            ConnectionPointSetPieceContract contract = default;
            bool found = false;
            foreach (ConnectionPointSetPieceContract candidate in contracts)
            {
                if (!string.Equals(candidate.name, contractName, StringComparison.Ordinal))
                {
                    continue;
                }

                contract = candidate;
                found = true;
                break;
            }

            if (!found)
            {
                throw new InvalidOperationException(
                    $"Reviewed source-root regression failed: active contract '{contractName}' was not loaded.");
            }

            var sources = new HashSet<string>(contract.exitSurfaceRootSources, StringComparer.Ordinal);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(contract.prefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException(
                    $"Reviewed source-root regression failed for '{contractName}': missing prefab {contract.prefabPath}.");
            }

            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                int rootCount = ReviewedStairSourceResolver.CountSourceRoots(instance, sources);
                if (rootCount <= 0)
                {
                    throw new InvalidOperationException(
                        $"Reviewed source-root regression failed for '{contractName}': exitSurfaceRoots resolved 0 source roots from [{string.Join(", ", contract.exitSurfaceRootSources)}].");
                }
            }
            finally
            {
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
        }

        private static bool TryParseReviewedCells(JToken token, out Vector2Int[] cells)
        {
            var values = new List<Vector2Int>();
            if (!(token is JArray array))
            {
                cells = Array.Empty<Vector2Int>();
                return false;
            }

            foreach (JToken item in array)
            {
                values.Add(new Vector2Int(item.Value<int>("x"), item.Value<int>("z")));
            }

            cells = values.ToArray();
            return true;
        }

        private static string[] ParseReviewedVisualAnchorSources(JToken token, string role)
        {
            if (!(token is JArray array))
            {
                return Array.Empty<string>();
            }

            foreach (JToken item in array)
            {
                if (!string.Equals(item.Value<string>("role"), role, StringComparison.Ordinal) ||
                    !(item["sourcePrefabs"] is JArray sources))
                {
                    continue;
                }

                var values = new List<string>();
                foreach (JToken source in sources)
                {
                    string path = NormalizeAssetPath(source.Value<string>() ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        values.Add(path);
                    }
                }

                return values.ToArray();
            }

            return Array.Empty<string>();
        }

        private static Vector3[] ParseReviewedVisualAnchorExpectedPositions(JToken token)
        {
            if (!(token is JArray array))
            {
                return Array.Empty<Vector3>();
            }

            Vector3[] bodyRootPositions = ParseExpectedPositionsForRole(array, "stairBodyRoots");
            if (bodyRootPositions.Length > 0)
            {
                return bodyRootPositions;
            }

            // Some contracts author expectedLocalPositions directly on the exitSurfaceRoots
            // anchor instead of a separate stairBodyRoots entry. Honor them rather than
            // silently dropping to the bounds-based alignment fallback, which can shift the
            // visual by a whole cell when source-root pivots do not span the exit port band.
            return ParseExpectedPositionsForRole(array, "exitSurfaceRoots");
        }

        private static Vector3[] ParseExpectedPositionsForRole(JArray array, string role)
        {
            foreach (JToken item in array)
            {
                if (!string.Equals(item.Value<string>("role"), role, StringComparison.Ordinal) ||
                    !(item["expectedLocalPositions"] is JArray positions))
                {
                    continue;
                }

                return ParseReviewedVector3Array(positions);
            }

            return Array.Empty<Vector3>();
        }

        private static ReviewedSourceRootPose[] ParseReviewedSourceRootPoses(JToken token, IReadOnlyCollection<string> sourcePaths)
        {
            if (!(token is JArray array))
            {
                return Array.Empty<ReviewedSourceRootPose>();
            }

            var sources = new HashSet<string>(sourcePaths, StringComparer.Ordinal);
            var values = new List<ReviewedSourceRootPose>();
            foreach (JToken item in array)
            {
                string sourcePath = NormalizeAssetPath(item.Value<string>("sourcePrefab") ?? string.Empty);
                if (string.IsNullOrWhiteSpace(sourcePath) || !sources.Contains(sourcePath))
                {
                    continue;
                }

                values.Add(new ReviewedSourceRootPose(
                    sourcePath,
                    ParseVector3(item["localPosition"]),
                    item.Value<float?>("localYawDegrees") ?? 0f));
            }

            return values.ToArray();
        }

        private static Vector3[] ParseReviewedVector3Array(JArray array)
        {
            var values = new Vector3[array.Count];
            for (int i = 0; i < array.Count; i++)
            {
                values[i] = ParseVector3(array[i]);
            }

            return values;
        }

        private static ConnectionPoint[] ParseReviewedPortsAsConnectionPoints(JArray portArray)
        {
            var ports = new List<ConnectionPoint>();
            int lowestLevel = int.MaxValue;
            int highestLevel = int.MinValue;
            foreach (JToken port in portArray)
            {
                if (!(port["cells"] is JArray cells) || cells.Count == 0)
                {
                    continue;
                }

                Vector2Int[] spanCells = ParsePortCells(cells);
                Vector2Int cell = spanCells[spanCells.Length / 2];
                int level = port.Value<int?>("level") ?? 0;
                lowestLevel = Mathf.Min(lowestLevel, level);
                highestLevel = Mathf.Max(highestLevel, level);
                ports.Add(new ConnectionPoint(
                    cell,
                    DirectionFromName(port.Value<string>("side")),
                    level,
                    string.Empty,
                    spanCells,
                    hasLocalPoint: true,
                    ParseVector3(port["localEdgePosition"])));
            }

            var points = new ConnectionPoint[ports.Count];
            for (int i = 0; i < ports.Count; i++)
            {
                ConnectionPoint port = ports[i];
                string role = "side";
                if (port.level == lowestLevel)
                {
                    role = "entry";
                }
                else if (port.level == highestLevel)
                {
                    role = "exit";
                }

                points[i] = new ConnectionPoint(
                    port.localCell,
                    port.direction,
                    port.level,
                    role,
                    port.spanCells,
                    port.hasLocalPoint,
                    port.localPoint);
            }

            return points;
        }

        private static Vector2Int[] ParsePortCells(JArray cells)
        {
            var result = new Vector2Int[cells.Count];
            for (int i = 0; i < cells.Count; i++)
            {
                JToken cell = cells[i];
                result[i] = new Vector2Int(cell.Value<int>("x"), cell.Value<int>("z"));
            }

            return result;
        }

        private static Bounds BuildReviewedLocalBounds(JToken token, int rise, float levelHeight)
        {
            Vector3 min = ParseVector3(token["localBoundsMin"]);
            JToken sizeToken = token["localBoundsSizeCells"];
            int sizeX = sizeToken == null ? 1 : Mathf.Max(1, sizeToken.Value<int>("x"));
            int sizeZ = sizeToken == null ? 1 : Mathf.Max(1, sizeToken.Value<int>("z"));
            Vector3 max = new Vector3(
                min.x + sizeX * CellSize,
                min.y + rise * levelHeight,
                min.z + sizeZ * CellSize);
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            return bounds;
        }

        private static ConnectionPointSetPieceContract FindReviewedPrimaryStraightStair(IReadOnlyList<ConnectionPointSetPieceContract> setPieces)
        {
            foreach (ConnectionPointSetPieceContract setPiece in setPieces)
            {
                if (!setPiece.isBridge &&
                    setPiece.riseLevels == PrimaryStairRiseLevels &&
                    string.Equals(Path.GetFileNameWithoutExtension(setPiece.prefabPath), "straight_stair_1x", StringComparison.Ordinal))
                {
                    return setPiece;
                }
            }

            foreach (ConnectionPointSetPieceContract setPiece in setPieces)
            {
                if (!setPiece.isBridge &&
                    setPiece.riseLevels == PrimaryStairRiseLevels)
                {
                    return setPiece;
                }
            }

            throw new InvalidOperationException($"No reviewed rise-{PrimaryStairRiseLevels} straight stair contract was usable for active generation.");
        }

        private static bool IsReviewedStairPrefabPath(string path)
        {
            return NormalizeAssetPath(path).StartsWith("Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/Stairs/", StringComparison.Ordinal) &&
                NormalizeAssetPath(path).EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim();
        }

        private static string FormatReasonList(IReadOnlyList<string> reasons)
        {
            if (reasons == null || reasons.Count == 0)
            {
                return "[]";
            }

            return $"[{string.Join(" | ", reasons)}]";
        }

        private static List<KeyValuePair<Vector2Int, int>> SortedCells(IReadOnlyDictionary<Vector2Int, int> levels)
        {
            var cells = new List<KeyValuePair<Vector2Int, int>>(levels);
            cells.Sort((left, right) =>
            {
                int z = left.Key.y.CompareTo(right.Key.y);
                return z != 0 ? z : left.Key.x.CompareTo(right.Key.x);
            });
            return cells;
        }

        private static HashSet<EdgeKey> BuildTransitionKeys(
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<TransitionEdge> transitions,
            out HashSet<OpenEdgeKey> openEdges,
            out HashSet<OpenEdgeKey> bridgeSpanEdges,
            out Dictionary<Vector2Int, int> aerialDeckCellLevels)
        {
            var keys = new HashSet<EdgeKey>();
            openEdges = new HashSet<OpenEdgeKey>();
            bridgeSpanEdges = new HashSet<OpenEdgeKey>();
            aerialDeckCellLevels = new Dictionary<Vector2Int, int>();
            foreach (TransitionEdge transition in transitions)
            {
                // Aerial deck cells (rise-0 synthesized spans) walk at the landing
                // level: a floor edge facing one is even with it, so its railing
                // is unnecessary (user rule 2026-06-12).
                if (transition.synthesizedSetPiece != null &&
                    transition.HasLevels &&
                    transition.firstLevel == transition.secondLevel)
                {
                    foreach (Vector2Int deckCell in transition.footprintCells)
                    {
                        aerialDeckCellLevels[deckCell] = transition.firstLevel;
                    }
                }

                if (!transition.HasLevels)
                {
                    // Skipping here silently omitted an opening from the wall
                    // plan, which can seal a stair mouth. It never incremented
                    // stats.rejected, so it was invisible even to the summary line.
                    throw new InvalidOperationException(
                        $"Transition references a missing level cell: {transition.firstCell} <-> {transition.secondCell} " +
                        $"prefab '{transition.stairPrefabPath}'.");
                }

                if (AreCardinalNeighbors(transition.firstCell, transition.secondCell))
                {
                    // Embedded bodies fill the face between their transition cells;
                    // a span deck or a stairwell tower stands beside/above it, so
                    // that rise-R face stays WALLED (the wall invariant) — the walk
                    // goes around through the deck or the tower.
                    bool bodyFillsSharedFace =
                        !string.Equals(transition.placementClass, ExternalSpanStairPlacementClass, StringComparison.Ordinal) &&
                        !string.Equals(transition.placementClass, StairwellStairPlacementClass, StringComparison.Ordinal);
                    if (bodyFillsSharedFace)
                    {
                        keys.Add(new EdgeKey(transition.firstCell, transition.secondCell));
                    }

                    if (transition.hasPortDirections)
                    {
                        AddTransitionLandingOpenEdges(openEdges, bridgeSpanEdges, transition);
                    }

                    continue;
                }

                if (transition.hasPortDirections)
                {
                    AddTransitionLandingOpenEdges(openEdges, bridgeSpanEdges, transition);
                    continue;
                }

                if (string.IsNullOrEmpty(transition.stairPrefabPath))
                {
                    throw new InvalidOperationException($"Transition cells must share one edge: {transition.firstCell} <-> {transition.secondCell}.");
                }

                int firstLevel = transition.firstLevel;
                int secondLevel = transition.secondLevel;
                Vector2Int higherCell = firstLevel > secondLevel ? transition.firstCell : transition.secondCell;
                Vector2Int lowerCell = firstLevel > secondLevel ? transition.secondCell : transition.firstCell;
                if (!TryDirectionFromCellToward(higherCell, lowerCell, out int lowerDirection))
                {
                    throw new InvalidOperationException(
                        $"Non-straight named set-piece transition {higherCell}(L{Mathf.Max(firstLevel, secondLevel)}) -> " +
                        $"{lowerCell}(L{Mathf.Min(firstLevel, secondLevel)}) prefab '{transition.stairPrefabPath}'. " +
                        "Reserve-first accepts only measured straight-through connectors.");
                }

                openEdges.Add(new OpenEdgeKey(higherCell, lowerDirection));
                openEdges.Add(new OpenEdgeKey(lowerCell, OppositeDirection(lowerDirection)));
            }

            return keys;
        }

        // Embedded stair ports stay fully open: the stair body fills that face with
        // steps. Bridge (externalSpan) ports go to bridgeSpanEdges instead — the deck
        // arrives ON TOP of the receiving face, so the wall below the entry level must
        // still render and only the railing is suppressed.
        private static void AddTransitionLandingOpenEdges(
            HashSet<OpenEdgeKey> openEdges,
            HashSet<OpenEdgeKey> bridgeSpanEdges,
            TransitionEdge transition)
        {
            // Seam/dais landing cells describe traversal access to a step strip;
            // they are not prefab mouths. The strip fills only the shared face
            // between its transition cells, so opening a remote landing edge here
            // would erase the abyss-support wall beneath an exposed floor edge.
            if (string.Equals(transition.placementClass, SeamStairPlacementClass, StringComparison.Ordinal) ||
                string.Equals(transition.placementClass, DaisStairPlacementClass, StringComparison.Ordinal))
            {
                return;
            }

            bool externalSpan = string.Equals(transition.placementClass, ExternalSpanStairPlacementClass, StringComparison.Ordinal);
            HashSet<OpenEdgeKey> target = externalSpan ? bridgeSpanEdges : openEdges;
            int lowerFloorSide = OppositeDirection(transition.lowerPortDirection);
            int upperFloorSide = OppositeDirection(transition.upperPortDirection);
            foreach (Vector2Int cell in transition.lowerLandingCells)
            {
                target.Add(new OpenEdgeKey(cell, lowerFloorSide));
            }

            foreach (Vector2Int cell in transition.upperLandingCells)
            {
                target.Add(new OpenEdgeKey(cell, upperFloorSide));
            }
        }

        // INVARIANT (do not weaken): if there is a floor, the walls beneath its edges
        // always render to complete the tier's shape. There is deliberately NO
        // suppression for edges that merely run alongside a bridge span — a deck flush
        // against a terrain wall is correct architecture. Stairs only ever affect
        // railings (suppressed at ports and on stair cells) and embedded port faces.

        private static StairReservationSet BuildStairReservations(
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<TransitionEdge> transitions,
            TieredPlatformContracts contracts,
            Vector3 origin)
        {
            var reserved = new HashSet<Vector2Int>();
            var bridgeReserved = new HashSet<Vector2Int>();
            if (transitions == null)
            {
                return new StairReservationSet(reserved, bridgeReserved);
            }

            foreach (TransitionEdge transition in transitions)
            {
                // Seam/corridor/dais step strips reserve nothing: they sit on the
                // lower cell's floor and the cell stays walkable; their 1u edge is
                // bare by the ledge policy, so no floor or railing suppression
                // applies, and they have no reviewed contract to resolve.
                if (string.Equals(transition.placementClass, SeamStairPlacementClass, StringComparison.Ordinal) ||
                    string.Equals(transition.placementClass, DaisStairPlacementClass, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!transition.HasLevels || transition.RiseLevels == 0)
                {
                    continue;
                }

                int firstLevel = transition.firstLevel;
                int secondLevel = transition.secondLevel;
                Vector2Int higherCell = firstLevel > secondLevel ? transition.firstCell : transition.secondCell;
                Vector2Int lowerCell = firstLevel > secondLevel ? transition.secondCell : transition.firstCell;
                int higherLevel = Mathf.Max(firstLevel, secondLevel);
                int lowerLevel = Mathf.Min(firstLevel, secondLevel);
                int deltaLevels = higherLevel - lowerLevel;
                int lowerDirectionId = transition.hasPortDirections
                    ? transition.lowerPortDirection
                    : AreCardinalNeighbors(higherCell, lowerCell)
                        ? EdgeFromCellToward(higherCell, lowerCell).direction
                        : DirectionFromCellToward(higherCell, lowerCell);
                int entryDirectionId = transition.hasPortDirections ? transition.lowerPortDirection : lowerDirectionId;
                int exitDirectionId = transition.hasPortDirections ? transition.upperPortDirection : OppositeDirection(lowerDirectionId);
                Vector3 stairTop = TransitionExitEdgeCenter(origin, transition, higherCell, OppositeDirection(exitDirectionId), higherLevel * contracts.levelHeight);
                Vector2 lowerDirection = DirectionVector(entryDirectionId);
                ConnectionPointSetPieceContract setPiece = ResolveTransitionSetPieceContract(
                    contracts,
                    transition,
                    deltaLevels);
                bool externalSpan = string.Equals(transition.placementClass, ExternalSpanStairPlacementClass, StringComparison.Ordinal);
                if (externalSpan != setPiece.isBridge)
                {
                    string requiredPlacement = setPiece.isBridge ? ExternalSpanStairPlacementClass : EmbeddedStairPlacementClass;
                    throw new InvalidOperationException(
                        $"Stair prefab '{setPiece.name}' placement class '{transition.placementClass}' violates bridge contract; required '{requiredPlacement}'.");
                }

                ConnectionPointPlacement placement = CalculateConnectionPointPlacement(
                    setPiece,
                    lowerCell,
                    higherCell,
                    lowerLevel,
                    higherLevel,
                    origin,
                    contracts.levelHeight,
                    stairTop,
                    lowerDirection);

                var transitionReserved = new HashSet<Vector2Int>();
                var transitionContractFootprint = new HashSet<Vector2Int>();
                foreach (Vector2Int localCell in setPiece.floorBlockedCells)
                {
                    Vector3 worldCenter = placement.position + Quaternion.Euler(0f, placement.yRotation, 0f) * LocalCellCenter(setPiece, localCell);
                    Vector2Int worldCell = CellFromWorldPoint(origin, worldCenter);
                    transitionContractFootprint.Add(worldCell);
                    // A span deck owns no ground at all: unleveled footprint cells
                    // are the gap below, and LEVELED ones are planner-validated
                    // overflight (decision 30) whose floors must still render —
                    // nothing to reserve either way. The footprint-set and landing
                    // checks below still verify the placement against the plan.
                    if (externalSpan)
                    {
                        continue;
                    }

                    if (!levels.TryGetValue(worldCell, out int reservedLevel))
                    {
                        // Stairwell towers stand on void (decision 26): unleveled
                        // by design; a LEVELED tower cell stays an error.
                        if (string.Equals(transition.placementClass, StairwellStairPlacementClass, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        throw new InvalidOperationException(
                            $"Stair prefab '{setPiece.name}' footprint cell {worldCell} is not present at lower level {lowerLevel}.");
                    }

                    if (reservedLevel != lowerLevel)
                    {
                        throw new InvalidOperationException(
                            $"Stair prefab '{setPiece.name}' footprint cell {worldCell} is not present at lower level {lowerLevel}.");
                    }

                    transitionReserved.Add(worldCell);
                    reserved.Add(worldCell);
                    if (externalSpan)
                    {
                        bridgeReserved.Add(worldCell);
                    }
                }

                if (transition.footprintCells.Length > 0 &&
                    !CellSetsEqual(transitionContractFootprint, transition.footprintCells))
                {
                    throw new InvalidOperationException(
                        $"Stair prefab '{setPiece.name}' footprint mismatch. Expected {FormatCells(transitionContractFootprint)} from contract, got planned {FormatCells(transition.footprintCells)}.");
                }

                if (transition.hasLandings)
                {
                    ValidateTransitionLandings(
                        transition,
                        setPiece,
                        placement,
                        origin,
                        levels,
                        lowerLevel,
                        higherLevel,
                        transitionContractFootprint);
                }
            }

            return new StairReservationSet(reserved, bridgeReserved);
        }

        private static void ValidateTransitionLandings(
            TransitionEdge transition,
            ConnectionPointSetPieceContract setPiece,
            ConnectionPointPlacement placement,
            Vector3 origin,
            IReadOnlyDictionary<Vector2Int, int> levels,
            int lowerLevel,
            int higherLevel,
            HashSet<Vector2Int> footprintCells)
        {
            Vector2Int[] expectedLowerLandings = LandingCellsFromPort(origin, placement, setPiece, setPiece.entry);
            Vector2Int[] expectedUpperLandings = LandingCellsFromPort(origin, placement, setPiece, setPiece.exit);
            Vector2Int[] plannedLowerLandings = transition.lowerLandingCells.Length > 0
                ? transition.lowerLandingCells
                : new[] { transition.lowerLandingCell };
            Vector2Int[] plannedUpperLandings = transition.upperLandingCells.Length > 0
                ? transition.upperLandingCells
                : new[] { transition.upperLandingCell };

            if (!CellSetsEqual(expectedLowerLandings, plannedLowerLandings))
            {
                throw new InvalidOperationException(
                    $"Stair prefab '{setPiece.name}' lower landing span mismatch. Expected {FormatCells(expectedLowerLandings)} from entry port, got {FormatCells(plannedLowerLandings)}.");
            }

            if (!CellSetsEqual(expectedUpperLandings, plannedUpperLandings))
            {
                throw new InvalidOperationException(
                    $"Stair prefab '{setPiece.name}' upper landing span mismatch. Expected {FormatCells(expectedUpperLandings)} from exit port, got {FormatCells(plannedUpperLandings)}.");
            }

            foreach (Vector2Int lowerLandingCell in plannedLowerLandings)
            {
                if (!levels.TryGetValue(lowerLandingCell, out int lowerLandingLevel) || lowerLandingLevel != lowerLevel)
                {
                    throw new InvalidOperationException(
                        $"Stair prefab '{setPiece.name}' lower landing {lowerLandingCell} is not present at level {lowerLevel}.");
                }
            }

            foreach (Vector2Int upperLandingCell in plannedUpperLandings)
            {
                if (!levels.TryGetValue(upperLandingCell, out int upperLandingLevel) || upperLandingLevel != higherLevel)
                {
                    throw new InvalidOperationException(
                        $"Stair prefab '{setPiece.name}' upper landing {upperLandingCell} is not present at level {higherLevel}.");
                }
            }

            foreach (Vector2Int lowerLandingCell in plannedLowerLandings)
            {
                if (footprintCells.Contains(lowerLandingCell))
                {
                    throw new InvalidOperationException($"Stair prefab '{setPiece.name}' landing overlaps its footprint.");
                }
            }

            foreach (Vector2Int upperLandingCell in plannedUpperLandings)
            {
                if (footprintCells.Contains(upperLandingCell))
                {
                    throw new InvalidOperationException($"Stair prefab '{setPiece.name}' landing overlaps its footprint.");
                }
            }
        }

        private static Vector2Int LandingCellFromPort(Vector3 origin, ConnectionPointPlacement placement, ConnectionPoint port)
        {
            Vector3 localPoint = port.role == "entry" ? placement.localEntryPoint : placement.localExitPoint;
            Vector3 worldPoint = placement.position + Quaternion.Euler(0f, placement.yRotation, 0f) * localPoint;
            int direction = DirectionFromVector(Rotate2D(DirectionVector(port.direction), placement.yRotation));
            return CellFromWorldPort(origin, worldPoint, direction);
        }

        private static Vector2Int[] LandingCellsFromPort(
            Vector3 origin,
            ConnectionPointPlacement placement,
            ConnectionPointSetPieceContract setPiece,
            ConnectionPoint port)
        {
            Vector2Int[] localCells = port.spanCells == null || port.spanCells.Length == 0
                ? new[] { port.localCell }
                : port.spanCells;
            var cells = new Vector2Int[localCells.Length];
            Quaternion rotation = Quaternion.Euler(0f, placement.yRotation, 0f);
            int direction = DirectionFromVector(Rotate2D(DirectionVector(port.direction), placement.yRotation));
            for (int i = 0; i < localCells.Length; i++)
            {
                Vector3 localPoint = LocalPortCellEdgeCenter(setPiece, localCells[i], port.direction, port.level);
                Vector3 worldPoint = placement.position + rotation * localPoint;
                cells[i] = CellFromWorldPort(origin, worldPoint, direction);
            }

            return cells;
        }

        private static Vector3 LocalPortCellEdgeCenter(
            ConnectionPointSetPieceContract setPiece,
            Vector2Int cell,
            int direction,
            int level)
        {
            float minX = setPiece.localBounds.min.x + cell.x * CellSize;
            float maxX = minX + CellSize;
            float minZ = setPiece.localBounds.min.z + cell.y * CellSize;
            float maxZ = minZ + CellSize;
            float x = (minX + maxX) * 0.5f;
            float z = (minZ + maxZ) * 0.5f;
            switch (direction)
            {
                case Direction.North:
                    z = maxZ;
                    break;
                case Direction.East:
                    x = maxX;
                    break;
                case Direction.South:
                    z = minZ;
                    break;
                case Direction.West:
                    x = minX;
                    break;
            }

            return new Vector3(x, 0f, z);
        }

        private static bool CellSetsEqual(HashSet<Vector2Int> expected, IReadOnlyList<Vector2Int> actual)
        {
            if (actual == null || expected.Count != actual.Count)
            {
                return false;
            }

            foreach (Vector2Int cell in actual)
            {
                if (!expected.Contains(cell))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool CellSetsEqual(IReadOnlyList<Vector2Int> expected, IReadOnlyList<Vector2Int> actual)
        {
            if (expected == null || actual == null || expected.Count != actual.Count)
            {
                return false;
            }

            var expectedSet = new HashSet<Vector2Int>(expected);
            foreach (Vector2Int cell in actual)
            {
                if (!expectedSet.Remove(cell))
                {
                    return false;
                }
            }

            return expectedSet.Count == 0;
        }

        private static string FormatCells(IEnumerable<Vector2Int> cells)
        {
            return cells == null ? "[]" : "[" + string.Join(",", cells) + "]";
        }

        private static Vector3 LocalCellCenter(ConnectionPointSetPieceContract setPiece, Vector2Int cell)
        {
            return new Vector3(
                setPiece.localBounds.min.x + (cell.x + 0.5f) * CellSize,
                setPiece.localBounds.min.y,
                setPiece.localBounds.min.z + (cell.y + 0.5f) * CellSize);
        }

        /// <summary>
        /// Split the plan's declared-open edges into the column table the
        /// existing passes read and the surface-scoped rim table C1b added.
        /// </summary>
        /// <remarks>
        /// <c>OpenEdgeKey</c> deliberately keeps its <c>(cell, direction)</c>
        /// shape. Its producers — transition ports, bridge span ports, internal
        /// path guards — all describe the level field, which IS the column
        /// floor, so giving the key a level would make every one of them write
        /// <c>levels[cell]</c> into it: a rename, not a discriminator, and one
        /// that would break the several producers whose cells are not in the
        /// level field at all. A rim that belongs to a stacked surface is a
        /// genuinely different thing and gets its own table.
        /// </remarks>
        private static void AddPlannedOpenEdges(
            HashSet<OpenEdgeKey> openEdges,
            IReadOnlyCollection<OpenFloorEdge> plannedOpenEdges,
            IReadOnlyDictionary<Vector2Int, int> levels,
            out HashSet<(Vector2Int cell, int level, int direction)> bareStackedRims)
        {
            bareStackedRims = new HashSet<(Vector2Int cell, int level, int direction)>();
            if (plannedOpenEdges == null)
            {
                return;
            }

            foreach (OpenFloorEdge edge in plannedOpenEdges)
            {
                if (!levels.TryGetValue(edge.cell, out int columnFloor))
                {
                    Debug.LogError(
                        $"Dungeon Lab Elevation Edge Model: skipped planned open floor edge because it referenced a missing floor cell: " +
                        $"{edge.cell} dir {DirectionName(edge.direction)}.");
                    continue;
                }

                if (edge.IsSurfaceScoped && edge.level != columnFloor)
                {
                    bareStackedRims.Add((edge.cell, edge.level, edge.direction));
                    continue;
                }

                openEdges.Add(new OpenEdgeKey(edge.cell, edge.direction));
            }
        }

        private static void ValidateRoomBoundaryContext(
            RoomBoundaryContext context,
            ref TieredPlatformBuildStats stats)
        {
            if (context == null)
            {
                return;
            }

            if (context.enclosedRooms == null)
            {
                throw new InvalidOperationException("Room boundary context is missing enclosed room flags.");
            }

            stats.totalRooms = context.enclosedRooms.Count;
            stats.doorways = context.doorwayEdges != null ? context.doorwayEdges.Count : 0;
            stats.internalPathEdges = context.internalPathEdges != null ? context.internalPathEdges.Count : 0;
            var doorwayCounts = new int[context.enclosedRooms.Count];
            if (context.doorwayEdges != null && context.cellRoomIds != null)
            {
                foreach (DoorwayEdge doorway in context.doorwayEdges)
                {
                    if (context.cellRoomIds.TryGetValue(doorway.firstCell, out int firstRoom) &&
                        firstRoom >= 0 &&
                        firstRoom < doorwayCounts.Length)
                    {
                        doorwayCounts[firstRoom]++;
                    }

                    if (context.cellRoomIds.TryGetValue(doorway.secondCell, out int secondRoom) &&
                        secondRoom >= 0 &&
                        secondRoom < doorwayCounts.Length)
                    {
                        doorwayCounts[secondRoom]++;
                    }
                }
            }

            for (int i = 0; i < context.enclosedRooms.Count; i++)
            {
                if (!context.enclosedRooms[i])
                {
                    continue;
                }

                stats.enclosedRooms++;
                if (doorwayCounts[i] <= 0)
                {
                    throw new InvalidOperationException($"Enclosed room {i} has no doorway edge; refusing to seal a room.");
                }
            }

            if (context.internalPathEdges != null)
            {
                foreach (InternalPathEdge edge in context.internalPathEdges)
                {
                    if (!IsCardinalDirection(edge.direction))
                    {
                        throw new InvalidOperationException($"Internal path edge {edge.cell} had invalid direction {edge.direction}.");
                    }
                }
            }

            if (context.gatewayConnectionEnds != null)
            {
                var endKeys = new HashSet<(int connectionIndex, int endIndex)>();
                foreach (GatewayConnectionEnd connectionEnd in
                         context.gatewayConnectionEnds)
                {
                    if (!endKeys.Add(
                            (
                                connectionEnd.roomThreshold.connectionIndex,
                                connectionEnd.endIndex)))
                    {
                        throw new InvalidOperationException(
                            $"Gateway connection end {connectionEnd.roomThreshold.connectionIndex}:{connectionEnd.endIndex} was duplicated.");
                    }

                    if (connectionEnd.outwardPath == null ||
                        connectionEnd.outwardPath.Count < 2 ||
                        !AreCardinalNeighbors(
                            connectionEnd.roomThreshold.firstCell,
                            connectionEnd.roomThreshold.secondCell) ||
                        !(connectionEnd.outwardPath[0] ==
                              connectionEnd.roomThreshold.firstCell &&
                          connectionEnd.outwardPath[1] ==
                              connectionEnd.roomThreshold.secondCell) &&
                        !(connectionEnd.outwardPath[0] ==
                              connectionEnd.roomThreshold.secondCell &&
                          connectionEnd.outwardPath[1] ==
                              connectionEnd.roomThreshold.firstCell))
                    {
                        throw new InvalidOperationException(
                            $"Gateway connection end {connectionEnd.roomThreshold.connectionIndex}:{connectionEnd.endIndex} did not begin at its room threshold.");
                    }
                }
            }
        }

        // Walls and floors have different jobs: a stair footprint replaces the FLOOR
        // surface (and owns the railings on top of it), but it never replaces the WALL
        // shell. Set-piece reservations are full prefab zones and stay fully skipped.
        // Stair footprint cells that exist in the level field (embedded bodies and
        // bridge decks passing over real floor) generate walls like any other cell
        // with their top railings suppressed; bridge connection faces (bridgeSpanEdges
        // ports) render the wall up to the deck's entry level, railing suppressed.
        private static List<WallEdge> BuildWallEdges(
            IReadOnlyDictionary<Vector2Int, int> levels,
            SurfaceColumns surfaceColumns,
            HashSet<Vector2Int> setPieceReservedCells,
            HashSet<Vector2Int> stairFootprintCells,
            HashSet<Vector2Int> allReservedCells,
            HashSet<EdgeKey> transitionKeys,
            HashSet<OpenEdgeKey> transitionOpenEdges,
            HashSet<OpenEdgeKey> bridgeSpanEdges,
            HashSet<(Vector2Int cell, int level, int direction)> bareStackedRims,
            Dictionary<Vector2Int, int> aerialDeckCellLevels,
            RoomBoundaryContext roomBoundaryContext,
            HashSet<Vector2Int> promontoryCells,
            out List<RimEdge> railingOnlyEdges,
            ref TieredPlatformBuildStats stats)
        {
            var wallEdges = new List<WallEdge>();
            railingOnlyEdges = new List<RimEdge>();
            // The visit key is the BOUNDARY's identity — an unordered column
            // pair, or a column and the void beyond one of its faces — and that
            // is right rather than a limitation. §7.1's construction walks the
            // boundary between two COLUMNS and classifies every cut interval in
            // one pass; it never pairs surfaces, which is the whole reason it
            // has none of the nearest-surface shortcut's failure modes. Keying
            // this on a surface would visit the same boundary once per stacked
            // surface and emit its faces twice.
            var visited = new HashSet<string>();
            // Counts SURFACES, not plan cells — the two are the same number only
            // while every column holds one surface, and the renderer places one
            // floor tile per surface.
            stats.floorCells = CountRenderableFloorCells(levels, allReservedCells);
            foreach (StackedSurface surface in surfaceColumns.AllAbove())
            {
                if (!allReservedCells.Contains(surface.cell))
                {
                    stats.floorCells++;
                }
            }

            HashSet<EdgeKey> doorwayKeys = BuildDoorwayKeys(roomBoundaryContext);
            Dictionary<OpenEdgeKey, InternalPathEdgeGuard> internalPathEdgeGuards = BuildInternalPathEdgeGuards(roomBoundaryContext);

            // Decision C: every void-edge cliff face drops to this shared base
            // (the underworld plinth) instead of bottoming at y=0.
            int abyssBase = AbyssBaseForMinFloor(MinFloorLevel(levels));

            foreach (var item in levels)
            {
                Vector2Int cell = item.Key;
                if (setPieceReservedCells.Contains(cell))
                {
                    continue;
                }

                int level = item.Value;
                bool cellIsStair = stairFootprintCells.Contains(cell);
                foreach (int direction in CardinalDirections)
                {
                    Vector2Int neighbor = Neighbor(cell, direction);
                    if (setPieceReservedCells.Contains(neighbor))
                    {
                        continue;
                    }

                    bool neighborIsStair = stairFootprintCells.Contains(neighbor);
                    bool hasNeighbor = levels.TryGetValue(neighbor, out int neighborLevel);
                    string visitKey = hasNeighbor ? new EdgeKey(cell, neighbor).ToString() : $"empty:{cell.x},{cell.y}:{direction}";
                    if (!visited.Add(visitKey))
                    {
                        continue;
                    }

                    if (hasNeighbor && transitionKeys.Contains(new EdgeKey(cell, neighbor)))
                    {
                        continue;
                    }

                    // Bridge connection faces render the wall below the deck entry but
                    // never a railing; they win over fully-open lateral edges. Check
                    // both orientations because either side may be visited first.
                    bool bridgePortEdge =
                        bridgeSpanEdges.Contains(new OpenEdgeKey(cell, direction)) ||
                        (hasNeighbor && bridgeSpanEdges.Contains(new OpenEdgeKey(neighbor, OppositeDirection(direction))));
                    if (!bridgePortEdge &&
                        (transitionOpenEdges.Contains(new OpenEdgeKey(cell, direction)) ||
                            (hasNeighbor && transitionOpenEdges.Contains(new OpenEdgeKey(neighbor, OppositeDirection(direction))))))
                    {
                        continue;
                    }

                    if (!bridgePortEdge &&
                        TryGetInternalPathEdgeGuard(internalPathEdgeGuards, cell, direction, hasNeighbor, neighbor, out PlatformEdge internalPathEdge, out InternalPathEdgeGuard internalPathGuard))
                    {
                        // The guard belongs to whichever side owns the edge, so
                        // it is railed at THAT side's level. Identical to the
                        // `levels[edge cell]` lookup this replaces; stated at
                        // the producer instead of re-derived at the consumer.
                        int internalPathLevel =
                            internalPathEdge.x == cell.x && internalPathEdge.z == cell.y
                                ? level
                                : neighborLevel;
                        ApplyInternalPathEdgeGuard(
                            cell,
                            level,
                            hasNeighbor,
                            neighbor,
                            neighborLevel,
                            abyssBase,
                            internalPathEdge,
                            internalPathLevel,
                            internalPathGuard,
                            wallEdges,
                            railingOnlyEdges,
                            ref stats);
                        continue;
                    }

                    // The band decomposition (§7.1 step 1) decides the face. For
                    // a single-layer field every column's mass is
                    // `[abyssBase, level)`, so this reproduces the level compare
                    // it replaces exactly — see the notes on DecomposeBoundary.
                    ColumnMass cellMass = ComputeColumnMass(
                        hasSurface: true,
                        lowestLevel: level,
                        lowestIsGroundBacked: IsGroundBackedSurface(levels, aerialDeckCellLevels, cell, level));
                    ColumnMass neighborMass = ComputeColumnMass(
                        hasSurface: hasNeighbor,
                        lowestLevel: neighborLevel,
                        lowestIsGroundBacked: hasNeighbor &&
                            IsGroundBackedSurface(levels, aerialDeckCellLevels, neighbor, neighborLevel));

                    if (hasNeighbor)
                    {
                        BoundaryFace face = DecomposeBoundary(
                            cellMass,
                            neighborMass,
                            neighborHasSurface: true,
                            neighborLowestLevel: neighborLevel,
                            cellHasSurface: true,
                            cellLowestLevel: level,
                            abyssBase: abyssBase);

                        if (!face.hasFace)
                        {
                            // Interior (both solid) or open air (neither). A
                            // partition still stands between two floors at one
                            // level; open air between stacked surfaces is the
                            // case that keeps a chamber under a gallery open,
                            // and it carries no partition.
                            if (level == neighborLevel &&
                                IsPartitionWallEdge(roomBoundaryContext, doorwayKeys, cell, neighbor, otherSideIsFloor: true))
                            {
                                wallEdges.Add(new WallEdge(EdgeFromCellToward(GetPartitionOwnerCell(roomBoundaryContext, cell, neighbor), GetPartitionOtherCell(roomBoundaryContext, cell, neighbor)), level, level + 1, false, true));
                                stats.partitionWalls++;
                                continue;
                            }

                            stats.interiorEdges++;
                            continue;
                        }

                        Vector2Int solidCell = face.solidSideIsCell ? cell : neighbor;
                        Vector2Int openCell = face.solidSideIsCell ? neighbor : cell;
                        bool solidSideIsStair = face.solidSideIsCell ? cellIsStair : neighborIsStair;
                        // A FLUSH SEAM: the open side carries a walkable surface
                        // level with the top of this face, so the two sides are
                        // one continuous walk and the face's top belongs to that
                        // surface. The face itself still renders — from the
                        // chamber below, a gallery's terrace really is a wall
                        // from the floor up — but it must take no guard and no
                        // shell course, or the dungeon walls its own upper route
                        // shut. Measured live 2026-07-31: without this the shell
                        // pass put a 5.7u enclosure wall across the seam where a
                        // ground-backed terrace meets a suspended gallery at the
                        // same level, and the probe player could not cross it.
                        //
                        // Inert on a single-layer plan, and provably so rather
                        // than by measurement: a retaining face has the open
                        // side's floor at its BOTTOM, and a cliff has it at or
                        // below the bottom, so no single-layer face can have a
                        // surface at its top on the open side.
                        bool flushSeam = surfaceColumns.HasSurfaceAt(openCell, face.higherLevel);
                        wallEdges.Add(new WallEdge(
                            EdgeFromCellToward(solidCell, openCell),
                            face.lowerLevel,
                            face.higherLevel,
                            face.isRetaining,
                            false,
                            suppressRailing: solidSideIsStair || bridgePortEdge || flushSeam));
                        if (face.isRetaining)
                        {
                            stats.retainingEdges++;
                        }
                        else
                        {
                            stats.cliffEdges++;
                        }

                        continue;
                    }

                    // Decision J: a promontory pier is an OPEN deck on a column
                    // forest — no solid cliff face and no railing on its exposed
                    // sides. PlacePromontoryPiers renders its wall-cover facing
                    // and columns instead.
                    if (promontoryCells.Contains(cell))
                    {
                        continue;
                    }

                    bool deckOwnsEdgeTop = cellIsStair || bridgePortEdge;
                    if (level <= 0)
                    {
                        // A bridge port face is the deck's entrance: the partition
                        // (which rises ABOVE the floor, unlike the drop-face walls
                        // the deck lands on) must not wall it shut.
                        if (!deckOwnsEdgeTop && IsPartitionWallEdge(roomBoundaryContext, doorwayKeys, cell, neighbor, otherSideIsFloor: false))
                        {
                            wallEdges.Add(new WallEdge(new PlatformEdge(cell.x, cell.y, direction), level, level + 1, false, true));
                            stats.partitionWalls++;
                        }
                        else if (!deckOwnsEdgeTop)
                        {
                            railingOnlyEdges.Add(new RimEdge(new PlatformEdge(cell.x, cell.y, direction), level));
                        }

                        // Decision C: a ground-or-below floor edge facing void still
                        // sits on the underworld — the cliff face drops to the abyss
                        // base beneath it. The top guard (railing/partition above) is
                        // handled just above, so this cliff suppresses its own railing.
                        if (level > abyssBase)
                        {
                            wallEdges.Add(new WallEdge(new PlatformEdge(cell.x, cell.y, direction), abyssBase, level, false, false, suppressRailing: true));
                            stats.cliffEdges++;
                        }

                        continue;
                    }

                    // A floor edge facing an aerial deck cell at the SAME level is
                    // even with the deck — no railing (user rule 2026-06-12); the
                    // wall below stays per the invariant.
                    bool evenWithAerialDeck =
                        aerialDeckCellLevels.TryGetValue(neighbor, out int adjacentDeckLevel) &&
                        adjacentDeckLevel == level;
                    // Decision C: the cliff face drops to the shared abyss base
                    // (was y=0) so the whole dungeon rises from a deep plinth.
                    wallEdges.Add(new WallEdge(new PlatformEdge(cell.x, cell.y, direction), abyssBase, level, false, false, suppressRailing: deckOwnsEdgeTop || evenWithAerialDeck));
                    stats.cliffEdges++;
                }
            }

            AddStackedSurfaceRims(surfaceColumns, bareStackedRims, railingOnlyEdges, ref stats);
            return wallEdges;
        }

        /// <summary>
        /// Guard every lateral edge of a surface that stands above its column
        /// floor (design §7.1 step 1's guard rule, §5's bare rim).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A suspended surface emits no <c>WallEdge</c> at all — it has no
        /// structural band, and the 0.5u underside is the <c>_E_</c> floor
        /// family's own closed slab rather than a wall face (owner ruling, C1a).
        /// Its edges therefore never reach the wall loop, and without this pass
        /// a gallery would be an unguarded slab: the one thing the wall walk
        /// cannot do for it is exactly the one thing it needs.
        /// </para>
        /// <para>
        /// The rule is per (surface, direction): a rim exists wherever the
        /// neighbouring COLUMN carries no surface at this surface's own level.
        /// It does not ask what the neighbour's floor is — a gallery beside a
        /// ground floor four levels down is still a rim, and the retaining face
        /// between the two floors below is a separate, already-emitted thing.
        /// </para>
        /// <para>
        /// <c>bare</c> is what makes an aperture an aperture. The rim around a
        /// hole in an upper route must stay open or you cannot fall through it,
        /// and §5 makes that a per-(rim surface, direction) property rather than
        /// a property of the opening, so a pit railed on three sides and bare on
        /// one is expressible.
        /// </para>
        /// </remarks>
        private static void AddStackedSurfaceRims(
            SurfaceColumns surfaceColumns,
            HashSet<(Vector2Int cell, int level, int direction)> bareStackedRims,
            List<RimEdge> railingOnlyEdges,
            ref TieredPlatformBuildStats stats)
        {
            if (surfaceColumns == null || surfaceColumns.IsSingleLayer)
            {
                return;
            }

            foreach (StackedSurface surface in surfaceColumns.AllAbove())
            {
                stats.stackedSurfaces++;
                foreach (int direction in CardinalDirections)
                {
                    Vector2Int neighbor = Neighbor(surface.cell, direction);
                    if (surfaceColumns.HasSurfaceAt(neighbor, surface.level))
                    {
                        continue;
                    }

                    bool bare = bareStackedRims != null &&
                        bareStackedRims.Contains((surface.cell, surface.level, direction));
                    railingOnlyEdges.Add(new RimEdge(
                        new PlatformEdge(surface.cell.x, surface.cell.y, direction),
                        surface.level,
                        bare));
                    if (bare)
                    {
                        stats.stackedBareRims++;
                        stats.bareBoundaryEdges++;
                    }
                    else
                    {
                        stats.stackedRailedRims++;
                    }
                }
            }
        }

        private static Dictionary<OpenEdgeKey, InternalPathEdgeGuard> BuildInternalPathEdgeGuards(RoomBoundaryContext context)
        {
            var guards = new Dictionary<OpenEdgeKey, InternalPathEdgeGuard>();
            if (context?.internalPathEdges == null)
            {
                return guards;
            }

            foreach (InternalPathEdge edge in context.internalPathEdges)
            {
                var key = new OpenEdgeKey(edge.cell, edge.direction);
                if (!guards.TryGetValue(key, out InternalPathEdgeGuard existing) ||
                    existing == InternalPathEdgeGuard.Bare && edge.guard == InternalPathEdgeGuard.Railing)
                {
                    guards[key] = edge.guard;
                }
            }

            return guards;
        }

        private static bool TryGetInternalPathEdgeGuard(
            Dictionary<OpenEdgeKey, InternalPathEdgeGuard> guards,
            Vector2Int cell,
            int direction,
            bool hasNeighbor,
            Vector2Int neighbor,
            out PlatformEdge edge,
            out InternalPathEdgeGuard guard)
        {
            if (guards.TryGetValue(new OpenEdgeKey(cell, direction), out guard))
            {
                edge = new PlatformEdge(cell.x, cell.y, direction);
                return true;
            }

            if (hasNeighbor && guards.TryGetValue(new OpenEdgeKey(neighbor, OppositeDirection(direction)), out guard))
            {
                edge = new PlatformEdge(neighbor.x, neighbor.y, OppositeDirection(direction));
                return true;
            }

            edge = default;
            guard = InternalPathEdgeGuard.Bare;
            return false;
        }

        private static void ApplyInternalPathEdgeGuard(
            Vector2Int cell,
            int level,
            bool hasNeighbor,
            Vector2Int neighbor,
            int neighborLevel,
            int abyssBase,
            PlatformEdge edge,
            int edgeLevel,
            InternalPathEdgeGuard guard,
            List<WallEdge> wallEdges,
            List<RimEdge> railingOnlyEdges,
            ref TieredPlatformBuildStats stats)
        {
            if (guard == InternalPathEdgeGuard.Railing)
            {
                railingOnlyEdges.Add(new RimEdge(edge, edgeLevel));
                stats.internalPathRailings++;
            }
            else
            {
                stats.internalPathBareEdges++;
                stats.bareBoundaryEdges++;
            }

            if (hasNeighbor)
            {
                if (level == neighborLevel)
                {
                    return;
                }

                Vector2Int higherCell = level > neighborLevel ? cell : neighbor;
                Vector2Int lowerCell = level > neighborLevel ? neighbor : cell;
                int higherLevel = Mathf.Max(level, neighborLevel);
                int lowerLevel = Mathf.Min(level, neighborLevel);
                wallEdges.Add(new WallEdge(
                    EdgeFromCellToward(higherCell, lowerCell),
                    lowerLevel,
                    higherLevel,
                    true,
                    false,
                    suppressRailing: true));
                stats.retainingEdges++;
                return;
            }

            if (level > abyssBase)
            {
                wallEdges.Add(new WallEdge(edge, abyssBase, level, false, false, suppressRailing: true));
                stats.cliffEdges++;
            }
        }

        private static HashSet<Vector2Int> BuildReservedSetPieceCellSet(IReadOnlyCollection<Vector2Int> reservedSetPieceCells)
        {
            return reservedSetPieceCells != null
                ? new HashSet<Vector2Int>(reservedSetPieceCells)
                : new HashSet<Vector2Int>();
        }

        // ------------------------------------------------------------------
        // Boundary band decomposition — Phase C of the layered 3D topology
        // design (§7.1 step 1), the replacement for "compare two levels".
        //
        // A boundary between two plan columns is decided by where SOLID MASS
        // exists in each column, not by comparing one level to another. The
        // draft's "nearest surface" shortcut was rejected in review because it
        // ties, is asymmetric and can be one-to-many; the band walk has none of
        // those problems because it never pairs surfaces at all.
        //
        // WHAT COUNTS AS MASS, and this is the correction that carries the pit
        // design: an occupied band is STRUCTURAL, not "down to whatever supports
        // it". Treating a surface's band as level -> support fills stacked space
        // with solid mass and walls off the very chamber a gallery is meant to
        // overlook.
        //
        //   > IsGroundBacked(s) = s is a Floor AND s is the lowest surface in
        //   > its column.
        //
        // Both conditions are load-bearing. A bridge deck over a true gap IS
        // lowest in its column but must not become a solid pillar — the kind
        // test excludes it. A gallery slab over its room's own lower chamber IS
        // a Floor but is not lowest — the column test excludes it.
        //
        // A suspended surface therefore contributes NO wall mass. Its 0.5u
        // underside is real geometry, but it is the `_E_` floor family's own
        // closed slab (§0.1, measured: `_E_` is the `_O_` top surface plus a
        // bottom, 4u x 0.5u x 4u, hanging entirely below the walk surface), not
        // a wall face. That is why no fractional band reaches `WallEdge`, whose
        // levels are integers, and why the fascia and the soffit are the same
        // change rather than two.
        //
        // WHY SINGLE-LAYER OUTPUT CANNOT MOVE. With one surface per column every
        // surface is a lowest-in-column Floor, so every column's mass is exactly
        // `[abyssBase, level)` and the walk yields:
        //
        //   equal levels          -> both solid throughout -> interior, no face
        //   levels a < b          -> [a, b) solid on one side -> ONE face whose
        //                            open side has a surface at the interval's
        //                            bottom -> retaining, extent a..b
        //   neighbour is void     -> [abyss, a) solid on one side, open side has
        //                            no surface at the bottom -> cliff, extent
        //                            abyssBase..a
        //
        // which is the three cases the old code wrote out by hand, with the same
        // extents and the same types. The decomposition bites only where columns
        // carry more than one surface.
        // ------------------------------------------------------------------

        /// <summary>
        /// The solid mass a plan column contributes to its boundaries.
        /// </summary>
        /// <remarks>
        /// At most one band today: the ground band `[abyssBase, groundTop)`,
        /// present only when the column's lowest surface is ground-backed.
        /// Support and Wall prisms (§7.1's other two band sources) are authored
        /// and have no producer yet, so they add nothing here.
        /// </remarks>
        private readonly struct ColumnMass
        {
            public readonly bool hasGround;
            public readonly int groundTop;      // exclusive top of [abyssBase, groundTop)

            private ColumnMass(bool hasGround, int groundTop)
            {
                this.hasGround = hasGround;
                this.groundTop = groundTop;
            }

            public static ColumnMass None => new ColumnMass(false, 0);

            public static ColumnMass Ground(int top)
            {
                return new ColumnMass(true, top);
            }

            /// <summary>Is this column solid immediately above <paramref name="level"/>?</summary>
            public bool IsSolidAbove(int level, int abyssBase)
            {
                return hasGround && level >= abyssBase && level < groundTop;
            }
        }

        /// <summary>
        /// What the boundary walk decided for one column pair.
        /// </summary>
        private readonly struct BoundaryFace
        {
            public readonly bool hasFace;
            public readonly int lowerLevel;
            public readonly int higherLevel;
            public readonly bool isRetaining;
            // True when the SOLID side is the cell being visited, so the caller
            // knows which way the face points without recomparing levels.
            public readonly bool solidSideIsCell;

            public BoundaryFace(bool hasFace, int lowerLevel, int higherLevel, bool isRetaining, bool solidSideIsCell)
            {
                this.hasFace = hasFace;
                this.lowerLevel = lowerLevel;
                this.higherLevel = higherLevel;
                this.isRetaining = isRetaining;
                this.solidSideIsCell = solidSideIsCell;
            }

            public static BoundaryFace None => new BoundaryFace(false, 0, 0, false, false);
        }

        /// <summary>
        /// `IsGroundBacked(s)`: the surface is a Floor AND the lowest in its
        /// column, so the mass under it is earth rather than open air (§7.1).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Unconditionally true for every entry in today's level field, and that
        /// is a FACT about the pipeline rather than an assumption this code
        /// makes. `TrySetPlannedStairCells` refuses to floor a span deck's or a
        /// stairwell tower's footprint — "the gap stays a gap (span) and the
        /// tower stands on void" — so the one suspended surface the generator
        /// produces never reaches `levels` at its own height. A deck cell either
        /// carries the pass-under floor beneath it or carries nothing at all.
        /// That is why the band decomposition reproduces today's walls exactly.
        /// </para>
        /// <para>
        /// It is written as the real predicate rather than `return true` so the
        /// phase that puts a second surface in a column gets the right answer
        /// from the same call site instead of a constant somebody has to
        /// remember to revisit.
        /// </para>
        /// </remarks>
        private static bool IsGroundBackedSurface(
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyDictionary<Vector2Int, int> aerialDeckCellLevels,
            Vector2Int cell,
            int level)
        {
            // A suspended deck standing at this cell's recorded height is not
            // ground-backed; anything else in the field is the bottom of its
            // column and rests on fill.
            return !(aerialDeckCellLevels != null &&
                aerialDeckCellLevels.TryGetValue(cell, out int deckLevel) &&
                deckLevel == level);
        }

        /// <summary>
        /// The mass a column contributes, from the surfaces standing in it.
        /// </summary>
        /// <remarks>
        /// <paramref name="hasSurface"/> false means a void column — no surface,
        /// no mass, which is what produces a cliff on the other side.
        /// </remarks>
        private static ColumnMass ComputeColumnMass(bool hasSurface, int lowestLevel, bool lowestIsGroundBacked)
        {
            return hasSurface && lowestIsGroundBacked
                ? ColumnMass.Ground(lowestLevel)
                : ColumnMass.None;
        }

        /// <summary>
        /// Walk the boundary between two columns bottom to top and classify each
        /// cut interval by which side is solid (§7.1 step 1).
        /// </summary>
        /// <remarks>
        /// Both solid is interior and emits nothing; neither solid is open air
        /// and emits nothing — that second case is what keeps a chamber under a
        /// gallery open. One solid emits a face on the solid side, typed by what
        /// stands at the interval's BOTTOM on the open side: a surface level with
        /// it means the face retains that floor, no surface means it drops away
        /// as a cliff.
        /// <para>
        /// Only the topmost such interval can produce a face while the only band
        /// source is the ground band, because two ground bands share the same
        /// floor `abyssBase` and can differ only at the top. The walk is written
        /// as a walk anyway: the moment a Support or Wall prism gets a producer,
        /// the extra intervals classify with no further work here.
        /// </para>
        /// </remarks>
        private static BoundaryFace DecomposeBoundary(
            ColumnMass cellMass,
            ColumnMass neighborMass,
            bool neighborHasSurface,
            int neighborLowestLevel,
            bool cellHasSurface,
            int cellLowestLevel,
            int abyssBase)
        {
            // Cut levels: every band endpoint on either side, ascending.
            var cuts = new SortedSet<int> { abyssBase };
            if (cellMass.hasGround)
            {
                cuts.Add(cellMass.groundTop);
            }

            if (neighborMass.hasGround)
            {
                cuts.Add(neighborMass.groundTop);
            }

            int[] ordered = new int[cuts.Count];
            cuts.CopyTo(ordered);

            for (int i = 0; i < ordered.Length - 1; i++)
            {
                int bottom = ordered[i];
                int top = ordered[i + 1];
                bool cellSolid = cellMass.IsSolidAbove(bottom, abyssBase);
                bool neighborSolid = neighborMass.IsSolidAbove(bottom, abyssBase);
                if (cellSolid == neighborSolid)
                {
                    // both solid -> interior; neither -> open air. No geometry.
                    continue;
                }

                // The open side is whichever is not solid. The face is retaining
                // when that side has a walkable surface level with the interval's
                // bottom, and a cliff when it has nothing there.
                bool openSideHasSurfaceAtBottom = cellSolid
                    ? neighborHasSurface && neighborLowestLevel == bottom
                    : cellHasSurface && cellLowestLevel == bottom;
                return new BoundaryFace(true, bottom, top, openSideHasSurfaceAtBottom, cellSolid);
            }

            return BoundaryFace.None;
        }

        // Lowest floor level in the dungeon — the anchor for the decision-C abyss
        // base (the whole mass drops to AbyssDepthLevels below this).
        private static int MinFloorLevel(IReadOnlyDictionary<Vector2Int, int> levels)
        {
            int min = 0;
            bool any = false;
            foreach (int level in levels.Values)
            {
                if (!any || level < min)
                {
                    min = level;
                    any = true;
                }
            }

            return min;
        }

        private static int CountRenderableFloorCells(
            IReadOnlyDictionary<Vector2Int, int> levels,
            HashSet<Vector2Int> reservedCells)
        {
            int count = 0;
            foreach (Vector2Int cell in levels.Keys)
            {
                if (!reservedCells.Contains(cell))
                {
                    count++;
                }
            }

            return count;
        }

        private static HashSet<EdgeKey> BuildDoorwayKeys(RoomBoundaryContext context)
        {
            var keys = new HashSet<EdgeKey>();
            if (context == null || context.doorwayEdges == null)
            {
                return keys;
            }

            foreach (DoorwayEdge doorway in context.doorwayEdges)
            {
                if (!AreCardinalNeighbors(doorway.firstCell, doorway.secondCell))
                {
                    throw new InvalidOperationException($"Doorway cells must share one edge: {doorway.firstCell} <-> {doorway.secondCell}.");
                }

                keys.Add(new EdgeKey(doorway.firstCell, doorway.secondCell));
            }

            return keys;
        }

        private static bool IsPartitionWallEdge(
            RoomBoundaryContext context,
            HashSet<EdgeKey> doorwayKeys,
            Vector2Int cell,
            Vector2Int neighbor,
            bool otherSideIsFloor)
        {
            if (context == null ||
                context.cellRoomIds == null ||
                context.enclosedRooms == null ||
                doorwayKeys.Contains(new EdgeKey(cell, neighbor)))
            {
                return false;
            }

            int cellRoom = GetRoomId(context, cell);
            int neighborRoom = GetRoomId(context, neighbor);
            bool cellEnclosed = IsEnclosedRoom(context, cellRoom);
            bool neighborEnclosed = IsEnclosedRoom(context, neighborRoom);
            if (!cellEnclosed && !neighborEnclosed)
            {
                return false;
            }

            if (otherSideIsFloor && (cellRoom < 0 || neighborRoom < 0))
            {
                return false;
            }

            if (!otherSideIsFloor)
            {
                return cellEnclosed || neighborEnclosed;
            }

            return cellRoom != neighborRoom;
        }

        private static Vector2Int GetPartitionOwnerCell(RoomBoundaryContext context, Vector2Int first, Vector2Int second)
        {
            int firstRoom = GetRoomId(context, first);
            if (IsEnclosedRoom(context, firstRoom))
            {
                return first;
            }

            return second;
        }

        private static Vector2Int GetPartitionOtherCell(RoomBoundaryContext context, Vector2Int first, Vector2Int second)
        {
            int firstRoom = GetRoomId(context, first);
            if (IsEnclosedRoom(context, firstRoom))
            {
                return second;
            }

            return first;
        }

        private static int GetRoomId(RoomBoundaryContext context, Vector2Int cell)
        {
            return context.cellRoomIds != null && context.cellRoomIds.TryGetValue(cell, out int roomId) ? roomId : -1;
        }

        private static bool IsEnclosedRoom(RoomBoundaryContext context, int roomId)
        {
            return roomId >= 0 &&
                context.enclosedRooms != null &&
                roomId < context.enclosedRooms.Count &&
                context.enclosedRooms[roomId];
        }

        private static void PlaceElevationWallStack(
            DropFaceStack stack,
            Transform parent,
            WallEdge wallEdge,
            Vector3 origin,
            float levelHeight,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            Vector3 cellMin = CellMin(origin, wallEdge.edge.x, wallEdge.edge.z, wallEdge.lowerLevel * levelHeight);
            Vector3 cellMax = cellMin + new Vector3(CellSize, 0f, CellSize);
            GetEdgePlacement(wallEdge.edge, cellMin, cellMax, out Vector3 edgeA, out Vector3 edgeB, out Vector2 outwardNormal);
            // Walls compose by world height in whole masonry courses anchored at the
            // TOP of the drop, so the face under the upper floor edge is always
            // closed (the pack's one-sided floors have no skirts — any reveal reads
            // as a hole). The pack has no 1u course, so an odd drop's stack sinks
            // its bottom course 1u below the lower floor (or below ground at a
            // cliff), hidden inside the tier mass.
            float dropHeight = (wallEdge.higherLevel - wallEdge.lowerLevel) * levelHeight;
            int courseCount = Mathf.CeilToInt((dropHeight - 0.01f) / stack.totalHeight);
            if (courseCount < 0)
            {
                throw new InvalidOperationException(
                    $"Wall drop {dropHeight:0.###}u at edge ({wallEdge.edge.x},{wallEdge.edge.z}) is not composable from " +
                    $"{stack.totalHeight:0.###}u courses.");
            }

            float yOffset = dropHeight - courseCount * stack.totalHeight;
            for (int course = 0; course < courseCount; course++)
            {
                for (int i = 0; i < stack.pieces.Length; i++)
                {
                    MeasuredPrefab piece = stack.pieces[i];
                    string wallKind = wallEdge.isRetaining ? "retaining" : "cliff";
                    PlaceEdgePrefab(
                        piece,
                        parent,
                        $"{wallKind}_{DirectionName(wallEdge.edge.direction).ToLowerInvariant()}_{wallEdge.edge.x}_{wallEdge.edge.z}_{course}_{i}",
                        edgeA + Vector3.up * yOffset,
                        edgeB + Vector3.up * yOffset,
                        outwardNormal,
                        ref bounds,
                        ref hasBounds);
                    yOffset += piece.height;
                }
            }
        }

        private static void PlacePartitionWall(
            PartitionWallContracts contracts,
            Transform parent,
            WallEdge wallEdge,
            Vector3 origin,
            float levelHeight,
            ref Bounds bounds,
            ref bool hasBounds,
            ref TieredPlatformBuildStats stats)
        {
            MeasuredPrefab wall = contracts.ForHeight(wallEdge.partitionHeightUnits);
            Vector3 cellMin = CellMin(origin, wallEdge.edge.x, wallEdge.edge.z, wallEdge.lowerLevel * levelHeight);
            Vector3 cellMax = cellMin + new Vector3(CellSize, 0f, CellSize);
            GetEdgePlacement(wallEdge.edge, cellMin, cellMax, out Vector3 edgeA, out Vector3 edgeB, out Vector2 outwardNormal);
            GameObject instance = PlaceEdgePrefab(
                wall,
                parent,
                $"partition_{DirectionName(wallEdge.edge.direction).ToLowerInvariant()}_{wallEdge.edge.x}_{wallEdge.edge.z}",
                edgeA,
                edgeB,
                outwardNormal,
                ref bounds,
                ref hasBounds);
            ValidatePlacedEdgePrefab(instance, wall, edgeA, edgeB, outwardNormal);
            stats.partitionWallChecks++;
        }

        private static Dictionary<int, List<PlatformEdge>> GroupWallEdgesByLevel(List<WallEdge> wallEdges, bool includePartitions)
        {
            var groups = new Dictionary<int, List<PlatformEdge>>();
            foreach (WallEdge wallEdge in wallEdges)
            {
                if (!includePartitions && wallEdge.isPartition)
                {
                    continue;
                }

                if (!groups.TryGetValue(wallEdge.higherLevel, out List<PlatformEdge> edges))
                {
                    edges = new List<PlatformEdge>();
                    groups[wallEdge.higherLevel] = edges;
                }

                edges.Add(wallEdge.edge);
            }

            return groups;
        }

        /// <summary>
        /// Railing corner columns are placed per level, so the rims group by the
        /// level they were emitted AT.
        /// </summary>
        /// <remarks>
        /// This used to look each edge's cell up in the level field, which is a
        /// COLUMN query: correct while every column held one surface, and wrong
        /// the moment one holds two, because a gallery rim and the chamber rim
        /// under it would have grouped together and stacked their corner columns
        /// at the chamber's height. The rim now carries the answer. Every
        /// existing producer supplies exactly what the lookup returned, so the
        /// grouping is unchanged for a single-layer plan — including the
        /// skip-if-absent case, which could only fire for a cell outside the
        /// level field and no producer creates one.
        /// </remarks>
        /// <summary>
        /// Does this rim belong to its column's floor — the only surface the
        /// heightfield-keyed edge tables can be describing?
        /// </summary>
        private static bool IsColumnFloorRim(IReadOnlyDictionary<Vector2Int, int> levels, RimEdge rim)
        {
            return levels.TryGetValue(new Vector2Int(rim.edge.x, rim.edge.z), out int columnFloor) &&
                columnFloor == rim.level;
        }

        private static Dictionary<int, List<PlatformEdge>> GroupRailingEdgesByLevel(
            List<RimEdge> railingEdges)
        {
            var groups = new Dictionary<int, List<PlatformEdge>>();
            foreach (RimEdge rim in railingEdges)
            {
                if (!groups.TryGetValue(rim.level, out List<PlatformEdge> edges))
                {
                    edges = new List<PlatformEdge>();
                    groups[rim.level] = edges;
                }

                edges.Add(rim.edge);
            }

            return groups;
        }

        private static List<PlatformEdge> ToPlatformEdges(List<WallEdge> wallEdges)
        {
            var edges = new List<PlatformEdge>();
            foreach (WallEdge wallEdge in wallEdges)
            {
                edges.Add(wallEdge.edge);
            }

            return edges;
        }

        private static List<CornerPlacement> BuildCornerPlacements(List<WallEdge> wallEdges, HashSet<Vector2Int> reservedCells)
        {
            var byVertex = new Dictionary<string, List<WallEdge>>();
            var vertices = new Dictionary<string, Vector2Int>();
            var levels = new Dictionary<string, int>();
            foreach (WallEdge wallEdge in wallEdges)
            {
                GetEdgeVertices(wallEdge.edge, out Vector2Int first, out Vector2Int second);
                AddVertexEdge(byVertex, vertices, levels, first, wallEdge);
                AddVertexEdge(byVertex, vertices, levels, second, wallEdge);
            }

            var corners = new List<CornerPlacement>();
            foreach (var item in byVertex)
            {
                Vector2Int vertex = vertices[item.Key];
                if (VertexTouchesReservedSetPiece(vertex, reservedCells))
                {
                    continue;
                }

                if (item.Value.Count < 2)
                {
                    continue;
                }

                if (TryFindCornerTurnEdges(item.Value, vertex, isPartition: true, out int partitionQuadrant))
                {
                    corners.Add(new CornerPlacement(
                        vertex,
                        partitionQuadrant,
                        levels[item.Key],
                        MinDropLevels(item.Value, isPartition: true),
                        isPartition: true,
                        partitionHeightUnits: MaxPartitionHeightUnits(item.Value)));
                }

                if (TryFindCornerTurnEdges(item.Value, vertex, isPartition: false, out int retainingQuadrant))
                {
                    corners.Add(new CornerPlacement(
                        vertex,
                        retainingQuadrant,
                        levels[item.Key],
                        MinDropLevels(item.Value, isPartition: false),
                        isPartition: false,
                        partitionHeightUnits: 0));
                }
            }

            return corners;
        }

        private static int MaxPartitionHeightUnits(IReadOnlyList<WallEdge> edges)
        {
            int height = 4;
            foreach (WallEdge edge in edges)
            {
                if (edge.isPartition)
                {
                    height = Mathf.Max(height, edge.partitionHeightUnits);
                }
            }

            return height;
        }

        private static int MinDropLevels(List<WallEdge> edges, bool isPartition)
        {
            int minDrop = int.MaxValue;
            foreach (WallEdge edge in edges)
            {
                if (edge.isPartition != isPartition)
                {
                    continue;
                }

                minDrop = Mathf.Min(minDrop, edge.higherLevel - edge.lowerLevel);
            }

            return minDrop == int.MaxValue ? 1 : minDrop;
        }

        private static bool TryFindCornerTurnEdges(
            List<WallEdge> edges,
            Vector2Int vertex,
            bool isPartition,
            out int quadrant)
        {
            quadrant = default;
            var candidates = new List<CornerTurnCandidate>();
            for (int i = 0; i < edges.Count; i++)
            {
                if (edges[i].isPartition != isPartition)
                {
                    continue;
                }

                Vector2Int firstDirection = EdgeDirectionFromVertex(edges[i].edge, vertex);
                if (firstDirection == Vector2Int.zero)
                {
                    continue;
                }

                candidates.Add(new CornerTurnCandidate(edges[i], firstDirection));
            }

            SortCornerTurnCandidates(candidates);
            for (int i = 0; i < candidates.Count; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    Vector2Int firstDirection = candidates[i].direction;
                    Vector2Int secondDirection = candidates[j].direction;
                    if (firstDirection.x * secondDirection.x + firstDirection.y * secondDirection.y != 0)
                    {
                        continue;
                    }

                    Vector2 quadrantOffset = new Vector2(
                        firstDirection.x + secondDirection.x,
                        firstDirection.y + secondDirection.y);
                    quadrant = QuadrantFromOffset(quadrantOffset, 0.05f);
                    return true;
                }
            }

            return false;
        }

        private static void SortCornerTurnCandidates(List<CornerTurnCandidate> candidates)
        {
            candidates.Sort(CompareCornerTurnCandidates);
        }

        private static int CompareCornerTurnCandidates(CornerTurnCandidate first, CornerTurnCandidate second)
        {
            int value = DirectionSortOrder(first.direction).CompareTo(DirectionSortOrder(second.direction));
            if (value != 0)
            {
                return value;
            }

            value = first.edge.edge.x.CompareTo(second.edge.edge.x);
            if (value != 0)
            {
                return value;
            }

            value = first.edge.edge.z.CompareTo(second.edge.edge.z);
            if (value != 0)
            {
                return value;
            }

            value = first.edge.edge.direction.CompareTo(second.edge.edge.direction);
            if (value != 0)
            {
                return value;
            }

            value = first.edge.higherLevel.CompareTo(second.edge.higherLevel);
            if (value != 0)
            {
                return value;
            }

            value = first.edge.isRetaining.CompareTo(second.edge.isRetaining);
            if (value != 0)
            {
                return value;
            }

            return first.edge.suppressRailing.CompareTo(second.edge.suppressRailing);
        }

        private static int DirectionSortOrder(Vector2Int direction)
        {
            if (direction == Vector2Int.up)
            {
                return 0;
            }

            if (direction == Vector2Int.right)
            {
                return 1;
            }

            if (direction == Vector2Int.down)
            {
                return 2;
            }

            if (direction == Vector2Int.left)
            {
                return 3;
            }

            return 4;
        }

        private static bool VertexTouchesReservedSetPiece(Vector2Int vertex, HashSet<Vector2Int> reservedCells)
        {
            if (reservedCells == null || reservedCells.Count == 0)
            {
                return false;
            }

            return reservedCells.Contains(new Vector2Int(vertex.x - 1, vertex.y - 1)) ||
                reservedCells.Contains(new Vector2Int(vertex.x, vertex.y - 1)) ||
                reservedCells.Contains(new Vector2Int(vertex.x - 1, vertex.y)) ||
                reservedCells.Contains(new Vector2Int(vertex.x, vertex.y));
        }

        private static Vector2Int EdgeDirectionFromVertex(PlatformEdge edge, Vector2Int vertex)
        {
            GetEdgeVertices(edge, out Vector2Int first, out Vector2Int second);
            if (vertex == first)
            {
                return second - first;
            }

            if (vertex == second)
            {
                return first - second;
            }

            return Vector2Int.zero;
        }

        private static void AddVertexEdge(
            Dictionary<string, List<WallEdge>> byVertex,
            Dictionary<string, Vector2Int> vertices,
            Dictionary<string, int> levels,
            Vector2Int vertex,
            WallEdge edge)
        {
            string key = $"{vertex.x},{vertex.y}:{edge.lowerLevel}";
            if (!byVertex.TryGetValue(key, out List<WallEdge> edges))
            {
                edges = new List<WallEdge>();
                byVertex[key] = edges;
                vertices[key] = vertex;
                levels[key] = edge.lowerLevel;
            }

            edges.Add(edge);
        }

        private static RailingContracts BuildRailingContracts(PackageInventory inventory, MeasuredPrefab floor)
        {
            MeasuredPrefab railing = MeasurePrefab(inventory.GetPrefabPath(RailingName), PrefabRole.Railing);
            MeasuredPrefab column = MeasurePrefab(inventory.GetPrefabPath(RailingColumnName), PrefabRole.RailingColumn);
            RailingAuthoredOffsets authored = MeasureAuthoredRailingOffsets(inventory, floor, railing);
            Debug.Log(
                $"Dungeon Lab Tiered Platforms: measured flat railing contract from {AuthoredFlatRailingModuleName}. " +
                $"Base side {DirectionName(authored.baseSide)}, rail offset {Format(authored.railing.position)} yaw {authored.railing.rotation.eulerAngles.y:0.###}; " +
                $"column endpoint offsets start {Format(authored.startColumnOffset)} end {Format(authored.endColumnOffset)}.");
            return new RailingContracts(railing, column, authored);
        }

        private static string LoadPrimaryStairPrefabPath()
        {
            return StairConnectorSettings.Load().PrimaryStairPath;
        }

        private static ConnectionPointPlacement CalculateConnectionPointPlacement(
            ConnectionPointSetPieceContract setPiece,
            Vector2Int lowerCell,
            Vector2Int higherCell,
            int lowerLevel,
            int higherLevel,
            Vector3 origin,
            float levelHeight,
            Vector3 worldExitTarget,
            Vector2 worldLowerDirection)
        {
            Vector2 entryDirection = DirectionVector(setPiece.entry.direction);
            if (entryDirection == Vector2.zero)
            {
                throw new InvalidOperationException($"Set-piece '{setPiece.name}' entry direction was not cardinal.");
            }

            float yRotation = CalculateYawToMap(entryDirection, worldLowerDirection);
            Quaternion rotation = Quaternion.Euler(0f, yRotation, 0f);
            Vector3 localEntryPoint = LocalConnectionPoint(setPiece, setPiece.entry, levelHeight);
            Vector3 localExitPoint = LocalConnectionPoint(setPiece, setPiece.exit, levelHeight);
            Vector3 position = worldExitTarget - rotation * localExitPoint;
            Vector3 worldEntryTarget = position + rotation * localEntryPoint;
            ValidateSetPieceGridAlignment(setPiece, position, rotation, origin);
            return new ConnectionPointPlacement(
                position,
                yRotation,
                localEntryPoint,
                localExitPoint,
                worldEntryTarget,
                worldExitTarget);
        }

        private static Vector3 TransitionExitEdgeCenter(
            Vector3 origin,
            TransitionEdge transition,
            Vector2Int higherCell,
            int lowerDirectionId,
            float y)
        {
            Vector2Int[] cells = transition.upperLandingCells.Length > 0
                ? transition.upperLandingCells
                : new[] { higherCell };
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (Vector2Int cell in cells)
            {
                sum += EdgeCenter(origin, new PlatformEdge(cell.x, cell.y, lowerDirectionId), y);
                count++;
            }

            return count == 0
                ? EdgeCenter(origin, new PlatformEdge(higherCell.x, higherCell.y, lowerDirectionId), y)
                : sum / count;
        }

        private static void ValidateSetPieceGridAlignment(
            ConnectionPointSetPieceContract setPiece,
            Vector3 position,
            Quaternion rotation,
            Vector3 origin)
        {
            foreach (Vector2Int localCell in setPiece.floorBlockedCells)
            {
                Vector3 worldCenter = position + rotation * LocalCellCenter(setPiece, localCell);
                if (!PlanCoordinateIsCellCenter(worldCenter.x - origin.x) ||
                    !PlanCoordinateIsCellCenter(worldCenter.z - origin.z))
                {
                    throw new InvalidOperationException(
                        $"placement grid alignment failed for '{setPiece.name}'. Footprint cell {localCell} center {Format(worldCenter)} is not aligned to the cell grid.");
                }
            }
        }

        private static bool PlanCoordinateIsCellCenter(float value)
        {
            float normalized = (value - CellSize * 0.5f) / CellSize;
            return Mathf.Abs(normalized - Mathf.Round(normalized)) <= 0.02f;
        }

        private static Vector3 LocalConnectionPoint(ConnectionPointSetPieceContract setPiece, ConnectionPoint point, float levelHeight)
        {
            if (point.hasLocalPoint)
            {
                return point.localPoint;
            }

            float minX = setPiece.localBounds.min.x + point.localCell.x * CellSize;
            float maxX = Mathf.Min(minX + CellSize, setPiece.localBounds.max.x);
            float minZ = setPiece.localBounds.min.z + point.localCell.y * CellSize;
            float maxZ = Mathf.Min(minZ + CellSize, setPiece.localBounds.max.z);
            minX = Mathf.Clamp(minX, setPiece.localBounds.min.x, setPiece.localBounds.max.x);
            maxX = Mathf.Clamp(maxX, setPiece.localBounds.min.x, setPiece.localBounds.max.x);
            minZ = Mathf.Clamp(minZ, setPiece.localBounds.min.z, setPiece.localBounds.max.z);
            maxZ = Mathf.Clamp(maxZ, setPiece.localBounds.min.z, setPiece.localBounds.max.z);

            float x = (minX + maxX) * 0.5f;
            float z = (minZ + maxZ) * 0.5f;
            switch (point.direction)
            {
                case Direction.North:
                    z = maxZ;
                    break;
                case Direction.East:
                    x = maxX;
                    break;
                case Direction.South:
                    z = minZ;
                    break;
                case Direction.West:
                    x = minX;
                    break;
            }

            return new Vector3(x, setPiece.localBounds.min.y + point.level * levelHeight, z);
        }

        private static GameObject InstantiateConnectionPointSetPiecePrefab(
            ConnectionPointSetPieceContract setPiece,
            string name,
            Transform parent,
            Vector3 position,
            float yRotation)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(setPiece.prefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException($"Missing prefab '{setPiece.prefabPath}'.");
            }

            var root = new GameObject(name);
            root.transform.SetParent(parent, worldPositionStays: false);
            root.transform.position = position;
            root.transform.rotation = Quaternion.Euler(0f, yRotation, 0f);

            var visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
            visual.name = $"{name}_visual";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
            ApplyReviewedVisualAlignment(setPiece, root.transform, visual);
            return root;
        }

        // Change-making over the measured full-cell base denominations (the same
        // family and filter the forge uses for under-flight fill), top-anchored
        // at the tower's entry level with any odd remainder sunk below ground —
        // the drop-face walls' convention.
        private static void PlaceStairwellBaseFill(
            TransitionEdge transition,
            int lowerLevel,
            Vector3 origin,
            float levelHeight,
            Transform parent,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            List<StairwellBasePiece> denominations = LoadStairwellBaseDenominations();
            if (denominations.Count == 0)
            {
                return;
            }

            foreach (Vector2Int cell in transition.footprintCells)
            {
                Vector3 cellCenter = origin + new Vector3(cell.x * CellSize + CellSize * 0.5f, 0f, cell.y * CellSize + CellSize * 0.5f);
                float top = lowerLevel * levelHeight;
                float remaining = top;
                int course = 0;
                while (remaining > 0.01f && course <= 12)
                {
                    StairwellBasePiece piece = null;
                    for (int i = denominations.Count - 1; i >= 0; i--)
                    {
                        if (denominations[i].Height <= remaining + 1.01f)
                        {
                            piece = denominations[i];
                            break;
                        }
                    }

                    piece = piece ?? denominations[0];
                    var position = new Vector3(
                        cellCenter.x - (piece.boundsMin.x + piece.boundsMax.x) * 0.5f,
                        top - piece.boundsMax.y,
                        cellCenter.z - (piece.boundsMin.z + piece.boundsMax.z) * 0.5f);
                    GameObject instance = InstantiatePrefab(
                        piece.prefabPath,
                        $"stairwell_base_{cell.x}_{cell.y}_{course}",
                        parent,
                        position,
                        0f);
                    EncapsulateInstance(instance, ref bounds, ref hasBounds);
                    top -= piece.Height;
                    remaining -= piece.Height;
                    course++;
                }
            }
        }

        private sealed class StairwellBasePiece
        {
            public readonly string prefabPath;
            public readonly Vector3 boundsMin;
            public readonly Vector3 boundsMax;
            public float Height => boundsMax.y - boundsMin.y;

            public StairwellBasePiece(string prefabPath, Vector3 boundsMin, Vector3 boundsMax)
            {
                this.prefabPath = prefabPath;
                this.boundsMin = boundsMin;
                this.boundsMax = boundsMax;
            }
        }

        private static List<StairwellBasePiece> stairwellBaseCache;
        private static DateTime stairwellBaseCacheLibraryWriteTimeUtc = DateTime.MinValue;
        private static bool warnedMissingStairwellBase;

        private static List<StairwellBasePiece> LoadStairwellBaseDenominations()
        {
            if (!File.Exists(StepPieceLibraryPath))
            {
                return new List<StairwellBasePiece>();
            }

            DateTime libraryWriteTimeUtc = File.GetLastWriteTimeUtc(StepPieceLibraryPath);
            if (stairwellBaseCache != null && libraryWriteTimeUtc == stairwellBaseCacheLibraryWriteTimeUtc)
            {
                return stairwellBaseCache;
            }

            stairwellBaseCacheLibraryWriteTimeUtc = libraryWriteTimeUtc;
            stairwellBaseCache = new List<StairwellBasePiece>();
            JObject root = JObject.Parse(File.ReadAllText(StepPieceLibraryPath));
            if (root["pieces"] is JArray pieces)
            {
                foreach (JToken piece in pieces)
                {
                    string name = piece.Value<string>("name") ?? string.Empty;
                    if (!string.Equals(piece.Value<string>("category"), "bottomCap", StringComparison.Ordinal) ||
                        !string.Equals(piece.Value<string>("confidence"), "high", StringComparison.Ordinal) ||
                        !name.Contains("straight") ||
                        name.Contains("hole"))
                    {
                        continue;
                    }

                    Vector3 boundsMin = ParseVector3(piece["boundsMin"]);
                    Vector3 boundsMax = ParseVector3(piece["boundsMax"]);
                    Vector3 size = boundsMax - boundsMin;
                    if (Mathf.Abs(size.x - CellSize) > 0.3f || Mathf.Abs(size.z - CellSize) > 0.3f)
                    {
                        continue;
                    }

                    stairwellBaseCache.Add(new StairwellBasePiece(piece.Value<string>("path"), boundsMin, boundsMax));
                }
            }

            stairwellBaseCache.Sort((left, right) =>
            {
                int byHeight = left.Height.CompareTo(right.Height);
                return byHeight != 0 ? byHeight : string.CompareOrdinal(left.prefabPath, right.prefabPath);
            });

            if (stairwellBaseCache.Count == 0 && !warnedMissingStairwellBase)
            {
                warnedMissingStairwellBase = true;
                Debug.LogWarning(
                    "Dungeon Lab Elevation Edge Model: no measured full-cell base denominations for stairwell under-fill " +
                    "(towers will float). Re-run Tools > Dungeon Lab > Measure Step Piece Library.");
            }

            return stairwellBaseCache;
        }

        // Step 9 (decision 38): support columns under bridge-style spans. The
        // gold-standard scene builds its canyon bridge on COMP_Column stacks
        // (small=2u, med=4u, large=6u) that rise from the chasm floor in
        // measured modules and top out flush with the deck underside, beneath
        // the deck floor pieces' corner lines. Candidates are the four corners
        // of every FLAT deck floor slab in the synthesized plan (flights and
        // flipped underside caps contribute none), deduped across adjacent
        // slabs; a corner is legal only when every cell touching it is true
        // void and unreserved (decision 12: never block walkable — corridors
        // passing under a span keep their pass-through, and skipped corners
        // read as variation). Stacks are top-anchored change-making over the
        // measured modules; an odd remainder sinks the bottom course below
        // ground, the drop-face wall convention.
        // Decision J: the gold bridge_look_here pier. The deck (free, from
        // cellLevels) rides on PILLARS placed every 3 cells (2 clear cells
        // between). Each pillar fills one cell: a vertical stack of
        // P_MOD_Base_01_straight_large (4x4x6 bottomCap blocks) for the body, with
        // a COMP_Column_01_med (1.4x4) stack capping each of the cell's 4 corners.
        // No railings (open deck). Pillars start at the cantilevered TIP and step
        // back toward the room every 3 cells.
        private const int PromontoryPillarSpacingCells = 3;
        private const string PromontoryCoverName = "P_MOD_WallCover_01_M_straight";

        // Decision K: modular two-sided shell walls (above the floor). Heights
        // large=6u, med=4u, small=2u. The shell scales UP with elevation
        // (user 2026-06-16) through this allowed ladder: small(2) -> med(4) ->
        // large(6) -> med+med(8) -> large+med(10) -> large+large(12). large+large
        // is RESERVED for the HIGHEST tier; all other tiers cap at large+med(10).
        private const string ShellWallFamilyPrefix = "COMP_Wall_01_M_";
        private const string ShellWallLargeName = ShellWallFamilyPrefix + "straight_large";
        private const string ShellWallMedName = ShellWallFamilyPrefix + "straight_med";
        private const string ShellWallSmallName = ShellWallFamilyPrefix + "straight_small";
        private const float MinShellUnits = 2f;
        private const float MaxShellUnits = 12f;

        // Outer faces (toward the void) bias TALLER, inner tier-step faces (toward
        // a lower interior floor) bias SHORTER, by this many units (user 2026-06-16).
        private const float ShellEdgeBias = 2f;

        // Height scales with elevation on a STEEP curve so the bulk of the tiers
        // stay short and tall walls are rare, reserved for the highest tiers
        // (user 2026-06-16: linear "scaled up too fast"). t^exponent.
        private const float ShellHeightExponent = 3f;

        // Room-size thresholds (cells), mirroring DungeonLabGenerator's room mix so
        // the height cap classifies rooms the same way the planner builds them
        // (user 2026-06-16): large rooms (>= large) carry the tall 10u/12u shells;
        // medium rooms cap at med+med (8u); small rooms cap at med (4u).
        private const int LargeRoomMinAreaCells = 25;
        private const int MidRoomMinAreaCells = 12;

        // The bottom-up course heights (u) of a shell at the given floor level: a
        // tier-scaled height, biased up for outer / down for inner faces, capped by
        // the room-size limit (maxUnits) and snapped to the allowed ladder. The
        // large+large 12u top is reserved for the OUTER face of the highest tier;
        // everything else caps at large+med (10u).
        private static int[] ShellCourseHeights(int level, int minLevel, int maxLevel, bool outer, int maxUnits)
        {
            float t = maxLevel > minLevel ? (level - minLevel) / (float)(maxLevel - minLevel) : 1f;
            t = Mathf.Pow(t, ShellHeightExponent);
            float targetUnits = Mathf.Lerp(MinShellUnits, MaxShellUnits, t) + (outer ? ShellEdgeBias : -ShellEdgeBias);
            if (!(outer && level >= maxLevel))
            {
                targetUnits = Mathf.Min(targetUnits, 10f);
            }
            targetUnits = Mathf.Min(targetUnits, maxUnits);
            targetUnits = Mathf.Max(targetUnits, MinShellUnits);

            switch (Mathf.Clamp(Mathf.RoundToInt(targetUnits / 2f) * 2, 2, 12))
            {
                case 2: return new[] { 2 };
                case 4: return new[] { 4 };
                case 6: return new[] { 6 };
                case 8: return new[] { 4, 4 };
                case 10: return new[] { 6, 4 };
                case 12: return new[] { 6, 6 };
                default: return new[] { 4 };
            }
        }

        private static RawOuterShellPlan BuildRawOuterShellPlan(
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<WallEdge> wallEdges,
            IReadOnlyList<TransitionEdge> transitions,
            RoomBoundaryContext roomBoundaryContext,
            HashSet<Vector2Int> promontoryCells)
        {
            HashSet<Vector2Int> exterior = FloodExteriorVoid(levels);
            int minLevel = MinFloorLevel(levels);
            int maxLevel = minLevel;
            foreach (int level in levels.Values)
            {
                if (level > maxLevel)
                {
                    maxLevel = level;
                }
            }

            var roomArea = new Dictionary<int, int>();
            if (roomBoundaryContext?.cellRoomIds != null)
            {
                foreach (KeyValuePair<Vector2Int, int> entry in roomBoundaryContext.cellRoomIds)
                {
                    roomArea.TryGetValue(entry.Value, out int count);
                    roomArea[entry.Value] = count + 1;
                }
            }

            HashSet<Vector2Int> stairLandingCells =
                CollectStructuralStairLandingCells(transitions);
            HashSet<(Vector2Int cell, int direction, int higherLevel)> orphanLandingEdges =
                FindOrphanLandingShellEdges(
                    CollectOuterShellContinuityEdges(
                        wallEdges,
                        roundTierCorners: null,
                        promontoryCells),
                    stairLandingCells);
            var straightCourseHeights =
                new Dictionary<(int x, int z, int direction), int[]>();
            var largeWallRooms = new HashSet<int>();
            var plan = new RawOuterShellPlan(
                exterior,
                minLevel,
                maxLevel,
                roomArea,
                orphanLandingEdges,
                straightCourseHeights,
                largeWallRooms,
                roomBoundaryContext);

            foreach (WallEdge wall in wallEdges)
            {
                if (wall.isPartition || wall.suppressRailing)
                {
                    continue;
                }

                var cell = new Vector2Int(wall.edge.x, wall.edge.z);
                if (promontoryCells.Contains(cell) ||
                    orphanLandingEdges.Contains(
                        (cell, wall.edge.direction, wall.higherLevel)))
                {
                    continue;
                }

                bool outer = exterior.Contains(
                    Neighbor(cell, wall.edge.direction));
                int[] courseHeights = ShellCourseHeights(
                    wall.higherLevel,
                    minLevel,
                    maxLevel,
                    outer,
                    ShellRoomSizeCap(plan, cell));
                straightCourseHeights[
                    (wall.edge.x, wall.edge.z, wall.edge.direction)] =
                    courseHeights;

                if (!outer ||
                    roomBoundaryContext?.cellRoomIds == null ||
                    !roomBoundaryContext.cellRoomIds.TryGetValue(
                        cell,
                        out int roomId))
                {
                    continue;
                }

                foreach (int height in courseHeights)
                {
                    if (height >= 6)
                    {
                        largeWallRooms.Add(roomId);
                        break;
                    }
                }
            }

            return plan;
        }

        private static int ShellRoomSizeCap(
            RawOuterShellPlan plan,
            Vector2Int cell)
        {
            if (plan.roomBoundaryContext?.cellRoomIds == null)
            {
                return 12;
            }

            if (!plan.roomBoundaryContext.cellRoomIds.TryGetValue(
                    cell,
                    out int roomId) ||
                !plan.roomArea.TryGetValue(roomId, out int area))
            {
                return 4;
            }

            if (area >= LargeRoomMinAreaCells)
            {
                return 12;
            }

            return area >= MidRoomMinAreaCells ? 8 : 4;
        }

        private static GatewayWallPlan BuildGatewayWallPlan(
            IReadOnlyList<WallEdge> wallEdges,
            RawOuterShellPlan rawOuterShellPlan)
        {
            var supports =
                new Dictionary<string, GatewayWallSupport>(
                    StringComparer.Ordinal);
            var supportHeights =
                new Dictionary<string, (int baseLevel, int heightUnits)>(
                    StringComparer.Ordinal);
            foreach (WallEdge wall in wallEdges)
            {
                int baseLevel;
                int heightUnits;
                if (wall.isPartition)
                {
                    baseLevel = wall.lowerLevel;
                    heightUnits = wall.partitionHeightUnits;
                }
                else if (rawOuterShellPlan.straightCourseHeights.TryGetValue(
                             (
                                 wall.edge.x,
                                 wall.edge.z,
                                 wall.edge.direction),
                             out int[] courseHeights))
                {
                    baseLevel = wall.higherLevel;
                    heightUnits = 0;
                    foreach (int height in courseHeights)
                    {
                        heightUnits += height;
                    }
                }
                else
                {
                    continue;
                }

                Vector2Int cell = new Vector2Int(
                    wall.edge.x,
                    wall.edge.z);
                string key = new EdgeKey(
                    cell,
                    Neighbor(cell, wall.edge.direction)).ToString();
                var support = new GatewayWallSupport(
                    (wall.edge.x, wall.edge.z, wall.edge.direction));
                supports[key] = support;
                supportHeights[key] = (baseLevel, heightUnits);
            }

            return new GatewayWallPlan(supports, supportHeights);
        }

        private static HashSet<Vector2Int> BuildGatewayBlockedCells(
            ISet<Vector2Int> reservedCells,
            IReadOnlyList<TransitionEdge> transitions)
        {
            var blocked = reservedCells != null
                ? new HashSet<Vector2Int>(reservedCells)
                : new HashSet<Vector2Int>();
            if (transitions == null)
            {
                return blocked;
            }

            foreach (TransitionEdge transition in transitions)
            {
                blocked.Add(transition.firstCell);
                blocked.Add(transition.secondCell);
                if (transition.lowerLandingCells != null)
                {
                    blocked.UnionWith(transition.lowerLandingCells);
                }
                if (transition.upperLandingCells != null)
                {
                    blocked.UnionWith(transition.upperLandingCells);
                }
                if (transition.footprintCells != null)
                {
                    blocked.UnionWith(transition.footprintCells);
                }
            }

            return blocked;
        }

        private static HashSet<string> BuildGatewayBlockedPathEdgeKeys(
            RoomBoundaryContext roomBoundaryContext,
            ISet<EdgeKey> transitionKeys,
            ISet<OpenEdgeKey> transitionOpenEdges,
            ISet<OpenEdgeKey> bridgeSpanEdges)
        {
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            if (roomBoundaryContext?.gatewayConnectionEnds == null)
            {
                return blocked;
            }

            foreach (GatewayConnectionEnd connectionEnd in
                     roomBoundaryContext.gatewayConnectionEnds)
            {
                IReadOnlyList<Vector2Int> path = connectionEnd.outwardPath;
                for (int index = 0; index + 1 < path.Count; index++)
                {
                    Vector2Int first = path[index];
                    Vector2Int second = path[index + 1];
                    if (!AreCardinalNeighbors(first, second))
                    {
                        continue;
                    }

                    var edgeKey = new EdgeKey(first, second);
                    int direction = EdgeFromCellToward(first, second).direction;
                    bool isBlocked =
                        (transitionKeys != null &&
                         transitionKeys.Contains(edgeKey)) ||
                        (transitionOpenEdges != null &&
                         (transitionOpenEdges.Contains(
                              new OpenEdgeKey(first, direction)) ||
                          transitionOpenEdges.Contains(
                              new OpenEdgeKey(
                                  second,
                                  OppositeDirection(direction))))) ||
                        (bridgeSpanEdges != null &&
                         (bridgeSpanEdges.Contains(
                              new OpenEdgeKey(first, direction)) ||
                          bridgeSpanEdges.Contains(
                              new OpenEdgeKey(
                                  second,
                                  OppositeDirection(direction)))));
                    if (isBlocked)
                    {
                        blocked.Add(edgeKey.ToString());
                    }
                }
            }

            return blocked;
        }

        private static HashSet<(Vector2Int cell, int direction, int higherLevel)> FindOrphanLandingShellEdges(
            IReadOnlyList<(Vector2Int cell, int direction, int higherLevel)> shellEdges,
            ISet<Vector2Int> stairLandingCells)
        {
            var orphanEdges = new HashSet<(Vector2Int cell, int direction, int higherLevel)>();
            if (shellEdges == null ||
                shellEdges.Count == 0 ||
                stairLandingCells == null ||
                stairLandingCells.Count == 0)
            {
                return orphanEdges;
            }

            var uniqueEdges =
                new HashSet<(Vector2Int cell, int direction, int higherLevel)>(
                    shellEdges);
            var edgesByEndpoint =
                new Dictionary<
                    (Vector2Int vertex, int higherLevel),
                    List<(Vector2Int cell, int direction, int higherLevel)>>();
            foreach ((Vector2Int cell, int direction, int higherLevel) shellEdge in uniqueEdges)
            {
                GetEdgeVertices(
                    new PlatformEdge(shellEdge.cell.x, shellEdge.cell.y, shellEdge.direction),
                    out Vector2Int first,
                    out Vector2Int second);
                AddShellEdgeAtEndpoint(
                    edgesByEndpoint,
                    first,
                    shellEdge);
                AddShellEdgeAtEndpoint(
                    edgesByEndpoint,
                    second,
                    shellEdge);
            }

            var visited =
                new HashSet<(Vector2Int cell, int direction, int higherLevel)>();
            foreach ((Vector2Int cell, int direction, int higherLevel) shellEdge in uniqueEdges)
            {
                if (!visited.Add(shellEdge))
                {
                    continue;
                }

                var component =
                    new List<(Vector2Int cell, int direction, int higherLevel)>();
                var pending =
                    new Queue<(Vector2Int cell, int direction, int higherLevel)>();
                pending.Enqueue(shellEdge);
                bool touchesLanding = false;
                bool touchesNonLanding = false;
                while (pending.Count > 0)
                {
                    (Vector2Int cell, int direction, int higherLevel) current =
                        pending.Dequeue();
                    component.Add(current);
                    if (stairLandingCells.Contains(current.cell))
                    {
                        touchesLanding = true;
                    }
                    else
                    {
                        touchesNonLanding = true;
                    }

                    GetEdgeVertices(
                        new PlatformEdge(
                            current.cell.x,
                            current.cell.y,
                            current.direction),
                        out Vector2Int first,
                        out Vector2Int second);
                    EnqueueConnectedShellEdges(
                        edgesByEndpoint,
                        first,
                        current.higherLevel,
                        visited,
                        pending);
                    EnqueueConnectedShellEdges(
                        edgesByEndpoint,
                        second,
                        current.higherLevel,
                        visited,
                        pending);
                }

                if (touchesLanding && !touchesNonLanding)
                {
                    orphanEdges.UnionWith(component);
                }
            }

            return orphanEdges;
        }

        private static void AddShellEdgeAtEndpoint(
            Dictionary<
                (Vector2Int vertex, int higherLevel),
                List<(Vector2Int cell, int direction, int higherLevel)>> edgesByEndpoint,
            Vector2Int vertex,
            (Vector2Int cell, int direction, int higherLevel) shellEdge)
        {
            var key = (vertex, shellEdge.higherLevel);
            if (!edgesByEndpoint.TryGetValue(
                    key,
                    out List<(Vector2Int cell, int direction, int higherLevel)> edges))
            {
                edges =
                    new List<(Vector2Int cell, int direction, int higherLevel)>();
                edgesByEndpoint.Add(key, edges);
            }
            edges.Add(shellEdge);
        }

        private static void EnqueueConnectedShellEdges(
            IReadOnlyDictionary<
                (Vector2Int vertex, int higherLevel),
                List<(Vector2Int cell, int direction, int higherLevel)>> edgesByEndpoint,
            Vector2Int vertex,
            int higherLevel,
            ISet<(Vector2Int cell, int direction, int higherLevel)> visited,
            Queue<(Vector2Int cell, int direction, int higherLevel)> pending)
        {
            if (!edgesByEndpoint.TryGetValue(
                    (vertex, higherLevel),
                    out List<(Vector2Int cell, int direction, int higherLevel)> edges))
            {
                return;
            }

            foreach ((Vector2Int cell, int direction, int higherLevel) edge in edges)
            {
                if (visited.Add(edge))
                {
                    pending.Enqueue(edge);
                }
            }
        }

        private static HashSet<Vector2Int> CollectStructuralStairLandingCells(
            IReadOnlyList<TransitionEdge> transitions)
        {
            var landingCells = new HashSet<Vector2Int>();
            if (transitions == null)
            {
                return landingCells;
            }

            foreach (TransitionEdge transition in transitions)
            {
                if (!transition.hasLandings ||
                    string.Equals(transition.placementClass, SeamStairPlacementClass, StringComparison.Ordinal) ||
                    string.Equals(transition.placementClass, DaisStairPlacementClass, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Vector2Int cell in transition.lowerLandingCells)
                {
                    landingCells.Add(cell);
                }

                foreach (Vector2Int cell in transition.upperLandingCells)
                {
                    landingCells.Add(cell);
                }
            }

            return landingCells;
        }

        private static List<(Vector2Int cell, int direction, int higherLevel)> CollectOuterShellContinuityEdges(
            IReadOnlyList<WallEdge> wallEdges,
            IReadOnlyList<RoundTierCorner> roundTierCorners,
            HashSet<Vector2Int> promontoryCells)
        {
            var shellEdges = new List<(Vector2Int cell, int direction, int higherLevel)>();
            if (wallEdges != null)
            {
                foreach (WallEdge wall in wallEdges)
                {
                    var cell = new Vector2Int(wall.edge.x, wall.edge.z);
                    if (wall.isPartition ||
                        wall.suppressRailing ||
                        promontoryCells.Contains(cell))
                    {
                        continue;
                    }

                    shellEdges.Add((cell, wall.edge.direction, wall.higherLevel));
                }
            }

            if (roundTierCorners == null)
            {
                return shellEdges;
            }

            foreach (RoundTierCorner corner in roundTierCorners)
            {
                if (corner.wallOnly || promontoryCells.Contains(corner.cell))
                {
                    continue;
                }

                shellEdges.Add((
                    new Vector2Int(corner.edgeA.x, corner.edgeA.z),
                    corner.edgeA.direction,
                    corner.higherLevel));
                shellEdges.Add((
                    new Vector2Int(corner.edgeB.x, corner.edgeB.z),
                    corner.edgeB.direction,
                    corner.higherLevel));
            }

            return shellEdges;
        }

        private static bool SuppressesGeneratedTopGuard(
            bool wallSuppressesRailing,
            (int x, int z, int direction) edge,
            ISet<(int x, int z, int direction)> shellGuardEdges,
            ISet<(int x, int z, int direction)> bareLandingEdges)
        {
            return wallSuppressesRailing ||
                shellGuardEdges != null && shellGuardEdges.Contains(edge) ||
                bareLandingEdges != null && bareLandingEdges.Contains(edge);
        }

        // Tall shell walls on the dungeon's OUTER edges only — those facing void
        // that is reachable from outside the footprint (the true perimeter), not
        // interior chasms/overlooks (railings/D) or piers (open). Height scales
        // with the floor's tier height; each side is a uniform height for v1
        // (stepped med->small transitions + windows are later increments).
        private static OuterShellPlacementResult PlaceOuterShellWalls(
            HashSet<Vector2Int> promontoryCells,
            IReadOnlyList<RoundTierCorner> roundTierCorners,
            List<WallEdge> wallEdges,
            RoomBoundaryContext roomBoundaryContext,
            RawOuterShellPlan rawOuterShellPlan,
            GatewaySocketPlan gatewaySocketPlan,
            Vector3 origin,
            float levelHeight,
            Transform parent,
            ref Bounds bounds,
            ref bool hasBounds,
            ref TieredPlatformBuildStats stats)
        {
            var guardEdges = new HashSet<(int x, int z, int direction)>();
            var bareLandingEdges = new HashSet<(int x, int z, int direction)>();
            var largeWallRooms = new HashSet<int>();
            var gatewayFlankWallHeights = new Dictionary<EdgeKey, int>();
            HashSet<(Vector2Int cell, int direction, int higherLevel)> orphanLandingShellEdges =
                rawOuterShellPlan.orphanLandingEdges;
            foreach ((Vector2Int cell, int direction, int higherLevel) orphanEdge in orphanLandingShellEdges)
            {
                bareLandingEdges.Add((
                    orphanEdge.cell.x,
                    orphanEdge.cell.y,
                    orphanEdge.direction));
            }

            MeasuredPrefab large = default, med = default, small = default;
            bool haveLarge = false, haveMed = false, haveSmall = false;
            try { large = MeasurePrefab(PackageInventory.Load().GetPrefabPath(ShellWallLargeName), PrefabRole.StraightWall); haveLarge = true; } catch { }
            try { med = MeasurePrefab(PackageInventory.Load().GetPrefabPath(ShellWallMedName), PrefabRole.StraightWall); haveMed = true; } catch { }
            try { small = MeasurePrefab(PackageInventory.Load().GetPrefabPath(ShellWallSmallName), PrefabRole.StraightWall); haveSmall = true; } catch { }
            if (!haveMed)
            {
                Debug.LogWarning("Dungeon Lab Elevation Edge Model: shell wall pieces unavailable; outer shells skipped.");
                return new OuterShellPlacementResult(
                    guardEdges,
                    bareLandingEdges,
                    gatewayFlankWallHeights);
            }

            // Pick the modular piece whose nominal height matches a course (6/4/2u),
            // falling back to med if the large/small variant is missing.
            MeasuredPrefab CourseStraight(int h) =>
                h >= 6 && haveLarge ? large : h <= 2 && haveSmall ? small : med;

            void RecordLargePerimeterRoom(
                Vector2Int cell,
                bool outer,
                bool largeCourseAvailable,
                IReadOnlyList<int> courseHeights)
            {
                if (!outer ||
                    !largeCourseAvailable ||
                    roomBoundaryContext?.cellRoomIds == null ||
                    !roomBoundaryContext.cellRoomIds.TryGetValue(cell, out int roomId))
                {
                    return;
                }

                foreach (int height in courseHeights)
                {
                    if (height >= 6)
                    {
                        largeWallRooms.Add(roomId);
                        return;
                    }
                }
            }

            void RecordGatewayFlankHeight(
                PlatformEdge edge,
                IReadOnlyList<int> courseHeights)
            {
                int totalHeight = 0;
                foreach (int height in courseHeights)
                {
                    totalHeight += height;
                }

                Vector2Int cell = new Vector2Int(edge.x, edge.z);
                gatewayFlankWallHeights[
                    new EdgeKey(cell, Neighbor(cell, edge.direction))] = totalHeight;
            }

            // Build shells on top of existing drop faces (the C cliff walls AND the
            // interior retaining tier-steps). Stair mouths and authored stair-owned
            // tops are already excluded or suppressed. Lateral landing walls remain
            // eligible when they continue a same-level shell run or corner, but an
            // shell component made entirely from landing-cell edges is pruned here;
            // its structural drop face remains in wallEdges and stays intentionally
            // bare rather than receiving replacement railing or trim.
            // A face is OUTER (taller) when it fronts the exterior void, else INNER
            // (shorter). Round-tier corners are removed from wallEdges and shelled
            // as curved pieces below.
            int shells = 0;
            foreach (WallEdge wall in wallEdges)
            {
                var cell = new Vector2Int(wall.edge.x, wall.edge.z);
                if (!rawOuterShellPlan.straightCourseHeights.TryGetValue(
                        (wall.edge.x, wall.edge.z, wall.edge.direction),
                        out int[] courseHeights))
                {
                    continue;
                }

                bool outer = rawOuterShellPlan.exterior.Contains(
                    Neighbor(cell, wall.edge.direction));
                int level = wall.higherLevel;
                PlatformEdge edge = wall.edge;
                float y = level * levelHeight;
                int course = 0;
                RecordLargePerimeterRoom(cell, outer, haveLarge, courseHeights);
                RecordGatewayFlankHeight(edge, courseHeights);
                foreach (int h in courseHeights)
                {
                    MeasuredPrefab piece = CourseStraight(h);
                    Vector3 cellMin = CellMin(origin, edge.x, edge.z, y);
                    Vector3 cellMax = cellMin + new Vector3(CellSize, 0f, CellSize);
                    GetEdgePlacement(edge, cellMin, cellMax, out Vector3 edgeA, out Vector3 edgeB, out Vector2 outwardNormal);
                    PlaceEdgePrefab(piece, parent, $"shell_{cell.x}_{cell.y}_{edge.direction}_{course}", edgeA, edgeB, outwardNormal, ref bounds, ref hasBounds);
                    y += h;
                    course++;
                    shells++;
                }

                guardEdges.Add((edge.x, edge.z, edge.direction));
            }

            // Rounded/angled corners must stay in the SAME PivotMiddle family as
            // the straight shells. PivotMiddle authors one centered, double-sided
            // curve (M_concave) whose two faces serve both convex and concave
            // applications; there is no separate M_convex piece. The two full-cell
            // M angle variants share a footprint but reverse their authored end
            // profiles. Use the corner polarity already established by
            // FindRoundTierCorners to anchor that handedness: convex corners take
            // angle_1 and concave corners take angle_2. Consecutive chamfer cells
            // alternate polarity, so this mates both their shared seam and their
            // straight-wall endpoints without a coordinate-dependent phase. Every
            // vertical course in one cell stays on the same variant. Both angle
            // variants use the angle_1 transform contract: rotate 180 degrees and
            // recompute the full-cell pivot for the corrected yaw.
            //
            // The single rounded M_concave family is authored in the concave
            // structural orientation. Concave uses therefore keep their calibrated
            // yaw, while convex uses rotate 180 degrees to preserve the old
            // E_convex/E_concave polarity distinction. Keeping the old pivot after
            // either correction would move the authored footprint into the
            // diagonally opposite cell. Bounds-center metrology is for hard
            // L-corners and flips curves.
            int cornerSkips = 0;
            foreach (RoundTierCorner corner in roundTierCorners)
            {
                if (corner.wallOnly || promontoryCells.Contains(corner.cell))
                {
                    continue;
                }

                bool outer =
                    rawOuterShellPlan.exterior.Contains(Neighbor(new Vector2Int(corner.edgeA.x, corner.edgeA.z), corner.edgeA.direction)) ||
                    rawOuterShellPlan.exterior.Contains(Neighbor(new Vector2Int(corner.edgeB.x, corner.edgeB.z), corner.edgeB.direction));

                // Size the corner by a real FLOOR cell of the room, not corner.cell:
                // a CONCAVE corner's cell is the void notch it sweeps into (never a
                // room cell), so RoomSizeCap(corner.cell) would always hit the
                // conservative fallback and render a single short course while the
                // flanking straights stack tall. The edge's owning cell is always a
                // floor cell of the same room the straight walls use.
                var cornerRoomCell = new Vector2Int(corner.edgeA.x, corner.edgeA.z);

                bool useAngleVariantTwo = corner.angleStyle && corner.concave;
                string shape = corner.angleStyle
                    ? useAngleVariantTwo ? "angle_2" : "angle_1"
                    : "concave";
                string CornerPieceName(string size) =>
                    $"{ShellWallFamilyPrefix}{shape}_{size}" +
                    (useAngleVariantTwo && size == "med" ? " " : string.Empty);
                if (!TryLoadTierStepPiece(CornerPieceName("med"), out TierStepPiece curvedMed))
                {
                    cornerSkips++;
                    continue;
                }

                // The vendor-authored angle_2 medium asset has a literal trailing
                // space in its prefab name; the measured library preserves it.
                bool hasCurvedLarge = TryLoadTierStepPiece(CornerPieceName("large"), out TierStepPiece curvedLarge);
                bool hasCurvedSmall = TryLoadTierStepPiece(CornerPieceName("small"), out TierStepPiece curvedSmall);
                TierStepPiece CourseCurved(int h) =>
                    h >= 6 && hasCurvedLarge ? curvedLarge : h <= 2 && hasCurvedSmall ? curvedSmall : curvedMed;

                float shellYaw = CalculateOuterShellCornerYaw(
                    corner.yaw,
                    corner.angleStyle,
                    corner.concave);
                Vector3 pivot = DaisFullCellPivotWorld(corner.cell, shellYaw, origin);
                float y = corner.higherLevel * levelHeight;
                int course = 0;
                int[] courseHeights = ShellCourseHeights(
                    corner.higherLevel,
                    rawOuterShellPlan.minLevel,
                    rawOuterShellPlan.maxLevel,
                    outer,
                    ShellRoomSizeCap(
                        rawOuterShellPlan,
                        cornerRoomCell));
                RecordLargePerimeterRoom(
                    cornerRoomCell,
                    outer,
                    hasCurvedLarge,
                    courseHeights);
                foreach (int h in courseHeights)
                {
                    TierStepPiece piece = CourseCurved(h);
                    GameObject shell = InstantiatePrefab(piece.prefabPath, $"shell_corner_{corner.cell.x}_{corner.cell.y}_{course}", parent, pivot + Vector3.up * y, shellYaw);
                    EncapsulateInstance(shell, ref bounds, ref hasBounds);
                    y += h;
                    course++;
                    shells++;
                }

                guardEdges.Add(corner.edgeA);
                guardEdges.Add(corner.edgeB);
            }

            if (shells > 0)
            {
                stats.stairSummaries.Add($"outer shell wall pieces: {shells}" + (cornerSkips > 0 ? $" ({cornerSkips} corners skipped)" : string.Empty));
            }

            ValidateGatewayShellFlankEmission(
                gatewaySocketPlan,
                rawOuterShellPlan,
                gatewayFlankWallHeights);
            stats.largePerimeterRooms = largeWallRooms.Count;
            return new OuterShellPlacementResult(
                guardEdges,
                bareLandingEdges,
                gatewayFlankWallHeights);
        }

        private static void ValidateGatewayShellFlankEmission(
            GatewaySocketPlan gatewaySocketPlan,
            RawOuterShellPlan rawOuterShellPlan,
            IReadOnlyDictionary<EdgeKey, int> emittedShellHeights)
        {
            if (gatewaySocketPlan == null)
            {
                return;
            }

            foreach (GatewaySocket socket in gatewaySocketPlan.sockets)
            {
                ValidateFlank(socket.firstFlankEdge);
                ValidateFlank(socket.secondFlankEdge);
            }

            void ValidateFlank((int x, int z, int direction) flank)
            {
                if (!rawOuterShellPlan.straightCourseHeights.ContainsKey(
                        flank))
                {
                    return;
                }

                var cell = new Vector2Int(flank.x, flank.z);
                var key = new EdgeKey(
                    cell,
                    Neighbor(cell, flank.direction));
                if (!emittedShellHeights.ContainsKey(key))
                {
                    throw new InvalidOperationException(
                        $"Reserved gateway flank {key} was not emitted as its planned straight shell wall.");
                }
            }
        }

        private static void ApplyPartitionHeightPlan(
            List<WallEdge> wallEdges,
            RoomBoundaryContext roomBoundaryContext,
            IReadOnlyCollection<int> largeWallRooms,
            ref TieredPlatformBuildStats stats)
        {
            // The raw straight-shell plan is the only authority for a large
            // interior wall. Do not infer this from tier/elevation: a room
            // qualifies only when its exterior perimeter plans a 6u wall course.
            var largeRooms = largeWallRooms != null
                ? new HashSet<int>(largeWallRooms)
                : new HashSet<int>();
            for (int index = 0; index < wallEdges.Count; index++)
            {
                WallEdge wall = wallEdges[index];
                if (!wall.isPartition)
                {
                    continue;
                }

                Vector2Int firstCell = new Vector2Int(wall.edge.x, wall.edge.z);
                Vector2Int secondCell = Neighbor(firstCell, wall.edge.direction);
                int firstRoom = GetRoomId(roomBoundaryContext, firstCell);
                int secondRoom = GetRoomId(roomBoundaryContext, secondCell);
                bool firstKnown = firstRoom >= 0;
                bool secondKnown = secondRoom >= 0;
                bool large = firstKnown && secondKnown
                    ? largeRooms.Contains(firstRoom) && largeRooms.Contains(secondRoom)
                    : firstKnown
                        ? largeRooms.Contains(firstRoom)
                        : secondKnown && largeRooms.Contains(secondRoom);
                int heightUnits = large ? 6 : 4;
                wallEdges[index] = wall.WithPartitionHeight(heightUnits);
                if (large)
                {
                    stats.largePartitionWalls++;
                }
            }
        }

        private static GatewaySocketPlan BuildGatewaySocketPlan(
            IReadOnlyDictionary<Vector2Int, int> levels,
            RoomBoundaryContext roomBoundaryContext,
            GatewayWallPlan gatewayWallPlan,
            ISet<Vector2Int> blockedCells,
            ISet<string> blockedPathEdges,
            float requiredSocketWidth,
            List<string> unresolvedEnds)
        {
            var validByEnd =
                new Dictionary<string, GatewaySocket>(StringComparer.Ordinal);
            if (roomBoundaryContext?.gatewayConnectionEnds == null ||
                roomBoundaryContext.gatewayConnectionEnds.Count == 0)
            {
                return new GatewaySocketPlan(Array.Empty<GatewaySocket>());
            }

            var orderedEnds = new List<GatewayConnectionEnd>(
                roomBoundaryContext.gatewayConnectionEnds);
            orderedEnds.Sort((left, right) =>
            {
                int byConnection =
                    left.roomThreshold.connectionIndex.CompareTo(
                        right.roomThreshold.connectionIndex);
                return byConnection != 0
                    ? byConnection
                    : left.endIndex.CompareTo(right.endIndex);
            });
            foreach (GatewayConnectionEnd connectionEnd in orderedEnds)
            {
                if (!TryResolveGatewaySocket(
                        connectionEnd.outwardPath,
                        levels,
                        gatewayWallPlan.supportHeights,
                        blockedCells,
                        blockedPathEdges,
                        requiredSocketWidth,
                        out GatewaySocketCandidate candidate,
                        out string rejection))
                {
                    unresolvedEnds?.Add(
                        $"{GatewaySelectionGroup(connectionEnd)} @{new EdgeKey(connectionEnd.roomThreshold.firstCell, connectionEnd.roomThreshold.secondCell)}: {rejection}");
                    continue;
                }

                string groupKey = GatewaySelectionGroup(connectionEnd);
                int selectionScore = StairForge.StableHash(
                        $"{roomBoundaryContext.gatewaySelectionSalt}:gateway-end:{groupKey}:{candidate.edgeKey}") &
                    int.MaxValue;
                GatewayWallSupport firstSupport =
                    gatewayWallPlan.supports[candidate.firstFlankKey];
                GatewayWallSupport secondSupport =
                    gatewayWallPlan.supports[candidate.secondFlankKey];
                var socket = new GatewaySocket(
                    connectionEnd.endIndex,
                    candidate.edge,
                    candidate.floorLevel,
                    candidate.wallHeightUnits,
                    candidate.edgeKey,
                    candidate.firstFlankKey,
                    firstSupport.edge,
                    candidate.secondFlankKey,
                    secondSupport.edge,
                    groupKey,
                    selectionScore);
                if (!validByEnd.TryGetValue(
                        groupKey,
                        out GatewaySocket existing) ||
                    IsPreferredGatewaySocket(socket, existing))
                {
                    validByEnd[groupKey] = socket;
                }
            }

            var groupKeys = new List<string>(validByEnd.Keys);
            groupKeys.Sort(StringComparer.Ordinal);
            var selected = new List<GatewaySocket>();
            GatewaySocket guaranteedSocket = default;
            bool hasGuaranteedSocket = false;
            var claimedSocketEdges =
                new HashSet<string>(StringComparer.Ordinal);
            var claimedFlankEdges =
                new HashSet<string>(StringComparer.Ordinal);
            foreach (string groupKey in groupKeys)
            {
                GatewaySocket socket = validByEnd[groupKey];
                if (claimedSocketEdges.Contains(socket.edgeKey) ||
                    claimedFlankEdges.Contains(socket.edgeKey) ||
                    claimedSocketEdges.Contains(socket.firstFlankKey) ||
                    claimedSocketEdges.Contains(socket.secondFlankKey) ||
                    claimedFlankEdges.Contains(socket.firstFlankKey) ||
                    claimedFlankEdges.Contains(socket.secondFlankKey))
                {
                    continue;
                }

                if (!hasGuaranteedSocket ||
                    IsPreferredGatewaySocket(socket, guaranteedSocket))
                {
                    guaranteedSocket = socket;
                    hasGuaranteedSocket = true;
                }

                int coverageRoll = StairForge.StableHash(
                        $"{roomBoundaryContext.gatewaySelectionSalt}:gateway-coverage:{groupKey}") &
                    int.MaxValue;
                if (coverageRoll % 100 >= GatewayPlacementPercent)
                {
                    continue;
                }

                selected.Add(socket);
                claimedSocketEdges.Add(socket.edgeKey);
                claimedFlankEdges.Add(socket.firstFlankKey);
                claimedFlankEdges.Add(socket.secondFlankKey);
            }

            if (selected.Count == 0 && hasGuaranteedSocket)
            {
                selected.Add(guaranteedSocket);
            }

            return new GatewaySocketPlan(selected);
        }

        private static string GatewaySelectionGroup(
            GatewayConnectionEnd connectionEnd)
        {
            DoorwayEdge threshold = connectionEnd.roomThreshold;
            string connectionKey = threshold.connectionIndex >= 0
                ? $"connection:{threshold.connectionIndex}"
                : $"threshold:{new EdgeKey(threshold.firstCell, threshold.secondCell)}";
            return $"{connectionKey}:end:{connectionEnd.endIndex}";
        }

        private static bool IsPreferredGatewaySocket(
            GatewaySocket candidate,
            GatewaySocket existing)
        {
            if (candidate.selectionScore != existing.selectionScore)
            {
                return candidate.selectionScore < existing.selectionScore;
            }

            int byEdge = string.CompareOrdinal(
                candidate.edgeKey,
                existing.edgeKey);
            return byEdge != 0
                ? byEdge < 0
                : candidate.endIndex < existing.endIndex;
        }

        private static bool TryResolveGatewaySocket(
            IReadOnlyList<Vector2Int> outwardPath,
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyDictionary<
                string,
                (int baseLevel, int heightUnits)> wallSupports,
            ISet<Vector2Int> blockedCells,
            ISet<string> blockedPathEdges,
            float requiredSocketWidth,
            out GatewaySocketCandidate candidate,
            out string rejection)
        {
            candidate = default;
            rejection = null;
            if (outwardPath == null ||
                outwardPath.Count < 3 ||
                levels == null ||
                wallSupports == null ||
                Mathf.Abs(requiredSocketWidth - CellSize) > 0.08f)
            {
                rejection = $"outward path too short ({outwardPath?.Count ?? 0} cells)";
                return false;
            }

            // Why each candidate edge along the outward path was refused. A
            // gateway that never appears is otherwise indistinguishable from a
            // gateway the seed simply did not roll.
            var reasons = new List<string>();
            for (int index = 0; index + 1 < outwardPath.Count; index++)
            {
                Vector2Int firstCell = outwardPath[index];
                Vector2Int secondCell = outwardPath[index + 1];
                string step = $"[{index}]{firstCell}->{secondCell}";
                if (!AreCardinalNeighbors(firstCell, secondCell) ||
                    !levels.TryGetValue(firstCell, out int firstLevel) ||
                    !levels.TryGetValue(secondCell, out int secondLevel) ||
                    firstLevel != secondLevel ||
                    blockedCells != null &&
                    (blockedCells.Contains(firstCell) ||
                     blockedCells.Contains(secondCell)))
                {
                    reasons.Add($"{step} not a clear same-level step");
                    continue;
                }

                string edgeKey = new EdgeKey(
                    firstCell,
                    secondCell).ToString();
                if (blockedPathEdges != null &&
                    blockedPathEdges.Contains(edgeKey))
                {
                    reasons.Add($"{step} edge owned by a stair/bridge");
                    continue;
                }

                Vector2Int outward = secondCell - firstCell;
                if ((index > 0 &&
                     firstCell - outwardPath[index - 1] != outward) ||
                    (index + 2 < outwardPath.Count &&
                     outwardPath[index + 2] - secondCell != outward))
                {
                    reasons.Add($"{step} not part of a straight run");
                    continue;
                }

                Vector2Int tangent = outward.x != 0
                    ? Vector2Int.up
                    : Vector2Int.right;
                Vector2Int firstTransverseStart = firstCell + tangent;
                Vector2Int firstTransverseEnd = secondCell + tangent;
                Vector2Int secondTransverseStart = firstCell - tangent;
                Vector2Int secondTransverseEnd = secondCell - tangent;
                string firstTransverseFlank = new EdgeKey(
                    firstTransverseStart,
                    firstTransverseEnd).ToString();
                string secondTransverseFlank = new EdgeKey(
                    secondTransverseStart,
                    secondTransverseEnd).ToString();
                if (IsGatewayFlankPairClear(
                        firstTransverseStart,
                        firstTransverseEnd,
                        secondTransverseStart,
                        secondTransverseEnd,
                        blockedCells) &&
                    TryResolveGatewayFlankPair(
                        firstTransverseFlank,
                        secondTransverseFlank,
                        firstLevel,
                        wallSupports,
                        out int transverseHeight))
                {
                    candidate = new GatewaySocketCandidate(
                        EdgeFromCellToward(firstCell, secondCell),
                        firstLevel,
                        transverseHeight,
                        edgeKey,
                        firstTransverseFlank,
                        secondTransverseFlank,
                        index);
                    return true;
                }

                Vector2Int firstCorridorEnd = secondCell + tangent;
                Vector2Int secondCorridorEnd = secondCell - tangent;
                string firstCorridorFlank = new EdgeKey(
                    secondCell,
                    firstCorridorEnd).ToString();
                string secondCorridorFlank = new EdgeKey(
                    secondCell,
                    secondCorridorEnd).ToString();
                if (IsGatewayFlankPairClear(
                        secondCell,
                        firstCorridorEnd,
                        secondCell,
                        secondCorridorEnd,
                        blockedCells) &&
                    TryResolveGatewayFlankPair(
                        firstCorridorFlank,
                        secondCorridorFlank,
                        firstLevel,
                        wallSupports,
                        out int corridorHeight))
                {
                    candidate = new GatewaySocketCandidate(
                        EdgeFromCellToward(firstCell, secondCell),
                        firstLevel,
                        corridorHeight,
                        edgeKey,
                        firstCorridorFlank,
                        secondCorridorFlank,
                        index);
                    return true;
                }

                reasons.Add(
                    $"{step} no usable flank pair " +
                    $"(transverse {DescribeGatewayFlank(firstTransverseFlank, firstLevel, wallSupports)}" +
                    $"/{DescribeGatewayFlank(secondTransverseFlank, firstLevel, wallSupports)}; " +
                    $"corridor {DescribeGatewayFlank(firstCorridorFlank, firstLevel, wallSupports)}" +
                    $"/{DescribeGatewayFlank(secondCorridorFlank, firstLevel, wallSupports)})");
            }

            rejection = string.Join(", ", reasons);
            return false;
        }

        private static string DescribeGatewayFlank(
            string flankKey,
            int floorLevel,
            IReadOnlyDictionary<
                string,
                (int baseLevel, int heightUnits)> wallSupports)
        {
            if (!wallSupports.TryGetValue(
                    flankKey,
                    out (int baseLevel, int heightUnits) support))
            {
                return "none";
            }

            return support.baseLevel != floorLevel
                ? $"base{support.baseLevel}!={floorLevel}"
                : $"{support.heightUnits}u";
        }

        private static bool TryResolveGatewayFlankPair(
            string firstFlankKey,
            string secondFlankKey,
            int floorLevel,
            IReadOnlyDictionary<
                string,
                (int baseLevel, int heightUnits)> wallSupports,
            out int wallHeight)
        {
            wallHeight = 0;
            if (!wallSupports.TryGetValue(
                    firstFlankKey,
                    out (int baseLevel, int heightUnits) first) ||
                !wallSupports.TryGetValue(
                    secondFlankKey,
                    out (int baseLevel, int heightUnits) second) ||
                first.baseLevel != floorLevel ||
                second.baseLevel != floorLevel)
            {
                return false;
            }

            return TryResolveGatewayWallHeight(
                true,
                first.heightUnits,
                true,
                second.heightUnits,
                out wallHeight);
        }

        private static bool IsGatewayFlankPairClear(
            Vector2Int firstFlankStart,
            Vector2Int firstFlankEnd,
            Vector2Int secondFlankStart,
            Vector2Int secondFlankEnd,
            ISet<Vector2Int> blockedCells)
        {
            return blockedCells == null ||
                (!blockedCells.Contains(firstFlankStart) &&
                 !blockedCells.Contains(firstFlankEnd) &&
                 !blockedCells.Contains(secondFlankStart) &&
                 !blockedCells.Contains(secondFlankEnd));
        }

        private static List<GatewayPlacement> BuildGatewayPlacements(
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<WallEdge> wallEdges,
            IReadOnlyDictionary<EdgeKey, int> shellGatewayFlankWallHeights,
            GatewaySocketPlan gatewaySocketPlan,
            RoomBoundaryContext roomBoundaryContext,
            GatewayContracts contracts,
            PartitionWallContracts partitionContracts)
        {
            // Door material/style is seed-varied, never tier-selected. A gateway
            // is eligible only when both full-edge emitted flanking walls exist at
            // the same height; open, partial, and one-sided ports remain open.
            var placements = new List<GatewayPlacement>();
            if (gatewaySocketPlan == null ||
                gatewaySocketPlan.sockets.Count == 0)
            {
                return placements;
            }

            var flankHeights =
                new Dictionary<string, int>(StringComparer.Ordinal);
            if (shellGatewayFlankWallHeights != null)
            {
                foreach (KeyValuePair<EdgeKey, int> entry in
                         shellGatewayFlankWallHeights)
                {
                    flankHeights[entry.Key.ToString()] = entry.Value;
                }
            }
            foreach (WallEdge wall in wallEdges)
            {
                if (!wall.isPartition)
                {
                    continue;
                }

                Vector2Int cell = new Vector2Int(wall.edge.x, wall.edge.z);
                flankHeights[new EdgeKey(
                    cell,
                    Neighbor(cell, wall.edge.direction)).ToString()] =
                    wall.partitionHeightUnits;
            }

            foreach (GatewaySocket socket in gatewaySocketPlan.sockets)
            {
                bool hasFirstFlank = flankHeights.TryGetValue(
                    socket.firstFlankKey,
                    out int firstHeight);
                bool hasSecondFlank = flankHeights.TryGetValue(
                    socket.secondFlankKey,
                    out int secondHeight);
                if (!TryResolveGatewayWallHeight(
                        hasFirstFlank,
                        firstHeight,
                        hasSecondFlank,
                        secondHeight,
                        out int wallHeight) ||
                    wallHeight != socket.wallHeightUnits)
                {
                    continue;
                }

                var firstSocketCell =
                    new Vector2Int(socket.edge.x, socket.edge.z);
                Vector2Int secondSocketCell = Neighbor(
                    firstSocketCell,
                    socket.edge.direction);
                if (!levels.TryGetValue(
                        firstSocketCell,
                        out int firstFloorLevel) ||
                    !levels.TryGetValue(
                        secondSocketCell,
                        out int secondFloorLevel) ||
                    firstFloorLevel != socket.floorLevel ||
                    secondFloorLevel != socket.floorLevel)
                {
                    continue;
                }

                int gatewayHeightUnits =
                    wallHeight == 4 ||
                    wallHeight == 8
                        ? 4
                        : 6;
                GatewayStyle[] compatibleStyles = gatewayHeightUnits >= 6
                    ? new[]
                    {
                        GatewayStyle.Metal,
                        GatewayStyle.Wood,
                        GatewayStyle.LargeWood,
                        GatewayStyle.Barred,
                        GatewayStyle.OpenArch
                    }
                    : new[]
                    {
                        GatewayStyle.Metal,
                        GatewayStyle.Wood,
                        GatewayStyle.Barred,
                        GatewayStyle.OpenArch
                    };
                int styleRoll = StairForge.StableHash(
                        $"{roomBoundaryContext.gatewaySelectionSalt}:gateway-style:{socket.selectionGroup}:{socket.edgeKey}") &
                    int.MaxValue;
                GatewayStyle style = compatibleStyles[styleRoll % compatibleStyles.Length];
                int headerHeightUnits =
                    wallHeight - gatewayHeightUnits;
                placements.Add(new GatewayPlacement(
                    socket.edge,
                    socket.floorLevel,
                    wallHeight,
                    contracts.For(style, gatewayHeightUnits),
                    headerHeightUnits > 0
                        ? partitionContracts.ForHeight(headerHeightUnits)
                        : default,
                    headerHeightUnits));
            }

            return placements;
        }

        // Both flanks must exist — a gateway never spans open floor — but they
        // need not match. When the shell course planner gives the two sides of
        // one wall line different heights, the SHORTER side sets the opening
        // (owner ruling 2026-07-26): the door and its header then fit inside the
        // lower flank, and the taller side simply continues above them.
        private static bool TryResolveGatewayWallHeight(
            bool hasFirstFlank,
            int firstHeight,
            bool hasSecondFlank,
            int secondHeight,
            out int wallHeight)
        {
            wallHeight = 0;
            if (!hasFirstFlank ||
                !hasSecondFlank)
            {
                return false;
            }

            wallHeight = Mathf.Min(firstHeight, secondHeight);
            if (wallHeight != 4 &&
                wallHeight != 6 &&
                wallHeight != 8 &&
                wallHeight != 10 &&
                wallHeight != 12)
            {
                return false;
            }

            return true;
        }

        private static void PlaceGateway(
            GatewayPlacement gateway,
            Transform parent,
            Vector3 origin,
            float levelHeight,
            ref Bounds bounds,
            ref bool hasBounds,
            ref TieredPlatformBuildStats stats)
        {
            Vector3 cellMin = CellMin(
                origin,
                gateway.edge.x,
                gateway.edge.z,
                gateway.floorLevel * levelHeight);
            Vector3 cellMax = cellMin + new Vector3(CellSize, 0f, CellSize);
            GetEdgePlacement(
                gateway.edge,
                cellMin,
                cellMax,
                out Vector3 edgeA,
                out Vector3 edgeB,
                out Vector2 outwardNormal);
            string styleName = gateway.contract.style.ToString().ToLowerInvariant();
            GameObject instance = PlaceEdgePrefab(
                gateway.contract.prefab,
                parent,
                $"gateway_{styleName}_{gateway.edge.x}_{gateway.edge.z}_{gateway.edge.direction}",
                edgeA,
                edgeB,
                outwardNormal,
                ref bounds,
                ref hasBounds);
            List<GatewayLeafPose> leafPoses =
                ConfigureGatewayInstance(instance, gateway.contract.style);
            EncapsulateInstance(instance, ref bounds, ref hasBounds);
            ValidatePlacedEdgePrefab(
                instance,
                gateway.contract.prefab,
                edgeA,
                edgeB,
                outwardNormal);

            if (gateway.headerHeightUnits > 0)
            {
                MeasuredPrefab header = gateway.headerPrefab;
                GameObject headerInstance = PlaceEdgePrefab(
                    header,
                    parent,
                    $"gateway_header_{gateway.edge.x}_{gateway.edge.z}_{gateway.edge.direction}",
                    edgeA + Vector3.up * gateway.contract.wallHeightUnits,
                    edgeB + Vector3.up * gateway.contract.wallHeightUnits,
                    outwardNormal,
                    ref bounds,
                    ref hasBounds);
                ValidatePlacedEdgePrefab(
                    headerInstance,
                    header,
                    edgeA + Vector3.up * gateway.contract.wallHeightUnits,
                    edgeB + Vector3.up * gateway.contract.wallHeightUnits,
                    outwardNormal);
            }

            if (gateway.contract.style == GatewayStyle.Barred)
            {
                Vector2 localMidpoint =
                    (gateway.contract.prefab.localSegmentStart +
                     gateway.contract.prefab.localSegmentEnd) * 0.5f;
                Vector3 worldMidpoint = instance.transform.TransformPoint(
                    new Vector3(localMidpoint.x, 0f, localMidpoint.y));
                GameObject bars = InstantiatePrefab(
                    gateway.contract.auxiliaryPrefabPath,
                    $"gateway_bars_{gateway.edge.x}_{gateway.edge.z}_{gateway.edge.direction}",
                    instance.transform,
                    worldMidpoint,
                    instance.transform.eulerAngles.y);
                leafPoses.Add(ConfigureBarredGatewayInstance(bars));
                EncapsulateInstance(bars, ref bounds, ref hasBounds);
                stats.barredGateways++;
            }

            if (gateway.contract.style != GatewayStyle.OpenArch)
            {
                ConfigureInteractiveGateway(
                    instance,
                    gateway,
                    edgeA,
                    edgeB,
                    outwardNormal,
                    leafPoses);
            }

            stats.gateways++;
            if (gateway.contract.wallHeightUnits >= 6)
            {
                stats.largeGateways++;
            }
        }

        private static List<GatewayLeafPose> ConfigureGatewayInstance(
            GameObject instance,
            GatewayStyle style)
        {
            var leaves = new List<GatewayLeafPose>(2);
            if (style == GatewayStyle.OpenArch || style == GatewayStyle.Barred)
            {
                Transform doorLeaf = FindRequiredGatewayDescendant(
                    instance,
                    "MOD_Gateway_Door_01_med_01_door");
                doorLeaf.gameObject.SetActive(false);
                return leaves;
            }

            if (style == GatewayStyle.LargeWood)
            {
                Transform left = FindRequiredGatewayDescendant(
                    instance,
                    "MOD_Gateway_Door_01_large_door_L");
                Transform right = FindRequiredGatewayDescendant(
                    instance,
                    "MOD_Gateway_Door_01_large_door_R");
                leaves.Add(PrepareInteractiveGatewayLeaf(left, 100f));
                leaves.Add(PrepareInteractiveGatewayLeaf(right, -100f));
                foreach (Transform child in instance.GetComponentsInChildren<Transform>(true))
                {
                    if (child.name.StartsWith(
                            "MOD_Gateway_Door_01_large_plank_",
                            StringComparison.Ordinal))
                    {
                        child.gameObject.SetActive(false);
                    }
                }

                return leaves;
            }

            string leafName = style == GatewayStyle.Metal
                ? "MOD_Gateway_Door_01_med_01_door"
                : "MOD_Gateway_Door_01_med_02_door";
            leaves.Add(PrepareInteractiveGatewayLeaf(
                FindRequiredGatewayDescendant(instance, leafName),
                95f));
            return leaves;
        }

        private static GatewayLeafPose ConfigureBarredGatewayInstance(GameObject instance)
        {
            Transform leaf = FindRequiredGatewayDescendant(
                instance,
                "SM_PROP_bars_door_01_dungeon");
            return PrepareInteractiveGatewayLeaf(leaf, -75f);
        }

        private static Transform FindRequiredGatewayDescendant(
            GameObject instance,
            string exactName)
        {
            Transform child = FindFirstDescendant(
                instance.transform,
                name => string.Equals(name, exactName, StringComparison.Ordinal));
            if (child == null)
            {
                throw new InvalidOperationException(
                    $"Gateway '{instance.name}' is missing required authored child '{exactName}'.");
            }

            return child;
        }

        private static GatewayLeafPose PrepareInteractiveGatewayLeaf(
            Transform leaf,
            float localYaw)
        {
            Quaternion closedRotation = leaf.localRotation;
            Quaternion openRotation = Quaternion.Euler(0f, localYaw, 0f);
            leaf.localRotation = openRotation;
            foreach (Collider collider in leaf.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
            }

            foreach (Transform child in leaf.GetComponentsInChildren<Transform>(true))
            {
                GameObjectUtility.SetStaticEditorFlags(child.gameObject, 0);
            }

            return new GatewayLeafPose(leaf, closedRotation, openRotation);
        }

        private static void ConfigureInteractiveGateway(
            GameObject instance,
            GatewayPlacement gateway,
            Vector3 edgeA,
            Vector3 edgeB,
            Vector2 outwardNormal,
            IReadOnlyList<GatewayLeafPose> leafPoses)
        {
            if (leafPoses == null || leafPoses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Interactive gateway '{instance.name}' has no animated leaf.");
            }

            Vector3 edgeMidpoint = (edgeA + edgeB) * 0.5f;
            float blockerHeight = Mathf.Max(
                1f,
                Mathf.Min(3.5f, gateway.contract.wallHeightUnits - 0.25f));
            Vector3 blockerCenter = edgeMidpoint + Vector3.up * (blockerHeight * 0.5f);
            Vector3 blockerForward = new Vector3(outwardNormal.x, 0f, outwardNormal.y);
            Quaternion blockerRotation = Quaternion.LookRotation(
                blockerForward.sqrMagnitude > 0.001f ? blockerForward : Vector3.forward,
                Vector3.up);
            Vector3 blockerSize = new(
                Mathf.Max(0.5f, Vector3.Distance(edgeA, edgeB) - 0.45f),
                blockerHeight,
                0.35f);
            Vector3 localBlockerCenter =
                instance.transform.InverseTransformPoint(blockerCenter);
            float localBlockerYaw = Mathf.DeltaAngle(
                instance.transform.eulerAngles.y,
                blockerRotation.eulerAngles.y);
            DoorAuthoring.LeafPose[] leaves = leafPoses
                .Select(pose => new DoorAuthoring.LeafPose(
                    pose.leaf,
                    pose.closedLocalRotation,
                    pose.openLocalRotation))
                .ToArray();

            DoorAuthoring authoring =
                instance.GetComponent<DoorAuthoring>()
                ?? instance.AddComponent<DoorAuthoring>();
            authoring.Configure(
                $"RANDOM_DUNGEON:GATEWAY:{gateway.edge.x}:{gateway.edge.z}:{gateway.edge.direction}",
                "RANDOM_DUNGEON",
                templateOnly: false,
                productionEnabled: true,
                defaultOpen: true,
                definitionVersion: 1,
                openInteractionProfileId: "WORLD_DOOR_INSTANT",
                closeInteractionProfileId: "WORLD_DOOR_INSTANT",
                interactionAnchorLocal: instance.transform.InverseTransformPoint(
                    edgeMidpoint + Vector3.up * 1.25f),
                maxInteractionDistance: 4.25f,
                closedBlockerCenterLocal: localBlockerCenter,
                closedBlockerSize: blockerSize,
                closedBlockerLocalYaw: localBlockerYaw,
                leaves);

            DoorMotor motor =
                instance.GetComponent<DoorMotor>()
                ?? instance.AddComponent<DoorMotor>();
            motor.Configure(authoring);
            DoorInteractable interactable =
                instance.GetComponent<DoorInteractable>()
                ?? instance.AddComponent<DoorInteractable>();
            interactable.Configure(authoring, motor);

            var hitboxObject = new GameObject("InteractionHitbox");
            hitboxObject.transform.SetParent(instance.transform, worldPositionStays: true);
            hitboxObject.transform.SetPositionAndRotation(blockerCenter, blockerRotation);
            BoxCollider collider = hitboxObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = new Vector3(blockerSize.x, blockerSize.y, 0.6f);
            WorldInteractionHitbox hitbox =
                hitboxObject.AddComponent<WorldInteractionHitbox>();
            hitbox.Configure(interactable);

            EditorUtility.SetDirty(authoring);
            EditorUtility.SetDirty(motor);
            EditorUtility.SetDirty(interactable);
            EditorUtility.SetDirty(hitboxObject);
        }

        private static float CalculateOuterShellCornerYaw(
            float structuralYaw,
            bool angleStyle,
            bool concave)
        {
            bool flipFullCellShell = angleStyle || !concave;
            return flipFullCellShell
                ? Mathf.Repeat(structuralYaw + 180f, 360f)
                : structuralYaw;
        }

        // Void cells reachable from outside the floor footprint (4-adjacency over
        // non-floor cells, bounded to a one-cell ring around the bbox).
        private static HashSet<Vector2Int> FloodExteriorVoid(IReadOnlyDictionary<Vector2Int, int> levels)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (Vector2Int cell in levels.Keys)
            {
                minX = Mathf.Min(minX, cell.x);
                maxX = Mathf.Max(maxX, cell.x);
                minZ = Mathf.Min(minZ, cell.y);
                maxZ = Mathf.Max(maxZ, cell.y);
            }

            int loX = minX - 1, hiX = maxX + 1, loZ = minZ - 1, hiZ = maxZ + 1;
            var exterior = new HashSet<Vector2Int>();
            var queue = new Queue<Vector2Int>();
            var startCell = new Vector2Int(loX, loZ);
            exterior.Add(startCell);
            queue.Enqueue(startCell);
            while (queue.Count > 0)
            {
                Vector2Int cur = queue.Dequeue();
                foreach (Vector2Int dir in new[] { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left })
                {
                    Vector2Int next = cur + dir;
                    if (next.x < loX || next.x > hiX || next.y < loZ || next.y > hiZ ||
                        exterior.Contains(next) || levels.ContainsKey(next))
                    {
                        continue;
                    }

                    exterior.Add(next);
                    queue.Enqueue(next);
                }
            }

            return exterior;
        }

        private static void PlacePromontoryPiers(
            HashSet<Vector2Int> promontoryCells,
            IReadOnlyDictionary<Vector2Int, int> levels,
            HashSet<OpenEdgeKey> plannedOpenEdges,
            int abyssBase,
            Vector3 origin,
            float levelHeight,
            Transform parent,
            ref Bounds bounds,
            ref bool hasBounds,
            ref TieredPlatformBuildStats stats)
        {
            List<StairwellBasePiece> bodyBlocks = LoadStairwellBaseDenominations(); // 4x4 bottomCap bases, tallest first below
            List<StairwellBasePiece> columns = LoadSpanColumnDenominations();
            if (bodyBlocks.Count == 0 || columns.Count == 0)
            {
                return;
            }

            float bottomY = origin.y + abyssBase * levelHeight;
            int pillars = 0;
            var pillarCells = new HashSet<Vector2Int>();
            foreach (List<Vector2Int> pier in GroupPromontoryPiers(promontoryCells))
            {
                List<Vector2Int> ordered = OrderPierTipFirst(pier, levels, promontoryCells);
                for (int i = 0; i < ordered.Count; i += PromontoryPillarSpacingCells)
                {
                    Vector2Int cell = ordered[i];
                    if (!levels.TryGetValue(cell, out int level))
                    {
                        continue;
                    }

                    pillarCells.Add(cell);
                    float topY = origin.y + level * levelHeight;
                    string name = $"pier_pillar_{cell.x}_{cell.y}";

                    // Body: base blocks fill the cell. The bottomCap block spans
                    // local x[-4,0] z[0,4], so a +x/-z pivot at the cell's far-x,
                    // near-z corner lays it over the cell.
                    PlaceVerticalStack(bodyBlocks, origin.x + (cell.x + 1) * CellSize, origin.z + cell.y * CellSize, bottomY, topY, parent, $"{name}_body", ref bounds, ref hasBounds);

                    // Caps: a column stack at each of the cell's four corners.
                    foreach (Vector2Int corner in new[] { new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(0, 1), new Vector2Int(1, 1) })
                    {
                        PlaceVerticalStack(columns, origin.x + (cell.x + corner.x) * CellSize, origin.z + (cell.y + corner.y) * CellSize, bottomY, topY, parent, $"{name}_cap", ref bounds, ref hasBounds);
                    }

                    pillars++;
                }
            }

            if (pillars > 0)
            {
                stats.stairSummaries.Add($"promontory pillars: {pillars}");
            }

            // Deck bulk: every NON-pillar deck cell gets a P_MOD_Base_01_straight_
            // small (4x4x2) filling the 2u underside, so the deck is a solid slab
            // between pillars rather than a bare floor plane. Pillar cells are
            // already solid up to the deck.
            int slabs = 0;
            foreach (Vector2Int cell in promontoryCells)
            {
                if (pillarCells.Contains(cell) || !levels.TryGetValue(cell, out int level))
                {
                    continue;
                }

                PlaceVerticalStack(bodyBlocks, origin.x + (cell.x + 1) * CellSize, origin.z + cell.y * CellSize, origin.y + (level - 2) * levelHeight, origin.y + level * levelHeight, parent, $"pier_slab_{cell.x}_{cell.y}", ref bounds, ref hasBounds);
                slabs++;
            }

            if (slabs > 0)
            {
                stats.stairSummaries.Add($"promontory deck slabs: {slabs}");
            }
            stats.promontoryDeckCells = pillars + slabs;

            // Deck thickness (the gold deck is a thick slab, not a flat plane):
            // a wall-cover fascia runs every void-facing deck edge at the deck
            // level AND one course below, giving the causeway visual depth.
            bool haveCover = false;
            MeasuredPrefab cover = default;
            try
            {
                cover = MeasurePrefab(PackageInventory.Load().GetPrefabPath(PromontoryCoverName), PrefabRole.StraightWall);
                haveCover = true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Dungeon Lab Elevation Edge Model: promontory deck cover '{PromontoryCoverName}' unavailable ({exception.Message}); decks render thin.");
            }

            if (haveCover)
            {
                int covers = 0;
                foreach (Vector2Int cell in promontoryCells)
                {
                    if (!levels.TryGetValue(cell, out int level))
                    {
                        continue;
                    }

                    foreach (int direction in CardinalDirections)
                    {
                        if (levels.ContainsKey(Neighbor(cell, direction)))
                        {
                            continue;
                        }

                        // An external connector's terminal edge is an explicit
                        // continuation port. Keep both fascia courses off that
                        // one edge; scenic promontories declare no such opening
                        // and retain their established deck cover treatment.
                        if (plannedOpenEdges != null &&
                            plannedOpenEdges.Contains(new OpenEdgeKey(cell, direction)))
                        {
                            continue;
                        }

                        foreach (int courseLevel in new[] { level - 2, level })
                        {
                            Vector3 cellMin = CellMin(origin, cell.x, cell.y, courseLevel * levelHeight);
                            Vector3 cellMax = cellMin + new Vector3(CellSize, 0f, CellSize);
                            GetEdgePlacement(new PlatformEdge(cell.x, cell.y, direction), cellMin, cellMax, out Vector3 edgeA, out Vector3 edgeB, out Vector2 outwardNormal);
                            PlaceEdgePrefab(cover, parent, $"pier_cover_{cell.x}_{cell.y}_{direction}_{courseLevel}", edgeA, edgeB, outwardNormal, ref bounds, ref hasBounds);
                            covers++;
                        }
                    }
                }

                if (covers > 0)
                {
                    stats.stairSummaries.Add($"promontory deck covers: {covers}");
                }
            }
        }

        // Connected components (4-adjacency) of the promontory cells = the
        // individual piers.
        private static List<List<Vector2Int>> GroupPromontoryPiers(HashSet<Vector2Int> cells)
        {
            var piers = new List<List<Vector2Int>>();
            var seen = new HashSet<Vector2Int>();
            foreach (Vector2Int start in cells)
            {
                if (!seen.Add(start))
                {
                    continue;
                }

                var component = new List<Vector2Int>();
                var stack = new Stack<Vector2Int>();
                stack.Push(start);
                while (stack.Count > 0)
                {
                    Vector2Int cur = stack.Pop();
                    component.Add(cur);
                    foreach (Vector2Int dir in new[] { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left })
                    {
                        Vector2Int next = cur + dir;
                        if (cells.Contains(next) && seen.Add(next))
                        {
                            stack.Push(next);
                        }
                    }
                }

                piers.Add(component);
            }

            return piers;
        }

        // Orders a straight 1-wide pier from its cantilevered tip (no room
        // neighbour) toward the room.
        private static List<Vector2Int> OrderPierTipFirst(List<Vector2Int> pier, IReadOnlyDictionary<Vector2Int, int> levels, HashSet<Vector2Int> promontoryCells)
        {
            var set = new HashSet<Vector2Int>(pier);
            var dirs = new[] { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
            int PierNeighbours(Vector2Int c)
            {
                int n = 0;
                foreach (Vector2Int d in dirs)
                {
                    if (set.Contains(c + d))
                    {
                        n++;
                    }
                }

                return n;
            }

            // The tip end has no room-floor neighbour; pick it (fall back to any end).
            Vector2Int tip = pier[0];
            foreach (Vector2Int c in pier)
            {
                if (PierNeighbours(c) != 1)
                {
                    continue;
                }

                bool touchesRoom = false;
                foreach (Vector2Int d in dirs)
                {
                    if (levels.ContainsKey(c + d) && !promontoryCells.Contains(c + d))
                    {
                        touchesRoom = true;
                        break;
                    }
                }

                tip = c;
                if (!touchesRoom)
                {
                    break;
                }
            }

            var ordered = new List<Vector2Int>();
            var visited = new HashSet<Vector2Int>();
            Vector2Int? cursor = tip;
            while (cursor.HasValue)
            {
                Vector2Int cur = cursor.Value;
                ordered.Add(cur);
                visited.Add(cur);
                cursor = null;
                foreach (Vector2Int dir in dirs)
                {
                    Vector2Int next = cur + dir;
                    if (set.Contains(next) && !visited.Contains(next))
                    {
                        cursor = next;
                        break;
                    }
                }
            }

            return ordered;
        }

        // Stacks denominations bottom-up from bottomY to topY at world (px,pz),
        // each piece placed with its own pivot so its base sits on the running
        // height; tallest-fitting course first, a small top gap (hidden under the
        // deck) tolerated rather than overshooting the deck.
        private static void PlaceVerticalStack(
            List<StairwellBasePiece> denominations,
            float px,
            float pz,
            float bottomY,
            float topY,
            Transform parent,
            string name,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            float y = bottomY;
            int course = 0;
            while (topY - y > 0.5f && course <= 40)
            {
                float remaining = topY - y;
                StairwellBasePiece piece = null;
                for (int i = denominations.Count - 1; i >= 0; i--)
                {
                    if (denominations[i].Height <= remaining + 0.5f)
                    {
                        piece = denominations[i];
                        break;
                    }
                }

                if (piece == null)
                {
                    break;
                }

                var position = new Vector3(px, y - piece.boundsMin.y, pz);
                GameObject instance = InstantiatePrefab(piece.prefabPath, $"{name}_{course}", parent, position, 0f);
                EncapsulateInstance(instance, ref bounds, ref hasBounds);
                y += piece.Height;
                course++;
            }
        }

        private static void PlaceSpanSupportColumns(
            TransitionEdge transition,
            GameObject stairInstance,
            IReadOnlyDictionary<Vector2Int, int> levels,
            HashSet<Vector2Int> reservedCells,
            Vector3 origin,
            float abyssRelativeY,
            Transform parent,
            ref Bounds bounds,
            ref bool hasBounds,
            TieredPlatformBuildStats stats)
        {
            List<StairwellBasePiece> denominations = LoadSpanColumnDenominations();
            if (denominations.Count == 0 || stairInstance == null)
            {
                return;
            }

            Transform visual = stairInstance.transform.childCount > 0 ? stairInstance.transform.GetChild(0) : null;
            IReadOnlyList<SynthesizedPiecePlacement> pieces = transition.synthesizedSetPiece.pieces;
            if (visual == null || visual.childCount != pieces.Count)
            {
                Debug.LogError(
                    $"Dungeon Lab Elevation Edge Model: span '{transition.synthesizedSetPiece.name}' visual does not match its piece plan " +
                    $"({(visual == null ? 0 : visual.childCount)} children vs {pieces.Count} pieces); skipping support columns.");
                return;
            }

            // Corner key -> (corner position, deck underside height), deduped on a
            // half-unit grid so adjacent slabs share one pier at their seam.
            var corners = new Dictionary<Vector2Int, KeyValuePair<Vector3, float>>();
            for (int i = 0; i < pieces.Count; i++)
            {
                SynthesizedPiecePlacement piece = pieces[i];
                if (Mathf.Abs(piece.localPitchDegrees) > 1f ||
                    piece.sourcePrefab.IndexOf("/P_MOD_Floor_01_O_straight", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                if (!TryGetWorldRendererBounds(visual.GetChild(i).gameObject, out Bounds slab))
                {
                    continue;
                }

                for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
                {
                    var corner = new Vector3(
                        (cornerIndex & 1) == 0 ? slab.min.x : slab.max.x,
                        slab.min.y,
                        (cornerIndex & 2) == 0 ? slab.min.z : slab.max.z);
                    var key = new Vector2Int(Mathf.RoundToInt(corner.x * 2f), Mathf.RoundToInt(corner.z * 2f));
                    if (!corners.ContainsKey(key))
                    {
                        corners.Add(key, new KeyValuePair<Vector3, float>(corner, slab.min.y));
                    }
                }
            }

            int stacks = 0;
            foreach (KeyValuePair<Vector2Int, KeyValuePair<Vector3, float>> entry in corners)
            {
                Vector3 corner = entry.Value.Key;
                float top = entry.Value.Value - origin.y;
                if (top <= 0.5f || !SpanColumnCornerIsLegal(corner, origin, levels, reservedCells))
                {
                    continue;
                }

                // Decision C/J2: span support columns reach the shared abyss base
                // (was the y=0 ground line, which now floats above the underworld).
                float remaining = top - abyssRelativeY;
                int course = 0;
                while (remaining > 0.01f && course <= 30)
                {
                    StairwellBasePiece piece = null;
                    for (int i = denominations.Count - 1; i >= 0; i--)
                    {
                        if (denominations[i].Height <= remaining + 1.01f)
                        {
                            piece = denominations[i];
                            break;
                        }
                    }

                    piece = piece ?? denominations[0];
                    var position = new Vector3(
                        corner.x - (piece.boundsMin.x + piece.boundsMax.x) * 0.5f,
                        origin.y + top - piece.boundsMax.y,
                        corner.z - (piece.boundsMin.z + piece.boundsMax.z) * 0.5f);
                    GameObject instance = InstantiatePrefab(
                        piece.prefabPath,
                        $"span_support_{entry.Key.x}_{entry.Key.y}_{course}",
                        parent,
                        position,
                        0f);
                    EncapsulateInstance(instance, ref bounds, ref hasBounds);
                    top -= piece.Height;
                    remaining -= piece.Height;
                    course++;
                }

                stacks++;
            }

            if (stacks > 0)
            {
                stats.stairSummaries.Add($"span support columns: {stacks} stacks under {transition.synthesizedSetPiece.name}");
                Debug.Log(
                    $"Dungeon Lab Elevation Edge Model: placed {stacks} support column stacks under span " +
                    $"'{transition.synthesizedSetPiece.name}' {transition.firstCell} <-> {transition.secondCell}.");
            }
        }

        // Every cell a pier corner touches must be true void and unreserved —
        // a corner sits on a cell junction, so up to four cells qualify.
        private static bool SpanColumnCornerIsLegal(
            Vector3 corner,
            Vector3 origin,
            IReadOnlyDictionary<Vector2Int, int> levels,
            HashSet<Vector2Int> reservedCells)
        {
            for (int dx = -1; dx <= 1; dx += 2)
            {
                for (int dz = -1; dz <= 1; dz += 2)
                {
                    var cell = new Vector2Int(
                        Mathf.FloorToInt((corner.x - origin.x + dx * 0.05f) / CellSize),
                        Mathf.FloorToInt((corner.z - origin.z + dz * 0.05f) / CellSize));
                    if (levels.ContainsKey(cell) || reservedCells.Contains(cell))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryGetWorldRendererBounds(GameObject instance, out Bounds worldBounds)
        {
            worldBounds = new Bounds();
            bool hasAny = false;
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>())
            {
                if (!hasAny)
                {
                    worldBounds = renderer.bounds;
                    hasAny = true;
                }
                else
                {
                    worldBounds.Encapsulate(renderer.bounds);
                }
            }

            return hasAny;
        }

        private static List<StairwellBasePiece> spanColumnCache;
        private static DateTime spanColumnCacheLibraryWriteTimeUtc = DateTime.MinValue;
        private static bool warnedMissingSpanColumns;

        // Measured column modules for span piers: the COMP composites (decision
        // 12 kept them in metrology scope), one piece per height denomination,
        // brazier-topped variant excluded.
        private static List<StairwellBasePiece> LoadSpanColumnDenominations()
        {
            if (!File.Exists(StepPieceLibraryPath))
            {
                return new List<StairwellBasePiece>();
            }

            DateTime libraryWriteTimeUtc = File.GetLastWriteTimeUtc(StepPieceLibraryPath);
            if (spanColumnCache != null && libraryWriteTimeUtc == spanColumnCacheLibraryWriteTimeUtc)
            {
                return spanColumnCache;
            }

            spanColumnCacheLibraryWriteTimeUtc = libraryWriteTimeUtc;
            var byHeight = new SortedDictionary<int, StairwellBasePiece>();
            JObject root = JObject.Parse(File.ReadAllText(StepPieceLibraryPath));
            if (root["pieces"] is JArray pieces)
            {
                foreach (JToken piece in pieces)
                {
                    string name = piece.Value<string>("name") ?? string.Empty;
                    if (!string.Equals(piece.Value<string>("category"), "column", StringComparison.Ordinal) ||
                        !string.Equals(piece.Value<string>("confidence"), "high", StringComparison.Ordinal) ||
                        !name.StartsWith("COMP_Column_01_", StringComparison.Ordinal) ||
                        name.Contains("brazier"))
                    {
                        continue;
                    }

                    Vector3 boundsMin = ParseVector3(piece["boundsMin"]);
                    Vector3 boundsMax = ParseVector3(piece["boundsMax"]);
                    float height = boundsMax.y - boundsMin.y;
                    int heightKey = Mathf.RoundToInt(height);
                    if (heightKey < 1 || Mathf.Abs(height - heightKey) > 0.1f)
                    {
                        continue;
                    }

                    var candidate = new StairwellBasePiece(piece.Value<string>("path"), boundsMin, boundsMax);
                    if (!byHeight.TryGetValue(heightKey, out StairwellBasePiece existing) ||
                        string.CompareOrdinal(candidate.prefabPath, existing.prefabPath) < 0)
                    {
                        byHeight[heightKey] = candidate;
                    }
                }
            }

            spanColumnCache = new List<StairwellBasePiece>(byHeight.Values);
            if (spanColumnCache.Count == 0 && !warnedMissingSpanColumns)
            {
                warnedMissingSpanColumns = true;
                Debug.LogWarning(
                    "Dungeon Lab Elevation Edge Model: no measured COMP_Column denominations for span support columns " +
                    "(bridge decks will stand unsupported). Re-run Tools > Dungeon Lab > Measure Step Piece Library.");
            }

            return spanColumnCache;
        }

        // Online synthesis (step 7): resolve the set-piece contract for a
        // transition — synthesized transitions carry their contract token in
        // memory (parsed here with the active level height by the same parser
        // core as the reviewed files); everything else resolves from the loaded
        // contract pool by prefab path.
        private static ConnectionPointSetPieceContract ResolveTransitionSetPieceContract(
            TieredPlatformContracts contracts,
            TransitionEdge transition,
            int deltaLevels)
        {
            if (transition.synthesizedSetPiece == null)
            {
                return SelectConnectionPointStairContract(contracts, transition.stairPrefabPath, deltaLevels);
            }

            if (!TryBuildSynthesizedSetPieceContract(
                    transition.synthesizedSetPiece.contractToken,
                    contracts.levelHeight,
                    out ConnectionPointSetPieceContract synthesized,
                    out string rejectedReason))
            {
                throw new InvalidOperationException(
                    $"Synthesized stair contract '{transition.synthesizedSetPiece.name}' failed to parse: {rejectedReason}.");
            }

            return synthesized;
        }

        // Online synthesis (step 7): a synthesized stair has no prefab asset; the
        // visual is built directly from the piece plan that also produced the
        // contract, so the alignment validators below run against geometry that
        // is exact by construction (the forge round-trip's one-plan guarantee).
        private static GameObject InstantiateSynthesizedSetPiece(
            SynthesizedStairSetPiece synthesized,
            ConnectionPointSetPieceContract setPiece,
            string name,
            Transform parent,
            Vector3 position,
            float yRotation)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, worldPositionStays: false);
            root.transform.position = position;
            root.transform.rotation = Quaternion.Euler(0f, yRotation, 0f);

            var visual = new GameObject($"{name}_visual");
            visual.transform.SetParent(root.transform, worldPositionStays: false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;

            foreach (SynthesizedPiecePlacement piece in synthesized.pieces)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(piece.sourcePrefab);
                if (prefab == null)
                {
                    throw new InvalidOperationException(
                        $"Synthesized stair '{synthesized.name}' references a missing piece prefab '{piece.sourcePrefab}'.");
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, visual.transform);
                instance.name = piece.pieceName;
                instance.transform.localPosition = piece.localPosition;
                instance.transform.localRotation = Quaternion.Euler(piece.localPitchDegrees, piece.localYawDegrees, 0f);
            }

            ApplyReviewedVisualAlignment(setPiece, root.transform, visual);
            return root;
        }

        private static void ApplyReviewedVisualAlignment(
            ConnectionPointSetPieceContract setPiece,
            Transform contractRoot,
            GameObject visual)
        {
            if (setPiece.exitSurfaceRootSources.Length == 0)
            {
                return;
            }

            if (!TryCollectReviewedSourceRootBounds(visual, contractRoot, setPiece.exitSurfaceRootSources, out Bounds actualBounds))
            {
                throw new InvalidOperationException($"Stair prefab '{setPiece.name}' visual did not contain reviewed exit surface roots.");
            }

            if (setPiece.reviewedSourceRootPoses.Length > 0)
            {
                if (!TryAlignReviewedSourceRootPoses(setPiece, contractRoot, visual, out string alignmentError))
                {
                    throw new InvalidOperationException($"Stair prefab '{setPiece.name}' {alignmentError}");
                }
            }
            else if (setPiece.reviewedVisualAnchorPositions.Length > 0)
            {
                if (!TryCollectReviewedSourceRootPositions(
                        visual,
                        contractRoot,
                        setPiece.exitSurfaceRootSources,
                        out List<Vector3> actualPositions))
                {
                    throw new InvalidOperationException($"Stair prefab '{setPiece.name}' visual did not contain reviewed visual anchor roots.");
                }

                if (!TryFindReviewedRootSetOffset(actualPositions, setPiece.reviewedVisualAnchorPositions, out Vector3 reviewedOffset))
                {
                    throw new InvalidOperationException(
                        $"Stair prefab '{setPiece.name}' reviewed visual anchor roots do not match the contract frame.");
                }

                visual.transform.localPosition = reviewedOffset;
            }
            else
            {
                Bounds expectedBounds = BuildExpectedExitSurfaceRootBounds(setPiece);
                Vector3 offset = new Vector3(
                    expectedBounds.max.x - actualBounds.max.x,
                    expectedBounds.max.y - actualBounds.max.y,
                    expectedBounds.min.z - actualBounds.min.z);
                visual.transform.localPosition = offset;
                WarnBoundsAlignmentFallback(setPiece, offset);
            }

            ValidateReviewedVisualAnchorAlignment(setPiece, contractRoot, visual);
        }

        private static readonly HashSet<string> WarnedBoundsFallbackContracts = new HashSet<string>(StringComparer.Ordinal);

        private static void WarnBoundsAlignmentFallback(ConnectionPointSetPieceContract setPiece, Vector3 offset)
        {
            if (!WarnedBoundsFallbackContracts.Add(setPiece.name))
            {
                return;
            }

            Debug.LogWarning(
                $"Dungeon Lab: stair contract '{setPiece.name}' has no expectedLocalPositions (stairBodyRoots or exitSurfaceRoots), " +
                $"so the visual was aligned with the bounds-based fallback (offset {offset.x:0.###},{offset.y:0.###},{offset.z:0.###}). " +
                "This heuristic can misplace stair bodies by a whole cell; author expectedLocalPositions to pin the alignment.");
        }

        private static Bounds BuildExpectedExitSurfaceRootBounds(ConnectionPointSetPieceContract setPiece)
        {
            var bounds = new Bounds(setPiece.exit.localPoint, Vector3.zero);
            if (!TryBuildReviewedPortSpanBand(
                    setPiece.exit,
                    setPiece.localBounds,
                    out bool lateralAxisIsZ,
                    out float lateralMin,
                    out float lateralMax,
                    out string error))
            {
                throw new InvalidOperationException($"Stair prefab '{setPiece.name}' exit visual anchor span is invalid: {error}");
            }

            if (lateralAxisIsZ)
            {
                bounds.Encapsulate(new Vector3(setPiece.exit.localPoint.x, setPiece.exit.localPoint.y, lateralMin));
                bounds.Encapsulate(new Vector3(setPiece.exit.localPoint.x, setPiece.exit.localPoint.y, lateralMax));
            }
            else
            {
                bounds.Encapsulate(new Vector3(lateralMin, setPiece.exit.localPoint.y, setPiece.exit.localPoint.z));
                bounds.Encapsulate(new Vector3(lateralMax, setPiece.exit.localPoint.y, setPiece.exit.localPoint.z));
            }

            return bounds;
        }

        private static bool TryValidateReviewedPortSpan(
            string label,
            Bounds localBounds,
            ConnectionPoint port,
            out string rejectedReason)
        {
            if (!TryBuildReviewedPortSpanBand(
                    port,
                    localBounds,
                    out _,
                    out _,
                    out _,
                    out string error))
            {
                rejectedReason = $"{label}:{port.role} port span is invalid: {error}";
                return false;
            }

            rejectedReason = string.Empty;
            return true;
        }

        private static bool TryBuildReviewedPortSpanBand(
            ConnectionPoint port,
            Bounds localBounds,
            out bool lateralAxisIsZ,
            out float lateralMin,
            out float lateralMax,
            out string error)
        {
            lateralAxisIsZ = false;
            lateralMin = 0f;
            lateralMax = 0f;
            error = string.Empty;

            if (port.spanCells == null || port.spanCells.Length == 0)
            {
                error = "port has no span cells";
                return false;
            }

            bool directionIsX = port.direction == Direction.East || port.direction == Direction.West;
            bool directionIsZ = port.direction == Direction.North || port.direction == Direction.South;
            if (!directionIsX && !directionIsZ)
            {
                error = $"port direction {DirectionName(port.direction)} is not horizontal";
                return false;
            }

            lateralAxisIsZ = directionIsX;
            int fixedIndex = directionIsX ? port.spanCells[0].x : port.spanCells[0].y;
            lateralMin = float.PositiveInfinity;
            lateralMax = float.NegativeInfinity;

            foreach (Vector2Int cell in port.spanCells)
            {
                int cellFixedIndex = directionIsX ? cell.x : cell.y;
                if (cellFixedIndex != fixedIndex)
                {
                    error = $"span cells are not on one {DirectionName(port.direction)} edge";
                    return false;
                }

                float cellLateralMin = directionIsX
                    ? localBounds.min.z + cell.y * CellSize
                    : localBounds.min.x + cell.x * CellSize;
                lateralMin = Mathf.Min(lateralMin, cellLateralMin);
                lateralMax = Mathf.Max(lateralMax, cellLateralMin + CellSize);
            }

            float expectedWidth = port.spanCells.Length * CellSize;
            if (Mathf.Abs((lateralMax - lateralMin) - expectedWidth) > 0.02f)
            {
                error = $"span cells are not contiguous; expected width {expectedWidth:0.###}, got {lateralMax - lateralMin:0.###}";
                return false;
            }

            float expectedLateralCenter = (lateralMin + lateralMax) * 0.5f;
            float actualLateralCenter = directionIsX ? port.localPoint.z : port.localPoint.x;
            if (Mathf.Abs(expectedLateralCenter - actualLateralCenter) > 0.02f)
            {
                error = $"local edge center is not centered on lane span; expected {expectedLateralCenter:0.###}, got {actualLateralCenter:0.###}";
                return false;
            }

            float expectedNormalCoordinate;
            float actualNormalCoordinate;
            switch (port.direction)
            {
                case Direction.East:
                    expectedNormalCoordinate = localBounds.min.x + (fixedIndex + 1) * CellSize;
                    actualNormalCoordinate = port.localPoint.x;
                    break;
                case Direction.West:
                    expectedNormalCoordinate = localBounds.min.x + fixedIndex * CellSize;
                    actualNormalCoordinate = port.localPoint.x;
                    break;
                case Direction.North:
                    expectedNormalCoordinate = localBounds.min.z + (fixedIndex + 1) * CellSize;
                    actualNormalCoordinate = port.localPoint.z;
                    break;
                default:
                    expectedNormalCoordinate = localBounds.min.z + fixedIndex * CellSize;
                    actualNormalCoordinate = port.localPoint.z;
                    break;
            }

            if (Mathf.Abs(expectedNormalCoordinate - actualNormalCoordinate) > 0.02f)
            {
                error = $"local edge coordinate is not on the declared side; expected {expectedNormalCoordinate:0.###}, got {actualNormalCoordinate:0.###}";
                return false;
            }

            return true;
        }

        private static void ValidateReviewedPortSpanBandRegression()
        {
            var localBounds = new Bounds();
            localBounds.SetMinMax(new Vector3(-12f, 0f, 0f), new Vector3(0f, 6f, 12f));
            for (int laneCount = 1; laneCount <= 3; laneCount++)
            {
                var spanCells = new Vector2Int[laneCount];
                for (int z = 0; z < laneCount; z++)
                {
                    spanCells[z] = new Vector2Int(2, z);
                }

                var port = new ConnectionPoint(
                    spanCells[spanCells.Length / 2],
                    Direction.East,
                    level: 0,
                    role: "regression",
                    spanCells,
                    hasLocalPoint: true,
                    new Vector3(0f, 0f, laneCount * CellSize * 0.5f));

                if (!TryBuildReviewedPortSpanBand(
                        port,
                        localBounds,
                        out bool lateralAxisIsZ,
                        out float lateralMin,
                        out float lateralMax,
                        out string error))
                {
                    throw new InvalidOperationException(
                        $"Reviewed stair port span regression failed for lane count {laneCount}: {error}");
                }

                float expectedMax = laneCount * CellSize;
                if (!lateralAxisIsZ ||
                    Mathf.Abs(lateralMin) > 0.001f ||
                    Mathf.Abs(lateralMax - expectedMax) > 0.001f)
                {
                    throw new InvalidOperationException(
                        $"Reviewed stair port span regression failed for lane count {laneCount}: expected z band [0,{expectedMax:0.###}], got {(lateralAxisIsZ ? "z" : "x")} band [{lateralMin:0.###},{lateralMax:0.###}].");
                }
            }
        }

        private static void ValidateReviewedVisualAnchorAlignment(
            ConnectionPointSetPieceContract setPiece,
            Transform contractRoot,
            GameObject visual)
        {
            if (setPiece.reviewedSourceRootPoses.Length > 0)
            {
                ValidateReviewedSourceRootPoseAlignment(setPiece, contractRoot, visual);
                return;
            }

            if (setPiece.reviewedVisualAnchorPositions.Length > 0)
            {
                ValidateReviewedVisualRootSetAlignment(setPiece, contractRoot, visual);
                return;
            }

            if (!TryCollectReviewedSourceRootBounds(visual, contractRoot, setPiece.exitSurfaceRootSources, out Bounds actualBounds))
            {
                throw new InvalidOperationException($"Stair prefab '{setPiece.name}' visual did not contain reviewed exit surface roots after alignment.");
            }

            if (!TryBuildReviewedPortSpanBand(
                    setPiece.exit,
                    setPiece.localBounds,
                    out bool lateralAxisIsZ,
                    out float lateralMin,
                    out _,
                    out string error))
            {
                throw new InvalidOperationException($"Stair prefab '{setPiece.name}' exit visual anchor span is invalid after alignment: {error}");
            }

            float actualLateralMin = lateralAxisIsZ ? actualBounds.min.z : actualBounds.min.x;
            if (Mathf.Abs(actualBounds.max.x - setPiece.exit.localPoint.x) > ReviewedVisualAnchorTolerance ||
                Mathf.Abs(actualBounds.max.y - setPiece.exit.localPoint.y) > ReviewedVisualAnchorTolerance ||
                Mathf.Abs(actualLateralMin - lateralMin) > ReviewedVisualAnchorTolerance)
            {
                string lateralAxis = lateralAxisIsZ ? "z" : "x";
                throw new InvalidOperationException(
                    $"Stair prefab '{setPiece.name}' visual anchor alignment failed. " +
                    $"Expected maxX {setPiece.exit.localPoint.x:0.###}, maxY {setPiece.exit.localPoint.y:0.###}, min{lateralAxis.ToUpperInvariant()} {lateralMin:0.###}; " +
                    $"got maxX {actualBounds.max.x:0.###}, maxY {actualBounds.max.y:0.###}, min{lateralAxis.ToUpperInvariant()} {actualLateralMin:0.###}.");
            }
        }

        private static bool TryAlignReviewedSourceRootPoses(
            ConnectionPointSetPieceContract setPiece,
            Transform contractRoot,
            GameObject visual,
            out string error)
        {
            error = string.Empty;
            if (setPiece.reviewedSourceRootPoses.Length == 0)
            {
                error = "sourceRootPoses is empty.";
                return false;
            }

            Vector3 originalPosition = visual.transform.localPosition;
            Quaternion originalRotation = visual.transform.localRotation;

            foreach (ReviewedSourceRootPose alignmentPose in setPiece.reviewedSourceRootPoses)
            {
                var sources = new HashSet<string>(new[] { alignmentPose.sourcePrefab }, StringComparer.Ordinal);
                List<ReviewedSourceRootTransform> roots = CollectReviewedSourceRootTransforms(visual, contractRoot, sources);
                Quaternion expectedRotation = Quaternion.Euler(0f, alignmentPose.localYawDegrees, 0f);

                foreach (ReviewedSourceRootTransform root in roots)
                {
                    visual.transform.localPosition = originalPosition;
                    visual.transform.localRotation = originalRotation;

                    Quaternion rotationDelta = expectedRotation * Quaternion.Inverse(root.localRotation);
                    visual.transform.localRotation = rotationDelta * visual.transform.localRotation;

                    List<ReviewedSourceRootTransform> rotatedRoots = CollectReviewedSourceRootTransforms(visual, contractRoot, sources);
                    ReviewedSourceRootTransform rotatedRoot = FindNearestReviewedSourceRoot(rotatedRoots, root.localPosition);
                    visual.transform.localPosition += alignmentPose.localPosition - rotatedRoot.localPosition;

                    if (TryValidateReviewedSourceRootPoses(setPiece, contractRoot, visual, out _))
                    {
                        return true;
                    }
                }
            }

            visual.transform.localPosition = originalPosition;
            visual.transform.localRotation = originalRotation;
            error = "sourceRootPoses could not normalize visual source roots.";
            return false;
        }

        private static void ValidateReviewedSourceRootPoseAlignment(
            ConnectionPointSetPieceContract setPiece,
            Transform contractRoot,
            GameObject visual)
        {
            if (!TryValidateReviewedSourceRootPoses(setPiece, contractRoot, visual, out string error))
            {
                throw new InvalidOperationException($"Stair prefab '{setPiece.name}' visual source root pose alignment failed. {error}");
            }
        }

        private static bool TryValidateReviewedSourceRootPoses(
            ConnectionPointSetPieceContract setPiece,
            Transform contractRoot,
            GameObject visual,
            out string error)
        {
            error = string.Empty;
            foreach (ReviewedSourceRootPose pose in setPiece.reviewedSourceRootPoses)
            {
                var sources = new HashSet<string>(new[] { pose.sourcePrefab }, StringComparer.Ordinal);
                List<ReviewedSourceRootTransform> roots = CollectReviewedSourceRootTransforms(visual, contractRoot, sources);
                if (!TryFindReviewedSourceRootAt(roots, pose.localPosition, out ReviewedSourceRootTransform sourceRoot))
                {
                    error = $"sourceRootPoses source root not found at {FormatVector(pose.localPosition)} for {pose.sourcePrefab}";
                    return false;
                }

                float actualYaw = LocalYawDegrees(sourceRoot.localRotation);
                float yawDelta = DeltaDegrees(actualYaw, pose.localYawDegrees);
                if (Mathf.Abs(yawDelta) > ReviewedVisualAnchorTolerance)
                {
                    error = $"sourceRootPoses source root yaw mismatch for {pose.sourcePrefab}; expected {pose.localYawDegrees:0.###}, got {actualYaw:0.###}, delta {yawDelta:0.###}";
                    return false;
                }
            }

            return true;
        }

        private static void ValidateReviewedVisualRootSetAlignment(
            ConnectionPointSetPieceContract setPiece,
            Transform contractRoot,
            GameObject visual)
        {
            if (!TryCollectReviewedSourceRootPositions(
                    visual,
                    contractRoot,
                    setPiece.exitSurfaceRootSources,
                    out List<Vector3> actualPositions))
            {
                throw new InvalidOperationException($"Stair prefab '{setPiece.name}' visual did not contain reviewed visual anchor roots after alignment.");
            }

            if (actualPositions.Count != setPiece.reviewedVisualAnchorPositions.Length)
            {
                throw new InvalidOperationException(
                    $"Stair prefab '{setPiece.name}' visual anchor alignment failed. " +
                    $"Expected {setPiece.reviewedVisualAnchorPositions.Length} source roots, got {actualPositions.Count}.");
            }

            var matchedActual = new bool[actualPositions.Count];
            for (int expectedIndex = 0; expectedIndex < setPiece.reviewedVisualAnchorPositions.Length; expectedIndex++)
            {
                Vector3 expected = setPiece.reviewedVisualAnchorPositions[expectedIndex];
                int matchIndex = -1;
                float matchDistance = float.MaxValue;
                for (int actualIndex = 0; actualIndex < actualPositions.Count; actualIndex++)
                {
                    if (matchedActual[actualIndex])
                    {
                        continue;
                    }

                    Vector3 delta = actualPositions[actualIndex] - expected;
                    if (Mathf.Abs(delta.x) > ReviewedVisualAnchorTolerance ||
                        Mathf.Abs(delta.y) > ReviewedVisualAnchorTolerance ||
                        Mathf.Abs(delta.z) > ReviewedVisualAnchorTolerance)
                    {
                        continue;
                    }

                    float distance = delta.sqrMagnitude;
                    if (distance < matchDistance)
                    {
                        matchIndex = actualIndex;
                        matchDistance = distance;
                    }
                }

                if (matchIndex < 0)
                {
                    Vector3 nearest = FindNearestPoint(actualPositions, expected, out Vector3 nearestDelta);
                    throw new InvalidOperationException(
                        $"Stair prefab '{setPiece.name}' visual anchor alignment failed. " +
                        $"Expected source root {expectedIndex} at {FormatVector(expected)}, " +
                        $"nearest actual {FormatVector(nearest)}, delta {FormatVector(nearestDelta)}.");
                }

                matchedActual[matchIndex] = true;
            }
        }

        private static bool TryCollectReviewedSourceRootPositions(
            GameObject root,
            Transform space,
            IReadOnlyList<string> sourcePaths,
            out List<Vector3> positions)
        {
            positions = new List<Vector3>();
            var sources = new HashSet<string>(sourcePaths, StringComparer.Ordinal);
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (!IsReviewedSourceRoot(transform, sources))
                {
                    continue;
                }

                positions.Add(space.InverseTransformPoint(transform.position));
            }

            return positions.Count > 0;
        }

        private static List<ReviewedSourceRootTransform> CollectReviewedSourceRootTransforms(
            GameObject root,
            Transform space,
            HashSet<string> sourcePaths)
        {
            var transforms = new List<ReviewedSourceRootTransform>();
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform == root.transform || !IsReviewedSourceRoot(transform, sourcePaths))
                {
                    continue;
                }

                transforms.Add(new ReviewedSourceRootTransform(
                    space.InverseTransformPoint(transform.position),
                    Quaternion.Inverse(space.rotation) * transform.rotation));
            }

            return transforms;
        }

        private static bool TryFindReviewedSourceRootAt(
            List<ReviewedSourceRootTransform> roots,
            Vector3 expectedPosition,
            out ReviewedSourceRootTransform root)
        {
            foreach (ReviewedSourceRootTransform candidate in roots)
            {
                Vector3 delta = candidate.localPosition - expectedPosition;
                if (Mathf.Abs(delta.x) <= ReviewedVisualAnchorTolerance &&
                    Mathf.Abs(delta.y) <= ReviewedVisualAnchorTolerance &&
                    Mathf.Abs(delta.z) <= ReviewedVisualAnchorTolerance)
                {
                    root = candidate;
                    return true;
                }
            }

            root = default;
            return false;
        }

        private static ReviewedSourceRootTransform FindNearestReviewedSourceRoot(
            List<ReviewedSourceRootTransform> roots,
            Vector3 target)
        {
            ReviewedSourceRootTransform nearest = roots[0];
            Vector3 nearestDelta = nearest.localPosition - target;
            float nearestDistance = nearestDelta.sqrMagnitude;
            for (int i = 1; i < roots.Count; i++)
            {
                Vector3 candidateDelta = roots[i].localPosition - target;
                float candidateDistance = candidateDelta.sqrMagnitude;
                if (candidateDistance >= nearestDistance)
                {
                    continue;
                }

                nearest = roots[i];
                nearestDelta = candidateDelta;
                nearestDistance = candidateDistance;
            }

            return nearest;
        }

        private static float LocalYawDegrees(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            return NormalizeSignedDegrees(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg);
        }

        private static float DeltaDegrees(float actual, float expected)
        {
            return NormalizeSignedDegrees(actual - expected);
        }

        private static float NormalizeSignedDegrees(float value)
        {
            while (value > 180f)
            {
                value -= 360f;
            }

            while (value <= -180f)
            {
                value += 360f;
            }

            return value;
        }

        private static bool TryFindReviewedRootSetOffset(
            List<Vector3> actual,
            IReadOnlyList<Vector3> expected,
            out Vector3 offset)
        {
            offset = Vector3.zero;
            if (actual.Count != expected.Count)
            {
                return false;
            }

            foreach (Vector3 expectedPoint in expected)
            {
                foreach (Vector3 actualPoint in actual)
                {
                    Vector3 candidateOffset = expectedPoint - actualPoint;
                    if (ReviewedRootSetsMatchWithOffset(actual, expected, candidateOffset))
                    {
                        offset = candidateOffset;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ReviewedRootSetsMatchWithOffset(
            List<Vector3> actual,
            IReadOnlyList<Vector3> expected,
            Vector3 offset)
        {
            var matchedActual = new bool[actual.Count];
            foreach (Vector3 expectedPoint in expected)
            {
                int matchIndex = -1;
                float matchDistance = float.MaxValue;
                for (int actualIndex = 0; actualIndex < actual.Count; actualIndex++)
                {
                    if (matchedActual[actualIndex])
                    {
                        continue;
                    }

                    Vector3 delta = actual[actualIndex] + offset - expectedPoint;
                    if (Mathf.Abs(delta.x) > ReviewedVisualAnchorTolerance ||
                        Mathf.Abs(delta.y) > ReviewedVisualAnchorTolerance ||
                        Mathf.Abs(delta.z) > ReviewedVisualAnchorTolerance)
                    {
                        continue;
                    }

                    float distance = delta.sqrMagnitude;
                    if (distance < matchDistance)
                    {
                        matchIndex = actualIndex;
                        matchDistance = distance;
                    }
                }

                if (matchIndex < 0)
                {
                    return false;
                }

                matchedActual[matchIndex] = true;
            }

            return true;
        }

        private static Vector3 FindNearestPoint(IReadOnlyList<Vector3> points, Vector3 target, out Vector3 delta)
        {
            Vector3 nearest = Vector3.zero;
            delta = Vector3.zero;
            float nearestDistance = float.MaxValue;
            foreach (Vector3 point in points)
            {
                Vector3 candidateDelta = point - target;
                float distance = candidateDelta.sqrMagnitude;
                if (distance >= nearestDistance)
                {
                    continue;
                }

                nearest = point;
                delta = candidateDelta;
                nearestDistance = distance;
            }

            return nearest;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:0.###},{value.y:0.###},{value.z:0.###})";
        }

        private static bool TryCollectReviewedSourceRootBounds(
            GameObject root,
            Transform space,
            IReadOnlyList<string> sourcePaths,
            out Bounds bounds)
        {
            bounds = default;
            bool initialized = false;
            var sources = new HashSet<string>(sourcePaths, StringComparer.Ordinal);
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (!IsReviewedSourceRoot(transform, sources))
                {
                    continue;
                }

                Vector3 point = space.InverseTransformPoint(transform.position);
                if (!initialized)
                {
                    bounds = new Bounds(point, Vector3.zero);
                    initialized = true;
                    continue;
                }

                bounds.Encapsulate(point);
            }

            return initialized;
        }

        private static bool IsReviewedSourceRoot(Transform source, HashSet<string> sourcePaths)
        {
            return ReviewedStairSourceResolver.IsSourceRoot(source, sourcePaths);
        }

        private static Vector3 ConnectionPointVisualOffset(
            ConnectionPointSetPieceContract setPiece,
            Transform logicalRoot,
            GameObject visual)
        {
            if (!TryGetFloorOrStairChildLocalBounds(visual, logicalRoot, out Bounds floorOrStairBounds))
            {
                throw new InvalidOperationException(
                    $"Connection-point set-piece '{setPiece.name}' had no Floor/Stairs child renderers or colliders.");
            }

            return new Vector3(
                setPiece.localBounds.min.x - floorOrStairBounds.min.x,
                setPiece.localBounds.min.y - floorOrStairBounds.min.y,
                setPiece.localBounds.min.z - floorOrStairBounds.min.z);
        }

        private static bool TryGetFloorOrStairChildLocalBounds(GameObject root, Transform space, out Bounds bounds)
        {
            bounds = default;
            bool initialized = false;

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !IsFloorOrStairPrefabSource(renderer.transform))
                {
                    continue;
                }

                if (TryEncapsulateRendererLocalBoundsInSpace(ref bounds, ref initialized, renderer, space))
                {
                    continue;
                }

                EncapsulateWorldBoundsInSpace(ref bounds, ref initialized, renderer.bounds, space);
            }

            if (initialized)
            {
                return true;
            }

            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || !IsFloorOrStairPrefabSource(collider.transform))
                {
                    continue;
                }

                if (TryEncapsulateColliderLocalBoundsInSpace(ref bounds, ref initialized, collider, space))
                {
                    continue;
                }

                EncapsulateWorldBoundsInSpace(ref bounds, ref initialized, collider.bounds, space);
            }

            return initialized;
        }

        private static bool TryEncapsulateRendererLocalBoundsInSpace(
            ref Bounds bounds,
            ref bool initialized,
            Renderer renderer,
            Transform space)
        {
            if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
            {
                EncapsulateLocalBoundsInSpace(ref bounds, ref initialized, skinned.transform, skinned.sharedMesh.bounds, space);
                return true;
            }

            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                EncapsulateLocalBoundsInSpace(ref bounds, ref initialized, meshFilter.transform, meshFilter.sharedMesh.bounds, space);
                return true;
            }

            return false;
        }

        private static bool TryEncapsulateColliderLocalBoundsInSpace(
            ref Bounds bounds,
            ref bool initialized,
            Collider collider,
            Transform space)
        {
            switch (collider)
            {
                case BoxCollider box:
                    EncapsulateLocalBoundsInSpace(ref bounds, ref initialized, box.transform, new Bounds(box.center, box.size), space);
                    return true;
                case SphereCollider sphere:
                    EncapsulateLocalBoundsInSpace(ref bounds, ref initialized, sphere.transform, new Bounds(sphere.center, Vector3.one * sphere.radius * 2f), space);
                    return true;
                case CapsuleCollider capsule:
                    EncapsulateLocalBoundsInSpace(ref bounds, ref initialized, capsule.transform, CapsuleLocalBounds(capsule), space);
                    return true;
                case MeshCollider meshCollider when meshCollider.sharedMesh != null:
                    EncapsulateLocalBoundsInSpace(ref bounds, ref initialized, meshCollider.transform, meshCollider.sharedMesh.bounds, space);
                    return true;
                default:
                    return false;
            }
        }

        private static Bounds CapsuleLocalBounds(CapsuleCollider capsule)
        {
            Vector3 size = Vector3.one * capsule.radius * 2f;
            switch (capsule.direction)
            {
                case 0:
                    size.x = capsule.height;
                    break;
                case 1:
                    size.y = capsule.height;
                    break;
                default:
                    size.z = capsule.height;
                    break;
            }

            return new Bounds(capsule.center, size);
        }

        private static void EncapsulateLocalBoundsInSpace(
            ref Bounds bounds,
            ref bool initialized,
            Transform boundsTransform,
            Bounds localBounds,
            Transform space)
        {
            Vector3 min = localBounds.min;
            Vector3 max = localBounds.max;
            EncapsulateLocalBoundsCorner(ref bounds, ref initialized, boundsTransform, space, new Vector3(min.x, min.y, min.z));
            EncapsulateLocalBoundsCorner(ref bounds, ref initialized, boundsTransform, space, new Vector3(min.x, min.y, max.z));
            EncapsulateLocalBoundsCorner(ref bounds, ref initialized, boundsTransform, space, new Vector3(min.x, max.y, min.z));
            EncapsulateLocalBoundsCorner(ref bounds, ref initialized, boundsTransform, space, new Vector3(min.x, max.y, max.z));
            EncapsulateLocalBoundsCorner(ref bounds, ref initialized, boundsTransform, space, new Vector3(max.x, min.y, min.z));
            EncapsulateLocalBoundsCorner(ref bounds, ref initialized, boundsTransform, space, new Vector3(max.x, min.y, max.z));
            EncapsulateLocalBoundsCorner(ref bounds, ref initialized, boundsTransform, space, new Vector3(max.x, max.y, min.z));
            EncapsulateLocalBoundsCorner(ref bounds, ref initialized, boundsTransform, space, new Vector3(max.x, max.y, max.z));
        }

        private static void EncapsulateLocalBoundsCorner(
            ref Bounds bounds,
            ref bool initialized,
            Transform boundsTransform,
            Transform space,
            Vector3 localPoint)
        {
            EncapsulateLocalPoint(ref bounds, ref initialized, space.InverseTransformPoint(boundsTransform.TransformPoint(localPoint)));
        }

        private static void EncapsulateWorldBoundsInSpace(ref Bounds bounds, ref bool initialized, Bounds worldBounds, Transform space)
        {
            Vector3 min = worldBounds.min;
            Vector3 max = worldBounds.max;
            EncapsulateLocalPoint(ref bounds, ref initialized, space.InverseTransformPoint(new Vector3(min.x, min.y, min.z)));
            EncapsulateLocalPoint(ref bounds, ref initialized, space.InverseTransformPoint(new Vector3(min.x, min.y, max.z)));
            EncapsulateLocalPoint(ref bounds, ref initialized, space.InverseTransformPoint(new Vector3(min.x, max.y, min.z)));
            EncapsulateLocalPoint(ref bounds, ref initialized, space.InverseTransformPoint(new Vector3(min.x, max.y, max.z)));
            EncapsulateLocalPoint(ref bounds, ref initialized, space.InverseTransformPoint(new Vector3(max.x, min.y, min.z)));
            EncapsulateLocalPoint(ref bounds, ref initialized, space.InverseTransformPoint(new Vector3(max.x, min.y, max.z)));
            EncapsulateLocalPoint(ref bounds, ref initialized, space.InverseTransformPoint(new Vector3(max.x, max.y, min.z)));
            EncapsulateLocalPoint(ref bounds, ref initialized, space.InverseTransformPoint(new Vector3(max.x, max.y, max.z)));
        }

        private static void EncapsulateLocalPoint(ref Bounds bounds, ref bool initialized, Vector3 point)
        {
            if (!initialized)
            {
                bounds = new Bounds(point, Vector3.zero);
                initialized = true;
                return;
            }

            bounds.Encapsulate(point);
        }

        private static bool IsFloorOrStairPrefabSource(Transform source)
        {
            Transform current = source;
            while (current != null)
            {
                string sourcePath = PrefabSourcePath(current);
                if (!string.IsNullOrWhiteSpace(sourcePath) && IsFloorOrStairPrefabPath(sourcePath))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static string PrefabSourcePath(Transform source)
        {
            if (source == null)
            {
                return string.Empty;
            }

            GameObject sourceObject = PrefabUtility.GetCorrespondingObjectFromOriginalSource(source.gameObject);
            if (sourceObject == null)
            {
                sourceObject = PrefabUtility.GetCorrespondingObjectFromSource(source.gameObject);
            }

            return sourceObject == null ? string.Empty : AssetDatabase.GetAssetPath(sourceObject);
        }

        private static bool IsFloorOrStairPrefabPath(string assetPath)
        {
            string normalized = assetPath.Replace('\\', '/');
            string fileName = Path.GetFileNameWithoutExtension(normalized);
            if (IsColumnRailWallOrTrim(fileName))
            {
                return false;
            }

            return normalized.StartsWith(AuthoredLevelModuleOneSidedStairsFolder, StringComparison.Ordinal) ||
                normalized.StartsWith(AuthoredCompFloorFolder, StringComparison.Ordinal) ||
                normalized.StartsWith(AuthoredPartStairsFolder, StringComparison.Ordinal) ||
                normalized.StartsWith(AuthoredPartStairsMeshFolder, StringComparison.Ordinal) ||
                normalized.StartsWith(AuthoredPartFloorFolder, StringComparison.Ordinal);
        }

        private static bool IsColumnRailWallOrTrim(string fileName)
        {
            return fileName.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fileName.IndexOf("Railing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fileName.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fileName.IndexOf("Trim", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ValidateConnectionPointTransition(
            GameObject stairInstance,
            ConnectionPointSetPieceContract setPiece,
            ConnectionPointPlacement placement,
            Vector2Int higherCell,
            Vector2Int lowerCell,
            int higherLevel,
            int lowerLevel,
            Vector3 origin,
            float levelHeight,
            Vector3 expectedTopCenter,
            Vector2 expectedLowerDirection,
            Vector2 expectedUpperDirection)
        {
            Vector3 worldEntry = stairInstance.transform.TransformPoint(placement.localEntryPoint);
            Vector3 worldExit = stairInstance.transform.TransformPoint(placement.localExitPoint);
            float lowerY = lowerLevel * levelHeight;
            float higherY = higherLevel * levelHeight;
            const float verticalTolerance = 0.08f;
            const float planTolerance = 0.24f;

            PlanBounds higherBounds = CellPlanBounds(origin, higherCell);
            PlanBounds lowerBounds = CellPlanBounds(origin, lowerCell);
            Vector2Int entryFootprintCell = CellFromWorldPoint(
                origin,
                stairInstance.transform.TransformPoint(LocalCellCenter(setPiece, setPiece.entry.localCell)));
            PlanBounds entryFootprintBounds = CellPlanBounds(origin, entryFootprintCell);
            Vector2 entryPlan = new Vector2(worldEntry.x, worldEntry.z);
            Vector2 exitPlan = new Vector2(worldExit.x, worldExit.z);

            if (Mathf.Abs(worldEntry.y - lowerY) > verticalTolerance ||
                !PlanBoundsContainsPoint(entryFootprintBounds, entryPlan, planTolerance))
            {
                throw new InvalidOperationException(
                    $"entry port check failed for '{setPiece.name}' {setPiece.entry}. " +
                    $"Expected entry footprint cell {entryFootprintCell} at level {lowerLevel} bounds {entryFootprintBounds}, Y {lowerY:0.###}; " +
                    $"got world {Format(worldEntry)} from local {Format(placement.localEntryPoint)}");
            }

            if (PlanDistance(worldExit, placement.worldExitTarget) > planTolerance ||
                Mathf.Abs(worldExit.y - higherY) > verticalTolerance ||
                !PlanBoundsContainsPoint(higherBounds, exitPlan, planTolerance))
            {
                throw new InvalidOperationException(
                    $"exit port check failed for '{setPiece.name}' {setPiece.exit}. " +
                    $"Expected upper cell {higherCell} at level {higherLevel} bounds {higherBounds}, edge target {Format(placement.worldExitTarget)}, Y {higherY:0.###}; " +
                    $"got world {Format(worldExit)} from local {Format(placement.localExitPoint)}");
            }

            Vector2 measuredEntryDirection = Rotate2D(DirectionVector(setPiece.entry.direction), stairInstance.transform.rotation.eulerAngles.y);
            if (Vector2.Dot(measuredEntryDirection.normalized, expectedLowerDirection.normalized) < 0.98f)
            {
                throw new InvalidOperationException(
                    $"entry port direction check failed for '{setPiece.name}' {setPiece.entry}. " +
                    $"Expected outward direction {expectedLowerDirection.normalized} from lower cell {lowerCell}; got {measuredEntryDirection.normalized}");
            }

            Vector2 measuredExitDirection = Rotate2D(DirectionVector(setPiece.exit.direction), stairInstance.transform.rotation.eulerAngles.y);
            if (Vector2.Dot(measuredExitDirection.normalized, expectedUpperDirection.normalized) < 0.98f)
            {
                throw new InvalidOperationException(
                    $"exit port direction check failed for '{setPiece.name}' {setPiece.exit}. " +
                    $"Expected outward direction {expectedUpperDirection.normalized} from upper cell {higherCell}; got {measuredExitDirection.normalized}");
            }

            PlanBounds footprint = TransformPlanBounds(
                setPiece.localPlanBounds,
                stairInstance.transform.position,
                stairInstance.transform.rotation.eulerAngles.y);
            if (!PlanBoundsOverlapOrTouch(footprint, higherBounds, planTolerance) ||
                !PlanBoundsOverlapOrTouch(footprint, lowerBounds, planTolerance))
            {
                throw new InvalidOperationException(
                    $"measured footprint check failed for '{setPiece.name}'. " +
                    $"Footprint {footprint}; expected overlap/touch lower {lowerCell} {lowerBounds} and upper {higherCell} {higherBounds}");
            }

            if (PlanDistance(worldExit, expectedTopCenter) > planTolerance)
            {
                Debug.LogWarning(
                    $"Dungeon Lab Connection Points: exit for '{setPiece.name}' is flush but differs from legacy top-edge center by {PlanDistance(worldExit, expectedTopCenter):0.###}u.");
            }

            Debug.Log(
                $"Dungeon Lab Connection Points: connectionCheck PASS for {setPiece.name} " +
                $"{lowerCell}(L{lowerLevel}) -> {higherCell}(L{higherLevel}); entry {Format(worldEntry)}, exit {Format(worldExit)}.");
        }


        private static Vector3 ParseVector3(JToken token)
        {
            if (token == null)
            {
                return Vector3.zero;
            }

            return new Vector3(
                token.Value<float>("x"),
                token.Value<float>("y"),
                token.Value<float>("z"));
        }


        private static ConnectionPointSetPieceContract FindConnectionPointSetPieceContract(
            IReadOnlyList<ConnectionPointSetPieceContract> setPieces,
            string prefabPath,
            int deltaLevels)
        {
            foreach (ConnectionPointSetPieceContract setPiece in setPieces)
            {
                if (setPiece.riseLevels == deltaLevels &&
                    !string.IsNullOrEmpty(prefabPath) &&
                    string.Equals(setPiece.prefabPath, prefabPath, StringComparison.Ordinal))
                {
                    return setPiece;
                }
            }

            if (!string.IsNullOrEmpty(prefabPath))
            {
                throw new InvalidOperationException($"No measured connection-point set-piece matched transition delta {deltaLevels} and prefab '{prefabPath}'.");
            }

            foreach (ConnectionPointSetPieceContract setPiece in setPieces)
            {
                if (!setPiece.isBridge &&
                    setPiece.riseLevels == deltaLevels)
                {
                    return setPiece;
                }
            }

            throw new InvalidOperationException($"No measured connection-point set-piece matched transition delta {deltaLevels} and prefab '{prefabPath}'.");
        }

        private static ConnectionPointSetPieceContract SelectConnectionPointStairContract(
            TieredPlatformContracts contracts,
            string prefabPath,
            int deltaLevels)
        {
            if (deltaLevels == PrimaryStairRiseLevels &&
                (string.IsNullOrEmpty(prefabPath) ||
                    string.Equals(prefabPath, contracts.connectionPointStraightStair.prefabPath, StringComparison.Ordinal)))
            {
                return contracts.connectionPointStraightStair;
            }

            return FindConnectionPointSetPieceContract(contracts.connectionPointVariantStairs, prefabPath, deltaLevels);
        }

        private static Vector3 LocalEdgeCenter(PlanBounds bounds, Vector2 outwardDirection, float y)
        {
            Vector2 direction = SnapCardinal(outwardDirection);
            if (Mathf.Abs(direction.x) >= Mathf.Abs(direction.y))
            {
                float x = direction.x >= 0f ? bounds.maxX : bounds.minX;
                return new Vector3(x, y, bounds.Center.y);
            }

            float z = direction.y >= 0f ? bounds.maxZ : bounds.minZ;
            return new Vector3(bounds.Center.x, y, z);
        }


        private static void FailStairTransition(
            GameObject stairInstance,
            Vector2Int higherCell,
            Vector2Int lowerCell,
            string message)
        {
            string fullMessage =
                $"Dungeon Lab Elevation Edge Model: rejected stair '{stairInstance.name}' at transition " +
                $"{higherCell} -> {lowerCell}; {message}.";
            Debug.LogError(fullMessage);
            throw new InvalidOperationException(fullMessage);
        }

        private static DropFaceStack BuildDropFaceStack(PackageInventory inventory, float levelHeight)
        {
            var candidates = new List<MeasuredPrefab>();
            var measured = new List<string>();
            foreach (string name in DropFaceCandidateNames)
            {
                if (!inventory.TryGetPrefabPath(name, out string prefabPath))
                {
                    continue;
                }

                MeasuredPrefab candidate = MeasurePrefab(prefabPath, PrefabRole.StraightWall);
                candidates.Add(candidate.WithName(name));
                measured.Add($"{name}={candidate.height:0.###}u");
            }

            foreach (MeasuredPrefab candidate in candidates)
            {
                if (Mathf.Abs(candidate.height - levelHeight) > 0.08f)
                {
                    continue;
                }

                Debug.Log(
                    $"Dungeon Lab Tiered Platforms: measured vertical contract. Stair variant {StairRiseVariant} levelHeight {levelHeight:0.###}u; " +
                    $"single cliff wall {candidate.name} height {candidate.height:0.###}u.");
                return new DropFaceStack(new[] { candidate }, candidate.height);
            }

            if (TryBuildStack(candidates, levelHeight, out List<MeasuredPrefab> stack, out float totalHeight))
            {
                Debug.LogWarning(
                    $"Dungeon Lab Tiered Platforms: no single cliff wall matched stair rise {levelHeight:0.###}u; " +
                    $"using measured stack {StackDescription(stack)} = {totalHeight:0.###}u. Candidates: {string.Join(", ", measured)}.");
                return new DropFaceStack(stack.ToArray(), totalHeight);
            }

            throw new InvalidOperationException(
                $"No cliff wall/base height or measured stack matched stair rise {levelHeight:0.###}u. Candidates: {string.Join(", ", measured)}.");
        }

        private static DropFaceStack BuildCornerStack(PackageInventory inventory, float levelHeight)
        {
            var candidates = new List<MeasuredPrefab>();
            var measured = new List<string>();
            foreach (string name in CornerCandidateNames)
            {
                if (!inventory.TryGetPrefabPath(name, out string prefabPath))
                {
                    continue;
                }

                MeasuredPrefab candidate = MeasurePrefab(prefabPath, PrefabRole.HardCorner).WithName(name);
                candidates.Add(candidate);
                measured.Add($"{name}={candidate.height:0.###}u/{CornerQuadrantName(candidate.baseQuadrant)}");
            }

            foreach (MeasuredPrefab candidate in candidates)
            {
                if (Mathf.Abs(candidate.height - levelHeight) > 0.08f)
                {
                    continue;
                }

                Debug.Log(
                    $"Dungeon Lab Tiered Platforms: measured hard-corner contract. Stair variant {StairRiseVariant} levelHeight {levelHeight:0.###}u; " +
                    $"single corner {candidate.name} height {candidate.height:0.###}u, base {CornerQuadrantName(candidate.baseQuadrant)}.");
                return new DropFaceStack(new[] { candidate }, candidate.height);
            }

            if (TryBuildStack(candidates, levelHeight, out List<MeasuredPrefab> stack, out float totalHeight))
            {
                Debug.LogWarning(
                    $"Dungeon Lab Tiered Platforms: no single hard corner matched stair rise {levelHeight:0.###}u; " +
                    $"using measured stack {StackDescription(stack)} = {totalHeight:0.###}u. Candidates: {string.Join(", ", measured)}.");
                return new DropFaceStack(stack.ToArray(), totalHeight);
            }

            throw new InvalidOperationException(
                $"No hard-corner height or measured stack matched stair rise {levelHeight:0.###}u. Candidates: {string.Join(", ", measured)}.");
        }

        private static PartitionWallContracts BuildPartitionWallContracts(PackageInventory inventory)
        {
            string mediumWallPath = inventory.GetPrefabPath(PartitionWallMediumName);
            string largeWallPath = inventory.GetPrefabPath(PartitionWallLargeName);
            string mediumCornerPath = inventory.GetPrefabPath(PartitionCornerMediumName);
            string largeCornerPath = inventory.GetPrefabPath(PartitionCornerLargeName);
            ValidateDoubleSidedWallPrefab(mediumWallPath, PartitionWallMediumName);
            ValidateDoubleSidedWallPrefab(largeWallPath, PartitionWallLargeName);
            MeasuredPrefab mediumWall = MeasurePrefab(
                mediumWallPath,
                PrefabRole.StraightWall).WithName(PartitionWallMediumName);
            MeasuredPrefab largeWall = MeasurePrefab(
                largeWallPath,
                PrefabRole.StraightWall).WithName(PartitionWallLargeName);
            MeasuredPrefab mediumCorner = MeasurePrefab(
                mediumCornerPath,
                PrefabRole.HardCorner).WithName(PartitionCornerMediumName);
            MeasuredPrefab largeCorner = MeasurePrefab(
                largeCornerPath,
                PrefabRole.HardCorner).WithName(PartitionCornerLargeName);
            Debug.Log(
                $"Dungeon Lab Elevation Edge Model: measured PivotMiddle partition contracts. " +
                $"medium wall {PartitionWallMediumName} height {mediumWall.height:0.###}u, " +
                $"large wall {PartitionWallLargeName} height {largeWall.height:0.###}u; " +
                $"medium corner {PartitionCornerMediumName} height {mediumCorner.height:0.###}u, " +
                $"large corner {PartitionCornerLargeName} height {largeCorner.height:0.###}u.");
            return new PartitionWallContracts(
                mediumWall,
                largeWall,
                mediumCorner,
                largeCorner);
        }

        private static GatewayContracts BuildGatewayContracts(PackageInventory inventory)
        {
            MeasuredPrefab mediumMetal = MeasureGatewayPrefab(
                inventory,
                GatewayMediumMetalName);
            MeasuredPrefab largeWallMetal = MeasureGatewayPrefab(
                inventory,
                GatewayLargeWallMetalName);
            MeasuredPrefab mediumWood = MeasureGatewayPrefab(
                inventory,
                GatewayMediumWoodName);
            MeasuredPrefab largeWallWood = MeasureGatewayPrefab(
                inventory,
                GatewayLargeWallWoodName);
            MeasuredPrefab largeWood = MeasureGatewayPrefab(
                inventory,
                GatewayLargeWoodName);
            string barsPrefabPath = inventory.GetPrefabPath(GatewayBarsName);

            Debug.Log(
                "Dungeon Lab Elevation Edge Model: measured static gateway contracts. " +
                $"{GatewayMediumMetalName}=4u metal, {GatewayLargeWallMetalName}=6u metal, " +
                $"{GatewayMediumWoodName}=4u wood, {GatewayLargeWallWoodName}=6u wood, " +
                $"{GatewayLargeWoodName}=6u double wood; barred openings use {GatewayBarsName}.");
            return new GatewayContracts(
                mediumMetal,
                largeWallMetal,
                mediumWood,
                largeWallWood,
                largeWood,
                barsPrefabPath);
        }

        private static MeasuredPrefab MeasureGatewayPrefab(
            PackageInventory inventory,
            string prefabName)
        {
            MeasuredPrefab measured = MeasurePrefab(
                inventory.GetPrefabPath(prefabName),
                PrefabRole.StraightWall).WithName(prefabName);
            // Every reviewed package gateway (metal, wood, tall, and double-door)
            // authors the same one-cell socket: the frame columns sit at local
            // x=-4 and x=0 on the z=0 wall plane. Door leaves and ornament can
            // extend farther in plan, so renderer/collider bounds are not a valid
            // attachment axis or center.
            return measured.WithAttachmentSegment(
                new Vector2(-CellSize, 0f),
                Vector2.zero,
                Vector2.down);
        }

        private static void ValidateDoubleSidedWallPrefab(string prefabPath, string prefabName)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException($"Missing partition wall prefab '{prefabName}' at '{prefabPath}'.");
            }

            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.hideFlags = HideFlags.HideAndDontSave;
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;

                if (!TryMeasureOpposingHorizontalFaceAreas(instance, out string axis, out float positiveArea, out float negativeArea))
                {
                    throw new InvalidOperationException(
                        $"Partition wall prefab '{prefabName}' did not measure as double-sided. " +
                        "Expected substantial opposing horizontal face normals.");
                }

                Debug.Log(
                    $"Dungeon Lab Elevation Edge Model: verified double-sided partition wall '{prefabName}' on {axis} axis; " +
                    $"opposing face areas +={positiveArea:0.###}, -={negativeArea:0.###}.");
            }
            finally
            {
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
        }

        private static bool TryMeasureOpposingHorizontalFaceAreas(
            GameObject instance,
            out string axis,
            out float positiveArea,
            out float negativeArea)
        {
            float positiveX = 0f;
            float negativeX = 0f;
            float positiveZ = 0f;
            float negativeZ = 0f;
            foreach (MeshFilter meshFilter in instance.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = meshFilter.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0)
                {
                    continue;
                }

                Matrix4x4 localToRoot = instance.transform.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix;
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    Vector3 a = localToRoot.MultiplyPoint3x4(vertices[triangles[i]]);
                    Vector3 b = localToRoot.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                    Vector3 c = localToRoot.MultiplyPoint3x4(vertices[triangles[i + 2]]);
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    float doubleArea = cross.magnitude;
                    if (doubleArea <= 0.000001f)
                    {
                        continue;
                    }

                    Vector3 normal = cross / doubleArea;
                    Vector2 horizontal = new Vector2(normal.x, normal.z);
                    if (horizontal.sqrMagnitude < 0.2f)
                    {
                        continue;
                    }

                    horizontal.Normalize();
                    if (Mathf.Abs(horizontal.x) >= Mathf.Abs(horizontal.y))
                    {
                        if (horizontal.x >= 0f)
                        {
                            positiveX += doubleArea;
                        }
                        else
                        {
                            negativeX += doubleArea;
                        }
                    }
                    else
                    {
                        if (horizontal.y >= 0f)
                        {
                            positiveZ += doubleArea;
                        }
                        else
                        {
                            negativeZ += doubleArea;
                        }
                    }
                }
            }

            float xPair = Mathf.Min(positiveX, negativeX);
            float zPair = Mathf.Min(positiveZ, negativeZ);
            if (xPair >= zPair)
            {
                axis = "X";
                positiveArea = positiveX;
                negativeArea = negativeX;
            }
            else
            {
                axis = "Z";
                positiveArea = positiveZ;
                negativeArea = negativeZ;
            }

            float maxArea = Mathf.Max(positiveArea, negativeArea);
            float minArea = Mathf.Min(positiveArea, negativeArea);
            return minArea > 0.5f && maxArea > 0f && minArea / maxArea >= 0.25f;
        }

        private static bool TryBuildStack(
            List<MeasuredPrefab> candidates,
            float targetHeight,
            out List<MeasuredPrefab> stack,
            out float totalHeight)
        {
            stack = new List<MeasuredPrefab>();
            totalHeight = 0f;
            candidates.Sort((left, right) => right.height.CompareTo(left.height));
            return TryBuildStackRecursive(candidates, targetHeight, 0f, stack, depth: 0, maxDepth: 4, out totalHeight);
        }

        private static bool TryBuildStackRecursive(
            List<MeasuredPrefab> candidates,
            float targetHeight,
            float currentHeight,
            List<MeasuredPrefab> stack,
            int depth,
            int maxDepth,
            out float totalHeight)
        {
            if (Mathf.Abs(currentHeight - targetHeight) <= 0.08f)
            {
                totalHeight = currentHeight;
                return true;
            }

            if (currentHeight > targetHeight + 0.08f || depth >= maxDepth)
            {
                totalHeight = currentHeight;
                return false;
            }

            foreach (MeasuredPrefab candidate in candidates)
            {
                if (candidate.height <= 0.05f)
                {
                    continue;
                }

                stack.Add(candidate);
                if (TryBuildStackRecursive(candidates, targetHeight, currentHeight + candidate.height, stack, depth + 1, maxDepth, out totalHeight))
                {
                    return true;
                }

                stack.RemoveAt(stack.Count - 1);
            }

            totalHeight = currentHeight;
            return false;
        }

        private static RailingAuthoredOffsets MeasureAuthoredRailingOffsets(
            PackageInventory inventory,
            MeasuredPrefab floor,
            MeasuredPrefab railing)
        {
            string modulePath = inventory.GetPrefabPath(AuthoredFlatRailingModuleName);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(modulePath);
            if (prefab == null)
            {
                throw new InvalidOperationException($"Missing authored railing module '{AuthoredFlatRailingModuleName}' at '{modulePath}'.");
            }

            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.hideFlags = HideFlags.HideAndDontSave;
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;

                Transform floorTransform = FindFirstChild(instance.transform, IsFloorName);
                if (floorTransform == null)
                {
                    throw new InvalidOperationException($"Authored module '{AuthoredFlatRailingModuleName}' did not contain a floor child.");
                }

                var railings = new List<RailingCandidate>();
                foreach (Transform child in instance.transform)
                {
                    if (!IsStraightRailingName(child.name))
                    {
                        continue;
                    }

                    var relative = new RelativeTransform(
                        floorTransform.InverseTransformPoint(child.position),
                        Quaternion.Inverse(floorTransform.rotation) * child.rotation);
                    Vector2 start = TransformRelativePlanPoint(relative, railing.localSegmentStart);
                    Vector2 end = TransformRelativePlanPoint(relative, railing.localSegmentEnd);
                    int side = FindNearestFloorEdge(floor.localPlanBounds, start, end);
                    railings.Add(new RailingCandidate(relative, start, end, side));
                }

                if (railings.Count == 0)
                {
                    throw new InvalidOperationException($"Authored module '{AuthoredFlatRailingModuleName}' did not contain any '{RailingName}' child.");
                }

                var columns = new List<RelativeTransform>();
                foreach (Transform child in instance.transform)
                {
                    if (!child.name.Contains(RailingColumnName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    columns.Add(new RelativeTransform(
                        floorTransform.InverseTransformPoint(child.position),
                        Quaternion.Inverse(floorTransform.rotation) * child.rotation));
                }

                foreach (RailingCandidate candidate in railings)
                {
                    if (!TryFindEndpointColumns(columns, candidate.start, candidate.end, out RelativeTransform startColumn, out RelativeTransform endColumn))
                    {
                        continue;
                    }

                    Vector3 startPoint = new Vector3(candidate.start.x, 0f, candidate.start.y);
                    Vector3 endPoint = new Vector3(candidate.end.x, 0f, candidate.end.y);
                    return new RailingAuthoredOffsets(
                        candidate.relative,
                        candidate.side,
                        startColumn,
                        endColumn,
                        startColumn.position - startPoint,
                        endColumn.position - endPoint);
                }

                throw new InvalidOperationException(
                    $"Authored module '{AuthoredFlatRailingModuleName}' did not contain railing columns at any measured straight railing endpoints.");
            }
            finally
            {
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
        }

        private static GameObject PlaceFloor(
            MeasuredPrefab floor,
            Transform parent,
            string name,
            Vector3 cellMin,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            Vector3 position = FloorPivotForTopSurface(floor, cellMin);
            GameObject instance = InstantiatePrefab(floor.prefabPath, name, parent, position, 0f);
            EncapsulateInstance(instance, ref bounds, ref hasBounds);
            return instance;
        }

        private static Vector3 FloorPivotForTopSurface(MeasuredPrefab floor, Vector3 topSurfaceCellMin)
        {
            Vector2 localMin = floor.localPlanBounds.Min;
            return topSurfaceCellMin - new Vector3(localMin.x, floor.localTopY, localMin.y);
        }

        private static void PlaceCliffStack(
            DropFaceStack stack,
            Transform parent,
            PlatformEdge edge,
            Vector3 origin,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            Vector3 cellMin = CellMin(origin, edge.x, edge.z, 0f);
            Vector3 cellMax = cellMin + new Vector3(CellSize, 0f, CellSize);
            GetEdgePlacement(edge, cellMin, cellMax, out Vector3 edgeA, out Vector3 edgeB, out Vector2 outwardNormal);
            float yOffset = 0f;
            for (int i = 0; i < stack.pieces.Length; i++)
            {
                MeasuredPrefab piece = stack.pieces[i];
                PlaceEdgePrefab(
                    piece,
                    parent,
                    $"cliff_{DirectionName(edge.direction).ToLowerInvariant()}_{edge.x}_{edge.z}_{i}",
                    edgeA + Vector3.up * yOffset,
                    edgeB + Vector3.up * yOffset,
                    outwardNormal,
                    ref bounds,
                    ref hasBounds);
                yOffset += piece.height;
            }
        }

        private static void PlaceCliffCornerStack(
            DropFaceStack stack,
            Transform parent,
            CornerPlacement corner,
            Vector3 origin,
            float levelHeight,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            // The corner course follows the wall faces' top anchoring (11bd8d4): the
            // shallowest adjoining drop decides the sink, so an odd drop hides the
            // remainder below the lower floor and a 1u drop's corner stays flush with
            // its upper floor instead of poking 1u above it.
            float dropHeight = Mathf.Max(corner.dropLevels, 1) * levelHeight;
            int courseCount = Mathf.CeilToInt((dropHeight - 0.01f) / stack.totalHeight);
            float sink = dropHeight - courseCount * stack.totalHeight;
            Vector3 vertexPosition = origin + new Vector3(
                corner.vertex.x * CellSize,
                corner.baseLevel * levelHeight + sink,
                corner.vertex.y * CellSize);
            float yOffset = 0f;
            for (int i = 0; i < stack.pieces.Length; i++)
            {
                MeasuredPrefab piece = stack.pieces[i];
                float yRotation = CalculateCornerYaw(piece.baseQuadrant, corner.targetQuadrant);
                if (!DoesRotatedBoundsOccupyQuadrant(piece.localPlanBounds, yRotation, corner.targetQuadrant))
                {
                    throw new InvalidOperationException(
                        $"Hard corner '{piece.name}' refused vertex {corner.vertex}. Rotation {yRotation:0.###} from measured base " +
                        $"{CornerQuadrantName(piece.baseQuadrant)} does not occupy target quadrant {CornerQuadrantName(corner.targetQuadrant)}.");
                }

                GameObject instance = InstantiatePrefab(
                    piece.prefabPath,
                    $"corner_{CornerQuadrantName(corner.targetQuadrant).ToLowerInvariant()}_{corner.vertex.x}_{corner.vertex.y}_{i}",
                    parent,
                    vertexPosition + Vector3.up * yOffset,
                    yRotation);
                EncapsulateInstance(instance, ref bounds, ref hasBounds);
                yOffset += piece.height;
            }
        }

        private static void PlacePartitionCorner(
            PartitionWallContracts contracts,
            Transform parent,
            CornerPlacement corner,
            Vector3 origin,
            float levelHeight,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            MeasuredPrefab piece = contracts.CornerForHeight(corner.partitionHeightUnits);
            Vector3 vertexPosition = origin + new Vector3(corner.vertex.x * CellSize, corner.baseLevel * levelHeight, corner.vertex.y * CellSize);
            float yRotation = CalculateCornerYaw(piece.baseQuadrant, corner.targetQuadrant);
            if (!DoesRotatedBoundsOccupyQuadrant(piece.localPlanBounds, yRotation, corner.targetQuadrant))
            {
                throw new InvalidOperationException(
                    $"PivotMiddle partition corner '{piece.name}' refused vertex {corner.vertex}. Rotation {yRotation:0.###} from measured base " +
                    $"{CornerQuadrantName(piece.baseQuadrant)} does not occupy target quadrant {CornerQuadrantName(corner.targetQuadrant)}.");
            }

            GameObject instance = InstantiatePrefab(
                piece.prefabPath,
                $"partition_corner_{CornerQuadrantName(corner.targetQuadrant).ToLowerInvariant()}_{corner.vertex.x}_{corner.vertex.y}",
                parent,
                vertexPosition,
                yRotation);
            EncapsulateInstance(instance, ref bounds, ref hasBounds);
        }

        private static GameObject PlaceEdgePrefab(
            MeasuredPrefab measured,
            Transform parent,
            string name,
            Vector3 edgeA,
            Vector3 edgeB,
            Vector2 outwardNormal,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            Vector2 localDirection = measured.localSegmentEnd - measured.localSegmentStart;
            Vector2 worldDirection = new Vector2(edgeB.x - edgeA.x, edgeB.z - edgeA.z);
            if (localDirection.sqrMagnitude <= 0.0001f || worldDirection.sqrMagnitude <= 0.0001f)
            {
                throw new InvalidOperationException($"Edge '{name}' has invalid local or world length.");
            }

            Vector3 start = edgeA;
            float yRotation = CalculateYawToMap(localDirection, worldDirection);
            Vector2 transformedFace = Rotate2D(measured.faceNormal, yRotation);
            if (Vector2.Dot(transformedFace.normalized, outwardNormal.normalized) < 0f)
            {
                start = edgeB;
                worldDirection = -worldDirection;
                yRotation = CalculateYawToMap(localDirection, worldDirection);
            }

            Quaternion rotation = Quaternion.Euler(0f, yRotation, 0f);
            Vector3 localStart = new Vector3(measured.localSegmentStart.x, 0f, measured.localSegmentStart.y);
            GameObject instance = InstantiatePrefab(measured.prefabPath, name, parent, start - rotation * localStart, yRotation);
            EncapsulateInstance(instance, ref bounds, ref hasBounds);
            return instance;
        }

        private static void ValidatePlacedEdgePrefab(
            GameObject instance,
            MeasuredPrefab measured,
            Vector3 edgeA,
            Vector3 edgeB,
            Vector2 outwardNormal)
        {
            Vector3 worldStart = instance.transform.TransformPoint(new Vector3(measured.localSegmentStart.x, 0f, measured.localSegmentStart.y));
            Vector3 worldEnd = instance.transform.TransformPoint(new Vector3(measured.localSegmentEnd.x, 0f, measured.localSegmentEnd.y));
            const float tolerance = 0.08f;
            bool forwardMatches = PlanDistance(worldStart, edgeA) <= tolerance && PlanDistance(worldEnd, edgeB) <= tolerance;
            bool reverseMatches = PlanDistance(worldStart, edgeB) <= tolerance && PlanDistance(worldEnd, edgeA) <= tolerance;
            if (!forwardMatches && !reverseMatches)
            {
                throw new InvalidOperationException(
                    $"Partition wall '{instance.name}' failed edge validation. Expected {Format(edgeA)} -> {Format(edgeB)}, " +
                    $"measured {Format(worldStart)} -> {Format(worldEnd)}.");
            }

            Vector2 transformedFace = Rotate2D(measured.faceNormal, instance.transform.rotation.eulerAngles.y);
            if (Vector2.Dot(transformedFace.normalized, outwardNormal.normalized) < -0.05f)
            {
                throw new InvalidOperationException(
                    $"Partition wall '{instance.name}' faced inward. Expected outward {outwardNormal}, measured {transformedFace.normalized}.");
            }
        }

        // A measured rise-1 step strip from the step piece library, used for in-room
        // seam transitions. Local geometry comes from metrology (never from names):
        // the strip is pivot-anchored at the top; its exit edge center is placed on
        // the shared cell edge at the raised floor height.
        private readonly struct SeamStripPiece
        {
            public readonly Vector3 localExitEdgeCenter;
            public readonly Vector2 localClimbDirection;
            public readonly float riseUnits;

            // Side cover shell (P_MOD_Stairs_01_WallCover family) co-located with the
            // strip; empty when the measured library has no matching cover yet (the
            // strip then renders with open sides, the pre-step-6 behavior).
            public readonly string sideCoverPrefabPath;

            public SeamStripPiece(Vector3 localExitEdgeCenter, Vector2 localClimbDirection, float riseUnits, string sideCoverPrefabPath)
            {
                this.localExitEdgeCenter = localExitEdgeCenter;
                this.localClimbDirection = localClimbDirection;
                this.riseUnits = riseUnits;
                this.sideCoverPrefabPath = sideCoverPrefabPath;
            }
        }

        private static readonly Dictionary<string, SeamStripPiece> seamStripCache =
            new Dictionary<string, SeamStripPiece>(StringComparer.Ordinal);

        private static SeamStripPiece LoadSeamStripPiece(string prefabPath, float levelHeight, int riseLevels = 1)
        {
            if (!File.Exists(StepPieceLibraryPath))
            {
                throw new InvalidOperationException(
                    $"Seam transitions need the measured step piece library at '{StepPieceLibraryPath}'. Run Tools > Dungeon Lab > Measure Step Piece Library.");
            }

            // The metrology tool can re-measure the library WITHOUT a script
            // compile (no domain reload), so a session cache keyed only by prefab
            // path can serve stale results — e.g. "no side cover" cached before
            // the cover family was ingested. Invalidate on library file change.
            DateTime libraryWriteTimeUtc = File.GetLastWriteTimeUtc(StepPieceLibraryPath);
            if (libraryWriteTimeUtc != seamStripCacheLibraryWriteTimeUtc)
            {
                seamStripCache.Clear();
                seamStripCacheLibraryWriteTimeUtc = libraryWriteTimeUtc;
            }

            if (seamStripCache.TryGetValue(prefabPath, out SeamStripPiece cached))
            {
                return cached;
            }

            JObject root = JObject.Parse(File.ReadAllText(StepPieceLibraryPath));
            JToken record = null;
            if (root["pieces"] is JArray pieces)
            {
                foreach (JToken piece in pieces)
                {
                    if (string.Equals(piece.Value<string>("path"), prefabPath, StringComparison.Ordinal))
                    {
                        record = piece;
                        break;
                    }
                }
            }

            if (record == null)
            {
                throw new InvalidOperationException(
                    $"Seam strip '{prefabPath}' has no record in {StepPieceLibraryPath}; re-run the metrology tool.");
            }

            if (!string.Equals(record.Value<string>("confidence"), "high", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Seam strip '{prefabPath}' is not high-confidence in {StepPieceLibraryPath}; review its measurement first.");
            }

            float riseUnits = record.Value<float?>("riseUnits") ?? 0f;
            if (record.Value<int?>("riseLevels") != riseLevels || Mathf.Abs(riseUnits - riseLevels * levelHeight) > 0.08f)
            {
                throw new InvalidOperationException(
                    $"Step strip '{prefabPath}' measured rise {riseUnits:0.###}u, not the expected {riseLevels * levelHeight:0.###}u.");
            }

            string sideCoverPath = FindMeasuredSideCoverPath(
                root,
                record.Value<float?>("runUnits") ?? 0f,
                record.Value<float?>("lateralWidthUnits") ?? 0f,
                riseUnits);
            if (string.IsNullOrEmpty(sideCoverPath) && warnedMissingSeamSideCover.Add(prefabPath))
            {
                Debug.LogWarning(
                    $"Dungeon Lab Elevation Edge Model: no measured stairSideCover matches seam strip '{prefabPath}' " +
                    "(its sides render open). Re-run Tools > Dungeon Lab > Measure Step Piece Library to ingest the WallCover family.");
            }

            string climbAxis = record.Value<string>("climbAxis") ?? string.Empty;
            Vector2 localClimb;
            switch (climbAxis)
            {
                case "x+":
                    localClimb = Vector2.right;
                    break;
                case "x-":
                    localClimb = Vector2.left;
                    break;
                case "z+":
                    localClimb = Vector2.up;
                    break;
                case "z-":
                    localClimb = Vector2.down;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Seam strip '{prefabPath}' has unusable measured climb axis '{climbAxis}'.");
            }

            Vector3 boundsMin = ParseVector3(record["boundsMin"]);
            Vector3 boundsMax = ParseVector3(record["boundsMax"]);
            float walkTopY = record.Value<float?>("walkSurfaceTopY") ?? 0f;
            Vector3 exitEdgeCenter;
            if (localClimb.x > 0.5f)
            {
                exitEdgeCenter = new Vector3(boundsMax.x, walkTopY, (boundsMin.z + boundsMax.z) * 0.5f);
            }
            else if (localClimb.x < -0.5f)
            {
                exitEdgeCenter = new Vector3(boundsMin.x, walkTopY, (boundsMin.z + boundsMax.z) * 0.5f);
            }
            else if (localClimb.y > 0.5f)
            {
                exitEdgeCenter = new Vector3((boundsMin.x + boundsMax.x) * 0.5f, walkTopY, boundsMax.z);
            }
            else
            {
                exitEdgeCenter = new Vector3((boundsMin.x + boundsMax.x) * 0.5f, walkTopY, boundsMin.z);
            }

            var loaded = new SeamStripPiece(exitEdgeCenter, localClimb, riseUnits, sideCoverPath);
            seamStripCache[prefabPath] = loaded;
            return loaded;
        }

        private static readonly HashSet<string> warnedMissingSeamSideCover = new HashSet<string>(StringComparer.Ordinal);
        private static DateTime seamStripCacheLibraryWriteTimeUtc = DateTime.MinValue;

        // The pack's WallCover shells are authored co-located with their flight
        // (same pivot, same climb axis), so the right cover is the one whose
        // measured run, lateral width AND height match the flight's; placement is
        // then the flight's own transform. Height matters: the cover family shares
        // one run and width per family and differs only in elevation, so a
        // run+width match alone would pair every flight with the same piece.
        // Names pick nothing here — only measured sizes.
        private static string FindMeasuredSideCoverPath(JObject libraryRoot, float runUnits, float lateralWidthUnits, float riseUnits)
        {
            if (!(libraryRoot["pieces"] is JArray pieces) || runUnits <= 0f || lateralWidthUnits <= 0f)
            {
                return string.Empty;
            }

            string bestPath = string.Empty;
            float bestRiseError = float.MaxValue;
            string bestName = string.Empty;
            foreach (JToken piece in pieces)
            {
                if (!string.Equals(piece.Value<string>("category"), "stairSideCover", StringComparison.Ordinal) ||
                    !string.Equals(piece.Value<string>("confidence"), "high", StringComparison.Ordinal))
                {
                    continue;
                }

                JToken size = piece["sizeUnits"];
                if (size == null)
                {
                    continue;
                }

                float runError = Mathf.Abs(size.Value<float>("x") - runUnits);
                float widthError = Mathf.Abs(size.Value<float>("z") - lateralWidthUnits);
                float riseError = Mathf.Abs(size.Value<float>("y") - riseUnits);
                if (runError > 0.3f || widthError > 0.5f || riseError > 0.3f)
                {
                    continue;
                }

                string name = piece.Value<string>("name") ?? string.Empty;
                if (riseError < bestRiseError - 0.001f ||
                    (Mathf.Abs(riseError - bestRiseError) <= 0.001f && string.CompareOrdinal(name, bestName) < 0))
                {
                    bestRiseError = riseError;
                    bestPath = piece.Value<string>("path") ?? string.Empty;
                    bestName = name;
                }
            }

            return bestPath;
        }

        // Dais rim corner notches (step 9, decision 40a — the user's manual fix
        // automated): where two perpendicular dais rim strips meet at a convex
        // corner, the diagonal cell keeps a bare quarter-cell notch of the
        // raised mass. The measured _4 tier-step pieces are exactly that notch:
        // 1u rise, 2x2 plan, pivot at the TOP of the rise on the corner vertex,
        // wedge extending into one quadrant ((-x,+z) at yaw 0 by authored
        // bounds). The corner vertex is the dais mass corner, so the piece
        // rotates so its footprint fills the diagonal cell's quadrant nearest
        // the vertex. Style (angle chamfer vs quarter-round) rolls once per
        // dais cluster (decision 42); a notch whose diagonal cell is missing,
        // reserved or mis-leveled stays bare — the ledge-policy fallback.
        private static void PlaceDaisCornerPieces(
            IReadOnlyList<TransitionEdge> transitions,
            IReadOnlyDictionary<Vector2Int, int> levels,
            Dictionary<Vector2Int, Vector2Int> sunkenDaisCorners,
            HashSet<Vector2Int> reservedCells,
            Vector3 origin,
            float levelHeight,
            Transform parent,
            ref Bounds bounds,
            ref bool hasBounds,
            TieredPlatformBuildStats stats)
        {
            // Rim faces with a strip, keyed by the raised dais cell and the
            // outward direction toward the strip's lower cell.
            var stripFaces = new HashSet<(Vector2Int daisCell, Vector2Int outward)>();
            var daisCells = new HashSet<Vector2Int>();
            foreach (TransitionEdge transition in transitions)
            {
                if (!string.Equals(transition.placementClass, DaisStairPlacementClass, StringComparison.Ordinal) ||
                    !transition.HasLevels ||
                    transition.RiseLevels == 0)
                {
                    continue;
                }

                int firstLevel = transition.firstLevel;
                int secondLevel = transition.secondLevel;

                Vector2Int daisCell = firstLevel > secondLevel ? transition.firstCell : transition.secondCell;
                Vector2Int ringCell = firstLevel > secondLevel ? transition.secondCell : transition.firstCell;
                stripFaces.Add((daisCell, ringCell - daisCell));
                daisCells.Add(daisCell);
            }

            if (stripFaces.Count == 0)
            {
                return;
            }

            int placed = 0;
            foreach (Vector2Int daisCell in daisCells)
            {
                int daisLevel = levels[daisCell];
                for (int dx = -1; dx <= 1; dx += 2)
                {
                    for (int dz = -1; dz <= 1; dz += 2)
                    {
                        if (!stripFaces.Contains((daisCell, new Vector2Int(dx, 0))) ||
                            !stripFaces.Contains((daisCell, new Vector2Int(0, dz))))
                        {
                            continue;
                        }

                        // Both rim faces climb the same rise by construction;
                        // the diagonal must be clean floor a full rise below.
                        if (!levels.TryGetValue(new Vector2Int(daisCell.x + dx, daisCell.y), out int sideLevel) ||
                            sideLevel >= daisLevel)
                        {
                            continue;
                        }

                        int rise = daisLevel - sideLevel;
                        var diagonalCell = new Vector2Int(daisCell.x + dx, daisCell.y + dz);
                        if (!levels.TryGetValue(diagonalCell, out int diagonalLevel) ||
                            diagonalLevel != daisLevel - rise ||
                            reservedCells.Contains(diagonalCell))
                        {
                            continue;
                        }

                        // Corner scale matches the strip protrusion (gallery
                        // rounds 3-5): rise 1 = the quarter-cell _4 notch,
                        // rise 2 = the full-cell _3 sweep — both pivot on the
                        // dais corner vertex with the same quadrant yaw map.
                        bool angleStyle = ChooseDaisCornerStyle(daisCell, daisCells) == 0;
                        string pieceName = rise == 1
                            ? (angleStyle ? "P_MOD_Stairs_01_E_angle_convex_4" : "P_MOD_Stairs_01_E_convex_4")
                            : (angleStyle ? "P_MOD_Stairs_01_E_angle_convex_3" : "P_MOD_Stairs_01_E_convex_3");
                        if (!TryLoadTierStepPiece(pieceName, out TierStepPiece piece))
                        {
                            return;
                        }

                        // The corner vertex shared by the dais cell and the
                        // diagonal cell; the piece fills the diagonal cell from
                        // it. Footprint at yaw 0 is (-x,+z).
                        var vertex = new Vector3(
                            origin.x + (daisCell.x + (dx > 0 ? 1 : 0)) * CellSize,
                            origin.y + daisLevel * levelHeight,
                            origin.z + (daisCell.y + (dz > 0 ? 1 : 0)) * CellSize);
                        float yaw = dx > 0
                            ? (dz > 0 ? 90f : 180f)
                            : (dz > 0 ? 0f : 270f);
                        GameObject instance = InstantiatePrefab(
                            piece.prefabPath,
                            $"dais_corner_{daisCell.x}_{daisCell.y}_{dx}_{dz}",
                            parent,
                            vertex,
                            yaw);
                        EncapsulateInstance(instance, ref bounds, ref hasBounds);
                        placed++;
                    }
                }
            }

            // Sunken corners: one concave sweep ON the pit corner cell with the
            // med floor cap at the surrounding level (the gallery-approved
            // construction; this cell's strips were suppressed in the render
            // loop). Yaw = the raised base table + the approved +90 offset.
            var pitCells = new HashSet<Vector2Int>(sunkenDaisCorners.Keys);
            foreach (KeyValuePair<Vector2Int, Vector2Int> entry in sunkenDaisCorners)
            {
                Vector2Int pitCell = entry.Key;
                Vector2Int outward = entry.Value;
                if (!levels.TryGetValue(pitCell, out int pitLevel) ||
                    !levels.TryGetValue(new Vector2Int(pitCell.x + outward.x, pitCell.y), out int rimLevel) ||
                    rimLevel <= pitLevel)
                {
                    continue;
                }

                int rise = rimLevel - pitLevel;
                bool angleStyle = ChooseDaisCornerStyle(pitCell, pitCells) == 0;
                string pieceName = rise == 1
                    ? (angleStyle ? "P_MOD_Stairs_01_E_angle_concave_5" : "P_MOD_Stairs_01_E_concave_5")
                    : (angleStyle ? "P_MOD_Stairs_01_E_angle_concave_3" : "P_MOD_Stairs_01_E_concave_3");
                string capName = angleStyle ? "P_MOD_Floor_01_O_angle_med" : "P_MOD_Floor_01_O_concave_med";
                if (!TryLoadTierStepPiece(pieceName, out TierStepPiece piece) ||
                    !TryLoadTierStepPiece(capName, out TierStepPiece cap))
                {
                    return;
                }

                float yaw = Mathf.Repeat(
                    (outward.x < 0
                        ? (outward.y < 0 ? 0f : 90f)
                        : (outward.y < 0 ? 270f : 180f)) + 90f,
                    360f);
                Vector3 pivot = DaisFullCellPivotWorld(pitCell, yaw, origin) + Vector3.up * (rimLevel * levelHeight);
                GameObject sweep = InstantiatePrefab(
                    piece.prefabPath,
                    $"dais_pit_corner_{pitCell.x}_{pitCell.y}",
                    parent,
                    pivot,
                    yaw);
                EncapsulateInstance(sweep, ref bounds, ref hasBounds);
                GameObject capInstance = InstantiatePrefab(
                    cap.prefabPath,
                    $"dais_pit_cap_{pitCell.x}_{pitCell.y}",
                    parent,
                    pivot,
                    yaw);
                EncapsulateInstance(capInstance, ref bounds, ref hasBounds);
                placed++;
            }

            if (placed > 0)
            {
                stats.stairSummaries.Add($"dais corner pieces: {placed}");
            }
        }

        // Sunken dais corner cells: LOWER in exactly two perpendicular
        // dais-class transitions (rect pits guarantee raised rims and tier
        // rims never match — their lower cells carry at most one dais face).
        // Value = the outward diagonal toward the two rims.
        private static Dictionary<Vector2Int, Vector2Int> FindSunkenDaisCorners(
            IReadOnlyList<TransitionEdge> transitions,
            IReadOnlyDictionary<Vector2Int, int> levels)
        {
            var lowerFaces = new Dictionary<Vector2Int, List<Vector2Int>>();
            foreach (TransitionEdge transition in transitions)
            {
                if (!string.Equals(transition.placementClass, DaisStairPlacementClass, StringComparison.Ordinal) ||
                    !transition.HasLevels ||
                    transition.RiseLevels == 0)
                {
                    continue;
                }

                int firstLevel = transition.firstLevel;
                int secondLevel = transition.secondLevel;

                Vector2Int lowerCell = firstLevel > secondLevel ? transition.secondCell : transition.firstCell;
                Vector2Int upperCell = firstLevel > secondLevel ? transition.firstCell : transition.secondCell;
                if (!lowerFaces.TryGetValue(lowerCell, out List<Vector2Int> faces))
                {
                    faces = new List<Vector2Int>();
                    lowerFaces.Add(lowerCell, faces);
                }

                faces.Add(upperCell - lowerCell);
            }

            var corners = new Dictionary<Vector2Int, Vector2Int>();
            foreach (KeyValuePair<Vector2Int, List<Vector2Int>> entry in lowerFaces)
            {
                if (entry.Value.Count == 2 && entry.Value[0].x * entry.Value[1].x + entry.Value[0].y * entry.Value[1].y == 0)
                {
                    corners.Add(entry.Key, entry.Value[0] + entry.Value[1]);
                }
            }

            return corners;
        }

        // Round tier corners (step 9, decision 36). A convex corner is a floor
        // cell with plain cliff wall edges on two perpendicular faces at equal
        // levels; its kit (shell stack + trim + railing arc + rounded floor)
        // replaces those faces. A concave corner is a LOWER notch cell whose
        // two mass neighbors face it with equal cliff edges and whose diagonal
        // mass cell is upper floor; the concave shell stands on the notch.
        // Yaw offsets are the gallery-calibrated family constants (convex
        // +270, concave +90 over the quadrant base table).
        private readonly struct RoundTierCorner
        {
            public readonly Vector2Int cell;
            public readonly (int x, int z, int direction) edgeA;
            public readonly (int x, int z, int direction) edgeB;
            public readonly int lowerLevel;
            public readonly int higherLevel;
            public readonly bool concave;
            public readonly bool angleStyle;
            public readonly float yaw;
            // Shell only: no floor swap, no sliver, no guard — corners under
            // CURVED stairs round their walls to match the stair above, but
            // the stair owns the cell top.
            public readonly bool wallOnly;

            public RoundTierCorner(
                Vector2Int cell,
                (int x, int z, int direction) edgeA,
                (int x, int z, int direction) edgeB,
                int lowerLevel,
                int higherLevel,
                bool concave,
                bool angleStyle,
                float yaw,
                bool wallOnly = false)
            {
                this.cell = cell;
                this.edgeA = edgeA;
                this.edgeB = edgeB;
                this.lowerLevel = lowerLevel;
                this.higherLevel = higherLevel;
                this.concave = concave;
                this.angleStyle = angleStyle;
                this.yaw = yaw;
                this.wallOnly = wallOnly;
            }
        }

        private static float QuadrantBaseYaw(int sx, int sz)
        {
            return sx < 0 ? (sz < 0 ? 0f : 90f) : (sz < 0 ? 270f : 180f);
        }

        private static List<RoundTierCorner> FindRoundTierCorners(
            List<WallEdge> wallEdges,
            IReadOnlyDictionary<Vector2Int, int> levels,
            HashSet<Vector2Int> reservedCells,
            IReadOnlyDictionary<Vector2Int, TransitionEdge> stairFootprintOwners,
            IReadOnlyDictionary<(int x, int z, int direction), TransitionEdge> stairPortEdgeOwners)
        {
            var corners = new List<RoundTierCorner>();
            var concaveCandidates = new List<RoundTierCorner>();
            var cliffEdges = new Dictionary<(Vector2Int cell, int dx, int dz), WallEdge>();
            foreach (WallEdge edge in wallEdges)
            {
                // Edges whose top guard is already owned by a stair or another
                // structural treatment keep their square wall faces. In particular,
                // an angle/round replacement here would sweep into a stair volume.
                if (edge.isPartition || edge.isRetaining || edge.suppressRailing)
                {
                    continue;
                }

                Vector2 direction = DirectionVector(edge.edge.direction);
                cliffEdges[(new Vector2Int(edge.edge.x, edge.edge.z), Mathf.RoundToInt(direction.x), Mathf.RoundToInt(direction.y))] = edge;
            }

            var claimedEdges = new HashSet<(int, int, int)>();
            var orderedCells = new List<Vector2Int>();
            var seenCells = new HashSet<Vector2Int>();
            foreach (var key in cliffEdges.Keys)
            {
                if (seenCells.Add(key.cell))
                {
                    orderedCells.Add(key.cell);
                }
            }

            orderedCells.Sort((left, right) =>
            {
                int byX = left.x.CompareTo(right.x);
                return byX != 0 ? byX : left.y.CompareTo(right.y);
            });

            // Convex corners first (deterministic order), then concave notches.
            foreach (Vector2Int cell in orderedCells)
            {
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        if (!cliffEdges.TryGetValue((cell, sx, 0), out WallEdge edgeA) ||
                            !cliffEdges.TryGetValue((cell, 0, sz), out WallEdge edgeB) ||
                            edgeA.lowerLevel != edgeB.lowerLevel ||
                            edgeA.higherLevel != edgeB.higherLevel)
                        {
                            continue;
                        }

                        if (reservedCells.Contains(cell))
                        {
                            continue;
                        }

                        var keyA = (edgeA.edge.x, edgeA.edge.z, edgeA.edge.direction);
                        var keyB = (edgeB.edge.x, edgeB.edge.z, edgeB.edge.direction);
                        if (claimedEdges.Contains(keyA) ||
                            claimedEdges.Contains(keyB))
                        {
                            continue;
                        }

                        if (TierCornerBelongsToStair(
                                cell,
                                keyA,
                                keyB,
                                stairFootprintOwners,
                                stairPortEdgeOwners))
                        {
                            continue;
                        }

                        // A diagonally-touching mass at the same level would be
                        // clipped by the sweep — keep that corner square.
                        var diagonal = new Vector2Int(cell.x + sx, cell.y + sz);
                        if (levels.TryGetValue(diagonal, out int diagonalLevel) && diagonalLevel >= edgeA.higherLevel)
                        {
                            continue;
                        }

                        int style = ChooseTierCornerStyle(cell, levels);
                        if (style == TierCornerStyleSquare)
                        {
                            continue;
                        }

                        bool angleStyle = style == TierCornerStyleAngle;
                        float yaw = Mathf.Repeat(QuadrantBaseYaw(sx, sz) + 270f, 360f);
                        corners.Add(new RoundTierCorner(cell, keyA, keyB, edgeA.lowerLevel, edgeA.higherLevel, concave: false, angleStyle, yaw));
                        claimedEdges.Add(keyA);
                        claimedEdges.Add(keyB);
                    }
                }
            }

            foreach (Vector2Int massCell in orderedCells)
            {
                for (int mx = -1; mx <= 1; mx += 2)
                {
                    for (int mz = -1; mz <= 1; mz += 2)
                    {
                        // Notch cell faced by this mass cell's x edge; the
                        // perpendicular mass neighbor must face it too. Cliff
                        // edges only face VOID (floor-facing walls are
                        // "retaining" and excluded above), so the notch is a
                        // void cell and the shell descends to the ground like
                        // the gold scene's exterior concave stacks.
                        var notch = new Vector2Int(massCell.x - mx, massCell.y);
                        var massZ = new Vector2Int(notch.x, notch.y + mz);
                        var massDiagonal = new Vector2Int(notch.x + mx, notch.y + mz);
                        if (!cliffEdges.TryGetValue((massCell, -mx, 0), out WallEdge edgeA) ||
                            !cliffEdges.TryGetValue((massZ, 0, -mz), out WallEdge edgeB) ||
                            edgeA.lowerLevel != edgeB.lowerLevel ||
                            edgeA.higherLevel != edgeB.higherLevel ||
                            levels.ContainsKey(notch) ||
                            !levels.TryGetValue(massDiagonal, out int diagonalLevel) ||
                            diagonalLevel != edgeA.higherLevel ||
                            reservedCells.Contains(notch))
                        {
                            continue;
                        }

                        var keyA = (edgeA.edge.x, edgeA.edge.z, edgeA.edge.direction);
                        var keyB = (edgeB.edge.x, edgeB.edge.z, edgeB.edge.direction);
                        if (claimedEdges.Contains(keyA) ||
                            claimedEdges.Contains(keyB))
                        {
                            continue;
                        }

                        if (TierCornerBelongsToStair(
                                notch,
                                keyA,
                                keyB,
                                stairFootprintOwners,
                                stairPortEdgeOwners))
                        {
                            continue;
                        }

                        int style = ChooseTierCornerStyle(massDiagonal, levels);
                        if (style == TierCornerStyleSquare)
                        {
                            continue;
                        }

                        bool angleStyle = style == TierCornerStyleAngle;
                        float yaw = Mathf.Repeat(QuadrantBaseYaw(mx, mz) + 90f, 360f);
                        concaveCandidates.Add(new RoundTierCorner(notch, keyA, keyB, edgeA.lowerLevel, edgeA.higherLevel, concave: true, angleStyle, yaw));
                    }
                }
            }

            // Two concave sweeps facing across one notch cell read as an
            // awkward lens (user review 2026-06-12) — keep at most one per
            // notch, EXCEPT a full four-way ring, which closes into a perfect
            // circular well and stays.
            var concaveByNotch = new Dictionary<Vector2Int, List<RoundTierCorner>>();
            foreach (RoundTierCorner candidate in concaveCandidates)
            {
                if (!concaveByNotch.TryGetValue(candidate.cell, out List<RoundTierCorner> group))
                {
                    group = new List<RoundTierCorner>();
                    concaveByNotch.Add(candidate.cell, group);
                }

                group.Add(candidate);
            }

            // Per notch, atomically: a full four-way ring with ALL edges free
            // closes into a perfect circular well and places whole; anything
            // less places AT MOST ONE sweep (facing pairs read as an oval —
            // user rule: 1-cell holes are squares or perfect circles, never
            // ovals; a degraded well must not leave two facing arcs).
            var processedNotches = new HashSet<Vector2Int>();
            foreach (RoundTierCorner candidate in concaveCandidates)
            {
                if (!processedNotches.Add(candidate.cell))
                {
                    continue;
                }

                List<RoundTierCorner> group = concaveByNotch[candidate.cell];
                bool fullRing = group.Count == 4;
                if (fullRing)
                {
                    foreach (RoundTierCorner member in group)
                    {
                        if (claimedEdges.Contains(member.edgeA) ||
                            claimedEdges.Contains(member.edgeB) ||
                            member.angleStyle != group[0].angleStyle)
                        {
                            fullRing = false;
                            break;
                        }
                    }
                }

                if (fullRing)
                {
                    foreach (RoundTierCorner member in group)
                    {
                        corners.Add(member);
                        claimedEdges.Add(member.edgeA);
                        claimedEdges.Add(member.edgeB);
                    }

                    continue;
                }

                foreach (RoundTierCorner member in group)
                {
                    if (claimedEdges.Contains(member.edgeA) ||
                        claimedEdges.Contains(member.edgeB))
                    {
                        continue;
                    }

                    corners.Add(member);
                    claimedEdges.Add(member.edgeA);
                    claimedEdges.Add(member.edgeB);
                    break;
                }
            }

            return corners;
        }

        private static void ValidateGatewayCornerPlanDisjoint(
            GatewaySocketPlan gatewaySocketPlan,
            IReadOnlyList<RoundTierCorner> roundTierCorners)
        {
            if (gatewaySocketPlan == null ||
                gatewaySocketPlan.sockets.Count == 0 ||
                roundTierCorners == null ||
                roundTierCorners.Count == 0)
            {
                return;
            }

            var cornerEdges =
                new HashSet<(int x, int z, int direction)>();
            foreach (RoundTierCorner corner in roundTierCorners)
            {
                cornerEdges.Add(corner.edgeA);
                cornerEdges.Add(corner.edgeB);
            }

            // A corner edge may be neither the opening NOR a flank. The opening
            // would steal a face from the frozen corner plan; a flank would be a
            // jamb that does not exist, because the chamfer spans a diagonal
            // between the far endpoints of the two faces it deleted rather than
            // standing on either of them. This is the guard that catches a
            // gateway about to be planted free-standing on open floor.
            foreach (GatewaySocket socket in gatewaySocketPlan.sockets)
            {
                // Neither the opening NOR a flank may be a corner-owned edge.
                // The opening would steal a face from the frozen corner plan; a
                // flank would be a jamb that does not exist, because the chamfer
                // spans a diagonal between the far endpoints of the two faces it
                // deleted rather than standing on either of them. This is the
                // guard that catches a gateway about to be planted on open floor
                // — it was weakened once, on 2026-07-26, and three free-standing
                // arches shipped before the next regen caught them.
                var openingEdge = (
                    socket.edge.x,
                    socket.edge.z,
                    socket.edge.direction);
                if (cornerEdges.Contains(openingEdge) ||
                    cornerEdges.Contains(socket.firstFlankEdge) ||
                    cornerEdges.Contains(socket.secondFlankEdge))
                {
                    throw new InvalidOperationException(
                        $"Gateway socket {socket.edgeKey} uses an edge owned by the frozen corner plan.");
                }
            }
        }

        // Everything a stair owns that a rounded tier corner would sweep into:
        // its footprint cells, and the floor-side edge of each landing.
        private static void BuildTierCornerStairClaims(
            IReadOnlyList<TransitionEdge> transitions,
            out Dictionary<Vector2Int, TransitionEdge> footprintOwners,
            out Dictionary<(int x, int z, int direction), TransitionEdge> portEdgeOwners)
        {
            footprintOwners = new Dictionary<Vector2Int, TransitionEdge>();
            portEdgeOwners = new Dictionary<(int x, int z, int direction), TransitionEdge>();
            if (transitions == null)
            {
                return;
            }

            foreach (TransitionEdge transition in transitions)
            {
                if (transition.footprintCells != null)
                {
                    foreach (Vector2Int cell in transition.footprintCells)
                    {
                        if (!footprintOwners.ContainsKey(cell))
                        {
                            footprintOwners.Add(cell, transition);
                        }
                    }
                }

                if (!transition.hasPortDirections)
                {
                    continue;
                }

                AddTierCornerPortClaims(
                    transition.lowerLandingCells,
                    OppositeDirection(transition.lowerPortDirection),
                    transition,
                    portEdgeOwners);
                AddTierCornerPortClaims(
                    transition.upperLandingCells,
                    OppositeDirection(transition.upperPortDirection),
                    transition,
                    portEdgeOwners);
            }
        }

        // The three conditions ValidateTierCornerCompatibility throws on, asked
        // BEFORE the corner is taken. Keeping it square is a style decision the
        // selector already makes for reserved cells and for a diagonally
        // touching mass; this is the same decision for the same reason.
        private static bool TierCornerBelongsToStair(
            Vector2Int cell,
            (int x, int z, int direction) edgeA,
            (int x, int z, int direction) edgeB,
            IReadOnlyDictionary<Vector2Int, TransitionEdge> footprintOwners,
            IReadOnlyDictionary<(int x, int z, int direction), TransitionEdge> portEdgeOwners)
        {
            return footprintOwners.ContainsKey(cell) ||
                footprintOwners.ContainsKey(new Vector2Int(edgeA.x, edgeA.z)) ||
                footprintOwners.ContainsKey(new Vector2Int(edgeB.x, edgeB.z)) ||
                portEdgeOwners.ContainsKey(edgeA) ||
                portEdgeOwners.ContainsKey(edgeB);
        }

        private static void ValidateTierCornerCompatibility(
            IReadOnlyList<RoundTierCorner> corners,
            IReadOnlyDictionary<Vector2Int, TransitionEdge> footprintOwners,
            IReadOnlyDictionary<(int x, int z, int direction), TransitionEdge> portEdgeOwners)
        {
            if (corners == null || corners.Count == 0)
            {
                return;
            }

            foreach (RoundTierCorner corner in corners)
            {
                if (footprintOwners.TryGetValue(corner.cell, out TransitionEdge footprintOwner))
                {
                    ThrowTierCornerCompatibilityError(corner, footprintOwner, "corner cell overlaps the stair footprint");
                }

                Vector2Int edgeAOwnerCell = new Vector2Int(corner.edgeA.Item1, corner.edgeA.Item2);
                Vector2Int edgeBOwnerCell = new Vector2Int(corner.edgeB.Item1, corner.edgeB.Item2);
                if (footprintOwners.TryGetValue(edgeAOwnerCell, out TransitionEdge edgeOwner) ||
                    footprintOwners.TryGetValue(edgeBOwnerCell, out edgeOwner))
                {
                    ThrowTierCornerCompatibilityError(corner, edgeOwner, "corner replaces an edge owned by the stair footprint");
                }

                if (portEdgeOwners.TryGetValue(corner.edgeA, out TransitionEdge portOwnerA) ||
                    portEdgeOwners.TryGetValue(corner.edgeB, out portOwnerA))
                {
                    ThrowTierCornerCompatibilityError(corner, portOwnerA, "corner replaces a stair landing port edge");
                }
            }
        }

        private static void AddTierCornerPortClaims(
            IReadOnlyList<Vector2Int> landingCells,
            int floorSide,
            TransitionEdge transition,
            Dictionary<(int x, int z, int direction), TransitionEdge> owners)
        {
            if (landingCells == null)
            {
                return;
            }

            foreach (Vector2Int cell in landingCells)
            {
                var key = (cell.x, cell.y, floorSide);
                if (!owners.ContainsKey(key))
                {
                    owners.Add(key, transition);
                }
            }
        }

        private static void ThrowTierCornerCompatibilityError(
            RoundTierCorner corner,
            TransitionEdge transition,
            string reason)
        {
            string stairName = transition.synthesizedSetPiece != null
                ? transition.synthesizedSetPiece.name
                : string.IsNullOrEmpty(transition.stairPrefabPath)
                    ? "<unnamed>"
                    : Path.GetFileNameWithoutExtension(transition.stairPrefabPath);
            throw new InvalidOperationException(
                $"[STAIR_BOUNDARY_CONFLICT] stair '{stairName}' placementClass '{transition.placementClass}' " +
                $"at tier corner {corner.cell}: {reason}; edges {FormatTierCornerEdges(corner)}.");
        }

        private static string FormatTierCornerEdges(RoundTierCorner corner)
        {
            return $"({corner.edgeA.Item1},{corner.edgeA.Item2},d{corner.edgeA.Item3})/" +
                $"({corner.edgeB.Item1},{corner.edgeB.Item2},d{corner.edgeB.Item3})";
        }

        // Decision 42 at tier scale: one corner style per contiguous same-level
        // floor region (the tier mass), anchored like the dais clusters.
        // Weighted three ways (user review 2026-06-12: unweighted angle/round
        // made every silhouette read round): most masses keep their square
        // corners; the rest split between chamfer and curve.
        private const int TierCornerSquarePercent = 50;
        private const int TierCornerAnglePercent = 25;
        private const int TierCornerStyleSquare = 0;
        private const int TierCornerStyleAngle = 1;
        private const int TierCornerStyleRound = 2;

        private static int ChooseTierCornerStyle(Vector2Int seedCell, IReadOnlyDictionary<Vector2Int, int> levels)
        {
            if (!levels.TryGetValue(seedCell, out int level))
            {
                return StyleFromHash(StairForge.StableHash($"tierstyle:{seedCell.x}:{seedCell.y}"));
            }

            var anchor = seedCell;
            var pending = new Stack<Vector2Int>();
            var seen = new HashSet<Vector2Int> { seedCell };
            pending.Push(seedCell);
            while (pending.Count > 0 && seen.Count <= 512)
            {
                Vector2Int cell = pending.Pop();
                if (cell.x < anchor.x || (cell.x == anchor.x && cell.y < anchor.y))
                {
                    anchor = cell;
                }

                foreach (Vector2Int neighbor in new[]
                         {
                             new Vector2Int(cell.x + 1, cell.y),
                             new Vector2Int(cell.x - 1, cell.y),
                             new Vector2Int(cell.x, cell.y + 1),
                             new Vector2Int(cell.x, cell.y - 1)
                         })
                {
                    if (levels.TryGetValue(neighbor, out int neighborLevel) &&
                        neighborLevel == level &&
                        seen.Add(neighbor))
                    {
                        pending.Push(neighbor);
                    }
                }
            }

            return StyleFromHash(StairForge.StableHash($"tierstyle:{anchor.x}:{anchor.y}"));
        }

        private static int StyleFromHash(int hash)
        {
            int roll = (hash & int.MaxValue) % 100;
            if (roll < TierCornerSquarePercent)
            {
                return TierCornerStyleSquare;
            }

            return roll < TierCornerSquarePercent + TierCornerAnglePercent ? TierCornerStyleAngle : TierCornerStyleRound;
        }

        private static void PlaceRoundTierCornerKits(
            IReadOnlyList<RoundTierCorner> corners,
            HashSet<(int x, int z, int direction)> shellGuardEdges,
            Vector3 origin,
            float levelHeight,
            Transform parent,
            ref Bounds bounds,
            ref bool hasBounds,
            TieredPlatformBuildStats stats)
        {
            int placed = 0;
            foreach (RoundTierCorner corner in corners)
            {
                string family = corner.angleStyle ? "angle" : corner.concave ? "concave" : "convex";
                if (!TryLoadTierStepPiece($"P_MOD_Wall_01_O_{family}_small", out TierStepPiece small) ||
                    !TryLoadTierStepPiece($"P_MOD_Wall_01_O_{family}_med", out TierStepPiece med) ||
                    !TryLoadTierStepPiece($"P_MOD_Wall_01_O_{family}_large", out TierStepPiece large))
                {
                    return;
                }

                Vector3 pivot = DaisFullCellPivotWorld(corner.cell, corner.yaw, origin);
                float top = corner.higherLevel * levelHeight;
                float courseTop = top;
                float remaining = top - corner.lowerLevel * levelHeight;
                var courses = new[] { large, med, small };
                int index = 0;
                while (remaining > 0.01f && index <= 12)
                {
                    TierStepPiece course = small;
                    foreach (TierStepPiece candidate in courses)
                    {
                        if (candidate.boundsMax.y - candidate.boundsMin.y <= remaining + 1.01f)
                        {
                            course = candidate;
                            break;
                        }
                    }

                    float courseHeight = course.boundsMax.y - course.boundsMin.y;
                    GameObject shell = InstantiatePrefab(
                        course.prefabPath,
                        $"tier_corner_{corner.cell.x}_{corner.cell.y}_c{index}",
                        parent,
                        pivot + Vector3.up * (courseTop - courseHeight),
                        corner.yaw);
                    EncapsulateInstance(shell, ref bounds, ref hasBounds);
                    courseTop -= courseHeight;
                    remaining -= courseHeight;
                    index++;
                }

                if (corner.concave && !corner.wallOnly)
                {
                    // The sliver of mass top between the square floor edges and
                    // the curve gets the matching floor (bite facing the
                    // notch): the CURVE is now the walkable edge.
                    string sliverName = corner.angleStyle ? "P_MOD_Floor_01_O_angle_med" : "P_MOD_Floor_01_O_concave_med";
                    if (TryLoadTierStepPiece(sliverName, out TierStepPiece sliverFloor))
                    {
                        GameObject sliver = InstantiatePrefab(
                            sliverFloor.prefabPath,
                            $"tier_corner_floor_{corner.cell.x}_{corner.cell.y}",
                            parent,
                            pivot + Vector3.up * top,
                            corner.yaw);
                        EncapsulateInstance(sliver, ref bounds, ref hasBounds);
                    }
                }

                // Trim curb + railing arc along the curve, per the ledge policy
                // (1u drops carry no guard). The guard families author their
                // arcs OPPOSITE the wall families: at a concave corner the
                // co-located guard bows convex (user review 2026-06-12), so
                // concave guards rotate 180 with the pivot recomputed through
                // the quadrant map — the arc lands back on the curve. Convex
                // corners stay co-located (approved).
                bool shellOwnsGuard = shellGuardEdges != null &&
                    (shellGuardEdges.Contains(corner.edgeA) || shellGuardEdges.Contains(corner.edgeB));
                if (!corner.wallOnly &&
                    !shellOwnsGuard &&
                    DropGetsRailing(corner.higherLevel - corner.lowerLevel))
                {
                    // The TRIM families author like the WALL families (concave
                    // trim co-locates with the concave shell); only the RAILING
                    // is a repurposed convex arc, so it alone takes the 180
                    // flip with the quadrant-pivot remap (user reviews
                    // 2026-06-12).
                    float guardYaw = corner.concave ? Mathf.Repeat(corner.yaw + 180f, 360f) : corner.yaw;
                    Vector3 guardPivot = corner.concave ? DaisFullCellPivotWorld(corner.cell, guardYaw, origin) : pivot;
                    string trimName = corner.angleStyle
                        ? "P_MOD_WallTrim_01_O_angle"
                        : corner.concave ? "P_MOD_WallTrim_01_O_concave" : "P_MOD_WallTrim_01_O_convex";
                    if (TryLoadTierStepPiece(trimName, out TierStepPiece trim))
                    {
                        GameObject trimInstance = InstantiatePrefab(
                            trim.prefabPath,
                            $"tier_corner_trim_{corner.cell.x}_{corner.cell.y}",
                            parent,
                            pivot + Vector3.up * top,
                            corner.yaw);
                        EncapsulateInstance(trimInstance, ref bounds, ref hasBounds);
                    }

                    if (TryLoadTierStepPiece(corner.angleStyle ? "P_MOD_Railing_01_angle" : "P_MOD_Railing_01_convex", out TierStepPiece rail))
                    {
                        GameObject railInstance = InstantiatePrefab(
                            rail.prefabPath,
                            $"tier_corner_railing_{corner.cell.x}_{corner.cell.y}",
                            parent,
                            guardPivot + Vector3.up * top,
                            guardYaw);
                        EncapsulateInstance(railInstance, ref bounds, ref hasBounds);
                    }
                }

                placed++;
            }

            if (placed > 0)
            {
                stats.stairSummaries.Add($"round tier corners: {placed}");
            }
        }

        // Pivot position that makes a (-x,+z)-footprint full-cell piece cover
        // the given cell at the given cardinal yaw (the forge convention).
        private static Vector3 DaisFullCellPivotWorld(Vector2Int cell, float yaw, Vector3 origin)
        {
            float minX = origin.x + cell.x * CellSize;
            float minZ = origin.z + cell.y * CellSize;
            int quarter = Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 90f) & 3;
            switch (quarter)
            {
                case 0: return new Vector3(minX + CellSize, origin.y, minZ);
                case 1: return new Vector3(minX, origin.y, minZ);
                case 2: return new Vector3(minX, origin.y, minZ + CellSize);
                default: return new Vector3(minX + CellSize, origin.y, minZ + CellSize);
            }
        }

        // Decision 42: one corner style per dais. The cluster anchor is the
        // lexicographically smallest cell of the connected dais region, hashed
        // to a stable angle/round choice.
        private static int ChooseDaisCornerStyle(Vector2Int daisCell, HashSet<Vector2Int> daisCells)
        {
            var anchor = daisCell;
            var pending = new Stack<Vector2Int>();
            var seen = new HashSet<Vector2Int> { daisCell };
            pending.Push(daisCell);
            while (pending.Count > 0)
            {
                Vector2Int cell = pending.Pop();
                if (cell.x < anchor.x || (cell.x == anchor.x && cell.y < anchor.y))
                {
                    anchor = cell;
                }

                foreach (Vector2Int neighbor in new[]
                         {
                             new Vector2Int(cell.x + 1, cell.y),
                             new Vector2Int(cell.x - 1, cell.y),
                             new Vector2Int(cell.x, cell.y + 1),
                             new Vector2Int(cell.x, cell.y - 1)
                         })
                {
                    if (daisCells.Contains(neighbor) && seen.Add(neighbor))
                    {
                        pending.Push(neighbor);
                    }
                }
            }

            return StairForge.StableHash($"daisstyle:{anchor.x}:{anchor.y}") & 1;
        }

        private sealed class TierStepPiece
        {
            public readonly string prefabPath;
            public readonly Vector3 boundsMin;
            public readonly Vector3 boundsMax;

            public TierStepPiece(string prefabPath, Vector3 boundsMin, Vector3 boundsMax)
            {
                this.prefabPath = prefabPath;
                this.boundsMin = boundsMin;
                this.boundsMax = boundsMax;
            }
        }

        private static Dictionary<string, TierStepPiece> tierStepPieceCache;
        private static DateTime tierStepPieceCacheLibraryWriteTimeUtc = DateTime.MinValue;
        private static bool warnedMissingTierStepPieces;

        private static bool TryLoadTierStepPiece(string pieceName, out TierStepPiece piece)
        {
            piece = null;
            if (!File.Exists(StepPieceLibraryPath))
            {
                return false;
            }

            DateTime libraryWriteTimeUtc = File.GetLastWriteTimeUtc(StepPieceLibraryPath);
            if (tierStepPieceCache == null || libraryWriteTimeUtc != tierStepPieceCacheLibraryWriteTimeUtc)
            {
                tierStepPieceCacheLibraryWriteTimeUtc = libraryWriteTimeUtc;
                tierStepPieceCache = new Dictionary<string, TierStepPiece>(StringComparer.Ordinal);
                JObject root = JObject.Parse(File.ReadAllText(StepPieceLibraryPath));
                if (root["pieces"] is JArray pieces)
                {
                    foreach (JToken candidate in pieces)
                    {
                        string category = candidate.Value<string>("category");
                        if ((!string.Equals(category, "tierStepEdge", StringComparison.Ordinal) &&
                             !string.Equals(category, "floorRound", StringComparison.Ordinal) &&
                             !string.Equals(category, "wall", StringComparison.Ordinal) &&
                             !string.Equals(category, "wallTrim", StringComparison.Ordinal) &&
                             !string.Equals(category, "railing", StringComparison.Ordinal)) ||
                            !string.Equals(candidate.Value<string>("confidence"), "high", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string name = candidate.Value<string>("name") ?? string.Empty;
                        tierStepPieceCache[name] = new TierStepPiece(
                            candidate.Value<string>("path"),
                            ParseVector3(candidate["boundsMin"]),
                            ParseVector3(candidate["boundsMax"]));
                    }
                }
            }

            if (!tierStepPieceCache.TryGetValue(pieceName, out piece))
            {
                if (!warnedMissingTierStepPieces)
                {
                    warnedMissingTierStepPieces = true;
                    Debug.LogWarning(
                        $"Dungeon Lab Elevation Edge Model: tier step piece '{pieceName}' is not measured (dais corners stay bare). " +
                        "Re-run Tools > Dungeon Lab > Measure Step Piece Library.");
                }

                return false;
            }

            return true;
        }

        private static void PlaceSeamStepStrip(
            string prefabPath,
            Vector2Int raisedCell,
            Vector2Int lowerCell,
            int raisedLevel,
            int riseLevels,
            Vector3 origin,
            float levelHeight,
            Transform parent,
            ref Bounds bounds,
            ref bool hasBounds,
            TieredPlatformBuildStats stats)
        {
            SeamStripPiece piece = LoadSeamStripPiece(prefabPath, levelHeight, riseLevels);
            PlatformEdge sharedEdge = EdgeFromCellToward(raisedCell, lowerCell);
            Vector3 exitTarget = EdgeCenter(origin, sharedEdge, raisedLevel * levelHeight);
            // sharedEdge points from the raised cell toward the lower one; the strip
            // climbs the other way.
            Vector2 worldClimb = -DirectionVector(sharedEdge.direction);
            float yRotation = CalculateYawToMap(piece.localClimbDirection, worldClimb);
            Quaternion rotation = Quaternion.Euler(0f, yRotation, 0f);
            Vector3 position = exitTarget - rotation * piece.localExitEdgeCenter;
            GameObject instance = InstantiatePrefab(
                prefabPath,
                $"seam_step_{raisedCell.x}_{raisedCell.y}_to_{lowerCell.x}_{lowerCell.y}",
                parent,
                position,
                yRotation);
            EncapsulateInstance(instance, ref bounds, ref hasBounds);
            if (!string.IsNullOrEmpty(piece.sideCoverPrefabPath))
            {
                // Cover shells share the flight's pivot, so the strip's transform is
                // the cover's transform (deferred issue: open strip sides).
                GameObject cover = InstantiatePrefab(
                    piece.sideCoverPrefabPath,
                    $"seam_step_cover_{raisedCell.x}_{raisedCell.y}_to_{lowerCell.x}_{lowerCell.y}",
                    parent,
                    position,
                    yRotation);
                EncapsulateInstance(cover, ref bounds, ref hasBounds);
            }

            stats.stairSummaries.Add($"seam strip rise 1 placed {raisedCell} -> {lowerCell}");
        }

        // Ledge policy (design decision 4, re-refined 2026-06-11 after the first
        // odd-level visuals): a 1u drop gets no GUARD — no railing, no parapet —
        // but its face below the edge is still closed like every drop face (a
        // sunken top-anchored wall course; bare reveals read as holes against the
        // pack's skirt-less one-sided floors). Drops of 2u or more get a guard
        // that may be a railing or a wall, consistent within a room. Railing
        // piece LENGTH follows the edge length, not the drop: the half-length
        // piece is reserved for sub-cell edges.
        // Decision 43(c) helpers. A guard side is (roomId, face direction);
        // a side is 1u-crossed when two of its cliff edges sit at adjacent
        // positions with upper floors one level apart — the point a railing
        // line cannot traverse (measured: the pack has no half-length angled
        // railing and no sloped 1u railing transition piece).
        private static bool TryGetRoomSide(
            RoomBoundaryContext roomBoundaryContext,
            WallEdge wallEdge,
            out (int roomId, int direction) side)
        {
            side = default;
            if (wallEdge.isPartition ||
                wallEdge.isRetaining ||
                roomBoundaryContext?.cellRoomIds == null ||
                !roomBoundaryContext.cellRoomIds.TryGetValue(new Vector2Int(wallEdge.edge.x, wallEdge.edge.z), out int roomId))
            {
                return false;
            }

            side = (roomId, wallEdge.edge.direction);
            return true;
        }

        private static HashSet<(int roomId, int direction)> FindWallGuardSides(
            List<WallEdge> wallEdges,
            RoomBoundaryContext roomBoundaryContext)
        {
            var sideEdges = new Dictionary<(int roomId, int direction), List<WallEdge>>();
            foreach (WallEdge wallEdge in wallEdges)
            {
                if (!TryGetRoomSide(roomBoundaryContext, wallEdge, out (int roomId, int direction) side))
                {
                    continue;
                }

                if (!sideEdges.TryGetValue(side, out List<WallEdge> edges))
                {
                    edges = new List<WallEdge>();
                    sideEdges.Add(side, edges);
                }

                edges.Add(wallEdge);
            }

            var guarded = new HashSet<(int roomId, int direction)>();
            foreach (KeyValuePair<(int roomId, int direction), List<WallEdge>> entry in sideEdges)
            {
                List<WallEdge> edges = entry.Value;
                for (int i = 0; i < edges.Count && !guarded.Contains(entry.Key); i++)
                {
                    for (int j = i + 1; j < edges.Count; j++)
                    {
                        int manhattan = Mathf.Abs(edges[i].edge.x - edges[j].edge.x) +
                            Mathf.Abs(edges[i].edge.z - edges[j].edge.z);
                        if (manhattan == 1 &&
                            Mathf.Abs(edges[i].higherLevel - edges[j].higherLevel) == 1)
                        {
                            guarded.Add(entry.Key);
                            break;
                        }
                    }
                }
            }

            return guarded;
        }

        // The parapet: a small double-sided E wall standing on the upper
        // floor, centered on the edge line like the railing it replaces.
        // Piece choice is interim (user 2026-06-13): revisit when dungeons
        // grow wall varieties.
        private const string ParapetWallPieceName = "P_MOD_Wall_01_E_straight_small";
        private static bool warnedMissingParapetWall;

        private static void PlaceParapetEdge(
            Transform parent,
            PlatformEdge edge,
            Vector3 origin,
            float floorTopHeight,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            if (!TryLoadTierStepPiece(ParapetWallPieceName, out TierStepPiece piece))
            {
                if (!warnedMissingParapetWall)
                {
                    warnedMissingParapetWall = true;
                    Debug.LogWarning(
                        $"Dungeon Lab Elevation Edge Model: parapet wall '{ParapetWallPieceName}' is not in the measured library; " +
                        "1u-crossed guard sides stay bare. Re-run Tools > Dungeon Lab > Measure Step Piece Library.");
                }

                return;
            }

            Vector3 edgeCenter = EdgeCenter(origin, edge, floorTopHeight);
            Vector3 size = piece.boundsMax - piece.boundsMin;
            bool longAxisIsX = size.x >= size.z;
            Vector2 outward = DirectionVector(edge.direction);
            bool edgeLineIsX = Mathf.Abs(outward.x) < 0.5f;
            float yaw = longAxisIsX == edgeLineIsX ? 0f : 90f;
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 rotatedCenter = rotation * new Vector3(
                (piece.boundsMin.x + piece.boundsMax.x) * 0.5f,
                0f,
                (piece.boundsMin.z + piece.boundsMax.z) * 0.5f);
            Vector3 position = new Vector3(
                edgeCenter.x - rotatedCenter.x,
                floorTopHeight - piece.boundsMin.y,
                edgeCenter.z - rotatedCenter.z);
            GameObject instance = InstantiatePrefab(
                piece.prefabPath,
                $"parapet_{DirectionName(edge.direction).ToLowerInvariant()}_{edge.x}_{edge.z}",
                parent,
                position,
                yaw);
            EncapsulateInstance(instance, ref bounds, ref hasBounds);
        }

        private static bool DropGetsRailing(int dropLevels)
        {
            return dropLevels != 1;
        }

        private static void PlaceRailingEdge(
            MeasuredPrefab floor,
            RailingContracts railings,
            Transform parent,
            PlatformEdge edge,
            Vector3 origin,
            float levelHeight,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            Vector3 cellMin = CellMin(origin, edge.x, edge.z, levelHeight);
            Vector3 floorPosition = FloorPivotForTopSurface(floor, cellMin);
            float yRotation = CalculateYawToMap(DirectionVector(railings.authored.baseSide), DirectionVector(edge.direction));
            Quaternion deltaRotation = Quaternion.Euler(0f, yRotation, 0f);
            Quaternion rotation = deltaRotation * railings.authored.railing.rotation;
            Vector2 floorLocalCenter = floor.localPlanBounds.Center;
            Vector3 pivotToCenter = new Vector3(floorLocalCenter.x, 0f, floorLocalCenter.y);
            Vector3 cellCenter = floorPosition + pivotToCenter;
            Vector3 offsetFromCenter = railings.authored.railing.position - pivotToCenter;
            Vector3 position = cellCenter + deltaRotation * offsetFromCenter;
            GameObject instance = InstantiatePrefab(
                railings.railing.prefabPath,
                $"railing_{DirectionName(edge.direction).ToLowerInvariant()}_{edge.x}_{edge.z}",
                parent,
                position,
                rotation.eulerAngles.y);
            EncapsulateInstance(instance, ref bounds, ref hasBounds);
            PlaceRailingCoverTrim(parent, edge, origin, levelHeight, ref bounds, ref hasBounds);
        }

        // New rule (user review 2026-06-11): a ledge railing is "covered" — the
        // pack's wall-top trim curb runs under it so the open band beneath the
        // bottom rail is backed by stone. The COMP walls ship this trim built in,
        // which is why partitions read finished while bare railings did not.
        // Measured from the wallTrim family; warn-once and stay bare until the
        // metrology tool ingests it.
        private static void PlaceRailingCoverTrim(
            Transform parent,
            PlatformEdge edge,
            Vector3 origin,
            float floorTopHeight,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            RailingTrimPiece trim = LoadRailingTrimPiece();
            if (trim == null)
            {
                return;
            }

            Vector3 edgeCenter = EdgeCenter(origin, edge, floorTopHeight);
            // The trim's measured LONG plan axis runs along the edge line
            // (perpendicular to the edge's outward direction); thin profile
            // centered on the edge, base on the floor plane so the curb rises
            // under the railing.
            Vector3 size = trim.boundsMax - trim.boundsMin;
            bool longAxisIsX = size.x >= size.z;
            Vector2 outward = DirectionVector(edge.direction);
            bool edgeLineIsX = Mathf.Abs(outward.x) < 0.5f;
            float yaw = longAxisIsX == edgeLineIsX ? 0f : 90f;
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 rotatedCenter = rotation * new Vector3(
                (trim.boundsMin.x + trim.boundsMax.x) * 0.5f,
                0f,
                (trim.boundsMin.z + trim.boundsMax.z) * 0.5f);
            Vector3 position = new Vector3(
                edgeCenter.x - rotatedCenter.x,
                floorTopHeight - trim.boundsMin.y,
                edgeCenter.z - rotatedCenter.z);
            GameObject instance = InstantiatePrefab(
                trim.prefabPath,
                $"railing_trim_{DirectionName(edge.direction).ToLowerInvariant()}_{edge.x}_{edge.z}",
                parent,
                position,
                yaw);
            EncapsulateInstance(instance, ref bounds, ref hasBounds);
        }

        private sealed class RailingTrimPiece
        {
            public readonly string prefabPath;
            public readonly Vector3 boundsMin;
            public readonly Vector3 boundsMax;

            public RailingTrimPiece(string prefabPath, Vector3 boundsMin, Vector3 boundsMax)
            {
                this.prefabPath = prefabPath;
                this.boundsMin = boundsMin;
                this.boundsMax = boundsMax;
            }
        }

        private static RailingTrimPiece railingTrimCache;
        private static bool railingTrimCacheResolved;
        private static DateTime railingTrimCacheLibraryWriteTimeUtc = DateTime.MinValue;
        private static bool warnedMissingRailingTrim;

        // Selection is by measurement within the wallTrim category: a full-cell
        // (4u) straight curb — longest plan axis ~4u, thin profile, low height.
        // Cache invalidates on the library file's write time (the metrology tool
        // re-measures without a domain reload).
        private static RailingTrimPiece LoadRailingTrimPiece()
        {
            if (!File.Exists(StepPieceLibraryPath))
            {
                return null;
            }

            DateTime libraryWriteTimeUtc = File.GetLastWriteTimeUtc(StepPieceLibraryPath);
            if (railingTrimCacheResolved && libraryWriteTimeUtc == railingTrimCacheLibraryWriteTimeUtc)
            {
                return railingTrimCache;
            }

            railingTrimCache = null;
            railingTrimCacheResolved = true;
            railingTrimCacheLibraryWriteTimeUtc = libraryWriteTimeUtc;

            JObject root = JObject.Parse(File.ReadAllText(StepPieceLibraryPath));
            float bestScore = float.MaxValue;
            if (root["pieces"] is JArray pieces)
            {
                foreach (JToken piece in pieces)
                {
                    if (!string.Equals(piece.Value<string>("category"), "wallTrim", StringComparison.Ordinal) ||
                        !string.Equals(piece.Value<string>("confidence"), "high", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Vector3 boundsMin = ParseVector3(piece["boundsMin"]);
                    Vector3 boundsMax = ParseVector3(piece["boundsMax"]);
                    Vector3 size = boundsMax - boundsMin;
                    float length = Mathf.Max(size.x, size.z);
                    float thickness = Mathf.Min(size.x, size.z);
                    if (Mathf.Abs(length - CellSize) > 0.3f || thickness > 1.2f || size.y > 1.5f)
                    {
                        continue;
                    }

                    float score = Mathf.Abs(length - CellSize) + thickness;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        railingTrimCache = new RailingTrimPiece(piece.Value<string>("path"), boundsMin, boundsMax);
                    }
                }
            }

            if (railingTrimCache == null && !warnedMissingRailingTrim)
            {
                warnedMissingRailingTrim = true;
                Debug.LogWarning(
                    "Dungeon Lab Elevation Edge Model: no measured wallTrim curb for railings (they render uncovered). " +
                    "Re-run Tools > Dungeon Lab > Measure Step Piece Library to ingest the P_MOD_WallTrim_01 family.");
            }

            return railingTrimCache;
        }

        private static void PlaceRailingCornerColumns(
            RailingContracts railings,
            Transform parent,
            List<PlatformEdge> cliffEdges,
            HashSet<Vector2Int> reservedCells,
            Vector3 origin,
            float levelHeight,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            var counts = new Dictionary<Vector2Int, int>();
            foreach (PlatformEdge edge in cliffEdges)
            {
                GetEdgeVertices(edge, out Vector2Int first, out Vector2Int second);
                Increment(counts, first);
                Increment(counts, second);
            }

            foreach (var item in counts)
            {
                if (VertexTouchesReservedSetPiece(item.Key, reservedCells))
                {
                    continue;
                }

                if (item.Value < 2)
                {
                    continue;
                }

                if (!TryGetRailingCornerEdgeForVertex(cliffEdges, item.Key, out PlatformEdge edge, out bool vertexIsStart))
                {
                    continue;
                }

                Vector3 vertexPosition = origin + new Vector3(item.Key.x * CellSize, levelHeight, item.Key.y * CellSize);
                float yRotation = CalculateYawToMap(DirectionVector(railings.authored.baseSide), DirectionVector(edge.direction));
                Quaternion deltaRotation = Quaternion.Euler(0f, yRotation, 0f);
                Vector3 endpointOffset = vertexIsStart ? railings.authored.startColumnOffset : railings.authored.endColumnOffset;
                Quaternion authoredRotation = vertexIsStart ? railings.authored.startColumn.rotation : railings.authored.endColumn.rotation;
                Vector3 position = vertexPosition + deltaRotation * endpointOffset;
                Quaternion rotation = deltaRotation * authoredRotation;
                GameObject instance = InstantiatePrefab(
                    railings.column.prefabPath,
                    $"railing_column_{item.Key.x}_{item.Key.y}",
                    parent,
                    position,
                    rotation.eulerAngles.y);
                EncapsulateInstance(instance, ref bounds, ref hasBounds);
            }
        }


        private static void ValidateAuthoredWallCoverAndTrimIntact(GameObject stairInstance)
        {
            int coverCount = 0;
            int trimCount = 0;
            foreach (Transform child in stairInstance.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.Contains("WallCover", StringComparison.Ordinal))
                {
                    coverCount++;
                    if (!child.gameObject.activeSelf)
                    {
                        throw new InvalidOperationException("Decorated stair WallCover was inactive; WallCover must stay authored and must not be toggled.");
                    }
                }

                if (child.name.Contains("WallTrim", StringComparison.Ordinal))
                {
                    trimCount++;
                    if (!child.gameObject.activeSelf)
                    {
                        throw new InvalidOperationException("Decorated stair WallTrim was inactive; WallTrim must stay authored and must not be toggled.");
                    }
                }
            }

            if (coverCount <= 0)
            {
                throw new InvalidOperationException("Decorated stair validation failed: no authored WallCover child was found.");
            }

            if (trimCount <= 0)
            {
                throw new InvalidOperationException("Decorated stair validation failed: no authored WallTrim child was found.");
            }
        }

        private static MeasuredPrefab MeasurePrefab(string prefabPath, PrefabRole role)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException($"Missing prefab at '{prefabPath}'.");
            }

            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.hideFlags = HideFlags.HideAndDontSave;
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;

                if (!TryGetPlanBounds(instance, out PlanBounds rendererBounds))
                {
                    throw new InvalidOperationException($"Prefab '{prefabPath}' has no renderer or collider bounds.");
                }

                if (!TryGetColliderPlanBounds(instance, out PlanBounds colliderBounds))
                {
                    colliderBounds = rendererBounds;
                }

                Vector2 faceNormal = Vector2.up;
                int baseQuadrant = CornerQuadrant.SouthEast;
                if (role == PrefabRole.StraightWall)
                {
                    if (!TryMeasureDominantHorizontalFaceNormal(instance, out faceNormal))
                    {
                        throw new InvalidOperationException($"Could not measure a dominant horizontal face normal for straight wall prefab '{prefabPath}'.");
                    }

                    faceNormal = SnapCardinal(faceNormal);
                }
                else if (role == PrefabRole.HardCorner)
                {
                    baseQuadrant = DominantQuadrantFromBounds(rendererBounds);
                }

                MeasureVerticalExtents(instance, out float localMinY, out float localMaxY);
                PlanBounds segmentBounds = role == PrefabRole.StraightWall ? colliderBounds : rendererBounds;
                (Vector2 start, Vector2 end) = MeasureSegment(segmentBounds);
                return new MeasuredPrefab(
                    string.Empty,
                    prefabPath,
                    role,
                    rendererBounds,
                    start,
                    end,
                    faceNormal,
                    localMaxY - localMinY,
                    localMaxY,
                    baseQuadrant);
            }
            finally
            {
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
        }

        private static Transform FindFirstChild(Transform parent, Predicate<string> predicate)
        {
            foreach (Transform child in parent)
            {
                if (predicate(child.name))
                {
                    return child;
                }
            }

            return null;
        }

        private static Transform FindFirstDescendant(Transform parent, Predicate<string> predicate)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child == parent)
                {
                    continue;
                }

                if (predicate(child.name))
                {
                    return child;
                }
            }

            return null;
        }

        private static bool IsFloorName(string name)
        {
            return name.Contains(FloorName, StringComparison.Ordinal);
        }

        private static bool IsStraightRailingName(string name)
        {
            return name.Contains(RailingName, StringComparison.Ordinal) && !name.Contains("half", StringComparison.OrdinalIgnoreCase);
        }

        private static Vector2 TransformRelativePlanPoint(RelativeTransform transform, Vector2 localPoint)
        {
            Vector3 transformed = transform.position + transform.rotation * new Vector3(localPoint.x, 0f, localPoint.y);
            return new Vector2(transformed.x, transformed.z);
        }

        private static int FindNearestFloorEdge(PlanBounds floorBounds, Vector2 start, Vector2 end)
        {
            Vector2 midpoint = (start + end) * 0.5f;
            float south = Mathf.Abs(midpoint.y - floorBounds.minZ);
            float north = Mathf.Abs(midpoint.y - floorBounds.maxZ);
            float west = Mathf.Abs(midpoint.x - floorBounds.minX);
            float east = Mathf.Abs(midpoint.x - floorBounds.maxX);
            float min = Mathf.Min(Mathf.Min(south, north), Mathf.Min(west, east));

            if (Mathf.Approximately(min, north))
            {
                return Direction.North;
            }

            if (Mathf.Approximately(min, east))
            {
                return Direction.East;
            }

            if (Mathf.Approximately(min, west))
            {
                return Direction.West;
            }

            return Direction.South;
        }

        private static bool TryFindEndpointColumns(
            List<RelativeTransform> columns,
            Vector2 start,
            Vector2 end,
            out RelativeTransform startColumn,
            out RelativeTransform endColumn)
        {
            startColumn = default;
            endColumn = default;
            int startIndex = FindNearestColumn(columns, start, -1);
            int endIndex = FindNearestColumn(columns, end, startIndex);
            if (startIndex < 0 || endIndex < 0)
            {
                return false;
            }

            startColumn = columns[startIndex];
            endColumn = columns[endIndex];
            return true;
        }

        private static int FindNearestColumn(List<RelativeTransform> columns, Vector2 point, int excludedIndex)
        {
            int bestIndex = -1;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == excludedIndex)
                {
                    continue;
                }

                Vector2 columnPoint = new Vector2(columns[i].position.x, columns[i].position.z);
                float distance = Vector2.Distance(columnPoint, point);
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestIndex = i;
            }

            return bestDistance <= 0.2f ? bestIndex : -1;
        }

        private static (Vector2 start, Vector2 end) MeasureSegment(PlanBounds bounds)
        {
            Vector2 size = bounds.Size;
            if (size.x >= size.y)
            {
                return (new Vector2(bounds.minX, 0f), new Vector2(bounds.maxX, 0f));
            }

            return (new Vector2(0f, bounds.minZ), new Vector2(0f, bounds.maxZ));
        }

        private static float MeasureHeight(GameObject instance)
        {
            return MeasureVerticalExtents(instance, out float minY, out float maxY) ? maxY - minY : 0f;
        }

        private static bool MeasureVerticalExtents(GameObject instance, out float minY, out float maxY)
        {
            bool initialized = false;
            minY = 0f;
            maxY = 0f;

            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (!initialized)
                {
                    minY = renderer.bounds.min.y;
                    maxY = renderer.bounds.max.y;
                    initialized = true;
                    continue;
                }

                minY = Mathf.Min(minY, renderer.bounds.min.y);
                maxY = Mathf.Max(maxY, renderer.bounds.max.y);
            }

            if (!initialized)
            {
                foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
                {
                    if (!initialized)
                    {
                        minY = collider.bounds.min.y;
                        maxY = collider.bounds.max.y;
                        initialized = true;
                        continue;
                    }

                    minY = Mathf.Min(minY, collider.bounds.min.y);
                    maxY = Mathf.Max(maxY, collider.bounds.max.y);
                }
            }

            return initialized;
        }

        private static bool TryMeasureDominantHorizontalFaceNormal(GameObject instance, out Vector2 faceNormal)
        {
            var buckets = new FaceNormalBuckets();

            foreach (MeshFilter meshFilter in instance.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = meshFilter.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0)
                {
                    continue;
                }

                Matrix4x4 localToRoot = instance.transform.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix;
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;

                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    Vector3 a = localToRoot.MultiplyPoint3x4(vertices[triangles[i]]);
                    Vector3 b = localToRoot.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                    Vector3 c = localToRoot.MultiplyPoint3x4(vertices[triangles[i + 2]]);
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    float doubleArea = cross.magnitude;
                    if (doubleArea <= 0.000001f)
                    {
                        continue;
                    }

                    Vector3 normal = cross / doubleArea;
                    Vector2 horizontal = new Vector2(normal.x, normal.z);
                    if (horizontal.sqrMagnitude < 0.2f)
                    {
                        continue;
                    }

                    Vector3 center = (a + b + c) / 3f;
                    buckets.Add(horizontal.normalized, new Vector2(center.x, center.z), doubleArea);
                }
            }

            if (!buckets.TryGetDominant(out faceNormal))
            {
                faceNormal = Vector2.zero;
                return false;
            }

            return true;
        }

        private static bool TryGetPlanBounds(GameObject instance, out PlanBounds bounds)
        {
            bounds = default;
            bool initialized = false;

            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                EncapsulatePlanBounds(ref bounds, ref initialized, renderer.bounds.min);
                EncapsulatePlanBounds(ref bounds, ref initialized, renderer.bounds.max);
            }

            if (initialized)
            {
                return true;
            }

            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
            {
                EncapsulatePlanBounds(ref bounds, ref initialized, collider.bounds.min);
                EncapsulatePlanBounds(ref bounds, ref initialized, collider.bounds.max);
            }

            return initialized;
        }

        private static bool TryGetColliderPlanBounds(GameObject instance, out PlanBounds bounds)
        {
            bounds = default;
            bool initialized = false;

            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
            {
                EncapsulatePlanBounds(ref bounds, ref initialized, collider.bounds.min);
                EncapsulatePlanBounds(ref bounds, ref initialized, collider.bounds.max);
            }

            return initialized;
        }

        private static void EncapsulatePlanBounds(ref PlanBounds bounds, ref bool initialized, Vector3 point)
        {
            if (!initialized)
            {
                bounds = new PlanBounds(point.x, point.x, point.z, point.z);
                initialized = true;
                return;
            }

            bounds = new PlanBounds(
                Mathf.Min(bounds.minX, point.x),
                Mathf.Max(bounds.maxX, point.x),
                Mathf.Min(bounds.minZ, point.z),
                Mathf.Max(bounds.maxZ, point.z));
        }

        private static GameObject InstantiatePrefab(string prefabPath, string name, Transform parent, Vector3 position, float yRotation)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException($"Missing prefab at '{prefabPath}'.");
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (!Application.isBatchMode)
            {
                Undo.RegisterCreatedObjectUndo(instance, $"Create {name}");
            }
            instance.name = name;
            instance.transform.SetParent(parent);
            instance.transform.position = position;
            instance.transform.rotation = Quaternion.Euler(0f, yRotation, 0f);
            instance.transform.localScale = Vector3.one;
            return instance;
        }

        private static GameObject CreateChild(Transform parent, string name)
        {
            var child = new GameObject(name);
            if (!Application.isBatchMode)
            {
                Undo.RegisterCreatedObjectUndo(child, $"Create {name}");
            }
            child.transform.SetParent(parent, false);
            return child;
        }

        private static void ClearExistingRoot(string rootName)
        {
            var existing = GameObject.Find(rootName);
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing);
            }
        }

        private static Vector3 CellMin(Vector3 origin, int x, int z, float y)
        {
            return origin + new Vector3(x * CellSize, y, z * CellSize);
        }

        private static Vector2Int Neighbor(Vector2Int cell, int direction)
        {
            switch (direction)
            {
                case Direction.North:
                    return new Vector2Int(cell.x, cell.y + 1);
                case Direction.East:
                    return new Vector2Int(cell.x + 1, cell.y);
                case Direction.South:
                    return new Vector2Int(cell.x, cell.y - 1);
                case Direction.West:
                    return new Vector2Int(cell.x - 1, cell.y);
                default:
                    throw new InvalidOperationException($"Unknown direction {direction}.");
            }
        }

        private static Vector2Int CellFromWorldPort(Vector3 origin, Vector3 worldPoint, int outwardDirection)
        {
            const float boundaryBias = 0.001f;
            const float outwardNudge = 0.05f;
            Vector2 outward = DirectionVector(outwardDirection) * outwardNudge;
            return new Vector2Int(
                Mathf.FloorToInt((worldPoint.x - origin.x + outward.x + boundaryBias) / CellSize),
                Mathf.FloorToInt((worldPoint.z - origin.z + outward.y + boundaryBias) / CellSize));
        }

        private static Vector2Int CellFromWorldPoint(Vector3 origin, Vector3 worldPoint)
        {
            const float boundaryBias = 0.001f;
            return new Vector2Int(
                Mathf.FloorToInt((worldPoint.x - origin.x + boundaryBias) / CellSize),
                Mathf.FloorToInt((worldPoint.z - origin.z + boundaryBias) / CellSize));
        }

        private static int DirectionFromVector(Vector2 vector)
        {
            Vector2 cardinal = SnapCardinal(vector);
            if (cardinal == Vector2.up)
            {
                return Direction.North;
            }

            if (cardinal == Vector2.right)
            {
                return Direction.East;
            }

            if (cardinal == Vector2.down)
            {
                return Direction.South;
            }

            return Direction.West;
        }

        private static bool AreCardinalNeighbors(Vector2Int first, Vector2Int second)
        {
            int dx = Mathf.Abs(first.x - second.x);
            int dz = Mathf.Abs(first.y - second.y);
            return dx + dz == 1;
        }

        private static PlatformEdge EdgeFromCellToward(Vector2Int cell, Vector2Int neighbor)
        {
            Vector2Int delta = neighbor - cell;
            if (delta == Vector2Int.up)
            {
                return new PlatformEdge(cell.x, cell.y, Direction.North);
            }

            if (delta == Vector2Int.right)
            {
                return new PlatformEdge(cell.x, cell.y, Direction.East);
            }

            if (delta == Vector2Int.down)
            {
                return new PlatformEdge(cell.x, cell.y, Direction.South);
            }

            if (delta == Vector2Int.left)
            {
                return new PlatformEdge(cell.x, cell.y, Direction.West);
            }

            throw new InvalidOperationException($"Cells are not cardinal neighbors: {cell} -> {neighbor}.");
        }

        private static int DirectionFromCellToward(Vector2Int cell, Vector2Int target)
        {
            if (TryDirectionFromCellToward(cell, target, out int direction))
            {
                return direction;
            }

            throw new InvalidOperationException($"Transition port cells must align cardinally: {cell} -> {target}.");
        }

        private static bool TryDirectionFromCellToward(Vector2Int cell, Vector2Int target, out int direction)
        {
            Vector2Int delta = target - cell;
            if (delta.x == 0 && delta.y > 0)
            {
                direction = Direction.North;
                return true;
            }

            if (delta.x > 0 && delta.y == 0)
            {
                direction = Direction.East;
                return true;
            }

            if (delta.x == 0 && delta.y < 0)
            {
                direction = Direction.South;
                return true;
            }

            if (delta.x < 0 && delta.y == 0)
            {
                direction = Direction.West;
                return true;
            }

            direction = 0;
            return false;
        }

        private static int OppositeDirection(int direction)
        {
            switch (direction)
            {
                case Direction.North:
                    return Direction.South;
                case Direction.East:
                    return Direction.West;
                case Direction.South:
                    return Direction.North;
                case Direction.West:
                    return Direction.East;
                default:
                    return direction;
            }
        }

        private static bool IsCardinalDirection(int direction)
        {
            return direction == Direction.North ||
                direction == Direction.East ||
                direction == Direction.South ||
                direction == Direction.West;
        }

        private static Vector3 EdgeStart(Vector3 origin, PlatformEdge edge, float y)
        {
            Vector3 cellMin = CellMin(origin, edge.x, edge.z, y);
            Vector3 cellMax = cellMin + new Vector3(CellSize, 0f, CellSize);
            GetEdgePlacement(edge, cellMin, cellMax, out Vector3 edgeA, out _, out _);
            return edgeA;
        }

        private static Vector3 EdgeCenter(Vector3 origin, PlatformEdge edge, float y)
        {
            Vector3 cellMin = CellMin(origin, edge.x, edge.z, y);
            Vector3 cellMax = cellMin + new Vector3(CellSize, 0f, CellSize);
            GetEdgePlacement(edge, cellMin, cellMax, out Vector3 edgeA, out Vector3 edgeB, out _);
            return (edgeA + edgeB) * 0.5f;
        }

        private static Vector3 PortSpanEdgeCenter(Vector3 origin, PlatformEdge edge, float y, int spanCellCount)
        {
            int span = Mathf.Max(1, spanCellCount);
            Vector3 singleCellCenter = EdgeCenter(origin, edge, y);
            if (span == 1)
            {
                return singleCellCenter;
            }

            float lateralOffset = (span - 1) * CellSize * 0.5f;
            switch (edge.direction)
            {
                case Direction.North:
                case Direction.South:
                    return singleCellCenter + Vector3.right * lateralOffset;
                case Direction.East:
                case Direction.West:
                    return singleCellCenter + Vector3.forward * lateralOffset;
                default:
                    return singleCellCenter;
            }
        }

        private static PlanBounds TransformPlanBounds(PlanBounds localBounds, Vector3 position, float yRotation)
        {
            Vector2 a = TransformPlanPoint(localBounds.minX, localBounds.minZ, position, yRotation);
            Vector2 b = TransformPlanPoint(localBounds.minX, localBounds.maxZ, position, yRotation);
            Vector2 c = TransformPlanPoint(localBounds.maxX, localBounds.minZ, position, yRotation);
            Vector2 d = TransformPlanPoint(localBounds.maxX, localBounds.maxZ, position, yRotation);
            return new PlanBounds(
                Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)),
                Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x)),
                Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y)),
                Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y)));
        }

        private static Bounds TransformBounds(Bounds localBounds, Transform transform)
        {
            Vector3 min = localBounds.min;
            Vector3 max = localBounds.max;
            var bounds = new Bounds(transform.TransformPoint(new Vector3(min.x, min.y, min.z)), Vector3.zero);
            bounds.Encapsulate(transform.TransformPoint(new Vector3(min.x, min.y, max.z)));
            bounds.Encapsulate(transform.TransformPoint(new Vector3(min.x, max.y, min.z)));
            bounds.Encapsulate(transform.TransformPoint(new Vector3(min.x, max.y, max.z)));
            bounds.Encapsulate(transform.TransformPoint(new Vector3(max.x, min.y, min.z)));
            bounds.Encapsulate(transform.TransformPoint(new Vector3(max.x, min.y, max.z)));
            bounds.Encapsulate(transform.TransformPoint(new Vector3(max.x, max.y, min.z)));
            bounds.Encapsulate(transform.TransformPoint(new Vector3(max.x, max.y, max.z)));
            return bounds;
        }

        private static Vector2 TransformPlanPoint(float localX, float localZ, Vector3 position, float yRotation)
        {
            Vector2 rotated = Rotate2D(new Vector2(localX, localZ), yRotation);
            return new Vector2(position.x + rotated.x, position.z + rotated.y);
        }

        private static PlanBounds CellPlanBounds(Vector3 origin, Vector2Int cell)
        {
            Vector3 min = CellMin(origin, cell.x, cell.y, 0f);
            return new PlanBounds(min.x, min.x + CellSize, min.z, min.z + CellSize);
        }

        private static bool PlanBoundsContainsPoint(PlanBounds bounds, Vector2 point, float tolerance)
        {
            return point.x >= bounds.minX - tolerance &&
                point.x <= bounds.maxX + tolerance &&
                point.y >= bounds.minZ - tolerance &&
                point.y <= bounds.maxZ + tolerance;
        }

        private static bool PlanBoundsOverlapOrTouch(PlanBounds first, PlanBounds second, float tolerance)
        {
            return first.maxX >= second.minX - tolerance &&
                first.minX <= second.maxX + tolerance &&
                first.maxZ >= second.minZ - tolerance &&
                first.minZ <= second.maxZ + tolerance;
        }

        private static float PlanDistance(Vector3 first, Vector3 second)
        {
            return Vector2.Distance(
                new Vector2(first.x, first.z),
                new Vector2(second.x, second.z));
        }

        private static void GetEdgePlacement(
            PlatformEdge edge,
            Vector3 cellMin,
            Vector3 cellMax,
            out Vector3 edgeA,
            out Vector3 edgeB,
            out Vector2 outwardNormal)
        {
            switch (edge.direction)
            {
                case Direction.North:
                    edgeA = new Vector3(cellMin.x, cellMin.y, cellMax.z);
                    edgeB = new Vector3(cellMax.x, cellMin.y, cellMax.z);
                    outwardNormal = Vector2.up;
                    break;
                case Direction.East:
                    edgeA = new Vector3(cellMax.x, cellMin.y, cellMin.z);
                    edgeB = new Vector3(cellMax.x, cellMin.y, cellMax.z);
                    outwardNormal = Vector2.right;
                    break;
                case Direction.South:
                    edgeA = cellMin;
                    edgeB = new Vector3(cellMax.x, cellMin.y, cellMin.z);
                    outwardNormal = Vector2.down;
                    break;
                case Direction.West:
                    edgeA = cellMin;
                    edgeB = new Vector3(cellMin.x, cellMin.y, cellMax.z);
                    outwardNormal = Vector2.left;
                    break;
                default:
                    edgeA = cellMin;
                    edgeB = cellMin;
                    outwardNormal = Vector2.zero;
                    break;
            }
        }

        private static void GetEdgeVertices(PlatformEdge edge, out Vector2Int first, out Vector2Int second)
        {
            Vector2Int cell = new Vector2Int(edge.x, edge.z);
            switch (edge.direction)
            {
                case Direction.North:
                    first = new Vector2Int(cell.x, cell.y + 1);
                    second = new Vector2Int(cell.x + 1, cell.y + 1);
                    break;
                case Direction.East:
                    first = new Vector2Int(cell.x + 1, cell.y);
                    second = new Vector2Int(cell.x + 1, cell.y + 1);
                    break;
                case Direction.South:
                    first = new Vector2Int(cell.x, cell.y);
                    second = new Vector2Int(cell.x + 1, cell.y);
                    break;
                case Direction.West:
                    first = new Vector2Int(cell.x, cell.y);
                    second = new Vector2Int(cell.x, cell.y + 1);
                    break;
                default:
                    first = cell;
                    second = cell;
                    break;
            }
        }

        private static bool TryGetRailingCornerEdgeForVertex(
            List<PlatformEdge> cliffEdges,
            Vector2Int vertex,
            out PlatformEdge edge,
            out bool vertexIsStart)
        {
            for (int i = 0; i < cliffEdges.Count; i++)
            {
                Vector2Int firstDirection = EdgeDirectionFromVertex(cliffEdges[i], vertex);
                if (firstDirection == Vector2Int.zero)
                {
                    continue;
                }

                for (int j = i + 1; j < cliffEdges.Count; j++)
                {
                    Vector2Int secondDirection = EdgeDirectionFromVertex(cliffEdges[j], vertex);
                    if (secondDirection == Vector2Int.zero)
                    {
                        continue;
                    }

                    if (firstDirection.x * secondDirection.x + firstDirection.y * secondDirection.y != 0)
                    {
                        continue;
                    }

                    GetEdgeVertices(cliffEdges[i], out Vector2Int first, out Vector2Int second);
                    edge = cliffEdges[i];
                    vertexIsStart = first == vertex;
                    if (second == vertex)
                    {
                        vertexIsStart = false;
                    }

                    return true;
                }
            }

            edge = default;
            vertexIsStart = false;
            return false;
        }

        private static void Increment(Dictionary<Vector2Int, int> counts, Vector2Int key)
        {
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
        }

        private static void EncapsulateInstance(GameObject instance, ref Bounds bounds, ref bool hasBounds)
        {
            if (!TryGetRendererOrColliderWorldBounds(instance, out Bounds instanceBounds))
            {
                return;
            }

            Encapsulate(ref bounds, ref hasBounds, instanceBounds.min);
            Encapsulate(ref bounds, ref hasBounds, instanceBounds.max);
        }

        private static void EncapsulateChildren(GameObject root, ref Bounds bounds, ref bool hasBounds)
        {
            if (!TryGetRendererOrColliderWorldBounds(root, out Bounds instanceBounds))
            {
                return;
            }

            Encapsulate(ref bounds, ref hasBounds, instanceBounds.min);
            Encapsulate(ref bounds, ref hasBounds, instanceBounds.max);
        }

        private static bool TryGetRendererOrColliderWorldBounds(GameObject instance, out Bounds bounds)
        {
            bounds = default;
            bool initialized = false;

            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                EncapsulateWorldBounds(ref bounds, ref initialized, renderer.bounds);
            }

            if (initialized)
            {
                return true;
            }

            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
            {
                EncapsulateWorldBounds(ref bounds, ref initialized, collider.bounds);
            }

            return initialized;
        }

        private static void EncapsulateWorldBounds(ref Bounds bounds, ref bool initialized, Bounds candidate)
        {
            if (!initialized)
            {
                bounds = candidate;
                initialized = true;
                return;
            }

            bounds.Encapsulate(candidate);
        }

        private static void Encapsulate(ref Bounds bounds, ref bool hasBounds, Vector3 point)
        {
            if (!hasBounds)
            {
                bounds = new Bounds(point, Vector3.zero);
                hasBounds = true;
                return;
            }

            bounds.Encapsulate(point);
        }

        private static void CaptureScreenshot(Bounds bounds, string path, int width, int height)
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                var cameraObject = new GameObject("Tiered Platforms Screenshot Camera");
                Undo.RegisterCreatedObjectUndo(cameraObject, "Create Tiered Platforms Screenshot Camera");
                camera = cameraObject.AddComponent<Camera>();
                camera.tag = "MainCamera";
            }

            Vector3 center = bounds.center;
            float radius = Mathf.Max(16f, bounds.extents.magnitude);
            Vector3 direction = new Vector3(-0.85f, 0.65f, -0.95f).normalized;
            camera.transform.position = center - direction * (radius * 1.65f);
            camera.transform.LookAt(center + Vector3.up * 1.5f);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = Mathf.Max(250f, radius * 8f);
            camera.fieldOfView = 35f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.08f, 0.09f, 0.1f, 1f);

            var renderTexture = new RenderTexture(width, height, 24);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(renderTexture);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static float CalculateYawToMap(Vector2 localDirection, Vector2 worldDirection)
        {
            float localAngle = Mathf.Atan2(localDirection.y, localDirection.x) * Mathf.Rad2Deg;
            float worldAngle = Mathf.Atan2(worldDirection.y, worldDirection.x) * Mathf.Rad2Deg;
            return NormalizeAngle(localAngle - worldAngle);
        }

        private static float CalculateCornerYaw(int baseQuadrant, int targetQuadrant)
        {
            return NormalizeAngle((targetQuadrant - baseQuadrant) * 90f);
        }

        private static bool DoesRotatedBoundsOccupyQuadrant(PlanBounds localBounds, float yRotation, int targetQuadrant)
        {
            Vector2 rotatedCenter = Rotate2D(localBounds.Center, yRotation);
            return IsOffsetInQuadrant(rotatedCenter, targetQuadrant, 0.05f);
        }

        private static bool IsOffsetInQuadrant(Vector2 offset, int quadrant, float tolerance)
        {
            switch (quadrant)
            {
                case CornerQuadrant.NorthEast:
                    return offset.x > tolerance && offset.y > tolerance;
                case CornerQuadrant.SouthEast:
                    return offset.x > tolerance && offset.y < -tolerance;
                case CornerQuadrant.SouthWest:
                    return offset.x < -tolerance && offset.y < -tolerance;
                case CornerQuadrant.NorthWest:
                    return offset.x < -tolerance && offset.y > tolerance;
                default:
                    return false;
            }
        }

        private static int DominantQuadrantFromBounds(PlanBounds bounds)
        {
            return QuadrantFromOffset(bounds.Center, 0.05f);
        }

        private static int QuadrantFromOffset(Vector2 offset, float tolerance)
        {
            if (Mathf.Abs(offset.x) <= tolerance || Mathf.Abs(offset.y) <= tolerance)
            {
                throw new InvalidOperationException($"Cannot infer quadrant from near-axis offset {offset}.");
            }

            if (offset.x > 0f)
            {
                return offset.y > 0f ? CornerQuadrant.NorthEast : CornerQuadrant.SouthEast;
            }

            return offset.y > 0f ? CornerQuadrant.NorthWest : CornerQuadrant.SouthWest;
        }

        private static Vector2 Rotate2D(Vector2 vector, float yRotation)
        {
            float radians = -yRotation * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            return new Vector2(
                vector.x * cos - vector.y * sin,
                vector.x * sin + vector.y * cos);
        }

        private static float NormalizeAngle(float value)
        {
            float normalized = value % 360f;
            return normalized < 0f ? normalized + 360f : normalized;
        }

        private static Vector2 SnapCardinal(Vector2 vector)
        {
            if (Mathf.Abs(vector.x) >= Mathf.Abs(vector.y))
            {
                return new Vector2(Mathf.Sign(vector.x), 0f);
            }

            return new Vector2(0f, Mathf.Sign(vector.y));
        }

        private static Vector2 DirectionVector(int direction)
        {
            switch (direction)
            {
                case Direction.North:
                    return Vector2.up;
                case Direction.East:
                    return Vector2.right;
                case Direction.South:
                    return Vector2.down;
                case Direction.West:
                    return Vector2.left;
                default:
                    return Vector2.zero;
            }
        }

        private static string DirectionName(int direction)
        {
            switch (direction)
            {
                case Direction.North:
                    return "North";
                case Direction.East:
                    return "East";
                case Direction.South:
                    return "South";
                case Direction.West:
                    return "West";
                case Direction.Up:
                    return "Up";
                default:
                    return $"Unknown({direction})";
            }
        }

        private static int DirectionFromName(string direction)
        {
            switch (direction)
            {
                case "N":
                case "North":
                case "north":
                    return Direction.North;
                case "E":
                case "East":
                case "east":
                    return Direction.East;
                case "S":
                case "South":
                case "south":
                    return Direction.South;
                case "W":
                case "West":
                case "west":
                    return Direction.West;
                case "UP":
                case "Up":
                case "up":
                    return Direction.Up;
                case "DOWN":
                case "Down":
                case "down":
                    return Direction.Down;
                default:
                    return 0;
            }
        }

        private static string CornerQuadrantName(int quadrant)
        {
            switch (quadrant)
            {
                case CornerQuadrant.NorthEast:
                    return "NorthEast";
                case CornerQuadrant.SouthEast:
                    return "SouthEast";
                case CornerQuadrant.SouthWest:
                    return "SouthWest";
                case CornerQuadrant.NorthWest:
                    return "NorthWest";
                default:
                    return $"Unknown({quadrant})";
            }
        }

        private static string Format(Vector3 value)
        {
            return $"({value.x:0.###},{value.y:0.###},{value.z:0.###})";
        }

        private static string StackDescription(IReadOnlyList<MeasuredPrefab> stack)
        {
            var names = new List<string>();
            foreach (MeasuredPrefab piece in stack)
            {
                names.Add($"{piece.name}({piece.height:0.###}u)");
            }

            return string.Join(" + ", names);
        }

        private enum PrefabRole
        {
            Floor,
            StraightWall,
            HardCorner,
            StairRun,
            Railing,
            RailingColumn
        }

        private sealed class TieredPlatformBuildStats
        {
            public int floorCells;
            public int interiorEdges;
            public int cliffEdges;
            public int retainingEdges;
            public int transitionEdges;
            public int totalRooms;
            public int enclosedRooms;
            public int partitionWalls;
            public int doorways;
            public int gateways;
            public int largeGateways;
            public int barredGateways;
            public int traps;
            public int largePerimeterRooms;
            public int largePartitionWalls;
            public int partitionWallChecks;
            public int internalPathEdges;
            public int internalPathRailings;
            public int internalPathBareEdges;
            public int bareBoundaryEdges;
            public int promontoryDeckCells;
            public int stairOpenings;
            public int stairFootprintChecks;
            public int multiRiseStairChecks;
            public int rejected;
            public int corners;
            public int railings;
            // C1b: surfaces standing above their column floor, and how their
            // rims were guarded. Zero on every plan the generator produces
            // today, which is what makes them a change detector rather than
            // decoration.
            public int stackedSurfaces;
            public int stackedRailedRims;
            public int stackedBareRims;
            public string stairSummary;
            public readonly List<string> stairSummaries = new List<string>();

            public TieredPlatformBuildStats()
            {
                stairSummary = string.Empty;
            }
        }

        // Online synthesis (step 7): one piece of a synthesized staircase's
        // in-memory plan — the same data the forge would have written into a
        // prefab, kept as pure data so planning stays headless-safe.
        public readonly struct SynthesizedPiecePlacement
        {
            public readonly string sourcePrefab;
            public readonly string pieceName;
            public readonly Vector3 localPosition;
            public readonly float localYawDegrees;
            // 180 flips a one-sided piece upside down (deck bottom caps reuse the
            // floor slab face-down — the pack has no ceiling family).
            public readonly float localPitchDegrees;

            public SynthesizedPiecePlacement(string sourcePrefab, string pieceName, Vector3 localPosition, float localYawDegrees)
                : this(sourcePrefab, pieceName, localPosition, localYawDegrees, 0f)
            {
            }

            public SynthesizedPiecePlacement(string sourcePrefab, string pieceName, Vector3 localPosition, float localYawDegrees, float localPitchDegrees)
            {
                this.sourcePrefab = sourcePrefab;
                this.pieceName = pieceName;
                this.localPosition = localPosition;
                this.localYawDegrees = localYawDegrees;
                this.localPitchDegrees = localPitchDegrees;
            }
        }

        // Online synthesis (step 7): an in-memory stair set piece riding a
        // TransitionEdge — contract token plus piece plan emitted from ONE forge
        // plan (the anti-drift property), never persisted as an asset. Planning
        // validates the token with the real parser; the renderer parses it again
        // with the active level height and builds the visual from the piece plan.
        public sealed class SynthesizedStairSetPiece
        {
            public readonly string name;
            public readonly JObject contractToken;
            public readonly SynthesizedPiecePlacement[] pieces;

            public SynthesizedStairSetPiece(string name, JObject contractToken, SynthesizedPiecePlacement[] pieces)
            {
                this.name = name;
                this.contractToken = contractToken;
                this.pieces = pieces ?? Array.Empty<SynthesizedPiecePlacement>();
            }
        }

        /// <summary>
        /// A placed vertical connection, and the two SURFACES it joins.
        /// </summary>
        /// <remarks>
        /// <para>
        /// C2b-2 of the layered 3D topology design. The edge used to carry only
        /// its two CELLS, so every consumer that needed its elevations looked
        /// them up in the level field — which is unambiguous only while a cell
        /// has one surface. A cross-layer stair whose upper end stands over its
        /// own lower end would have read both endpoints as the column floor,
        /// computed a delta of 0, and been rejected by the transition-contract
        /// gate as too shallow to be a stair.
        /// </para>
        /// <para>
        /// Recording the levels at construction is safe because nothing can move
        /// them afterwards: of the surface field's three writers,
        /// `TrySetFloorLevel` rejects a conflicting value rather than
        /// overwriting, `AddFloorLevel` requires an empty column, and
        /// `RelevelFloor` — the only mover — runs inside `TryRealizeRecipes`,
        /// which precedes every producer including its own transitions. Every
        /// producer already knew both levels; they had to, to decide which end
        /// was the raised one.
        /// </para>
        /// </remarks>
        public readonly struct TransitionEdge
        {
            public readonly Vector2Int firstCell;
            public readonly Vector2Int secondCell;
            /// <summary>The surface level at <see cref="firstCell"/>.</summary>
            public readonly int firstLevel;
            /// <summary>The surface level at <see cref="secondCell"/>.</summary>
            public readonly int secondLevel;
            public readonly string stairPrefabPath;
            public readonly SynthesizedStairSetPiece synthesizedSetPiece;
            public readonly bool hasLandings;
            public readonly Vector2Int lowerLandingCell;
            public readonly Vector2Int upperLandingCell;
            public readonly Vector2Int[] lowerLandingCells;
            public readonly Vector2Int[] upperLandingCells;
            public readonly Vector2Int[] footprintCells;
            public readonly bool hasPortDirections;
            public readonly int lowerPortDirection;
            public readonly int upperPortDirection;
            public readonly string placementClass;

            /// <summary>
            /// An endpoint standing on a column that carries no surface.
            /// </summary>
            /// <remarks>
            /// Recorded rather than thrown at the producer, so the checks that
            /// used to catch a missing cell by failing a field lookup keep
            /// producing the same rejection now that they read the edge.
            /// </remarks>
            public const int UnknownLevel = int.MinValue;

            /// <summary>True when both ends resolved a surface.</summary>
            public bool HasLevels => firstLevel != UnknownLevel && secondLevel != UnknownLevel;

            /// <summary>The lower of the two endpoint levels.</summary>
            public int LowerLevel => Mathf.Min(firstLevel, secondLevel);

            /// <summary>The upper of the two endpoint levels.</summary>
            public int UpperLevel => Mathf.Max(firstLevel, secondLevel);

            /// <summary>How far this connection climbs. Zero for a flat deck.</summary>
            public int RiseLevels => UpperLevel - LowerLevel;

            /// <summary>The cell at the upper end; ties resolve to <see cref="firstCell"/>.</summary>
            public Vector2Int HigherCell => firstLevel >= secondLevel ? firstCell : secondCell;

            /// <summary>The cell at the lower end; ties resolve to <see cref="secondCell"/>.</summary>
            public Vector2Int LowerCell => firstLevel >= secondLevel ? secondCell : firstCell;

            // Landing-less classed transition (seam strips): landings default to
            // the transition's own cells in the port graph.
            public TransitionEdge(
                Vector2Int firstCell,
                int firstLevel,
                Vector2Int secondCell,
                int secondLevel,
                string stairPrefabPath,
                string placementClass)
                : this(
                    firstCell,
                    firstLevel,
                    secondCell,
                    secondLevel,
                    stairPrefabPath,
                    Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>(),
                    false,
                    0,
                    0,
                    placementClass,
                    false)
            {
            }

            public TransitionEdge(
                Vector2Int firstCell,
                int firstLevel,
                Vector2Int secondCell,
                int secondLevel,
                string stairPrefabPath,
                Vector2Int[] lowerLandingCells,
                Vector2Int[] upperLandingCells,
                Vector2Int[] footprintCells,
                int lowerPortDirection,
                int upperPortDirection,
                string placementClass)
                : this(
                    firstCell,
                    firstLevel,
                    secondCell,
                    secondLevel,
                    stairPrefabPath,
                    lowerLandingCells,
                    upperLandingCells,
                    footprintCells,
                    true,
                    lowerPortDirection,
                    upperPortDirection,
                    placementClass,
                    true)
            {
            }

            // Online synthesis (step 7): same shape as the full contract-stair
            // ctor plus the in-memory set piece the renderer materializes instead
            // of loading a prefab.
            public TransitionEdge(
                Vector2Int firstCell,
                int firstLevel,
                Vector2Int secondCell,
                int secondLevel,
                string stairPrefabPath,
                Vector2Int[] lowerLandingCells,
                Vector2Int[] upperLandingCells,
                Vector2Int[] footprintCells,
                int lowerPortDirection,
                int upperPortDirection,
                string placementClass,
                SynthesizedStairSetPiece synthesizedSetPiece)
                : this(
                    firstCell,
                    firstLevel,
                    secondCell,
                    secondLevel,
                    stairPrefabPath,
                    lowerLandingCells,
                    upperLandingCells,
                    footprintCells,
                    true,
                    lowerPortDirection,
                    upperPortDirection,
                    placementClass,
                    true)
            {
                this.synthesizedSetPiece = synthesizedSetPiece;
            }

            private TransitionEdge(
                Vector2Int firstCell,
                int firstLevel,
                Vector2Int secondCell,
                int secondLevel,
                string stairPrefabPath,
                Vector2Int[] lowerLandingCells,
                Vector2Int[] upperLandingCells,
                Vector2Int[] footprintCells,
                bool hasPortDirections,
                int lowerPortDirection,
                int upperPortDirection,
                string placementClass,
                bool hasLandings)
            {
                this.firstCell = firstCell;
                this.firstLevel = firstLevel;
                this.secondCell = secondCell;
                this.secondLevel = secondLevel;
                this.stairPrefabPath = stairPrefabPath ?? string.Empty;
                this.synthesizedSetPiece = null;
                this.lowerLandingCells = lowerLandingCells ?? Array.Empty<Vector2Int>();
                this.upperLandingCells = upperLandingCells ?? Array.Empty<Vector2Int>();
                this.footprintCells = footprintCells ?? Array.Empty<Vector2Int>();
                this.lowerLandingCell = this.lowerLandingCells.Length > 0 ? this.lowerLandingCells[0] : default;
                this.upperLandingCell = this.upperLandingCells.Length > 0 ? this.upperLandingCells[0] : default;
                this.hasLandings = hasLandings;
                this.hasPortDirections = hasPortDirections;
                this.lowerPortDirection = lowerPortDirection;
                this.upperPortDirection = upperPortDirection;
                this.placementClass = string.IsNullOrWhiteSpace(placementClass) ? EmbeddedStairPlacementClass : placementClass;
            }
        }


        public sealed class RoomBoundaryContext
        {
            public readonly IReadOnlyDictionary<Vector2Int, int> cellRoomIds;
            public readonly IReadOnlyList<bool> enclosedRooms;
            public readonly IReadOnlyList<DoorwayEdge> doorwayEdges;
            public readonly IReadOnlyList<InternalPathEdge> internalPathEdges;
            public readonly int gatewaySelectionSalt;
            public readonly IReadOnlyList<GatewayConnectionEnd> gatewayConnectionEnds;

            public RoomBoundaryContext(
                IReadOnlyDictionary<Vector2Int, int> cellRoomIds,
                IReadOnlyList<bool> enclosedRooms,
                IReadOnlyList<DoorwayEdge> doorwayEdges,
                IReadOnlyList<InternalPathEdge> internalPathEdges = null,
                int gatewaySelectionSalt = 0,
                IReadOnlyList<GatewayConnectionEnd> gatewayConnectionEnds = null)
            {
                this.cellRoomIds = cellRoomIds;
                this.enclosedRooms = enclosedRooms;
                this.doorwayEdges = doorwayEdges;
                this.internalPathEdges = internalPathEdges ?? Array.Empty<InternalPathEdge>();
                this.gatewaySelectionSalt = gatewaySelectionSalt;
                this.gatewayConnectionEnds =
                    gatewayConnectionEnds ?? Array.Empty<GatewayConnectionEnd>();
            }
        }

        public enum InternalPathEdgeGuard
        {
            Bare,
            Railing
        }

        public readonly struct InternalPathEdge
        {
            public readonly Vector2Int cell;
            public readonly int direction;
            public readonly InternalPathEdgeGuard guard;

            public InternalPathEdge(Vector2Int cell, int direction, InternalPathEdgeGuard guard)
            {
                this.cell = cell;
                this.direction = direction;
                this.guard = guard;
            }
        }

        public readonly struct DoorwayEdge
        {
            public readonly Vector2Int firstCell;
            public readonly Vector2Int secondCell;
            public readonly int connectionIndex;

            public DoorwayEdge(Vector2Int firstCell, Vector2Int secondCell)
                : this(firstCell, secondCell, -1)
            {
            }

            public DoorwayEdge(
                Vector2Int firstCell,
                Vector2Int secondCell,
                int connectionIndex)
            {
                this.firstCell = firstCell;
                this.secondCell = secondCell;
                this.connectionIndex = connectionIndex;
            }
        }

        public sealed class GatewayConnectionEnd
        {
            public readonly DoorwayEdge roomThreshold;
            public readonly int endIndex;
            public readonly IReadOnlyList<Vector2Int> outwardPath;

            public GatewayConnectionEnd(
                DoorwayEdge roomThreshold,
                int endIndex,
                IReadOnlyList<Vector2Int> outwardPath)
            {
                this.roomThreshold = roomThreshold;
                this.endIndex = endIndex;
                if (outwardPath == null)
                {
                    this.outwardPath = Array.Empty<Vector2Int>();
                    return;
                }

                var pathCopy = new Vector2Int[outwardPath.Count];
                for (int index = 0; index < outwardPath.Count; index++)
                {
                    pathCopy[index] = outwardPath[index];
                }
                this.outwardPath = Array.AsReadOnly(pathCopy);
            }
        }

        public readonly struct OpenFloorEdge
        {
            /// <summary>
            /// The sentinel <see cref="level"/> for "this cell's column floor",
            /// which is what every producer before C1b meant.
            /// </summary>
            public const int ColumnFloorLevel = int.MinValue;

            public readonly Vector2Int cell;
            public readonly int direction;
            // C1b: which SURFACE at this cell the edge belongs to. A rim is a
            // property of a surface, not of a column — an aperture in an upper
            // gallery leaves the floor below it fully guarded.
            public readonly int level;

            public OpenFloorEdge(Vector2Int cell, int direction)
                : this(cell, ColumnFloorLevel, direction)
            {
            }

            public OpenFloorEdge(Vector2Int cell, int level, int direction)
            {
                this.cell = cell;
                this.level = level;
                this.direction = direction;
            }

            /// <summary>True when the edge names a surface rather than a column.</summary>
            public bool IsSurfaceScoped => level != ColumnFloorLevel;
        }

        /// <summary>
        /// A walkable surface ABOVE its column's floor (design §3.1).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The renderer's second input. The column floor still arrives as the
        /// <c>Dictionary&lt;Vector2Int,int&gt;</c> level field it always has —
        /// which is what keeps every other consumer, and every existing seed,
        /// exactly where it was — and this carries whatever is stacked over it.
        /// A single-layer plan passes an empty collection and nothing in the
        /// renderer takes a different path.
        /// </para>
        /// <para>
        /// <c>kind</c> travels with the surface because §7.1's
        /// <c>IsGroundBacked</c> needs it: without it a suspended deck would be
        /// indistinguishable from a floor standing on fill.
        /// </para>
        /// </remarks>
        public readonly struct StackedSurface
        {
            public readonly Vector2Int cell;
            public readonly int level;
            public readonly SurfaceKind kind;

            public StackedSurface(Vector2Int cell, int level, SurfaceKind kind)
            {
                this.cell = cell;
                this.level = level;
                this.kind = kind;
            }
        }

        public readonly struct BuildReport
        {
            public readonly float levelHeight;
            public readonly int floorCells;
            public readonly int interiorEdges;
            public readonly int cliffEdges;
            public readonly int retainingEdges;
            public readonly int transitionEdges;
            public readonly int corners;
            public readonly int railings;
            public readonly int enclosedRooms;
            public readonly int totalRooms;
            public readonly int partitionWalls;
            public readonly int doorways;
            public readonly int gateways;
            public readonly int largeGateways;
            public readonly int barredGateways;
            public readonly int traps;
            public readonly int largePerimeterRooms;
            public readonly int largePartitionWalls;
            public readonly int partitionWallChecks;
            public readonly int stairFootprintChecks;
            public readonly int multiRiseStairChecks;
            public readonly int internalPathEdges;
            public readonly int internalPathRailings;
            public readonly int internalPathBareEdges;
            public readonly int bareBoundaryEdges;
            public readonly int promontoryDeckCells;
            // C1b. Deliberately NOT in Summary: that string is logged and can
            // reach diagnostics, and these are zero on every plan the generator
            // produces, so adding them would be noise everywhere and evidence
            // only in a fixture — which reads the fields directly.
            public readonly int stackedSurfaces;
            public readonly int stackedRailedRims;
            public readonly int stackedBareRims;
            public readonly int rejected;
            public readonly int rejectedContracts;
            public readonly string rejectedContractReasons;
            public readonly int unsupportedContracts;
            public readonly string unsupportedContractReasons;
            public readonly string stairSummary;
            public string Summary =>
                $"levelHeight {levelHeight:0.###}u; cells {floorCells}; interior {interiorEdges}; cliffs {cliffEdges}; retaining {retainingEdges}; transitions {transitionEdges}; enclosed rooms {enclosedRooms}/{totalRooms}, partition walls {partitionWalls} ({largePartitionWalls} large), large-perimeter rooms {largePerimeterRooms}, railings {railings}, internal path edges {internalPathEdges}, internal path railings {internalPathRailings}, internal path bare edges {internalPathBareEdges}, bare boundary edges {bareBoundaryEdges}, promontory deck cells {promontoryDeckCells}, doorways {doorways}, static gateways {gateways} ({largeGateways} at 6u, {barredGateways} barred), traps {traps}, partitionWallChecks {partitionWallChecks} passed; stairFootprintChecks {stairFootprintChecks} passed; multiRiseStairChecks {multiRiseStairChecks} passed; corners {corners}; REJECTED {rejected}; {stairSummary}";

            public BuildReport(
                float levelHeight,
                int floorCells,
                int interiorEdges,
                int cliffEdges,
                int retainingEdges,
                int transitionEdges,
                int corners,
                int railings,
                int enclosedRooms,
                int totalRooms,
                int partitionWalls,
                int doorways,
                int gateways,
                int largeGateways,
                int barredGateways,
                int traps,
                int largePerimeterRooms,
                int largePartitionWalls,
                int partitionWallChecks,
                int stairFootprintChecks,
                int multiRiseStairChecks,
                int internalPathEdges,
                int internalPathRailings,
                int internalPathBareEdges,
                int bareBoundaryEdges,
                int promontoryDeckCells,
                int stackedSurfaces,
                int stackedRailedRims,
                int stackedBareRims,
                int rejected,
                int rejectedContracts,
                string rejectedContractReasons,
                int unsupportedContracts,
                string unsupportedContractReasons,
                string stairSummary)
            {
                this.levelHeight = levelHeight;
                this.floorCells = floorCells;
                this.interiorEdges = interiorEdges;
                this.cliffEdges = cliffEdges;
                this.retainingEdges = retainingEdges;
                this.transitionEdges = transitionEdges;
                this.corners = corners;
                this.railings = railings;
                this.enclosedRooms = enclosedRooms;
                this.totalRooms = totalRooms;
                this.partitionWalls = partitionWalls;
                this.doorways = doorways;
                this.gateways = gateways;
                this.largeGateways = largeGateways;
                this.barredGateways = barredGateways;
                this.traps = traps;
                this.largePerimeterRooms = largePerimeterRooms;
                this.largePartitionWalls = largePartitionWalls;
                this.partitionWallChecks = partitionWallChecks;
                this.stairFootprintChecks = stairFootprintChecks;
                this.multiRiseStairChecks = multiRiseStairChecks;
                this.internalPathEdges = internalPathEdges;
                this.internalPathRailings = internalPathRailings;
                this.internalPathBareEdges = internalPathBareEdges;
                this.bareBoundaryEdges = bareBoundaryEdges;
                this.promontoryDeckCells = promontoryDeckCells;
                this.stackedSurfaces = stackedSurfaces;
                this.stackedRailedRims = stackedRailedRims;
                this.stackedBareRims = stackedBareRims;
                this.rejected = rejected;
                this.rejectedContracts = rejectedContracts;
                this.rejectedContractReasons = rejectedContractReasons ?? "[]";
                this.unsupportedContracts = unsupportedContracts;
                this.unsupportedContractReasons = unsupportedContractReasons ?? "[]";
                this.stairSummary = stairSummary;
            }
        }

        private readonly struct WallEdge
        {
            public readonly PlatformEdge edge;
            public readonly int lowerLevel;
            public readonly int higherLevel;
            public readonly bool isRetaining;
            public readonly bool isPartition;
            public readonly int partitionHeightUnits;

            // The wall face still renders, but the top of this edge is owned by a stair
            // body (its authored railings guard it), so the engine must not add a railing.
            public readonly bool suppressRailing;

            public WallEdge(PlatformEdge edge, int lowerLevel, int higherLevel, bool isRetaining, bool isPartition)
                : this(
                    edge,
                    lowerLevel,
                    higherLevel,
                    isRetaining,
                    isPartition,
                    suppressRailing: false,
                    partitionHeightUnits: isPartition ? 4 : 0)
            {
            }

            public WallEdge(PlatformEdge edge, int lowerLevel, int higherLevel, bool isRetaining, bool isPartition, bool suppressRailing)
                : this(
                    edge,
                    lowerLevel,
                    higherLevel,
                    isRetaining,
                    isPartition,
                    suppressRailing,
                    partitionHeightUnits: isPartition ? 4 : 0)
            {
            }

            private WallEdge(
                PlatformEdge edge,
                int lowerLevel,
                int higherLevel,
                bool isRetaining,
                bool isPartition,
                bool suppressRailing,
                int partitionHeightUnits)
            {
                this.edge = edge;
                this.lowerLevel = lowerLevel;
                this.higherLevel = higherLevel;
                this.isRetaining = isRetaining;
                this.isPartition = isPartition;
                this.suppressRailing = suppressRailing;
                this.partitionHeightUnits = partitionHeightUnits;
            }

            public WallEdge WithPartitionHeight(int heightUnits)
            {
                if (!isPartition || (heightUnits != 4 && heightUnits != 6))
                {
                    throw new InvalidOperationException(
                        $"Partition wall height must be 4u or 6u; received {heightUnits}u.");
                }

                return new WallEdge(
                    edge,
                    lowerLevel,
                    higherLevel,
                    isRetaining,
                    isPartition,
                    suppressRailing,
                    heightUnits);
            }
        }

        private readonly struct EdgeKey
        {
            private readonly Vector2Int first;
            private readonly Vector2Int second;

            public EdgeKey(Vector2Int left, Vector2Int right)
            {
                if (left.x < right.x || left.x == right.x && left.y <= right.y)
                {
                    first = left;
                    second = right;
                }
                else
                {
                    first = right;
                    second = left;
                }
            }

            public override string ToString()
            {
                return $"{first.x},{first.y}|{second.x},{second.y}";
            }
        }

        private readonly struct OuterShellPlacementResult
        {
            public readonly HashSet<(int x, int z, int direction)> guardEdges;
            public readonly HashSet<(int x, int z, int direction)> bareLandingEdges;
            public readonly Dictionary<EdgeKey, int> gatewayFlankWallHeights;

            public OuterShellPlacementResult(
                HashSet<(int x, int z, int direction)> guardEdges,
                HashSet<(int x, int z, int direction)> bareLandingEdges,
                Dictionary<EdgeKey, int> gatewayFlankWallHeights)
            {
                this.guardEdges =
                    guardEdges ?? new HashSet<(int x, int z, int direction)>();
                this.bareLandingEdges =
                    bareLandingEdges ?? new HashSet<(int x, int z, int direction)>();
                this.gatewayFlankWallHeights =
                    gatewayFlankWallHeights ?? new Dictionary<EdgeKey, int>();
            }
        }

        private readonly struct OpenEdgeKey
        {
            private readonly Vector2Int cell;
            private readonly int direction;

            public OpenEdgeKey(Vector2Int cell, int direction)
            {
                this.cell = cell;
                this.direction = direction;
            }
        }

        private readonly struct TieredPlatformContracts
        {
            public readonly float levelHeight;
            public readonly MeasuredPrefab floor;
            // §7.1 step 3 / §0.1: the same tile with a bottom face. Used for a
            // surface whose underside is visible, which is every surface that is
            // not the lowest in its column.
            public readonly MeasuredPrefab suspendedFloor;
            public readonly ConnectionPointSetPieceContract connectionPointStraightStair;
            public readonly IReadOnlyList<ConnectionPointSetPieceContract> connectionPointVariantStairs;
            public readonly DropFaceStack dropFaceStack;
            public readonly DropFaceStack cornerStack;
            public readonly RailingContracts railings;
            public readonly PartitionWallContracts partitions;
            public readonly GatewayContracts gateways;
            public readonly int rejectedContracts;
            public readonly string rejectedContractReasons;
            public readonly int unsupportedContracts;
            public readonly string unsupportedContractReasons;

            public TieredPlatformContracts(
                float levelHeight,
                MeasuredPrefab floor,
                MeasuredPrefab suspendedFloor,
                ConnectionPointSetPieceContract connectionPointStraightStair,
                IReadOnlyList<ConnectionPointSetPieceContract> connectionPointVariantStairs,
                DropFaceStack dropFaceStack,
                DropFaceStack cornerStack,
                RailingContracts railings,
                PartitionWallContracts partitions,
                GatewayContracts gateways,
                int rejectedContracts,
                string rejectedContractReasons,
                int unsupportedContracts,
                string unsupportedContractReasons)
            {
                this.levelHeight = levelHeight;
                this.floor = floor;
                this.suspendedFloor = suspendedFloor;
                this.connectionPointStraightStair = connectionPointStraightStair;
                this.connectionPointVariantStairs = connectionPointVariantStairs;
                this.dropFaceStack = dropFaceStack;
                this.cornerStack = cornerStack;
                this.railings = railings;
                this.partitions = partitions;
                this.gateways = gateways;
                this.rejectedContracts = rejectedContracts;
                this.rejectedContractReasons = rejectedContractReasons ?? "[]";
                this.unsupportedContracts = unsupportedContracts;
                this.unsupportedContractReasons = unsupportedContractReasons ?? "[]";
            }
        }

        private readonly struct ActiveStairContractCatalog
        {
            public readonly float levelHeight;
            public readonly List<ConnectionPointSetPieceContract> contracts;
            public readonly int rejectedContracts;
            public readonly string rejectedContractReasons;
            public readonly int unsupportedContracts;
            public readonly string unsupportedContractReasons;

            public ActiveStairContractCatalog(
                float levelHeight,
                List<ConnectionPointSetPieceContract> contracts,
                int rejectedContracts,
                string rejectedContractReasons,
                int unsupportedContracts,
                string unsupportedContractReasons)
            {
                this.levelHeight = levelHeight;
                this.contracts = contracts;
                this.rejectedContracts = rejectedContracts;
                this.rejectedContractReasons = rejectedContractReasons ?? "[]";
                this.unsupportedContracts = unsupportedContracts;
                this.unsupportedContractReasons = unsupportedContractReasons ?? "[]";
            }
        }

        private readonly struct PartitionWallContracts
        {
            public readonly MeasuredPrefab mediumWall;
            public readonly MeasuredPrefab largeWall;
            public readonly MeasuredPrefab mediumCorner;
            public readonly MeasuredPrefab largeCorner;

            public PartitionWallContracts(
                MeasuredPrefab mediumWall,
                MeasuredPrefab largeWall,
                MeasuredPrefab mediumCorner,
                MeasuredPrefab largeCorner)
            {
                this.mediumWall = mediumWall;
                this.largeWall = largeWall;
                this.mediumCorner = mediumCorner;
                this.largeCorner = largeCorner;
            }

            public MeasuredPrefab ForHeight(int heightUnits)
            {
                return heightUnits >= 6 ? largeWall : mediumWall;
            }

            public MeasuredPrefab CornerForHeight(int heightUnits)
            {
                return heightUnits >= 6 ? largeCorner : mediumCorner;
            }
        }

        private enum GatewayStyle
        {
            Metal,
            Wood,
            LargeWood,
            Barred,
            OpenArch
        }

        private readonly struct GatewayContract
        {
            public readonly GatewayStyle style;
            public readonly int wallHeightUnits;
            public readonly MeasuredPrefab prefab;
            public readonly string auxiliaryPrefabPath;

            public GatewayContract(
                GatewayStyle style,
                int wallHeightUnits,
                MeasuredPrefab prefab,
                string auxiliaryPrefabPath = "")
            {
                this.style = style;
                this.wallHeightUnits = wallHeightUnits;
                this.prefab = prefab;
                this.auxiliaryPrefabPath = auxiliaryPrefabPath ?? string.Empty;
            }
        }

        private readonly struct GatewayContracts
        {
            private readonly MeasuredPrefab mediumMetal;
            private readonly MeasuredPrefab largeWallMetal;
            private readonly MeasuredPrefab mediumWood;
            private readonly MeasuredPrefab largeWallWood;
            private readonly MeasuredPrefab largeWood;
            private readonly string barsPrefabPath;
            public readonly float socketWidth;

            public GatewayContracts(
                MeasuredPrefab mediumMetal,
                MeasuredPrefab largeWallMetal,
                MeasuredPrefab mediumWood,
                MeasuredPrefab largeWallWood,
                MeasuredPrefab largeWood,
                string barsPrefabPath)
            {
                this.mediumMetal = mediumMetal;
                this.largeWallMetal = largeWallMetal;
                this.mediumWood = mediumWood;
                this.largeWallWood = largeWallWood;
                this.largeWood = largeWood;
                this.barsPrefabPath = barsPrefabPath ?? string.Empty;
                socketWidth = Vector2.Distance(
                    mediumMetal.localSegmentStart,
                    mediumMetal.localSegmentEnd);
                ValidateSocketWidth(largeWallMetal);
                ValidateSocketWidth(mediumWood);
                ValidateSocketWidth(largeWallWood);
                ValidateSocketWidth(largeWood);
            }

            private void ValidateSocketWidth(MeasuredPrefab prefab)
            {
                float width = Vector2.Distance(
                    prefab.localSegmentStart,
                    prefab.localSegmentEnd);
                if (Mathf.Abs(width - socketWidth) > 0.01f)
                {
                    throw new InvalidOperationException(
                        $"Gateway prefab '{prefab.name}' has a {width:0.###}u socket but the gateway family requires {socketWidth:0.###}u.");
                }
            }

            public GatewayContract For(GatewayStyle style, int wallHeightUnits)
            {
                bool largeWall = wallHeightUnits == 6;
                if (!largeWall && wallHeightUnits != 4)
                {
                    throw new InvalidOperationException(
                        $"Gateway selection requires equal 4u or 6u flanking walls; received {wallHeightUnits}u.");
                }

                switch (style)
                {
                    case GatewayStyle.Metal:
                        return new GatewayContract(
                            style,
                            wallHeightUnits,
                            largeWall ? largeWallMetal : mediumMetal);
                    case GatewayStyle.Wood:
                        return new GatewayContract(
                            style,
                            wallHeightUnits,
                            largeWall ? largeWallWood : mediumWood);
                    case GatewayStyle.LargeWood:
                        if (!largeWall)
                        {
                            throw new InvalidOperationException(
                                "The double-leaf large wood gateway requires 6u flanking walls.");
                        }

                        return new GatewayContract(style, wallHeightUnits, largeWood);
                    case GatewayStyle.Barred:
                        return new GatewayContract(
                            style,
                            wallHeightUnits,
                            largeWall ? largeWallMetal : mediumMetal,
                            barsPrefabPath);
                    case GatewayStyle.OpenArch:
                        return new GatewayContract(
                            style,
                            wallHeightUnits,
                            largeWall ? largeWallMetal : mediumMetal);
                    default:
                        throw new ArgumentOutOfRangeException(nameof(style), style, null);
                }
            }
        }

        private sealed class RawOuterShellPlan
        {
            public readonly HashSet<Vector2Int> exterior;
            public readonly int minLevel;
            public readonly int maxLevel;
            public readonly Dictionary<int, int> roomArea;
            public readonly HashSet<
                (Vector2Int cell, int direction, int higherLevel)>
                orphanLandingEdges;
            public readonly Dictionary<
                (int x, int z, int direction),
                int[]> straightCourseHeights;
            public readonly HashSet<int> largeWallRooms;
            public readonly RoomBoundaryContext roomBoundaryContext;

            public RawOuterShellPlan(
                HashSet<Vector2Int> exterior,
                int minLevel,
                int maxLevel,
                Dictionary<int, int> roomArea,
                HashSet<
                    (Vector2Int cell, int direction, int higherLevel)>
                    orphanLandingEdges,
                Dictionary<
                    (int x, int z, int direction),
                    int[]> straightCourseHeights,
                HashSet<int> largeWallRooms,
                RoomBoundaryContext roomBoundaryContext)
            {
                this.exterior = exterior ?? new HashSet<Vector2Int>();
                this.minLevel = minLevel;
                this.maxLevel = maxLevel;
                this.roomArea = roomArea ?? new Dictionary<int, int>();
                this.orphanLandingEdges =
                    orphanLandingEdges ??
                    new HashSet<
                        (Vector2Int cell, int direction, int higherLevel)>();
                this.straightCourseHeights =
                    straightCourseHeights ??
                    new Dictionary<
                        (int x, int z, int direction),
                        int[]>();
                this.largeWallRooms =
                    largeWallRooms ?? new HashSet<int>();
                this.roomBoundaryContext = roomBoundaryContext;
            }
        }

        private readonly struct GatewayWallSupport
        {
            public readonly (int x, int z, int direction) edge;

            public GatewayWallSupport(
                (int x, int z, int direction) edge)
            {
                this.edge = edge;
            }
        }

        private sealed class GatewayWallPlan
        {
            public readonly Dictionary<string, GatewayWallSupport> supports;
            public readonly Dictionary<
                string,
                (int baseLevel, int heightUnits)> supportHeights;

            public GatewayWallPlan(
                Dictionary<string, GatewayWallSupport> supports,
                Dictionary<
                    string,
                    (int baseLevel, int heightUnits)> supportHeights)
            {
                this.supports =
                    supports ??
                    new Dictionary<string, GatewayWallSupport>(
                        StringComparer.Ordinal);
                this.supportHeights =
                    supportHeights ??
                    new Dictionary<
                        string,
                        (int baseLevel, int heightUnits)>(
                        StringComparer.Ordinal);
            }
        }

        private readonly struct GatewaySocketCandidate
        {
            public readonly PlatformEdge edge;
            public readonly int floorLevel;
            public readonly int wallHeightUnits;
            public readonly string edgeKey;
            public readonly string firstFlankKey;
            public readonly string secondFlankKey;
            public readonly int pathDistance;

            public GatewaySocketCandidate(
                PlatformEdge edge,
                int floorLevel,
                int wallHeightUnits,
                string edgeKey,
                string firstFlankKey,
                string secondFlankKey,
                int pathDistance)
            {
                this.edge = edge;
                this.floorLevel = floorLevel;
                this.wallHeightUnits = wallHeightUnits;
                this.edgeKey = edgeKey ?? string.Empty;
                this.firstFlankKey = firstFlankKey ?? string.Empty;
                this.secondFlankKey = secondFlankKey ?? string.Empty;
                this.pathDistance = pathDistance;
            }
        }

        private readonly struct GatewaySocket
        {
            public readonly int endIndex;
            public readonly PlatformEdge edge;
            public readonly int floorLevel;
            public readonly int wallHeightUnits;
            public readonly string edgeKey;
            public readonly string firstFlankKey;
            public readonly (int x, int z, int direction) firstFlankEdge;
            public readonly string secondFlankKey;
            public readonly (int x, int z, int direction) secondFlankEdge;
            public readonly string selectionGroup;
            public readonly int selectionScore;

            public GatewaySocket(
                int endIndex,
                PlatformEdge edge,
                int floorLevel,
                int wallHeightUnits,
                string edgeKey,
                string firstFlankKey,
                (int x, int z, int direction) firstFlankEdge,
                string secondFlankKey,
                (int x, int z, int direction) secondFlankEdge,
                string selectionGroup,
                int selectionScore)
            {
                this.endIndex = endIndex;
                this.edge = edge;
                this.floorLevel = floorLevel;
                this.wallHeightUnits = wallHeightUnits;
                this.edgeKey = edgeKey ?? string.Empty;
                this.firstFlankKey = firstFlankKey ?? string.Empty;
                this.firstFlankEdge = firstFlankEdge;
                this.secondFlankKey = secondFlankKey ?? string.Empty;
                this.secondFlankEdge = secondFlankEdge;
                this.selectionGroup = selectionGroup ?? string.Empty;
                this.selectionScore = selectionScore;
            }
        }

        private sealed class GatewaySocketPlan
        {
            public readonly IReadOnlyList<GatewaySocket> sockets;

            public GatewaySocketPlan(
                IReadOnlyList<GatewaySocket> sockets)
            {
                var socketCopy = sockets != null
                    ? new List<GatewaySocket>(sockets)
                    : new List<GatewaySocket>();
                this.sockets = socketCopy.AsReadOnly();
            }
        }

        private readonly struct GatewayPlacement
        {
            public readonly PlatformEdge edge;
            public readonly int floorLevel;
            public readonly int wallHeightUnits;
            public readonly GatewayContract contract;
            public readonly MeasuredPrefab headerPrefab;
            public readonly int headerHeightUnits;

            public GatewayPlacement(
                PlatformEdge edge,
                int floorLevel,
                int wallHeightUnits,
                GatewayContract contract,
                MeasuredPrefab headerPrefab,
                int headerHeightUnits)
            {
                if (contract.wallHeightUnits + headerHeightUnits != wallHeightUnits ||
                    (headerHeightUnits != 0 &&
                     headerHeightUnits != 4 &&
                     headerHeightUnits != 6))
                {
                    throw new InvalidOperationException(
                        $"Gateway assembly {contract.wallHeightUnits}u + {headerHeightUnits}u did not match its {wallHeightUnits}u flanking walls.");
                }

                this.edge = edge;
                this.floorLevel = floorLevel;
                this.wallHeightUnits = wallHeightUnits;
                this.contract = contract;
                this.headerPrefab = headerPrefab;
                this.headerHeightUnits = headerHeightUnits;
            }
        }

        private readonly struct GatewayLeafPose
        {
            public readonly Transform leaf;
            public readonly Quaternion closedLocalRotation;
            public readonly Quaternion openLocalRotation;

            public GatewayLeafPose(
                Transform leaf,
                Quaternion closedLocalRotation,
                Quaternion openLocalRotation)
            {
                this.leaf = leaf;
                this.closedLocalRotation = closedLocalRotation;
                this.openLocalRotation = openLocalRotation;
            }
        }


        private readonly struct ReviewedSourceRootPose
        {
            public readonly string sourcePrefab;
            public readonly Vector3 localPosition;
            public readonly float localYawDegrees;

            public ReviewedSourceRootPose(string sourcePrefab, Vector3 localPosition, float localYawDegrees)
            {
                this.sourcePrefab = sourcePrefab ?? string.Empty;
                this.localPosition = localPosition;
                this.localYawDegrees = localYawDegrees;
            }
        }

        private readonly struct ReviewedSourceRootTransform
        {
            public readonly Vector3 localPosition;
            public readonly Quaternion localRotation;

            public ReviewedSourceRootTransform(Vector3 localPosition, Quaternion localRotation)
            {
                this.localPosition = localPosition;
                this.localRotation = localRotation;
            }
        }

        private readonly struct ConnectionPointSetPieceContract
        {
            public readonly string prefabPath;
            public readonly string name;
            public readonly PlanBounds localPlanBounds;
            public readonly Bounds localBounds;
            public readonly Bounds renderLocalBounds;
            public readonly ConnectionPoint[] points;
            public readonly ConnectionPoint entry;
            public readonly ConnectionPoint exit;
            public readonly int riseLevels;
            public readonly Vector2Int[] floorBlockedCells;
            public readonly string[] exitSurfaceRootSources;
            public readonly Vector3[] reviewedVisualAnchorPositions;
            public readonly ReviewedSourceRootPose[] reviewedSourceRootPoses;
            public readonly bool isBridge;

            public ConnectionPointSetPieceContract(
                string prefabPath,
                string name,
                PlanBounds localPlanBounds,
                Bounds localBounds,
                Bounds renderLocalBounds,
                ConnectionPoint[] points,
                ConnectionPoint entry,
                ConnectionPoint exit,
                int riseLevels)
                : this(
                    prefabPath,
                    name,
                    localPlanBounds,
                    localBounds,
                    renderLocalBounds,
                    points,
                    entry,
                    exit,
                    riseLevels,
                    Array.Empty<Vector2Int>(),
                    Array.Empty<string>(),
                    Array.Empty<Vector3>(),
                    Array.Empty<ReviewedSourceRootPose>())
            {
            }

            public ConnectionPointSetPieceContract(
                string prefabPath,
                string name,
                PlanBounds localPlanBounds,
                Bounds localBounds,
                Bounds renderLocalBounds,
                ConnectionPoint[] points,
                ConnectionPoint entry,
                ConnectionPoint exit,
                int riseLevels,
                Vector2Int[] floorBlockedCells,
                string[] exitSurfaceRootSources)
                : this(
                    prefabPath,
                    name,
                    localPlanBounds,
                    localBounds,
                    renderLocalBounds,
                    points,
                    entry,
                    exit,
                    riseLevels,
                    floorBlockedCells,
                    exitSurfaceRootSources,
                    Array.Empty<Vector3>(),
                    Array.Empty<ReviewedSourceRootPose>(),
                    isBridge: false)
            {
            }

            public ConnectionPointSetPieceContract(
                string prefabPath,
                string name,
                PlanBounds localPlanBounds,
                Bounds localBounds,
                Bounds renderLocalBounds,
                ConnectionPoint[] points,
                ConnectionPoint entry,
                ConnectionPoint exit,
                int riseLevels,
                Vector2Int[] floorBlockedCells,
                string[] exitSurfaceRootSources,
                Vector3[] reviewedVisualAnchorPositions)
                : this(
                    prefabPath,
                    name,
                    localPlanBounds,
                    localBounds,
                    renderLocalBounds,
                    points,
                    entry,
                    exit,
                    riseLevels,
                    floorBlockedCells,
                    exitSurfaceRootSources,
                    reviewedVisualAnchorPositions,
                    Array.Empty<ReviewedSourceRootPose>(),
                    isBridge: false)
            {
            }

            public ConnectionPointSetPieceContract(
                string prefabPath,
                string name,
                PlanBounds localPlanBounds,
                Bounds localBounds,
                Bounds renderLocalBounds,
                ConnectionPoint[] points,
                ConnectionPoint entry,
                ConnectionPoint exit,
                int riseLevels,
                Vector2Int[] floorBlockedCells,
                string[] exitSurfaceRootSources,
                Vector3[] reviewedVisualAnchorPositions,
                ReviewedSourceRootPose[] reviewedSourceRootPoses)
                : this(
                    prefabPath,
                    name,
                    localPlanBounds,
                    localBounds,
                    renderLocalBounds,
                    points,
                    entry,
                    exit,
                    riseLevels,
                    floorBlockedCells,
                    exitSurfaceRootSources,
                    reviewedVisualAnchorPositions,
                    reviewedSourceRootPoses,
                    isBridge: false)
            {
            }

            public ConnectionPointSetPieceContract(
                string prefabPath,
                string name,
                PlanBounds localPlanBounds,
                Bounds localBounds,
                Bounds renderLocalBounds,
                ConnectionPoint[] points,
                ConnectionPoint entry,
                ConnectionPoint exit,
                int riseLevels,
                Vector2Int[] floorBlockedCells,
                string[] exitSurfaceRootSources,
                Vector3[] reviewedVisualAnchorPositions,
                ReviewedSourceRootPose[] reviewedSourceRootPoses,
                bool isBridge)
            {
                this.prefabPath = prefabPath;
                this.name = name;
                this.localPlanBounds = localPlanBounds;
                this.localBounds = localBounds;
                this.renderLocalBounds = renderLocalBounds;
                this.points = points;
                this.entry = entry;
                this.exit = exit;
                this.riseLevels = riseLevels;
                this.floorBlockedCells = floorBlockedCells ?? Array.Empty<Vector2Int>();
                this.exitSurfaceRootSources = exitSurfaceRootSources ?? Array.Empty<string>();
                this.reviewedVisualAnchorPositions = reviewedVisualAnchorPositions ?? Array.Empty<Vector3>();
                this.reviewedSourceRootPoses = reviewedSourceRootPoses ?? Array.Empty<ReviewedSourceRootPose>();
                this.isBridge = isBridge;
            }
        }

        private readonly struct StairReservationSet
        {
            public readonly HashSet<Vector2Int> floorBlockedCells;

            // Footprint cells of externalSpan (bridge) stairs: the deck floats, so these
            // cells never generate their own wall edges (their neighbors provide the
            // shell, and the deck's connection faces come from bridge port edges).
            public readonly HashSet<Vector2Int> bridgeFloorBlockedCells;

            public StairReservationSet(HashSet<Vector2Int> floorBlockedCells, HashSet<Vector2Int> bridgeFloorBlockedCells)
            {
                this.floorBlockedCells = floorBlockedCells ?? new HashSet<Vector2Int>();
                this.bridgeFloorBlockedCells = bridgeFloorBlockedCells ?? new HashSet<Vector2Int>();
            }
        }

        private readonly struct ConnectionPoint
        {
            public readonly Vector2Int localCell;
            public readonly int direction;
            public readonly int level;
            public readonly string role;
            public readonly Vector2Int[] spanCells;
            public int spanCellCount => spanCells == null || spanCells.Length == 0 ? 1 : spanCells.Length;
            public readonly bool hasLocalPoint;
            public readonly Vector3 localPoint;

            public ConnectionPoint(Vector2Int localCell, int direction, int level, string role)
            {
                this.localCell = localCell;
                this.direction = direction;
                this.level = level;
                this.role = role;
                spanCells = new[] { localCell };
                hasLocalPoint = false;
                localPoint = Vector3.zero;
            }

            public ConnectionPoint(
                Vector2Int localCell,
                int direction,
                int level,
                string role,
                Vector2Int[] spanCells,
                bool hasLocalPoint,
                Vector3 localPoint)
            {
                this.localCell = localCell;
                this.direction = direction;
                this.level = level;
                this.role = role;
                this.spanCells = spanCells == null || spanCells.Length == 0 ? new[] { localCell } : spanCells;
                this.hasLocalPoint = hasLocalPoint;
                this.localPoint = localPoint;
            }

            public override string ToString()
            {
                string pointSuffix = hasLocalPoint ? $" local {Format(localPoint)}" : string.Empty;
                string spanSuffix = spanCellCount > 1 ? $" span {spanCellCount}" : string.Empty;
                return $"{role}@L{level}:{DirectionName(direction)}[{localCell.x},{localCell.y}]{spanSuffix}{pointSuffix}";
            }
        }

        private readonly struct ConnectionPointPlacement
        {
            public readonly Vector3 position;
            public readonly float yRotation;
            public readonly Vector3 localEntryPoint;
            public readonly Vector3 localExitPoint;
            public readonly Vector3 worldEntryTarget;
            public readonly Vector3 worldExitTarget;

            public ConnectionPointPlacement(
                Vector3 position,
                float yRotation,
                Vector3 localEntryPoint,
                Vector3 localExitPoint,
                Vector3 worldEntryTarget,
                Vector3 worldExitTarget)
            {
                this.position = position;
                this.yRotation = yRotation;
                this.localEntryPoint = localEntryPoint;
                this.localExitPoint = localExitPoint;
                this.worldEntryTarget = worldEntryTarget;
                this.worldExitTarget = worldExitTarget;
            }
        }


        private readonly struct DropFaceStack
        {
            public readonly MeasuredPrefab[] pieces;
            public readonly float totalHeight;
            public string Description => StackDescription(pieces);

            public DropFaceStack(MeasuredPrefab[] pieces, float totalHeight)
            {
                this.pieces = pieces;
                this.totalHeight = totalHeight;
            }
        }

        private readonly struct RailingContracts
        {
            public readonly MeasuredPrefab railing;
            public readonly MeasuredPrefab column;
            public readonly RailingAuthoredOffsets authored;

            public RailingContracts(MeasuredPrefab railing, MeasuredPrefab column, RailingAuthoredOffsets authored)
            {
                this.railing = railing;
                this.column = column;
                this.authored = authored;
            }
        }

        private readonly struct RailingAuthoredOffsets
        {
            public readonly RelativeTransform railing;
            public readonly int baseSide;
            public readonly RelativeTransform startColumn;
            public readonly RelativeTransform endColumn;
            public readonly Vector3 startColumnOffset;
            public readonly Vector3 endColumnOffset;

            public RailingAuthoredOffsets(
                RelativeTransform railing,
                int baseSide,
                RelativeTransform startColumn,
                RelativeTransform endColumn,
                Vector3 startColumnOffset,
                Vector3 endColumnOffset)
            {
                this.railing = railing;
                this.baseSide = baseSide;
                this.startColumn = startColumn;
                this.endColumn = endColumn;
                this.startColumnOffset = startColumnOffset;
                this.endColumnOffset = endColumnOffset;
            }
        }

        private readonly struct RailingCandidate
        {
            public readonly RelativeTransform relative;
            public readonly Vector2 start;
            public readonly Vector2 end;
            public readonly int side;

            public RailingCandidate(RelativeTransform relative, Vector2 start, Vector2 end, int side)
            {
                this.relative = relative;
                this.start = start;
                this.end = end;
                this.side = side;
            }
        }

        private readonly struct RelativeTransform
        {
            public readonly Vector3 position;
            public readonly Quaternion rotation;

            public RelativeTransform(Vector3 position, Quaternion rotation)
            {
                this.position = position;
                this.rotation = rotation;
            }
        }

        private readonly struct MeasuredPrefab
        {
            public readonly string name;
            public readonly string prefabPath;
            public readonly PrefabRole role;
            public readonly PlanBounds localPlanBounds;
            public readonly Vector2 localSegmentStart;
            public readonly Vector2 localSegmentEnd;
            public readonly Vector2 faceNormal;
            public readonly float height;
            public readonly float localTopY;
            public readonly int baseQuadrant;

            public MeasuredPrefab(
                string name,
                string prefabPath,
                PrefabRole role,
                PlanBounds localPlanBounds,
                Vector2 localSegmentStart,
                Vector2 localSegmentEnd,
                Vector2 faceNormal,
                float height,
                float localTopY,
                int baseQuadrant)
            {
                this.name = name;
                this.prefabPath = prefabPath;
                this.role = role;
                this.localPlanBounds = localPlanBounds;
                this.localSegmentStart = localSegmentStart;
                this.localSegmentEnd = localSegmentEnd;
                this.faceNormal = faceNormal;
                this.height = height;
                this.localTopY = localTopY;
                this.baseQuadrant = baseQuadrant;
            }

            public MeasuredPrefab WithName(string newName)
            {
                return new MeasuredPrefab(newName, prefabPath, role, localPlanBounds, localSegmentStart, localSegmentEnd, faceNormal, height, localTopY, baseQuadrant);
            }

            public MeasuredPrefab WithAttachmentSegment(
                Vector2 segmentStart,
                Vector2 segmentEnd,
                Vector2 segmentFaceNormal)
            {
                Vector2 direction = segmentEnd - segmentStart;
                if (direction.sqrMagnitude <= 0.0001f ||
                    segmentFaceNormal.sqrMagnitude <= 0.0001f ||
                    Mathf.Abs(Vector2.Dot(
                        direction.normalized,
                        segmentFaceNormal.normalized)) > 0.01f)
                {
                    throw new InvalidOperationException(
                        $"Prefab '{name}' cannot use an invalid boundary attachment segment.");
                }

                return new MeasuredPrefab(
                    name,
                    prefabPath,
                    role,
                    localPlanBounds,
                    segmentStart,
                    segmentEnd,
                    segmentFaceNormal.normalized,
                    height,
                    localTopY,
                    baseQuadrant);
            }
        }

        private readonly struct PlanBounds
        {
            public readonly float minX;
            public readonly float maxX;
            public readonly float minZ;
            public readonly float maxZ;

            public Vector2 Min => new Vector2(minX, minZ);
            public Vector2 Center => new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
            public Vector2 Size => new Vector2(maxX - minX, maxZ - minZ);

            public PlanBounds(float minX, float maxX, float minZ, float maxZ)
            {
                this.minX = minX;
                this.maxX = maxX;
                this.minZ = minZ;
                this.maxZ = maxZ;
            }
        }

        private readonly struct PlatformEdge
        {
            public readonly int x;
            public readonly int z;
            public readonly int direction;

            public PlatformEdge(int x, int z, int direction)
            {
                this.x = x;
                this.z = z;
                this.direction = direction;
            }
        }

        /// <summary>
        /// A guarded lateral edge of ONE surface: <c>(x, z, level, direction)</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the level discriminator §7.1 asks for, at the one place it is
        /// actually load-bearing. A wall face already carries its own extent
        /// (<c>WallEdge.lowerLevel/higherLevel</c>), but a railing-only edge did
        /// not: the height it was placed at came from looking the cell up in the
        /// level field (<c>levels[cell]</c>), which is a column query and can
        /// only ever answer for the column floor. A gallery rim eight levels up
        /// would have been railed at the chamber's height.
        /// </para>
        /// <para>
        /// Every existing producer already had the level in hand and now states
        /// it, so the value placed is identical; the type simply stops throwing
        /// the answer away and re-deriving it.
        /// </para>
        /// </remarks>
        private readonly struct RimEdge
        {
            public readonly PlatformEdge edge;
            public readonly int level;
            // C1b: a rim the plan declared BARE — the aperture case (design §5).
            // Distinct from suppression, which is about another piece owning the
            // guard; this is about the plan wanting the drop open.
            public readonly bool bare;

            public RimEdge(PlatformEdge edge, int level)
                : this(edge, level, bare: false)
            {
            }

            public RimEdge(PlatformEdge edge, int level, bool bare)
            {
                this.edge = edge;
                this.level = level;
                this.bare = bare;
            }
        }

        /// <summary>
        /// The renderer's view of a plan's surfaces: a column floor per cell,
        /// plus whatever is stacked over it (design §3.1).
        /// </summary>
        /// <remarks>
        /// <para>
        /// WHAT THIS IS NOT. It is not a replacement for the level field, and
        /// the boundary walk does not consult it for mass. §7.1's band table
        /// gives a suspended surface no structural band at all — the owner's
        /// fascia ruling made the 0.5u underside the <c>_E_</c> slab's own
        /// geometry rather than a wall face — so the only band a column has is
        /// the ground band under its floor, which is exactly what
        /// <c>levels[cell]</c> already is. Stacking therefore does not change
        /// one wall face, and that is a property of the band model, not luck.
        /// </para>
        /// <para>
        /// WHAT IT IS FOR: the three things a surface above the floor needs that
        /// a column cannot answer — its own floor tile, its own rim guards, and
        /// its own <c>IsGroundBacked</c>.
        /// </para>
        /// </remarks>
        private sealed class SurfaceColumns
        {
            private readonly IReadOnlyDictionary<Vector2Int, int> columnFloors;
            private readonly Dictionary<Vector2Int, List<StackedSurface>> above;

            public SurfaceColumns(
                IReadOnlyDictionary<Vector2Int, int> columnFloors,
                IReadOnlyCollection<StackedSurface> stackedSurfaces)
            {
                this.columnFloors = columnFloors;
                if (stackedSurfaces == null || stackedSurfaces.Count == 0)
                {
                    above = null;
                    return;
                }

                above = new Dictionary<Vector2Int, List<StackedSurface>>();
                foreach (StackedSurface surface in stackedSurfaces)
                {
                    // A span deck is a walkable surface in the PLAN and authored
                    // set-piece geometry in the SCENE: the transition prefab
                    // already carries its walk slab, its railings and its
                    // underside caps. Drawing a floor tile and rim guards over it
                    // here would be a second deck in the same place. It is also
                    // the one surface that legitimately stands in a column with no
                    // floor — that is what a bridge over a true gap is — so this
                    // skip has to come before the floor check below.
                    if (surface.kind == SurfaceKind.Deck)
                    {
                        continue;
                    }

                    if (!columnFloors.TryGetValue(surface.cell, out int floorLevel))
                    {
                        throw new InvalidOperationException(
                            $"Stacked surface {surface.cell} L{surface.level} has no column floor beneath it. " +
                            "The level field is the column FLOOR; a surface that is alone in its column belongs there.");
                    }

                    if (surface.level <= floorLevel)
                    {
                        throw new InvalidOperationException(
                            $"Stacked surface {surface.cell} L{surface.level} is not above its column floor (L{floorLevel}).");
                    }

                    if (!above.TryGetValue(surface.cell, out List<StackedSurface> column))
                    {
                        column = new List<StackedSurface>(1);
                        above[surface.cell] = column;
                    }

                    column.Add(surface);
                }

                if (above.Count == 0)
                {
                    // Every stacked surface was a deck. Nothing to draw above a
                    // column floor, so this is the single-layer path — stated
                    // rather than left to an empty dictionary reading as stacked.
                    above = null;
                    return;
                }

                foreach (List<StackedSurface> column in above.Values)
                {
                    column.Sort((first, second) => first.level.CompareTo(second.level));
                }
            }

            /// <summary>True while nothing the renderer draws stands above a column floor.</summary>
            public bool IsSingleLayer => above == null;

            /// <summary>The surfaces stacked over one column's floor, ascending.</summary>
            public IReadOnlyList<StackedSurface> Above(Vector2Int cell)
            {
                if (above != null && above.TryGetValue(cell, out List<StackedSurface> column))
                {
                    return column;
                }

                return Array.Empty<StackedSurface>();
            }

            /// <summary>Every stacked surface, in canonical (x, y, level) order.</summary>
            public List<StackedSurface> AllAbove()
            {
                var all = new List<StackedSurface>();
                if (above == null)
                {
                    return all;
                }

                foreach (List<StackedSurface> column in above.Values)
                {
                    all.AddRange(column);
                }

                all.Sort((first, second) =>
                {
                    int byX = first.cell.x.CompareTo(second.cell.x);
                    if (byX != 0)
                    {
                        return byX;
                    }

                    int byY = first.cell.y.CompareTo(second.cell.y);
                    return byY != 0 ? byY : first.level.CompareTo(second.level);
                });
                return all;
            }

            /// <summary>Is there a walkable surface at exactly this level here?</summary>
            public bool HasSurfaceAt(Vector2Int cell, int level)
            {
                if (columnFloors.TryGetValue(cell, out int floorLevel) && floorLevel == level)
                {
                    return true;
                }

                foreach (StackedSurface surface in Above(cell))
                {
                    if (surface.level == level)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private readonly struct CornerPlacement
        {
            public readonly Vector2Int vertex;
            public readonly int targetQuadrant;
            public readonly int baseLevel;
            public readonly int dropLevels;
            public readonly bool isPartition;
            public readonly int partitionHeightUnits;

            public CornerPlacement(
                Vector2Int vertex,
                int targetQuadrant,
                int baseLevel,
                int dropLevels,
                bool isPartition,
                int partitionHeightUnits)
            {
                this.vertex = vertex;
                this.targetQuadrant = targetQuadrant;
                this.baseLevel = baseLevel;
                this.dropLevels = dropLevels;
                this.isPartition = isPartition;
                this.partitionHeightUnits = partitionHeightUnits;
            }
        }

        private readonly struct CornerTurnCandidate
        {
            public readonly WallEdge edge;
            public readonly Vector2Int direction;

            public CornerTurnCandidate(WallEdge edge, Vector2Int direction)
            {
                this.edge = edge;
                this.direction = direction;
            }
        }

        private struct FaceNormalBuckets
        {
            private float positiveXArea;
            private float negativeXArea;
            private float positiveZArea;
            private float negativeZArea;
            private float positiveXOffset;
            private float negativeXOffset;
            private float positiveZOffset;
            private float negativeZOffset;
            private Vector2 signedArea;

            public void Add(Vector2 normal, Vector2 center, float area)
            {
                signedArea += normal * area;
                if (Mathf.Abs(normal.x) >= Mathf.Abs(normal.y))
                {
                    if (normal.x >= 0f)
                    {
                        positiveXArea += area;
                        positiveXOffset += center.x * area;
                    }
                    else
                    {
                        negativeXArea += area;
                        negativeXOffset += center.x * area;
                    }
                }
                else if (normal.y >= 0f)
                {
                    positiveZArea += area;
                    positiveZOffset += center.y * area;
                }
                else
                {
                    negativeZArea += area;
                    negativeZOffset += center.y * area;
                }
            }

            public bool TryGetDominant(out Vector2 normal)
            {
                float xArea = positiveXArea + negativeXArea;
                float zArea = positiveZArea + negativeZArea;
                if (xArea <= 0.0001f && zArea <= 0.0001f)
                {
                    normal = Vector2.zero;
                    return false;
                }

                bool useX = xArea > zArea;
                float positiveArea = useX ? positiveXArea : positiveZArea;
                float negativeArea = useX ? negativeXArea : negativeZArea;
                float sign = ChooseDominantSign(
                    positiveArea,
                    negativeArea,
                    useX ? signedArea.x : signedArea.y,
                    useX ? positiveXOffset : positiveZOffset,
                    useX ? negativeXOffset : negativeZOffset);

                normal = useX ? new Vector2(sign, 0f) : new Vector2(0f, sign);
                return true;
            }

            private static float ChooseDominantSign(
                float positiveArea,
                float negativeArea,
                float signedArea,
                float positiveOffset,
                float negativeOffset)
            {
                float maxArea = Mathf.Max(positiveArea, negativeArea);
                if (maxArea <= 0.0001f)
                {
                    return 1f;
                }

                if (positiveArea > negativeArea + maxArea * 0.05f)
                {
                    return 1f;
                }

                if (negativeArea > positiveArea + maxArea * 0.05f)
                {
                    return -1f;
                }

                if (Mathf.Abs(signedArea) > maxArea * 0.01f)
                {
                    return Mathf.Sign(signedArea);
                }

                float positiveCenter = positiveArea > 0.0001f ? positiveOffset / positiveArea : float.NegativeInfinity;
                float negativeCenter = negativeArea > 0.0001f ? negativeOffset / negativeArea : float.PositiveInfinity;
                return positiveCenter >= -negativeCenter ? 1f : -1f;
            }
        }

        private static class Direction
        {
            public const int North = 1;
            public const int East = 2;
            public const int South = 4;
            public const int West = 8;
            public const int Up = 16;
            public const int Down = 32;
        }

        private static class CornerQuadrant
        {
            public const int NorthEast = 0;
            public const int SouthEast = 1;
            public const int SouthWest = 2;
            public const int NorthWest = 3;
        }

        private sealed class PackageInventory
        {
            private readonly Dictionary<string, string> prefabPaths;

            private PackageInventory(Dictionary<string, string> prefabPaths)
            {
                this.prefabPaths = prefabPaths;
            }

            public static PackageInventory Load()
            {
                if (!File.Exists(PackageInventoryPath))
                {
                    throw new InvalidOperationException($"Missing package inventory at '{PackageInventoryPath}'.");
                }

                string json = File.ReadAllText(PackageInventoryPath);
                var wrapper = JsonUtility.FromJson<InventoryWrapper>($"{{\"items\":{json}}}");
                if (wrapper == null || wrapper.items == null)
                {
                    throw new InvalidOperationException($"Could not parse package inventory at '{PackageInventoryPath}'.");
                }

                var paths = new Dictionary<string, string>();
                foreach (InventoryRecord item in wrapper.items)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.name) || string.IsNullOrWhiteSpace(item.path))
                    {
                        continue;
                    }

                    paths[item.name] = PackageAssetRoot + item.path;
                }

                return new PackageInventory(paths);
            }

            public string GetPrefabPath(string prefabName)
            {
                if (!prefabPaths.TryGetValue(prefabName, out string prefabPath))
                {
                    throw new InvalidOperationException($"Prefab '{prefabName}' was not found in {PackageInventoryPath}.");
                }

                return prefabPath;
            }

            public bool TryGetPrefabPath(string prefabName, out string prefabPath)
            {
                return prefabPaths.TryGetValue(prefabName, out prefabPath);
            }
        }

        [Serializable]
        private sealed class InventoryWrapper
        {
            public InventoryRecord[] items;
        }

        [Serializable]
        private sealed class InventoryRecord
        {
            public string name;
            public string path;
        }
    }
}

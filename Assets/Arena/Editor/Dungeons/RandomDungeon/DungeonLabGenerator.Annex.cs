using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // M3 of the density-scale design (§4.2): ANNEX the vacant lattice cells.
    //
    // §2 measured the void as two roughly equal halves. M2 (pack) closed the
    // first — the channel of open air ringing every room inside its own 9x9
    // envelope. This closes the second: the ~45% of the lattice box that is
    // inside NO node's envelope at all, because the topology map has no node
    // there. Those are the `.` tokens in a topology map, and at a 9-cell pitch
    // each one is a 9x9 crater — 36x the tolerance density 5 is accepted on.
    //
    // The mechanism is deliberately the smallest one that works: a vacant cell
    // is claimed by an ADJACENT ROOM as an extra rect part in its footprint.
    //
    //   * M3 adds no rooms. Room index is 1:1 with route node index throughout
    //     the generator (`intent.nodes[zone.roomIndex]`, TryAssignRoomLevels,
    //     recipe slots, tier requirements), and a 14th room for a crater would
    //     have no node to be. A rect part costs nothing in that coupling and
    //     inherits the room's level for free.
    //   * An annexed part is never parts[0]. Dominant/Center is the node anchor
    //     that corridors, thresholds and the slice-2 alignment rule are built
    //     on, so it stays the rect that was inflated around the node centre.
    //   * Nothing is reserved ahead of time. M3 runs AFTER rooms, recipes and
    //     corridors are compiled and only takes space that is provably free —
    //     which is the ordering lesson from the blind stairwell-shaft
    //     reservation that took density 0 from 199/200 to 138/200
    //     (CURRENT_STATUS.md, 2026-07-27). It cannot fail an attempt: worst
    //     case it annexes nothing and the seed is exactly what M2 produced.
    internal sealed partial class DungeonLabGenerator
    {
        // A crater remnant smaller than this is a sliver, and slivers are M4's
        // job with a different mechanism. 2x2 is the smallest piece of floor
        // that reads as space rather than as a notch in a wall.
        private const int AnnexMinimumRectCells = 4;

        // The annexed part has to arrive as room, not as a pinch: a one-cell
        // join reads as a doorway the route never declared, and the boundary
        // builder would have to wall it on three sides.
        private const int AnnexMinimumSharedEdgeCells = 2;

        // One corridor crossing a crater splits it in two. Taking a second rect
        // lets the other side be annexed too — by a different room if that is
        // the one it touches — instead of being written off as leftover.
        private const int AnnexMaximumRectsPerVacantCell = 2;

        // M4 (mop-up) is the same claim with two parameters moved: it sweeps
        // EVERY lattice band rather than only the vacant ones, and it takes
        // rects down to a single cell. What is left after M3 is the channel
        // around each room — ragged, wrapping a room on two or three sides — so
        // one rect per band would leave most of it. §5 allows any number of
        // one-cell holes, so a lone cell with only one room face is left alone
        // rather than annexed on a pinch.
        // A mop-up remnant often touches its only neighbour on one cell — a
        // notch beside a recipe room, or a corner pocket — and leaving those
        // unclaimed is most of what §5's tolerance is spent on.
        private const int MopUpMinimumSharedEdgeCells = 1;
        private const int MopUpMinimumRectCells = 1;
        private const int MopUpMaximumRectsPerBand = 16;

        // A claim needs a room face to abut, and a band is swept in scan order,
        // so a hole two bands away from any room is out of reach on the first
        // pass and adjacent to a grown room on the second. Sweeping once left a
        // 128-cell corner on `ridge-ravine`, whose lattice puts six vacant cells
        // in one block with the nearest node a lane and a half away. Repeat
        // until a pass claims nothing.
        private const int MopUpSweepPasses = 8;

        private static int lastAnnexedRectCount;
        private static int lastAnnexedFloorCells;
        private static int lastMoppedRectCount;
        private static int lastMoppedFloorCells;
        private static int lastVacantLatticeCellCount;
        private static HashSet<Vector2Int> lastReservedShaftCells = new HashSet<Vector2Int>();

        /// <summary>
        /// M3 then M4a: claim the craters, then mop up what is left.
        /// </summary>
        /// <remarks>
        /// One mechanism, two parameter sets. M3 sweeps the bands of the lattice
        /// cells no node occupies and takes rects of at least four cells; M4a
        /// sweeps EVERY band and takes rects down to one. They are separate dial
        /// columns because they remove different things — craters are what the
        /// packer could never reach, channel is what it left behind — and §4.3
        /// turns them on at different points.
        /// </remarks>
        /// <returns>The number of floor cells added.</returns>
        private static int AnnexAndMopUpLatticeVoid(
            int dungeonSeed,
            int layoutAttempt,
            RouteIntent intent,
            DungeonPatternSpatialSettings spatial,
            IReadOnlyList<Vector2Int> nodeCenters,
            IReadOnlyList<Vector2Int> latticeCellCenters,
            HashSet<Vector2Int> protectedVistaCells,
            IReadOnlyList<RecipePlacement> recipePlacements,
            IReadOnlyList<RoomConnection> connections,
            List<RoomFootprint> rooms,
            HashSet<Vector2Int> floorCells)
        {
            lastAnnexedRectCount = 0;
            lastAnnexedFloorCells = 0;
            lastMoppedRectCount = 0;
            lastMoppedFloorCells = 0;
            lastVacantLatticeCellCount = 0;
            lastReservedShaftCells = new HashSet<Vector2Int>();
            if (!TrySplitLatticeCenters(
                    intent,
                    latticeCellCenters,
                    out List<Vector2Int> vacantCenters,
                    out List<Vector2Int> allCenters))
            {
                return 0;
            }

            lastVacantLatticeCellCount = vacantCenters.Count;
            DungeonGenerationSettings settings = CurrentGenerationSettings.Validated();
            if (settings.annexVacantFraction <= 0f && settings.mopUpVoidFraction <= 0f)
            {
                return 0;
            }

            var claim = new LatticeVoidClaim(
                intent,
                spatial,
                nodeCenters,
                latticeCellCenters,
                protectedVistaCells,
                recipePlacements,
                connections,
                rooms,
                floorCells);

            lastAnnexedFloorCells = claim.Sweep(
                SelectBands(vacantCenters, settings.annexVacantFraction, dungeonSeed, layoutAttempt, "vacant-selection"),
                AnnexMinimumRectCells,
                AnnexMaximumRectsPerVacantCell,
                AnnexMinimumSharedEdgeCells,
                out lastAnnexedRectCount);
            List<Vector2Int> mopUpBands = SelectBands(
                allCenters,
                settings.mopUpVoidFraction,
                dungeonSeed,
                layoutAttempt,
                "mop-up");
            for (int pass = 0; pass < MopUpSweepPasses; pass++)
            {
                int cells = claim.Sweep(
                    mopUpBands,
                    MopUpMinimumRectCells,
                    MopUpMaximumRectsPerBand,
                    MopUpMinimumSharedEdgeCells,
                    out int rects);
                lastMoppedFloorCells += cells;
                lastMoppedRectCount += rects;
                if (cells == 0)
                {
                    break;
                }
            }

            return lastAnnexedFloorCells + lastMoppedFloorCells;
        }

        // Half a dial step is half the bands, drawn rather than striped: taking
        // every other one in scan order would put the survivors on a diagonal,
        // which reads as a pattern. The stream is keyed like every other decision
        // in the generator, so which bands survive cannot be perturbed by
        // anything else — and the survivors are swept in scan order regardless of
        // how the draw shuffled them, so geometry does not depend on draw order.
        private static List<Vector2Int> SelectBands(
            List<Vector2Int> centers,
            float fraction,
            int dungeonSeed,
            int layoutAttempt,
            string purpose)
        {
            if (fraction <= 0f)
            {
                return new List<Vector2Int>();
            }

            var selected = new List<Vector2Int>(centers);
            if (fraction >= 1f)
            {
                return selected;
            }

            int keep = Mathf.Clamp(Mathf.RoundToInt(selected.Count * fraction), 0, selected.Count);
            ShuffleInPlace(selected, DerivedRandom(dungeonSeed, layoutAttempt, "annex", purpose));
            selected.RemoveRange(keep, selected.Count - keep);
            SortCellsRowMajor(selected);
            return selected;
        }

        // A lattice cell with no node on it is a crater. There is no way to
        // recover one from the node centres alone — a vacant cell contributes
        // none — which is why the embedder carries the whole lattice grid
        // through its transform.
        private static bool TrySplitLatticeCenters(
            RouteIntent intent,
            IReadOnlyList<Vector2Int> latticeCellCenters,
            out List<Vector2Int> vacantCenters,
            out List<Vector2Int> allCenters)
        {
            vacantCenters = new List<Vector2Int>();
            allCenters = new List<Vector2Int>();
            DungeonRouteTopology topology = intent.topology;
            int expected = topology.latticeColumnCount * topology.latticeRowCount;
            if (latticeCellCenters == null || latticeCellCenters.Count != expected)
            {
                return false;
            }

            var occupied = new bool[expected];
            foreach (RouteTopologyNode node in topology.nodes)
            {
                occupied[node.lattice.y * topology.latticeColumnCount + node.lattice.x] = true;
            }

            for (int index = 0; index < expected; index++)
            {
                allCenters.Add(latticeCellCenters[index]);
                if (!occupied[index])
                {
                    vacantCenters.Add(latticeCellCenters[index]);
                }
            }

            SortCellsRowMajor(vacantCenters);
            SortCellsRowMajor(allCenters);
            return true;
        }

        /// <summary>
        /// The shared state a claim sweep mutates: rooms, floor, and the cell map.
        /// </summary>
        private sealed class LatticeVoidClaim
        {
            private readonly RectInt latticeEnvelope;
            // The prism ledger, not a bare cell set (design §6 invariant): a
            // fill pass that reads plan cells alone will happily pack a reserved
            // volume, and §11 names that as the most likely first-implementation
            // failure. Everything a claim must not take is registered here, so a
            // future OpenVolume is honoured by construction rather than by
            // somebody remembering to extend a second exclusion list.
            private readonly PrismLedger reservations;
            private readonly Dictionary<Vector2Int, int> roomIdByCell;
            private readonly bool[] canClaim;
            private readonly int[] claimScratch;
            private readonly int[] xLanes;
            private readonly int[] yLanes;
            private readonly int envelopeRadiusCells;
            private readonly List<RoomFootprint> rooms;
            private readonly HashSet<Vector2Int> floorCells;

            public LatticeVoidClaim(
                RouteIntent intent,
                DungeonPatternSpatialSettings spatial,
                IReadOnlyList<Vector2Int> nodeCenters,
                IReadOnlyList<Vector2Int> latticeCellCenters,
                HashSet<Vector2Int> protectedVistaCells,
                IReadOnlyList<RecipePlacement> recipePlacements,
                IReadOnlyList<RoomConnection> connections,
                List<RoomFootprint> rooms,
                HashSet<Vector2Int> floorCells)
            {
                this.rooms = rooms;
                this.floorCells = floorCells;
                envelopeRadiusCells = spatial.roomEnvelopeRadiusCells;
                latticeEnvelope = LatticeEnvelopeFor(nodeCenters, spatial);
                reservations = CollectAnnexBlockedCells(
                    intent,
                    protectedVistaCells,
                    recipePlacements,
                    connections,
                    rooms,
                    floorCells);
                roomIdByCell = new Dictionary<Vector2Int, int>(floorCells.Count);
                canClaim = new bool[rooms.Count];
                claimScratch = new int[rooms.Count];
                for (int room = 0; room < rooms.Count; room++)
                {
                    // A recipe room's footprint is authored and its port
                    // approaches are declared, so it may not grow (design §9
                    // risk 2). It can still be the neighbour a claim abuts.
                    canClaim[room] = !TryGetRecipeSlot(intent.recipeSlots, room, out _);
                    foreach (Vector2Int cell in rooms[room].cells)
                    {
                        roomIdByCell[cell] = room;
                    }
                }

                xLanes = DistinctSortedLaneCoordinates(latticeCellCenters, axisX: true);
                yLanes = DistinctSortedLaneCoordinates(latticeCellCenters, axisX: false);
            }

            public int Sweep(
                IReadOnlyList<Vector2Int> bandCenters,
                int minimumRectCells,
                int maximumRectsPerBand,
                int minimumSharedEdgeCells,
                out int rectCount)
            {
                int added = 0;
                rectCount = 0;
                foreach (Vector2Int center in bandCenters)
                {
                    if (!TryResolveVacantCellRegion(
                            center,
                            xLanes,
                            yLanes,
                            envelopeRadiusCells,
                            latticeEnvelope,
                            out RectInt region))
                    {
                        continue;
                    }

                    for (int rect = 0; rect < maximumRectsPerBand; rect++)
                    {
                        if (!TryChooseAnnexRect(
                                region,
                                minimumRectCells,
                                floorCells,
                                reservations,
                                roomIdByCell,
                                canClaim,
                                claimScratch,
                                minimumSharedEdgeCells,
                                out RectInt annex,
                                out int claimingRoom))
                        {
                            break;
                        }

                        var parts = new List<RectInt>(rooms[claimingRoom].parts) { annex };
                        rooms[claimingRoom] = new RoomFootprint(parts);
                        for (int y = annex.yMin; y < annex.yMax; y++)
                        {
                            for (int x = annex.xMin; x < annex.xMax; x++)
                            {
                                var cell = new Vector2Int(x, y);
                                floorCells.Add(cell);
                                roomIdByCell[cell] = claimingRoom;
                            }
                        }

                        added += annex.width * annex.height;
                        rectCount++;
                    }
                }

                return added;
            }
        }

        // Everything a claim must not take, as prisms. Floor is excluded
        // separately, because it grows as the sweeps proceed.
        private static PrismLedger CollectAnnexBlockedCells(
            RouteIntent intent,
            HashSet<Vector2Int> protectedVistaCells,
            IReadOnlyList<RecipePlacement> recipePlacements,
            IReadOnlyList<RoomConnection> connections,
            IReadOnlyList<RoomFootprint> rooms,
            HashSet<Vector2Int> floorCells)
        {
            var blocked = new PrismLedger();
            blocked.Register(
                new OwnerKey(OwnerFamily.Vista, "reserved-lane"),
                SortedCells(protectedVistaCells ?? new HashSet<Vector2Int>()),
                Array.Empty<Vector2Int>(),
                Array.Empty<Vector2Int>());
            foreach (RecipePlacement placement in
                     recipePlacements ?? Array.Empty<RecipePlacement>())
            {
                var recipeOwner = new OwnerKey(
                    OwnerFamily.Recipe,
                    placement?.RecipeId ?? string.Empty);
                blocked.Register(
                    recipeOwner,
                    placement?.protectedCells ?? Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>());

                // A showpiece's backdrop is AUTHORED VOID: the dais reads as
                // backed against an exterior wall only while the cells behind it
                // are empty, and TryValidateAcceptedRecipes enforces that at the
                // tier stage. Annexing them fails the seed rather than the
                // annexation — measured as 96 RECIPE_SHOWPIECE_FIT rejections
                // across densities 4 and 5 before this was here.
                blocked.Register(
                    recipeOwner,
                    placement?.showpieceReservation.backdropVoidCells ?? Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>());

                // D4: the OpenVolume producer's SECOND site, and it is not
                // optional. §6 makes "the fill passes query the prism ledger" an
                // invariant and §11 calls missing it the most likely first
                // implementation failure — but the invariant alone is not enough
                // here, because there are TWO ledgers (Phase B finding 2). The
                // annex sweep runs during LAYOUT and the recipe registers its
                // volume during ELEVATION, a whole stage later, so a volume
                // declared only at the elevation site would already have been
                // packed solid by the time it existed.
                RegisterDeclaredRecipeOpenVolumes(intent, placement, blocked);
            }

            ReserveStairwellShafts(intent, connections, rooms, floorCells, blocked);
            return blocked;
        }

        /// <summary>
        /// Register a placement's reserved voids against the LAYOUT-stage ledger,
        /// where the elevation stage's resolved base level does not exist yet.
        /// </summary>
        /// <remarks>
        /// The band comes from the topology's DECLARED node level instead, which
        /// is the same pre-elevation absolute the corridor band uses (§8.1) and
        /// is exactly what `TryAssignRoomLevels` will later copy into
        /// `zoneLevels`. So the two sites reserve the identical band, derived two
        /// ways because the two stages know different things — not two rules.
        /// </remarks>
        private static void RegisterDeclaredRecipeOpenVolumes(
            RouteIntent intent,
            RecipePlacement placement,
            PrismLedger ledger)
        {
            if (placement == null ||
                intent?.nodes == null ||
                placement.roomIndex < 0 ||
                placement.roomIndex >= intent.nodes.Length)
            {
                return;
            }

            int declaredBaseLevel = intent.nodes[placement.roomIndex].relativeElevationLevels;
            var recipeOwner = new OwnerKey(OwnerFamily.Recipe, placement.RecipeId);
            foreach (RecipeZonePlacement zone in placement.zones)
            {
                if (zone.kind != DungeonRecipeZoneKind.OpenVolume)
                {
                    continue;
                }

                ledger.RegisterOpenVolume(
                    zone.cells,
                    zone.OpenVolumeBand(declaredBaseLevel),
                    RecipeOpenVolumeOwner(placement.RecipeId, zone.id),
                    new[] { recipeOwner });
            }
        }

        /// <summary>
        /// One concrete shaft window per transition corridor — the rest is free.
        /// </summary>
        /// <remarks>
        /// This is the explicit stairwell shaft the density design has wanted
        /// since §3, arrived at from the other end. A stairwell tower stands on
        /// VOID BESIDE its corridor, and until now that was implicit: the placer
        /// searched at tier time for whatever void happened to survive. Phase 4
        /// bought it a blanket two-cell band around every transition corridor,
        /// which worked but cost ~200 cells a seed and was most of the void
        /// left at density 5.
        /// <para>
        /// The reservation does not decide where the tower goes.
        /// <c>AddValidStairwellTransitionCandidates</c> still searches every
        /// position and both sides; all this has to guarantee is that its
        /// candidate list is not empty. So it takes ONE window, on whichever
        /// side is actually free — which is only knowable here, after rooms,
        /// recipes and corridors are compiled. Choosing a window BEFORE
        /// inflation was tried on 2026-07-27 and took density 0 from 199/200 to
        /// 138/200; that failure was about ordering, and this is the other
        /// ordering.
        /// </para>
        /// </remarks>
        private static void ReserveStairwellShafts(
            RouteIntent intent,
            IReadOnlyList<RoomConnection> connections,
            IReadOnlyList<RoomFootprint> rooms,
            HashSet<Vector2Int> floorCells,
            PrismLedger blocked)
        {
            foreach (RoomConnection connection in connections ?? Array.Empty<RoomConnection>())
            {
                if (connection.path == null ||
                    connection.path.Count < 2 ||
                    !TryFindTraversalEdge(
                        intent,
                        connection.fromRoom,
                        connection.toRoom,
                        out RouteTraversalIntent edge) ||
                    (edge.transitionKind == RouteTransitionKind.LevelCorridor &&
                     edge.requiredRiseLevels == 0))
                {
                    continue;
                }

                var exterior = new List<Vector2Int>();
                foreach (Vector2Int cell in connection.path)
                {
                    if (!rooms[connection.fromRoom].Contains(cell) &&
                        !rooms[connection.toRoom].Contains(cell))
                    {
                        exterior.Add(cell);
                    }
                }

                // Abutting rooms have no exterior corridor at all, so there is
                // nowhere beside it for a tower to stand and nothing to protect.
                if (exterior.Count == 0)
                {
                    continue;
                }

                Vector2Int axis = CardinalUnit(exterior[exterior.Count - 1] - exterior[0]);
                if (axis == Vector2Int.zero)
                {
                    axis = CardinalUnit(
                        connection.path[connection.path.Count - 1] - connection.path[0]);
                }

                var lateral = new Vector2Int(axis.y, -axis.x);
                if (TryChooseShaftWindow(exterior, axis, lateral, floorCells, blocked, out RectInt window))
                {
                    var shaftOwner = new OwnerKey(
                        OwnerFamily.Corridor,
                        $"stairwell-shaft:{connection.fromRoom}-{connection.toRoom}");
                    for (int y = window.yMin; y < window.yMax; y++)
                    {
                        for (int x = window.xMin; x < window.xMax; x++)
                        {
                            var cell = new Vector2Int(x, y);
                            blocked.Register(
                                shaftOwner,
                                new[] { cell },
                                Array.Empty<Vector2Int>(),
                                Array.Empty<Vector2Int>());
                            lastReservedShaftCells.Add(cell);
                        }
                    }
                }
            }
        }

        // The widest tower footprint the shipped set produces is three cells on
        // one axis and two on the other (254 measured towers: 2x2:129, 2x3:54,
        // 3x2:43, 1x2:12, 2x1:16), so a 3x3 window beside the path holds any of
        // them in either orientation.
        private const int StairwellShaftWindowCells = 3;

        private static bool TryChooseShaftWindow(
            List<Vector2Int> exterior,
            Vector2Int axis,
            Vector2Int lateral,
            HashSet<Vector2Int> floorCells,
            PrismLedger blocked,
            out RectInt window)
        {
            window = default;
            // Nearest the middle of the corridor first, then alternating
            // outward, and the positive side before the negative — a total order
            // over geometry alone, so no stream is drawn and the choice cannot
            // be perturbed by anything else in the seed.
            int middle = (exterior.Count - 1) / 2;
            for (int step = 0; step < exterior.Count; step++)
            {
                int index = middle + (step % 2 == 0 ? step / 2 : -(step / 2 + 1));
                if (index < 0 || index >= exterior.Count)
                {
                    continue;
                }

                foreach (int side in ShaftSearchOrder)
                {
                    if (TryBuildShaftWindow(
                            exterior[index],
                            axis,
                            lateral * side,
                            floorCells,
                            blocked,
                            out window))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static readonly int[] ShaftSearchOrder = { 1, -1 };

        private static bool TryBuildShaftWindow(
            Vector2Int anchor,
            Vector2Int axis,
            Vector2Int outward,
            HashSet<Vector2Int> floorCells,
            PrismLedger blocked,
            out RectInt window)
        {
            window = default;
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = int.MinValue;
            int maxY = int.MinValue;
            for (int along = 0; along < StairwellShaftWindowCells; along++)
            {
                for (int out_ = 1; out_ <= StairwellShaftWindowCells; out_++)
                {
                    Vector2Int cell = anchor + axis * along + outward * out_;
                    if (floorCells.Contains(cell) || blocked.BlocksFill(cell))
                    {
                        return false;
                    }

                    minX = Mathf.Min(minX, cell.x);
                    minY = Mathf.Min(minY, cell.y);
                    maxX = Mathf.Max(maxX, cell.x);
                    maxY = Mathf.Max(maxY, cell.y);
                }
            }

            window = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return true;
        }

        private static bool TryFindTraversalEdge(
            RouteIntent intent,
            int fromNode,
            int toNode,
            out RouteTraversalIntent edge)
        {
            foreach (RouteTraversalIntent candidate in intent.traversalEdges)
            {
                if ((candidate.fromNode == fromNode && candidate.toNode == toNode) ||
                    (candidate.fromNode == toNode && candidate.toNode == fromNode))
                {
                    edge = candidate;
                    return true;
                }
            }

            edge = default;
            return false;
        }

        // The lattice partitions the plan into bands, one per lane, split at the
        // midpoint between adjacent lanes. A vacant cell's region is its own two
        // bands intersected — which is the 9x9 envelope a node there would have
        // had when the lanes are at the pitch, and correctly wider or narrower
        // when the rubber sheet has moved them.
        private static int[] DistinctSortedLaneCoordinates(
            IReadOnlyList<Vector2Int> cells,
            bool axisX)
        {
            var lanes = new SortedSet<int>();
            foreach (Vector2Int cell in cells)
            {
                lanes.Add(axisX ? cell.x : cell.y);
            }

            var ordered = new int[lanes.Count];
            lanes.CopyTo(ordered);
            return ordered;
        }

        private static bool TryResolveVacantCellRegion(
            Vector2Int center,
            int[] xLanes,
            int[] yLanes,
            int radiusCells,
            RectInt latticeEnvelope,
            out RectInt region)
        {
            region = default;
            ResolveLaneBand(xLanes, center.x, radiusCells, out int minX, out int maxX);
            ResolveLaneBand(yLanes, center.y, radiusCells, out int minY, out int maxY);

            // Clipped to the lattice envelope on purpose. A crater outside it is
            // outside the space the embedder gave this dungeon, so filling it
            // would add sprawl without adding fill — the metric's denominator is
            // the envelope, and the eye agrees with the metric here.
            minX = Mathf.Max(minX, latticeEnvelope.xMin);
            minY = Mathf.Max(minY, latticeEnvelope.yMin);
            maxX = Mathf.Min(maxX, latticeEnvelope.xMax - 1);
            maxY = Mathf.Min(maxY, latticeEnvelope.yMax - 1);
            if (maxX < minX || maxY < minY)
            {
                return false;
            }

            region = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return true;
        }

        private static void ResolveLaneBand(
            int[] lanes,
            int coordinate,
            int radiusCells,
            out int min,
            out int max)
        {
            int index = Array.BinarySearch(lanes, coordinate);
            min = index <= 0
                ? coordinate - radiusCells
                : (lanes[index - 1] + coordinate + 1) / 2;
            max = index < 0 || index >= lanes.Length - 1
                ? coordinate + radiusCells
                : (coordinate + lanes[index + 1] + 1) / 2 - 1;
        }

        /// <summary>
        /// The largest free rect in one crater that an eligible room can claim.
        /// </summary>
        /// <remarks>
        /// Exhaustive over the region's sub-rects — a crater is at most a couple
        /// of dozen cells on a side, and a prefix sum makes the free test O(1),
        /// so "the largest one that works" is cheaper than any heuristic worth
        /// arguing about. Ties resolve to the first in scan order, so the choice
        /// is a property of the geometry rather than of enumeration order.
        /// </remarks>
        private static bool TryChooseAnnexRect(
            RectInt region,
            int minimumRectCells,
            HashSet<Vector2Int> floorCells,
            PrismLedger blockedCells,
            Dictionary<Vector2Int, int> roomIdByCell,
            bool[] canClaim,
            int[] claimScratch,
            int minimumSharedEdgeCells,
            out RectInt annex,
            out int claimingRoom)
        {
            annex = default;
            claimingRoom = -1;
            int width = region.width;
            int height = region.height;

            // free[x, y] as an inclusive-exclusive prefix sum of BLOCKED cells,
            // so a rect is free exactly when its window sums to zero.
            var blockedPrefix = new int[width + 1, height + 1];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var cell = new Vector2Int(region.xMin + x, region.yMin + y);
                    int taken = floorCells.Contains(cell) || blockedCells.BlocksFill(cell) ? 1 : 0;
                    blockedPrefix[x + 1, y + 1] = taken +
                        blockedPrefix[x, y + 1] +
                        blockedPrefix[x + 1, y] -
                        blockedPrefix[x, y];
                }
            }

            int bestArea = 0;
            int bestShared = 0;
            for (int y0 = 0; y0 < height; y0++)
            {
                for (int y1 = y0; y1 < height; y1++)
                {
                    int rectHeight = y1 - y0 + 1;
                    for (int x0 = 0; x0 < width; x0++)
                    {
                        for (int x1 = x0; x1 < width; x1++)
                        {
                            int area = (x1 - x0 + 1) * rectHeight;
                            if (area < minimumRectCells || area < bestArea)
                            {
                                continue;
                            }

                            int taken = blockedPrefix[x1 + 1, y1 + 1] -
                                blockedPrefix[x0, y1 + 1] -
                                blockedPrefix[x1 + 1, y0] +
                                blockedPrefix[x0, y0];
                            if (taken > 0)
                            {
                                continue;
                            }

                            var candidate = new RectInt(
                                region.xMin + x0,
                                region.yMin + y0,
                                x1 - x0 + 1,
                                rectHeight);
                            if (!TryResolveAnnexClaimant(
                                    candidate,
                                    roomIdByCell,
                                    canClaim,
                                    claimScratch,
                                    minimumSharedEdgeCells,
                                    out int candidateRoom,
                                    out int shared))
                            {
                                continue;
                            }

                            if (area > bestArea || (area == bestArea && shared > bestShared))
                            {
                                bestArea = area;
                                bestShared = shared;
                                annex = candidate;
                                claimingRoom = candidateRoom;
                            }
                        }
                    }
                }
            }

            return claimingRoom >= 0;
        }

        // The room that owns the longest face on the crater takes it, so a
        // crater is absorbed by the room it most reads as belonging to. Ties go
        // to the lower room index, which is route order.
        private static bool TryResolveAnnexClaimant(
            RectInt rect,
            Dictionary<Vector2Int, int> roomIdByCell,
            bool[] canClaim,
            int[] claimScratch,
            int minimumSharedEdgeCells,
            out int claimingRoom,
            out int sharedEdgeCells)
        {
            Array.Clear(claimScratch, 0, claimScratch.Length);
            for (int x = rect.xMin; x < rect.xMax; x++)
            {
                CountAnnexNeighbor(new Vector2Int(x, rect.yMin - 1), roomIdByCell, canClaim, claimScratch);
                CountAnnexNeighbor(new Vector2Int(x, rect.yMax), roomIdByCell, canClaim, claimScratch);
            }

            for (int y = rect.yMin; y < rect.yMax; y++)
            {
                CountAnnexNeighbor(new Vector2Int(rect.xMin - 1, y), roomIdByCell, canClaim, claimScratch);
                CountAnnexNeighbor(new Vector2Int(rect.xMax, y), roomIdByCell, canClaim, claimScratch);
            }

            claimingRoom = -1;
            sharedEdgeCells = 0;
            for (int room = 0; room < claimScratch.Length; room++)
            {
                if (claimScratch[room] > sharedEdgeCells)
                {
                    sharedEdgeCells = claimScratch[room];
                    claimingRoom = room;
                }
            }

            if (sharedEdgeCells < minimumSharedEdgeCells)
            {
                claimingRoom = -1;
                sharedEdgeCells = 0;
                return false;
            }

            return true;
        }

        private static void CountAnnexNeighbor(
            Vector2Int cell,
            Dictionary<Vector2Int, int> roomIdByCell,
            bool[] canClaim,
            int[] claimScratch)
        {
            if (roomIdByCell.TryGetValue(cell, out int room) && canClaim[room])
            {
                claimScratch[room]++;
            }
        }

        private static void SortCellsRowMajor(List<Vector2Int> cells)
        {
            cells.Sort((first, second) =>
                first.y != second.y ? first.y.CompareTo(second.y) : first.x.CompareTo(second.x));
        }

        private static void ShuffleInPlace(List<Vector2Int> cells, System.Random random)
        {
            for (int index = cells.Count - 1; index > 0; index--)
            {
                int swap = random.Next(index + 1);
                (cells[index], cells[swap]) = (cells[swap], cells[index]);
            }
        }
    }
}

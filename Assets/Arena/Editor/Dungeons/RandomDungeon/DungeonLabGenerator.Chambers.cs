using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // M4b of the density-scale design (§4.2): CHAMBER SUBDIVISION.
    //
    // M3 and the mop-up fill a room's whole lattice band, which is the point —
    // but a 200-cell room is a warehouse, not a keep. Fork F3 called this the
    // one part of the plan that is polish rather than structure; at density 5 it
    // stopped being optional, because the annexed mass is most of the floor.
    //
    // The subdivision is a BOUNDARY-stage refinement, not a layout change:
    // `layout.rooms` keeps its 1:1 mapping to route nodes, and chambers exist
    // only inside `RoomBoundaryContext`. Repartitioning `cellRoomIds` alone
    // would produce no walls at all, which is the trap §4.2 spells out — three
    // pieces of state have to expand together:
    //
    //   * `cellRoomIds`   repartitioned to chamber granularity;
    //   * `enclosedRooms` sized to the CHAMBER count, each chamber inheriting
    //     its parent room's flag — `IsEnclosedRoom` bounds-checks against this
    //     array, so a chamber id past its end reads as unenclosed and
    //     `IsPartitionWallEdge` silently returns false;
    //   * one `DoorwayEdge` per chamber seam, added BEFORE
    //     `DemoteSealedEnclosedRooms` and `ValidateEnclosedRoomDoorways` run, so
    //     the existing sealed-room validation covers the expanded set instead of
    //     being bypassed by it.
    internal sealed partial class DungeonLabGenerator
    {
        // Above this a room reads as a hall rather than a room. 64 cells is 8x8,
        // the largest a generic room can be inflated to at density 0 — so a
        // chamber is never bigger than the biggest room the sparse end of the
        // dial produces, and the packed end reads as a suite of them.
        //
        // Swept at density 5 rather than assumed; density 0's own rooms average
        // ~40 cells, which is the family a chamber should land in:
        //
        //   threshold 48 -> 39 chambers/seed, mean 34 cells   (below the family)
        //   threshold 64 -> 30 chambers/seed, mean 45 cells   (shipped)
        //   threshold 96 -> 22 chambers/seed, mean 60 cells   (above it)
        private const int ChamberMaximumCells = 64;

        // A cut has to leave a wall on both sides of its doorway, or the gateway
        // rules reject it and the seam renders as a gap rather than a door
        // (docs: dungeon gateway rules, 2026-07-26). Three is the shortest seam
        // with a middle cell and a flank each side.
        private const int ChamberMinimumSeamCells = 3;

        // A chamber narrower than this is a corridor with a door on it.
        private const int ChamberMinimumSideCells = 2;

        private static int lastChamberCount;

        /// <summary>
        /// Splits over-large rooms into walled chambers with a doorway each.
        /// </summary>
        /// <remarks>
        /// Gated on the annex dial rather than on a new column: chambers exist
        /// because annexation made rooms big, so where nothing is annexed
        /// nothing is subdivided and densities 0-2 are untouched by
        /// construction.
        /// </remarks>
        private static void SubdivideOversizeRoomsIntoChambers(
            DungeonLayout layout,
            IReadOnlyList<RecipePlacement> recipePlacements,
            PrismLedger prisms,
            Dictionary<Vector2Int, int> cellRoomIds,
            List<ElevationEdgeModel.DoorwayEdge> doorways,
            ref bool[] enclosedRooms)
        {
            lastChamberCount = layout.rooms.Count;
            if (CurrentGenerationSettings.Validated().annexVacantFraction <= 0f)
            {
                return;
            }

            var authoredRooms = new HashSet<int>();
            foreach (RecipePlacement placement in recipePlacements ?? new RecipePlacement[0])
            {
                if (placement != null)
                {
                    authoredRooms.Add(placement.roomIndex);
                }
            }

            IReadOnlyList<HashSet<Vector2Int>> openVolumes =
                prisms?.OpenVolumeCellGroups() ?? Array.Empty<HashSet<Vector2Int>>();
            var enclosed = new List<bool>(enclosedRooms);
            for (int room = 0; room < layout.rooms.Count; room++)
            {
                // A recipe room's footprint is authored down to the cell, and a
                // partition through a dais would cut the showpiece in half.
                if (authoredRooms.Contains(room) || layout.rooms[room].Area <= ChamberMaximumCells)
                {
                    continue;
                }

                List<HashSet<Vector2Int>> chambers = SplitRoomIntoChambers(
                    layout.rooms[room].cells,
                    openVolumes);
                if (chambers.Count <= 1)
                {
                    continue;
                }

                // Chamber 0 keeps the room's own id, so every consumer that
                // reads a cell it did not split still lands on the route node.
                for (int chamber = 1; chamber < chambers.Count; chamber++)
                {
                    int chamberId = enclosed.Count;
                    enclosed.Add(enclosedRooms[room]);
                    foreach (Vector2Int cell in chambers[chamber])
                    {
                        cellRoomIds[cell] = chamberId;
                    }
                }

                AddChamberSeamDoorways(chambers, cellRoomIds, doorways);
            }

            enclosedRooms = enclosed.ToArray();
            lastChamberCount = enclosedRooms.Length;
        }

        /// <summary>
        /// Recursive guillotine cut down to <see cref="ChamberMaximumCells"/>.
        /// </summary>
        /// <remarks>
        /// Straight cuts on purpose: a partition wall is a straight run of
        /// masonry, and a ragged chamber boundary would render as a staircase of
        /// wall stubs. A cut is only taken when both sides stay 4-connected and
        /// the seam is long enough to hold a flanked doorway, so an L-shaped
        /// room is cut across the L rather than through its notch. The cut also
        /// keeps every owner-grouped reserved void wholly inside one chamber;
        /// the search may move the seam, but may not partition an atrium.
        /// </remarks>
        private static List<HashSet<Vector2Int>> SplitRoomIntoChambers(
            HashSet<Vector2Int> roomCells,
            IReadOnlyList<HashSet<Vector2Int>> openVolumes)
        {
            var chambers = new List<HashSet<Vector2Int>>();
            var pending = new Queue<HashSet<Vector2Int>>();
            pending.Enqueue(new HashSet<Vector2Int>(roomCells));
            while (pending.Count > 0)
            {
                HashSet<Vector2Int> cells = pending.Dequeue();
                if (cells.Count <= ChamberMaximumCells ||
                    !TryCutChamber(
                        cells,
                        openVolumes,
                        out HashSet<Vector2Int> first,
                        out HashSet<Vector2Int> second))
                {
                    chambers.Add(cells);
                    continue;
                }

                pending.Enqueue(first);
                pending.Enqueue(second);
            }

            // Scan order, so which chamber keeps the room's id is a property of
            // the geometry rather than of the queue.
            chambers.Sort((a, b) => CompareCells(LowestCell(a), LowestCell(b)));
            return chambers;
        }

        private static bool TryCutChamber(
            HashSet<Vector2Int> cells,
            IReadOnlyList<HashSet<Vector2Int>> openVolumes,
            out HashSet<Vector2Int> first,
            out HashSet<Vector2Int> second)
        {
            first = null;
            second = null;
            RectInt bounds = GetCellRect(cells);
            // Cut across the longer axis first: it is the axis that makes the
            // room read as a warehouse, and it leaves the wider seam.
            bool longerIsX = bounds.width >= bounds.height;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool cutX = attempt == 0 ? longerIsX : !longerIsX;
                int min = cutX ? bounds.xMin : bounds.yMin;
                int max = cutX ? bounds.xMax : bounds.yMax;
                int middle = (min + max) / 2;
                for (int step = 0; step < max - min; step++)
                {
                    int cut = middle + (step % 2 == 0 ? step / 2 : -(step / 2 + 1));
                    if (cut - min < ChamberMinimumSideCells || max - cut < ChamberMinimumSideCells)
                    {
                        continue;
                    }

                    if (TryTakeChamberCut(
                            cells,
                            openVolumes,
                            cutX,
                            cut,
                            out first,
                            out second))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryTakeChamberCut(
            HashSet<Vector2Int> cells,
            IReadOnlyList<HashSet<Vector2Int>> openVolumes,
            bool cutX,
            int cut,
            out HashSet<Vector2Int> first,
            out HashSet<Vector2Int> second)
        {
            first = new HashSet<Vector2Int>();
            second = new HashSet<Vector2Int>();
            foreach (Vector2Int cell in cells)
            {
                if ((cutX ? cell.x : cell.y) < cut)
                {
                    first.Add(cell);
                }
                else
                {
                    second.Add(cell);
                }
            }

            if (first.Count == 0 ||
                second.Count == 0 ||
                !IsCellSetConnected(first) ||
                !IsCellSetConnected(second) ||
                CountSeamPairs(first, second) < ChamberMinimumSeamCells ||
                SplitsOpenVolume(first, second, openVolumes))
            {
                first = null;
                second = null;
                return false;
            }

            return true;
        }

        private static bool SplitsOpenVolume(
            HashSet<Vector2Int> first,
            HashSet<Vector2Int> second,
            IReadOnlyList<HashSet<Vector2Int>> openVolumes)
        {
            foreach (HashSet<Vector2Int> volume in
                     openVolumes ?? Array.Empty<HashSet<Vector2Int>>())
            {
                bool touchesFirst = false;
                bool touchesSecond = false;
                foreach (Vector2Int cell in volume)
                {
                    touchesFirst |= first.Contains(cell);
                    touchesSecond |= second.Contains(cell);
                    if (touchesFirst && touchesSecond)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static int CountSeamPairs(HashSet<Vector2Int> first, HashSet<Vector2Int> second)
        {
            int pairs = 0;
            foreach (Vector2Int cell in first)
            {
                foreach (Vector2Int direction in CardinalCellOffsets)
                {
                    if (second.Contains(cell + direction))
                    {
                        pairs++;
                    }
                }
            }

            return pairs;
        }

        private static bool IsCellSetConnected(HashSet<Vector2Int> cells)
        {
            if (cells.Count == 0)
            {
                return false;
            }

            Vector2Int start = LowestCell(cells);
            var visited = new HashSet<Vector2Int> { start };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Vector2Int current = queue.Dequeue();
                foreach (Vector2Int direction in CardinalCellOffsets)
                {
                    Vector2Int neighbor = current + direction;
                    if (cells.Contains(neighbor) && visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            return visited.Count == cells.Count;
        }

        private static Vector2Int LowestCell(HashSet<Vector2Int> cells)
        {
            Vector2Int lowest = default;
            bool found = false;
            foreach (Vector2Int cell in cells)
            {
                if (!found || CompareCells(cell, lowest) < 0)
                {
                    lowest = cell;
                    found = true;
                }
            }

            return lowest;
        }

        // One doorway per seam, in the middle of the seam's longest straight
        // run. The middle is what the gateway rules want — a door needs a real
        // wall on both flanks — and the longest run is the seam segment most
        // likely to have them.
        private static void AddChamberSeamDoorways(
            IReadOnlyList<HashSet<Vector2Int>> chambers,
            IReadOnlyDictionary<Vector2Int, int> cellRoomIds,
            List<ElevationEdgeModel.DoorwayEdge> doorways)
        {
            for (int first = 0; first < chambers.Count; first++)
            {
                for (int second = first + 1; second < chambers.Count; second++)
                {
                    if (TryFindChamberSeamDoorway(
                            chambers[first],
                            chambers[second],
                            out Vector2Int firstCell,
                            out Vector2Int secondCell))
                    {
                        doorways.Add(new ElevationEdgeModel.DoorwayEdge(firstCell, secondCell));
                    }
                }
            }
        }

        private static bool TryFindChamberSeamDoorway(
            HashSet<Vector2Int> first,
            HashSet<Vector2Int> second,
            out Vector2Int firstCell,
            out Vector2Int secondCell)
        {
            firstCell = default;
            secondCell = default;
            var pairs = new List<(Vector2Int from, Vector2Int to)>();
            foreach (Vector2Int cell in first)
            {
                foreach (Vector2Int direction in CardinalCellOffsets)
                {
                    if (second.Contains(cell + direction))
                    {
                        pairs.Add((cell, cell + direction));
                    }
                }
            }

            if (pairs.Count == 0)
            {
                return false;
            }

            pairs.Sort((a, b) =>
            {
                int compare = CompareCells(a.from, b.from);
                return compare != 0 ? compare : CompareCells(a.to, b.to);
            });
            (Vector2Int from, Vector2Int to) chosen = pairs[pairs.Count / 2];
            firstCell = chosen.from;
            secondCell = chosen.to;
            return true;
        }
    }
}

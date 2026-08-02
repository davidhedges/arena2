using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    internal sealed partial class DungeonLabGenerator
    {
        // These are realization choices, not another room-plan hierarchy. A
        // topology still declares only a room-local layer and the connections
        // that bind it; this producer derives one connected cell set directly
        // from the existing RoomFootprint and threshold cells, writes it to the
        // existing SurfaceField, and immediately discards the choice.
        private enum GenericStructuralLayerPattern
        {
            FullStorey,
            PartialGallery,
            PerimeterRing,
            Balcony
        }

        private static readonly Vector2Int[] GenericLayerDirections =
        {
            Vector2Int.right,
            Vector2Int.up,
            Vector2Int.left,
            Vector2Int.down
        };

        /// <summary>
        /// Realize every non-recipe node's declared structural storeys in the
        /// same surface and prism owners used by recipes, corridors and spans.
        /// </summary>
        private static bool TryRealizeGenericStructuralRoomLayers(
            DungeonLayout layout,
            IReadOnlyList<RouteNodeIntent> nodes,
            IReadOnlyList<RecipePlacement> selectedRecipes,
            SurfaceField surfaces,
            PrismLedger prisms,
            List<PlanOpening> generatedOpeningCandidates,
            out string rejectionReason)
        {
            rejectionReason = string.Empty;
            if (layout.rooms == null || nodes == null)
            {
                rejectionReason = "[GENERIC_ROOM_LAYERS] layout or route intent was unavailable";
                return false;
            }

            int roomCount = Mathf.Min(layout.rooms.Count, nodes.Count);
            for (int roomIndex = 0; roomIndex < roomCount; roomIndex++)
            {
                RouteNodeIntent node = nodes[roomIndex];
                if (!node.DeclaresStoreys ||
                    HasSelectedRecipeAtRoom(selectedRecipes, roomIndex))
                {
                    continue;
                }

                RoomFootprint room = layout.rooms[roomIndex];
                var structuralLevels = new List<int> { node.relativeElevationLevels };
                foreach (RouteTopologyLayer layer in node.layers)
                {
                    if (layer.relativeLevel != 0)
                    {
                        structuralLevels.Add(node.relativeElevationLevels + layer.relativeLevel);
                    }
                }

                structuralLevels.Sort();
                int lowestLevel = structuralLevels[0];
                if (lowestLevel < node.relativeElevationLevels)
                {
                    // A basement has to become the column floor everywhere. A
                    // partial lowest layer would leave fill-backed columns at
                    // two unrelated elevations inside one room footprint.
                    foreach (Vector2Int cell in room.CellsRowMajor())
                    {
                        surfaces.AddSurface(cell, lowestLevel, SurfaceKind.Floor);
                    }
                }

                for (int levelIndex = 0; levelIndex < structuralLevels.Count; levelIndex++)
                {
                    int absoluteLevel = structuralLevels[levelIndex];
                    bool isNodeBase = absoluteLevel == node.relativeElevationLevels;
                    string layerId = isNodeBase
                        ? string.Empty
                        : LayerIdAtAbsoluteLevel(node, absoluteLevel);
                    HashSet<Vector2Int> layerCells;
                    GenericStructuralLayerPattern pattern;

                    if (isNodeBase || absoluteLevel == lowestLevel)
                    {
                        layerCells = new HashSet<Vector2Int>(room.cells);
                        pattern = GenericStructuralLayerPattern.FullStorey;
                    }
                    else
                    {
                        List<Vector2Int> thresholds = CollectLayerThresholdCells(
                            layout,
                            roomIndex,
                            layerId);
                        if (thresholds.Count == 0)
                        {
                            rejectionReason =
                                $"[GENERIC_ROOM_LAYER_UNBOUND] node '{node.id}' layer '{layerId}' " +
                                "had no layer-bound threshold";
                            return false;
                        }

                        layerCells = BuildGenericLayerCells(room, thresholds, out pattern);
                        if (!GenericLayerContainsRequirements(layerCells, thresholds) ||
                            !IsConnectedCellSet(layerCells))
                        {
                            rejectionReason =
                                $"[GENERIC_ROOM_LAYER_DISCONNECTED] node '{node.id}' layer '{layerId}' " +
                                $"could not connect its {thresholds.Count} threshold(s)";
                            return false;
                        }

                        foreach (Vector2Int cell in SortedCells(layerCells))
                        {
                            surfaces.AddSurface(cell, absoluteLevel, SurfaceKind.Floor);
                        }
                    }

                    if (absoluteLevel == lowestLevel)
                    {
                        continue;
                    }

                    int lowerLevel = structuralLevels[levelIndex - 1];
                    var layerOwner = new OwnerKey(
                        OwnerFamily.Room,
                        $"{node.id}#{(string.IsNullOrEmpty(layerId) ? "base" : layerId)}");
                    Vector2Int[] orderedLayerCells = SortedCells(layerCells).ToArray();
                    Vector2Int[] supportCells = GenericLayerSupportCells(room, layerCells);
                    if (!prisms.TryRegisterStructuralSurface(
                            orderedLayerCells,
                            supportCells,
                            lowerLevel,
                            absoluteLevel,
                            layerOwner,
                            out Prism blocker))
                    {
                        rejectionReason =
                            $"[GENERIC_ROOM_LAYER_PRISM] node '{node.id}' layer '{layerId}' " +
                            $"conflicted with {blocker}";
                        return false;
                    }

                    if (pattern == GenericStructuralLayerPattern.FullStorey)
                    {
                        continue;
                    }

                    var sharedVoidCells = new HashSet<Vector2Int>(room.cells);
                    sharedVoidCells.ExceptWith(layerCells);
                    if (sharedVoidCells.Count == 0)
                    {
                        continue;
                    }

                    // The lower walk surface remains a valid catch floor. The
                    // reserved air begins one level above it and includes the
                    // current layer elevation, so a late fill in the gallery's
                    // hole fails OPEN_VOLUME_VIOLATION.
                    var voidBand = new LevelBand(lowerLevel + 1, absoluteLevel + 1);
                    var voidOwner = new OwnerKey(
                        OwnerFamily.Opening,
                        $"{node.id}#{layerId}-shared-void");
                    if (!prisms.TryRegisterOpenVolume(
                            SortedCells(sharedVoidCells),
                            voidBand,
                            voidOwner,
                            new[] { layerOwner },
                            out Prism voidBlocker))
                    {
                        rejectionReason =
                            $"[GENERIC_ROOM_LAYER_VOID] node '{node.id}' layer '{layerId}' " +
                            $"shared void {voidBand} conflicted with {voidBlocker}";
                        return false;
                    }

                    AddGeneratedApertureCandidates(
                        node.id,
                        layerId,
                        layerCells,
                        sharedVoidCells,
                        absoluteLevel,
                        voidOwner,
                        generatedOpeningCandidates);
                }
            }

            return true;
        }

        /// <summary>
        /// Publish every legal rim-to-shared-void edge as a deterministic
        /// candidate. Final planning selects one aperture per volume after
        /// transition footprints are known; all remaining edges stay guarded
        /// and are rendered as the balcony/atrium rim by the existing renderer.
        /// </summary>
        private static void AddGeneratedApertureCandidates(
            string nodeId,
            string layerId,
            HashSet<Vector2Int> upperCells,
            HashSet<Vector2Int> sharedVoidCells,
            int upperLevel,
            OwnerKey voidOwner,
            List<PlanOpening> candidates)
        {
            if (candidates == null)
            {
                return;
            }

            int candidateIndex = 0;
            foreach (Vector2Int rim in SortedCells(upperCells))
            {
                foreach (Vector2Int direction in GenericLayerDirections)
                {
                    Vector2Int hole = rim + direction;
                    if (!sharedVoidCells.Contains(hole))
                    {
                        continue;
                    }

                    candidates.Add(new PlanOpening(
                        voidOwner,
                        $"{nodeId}#{layerId}-aperture-{candidateIndex++}",
                        OpeningKind.Aperture,
                        rim,
                        DirectionFromVector(new Vector2(direction.x, direction.y)),
                        upperLevel));
                }
            }
        }

        private static bool HasSelectedRecipeAtRoom(
            IReadOnlyList<RecipePlacement> selectedRecipes,
            int roomIndex)
        {
            foreach (RecipePlacement placement in
                     selectedRecipes ?? Array.Empty<RecipePlacement>())
            {
                if (placement != null && placement.roomIndex == roomIndex)
                {
                    return true;
                }
            }

            return false;
        }

        private static string LayerIdAtAbsoluteLevel(RouteNodeIntent node, int absoluteLevel)
        {
            foreach (RouteTopologyLayer layer in node.layers)
            {
                if (node.relativeElevationLevels + layer.relativeLevel == absoluteLevel)
                {
                    return layer.layerId;
                }
            }

            return string.Empty;
        }

        private static List<Vector2Int> CollectLayerThresholdCells(
            DungeonLayout layout,
            int roomIndex,
            string layerId)
        {
            var cells = new HashSet<Vector2Int>();
            RoomFootprint room = layout.rooms[roomIndex];
            foreach (RoomConnection connection in layout.connections)
            {
                bool atStart = connection.fromRoom == roomIndex &&
                    string.Equals(connection.fromLayerId, layerId, StringComparison.Ordinal);
                bool atEnd = connection.toRoom == roomIndex &&
                    string.Equals(connection.toLayerId, layerId, StringComparison.Ordinal);
                if (!atStart && !atEnd || connection.path == null)
                {
                    continue;
                }

                if (atStart)
                {
                    for (int index = 0; index < connection.path.Count; index++)
                    {
                        if (room.Contains(connection.path[index]))
                        {
                            cells.Add(connection.path[index]);
                            break;
                        }
                    }
                }
                else
                {
                    for (int index = connection.path.Count - 1; index >= 0; index--)
                    {
                        if (room.Contains(connection.path[index]))
                        {
                            cells.Add(connection.path[index]);
                            break;
                        }
                    }
                }
            }

            return new List<Vector2Int>(SortedCells(cells));
        }

        private static HashSet<Vector2Int> BuildGenericLayerCells(
            RoomFootprint room,
            IReadOnlyList<Vector2Int> thresholds,
            out GenericStructuralLayerPattern pattern)
        {
            if (room.Area <= 12 || room.bounds.width <= 2 || room.bounds.height <= 2)
            {
                pattern = GenericStructuralLayerPattern.FullStorey;
                return new HashSet<Vector2Int>(room.cells);
            }

            if (thresholds.Count <= 1)
            {
                pattern = GenericStructuralLayerPattern.Balcony;
                return BuildBalconyCells(room, thresholds[0]);
            }

            if (thresholds.Count >= 3 || ThresholdsShareBoundarySide(room, thresholds))
            {
                pattern = GenericStructuralLayerPattern.PerimeterRing;
                HashSet<Vector2Int> ring = BuildPerimeterRingCells(room, thresholds);
                if (GenericLayerContainsRequirements(ring, thresholds) && IsConnectedCellSet(ring))
                {
                    return ring;
                }
            }

            pattern = GenericStructuralLayerPattern.PartialGallery;
            HashSet<Vector2Int> gallery = BuildPartialGalleryCells(room, thresholds);
            if (GenericLayerContainsRequirements(gallery, thresholds) && IsConnectedCellSet(gallery))
            {
                return gallery;
            }

            pattern = GenericStructuralLayerPattern.FullStorey;
            return new HashSet<Vector2Int>(room.cells);
        }

        private static HashSet<Vector2Int> BuildBalconyCells(
            RoomFootprint room,
            Vector2Int threshold)
        {
            var result = new HashSet<Vector2Int> { threshold };
            foreach (Vector2Int direction in GenericLayerDirections)
            {
                Vector2Int neighbor = threshold + direction;
                if (room.Contains(neighbor))
                {
                    result.Add(neighbor);
                }
            }

            return result;
        }

        private static HashSet<Vector2Int> BuildPartialGalleryCells(
            RoomFootprint room,
            IReadOnlyList<Vector2Int> thresholds)
        {
            var skeleton = new HashSet<Vector2Int> { thresholds[0] };
            for (int index = 1; index < thresholds.Count; index++)
            {
                if (!TryFindRoomPath(room, thresholds[index], skeleton, out List<Vector2Int> path))
                {
                    return new HashSet<Vector2Int>();
                }

                skeleton.UnionWith(path);
            }

            var gallery = new HashSet<Vector2Int>(skeleton);
            foreach (Vector2Int cell in SortedCells(skeleton))
            {
                foreach (Vector2Int direction in GenericLayerDirections)
                {
                    Vector2Int neighbor = cell + direction;
                    if (room.Contains(neighbor))
                    {
                        gallery.Add(neighbor);
                    }
                }
            }

            return gallery;
        }

        private static HashSet<Vector2Int> BuildPerimeterRingCells(
            RoomFootprint room,
            IReadOnlyList<Vector2Int> thresholds)
        {
            var ring = new HashSet<Vector2Int>();
            foreach (Vector2Int cell in room.CellsRowMajor())
            {
                if (IsRoomBoundaryCell(room, cell))
                {
                    ring.Add(cell);
                }
            }

            foreach (Vector2Int threshold in thresholds)
            {
                if (ring.Contains(threshold))
                {
                    continue;
                }

                if (!TryFindRoomPath(room, threshold, ring, out List<Vector2Int> path))
                {
                    return new HashSet<Vector2Int>();
                }

                ring.UnionWith(path);
            }

            return ring;
        }

        private static bool TryFindRoomPath(
            RoomFootprint room,
            Vector2Int start,
            HashSet<Vector2Int> targets,
            out List<Vector2Int> path)
        {
            path = new List<Vector2Int>();
            if (!room.Contains(start) || targets == null || targets.Count == 0)
            {
                return false;
            }

            var queue = new Queue<Vector2Int>();
            var previous = new Dictionary<Vector2Int, Vector2Int>();
            var visited = new HashSet<Vector2Int> { start };
            queue.Enqueue(start);
            Vector2Int reached = default;
            bool found = false;
            while (queue.Count > 0 && !found)
            {
                Vector2Int current = queue.Dequeue();
                if (targets.Contains(current))
                {
                    reached = current;
                    found = true;
                    break;
                }

                foreach (Vector2Int direction in GenericLayerDirections)
                {
                    Vector2Int next = current + direction;
                    if (!room.Contains(next) || !visited.Add(next))
                    {
                        continue;
                    }

                    previous[next] = current;
                    queue.Enqueue(next);
                }
            }

            if (!found)
            {
                return false;
            }

            for (Vector2Int current = reached;; current = previous[current])
            {
                path.Add(current);
                if (current == start)
                {
                    break;
                }
            }

            path.Reverse();
            return true;
        }

        private static bool ThresholdsShareBoundarySide(
            RoomFootprint room,
            IReadOnlyList<Vector2Int> thresholds)
        {
            int commonSides = BoundarySideMask(room, thresholds[0]);
            for (int index = 1; index < thresholds.Count; index++)
            {
                commonSides &= BoundarySideMask(room, thresholds[index]);
            }

            return commonSides != 0;
        }

        private static int BoundarySideMask(RoomFootprint room, Vector2Int cell)
        {
            int mask = 0;
            mask |= !room.Contains(cell + Vector2Int.left) ? 1 : 0;
            mask |= !room.Contains(cell + Vector2Int.right) ? 2 : 0;
            mask |= !room.Contains(cell + Vector2Int.down) ? 4 : 0;
            mask |= !room.Contains(cell + Vector2Int.up) ? 8 : 0;
            return mask;
        }

        private static bool IsRoomBoundaryCell(RoomFootprint room, Vector2Int cell)
        {
            foreach (Vector2Int direction in GenericLayerDirections)
            {
                if (!room.Contains(cell + direction))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool GenericLayerContainsRequirements(
            HashSet<Vector2Int> cells,
            IReadOnlyList<Vector2Int> thresholds)
        {
            if (cells == null || cells.Count == 0)
            {
                return false;
            }

            foreach (Vector2Int threshold in thresholds)
            {
                if (!cells.Contains(threshold))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsConnectedCellSet(HashSet<Vector2Int> cells)
        {
            if (cells == null || cells.Count == 0)
            {
                return false;
            }

            Vector2Int start = default;
            foreach (Vector2Int cell in SortedCells(cells))
            {
                start = cell;
                break;
            }

            var visited = new HashSet<Vector2Int> { start };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Vector2Int current = queue.Dequeue();
                foreach (Vector2Int direction in GenericLayerDirections)
                {
                    Vector2Int next = current + direction;
                    if (cells.Contains(next) && visited.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            return visited.Count == cells.Count;
        }

        private static Vector2Int[] GenericLayerSupportCells(
            RoomFootprint room,
            HashSet<Vector2Int> layerCells)
        {
            var supports = new List<Vector2Int>();
            foreach (Vector2Int cell in SortedCells(layerCells))
            {
                if (IsRoomBoundaryCell(room, cell))
                {
                    supports.Add(cell);
                }
            }

            // An interior-only partial gallery still receives one bracket at a
            // deterministic cell; support is a structural reservation, not a
            // parallel geometry collection.
            if (supports.Count == 0)
            {
                foreach (Vector2Int cell in SortedCells(layerCells))
                {
                    supports.Add(cell);
                    break;
                }
            }

            return supports.ToArray();
        }

        /// <summary>
        /// Slice 3's no-recipe fixture. It exercises the production producer,
        /// the four footprint-derived shapes, the prism registrations, and the
        /// real floor/stair port graph without selecting or constructing a
        /// <see cref="DungeonRecipeAsset"/>.
        /// </summary>
        private static string BuildSlice3GenericStructuralLayerSnapshot()
        {
            var wideRoom = RoomFootprint.FromRect(new RectInt(0, 0, 7, 7));
            var thinRoom = RoomFootprint.FromRect(new RectInt(0, 0, 2, 6));
            HashSet<Vector2Int> full = BuildGenericLayerCells(
                thinRoom,
                new[] { new Vector2Int(0, 0), new Vector2Int(1, 5) },
                out GenericStructuralLayerPattern fullPattern);
            HashSet<Vector2Int> balcony = BuildGenericLayerCells(
                wideRoom,
                new[] { new Vector2Int(0, 3) },
                out GenericStructuralLayerPattern balconyPattern);
            HashSet<Vector2Int> gallery = BuildGenericLayerCells(
                wideRoom,
                new[] { new Vector2Int(0, 3), new Vector2Int(6, 3) },
                out GenericStructuralLayerPattern galleryPattern);
            HashSet<Vector2Int> ring = BuildGenericLayerCells(
                wideRoom,
                new[] { new Vector2Int(0, 1), new Vector2Int(0, 5) },
                out GenericStructuralLayerPattern ringPattern);

            var upperNeighbor = RoomFootprint.FromRect(new RectInt(-4, 2, 2, 3));
            var upperPath = new List<Vector2Int>
            {
                new Vector2Int(0, 3),
                new Vector2Int(-1, 3),
                new Vector2Int(-2, 3),
                new Vector2Int(-3, 3)
            };
            var connection = RoomConnection.ForRouteEdge(
                fromRoom: 0,
                toRoom: 1,
                edgeId: "generic-gallery-exit",
                plannedBand: LevelBand.SpanningEndpoints(MajorRiseLevels, MajorRiseLevels),
                path: upperPath,
                fromLayerId: "gallery");
            var floorCells = new HashSet<Vector2Int>(wideRoom.cells);
            floorCells.UnionWith(upperNeighbor.cells);
            floorCells.UnionWith(upperPath);
            var layout = new DungeonLayout(
                floorCells,
                new List<RoomFootprint> { wideRoom, upperNeighbor },
                new List<RoomConnection> { connection });
            var nodes = new[]
            {
                new RouteNodeIntent(
                    "generic-room",
                    "junction",
                    "gallery",
                    mainRouteOrder: 0,
                    branchOrder: -1,
                    relativeElevationLevels: 0,
                    layers: new[] { new RouteTopologyLayer("gallery", MajorRiseLevels) }),
                new RouteNodeIntent(
                    "upper-neighbor",
                    "connector",
                    "return",
                    mainRouteOrder: 1,
                    branchOrder: -1,
                    relativeElevationLevels: MajorRiseLevels)
            };
            var surfaces = new SurfaceField(new Dictionary<Vector2Int, int>());
            foreach (Vector2Int cell in wideRoom.CellsRowMajor())
            {
                surfaces.TrySetFloorLevel(cell, 0, out _);
            }

            foreach (Vector2Int cell in upperNeighbor.CellsRowMajor())
            {
                surfaces.TrySetFloorLevel(cell, MajorRiseLevels, out _);
            }

            for (int index = 1; index < upperPath.Count; index++)
            {
                surfaces.AddCorridorSurface(upperPath[index], MajorRiseLevels);
            }

            foreach (Vector2Int cell in new[]
                     {
                         new Vector2Int(-2, 2),
                         new Vector2Int(-1, 2),
                         new Vector2Int(0, 2)
                     })
            {
                surfaces.TrySetFloorLevel(cell, 0, out _);
            }

            var prisms = new PrismLedger();
            var generatedOpeningCandidates = new List<PlanOpening>();
            bool realized = TryRealizeGenericStructuralRoomLayers(
                layout,
                nodes,
                Array.Empty<RecipePlacement>(),
                surfaces,
                prisms,
                generatedOpeningCandidates,
                out string realizationFailure);
            var transitions = new List<ElevationEdgeModel.TransitionEdge>
            {
                new ElevationEdgeModel.TransitionEdge(
                    new Vector2Int(-2, 3),
                    MajorRiseLevels,
                    new Vector2Int(-2, 2),
                    0,
                    "generic-structural-fixture-stair",
                    EmbeddedStairPlacementClass)
            };
            bool openingsBuilt = TryBuildPlanOpenings(
                Array.Empty<ExternalConnectorPromontoryResolution>(),
                Array.Empty<RecipePlacement>(),
                generatedOpeningCandidates,
                surfaces,
                transitions,
                out PlanOpening[] generatedOpenings,
                out string openingFailure);
            bool graphBuilt = TryBuildFloorStairPortGraph(
                surfaces,
                transitions,
                generatedOpenings,
                out FloorStairPortGraph portGraph,
                out string graphFailure);
            string reachability = graphFailure;
            bool fallFreeConnected = false;
            if (graphBuilt)
            {
                fallFreeConnected = portGraph.IsFallFreeConnected(out reachability);
            }

            Vector2Int boundThreshold = upperPath[0];
            OwnerKey generatedOwner = prisms.SurfaceOwnerAt(
                new SurfaceKey(boundThreshold, MajorRiseLevels));
            bool volumesValid = prisms.TryValidateOpenVolumes(
                surfaces,
                out string volumeFailure);
            bool headroomValid = prisms.TryValidateSurfaceHeadroom(
                surfaces,
                out string headroomFailure);

            var landingCandidate = new StairTransitionCandidate(
                transitionIndex: 0,
                lowerLandingCell: new Vector2Int(-2, 2),
                upperLandingCell: boundThreshold,
                lowerLandingCells: new[] { new Vector2Int(-2, 2) },
                upperLandingCells: new[] { boundThreshold },
                footprintCells: Array.Empty<Vector2Int>(),
                option: default);
            bool boundLandingAccepted = !prisms.ConflictsWith(
                landingCandidate,
                lowerLevel: 0,
                upperLevel: MajorRiseLevels);
            var unboundedLedger = new PrismLedger();
            unboundedLedger.Register(
                new OwnerKey(OwnerFamily.Transition, "unbounded-regression"),
                new[] { boundThreshold },
                Array.Empty<Vector2Int>(),
                Array.Empty<Vector2Int>());
            bool unboundedLandingStillRejected = unboundedLedger.ConflictsWith(
                landingCandidate,
                lowerLevel: 0,
                upperLevel: MajorRiseLevels);

            int occupiedCells = CountPrismCells(prisms, PrismKind.Footprint);
            int supportCells = CountPrismCells(prisms, PrismKind.Support);
            int clearanceCells = CountPrismCells(prisms, PrismKind.FootprintClearance);
            int openVolumeCells = CountPrismCells(prisms, PrismKind.OpenVolume);

            bool slotlessLayerAccepted = false;
            if (TryParseLayerSchemaProbe(
                    LayerSchemaProbeJson
                        .Replace(", { \"layers\": { \"gallery\": 4 } }", string.Empty)
                        .Replace(
                            "\"C\": [\"probe-c\", \"culmination\", \"culmination\", 8, { \"main\": 2 }]",
                            "\"C\": [\"probe-c\", \"culmination\", \"culmination\", 8, { \"main\": 2 }, { \"layers\": { \"gallery\": 4 } }]")
                        .Replace("\"fromLayer\": \"gallery\"", "\"toLayer\": \"gallery\""),
                    out DungeonRouteTopology slotless))
            {
                var violations = new List<string>();
                ValidateNodeLayers(2, slotless.nodes[2], slotless, violations);
                slotlessLayerAccepted = violations.Count == 0;
            }

            return string.Join("\n", new[]
            {
                $"patterns.full={fullPattern == GenericStructuralLayerPattern.FullStorey && full.SetEquals(thinRoom.cells)}",
                $"patterns.balcony={balconyPattern == GenericStructuralLayerPattern.Balcony && balcony.Count < wideRoom.Area && IsConnectedCellSet(balcony)}",
                $"patterns.partialGallery={galleryPattern == GenericStructuralLayerPattern.PartialGallery && gallery.Count < wideRoom.Area && IsConnectedCellSet(gallery)}",
                $"patterns.perimeterRing={ringPattern == GenericStructuralLayerPattern.PerimeterRing && ring.Count < wideRoom.Area && IsConnectedCellSet(ring)}",
                $"producer.noRecipe={realized}",
                $"producer.failure={realizationFailure}",
                $"producer.basePreserved={surfaces.HasSurfaceAt(boundThreshold, 0)}",
                $"producer.boundLayerRealized={surfaces.HasSurfaceAt(boundThreshold, MajorRiseLevels)}",
                $"producer.stackedSurfaces={surfaces.Count - surfaces.FlooredCellCount}",
                $"producer.generatedOwner={generatedOwner.Token}",
                $"producer.occupiedCells={occupiedCells}",
                $"producer.supportCells={supportCells}",
                $"producer.clearanceCells={clearanceCells}",
                $"producer.openVolumeCells={openVolumeCells}",
                $"producer.generatedOpeningCandidates={generatedOpeningCandidates.Count}",
                $"producer.generatedApertures={generatedOpenings.Length}",
                $"producer.openingsBuilt={openingsBuilt}",
                $"producer.openingFailure={openingFailure}",
                $"producer.openVolumesValid={volumesValid}",
                $"producer.volumeFailure={volumeFailure}",
                $"producer.headroomValid={headroomValid}",
                $"producer.headroomFailure={headroomFailure}",
                $"producer.boundLandingAccepted={boundLandingAccepted}",
                $"producer.unboundedLandingStillRejected={unboundedLandingStillRejected}",
                $"navigation.graphBuilt={graphBuilt}",
                $"navigation.directedFalls={(graphBuilt ? portGraph.DirectedEdgeCount : 0)}",
                $"navigation.fallFreeConnected={fallFreeConnected}",
                $"navigation.reachability={reachability}",
                $"validator.slotlessLayerAccepted={slotlessLayerAccepted}"
            });
        }

        private static int CountPrismCells(PrismLedger prisms, PrismKind kind)
        {
            int count = 0;
            foreach (Vector2Int _ in prisms.CellsOfKind(kind))
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// Slice 5's pure realization fixture: planned level connections,
        /// bridge-owned void, shared-space rims, one aperture fall and its
        /// reversible return route, all without recipe identity.
        /// </summary>
        private static string BuildSlice5ConnectionRealizationSnapshot()
        {
            var firstRoom = RoomFootprint.FromRect(new RectInt(0, 0, 2, 2));
            var abuttingRoom = RoomFootprint.FromRect(new RectInt(2, 0, 2, 2));
            var separatedRoom = RoomFootprint.FromRect(new RectInt(5, 0, 2, 2));
            var direct = RoomConnection.ForRouteEdge(
                0,
                1,
                "direct",
                LevelBand.SpanningEndpoints(0, 0),
                new List<Vector2Int>
                {
                    new Vector2Int(1, 0),
                    new Vector2Int(2, 0)
                });
            var routed = RoomConnection.ForRouteEdge(
                0,
                1,
                "routed",
                LevelBand.SpanningEndpoints(0, 0),
                new List<Vector2Int>
                {
                    new Vector2Int(1, 0),
                    new Vector2Int(2, 0),
                    new Vector2Int(3, 0),
                    new Vector2Int(4, 0),
                    new Vector2Int(5, 0)
                });
            bool directClassified = string.Equals(
                LevelConnectionPlacementClass(
                    direct,
                    new[] { firstRoom, abuttingRoom }),
                DirectDoorwayPlacementClass,
                StringComparison.Ordinal);
            bool routedClassified = string.Equals(
                LevelConnectionPlacementClass(
                    routed,
                    new[] { firstRoom, separatedRoom }),
                RoutedCorridorPlacementClass,
                StringComparison.Ordinal);

            var identityTopology = new DungeonRouteTopology(
                "slice5-identity-fixture",
                "Slice 5 identity fixture",
                "slice5-fixture-v1",
                "<fixture>",
                Array.Empty<RouteTopologyNode>(),
                Array.Empty<RouteTopologyEdge>(),
                Array.Empty<RouteTopologySlot>(),
                string.Empty,
                0,
                1,
                1,
                Array.Empty<RouteOverlookIntent>(),
                0,
                1,
                false,
                false,
                1,
                MajorRiseLevels,
                true,
                default,
                Array.Empty<RouteLaneGap>(),
                Array.Empty<RouteLaneGap>(),
                1,
                1);
            var identityIntent = new RouteIntent(
                1,
                "slice5-fixture-v1",
                identityTopology,
                new[]
                {
                    new RouteNodeIntent("a", "arrival", "arrival", 0, -1, 0),
                    new RouteNodeIntent("b", "culmination", "culmination", 1, -1, 0)
                },
                new[]
                {
                    new RouteTraversalIntent(
                        "direct",
                        0,
                        1,
                        0,
                        RouteTransitionKind.LevelCorridor)
                },
                default,
                RouteElevationPolicy.AscendingSpine,
                Array.Empty<RecipeSlotIntent>(),
                string.Empty,
                0,
                1,
                Array.Empty<RouteOverlookIntent>(),
                false);
            var identityRequirements = new RouteTierRequirements(
                identityIntent,
                new RectInt(0, 0, 4, 2),
                Array.Empty<Vector2Int>(),
                default,
                default,
                default,
                default,
                Array.Empty<Vector2Int>(),
                Array.Empty<RecipePlacement>());
            var identityFloor = new HashSet<Vector2Int>(firstRoom.cells);
            identityFloor.UnionWith(abuttingRoom.cells);
            bool exactIdentityAccepted = FindConnectionIdentityViolations(
                new DungeonLayout(
                    identityFloor,
                    new List<RoomFootprint> { firstRoom, abuttingRoom },
                    new List<RoomConnection> { direct }),
                identityRequirements).Count == 0;
            bool duplicateIdentityRejected = FindConnectionIdentityViolations(
                new DungeonLayout(
                    identityFloor,
                    new List<RoomFootprint> { firstRoom, abuttingRoom },
                    new List<RoomConnection> { direct, direct }),
                identityRequirements).Count > 0;
            bool missingIdentityRejected = FindConnectionIdentityViolations(
                new DungeonLayout(
                    identityFloor,
                    new List<RoomFootprint> { firstRoom, abuttingRoom },
                    new List<RoomConnection>()),
                identityRequirements).Count > 0;
            bool inventedIdentityRejected = FindConnectionIdentityViolations(
                new DungeonLayout(
                    identityFloor,
                    new List<RoomFootprint> { firstRoom, abuttingRoom },
                    new List<RoomConnection>
                    {
                        RoomConnection.ForSynthesizedLoop(
                            0,
                            1,
                            LevelBand.SpanningEndpoints(0, 0),
                            direct.path)
                    }),
                identityRequirements).Count > 0;

            var bridgeCells = new[]
            {
                new Vector2Int(20, 20),
                new Vector2Int(21, 20)
            };
            var bridgeOwner = new OwnerKey(
                OwnerFamily.Transition,
                "connection-stair:fixture-bridge");
            var bridgePrisms = new PrismLedger();
            bridgePrisms.RegisterSpanDeck(
                bridgeCells,
                MajorRiseLevels,
                bridgeOwner);
            bool bridgeVolumeRegistered = TryRegisterPlannedBridgeOpenVolume(
                bridgePrisms,
                "fixture-bridge",
                bridgeCells,
                0,
                MajorRiseLevels,
                bridgeOwner,
                out _);
            var emptyBridgeSurfaces = new SurfaceField(
                new Dictionary<Vector2Int, int>());
            bool bridgeVolumeValid = bridgePrisms.TryValidateOpenVolumes(
                emptyBridgeSurfaces,
                out _);
            var filledBridgeSurfaces = new SurfaceField(
                new Dictionary<Vector2Int, int>
                {
                    [bridgeCells[0]] = 2
                });
            bool bridgeFillRejected = !bridgePrisms.TryValidateOpenVolumes(
                filledBridgeSurfaces,
                out string bridgeFillFailure) &&
                bridgeFillFailure.StartsWith(
                    "[OPEN_VOLUME_VIOLATION]",
                    StringComparison.Ordinal);

            var atriumRoom = RoomFootprint.FromRect(new RectInt(0, 0, 7, 7));
            HashSet<Vector2Int> atriumRing = BuildPerimeterRingCells(
                atriumRoom,
                new[] { new Vector2Int(0, 1), new Vector2Int(0, 5) });
            var atriumVoid = new HashSet<Vector2Int>(atriumRoom.cells);
            atriumVoid.ExceptWith(atriumRing);
            int atriumRimEdges = CountSharedVoidRimEdges(atriumRing, atriumVoid);
            HashSet<Vector2Int> balcony = BuildBalconyCells(
                atriumRoom,
                new Vector2Int(0, 3));
            var balconyVoid = new HashSet<Vector2Int>(atriumRoom.cells);
            balconyVoid.ExceptWith(balcony);
            int balconyRimEdges = CountSharedVoidRimEdges(balcony, balconyVoid);

            var sharedSurfaces = new SurfaceField(
                new Dictionary<Vector2Int, int>());
            foreach (Vector2Int cell in atriumRoom.CellsRowMajor())
            {
                sharedSurfaces.AddFloorLevel(cell, 0);
            }

            foreach (Vector2Int cell in SortedCells(atriumRing))
            {
                sharedSurfaces.AddSurface(
                    cell,
                    MajorRiseLevels,
                    SurfaceKind.Floor);
            }

            var sharedOwner = new OwnerKey(
                OwnerFamily.Opening,
                "fixture-atrium");
            var apertureCandidates = new List<PlanOpening>();
            AddGeneratedApertureCandidates(
                "fixture-room",
                "gallery",
                atriumRing,
                atriumVoid,
                MajorRiseLevels,
                sharedOwner,
                apertureCandidates);
            var returnTransitions = new List<ElevationEdgeModel.TransitionEdge>
            {
                new ElevationEdgeModel.TransitionEdge(
                    new Vector2Int(0, 0),
                    MajorRiseLevels,
                    new Vector2Int(1, 1),
                    0,
                    "fixture-return-stair",
                    EmbeddedStairPlacementClass)
            };
            bool openingsBuilt = TryBuildPlanOpenings(
                Array.Empty<ExternalConnectorPromontoryResolution>(),
                Array.Empty<RecipePlacement>(),
                apertureCandidates,
                sharedSurfaces,
                returnTransitions,
                out PlanOpening[] openings,
                out string openingFailure);
            bool graphBuilt = TryBuildFloorStairPortGraph(
                sharedSurfaces,
                returnTransitions,
                openings,
                out FloorStairPortGraph graph,
                out string graphFailure);
            string reachability = graphFailure;
            bool fallFreeConnected = false;
            if (graphBuilt)
            {
                fallFreeConnected = graph.IsFallFreeConnected(out reachability);
            }

            List<ElevationEdgeModel.OpenFloorEdge> renderedOpenEdges =
                BuildPlannedOpenEdges(openings);
            bool surfaceScopedAperture = openings.Length == 1 &&
                openings[0].kind == OpeningKind.Aperture &&
                openings[0].IsSurfaceScoped &&
                renderedOpenEdges.Count == 1 &&
                renderedOpenEdges[0].IsSurfaceScoped;

            return string.Join("\n", new[]
            {
                $"level.directDoorway={directClassified}",
                $"level.routedCorridor={routedClassified}",
                $"identity.exactAccepted={exactIdentityAccepted}",
                $"identity.duplicateRejected={duplicateIdentityRejected}",
                $"identity.missingRejected={missingIdentityRejected}",
                $"identity.inventedRejected={inventedIdentityRejected}",
                $"vertical.fourUnitAccepted={AreStructuralConnectionLevels(0, MajorRiseLevels)}",
                $"vertical.eightUnitAccepted={AreStructuralConnectionLevels(0, DoubleMajorRiseLevels)}",
                $"vertical.stairClass={EmbeddedStairPlacementClass}",
                $"vertical.stairwellClass={StairwellStairPlacementClass}",
                $"bridge.class={ExternalSpanStairPlacementClass}",
                $"bridge.volumeRegistered={bridgeVolumeRegistered}",
                $"bridge.volumeValid={bridgeVolumeValid}",
                $"bridge.fillRejected={bridgeFillRejected}",
                $"shared.balconyRimEdges={balconyRimEdges}",
                $"shared.atriumRimEdges={atriumRimEdges}",
                $"shared.apertureCandidates={apertureCandidates.Count}",
                $"shared.openingsBuilt={openingsBuilt}",
                $"shared.openingFailure={openingFailure}",
                $"shared.apertures={openings.Length}",
                $"shared.surfaceScopedAperture={surfaceScopedAperture}",
                $"navigation.graphBuilt={graphBuilt}",
                $"navigation.directedFalls={(graphBuilt ? graph.DirectedEdgeCount : 0)}",
                $"navigation.fallFreeConnected={fallFreeConnected}",
                $"navigation.reachability={reachability}"
            });
        }

        private static int CountSharedVoidRimEdges(
            HashSet<Vector2Int> surfaceCells,
            HashSet<Vector2Int> voidCells)
        {
            int count = 0;
            foreach (Vector2Int cell in SortedCells(surfaceCells))
            {
                foreach (Vector2Int direction in GenericLayerDirections)
                {
                    count += voidCells.Contains(cell + direction) ? 1 : 0;
                }
            }

            return count;
        }
    }
}

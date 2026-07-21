using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // Phase 4 schema probe: one deliberately narrow authored episode. These
    // values describe only the throne-hall composition proven by this slice;
    // they are not a general recipe schema, catalog entry, or renderer plan.
    internal sealed partial class DungeonLabGenerator
    {
        private const string ThroneHallEpisodeId = "episode_throne_twin_stairs_01";
        private const int ThroneHallSlotNode = Phase1VistaTargetNode;
        private const string ThroneHallEntryEdgeId = "main-3-4";
        private const string ThroneHallExitEdgeId = "main-4-5";
        private const int ThroneHallGalleryRiseLevels = 1;
        private const int ThroneHallCoupledStairCount = 2;

        private static readonly string[] ThroneHallFocalDesignIds =
        {
            "dais_backed_angle_bay_r1",
            "dais_backed_round_bay_r1"
        };

        private enum ThroneThresholdKind
        {
            ProcessionalEntry,
            ProcessionalExit
        }

        private sealed class ThroneHallEpisodeIntent
        {
            public readonly string id;
            public readonly int slotNode;
            public readonly string focalAxisBinding;
            public readonly Vector2Int dominantRoomSize;
            public readonly Vector2Int focalZoneSize;
            public readonly Vector2Int sideGallerySize;
            public readonly int galleryRiseLevels;
            public readonly int coupledStairCount;
            public readonly string[] allowedFocalDesignIds;
            public readonly ThroneThresholdIntent[] thresholds;

            public ThroneHallEpisodeIntent()
            {
                id = ThroneHallEpisodeId;
                slotNode = ThroneHallSlotNode;
                focalAxisBinding = "vista-source-to-target";
                dominantRoomSize = new Vector2Int(7, 5);
                focalZoneSize = new Vector2Int(3, 5);
                sideGallerySize = new Vector2Int(4, 2);
                galleryRiseLevels = ThroneHallGalleryRiseLevels;
                coupledStairCount = ThroneHallCoupledStairCount;
                allowedFocalDesignIds = (string[])ThroneHallFocalDesignIds.Clone();
                thresholds = new[]
                {
                    new ThroneThresholdIntent("processional-entry", ThroneHallEntryEdgeId, ThroneThresholdKind.ProcessionalEntry),
                    new ThroneThresholdIntent("processional-exit", ThroneHallExitEdgeId, ThroneThresholdKind.ProcessionalExit)
                };
            }
        }

        private readonly struct ThroneThresholdIntent
        {
            public readonly string id;
            public readonly string edgeId;
            public readonly ThroneThresholdKind kind;

            public ThroneThresholdIntent(string id, string edgeId, ThroneThresholdKind kind)
            {
                this.id = id;
                this.edgeId = edgeId;
                this.kind = kind;
            }
        }

        private readonly struct ThroneThresholdPlacement
        {
            public readonly string id;
            public readonly string edgeId;
            public readonly ThroneThresholdKind kind;
            public readonly Vector2Int cell;
            public readonly Vector2Int outwardDirection;
            public readonly int expectedRelativeLevel;

            public ThroneThresholdPlacement(
                ThroneThresholdIntent intent,
                Vector2Int cell,
                Vector2Int outwardDirection,
                int expectedRelativeLevel)
            {
                id = intent.id;
                edgeId = intent.edgeId;
                kind = intent.kind;
                this.cell = cell;
                this.outwardDirection = outwardDirection;
                this.expectedRelativeLevel = expectedRelativeLevel;
            }
        }

        private readonly struct ThroneStairPlacement
        {
            public readonly string id;
            public readonly Vector2Int lowerTransitionCell;
            public readonly Vector2Int upperTransitionCell;
            public readonly Vector2Int[] lowerLandingCells;
            public readonly Vector2Int[] upperLandingCells;
            public readonly Vector2Int[] footprintCells;
            public readonly Vector2Int climbDirection;

            public ThroneStairPlacement(
                string id,
                Vector2Int lowerTransitionCell,
                Vector2Int upperTransitionCell,
                Vector2Int lowerLandingCell,
                Vector2Int upperLandingCell,
                Vector2Int climbDirection)
            {
                this.id = id;
                this.lowerTransitionCell = lowerTransitionCell;
                this.upperTransitionCell = upperTransitionCell;
                lowerLandingCells = new[] { lowerLandingCell };
                upperLandingCells = new[] { upperLandingCell };
                footprintCells = new[] { lowerTransitionCell };
                this.climbDirection = climbDirection;
            }
        }

        private sealed class ThroneHallEpisodePlacement
        {
            public readonly ThroneHallEpisodeIntent intent;
            public readonly int roomIndex;
            public readonly Vector2Int roomCenter;
            public readonly Vector2Int focalAxis;
            public readonly Vector2Int transverseAxis;
            public readonly Vector2Int[] roomCells;
            public readonly Vector2Int[] focalZoneCells;
            public readonly Vector2Int[][] sideGalleryCells;
            public readonly ThroneThresholdPlacement[] thresholds;
            public readonly ThroneStairPlacement[] twinStairs;
            public readonly string selectedFocalDesignId;
            public readonly Vector2Int showpieceOriginCell;
            public readonly float showpieceYawDegrees;

            public ThroneHallEpisodePlacement(
                ThroneHallEpisodeIntent intent,
                int roomIndex,
                Vector2Int roomCenter,
                Vector2Int focalAxis,
                Vector2Int transverseAxis,
                Vector2Int[] roomCells,
                Vector2Int[] focalZoneCells,
                Vector2Int[][] sideGalleryCells,
                ThroneThresholdPlacement[] thresholds,
                ThroneStairPlacement[] twinStairs,
                string selectedFocalDesignId,
                Vector2Int showpieceOriginCell,
                float showpieceYawDegrees)
            {
                this.intent = intent;
                this.roomIndex = roomIndex;
                this.roomCenter = roomCenter;
                this.focalAxis = focalAxis;
                this.transverseAxis = transverseAxis;
                this.roomCells = roomCells;
                this.focalZoneCells = focalZoneCells;
                this.sideGalleryCells = sideGalleryCells;
                this.thresholds = thresholds;
                this.twinStairs = twinStairs;
                this.selectedFocalDesignId = selectedFocalDesignId;
                this.showpieceOriginCell = showpieceOriginCell;
                this.showpieceYawDegrees = showpieceYawDegrees;
            }

            public bool TryGetThreshold(string edgeId, out ThroneThresholdPlacement threshold)
            {
                foreach (ThroneThresholdPlacement candidate in thresholds)
                {
                    if (string.Equals(candidate.edgeId, edgeId, StringComparison.Ordinal))
                    {
                        threshold = candidate;
                        return true;
                    }
                }

                threshold = default;
                return false;
            }
        }

        private readonly struct ThroneHallEpisodeResolution
        {
            public readonly string id;
            public readonly int roomIndex;
            public readonly Vector2Int focalAxis;
            public readonly Vector2Int[] focalZoneCells;
            public readonly Vector2Int[][] sideGalleryCells;
            public readonly ThroneThresholdPlacement[] thresholds;
            public readonly ThroneStairPlacement[] twinStairs;
            public readonly string selectedFocalDesignId;
            public readonly Vector2Int showpieceOriginCell;
            public readonly float showpieceYawDegrees;
            public readonly int baseLevel;
            public readonly int galleryLevel;
            public readonly bool atomicAndValid;

            public ThroneHallEpisodeResolution(
                ThroneHallEpisodePlacement placement,
                int baseLevel,
                bool atomicAndValid)
            {
                id = placement?.intent?.id ?? string.Empty;
                roomIndex = placement?.roomIndex ?? -1;
                focalAxis = placement?.focalAxis ?? Vector2Int.zero;
                focalZoneCells = placement?.focalZoneCells ?? Array.Empty<Vector2Int>();
                sideGalleryCells = placement?.sideGalleryCells ?? Array.Empty<Vector2Int[]>();
                thresholds = placement?.thresholds ?? Array.Empty<ThroneThresholdPlacement>();
                twinStairs = placement?.twinStairs ?? Array.Empty<ThroneStairPlacement>();
                selectedFocalDesignId = placement?.selectedFocalDesignId ?? string.Empty;
                showpieceOriginCell = placement?.showpieceOriginCell ?? default;
                showpieceYawDegrees = placement?.showpieceYawDegrees ?? 0f;
                this.baseLevel = baseLevel;
                galleryLevel = baseLevel + (placement?.intent?.galleryRiseLevels ?? 0);
                this.atomicAndValid = atomicAndValid;
            }
        }

        private static ThroneHallEpisodeIntent BuildThroneHallEpisodeIntent()
        {
            return new ThroneHallEpisodeIntent();
        }

        private static List<RectInt> BuildThroneHallRoomParts(
            ThroneHallEpisodeIntent intent,
            Vector2Int center,
            Vector2Int focalAxis)
        {
            Vector2Int transverse = new Vector2Int(-focalAxis.y, focalAxis.x);
            int dominantFocalRadius = intent.dominantRoomSize.x / 2;
            int dominantTransverseRadius = intent.dominantRoomSize.y / 2;
            int galleryFocalMax = intent.sideGallerySize.x - 1;
            int galleryTransverseOuter = dominantTransverseRadius + intent.sideGallerySize.y;
            return new List<RectInt>
            {
                OrientedRect(
                    center,
                    focalAxis,
                    transverse,
                    -dominantFocalRadius,
                    dominantFocalRadius,
                    -dominantTransverseRadius,
                    dominantTransverseRadius),
                OrientedRect(
                    center,
                    focalAxis,
                    transverse,
                    0,
                    galleryFocalMax,
                    dominantTransverseRadius + 1,
                    galleryTransverseOuter),
                OrientedRect(
                    center,
                    focalAxis,
                    transverse,
                    0,
                    galleryFocalMax,
                    -galleryTransverseOuter,
                    -dominantTransverseRadius - 1)
            };
        }

        private static RectInt OrientedRect(
            Vector2Int center,
            Vector2Int focalAxis,
            Vector2Int transverseAxis,
            int focalMin,
            int focalMax,
            int transverseMin,
            int transverseMax)
        {
            Vector2Int first = OrientedCell(center, focalAxis, transverseAxis, focalMin, transverseMin);
            int minX = first.x;
            int maxX = first.x;
            int minY = first.y;
            int maxY = first.y;
            foreach (Vector2Int corner in new[]
                     {
                         OrientedCell(center, focalAxis, transverseAxis, focalMin, transverseMax),
                         OrientedCell(center, focalAxis, transverseAxis, focalMax, transverseMin),
                         OrientedCell(center, focalAxis, transverseAxis, focalMax, transverseMax)
                     })
            {
                minX = Mathf.Min(minX, corner.x);
                maxX = Mathf.Max(maxX, corner.x);
                minY = Mathf.Min(minY, corner.y);
                maxY = Mathf.Max(maxY, corner.y);
            }

            return new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private static Vector2Int OrientedCell(
            Vector2Int center,
            Vector2Int focalAxis,
            Vector2Int transverseAxis,
            int focal,
            int transverse)
        {
            return center + focalAxis * focal + transverseAxis * transverse;
        }

        private static bool TryPlaceThroneHallEpisode(
            int dungeonSeed,
            int layoutAttempt,
            RouteIntent routeIntent,
            IReadOnlyList<RoomFootprint> rooms,
            IReadOnlyList<Vector2Int> nodeCenters,
            Vector2Int vistaSourceFacing,
            Vector2Int vistaTargetFacing,
            out ThroneHallEpisodePlacement placement,
            out string rejectionReason)
        {
            placement = null;
            rejectionReason = string.Empty;
            ThroneHallEpisodeIntent episode = routeIntent?.landmarkEpisode;
            if (episode == null || episode.slotNode != ThroneHallSlotNode ||
                episode.slotNode < 0 || episode.slotNode >= rooms.Count)
            {
                rejectionReason = "episode intent did not identify the active landmark slot";
                return false;
            }

            if (!string.Equals(episode.focalAxisBinding, "vista-source-to-target", StringComparison.Ordinal) ||
                vistaSourceFacing == Vector2Int.zero ||
                vistaSourceFacing != -vistaTargetFacing)
            {
                rejectionReason = "focal-axis binding did not resolve from the opposed route vista";
                return false;
            }

            Vector2Int focalAxis = vistaSourceFacing;
            Vector2Int transverseAxis = new Vector2Int(-focalAxis.y, focalAxis.x);
            Vector2Int center = nodeCenters[episode.slotNode];
            RoomFootprint room = rooms[episode.slotNode];
            var expectedRoom = new RoomFootprint(BuildThroneHallRoomParts(episode, center, focalAxis));
            if (!room.cells.SetEquals(expectedRoom.cells))
            {
                rejectionReason = "inflated landmark footprint did not match the atomic episode footprint";
                return false;
            }

            var focalZone = new List<Vector2Int>();
            int focalStart = episode.dominantRoomSize.x / 2 - episode.focalZoneSize.x + 1;
            int focalEnd = focalStart + episode.focalZoneSize.x - 1;
            int focalTransverseRadius = episode.focalZoneSize.y / 2;
            for (int focal = focalStart; focal <= focalEnd; focal++)
            {
                for (int transverse = -focalTransverseRadius; transverse <= focalTransverseRadius; transverse++)
                {
                    focalZone.Add(OrientedCell(center, focalAxis, transverseAxis, focal, transverse));
                }
            }

            int dominantTransverseRadius = episode.dominantRoomSize.y / 2;
            int galleryOuter = dominantTransverseRadius + episode.sideGallerySize.y;
            var galleries = new[] { new List<Vector2Int>(), new List<Vector2Int>() };
            for (int focal = 0; focal < episode.sideGallerySize.x; focal++)
            {
                for (int depth = 1; depth <= episode.sideGallerySize.y; depth++)
                {
                    galleries[0].Add(OrientedCell(
                        center,
                        focalAxis,
                        transverseAxis,
                        focal,
                        -dominantTransverseRadius - depth));
                    galleries[1].Add(OrientedCell(
                        center,
                        focalAxis,
                        transverseAxis,
                        focal,
                        dominantTransverseRadius + depth));
                }
            }

            var stairs = new[]
            {
                new ThroneStairPlacement(
                    "twin-stair-negative",
                    OrientedCell(center, focalAxis, transverseAxis, 0, -dominantTransverseRadius),
                    OrientedCell(center, focalAxis, transverseAxis, 0, -dominantTransverseRadius - 1),
                    OrientedCell(center, focalAxis, transverseAxis, 0, -dominantTransverseRadius + 1),
                    OrientedCell(center, focalAxis, transverseAxis, 0, -galleryOuter),
                    -transverseAxis),
                new ThroneStairPlacement(
                    "twin-stair-positive",
                    OrientedCell(center, focalAxis, transverseAxis, 0, dominantTransverseRadius),
                    OrientedCell(center, focalAxis, transverseAxis, 0, dominantTransverseRadius + 1),
                    OrientedCell(center, focalAxis, transverseAxis, 0, dominantTransverseRadius - 1),
                    OrientedCell(center, focalAxis, transverseAxis, 0, galleryOuter),
                    transverseAxis)
            };

            if (episode.coupledStairCount != stairs.Length ||
                episode.galleryRiseLevels != ThroneHallGalleryRiseLevels)
            {
                rejectionReason = "coupled stair or gallery-rise contract did not match the supported episode";
                return false;
            }

            var thresholds = new ThroneThresholdPlacement[episode.thresholds.Length];
            for (int index = 0; index < episode.thresholds.Length; index++)
            {
                ThroneThresholdIntent thresholdIntent = episode.thresholds[index];
                int neighbor = thresholdIntent.kind == ThroneThresholdKind.ProcessionalEntry
                    ? episode.slotNode - 1
                    : episode.slotNode + 1;
                Vector2Int outward = CardinalUnit(nodeCenters[neighbor] - center);
                int transverseSign = IntDot(outward, transverseAxis);
                if (Mathf.Abs(transverseSign) != 1)
                {
                    rejectionReason = $"typed threshold '{thresholdIntent.id}' was not transverse to the focal axis";
                    return false;
                }

                Vector2Int thresholdCell = OrientedCell(
                    center,
                    focalAxis,
                    transverseAxis,
                    -1,
                    transverseSign * dominantTransverseRadius);
                thresholds[index] = new ThroneThresholdPlacement(
                    thresholdIntent,
                    thresholdCell,
                    outward,
                    routeIntent.nodes[episode.slotNode].relativeElevationLevels);
            }

            int designIndex = Phase1Random(
                dungeonSeed,
                layoutAttempt,
                episode.id,
                "focal-variation").Next(episode.allowedFocalDesignIds.Length);
            string selectedDesign = episode.allowedFocalDesignIds[designIndex];
            ResolveThroneShowpieceTransform(
                center,
                focalAxis,
                transverseAxis,
                focalTransverseRadius,
                out Vector2Int showpieceOrigin,
                out float showpieceYaw);

            Vector2Int[] roomCells = SortedCells(room.cells).ToArray();
            Vector2Int[] focalCells = SortedCells(focalZone).ToArray();
            Vector2Int[][] galleryCells =
            {
                SortedCells(galleries[0]).ToArray(),
                SortedCells(galleries[1]).ToArray()
            };
            if (!AllCellsBelongToRoom(room, focalCells, galleryCells, stairs, thresholds))
            {
                rejectionReason = "episode geometry or reservation escaped the landmark footprint";
                return false;
            }

            placement = new ThroneHallEpisodePlacement(
                episode,
                episode.slotNode,
                center,
                focalAxis,
                transverseAxis,
                roomCells,
                focalCells,
                galleryCells,
                thresholds,
                stairs,
                selectedDesign,
                showpieceOrigin,
                showpieceYaw);
            return true;
        }

        private static Vector2Int CardinalUnit(Vector2Int delta)
        {
            return new Vector2Int(Math.Sign(delta.x), Math.Sign(delta.y));
        }

        private static int IntDot(Vector2Int first, Vector2Int second)
        {
            return first.x * second.x + first.y * second.y;
        }

        private static bool AllCellsBelongToRoom(
            RoomFootprint room,
            IReadOnlyList<Vector2Int> focalCells,
            IReadOnlyList<Vector2Int>[] galleryCells,
            IReadOnlyList<ThroneStairPlacement> stairs,
            IReadOnlyList<ThroneThresholdPlacement> thresholds)
        {
            bool ContainsAll(IReadOnlyList<Vector2Int> cells)
            {
                foreach (Vector2Int cell in cells)
                {
                    if (!room.Contains(cell))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (!ContainsAll(focalCells))
            {
                return false;
            }

            foreach (IReadOnlyList<Vector2Int> gallery in galleryCells)
            {
                if (!ContainsAll(gallery))
                {
                    return false;
                }
            }

            foreach (ThroneStairPlacement stair in stairs)
            {
                if (!room.Contains(stair.lowerTransitionCell) ||
                    !room.Contains(stair.upperTransitionCell) ||
                    !ContainsAll(stair.lowerLandingCells) ||
                    !ContainsAll(stair.upperLandingCells) ||
                    !ContainsAll(stair.footprintCells))
                {
                    return false;
                }
            }

            foreach (ThroneThresholdPlacement threshold in thresholds)
            {
                if (!room.Contains(threshold.cell) || room.Contains(threshold.cell + threshold.outwardDirection))
                {
                    return false;
                }
            }

            return true;
        }

        private static void ResolveThroneShowpieceTransform(
            Vector2Int center,
            Vector2Int focalAxis,
            Vector2Int transverseAxis,
            int focalTransverseRadius,
            out Vector2Int originCell,
            out float yawDegrees)
        {
            Vector2Int wallCenter = OrientedCell(center, focalAxis, transverseAxis, 3, 0);
            Vector2Int alongStart = OrientedCell(center, focalAxis, transverseAxis, 3, -focalTransverseRadius);
            if (focalAxis == Vector2Int.up)
            {
                originCell = new Vector2Int(alongStart.x, wallCenter.y - 1);
                yawDegrees = 0f;
            }
            else if (focalAxis == Vector2Int.down)
            {
                originCell = new Vector2Int(alongStart.x + 5, wallCenter.y + 2);
                yawDegrees = 180f;
            }
            else if (focalAxis == Vector2Int.right)
            {
                originCell = new Vector2Int(wallCenter.x - 1, alongStart.y + 5);
                yawDegrees = 90f;
            }
            else
            {
                originCell = new Vector2Int(wallCenter.x + 2, alongStart.y);
                yawDegrees = 270f;
            }
        }

        private static bool TryRealizeThroneHallEpisode(
            ThroneHallEpisodePlacement placement,
            IReadOnlyList<RoomFootprint> rooms,
            Dictionary<Vector2Int, int> cellLevels,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            HashSet<string> transitionKeys,
            StairPlacementLedger stairLedger,
            string seamStairPrefabPath,
            List<DaisShowpiece> showpieces,
            out int baseLevel,
            out string rejectionReason)
        {
            baseLevel = 0;
            rejectionReason = string.Empty;
            if (placement == null || placement.roomIndex < 0 || placement.roomIndex >= rooms.Count)
            {
                rejectionReason = "[THRONE_EPISODE_ATOMICITY] tier planning received no complete episode placement";
                return false;
            }

            if (!cellLevels.TryGetValue(placement.thresholds[0].cell, out baseLevel))
            {
                rejectionReason = "[THRONE_EPISODE_LEVELS] processional entry had no base level";
                return false;
            }

            foreach (ThroneThresholdPlacement threshold in placement.thresholds)
            {
                if (!cellLevels.TryGetValue(threshold.cell, out int thresholdLevel) ||
                    thresholdLevel != baseLevel ||
                    thresholdLevel != threshold.expectedRelativeLevel)
                {
                    rejectionReason = $"[THRONE_EPISODE_LEVELS] typed threshold '{threshold.id}' resolved at {thresholdLevel}u instead of {threshold.expectedRelativeLevel}u";
                    return false;
                }
            }

            int galleryLevel = baseLevel + placement.intent.galleryRiseLevels;
            foreach (Vector2Int[] gallery in placement.sideGalleryCells)
            {
                foreach (Vector2Int cell in gallery)
                {
                    if (!cellLevels.ContainsKey(cell))
                    {
                        rejectionReason = $"[THRONE_EPISODE_LEVELS] gallery cell {cell} was absent from the canonical level field";
                        return false;
                    }

                    cellLevels[cell] = galleryLevel;
                }
            }

            foreach (ThroneStairPlacement stair in placement.twinStairs)
            {
                if (!transitionKeys.Add(TransitionKey(stair.upperTransitionCell, stair.lowerTransitionCell)))
                {
                    rejectionReason = $"[THRONE_EPISODE_ATOMICITY] coupled stair '{stair.id}' conflicted with an existing transition";
                    return false;
                }

                int lowerPortDirection = DirectionFromVector(new Vector2(stair.climbDirection.x, stair.climbDirection.y));
                transitions.Add(new ElevationEdgeModel.TransitionEdge(
                    stair.upperTransitionCell,
                    stair.lowerTransitionCell,
                    seamStairPrefabPath,
                    stair.lowerLandingCells,
                    stair.upperLandingCells,
                    stair.footprintCells,
                    lowerPortDirection,
                    OppositeDirection(lowerPortDirection),
                    DaisStairPlacementClass));
                stairLedger.Register(stair.footprintCells, stair.lowerLandingCells, stair.upperLandingCells);
            }

            if (!StairForge.TryGetBackedShowpieceDesign(
                    placement.selectedFocalDesignId,
                    out ElevationEdgeModel.SynthesizedPiecePlacement[] showpiecePieces))
            {
                rejectionReason = $"[THRONE_EPISODE_FOCAL] approved focal design '{placement.selectedFocalDesignId}' was unavailable";
                return false;
            }

            showpieces.Add(new DaisShowpiece
            {
                designName = placement.selectedFocalDesignId,
                originCell = placement.showpieceOriginCell,
                yawDegrees = placement.showpieceYawDegrees,
                roomLevel = baseLevel,
                pieces = showpiecePieces
            });

            // The episode room is authored space. Treat its cells as shareable
            // landing reservations after its own stairs exist: generic stairs may
            // land only at declared route thresholds, but no later footprint,
            // dais, bridge, sweep, or dressing anchor may consume the room.
            stairLedger.Register(
                Array.Empty<Vector2Int>(),
                placement.roomCells,
                Array.Empty<Vector2Int>());
            return true;
        }

        private static bool TryValidateResolvedThroneHallEpisode(
            ThroneHallEpisodePlacement placement,
            DungeonLayout layout,
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            IReadOnlyList<DaisShowpiece> showpieces,
            IReadOnlyList<Vector2Int> promontoryCells,
            int baseLevel,
            out ThroneHallEpisodeResolution resolution,
            out string rejectionReason)
        {
            resolution = default;
            rejectionReason = string.Empty;
            if (placement == null || placement.twinStairs.Length != placement.intent.coupledStairCount ||
                placement.sideGalleryCells.Length != 2 || placement.thresholds.Length != 2)
            {
                rejectionReason = "[THRONE_EPISODE_ATOMICITY] final plan lacked a complete episode group";
                return false;
            }

            foreach (ThroneThresholdPlacement threshold in placement.thresholds)
            {
                bool foundConnection = false;
                int expectedNeighbor = threshold.kind == ThroneThresholdKind.ProcessionalEntry
                    ? placement.roomIndex - 1
                    : placement.roomIndex + 1;
                foreach (RoomConnection connection in layout.connections)
                {
                    if (!placement.TryGetThreshold(threshold.edgeId, out _) ||
                        !(connection.fromRoom == placement.roomIndex && connection.toRoom == expectedNeighbor ||
                          connection.toRoom == placement.roomIndex && connection.fromRoom == expectedNeighbor))
                    {
                        continue;
                    }

                    Vector2Int actual = connection.fromRoom == placement.roomIndex
                        ? connection.path[0]
                        : connection.path[connection.path.Count - 1];
                    foundConnection = actual == threshold.cell &&
                        cellLevels.TryGetValue(actual, out int level) &&
                        level == threshold.expectedRelativeLevel;
                    break;
                }

                if (!foundConnection)
                {
                    rejectionReason = $"[THRONE_EPISODE_PORT_BINDING] edge '{threshold.edgeId}' did not terminate at typed threshold '{threshold.id}'";
                    return false;
                }
            }

            int galleryLevel = baseLevel + placement.intent.galleryRiseLevels;
            foreach (Vector2Int cell in placement.focalZoneCells)
            {
                if (!cellLevels.TryGetValue(cell, out int level) || level != baseLevel)
                {
                    rejectionReason = $"[THRONE_EPISODE_PROTECTION] focal cell {cell} was re-leveled or removed";
                    return false;
                }
            }

            foreach (Vector2Int[] gallery in placement.sideGalleryCells)
            {
                if (gallery.Length == 0)
                {
                    rejectionReason = "[THRONE_EPISODE_ATOMICITY] a side gallery was empty";
                    return false;
                }

                foreach (Vector2Int cell in gallery)
                {
                    if (!cellLevels.TryGetValue(cell, out int level) || level != galleryLevel)
                    {
                        rejectionReason = $"[THRONE_EPISODE_PROTECTION] gallery cell {cell} was re-leveled or removed";
                        return false;
                    }
                }
            }

            foreach (Vector2Int cell in placement.sideGalleryCells[0])
            {
                Vector2Int relative = cell - placement.roomCenter;
                int focal = IntDot(relative, placement.focalAxis);
                int transverse = IntDot(relative, placement.transverseAxis);
                Vector2Int mirror = OrientedCell(
                    placement.roomCenter,
                    placement.focalAxis,
                    placement.transverseAxis,
                    focal,
                    -transverse);
                if (Array.IndexOf(placement.sideGalleryCells[1], mirror) < 0)
                {
                    rejectionReason = $"[THRONE_EPISODE_SYMMETRY] gallery cell {cell} had no coupled mirror";
                    return false;
                }
            }

            foreach (ThroneStairPlacement stair in placement.twinStairs)
            {
                int matchCount = 0;
                foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
                {
                    if (string.Equals(
                            TransitionKey(transition.firstCell, transition.secondCell),
                            TransitionKey(stair.upperTransitionCell, stair.lowerTransitionCell),
                            StringComparison.Ordinal) &&
                        transition.hasLandings &&
                        SameCells(transition.lowerLandingCells, stair.lowerLandingCells) &&
                        SameCells(transition.upperLandingCells, stair.upperLandingCells) &&
                        SameCells(transition.footprintCells, stair.footprintCells))
                    {
                        matchCount++;
                    }
                }

                if (matchCount != 1)
                {
                    rejectionReason = $"[THRONE_EPISODE_ATOMICITY] coupled stair '{stair.id}' resolved {matchCount} times";
                    return false;
                }
            }

            int focalShowpieceCount = 0;
            foreach (DaisShowpiece showpiece in showpieces)
            {
                if (string.Equals(showpiece.designName, placement.selectedFocalDesignId, StringComparison.Ordinal) &&
                    showpiece.originCell == placement.showpieceOriginCell &&
                    Mathf.Abs(Mathf.DeltaAngle(showpiece.yawDegrees, placement.showpieceYawDegrees)) < 0.01f)
                {
                    focalShowpieceCount++;
                }
            }

            if (focalShowpieceCount != 1)
            {
                rejectionReason = $"[THRONE_EPISODE_FOCAL] focal showpiece resolved {focalShowpieceCount} times";
                return false;
            }

            foreach (Vector2Int promontory in promontoryCells ?? Array.Empty<Vector2Int>())
            {
                if (Array.IndexOf(placement.roomCells, promontory) >= 0)
                {
                    rejectionReason = $"[THRONE_EPISODE_PROTECTION] generic promontory consumed episode cell {promontory}";
                    return false;
                }
            }

            resolution = new ThroneHallEpisodeResolution(placement, baseLevel, atomicAndValid: true);
            return true;
        }

        private static bool SameCells(IReadOnlyList<Vector2Int> first, IReadOnlyList<Vector2Int> second)
        {
            if (first == null || second == null || first.Count != second.Count)
            {
                return false;
            }

            for (int index = 0; index < first.Count; index++)
            {
                if (first[index] != second[index])
                {
                    return false;
                }
            }

            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    internal sealed partial class DungeonLabGenerator
    {
        private enum RecipeOrientationBinding
        {
            RouteForward,
            VistaSourceToTarget
        }

        private readonly struct RecipePortBinding
        {
            public readonly string portId;
            public readonly string edgeId;

            public RecipePortBinding(string portId, string edgeId)
            {
                this.portId = portId ?? string.Empty;
                this.edgeId = edgeId ?? string.Empty;
            }
        }

        private sealed class RecipeSlotIntent
        {
            public readonly int slotNode;
            public readonly DungeonRecipeAsset recipe;
            public readonly RecipeOrientationBinding orientationBinding;
            public readonly RecipePortBinding[] portBindings;

            public RecipeSlotIntent(
                int slotNode,
                DungeonRecipeAsset recipe,
                RecipeOrientationBinding orientationBinding,
                RecipePortBinding[] portBindings)
            {
                this.slotNode = slotNode;
                this.recipe = recipe;
                this.orientationBinding = orientationBinding;
                this.portBindings = portBindings ?? Array.Empty<RecipePortBinding>();
            }

            public bool TryGetEdgeId(string portId, out string edgeId)
            {
                foreach (RecipePortBinding binding in portBindings)
                {
                    if (string.Equals(binding.portId, portId, StringComparison.Ordinal))
                    {
                        edgeId = binding.edgeId;
                        return true;
                    }
                }

                edgeId = string.Empty;
                return false;
            }
        }

        private readonly struct RecipePortPlacement
        {
            public readonly string id;
            public readonly string edgeId;
            public readonly DungeonRecipePortType type;
            public readonly bool mandatory;
            public readonly int neighborRoomIndex;
            public readonly Vector2Int cell;
            public readonly Vector2Int outwardDirection;
            public readonly int expectedRelativeLevel;

            public RecipePortPlacement(
                DungeonRecipePort port,
                string edgeId,
                int neighborRoomIndex,
                Vector2Int cell,
                Vector2Int outwardDirection,
                int expectedRelativeLevel)
            {
                id = port.id;
                this.edgeId = edgeId ?? string.Empty;
                type = port.type;
                mandatory = port.mandatory;
                this.neighborRoomIndex = neighborRoomIndex;
                this.cell = cell;
                this.outwardDirection = outwardDirection;
                this.expectedRelativeLevel = expectedRelativeLevel;
            }
        }

        private readonly struct RecipeZonePlacement
        {
            public readonly string id;
            public readonly DungeonRecipeZoneKind kind;
            public readonly int relativeLevel;
            public readonly Vector2Int[] cells;

            public RecipeZonePlacement(
                string id,
                DungeonRecipeZoneKind kind,
                int relativeLevel,
                Vector2Int[] cells)
            {
                this.id = id ?? string.Empty;
                this.kind = kind;
                this.relativeLevel = relativeLevel;
                this.cells = cells ?? Array.Empty<Vector2Int>();
            }
        }

        private readonly struct RecipeTransitionPlacement
        {
            public readonly string id;
            public readonly string atomicGroupId;
            public readonly Vector2Int lowerTransitionCell;
            public readonly Vector2Int upperTransitionCell;
            public readonly Vector2Int[] lowerLandingCells;
            public readonly Vector2Int[] upperLandingCells;
            public readonly Vector2Int[] footprintCells;
            public readonly Vector2Int climbDirection;

            public RecipeTransitionPlacement(
                DungeonRecipeTransition transition,
                Vector2Int lowerTransitionCell,
                Vector2Int upperTransitionCell,
                Vector2Int[] lowerLandingCells,
                Vector2Int[] upperLandingCells,
                Vector2Int[] footprintCells,
                Vector2Int climbDirection)
            {
                id = transition.id;
                atomicGroupId = transition.atomicGroupId;
                this.lowerTransitionCell = lowerTransitionCell;
                this.upperTransitionCell = upperTransitionCell;
                this.lowerLandingCells = lowerLandingCells ?? Array.Empty<Vector2Int>();
                this.upperLandingCells = upperLandingCells ?? Array.Empty<Vector2Int>();
                this.footprintCells = footprintCells ?? Array.Empty<Vector2Int>();
                this.climbDirection = climbDirection;
            }
        }

        private sealed class RecipePlacement
        {
            public readonly RecipeSlotIntent slot;
            public readonly int roomIndex;
            public readonly Vector2Int roomCenter;
            public readonly Vector2Int primaryAxis;
            public readonly Vector2Int transverseAxis;
            public readonly bool mirrored;
            public readonly Vector2Int[] roomCells;
            public readonly RecipeZonePlacement[] zones;
            public readonly Vector2Int[] protectedCells;
            public readonly RecipePortPlacement[] ports;
            public readonly RecipeTransitionPlacement[] transitions;
            public readonly string selectedVariationId;
            public readonly string selectedVisualImplementationId;
            public readonly Vector2Int showpieceOriginCell;
            public readonly float showpieceYawDegrees;

            public RecipePlacement(
                RecipeSlotIntent slot,
                int roomIndex,
                Vector2Int roomCenter,
                Vector2Int primaryAxis,
                Vector2Int transverseAxis,
                bool mirrored,
                Vector2Int[] roomCells,
                RecipeZonePlacement[] zones,
                Vector2Int[] protectedCells,
                RecipePortPlacement[] ports,
                RecipeTransitionPlacement[] transitions,
                string selectedVariationId,
                string selectedVisualImplementationId,
                Vector2Int showpieceOriginCell,
                float showpieceYawDegrees)
            {
                this.slot = slot;
                this.roomIndex = roomIndex;
                this.roomCenter = roomCenter;
                this.primaryAxis = primaryAxis;
                this.transverseAxis = transverseAxis;
                this.mirrored = mirrored;
                this.roomCells = roomCells ?? Array.Empty<Vector2Int>();
                this.zones = zones ?? Array.Empty<RecipeZonePlacement>();
                this.protectedCells = protectedCells ?? Array.Empty<Vector2Int>();
                this.ports = ports ?? Array.Empty<RecipePortPlacement>();
                this.transitions = transitions ?? Array.Empty<RecipeTransitionPlacement>();
                this.selectedVariationId = selectedVariationId ?? string.Empty;
                this.selectedVisualImplementationId = selectedVisualImplementationId ?? string.Empty;
                this.showpieceOriginCell = showpieceOriginCell;
                this.showpieceYawDegrees = showpieceYawDegrees;
            }

            public string RecipeId => slot?.recipe?.recipeId ?? string.Empty;

            public bool TryGetPort(string edgeId, out RecipePortPlacement port)
            {
                foreach (RecipePortPlacement candidate in ports)
                {
                    if (string.Equals(candidate.edgeId, edgeId, StringComparison.Ordinal))
                    {
                        port = candidate;
                        return true;
                    }
                }

                port = default;
                return false;
            }

            public bool TryGetZone(string zoneId, out RecipeZonePlacement zone)
            {
                foreach (RecipeZonePlacement candidate in zones)
                {
                    if (string.Equals(candidate.id, zoneId, StringComparison.Ordinal))
                    {
                        zone = candidate;
                        return true;
                    }
                }

                zone = default;
                return false;
            }
        }

        private readonly struct RecipeResolution
        {
            public readonly string id;
            public readonly DungeonRecipeKind kind;
            public readonly string contentDigest;
            public readonly int roomIndex;
            public readonly Vector2Int primaryAxis;
            public readonly bool mirrored;
            public readonly Vector2Int[] protectedCells;
            public readonly RecipeZonePlacement[] zones;
            public readonly RecipePortPlacement[] ports;
            public readonly RecipeTransitionPlacement[] transitions;
            public readonly string selectedVariationId;
            public readonly string selectedVisualImplementationId;
            public readonly Vector2Int showpieceOriginCell;
            public readonly float showpieceYawDegrees;
            public readonly int baseLevel;
            public readonly bool atomicAndValid;

            public RecipeResolution(RecipePlacement placement, int baseLevel, bool atomicAndValid)
            {
                id = placement?.RecipeId ?? string.Empty;
                kind = placement?.slot?.recipe?.kind ?? default;
                contentDigest = placement?.slot?.recipe != null
                    ? DungeonRecipeValidator.ComputeContentDigest(placement.slot.recipe)
                    : string.Empty;
                roomIndex = placement?.roomIndex ?? -1;
                primaryAxis = placement?.primaryAxis ?? default;
                mirrored = placement?.mirrored ?? false;
                protectedCells = placement?.protectedCells ?? Array.Empty<Vector2Int>();
                zones = placement?.zones ?? Array.Empty<RecipeZonePlacement>();
                ports = placement?.ports ?? Array.Empty<RecipePortPlacement>();
                transitions = placement?.transitions ?? Array.Empty<RecipeTransitionPlacement>();
                selectedVariationId = placement?.selectedVariationId ?? string.Empty;
                selectedVisualImplementationId = placement?.selectedVisualImplementationId ?? string.Empty;
                showpieceOriginCell = placement?.showpieceOriginCell ?? default;
                showpieceYawDegrees = placement?.showpieceYawDegrees ?? 0f;
                this.baseLevel = baseLevel;
                this.atomicAndValid = atomicAndValid;
            }
        }

        private static bool TryBuildRequiredRecipeSlots(
            ActiveDungeonRecipeCatalog catalog,
            out RecipeSlotIntent[] slots,
            out string rejectionReason)
        {
            slots = Array.Empty<RecipeSlotIntent>();
            rejectionReason = string.Empty;
            if (catalog == null ||
                !catalog.TryGet(DungeonRecipeIds.ProcessionalLandmark, out DungeonRecipeAsset throne) ||
                !catalog.TryGet(DungeonRecipeIds.CompressionConnector, out DungeonRecipeAsset vestibule))
            {
                rejectionReason = "[RECIPE_CATALOG] required reviewed Phase 5 recipes were not active";
                return false;
            }

            slots = new[]
            {
                new RecipeSlotIntent(
                    1,
                    vestibule,
                    RecipeOrientationBinding.RouteForward,
                    new[]
                    {
                        new RecipePortBinding("entry", "main-0-1"),
                        new RecipePortBinding("exit", "main-1-2")
                    }),
                new RecipeSlotIntent(
                    Phase1VistaTargetNode,
                    throne,
                    RecipeOrientationBinding.VistaSourceToTarget,
                    new[]
                    {
                        new RecipePortBinding("entry", "main-3-4"),
                        new RecipePortBinding("exit", "main-4-5")
                    })
            };
            return true;
        }

        private static List<RectInt> BuildRecipeRoomParts(
            RecipeSlotIntent slot,
            Vector2Int center,
            Vector2Int primaryAxis,
            bool mirrored)
        {
            Vector2Int transverse = new Vector2Int(-primaryAxis.y, primaryAxis.x);
            var parts = new List<RectInt>();
            foreach (DungeonRecipeZone zone in slot.recipe.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                if (zone == null ||
                    zone.kind != DungeonRecipeZoneKind.Walkable &&
                    zone.kind != DungeonRecipeZoneKind.Elevated)
                {
                    continue;
                }

                int transverseMin = mirrored
                    ? -(zone.offset.y + zone.size.y - 1)
                    : zone.offset.y;
                int transverseMax = mirrored
                    ? -zone.offset.y
                    : zone.offset.y + zone.size.y - 1;
                parts.Add(OrientedRecipeRect(
                    center,
                    primaryAxis,
                    transverse,
                    zone.offset.x,
                    zone.offset.x + zone.size.x - 1,
                    transverseMin,
                    transverseMax));
            }

            return parts;
        }

        private static RectInt OrientedRecipeRect(
            Vector2Int center,
            Vector2Int primaryAxis,
            Vector2Int transverseAxis,
            int primaryMin,
            int primaryMax,
            int transverseMin,
            int transverseMax)
        {
            Vector2Int first = RecipeCell(center, primaryAxis, transverseAxis, primaryMin, transverseMin);
            int minX = first.x;
            int maxX = first.x;
            int minY = first.y;
            int maxY = first.y;
            foreach (Vector2Int corner in new[]
                     {
                         RecipeCell(center, primaryAxis, transverseAxis, primaryMin, transverseMax),
                         RecipeCell(center, primaryAxis, transverseAxis, primaryMax, transverseMin),
                         RecipeCell(center, primaryAxis, transverseAxis, primaryMax, transverseMax)
                     })
            {
                minX = Mathf.Min(minX, corner.x);
                maxX = Mathf.Max(maxX, corner.x);
                minY = Mathf.Min(minY, corner.y);
                maxY = Mathf.Max(maxY, corner.y);
            }

            return new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private static Vector2Int RecipeCell(
            Vector2Int center,
            Vector2Int primaryAxis,
            Vector2Int transverseAxis,
            int primary,
            int transverse)
        {
            return center + primaryAxis * primary + transverseAxis * transverse;
        }

        private static Vector2Int TransformRecipeCell(
            Vector2Int local,
            Vector2Int center,
            Vector2Int primaryAxis,
            Vector2Int transverseAxis,
            bool mirrored)
        {
            return RecipeCell(
                center,
                primaryAxis,
                transverseAxis,
                local.x,
                mirrored ? -local.y : local.y);
        }

        private static Vector2Int TransformRecipeDirection(
            Vector2Int local,
            Vector2Int primaryAxis,
            Vector2Int transverseAxis,
            bool mirrored)
        {
            return primaryAxis * local.x + transverseAxis * (mirrored ? -local.y : local.y);
        }

        private static bool TryPlaceRouteRecipes(
            int dungeonSeed,
            int layoutAttempt,
            RouteIntent routeIntent,
            IReadOnlyList<RoomFootprint> rooms,
            IReadOnlyList<Vector2Int> nodeCenters,
            Vector2Int vistaSourceFacing,
            Vector2Int vistaTargetFacing,
            out RecipePlacement[] placements,
            out string rejectionReason)
        {
            placements = Array.Empty<RecipePlacement>();
            rejectionReason = string.Empty;
            if (routeIntent?.recipeSlots == null || routeIntent.recipeSlots.Length == 0)
            {
                rejectionReason = "route intent declared no recipe slots";
                return false;
            }

            var completed = new List<RecipePlacement>(routeIntent.recipeSlots.Length);
            foreach (RecipeSlotIntent slot in routeIntent.recipeSlots)
            {
                if (!TryPlaceRecipe(
                        dungeonSeed,
                        layoutAttempt,
                        routeIntent,
                        slot,
                        rooms,
                        nodeCenters,
                        vistaSourceFacing,
                        vistaTargetFacing,
                        out RecipePlacement placement,
                        out rejectionReason))
                {
                    return false;
                }

                completed.Add(placement);
            }

            placements = completed.ToArray();
            return true;
        }

        private static bool TryPlaceRecipe(
            int dungeonSeed,
            int layoutAttempt,
            RouteIntent routeIntent,
            RecipeSlotIntent slot,
            IReadOnlyList<RoomFootprint> rooms,
            IReadOnlyList<Vector2Int> nodeCenters,
            Vector2Int vistaSourceFacing,
            Vector2Int vistaTargetFacing,
            out RecipePlacement placement,
            out string rejectionReason)
        {
            placement = null;
            rejectionReason = string.Empty;
            if (slot?.recipe == null || slot.slotNode < 0 || slot.slotNode >= rooms.Count)
            {
                rejectionReason = "recipe slot did not identify an active reviewed asset and room";
                return false;
            }

            RouteNodeIntent node = routeIntent.nodes[slot.slotNode];
            if (Array.IndexOf(slot.recipe.eligibleRoles, node.role) < 0 ||
                Array.IndexOf(slot.recipe.eligibleBeats, node.beat) < 0)
            {
                rejectionReason = $"recipe '{slot.recipe.recipeId}' was not eligible for {node.role}/{node.beat}";
                return false;
            }

            Vector2Int primaryAxis;
            if (slot.orientationBinding == RecipeOrientationBinding.VistaSourceToTarget)
            {
                if (vistaSourceFacing == Vector2Int.zero || vistaSourceFacing != -vistaTargetFacing)
                {
                    rejectionReason = $"recipe '{slot.recipe.recipeId}' could not bind its primary axis to the opposed route vista";
                    return false;
                }

                primaryAxis = vistaSourceFacing;
            }
            else
            {
                primaryAxis = CardinalUnit(nodeCenters[slot.slotNode + 1] - nodeCenters[slot.slotNode]);
            }

            int quarterTurns = QuarterTurnsForAxis(primaryAxis);
            if (Array.IndexOf(slot.recipe.legalQuarterTurns, quarterTurns) < 0)
            {
                rejectionReason = $"recipe '{slot.recipe.recipeId}' did not allow resolved quarter-turn {quarterTurns}";
                return false;
            }

            Vector2Int transverseAxis = new Vector2Int(-primaryAxis.y, primaryAxis.x);
            bool firstMirror = slot.recipe.allowMirror &&
                Phase1Random(dungeonSeed, layoutAttempt, slot.recipe.recipeId, "mirror").Next(2) == 1;
            bool mirrorMatched = false;
            bool mirrored = false;
            int mirrorAttempts = slot.recipe.allowMirror ? 2 : 1;
            for (int attempt = 0; attempt < mirrorAttempts; attempt++)
            {
                bool candidate = attempt == 0 ? firstMirror : !firstMirror;
                if (RecipePortsMatchRoute(
                        routeIntent,
                        slot,
                        nodeCenters,
                        primaryAxis,
                        transverseAxis,
                        candidate))
                {
                    mirrored = candidate;
                    mirrorMatched = true;
                    break;
                }
            }

            if (!mirrorMatched)
            {
                rejectionReason = $"recipe '{slot.recipe.recipeId}' ports did not match their route-bound neighbors";
                return false;
            }

            Vector2Int center = nodeCenters[slot.slotNode];
            RoomFootprint room = rooms[slot.slotNode];
            var expected = new RoomFootprint(BuildRecipeRoomParts(slot, center, primaryAxis, mirrored));
            if (!room.cells.SetEquals(expected.cells))
            {
                rejectionReason = $"inflated room did not match atomic recipe '{slot.recipe.recipeId}'";
                return false;
            }

            var zonePlacements = new List<RecipeZonePlacement>();
            var protectedCells = new HashSet<Vector2Int>();
            foreach (DungeonRecipeZone zone in slot.recipe.zones)
            {
                var cells = new List<Vector2Int>();
                for (int x = zone.offset.x; x < zone.offset.x + zone.size.x; x++)
                {
                    for (int y = zone.offset.y; y < zone.offset.y + zone.size.y; y++)
                    {
                        cells.Add(TransformRecipeCell(
                            new Vector2Int(x, y),
                            center,
                            primaryAxis,
                            transverseAxis,
                            mirrored));
                    }
                }

                Vector2Int[] sorted = SortedCells(cells).ToArray();
                zonePlacements.Add(new RecipeZonePlacement(zone.id, zone.kind, zone.relativeLevel, sorted));
                if (zone.kind == DungeonRecipeZoneKind.ProtectedCirculation ||
                    zone.kind == DungeonRecipeZoneKind.ProtectedFocal)
                {
                    protectedCells.UnionWith(sorted);
                }
            }

            var ports = new List<RecipePortPlacement>();
            foreach (DungeonRecipePort port in slot.recipe.ports)
            {
                if (!slot.TryGetEdgeId(port.id, out string edgeId))
                {
                    rejectionReason = $"recipe port '{port.id}' had no route-edge binding";
                    return false;
                }

                ports.Add(new RecipePortPlacement(
                    port,
                    edgeId,
                    NeighborForEdge(routeIntent, edgeId, slot.slotNode),
                    TransformRecipeCell(port.cell, center, primaryAxis, transverseAxis, mirrored),
                    TransformRecipeDirection(port.outwardDirection, primaryAxis, transverseAxis, mirrored),
                    node.relativeElevationLevels + port.relativeLevel));
            }

            var transitions = new List<RecipeTransitionPlacement>();
            foreach (DungeonRecipeTransition transition in slot.recipe.transitions)
            {
                transitions.Add(new RecipeTransitionPlacement(
                    transition,
                    TransformRecipeCell(transition.lowerTransitionCell, center, primaryAxis, transverseAxis, mirrored),
                    TransformRecipeCell(transition.upperTransitionCell, center, primaryAxis, transverseAxis, mirrored),
                    TransformRecipeCells(transition.lowerLandingCells, center, primaryAxis, transverseAxis, mirrored),
                    TransformRecipeCells(transition.upperLandingCells, center, primaryAxis, transverseAxis, mirrored),
                    TransformRecipeCells(transition.footprintCells, center, primaryAxis, transverseAxis, mirrored),
                    TransformRecipeDirection(transition.climbDirection, primaryAxis, transverseAxis, mirrored)));
            }

            string variationId = string.Empty;
            string visualImplementationId = string.Empty;
            Vector2Int showpieceOrigin = default;
            float showpieceYaw = 0f;
            if (slot.recipe.variations.Length > 0)
            {
                DungeonRecipeVariation variation = SelectRecipeVariation(
                    slot.recipe,
                    Phase1Random(dungeonSeed, layoutAttempt, slot.recipe.recipeId, "variation"));
                DungeonRecipeMotif motif = FindRecipeMotif(slot.recipe, variation.motifId);
                variationId = variation.id;
                visualImplementationId = motif.implementationId;
                ResolvePrimaryVisualTransform(
                    slot.recipe,
                    center,
                    primaryAxis,
                    transverseAxis,
                    out showpieceOrigin,
                    out showpieceYaw);
            }

            Vector2Int[] roomCells = SortedCells(room.cells).ToArray();
            RecipeZonePlacement[] zoneArray = zonePlacements.ToArray();
            RecipePortPlacement[] portArray = ports.ToArray();
            RecipeTransitionPlacement[] transitionArray = transitions.ToArray();
            if (!RecipeCellsBelongToRoom(room, zoneArray, portArray, transitionArray))
            {
                rejectionReason = $"recipe '{slot.recipe.recipeId}' geometry or reservation escaped its room footprint";
                return false;
            }

            placement = new RecipePlacement(
                slot,
                slot.slotNode,
                center,
                primaryAxis,
                transverseAxis,
                mirrored,
                roomCells,
                zoneArray,
                SortedCells(protectedCells).ToArray(),
                portArray,
                transitionArray,
                variationId,
                visualImplementationId,
                showpieceOrigin,
                showpieceYaw);
            return true;
        }

        private static bool RecipePortsMatchRoute(
            RouteIntent intent,
            RecipeSlotIntent slot,
            IReadOnlyList<Vector2Int> nodeCenters,
            Vector2Int primaryAxis,
            Vector2Int transverseAxis,
            bool mirrored)
        {
            foreach (DungeonRecipePort port in slot.recipe.ports)
            {
                if (!slot.TryGetEdgeId(port.id, out string edgeId) ||
                    !TryGetTraversal(intent, edgeId, out RouteTraversalIntent edge))
                {
                    return false;
                }

                int neighbor = edge.fromNode == slot.slotNode ? edge.toNode : edge.fromNode;
                Vector2Int actualOutward = CardinalUnit(nodeCenters[neighbor] - nodeCenters[slot.slotNode]);
                Vector2Int contractOutward = TransformRecipeDirection(
                    port.outwardDirection,
                    primaryAxis,
                    transverseAxis,
                    mirrored);
                if (actualOutward != contractOutward)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryGetTraversal(
            RouteIntent intent,
            string edgeId,
            out RouteTraversalIntent traversal)
        {
            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                if (string.Equals(edge.id, edgeId, StringComparison.Ordinal))
                {
                    traversal = edge;
                    return true;
                }
            }

            traversal = default;
            return false;
        }

        private static int NeighborForEdge(RouteIntent intent, string edgeId, int slotNode)
        {
            if (!TryGetTraversal(intent, edgeId, out RouteTraversalIntent edge))
            {
                return -1;
            }

            return edge.fromNode == slotNode ? edge.toNode : edge.fromNode;
        }

        private static int QuarterTurnsForAxis(Vector2Int axis)
        {
            if (axis == Vector2Int.up)
                return 0;
            if (axis == Vector2Int.right)
                return 1;
            if (axis == Vector2Int.down)
                return 2;
            return 3;
        }

        private static Vector2Int[] TransformRecipeCells(
            IEnumerable<Vector2Int> cells,
            Vector2Int center,
            Vector2Int primaryAxis,
            Vector2Int transverseAxis,
            bool mirrored)
        {
            var transformed = new List<Vector2Int>();
            foreach (Vector2Int cell in cells ?? Array.Empty<Vector2Int>())
            {
                transformed.Add(TransformRecipeCell(cell, center, primaryAxis, transverseAxis, mirrored));
            }

            return SortedCells(transformed).ToArray();
        }

        private static DungeonRecipeVariation SelectRecipeVariation(
            DungeonRecipeAsset recipe,
            System.Random random)
        {
            int totalWeight = 0;
            foreach (DungeonRecipeVariation variation in recipe.variations)
            {
                totalWeight += variation.weight;
            }

            int roll = random.Next(totalWeight);
            foreach (DungeonRecipeVariation variation in recipe.variations)
            {
                roll -= variation.weight;
                if (roll < 0)
                {
                    return variation;
                }
            }

            return recipe.variations[recipe.variations.Length - 1];
        }

        private static DungeonRecipeMotif FindRecipeMotif(DungeonRecipeAsset recipe, string motifId)
        {
            foreach (DungeonRecipeMotif motif in recipe.motifs)
            {
                if (string.Equals(motif.id, motifId, StringComparison.Ordinal))
                {
                    return motif;
                }
            }

            return null;
        }

        private static bool RecipeCellsBelongToRoom(
            RoomFootprint room,
            IReadOnlyList<RecipeZonePlacement> zones,
            IReadOnlyList<RecipePortPlacement> ports,
            IReadOnlyList<RecipeTransitionPlacement> transitions)
        {
            bool ContainsAll(IEnumerable<Vector2Int> cells)
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

            foreach (RecipeZonePlacement zone in zones)
            {
                if (!ContainsAll(zone.cells))
                {
                    return false;
                }
            }

            foreach (RecipePortPlacement port in ports)
            {
                if (!room.Contains(port.cell) || room.Contains(port.cell + port.outwardDirection))
                {
                    return false;
                }
            }

            foreach (RecipeTransitionPlacement transition in transitions)
            {
                if (!room.Contains(transition.lowerTransitionCell) ||
                    !room.Contains(transition.upperTransitionCell) ||
                    !ContainsAll(transition.lowerLandingCells) ||
                    !ContainsAll(transition.upperLandingCells) ||
                    !ContainsAll(transition.footprintCells))
                {
                    return false;
                }
            }

            return true;
        }

        private static void ResolvePrimaryVisualTransform(
            DungeonRecipeAsset recipe,
            Vector2Int center,
            Vector2Int primaryAxis,
            Vector2Int transverseAxis,
            out Vector2Int originCell,
            out float yawDegrees)
        {
            DungeonRecipeZone primary = null;
            DungeonRecipeZone focal = null;
            foreach (DungeonRecipeZone zone in recipe.zones)
            {
                if (zone.kind == DungeonRecipeZoneKind.Walkable && primary == null)
                {
                    primary = zone;
                }

                if (zone.kind == DungeonRecipeZoneKind.ProtectedFocal && focal == null)
                {
                    focal = zone;
                }
            }

            int primaryMax = primary.offset.x + primary.size.x - 1;
            int transverseRadius = focal.size.y / 2;
            Vector2Int wallCenter = RecipeCell(center, primaryAxis, transverseAxis, primaryMax, 0);
            Vector2Int alongStart = RecipeCell(center, primaryAxis, transverseAxis, primaryMax, -transverseRadius);
            if (primaryAxis == Vector2Int.up)
            {
                originCell = new Vector2Int(alongStart.x, wallCenter.y - 1);
                yawDegrees = 0f;
            }
            else if (primaryAxis == Vector2Int.down)
            {
                originCell = new Vector2Int(alongStart.x + 5, wallCenter.y + 2);
                yawDegrees = 180f;
            }
            else if (primaryAxis == Vector2Int.right)
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

        private static bool TryRealizeRecipes(
            IReadOnlyList<RecipePlacement> placements,
            IReadOnlyList<RoomFootprint> rooms,
            Dictionary<Vector2Int, int> cellLevels,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            HashSet<string> transitionKeys,
            StairPlacementLedger stairLedger,
            string seamStairPrefabPath,
            List<DaisShowpiece> showpieces,
            out Dictionary<string, int> baseLevels,
            out string rejectionReason)
        {
            baseLevels = new Dictionary<string, int>(StringComparer.Ordinal);
            rejectionReason = string.Empty;
            if (placements == null || placements.Count == 0)
            {
                rejectionReason = "[RECIPE_ATOMICITY] tier planning received no complete recipe placements";
                return false;
            }

            foreach (RecipePlacement placement in placements)
            {
                if (placement == null || placement.roomIndex < 0 || placement.roomIndex >= rooms.Count)
                {
                    rejectionReason = "[RECIPE_ATOMICITY] tier planning received an incomplete recipe placement";
                    return false;
                }

                if (placement.ports.Length == 0 ||
                    !cellLevels.TryGetValue(placement.ports[0].cell, out int firstPortLevel))
                {
                    rejectionReason = $"[RECIPE_LEVELS] '{placement.RecipeId}' had no leveled primary port";
                    return false;
                }

                int baseLevel = firstPortLevel - placement.slot.recipe.ports[0].relativeLevel;
                foreach (RecipePortPlacement port in placement.ports)
                {
                    if (!cellLevels.TryGetValue(port.cell, out int portLevel) ||
                        portLevel != port.expectedRelativeLevel)
                    {
                        rejectionReason = $"[RECIPE_LEVELS] typed port '{port.id}' on '{placement.RecipeId}' resolved at {portLevel}u instead of {port.expectedRelativeLevel}u";
                        return false;
                    }
                }

                foreach (RecipeZonePlacement zone in placement.zones)
                {
                    if (zone.kind != DungeonRecipeZoneKind.Elevated)
                    {
                        continue;
                    }

                    foreach (Vector2Int cell in zone.cells)
                    {
                        if (!cellLevels.ContainsKey(cell))
                        {
                            rejectionReason = $"[RECIPE_LEVELS] zone '{zone.id}' on '{placement.RecipeId}' escaped the canonical level field";
                            return false;
                        }

                        cellLevels[cell] = baseLevel + zone.relativeLevel;
                    }
                }

                foreach (RecipeTransitionPlacement recipeTransition in placement.transitions)
                {
                    if (!transitionKeys.Add(TransitionKey(
                            recipeTransition.upperTransitionCell,
                            recipeTransition.lowerTransitionCell)))
                    {
                        rejectionReason = $"[RECIPE_ATOMICITY] transition '{recipeTransition.id}' on '{placement.RecipeId}' conflicted with an existing transition";
                        return false;
                    }

                    int lowerPortDirection = DirectionFromVector(new Vector2(
                        recipeTransition.climbDirection.x,
                        recipeTransition.climbDirection.y));
                    transitions.Add(new ElevationEdgeModel.TransitionEdge(
                        recipeTransition.upperTransitionCell,
                        recipeTransition.lowerTransitionCell,
                        seamStairPrefabPath,
                        recipeTransition.lowerLandingCells,
                        recipeTransition.upperLandingCells,
                        recipeTransition.footprintCells,
                        lowerPortDirection,
                        OppositeDirection(lowerPortDirection),
                        DaisStairPlacementClass));
                    stairLedger.Register(
                        recipeTransition.footprintCells,
                        recipeTransition.lowerLandingCells,
                        recipeTransition.upperLandingCells);
                }

                if (!string.IsNullOrEmpty(placement.selectedVisualImplementationId))
                {
                    if (!StairForge.TryGetBackedShowpieceDesign(
                            placement.selectedVisualImplementationId,
                            out ElevationEdgeModel.SynthesizedPiecePlacement[] pieces))
                    {
                        rejectionReason = $"[RECIPE_VARIATION] reviewed visual '{placement.selectedVisualImplementationId}' was unavailable";
                        return false;
                    }

                    showpieces.Add(new DaisShowpiece
                    {
                        designName = placement.selectedVisualImplementationId,
                        originCell = placement.showpieceOriginCell,
                        yawDegrees = placement.showpieceYawDegrees,
                        roomLevel = baseLevel,
                        pieces = pieces
                    });
                }

                stairLedger.Register(
                    Array.Empty<Vector2Int>(),
                    placement.roomCells,
                    Array.Empty<Vector2Int>());
                baseLevels.Add(placement.RecipeId, baseLevel);
            }

            return true;
        }

        private static bool TryValidateResolvedRecipes(
            IReadOnlyList<RecipePlacement> placements,
            DungeonLayout layout,
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            IReadOnlyList<DaisShowpiece> showpieces,
            IReadOnlyList<Vector2Int> promontoryCells,
            IReadOnlyDictionary<string, int> baseLevels,
            out RecipeResolution[] resolutions,
            out string rejectionReason)
        {
            resolutions = Array.Empty<RecipeResolution>();
            rejectionReason = string.Empty;
            var completed = new List<RecipeResolution>();
            foreach (RecipePlacement placement in placements ?? Array.Empty<RecipePlacement>())
            {
                if (!baseLevels.TryGetValue(placement.RecipeId, out int baseLevel) ||
                    !TryValidateResolvedRecipe(
                        placement,
                        layout,
                        cellLevels,
                        transitions,
                        showpieces,
                        promontoryCells,
                        baseLevel,
                        out RecipeResolution resolution,
                        out rejectionReason))
                {
                    return false;
                }

                completed.Add(resolution);
            }

            if (completed.Count != placements.Count)
            {
                rejectionReason = "[RECIPE_ATOMICITY] final plan did not resolve every selected recipe";
                return false;
            }

            resolutions = completed.ToArray();
            return true;
        }

        private static bool TryValidateResolvedRecipe(
            RecipePlacement placement,
            DungeonLayout layout,
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            IReadOnlyList<DaisShowpiece> showpieces,
            IReadOnlyList<Vector2Int> promontoryCells,
            int baseLevel,
            out RecipeResolution resolution,
            out string rejectionReason)
        {
            resolution = default;
            rejectionReason = string.Empty;
            if (placement == null ||
                placement.transitions.Length != placement.slot.recipe.transitions.Length ||
                placement.ports.Length != placement.slot.recipe.ports.Length)
            {
                rejectionReason = "[RECIPE_ATOMICITY] final plan lacked a complete recipe group";
                return false;
            }

            foreach (RecipePortPlacement port in placement.ports)
            {
                bool foundConnection = false;
                foreach (RoomConnection connection in layout.connections)
                {
                    if (!(connection.fromRoom == placement.roomIndex && connection.toRoom == port.neighborRoomIndex ||
                          connection.toRoom == placement.roomIndex && connection.fromRoom == port.neighborRoomIndex))
                    {
                        continue;
                    }

                    Vector2Int actual = connection.fromRoom == placement.roomIndex
                        ? connection.path[0]
                        : connection.path[connection.path.Count - 1];
                    foundConnection = actual == port.cell &&
                        cellLevels.TryGetValue(actual, out int level) &&
                        level == port.expectedRelativeLevel;
                    break;
                }

                if (!foundConnection)
                {
                    rejectionReason = $"[RECIPE_PORT_BINDING] edge '{port.edgeId}' did not terminate at typed port '{port.id}' on '{placement.RecipeId}'";
                    return false;
                }
            }

            foreach (RecipeZonePlacement zone in placement.zones)
            {
                foreach (Vector2Int cell in zone.cells)
                {
                    int expectedLevel = baseLevel + ResolvedRecipeRelativeLevel(placement.zones, cell);
                    if (!cellLevels.TryGetValue(cell, out int level) || level != expectedLevel)
                    {
                        rejectionReason = $"[RECIPE_PROTECTION] zone '{zone.id}' on '{placement.RecipeId}' was re-leveled or removed";
                        return false;
                    }
                }
            }

            foreach (DungeonRecipeSymmetryPair pair in placement.slot.recipe.symmetryPairs)
            {
                if (!placement.TryGetZone(pair.firstZoneId, out RecipeZonePlacement first) ||
                    !placement.TryGetZone(pair.secondZoneId, out RecipeZonePlacement second) ||
                    !PlacedZonesMirror(placement, first, second))
                {
                    rejectionReason = $"[RECIPE_SYMMETRY] pair '{pair.id}' on '{placement.RecipeId}' was incomplete";
                    return false;
                }
            }

            foreach (RecipeTransitionPlacement recipeTransition in placement.transitions)
            {
                int matchCount = 0;
                foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
                {
                    if (string.Equals(
                            TransitionKey(transition.firstCell, transition.secondCell),
                            TransitionKey(recipeTransition.upperTransitionCell, recipeTransition.lowerTransitionCell),
                            StringComparison.Ordinal) &&
                        transition.hasLandings &&
                        SameRecipeCells(transition.lowerLandingCells, recipeTransition.lowerLandingCells) &&
                        SameRecipeCells(transition.upperLandingCells, recipeTransition.upperLandingCells) &&
                        SameRecipeCells(transition.footprintCells, recipeTransition.footprintCells))
                    {
                        matchCount++;
                    }
                }

                if (matchCount != 1)
                {
                    rejectionReason = $"[RECIPE_ATOMICITY] transition '{recipeTransition.id}' on '{placement.RecipeId}' resolved {matchCount} times";
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(placement.selectedVisualImplementationId))
            {
                int visualCount = 0;
                foreach (DaisShowpiece showpiece in showpieces)
                {
                    if (string.Equals(showpiece.designName, placement.selectedVisualImplementationId, StringComparison.Ordinal) &&
                        showpiece.originCell == placement.showpieceOriginCell &&
                        Mathf.Abs(Mathf.DeltaAngle(showpiece.yawDegrees, placement.showpieceYawDegrees)) < 0.01f)
                    {
                        visualCount++;
                    }
                }

                if (visualCount != 1)
                {
                    rejectionReason = $"[RECIPE_VARIATION] selected visual on '{placement.RecipeId}' resolved {visualCount} times";
                    return false;
                }
            }

            foreach (Vector2Int promontory in promontoryCells ?? Array.Empty<Vector2Int>())
            {
                if (Array.IndexOf(placement.roomCells, promontory) >= 0)
                {
                    rejectionReason = $"[RECIPE_PROTECTION] generic promontory consumed '{placement.RecipeId}' cell {promontory}";
                    return false;
                }
            }

            resolution = new RecipeResolution(placement, baseLevel, atomicAndValid: true);
            return true;
        }

        private static int ResolvedRecipeRelativeLevel(
            IReadOnlyList<RecipeZonePlacement> zones,
            Vector2Int cell)
        {
            int level = 0;
            foreach (RecipeZonePlacement zone in zones)
            {
                if (zone.kind == DungeonRecipeZoneKind.Elevated &&
                    Array.IndexOf(zone.cells, cell) >= 0)
                {
                    level = Mathf.Max(level, zone.relativeLevel);
                }
            }

            return level;
        }

        private static bool PlacedZonesMirror(
            RecipePlacement placement,
            RecipeZonePlacement first,
            RecipeZonePlacement second)
        {
            foreach (Vector2Int cell in first.cells)
            {
                Vector2Int relative = cell - placement.roomCenter;
                int primary = IntDot(relative, placement.primaryAxis);
                int transverse = IntDot(relative, placement.transverseAxis);
                Vector2Int mirror = RecipeCell(
                    placement.roomCenter,
                    placement.primaryAxis,
                    placement.transverseAxis,
                    primary,
                    -transverse);
                if (Array.IndexOf(second.cells, mirror) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SameRecipeCells(
            IReadOnlyList<Vector2Int> first,
            IReadOnlyList<Vector2Int> second)
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

        private static int IntDot(Vector2Int first, Vector2Int second)
        {
            return first.x * second.x + first.y * second.y;
        }

        private static Vector2Int CardinalUnit(Vector2Int delta)
        {
            return new Vector2Int(Math.Sign(delta.x), Math.Sign(delta.y));
        }
    }
}

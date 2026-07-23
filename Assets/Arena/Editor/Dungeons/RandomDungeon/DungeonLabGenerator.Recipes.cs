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

        private readonly struct RecipeCandidateRejection
        {
            public readonly string recipeId;
            public readonly string reasonCode;

            public RecipeCandidateRejection(string recipeId, string reasonCode)
            {
                this.recipeId = recipeId ?? string.Empty;
                this.reasonCode = reasonCode ?? string.Empty;
            }
        }

        private sealed class RecipeSlotIntent
        {
            public readonly string slotId;
            public readonly int slotNode;
            public readonly DungeonRecipeAsset recipe;
            public readonly RecipeOrientationBinding orientationBinding;
            public readonly RecipePortBinding[] portBindings;
            public readonly string catalogDigest;
            public readonly string[] compatibleCandidateIds;
            public readonly RecipeCandidateRejection[] rejectedCandidates;
            public readonly string selectionStreamIdentity;
            public readonly bool forcedForAuthoringPreview;

            public RecipeSlotIntent(
                string slotId,
                int slotNode,
                DungeonRecipeAsset recipe,
                RecipeOrientationBinding orientationBinding,
                RecipePortBinding[] portBindings,
                string catalogDigest = "",
                string[] compatibleCandidateIds = null,
                RecipeCandidateRejection[] rejectedCandidates = null,
                string selectionStreamIdentity = "",
                bool forcedForAuthoringPreview = false)
            {
                this.slotId = slotId ?? string.Empty;
                this.slotNode = slotNode;
                this.recipe = recipe;
                this.orientationBinding = orientationBinding;
                this.portBindings = portBindings ?? Array.Empty<RecipePortBinding>();
                this.catalogDigest = catalogDigest ?? string.Empty;
                this.compatibleCandidateIds = compatibleCandidateIds ?? Array.Empty<string>();
                this.rejectedCandidates = rejectedCandidates ?? Array.Empty<RecipeCandidateRejection>();
                this.selectionStreamIdentity = selectionStreamIdentity ?? string.Empty;
                this.forcedForAuthoringPreview = forcedForAuthoringPreview;
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
            public readonly int widthCells;
            public readonly int approachDepthCells;
            public readonly int headroomLevels;

            public RecipePortPlacement(
                DungeonRecipePort port,
                string edgeId,
                int neighborRoomIndex,
                Vector2Int cell,
                Vector2Int outwardDirection,
                int expectedRelativeLevel,
                bool requiredForPlacement = false)
            {
                id = port.id;
                this.edgeId = edgeId ?? string.Empty;
                type = port.type;
                mandatory = requiredForPlacement || port.mandatory;
                this.neighborRoomIndex = neighborRoomIndex;
                this.cell = cell;
                this.outwardDirection = outwardDirection;
                this.expectedRelativeLevel = expectedRelativeLevel;
                widthCells = port.widthCells;
                approachDepthCells = port.approachDepthCells;
                headroomLevels = port.headroomLevels;
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

        private readonly struct RecipeShowpieceReservation
        {
            public readonly Vector2Int[] requiredFloorCells;
            public readonly Vector2Int[] wallMarginCells;
            public readonly Vector2Int[] backdropVoidCells;

            public RecipeShowpieceReservation(
                Vector2Int[] requiredFloorCells,
                Vector2Int[] wallMarginCells,
                Vector2Int[] backdropVoidCells)
            {
                this.requiredFloorCells = requiredFloorCells ?? Array.Empty<Vector2Int>();
                this.wallMarginCells = wallMarginCells ?? Array.Empty<Vector2Int>();
                this.backdropVoidCells = backdropVoidCells ?? Array.Empty<Vector2Int>();
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
            public readonly RecipeShowpieceReservation showpieceReservation;

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
                float showpieceYawDegrees,
                RecipeShowpieceReservation showpieceReservation)
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
                this.showpieceReservation = showpieceReservation;
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
            public readonly RecipeShowpieceReservation showpieceReservation;
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
                showpieceReservation = placement?.showpieceReservation ?? default;
                this.baseLevel = baseLevel;
                this.atomicAndValid = atomicAndValid;
            }
        }

        private const string RecipeSelectionStreamIdentity = "recipe-selection-v1";

        private static bool TryResolveRequiredRecipeSlots(
            ActiveDungeonRecipeCatalog catalog,
            RouteIntent intent,
            out RecipeSlotIntent[] slots,
            out string rejectionReason)
        {
            slots = Array.Empty<RecipeSlotIntent>();
            rejectionReason = string.Empty;
            if (catalog == null || intent?.nodes == null || intent.traversalEdges == null)
            {
                rejectionReason = "[RECIPE_SELECTION] catalog or route intent was unavailable";
                return false;
            }

            bool authoringPreviewActive =
                DungeonRecipeCatalogService.TryGetAuthoringPreviewContext(
                    out string authoringPreviewRecipeId,
                    out string authoringPreviewReplacedRecipeId);
            int authoringPreviewSlotNode = -1;
            if (authoringPreviewActive)
            {
                for (int nodeIndex = 0; nodeIndex < intent.nodes.Length; nodeIndex++)
                {
                    RouteNodeIntent node = intent.nodes[nodeIndex];
                    if (!node.HasRecipeSlot)
                    {
                        continue;
                    }

                    if (!TryBuildRecipeSlotBindings(
                            intent,
                            nodeIndex,
                            out RecipeOrientationBinding orientationBinding,
                            out RecipePortBinding[] portBindings))
                    {
                        rejectionReason =
                            $"[RECIPE_SELECTION] slot '{node.recipeSlotId}' had no declared route-edge binding contract";
                        return false;
                    }

                    DungeonRecipeAsset previewRecipe = null;
                    var productionCandidates = new List<DungeonRecipeAsset>();
                    foreach (DungeonRecipeAsset candidate in catalog.recipes)
                    {
                        if (!TryValidateRecipeCandidate(
                                intent,
                                nodeIndex,
                                candidate,
                                orientationBinding,
                                portBindings,
                                out _))
                        {
                            continue;
                        }

                        if (string.Equals(
                                candidate.recipeId,
                                authoringPreviewRecipeId,
                                StringComparison.Ordinal))
                        {
                            previewRecipe = candidate;
                        }
                        else
                        {
                            productionCandidates.Add(candidate);
                        }
                    }

                    if (previewRecipe == null)
                    {
                        continue;
                    }

                    authoringPreviewSlotNode = nodeIndex;
                    if (string.IsNullOrEmpty(authoringPreviewReplacedRecipeId))
                    {
                        if (productionCandidates.Count == 0)
                        {
                            rejectionReason =
                                $"[RECIPE_PREVIEW] recipe '{authoringPreviewRecipeId}' had no production candidate to replace at slot '{node.recipeSlotId}'";
                            return false;
                        }

                        int replacedIndex = productionCandidates.Count == 1
                            ? 0
                            : RecipeSelectionRandom(
                                intent.seed,
                                intent.patternId,
                                node.id).Next(productionCandidates.Count);
                        authoringPreviewReplacedRecipeId =
                            productionCandidates[replacedIndex].recipeId;
                        if (!DungeonRecipeCatalogService.TryReplaceAuthoringPreviewCatalogMember(
                                authoringPreviewReplacedRecipeId,
                                out catalog,
                                out rejectionReason))
                        {
                            return false;
                        }
                    }

                    break;
                }

                if (authoringPreviewSlotNode < 0)
                {
                    rejectionReason =
                        $"[RECIPE_PREVIEW] recipe '{authoringPreviewRecipeId}' had no compatible required route slot";
                    return false;
                }
            }

            bool authoringPreviewForced = false;
            var resolved = new List<RecipeSlotIntent>(3);
            for (int nodeIndex = 0; nodeIndex < intent.nodes.Length; nodeIndex++)
            {
                RouteNodeIntent node = intent.nodes[nodeIndex];
                if (!node.HasRecipeSlot)
                {
                    continue;
                }

                if (!TryBuildRecipeSlotBindings(
                        intent,
                        nodeIndex,
                        out RecipeOrientationBinding orientationBinding,
                        out RecipePortBinding[] portBindings))
                {
                    rejectionReason =
                        $"[RECIPE_SELECTION] slot '{node.recipeSlotId}' had no declared route-edge binding contract";
                    return false;
                }

                var compatible = new List<DungeonRecipeAsset>();
                var rejections = new List<RecipeCandidateRejection>();
                foreach (DungeonRecipeAsset candidate in catalog.recipes)
                {
                    if (TryValidateRecipeCandidate(
                            intent,
                            nodeIndex,
                            candidate,
                            orientationBinding,
                            portBindings,
                            out string reasonCode))
                    {
                        compatible.Add(candidate);
                    }
                    else
                    {
                        rejections.Add(new RecipeCandidateRejection(candidate?.recipeId, reasonCode));
                    }
                }

                if (compatible.Count == 0)
                {
                    rejectionReason =
                        $"[RECIPE_SELECTION] slot '{node.recipeSlotId}' at node '{node.id}' had no compatible active recipe";
                    return false;
                }

                int previewCandidateIndex = -1;
                if (authoringPreviewActive)
                {
                    for (int index = 0; index < compatible.Count; index++)
                    {
                        if (string.Equals(
                                compatible[index].recipeId,
                                authoringPreviewRecipeId,
                                StringComparison.Ordinal))
                        {
                            previewCandidateIndex = index;
                            break;
                        }
                    }
                }

                bool forceAuthoringPreview =
                    authoringPreviewActive &&
                    !authoringPreviewForced &&
                    nodeIndex == authoringPreviewSlotNode &&
                    previewCandidateIndex >= 0;
                DungeonRecipeAsset selectedRecipe;
                if (forceAuthoringPreview)
                {
                    selectedRecipe = compatible[previewCandidateIndex];
                    authoringPreviewForced = true;
                }
                else
                {
                    var selectionCandidates = compatible;
                    if (authoringPreviewActive &&
                        authoringPreviewForced &&
                        previewCandidateIndex >= 0)
                    {
                        selectionCandidates = new List<DungeonRecipeAsset>(compatible.Count - 1);
                        foreach (DungeonRecipeAsset candidate in compatible)
                        {
                            if (!string.Equals(
                                    candidate.recipeId,
                                    authoringPreviewRecipeId,
                                    StringComparison.Ordinal))
                            {
                                selectionCandidates.Add(candidate);
                            }
                        }

                        if (selectionCandidates.Count == 0)
                        {
                            rejectionReason =
                                $"[RECIPE_PREVIEW] forced recipe '{authoringPreviewRecipeId}' was the only candidate for multiple slots";
                            return false;
                        }
                    }

                    int selectedIndex = selectionCandidates.Count == 1
                        ? 0
                        : RecipeSelectionRandom(
                            intent.seed,
                            intent.patternId,
                            node.id).Next(selectionCandidates.Count);
                    selectedRecipe = selectionCandidates[selectedIndex];
                }

                var compatibleIds = new string[compatible.Count];
                for (int index = 0; index < compatible.Count; index++)
                {
                    compatibleIds[index] = compatible[index].recipeId;
                }

                resolved.Add(new RecipeSlotIntent(
                    node.recipeSlotId,
                    nodeIndex,
                    selectedRecipe,
                    orientationBinding,
                    portBindings,
                    catalog.digest,
                    compatibleIds,
                    rejections.ToArray(),
                    RecipeSelectionStreamIdentity,
                    forceAuthoringPreview));
            }

            if (authoringPreviewActive && !authoringPreviewForced)
            {
                rejectionReason =
                    $"[RECIPE_PREVIEW] recipe '{authoringPreviewRecipeId}' had no compatible required route slot";
                return false;
            }

            if (resolved.Count != 3)
            {
                rejectionReason =
                    $"[RECIPE_SELECTION] route declared {resolved.Count} recipe slots instead of 3";
                return false;
            }

            slots = resolved.ToArray();
            return true;
        }

        private static bool TryBuildRecipeSlotBindings(
            RouteIntent intent,
            int slotNode,
            out RecipeOrientationBinding orientationBinding,
            out RecipePortBinding[] portBindings)
        {
            orientationBinding = RecipeOrientationBinding.RouteForward;
            portBindings = Array.Empty<RecipePortBinding>();
            if (intent == null || slotNode < 0 || slotNode >= intent.nodes.Length)
            {
                return false;
            }

            string slotId = intent.nodes[slotNode].recipeSlotId;
            if (string.Equals(slotId, CompressionRecipeSlotId, StringComparison.Ordinal))
            {
                portBindings = new[]
                {
                    new RecipePortBinding("entry", "main-0-1"),
                    new RecipePortBinding("exit", "main-1-2")
                };
                return true;
            }

            if (string.Equals(slotId, LandmarkRecipeSlotId, StringComparison.Ordinal))
            {
                orientationBinding = RecipeOrientationBinding.VistaSourceToTarget;
                portBindings = new[]
                {
                    new RecipePortBinding("entry", "main-3-4"),
                    new RecipePortBinding("exit", "main-4-5")
                };
                return true;
            }

            if (string.Equals(slotId, ReturnRecipeSlotId, StringComparison.Ordinal))
            {
                string entryEdgeId = string.Equals(intent.patternId, TwinWingPatternId, StringComparison.Ordinal)
                    ? "wing-b-11-12"
                    : "branch-11-12";
                string exitEdgeId = string.Equals(intent.patternId, TwinWingPatternId, StringComparison.Ordinal)
                    ? "wing-b-rejoin-12-5"
                    : string.Equals(intent.patternId, AtriumRingPatternId, StringComparison.Ordinal)
                        ? "rejoin-12-6"
                        : "rejoin-12-7";
                portBindings = new[]
                {
                    new RecipePortBinding("entry", entryEdgeId),
                    new RecipePortBinding("exit", exitEdgeId)
                };
                return true;
            }

            return false;
        }

        private static bool TryValidateRecipeCandidate(
            RouteIntent intent,
            int slotNode,
            DungeonRecipeAsset candidate,
            RecipeOrientationBinding orientationBinding,
            IReadOnlyList<RecipePortBinding> portBindings,
            out string reasonCode)
        {
            RouteNodeIntent node = intent.nodes[slotNode];
            if (candidate == null)
            {
                reasonCode = "CANDIDATE_NULL";
                return false;
            }

            if (Array.IndexOf(candidate.eligibleRoles, node.role) < 0)
            {
                reasonCode = "ROLE_INELIGIBLE";
                return false;
            }

            if (Array.IndexOf(candidate.eligibleBeats, node.beat) < 0)
            {
                reasonCode = "BEAT_INELIGIBLE";
                return false;
            }

            int incidentDegree = 0;
            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                if (edge.fromNode == slotNode || edge.toNode == slotNode)
                {
                    incidentDegree++;
                }
            }

            int mandatoryPortCount = 0;
            foreach (DungeonRecipePort port in candidate.ports ?? Array.Empty<DungeonRecipePort>())
            {
                mandatoryPortCount += port != null && port.mandatory ? 1 : 0;
            }

            bool incidentSockets = candidate.UsesIncidentCardinalSockets;
            bool degreeCompatible = incidentSockets
                ? incidentDegree >= candidate.minimumActiveSockets &&
                  incidentDegree <= candidate.maximumActiveSockets
                : mandatoryPortCount == incidentDegree;
            if (!degreeCompatible ||
                portBindings == null ||
                !incidentSockets && portBindings.Count != incidentDegree)
            {
                reasonCode = "TRAVERSAL_DEGREE_MISMATCH";
                return false;
            }

            var boundPorts = new HashSet<string>(StringComparer.Ordinal);
            var boundEdges = new HashSet<string>(StringComparer.Ordinal);
            if (!incidentSockets)
            {
                foreach (RecipePortBinding binding in portBindings)
                {
                    if (!boundPorts.Add(binding.portId) ||
                        !boundEdges.Add(binding.edgeId) ||
                        !TryGetTraversal(intent, binding.edgeId, out RouteTraversalIntent edge) ||
                        edge.fromNode != slotNode && edge.toNode != slotNode)
                    {
                        reasonCode = "PORT_BINDING_MISMATCH";
                        return false;
                    }
                }
            }

            foreach (DungeonRecipePort port in candidate.ports ?? Array.Empty<DungeonRecipePort>())
            {
                if (port == null ||
                    (!incidentSockets && (!port.mandatory || !boundPorts.Contains(port.id))))
                {
                    reasonCode = "PORT_BINDING_MISMATCH";
                    return false;
                }

                if (port.widthCells != 1 ||
                    port.approachDepthCells < 1 ||
                    port.headroomLevels < MinHeadroomLevels)
                {
                    reasonCode = "PORT_CLEARANCE_INCOMPATIBLE";
                    return false;
                }

                if (port.relativeLevel != 0)
                {
                    reasonCode = "PORT_ELEVATION_INCOMPATIBLE";
                    return false;
                }
            }

            bool orientationContextValid =
                (orientationBinding == RecipeOrientationBinding.RouteForward &&
                 (incidentSockets || boundPorts.Contains("exit"))) ||
                (orientationBinding == RecipeOrientationBinding.VistaSourceToTarget &&
                 intent.vista.targetNode == slotNode &&
                 intent.vista.sourceNode >= 0 &&
                 intent.vista.sourceNode < intent.nodes.Length);
            if (!orientationContextValid ||
                candidate.legalQuarterTurns == null ||
                candidate.legalQuarterTurns.Length == 0)
            {
                reasonCode = "ORIENTATION_UNSUPPORTED";
                return false;
            }

            foreach (DungeonRecipeTransition transition in
                     candidate.transitions ?? Array.Empty<DungeonRecipeTransition>())
            {
                if (transition == null ||
                    transition.riseLevels != 1 ||
                    transition.laneCount != 1 ||
                    transition.headroomLevels < MinHeadroomLevels ||
                    transition.lowerLandingCells == null ||
                    transition.lowerLandingCells.Length == 0 ||
                    transition.upperLandingCells == null ||
                    transition.upperLandingCells.Length == 0 ||
                    transition.footprintCells == null ||
                    transition.footprintCells.Length == 0)
                {
                    reasonCode = "TRANSITION_CONTEXT_INCOMPATIBLE";
                    return false;
                }
            }

            DungeonRecipeValidationResult validation = DungeonRecipeValidator.ValidateContract(candidate);
            if (!validation.Passed)
            {
                reasonCode = "CONTRACT_INVALID";
                return false;
            }

            reasonCode = string.Empty;
            return true;
        }

        private static System.Random RecipeSelectionRandom(
            int dungeonSeed,
            string topologyId,
            string routeNodeId)
        {
            // This stream intentionally excludes layout attempt and the spatial
            // random version so selection cannot perturb embedding or placement.
            unchecked
            {
                uint hash = 2166136261u;
                MixPhase1Hash(
                    ref hash,
                    dungeonSeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                MixPhase1Hash(ref hash, topologyId ?? string.Empty);
                MixPhase1Hash(ref hash, routeNodeId ?? string.Empty);
                MixPhase1Hash(ref hash, "recipe-selection");
                return new System.Random((int)hash);
            }
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
                if (!TryResolveRouteForwardRecipeAxis(
                        routeIntent,
                        slot,
                        nodeCenters,
                        out primaryAxis))
                {
                    rejectionReason = $"recipe '{slot.recipe.recipeId}' had no usable named exit-edge orientation";
                    return false;
                }
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
            if (!TryResolveActiveRecipePortBindings(
                    routeIntent,
                    slot,
                    nodeCenters,
                    primaryAxis,
                    transverseAxis,
                    mirrored,
                    out RecipePortBinding[] activePortBindings))
            {
                rejectionReason = $"recipe '{slot.recipe.recipeId}' could not bind its active ports to route neighbors";
                return false;
            }

            foreach (RecipePortBinding binding in activePortBindings)
            {
                DungeonRecipePort port = FindRecipePort(slot.recipe, binding.portId);
                if (port == null)
                {
                    rejectionReason = $"recipe port '{binding.portId}' had no route-edge binding";
                    return false;
                }

                ports.Add(new RecipePortPlacement(
                    port,
                    binding.edgeId,
                    NeighborForEdge(routeIntent, binding.edgeId, slot.slotNode),
                    TransformRecipeCell(port.cell, center, primaryAxis, transverseAxis, mirrored),
                    TransformRecipeDirection(port.outwardDirection, primaryAxis, transverseAxis, mirrored),
                    node.relativeElevationLevels + port.relativeLevel,
                    requiredForPlacement: true));
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
            RecipeShowpieceReservation showpieceReservation = default;
            if (slot.recipe.variations.Length > 0)
            {
                DungeonRecipeVariation variation = SelectRecipeVariation(
                    slot.recipe,
                    Phase1Random(dungeonSeed, layoutAttempt, slot.recipe.recipeId, "variation"));
                DungeonRecipeMotif motif = FindRecipeMotif(slot.recipe, variation.motifId);
                variationId = variation.id;
                visualImplementationId = motif.implementationId;
                if (!StairForge.TryGetBackedShowpiecePlacementContract(
                        visualImplementationId,
                        out StairForge.BackedShowpiecePlacementContract showpieceContract))
                {
                    rejectionReason =
                        $"recipe '{slot.recipe.recipeId}' selected unavailable backed visual '{visualImplementationId}'";
                    return false;
                }

                ResolvePrimaryVisualTransform(
                    slot.recipe,
                    center,
                    primaryAxis,
                    transverseAxis,
                    showpieceContract,
                    out showpieceOrigin,
                    out showpieceYaw);
                if (!TryBuildBackedShowpieceReservation(
                        slot.recipe,
                        room,
                        rooms,
                        center,
                        primaryAxis,
                        transverseAxis,
                        mirrored,
                        showpieceContract,
                        out showpieceReservation,
                        out rejectionReason))
                {
                    return false;
                }
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
                showpieceYaw,
                showpieceReservation);
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
            return TryResolveActiveRecipePortBindings(
                intent,
                slot,
                nodeCenters,
                primaryAxis,
                transverseAxis,
                mirrored,
                out _);
        }

        private static bool TryResolveActiveRecipePortBindings(
            RouteIntent intent,
            RecipeSlotIntent slot,
            IReadOnlyList<Vector2Int> nodeCenters,
            Vector2Int primaryAxis,
            Vector2Int transverseAxis,
            bool mirrored,
            out RecipePortBinding[] bindings)
        {
            bindings = Array.Empty<RecipePortBinding>();
            if (intent == null || slot?.recipe == null || nodeCenters == null)
            {
                return false;
            }

            if (!slot.recipe.UsesIncidentCardinalSockets)
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

                bindings = slot.portBindings;
                return true;
            }

            var resolved = new List<RecipePortBinding>();
            var usedPorts = new HashSet<string>(StringComparer.Ordinal);
            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                if (edge.fromNode != slot.slotNode && edge.toNode != slot.slotNode)
                {
                    continue;
                }

                int neighbor = edge.fromNode == slot.slotNode ? edge.toNode : edge.fromNode;
                Vector2Int delta = nodeCenters[neighbor] - nodeCenters[slot.slotNode];
                if (delta == Vector2Int.zero || delta.x != 0 && delta.y != 0)
                {
                    return false;
                }

                Vector2Int actualOutward = CardinalUnit(delta);
                DungeonRecipePort matchingSocket = null;
                foreach (DungeonRecipePort socket in slot.recipe.ports)
                {
                    if (socket != null &&
                        TransformRecipeDirection(
                            socket.outwardDirection,
                            primaryAxis,
                            transverseAxis,
                            mirrored) == actualOutward)
                    {
                        if (matchingSocket != null)
                        {
                            return false;
                        }

                        matchingSocket = socket;
                    }
                }

                if (matchingSocket == null || !usedPorts.Add(matchingSocket.id))
                {
                    return false;
                }

                resolved.Add(new RecipePortBinding(matchingSocket.id, edge.id));
            }

            if (resolved.Count < slot.recipe.minimumActiveSockets ||
                resolved.Count > slot.recipe.maximumActiveSockets)
            {
                return false;
            }

            bindings = resolved.ToArray();
            return true;
        }

        private static DungeonRecipePort FindRecipePort(
            DungeonRecipeAsset recipe,
            string portId)
        {
            foreach (DungeonRecipePort port in recipe?.ports ?? Array.Empty<DungeonRecipePort>())
            {
                if (port != null && string.Equals(port.id, portId, StringComparison.Ordinal))
                {
                    return port;
                }
            }

            return null;
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

        private static bool TryResolveRouteForwardRecipeAxis(
            RouteIntent intent,
            RecipeSlotIntent slot,
            IReadOnlyList<Vector2Int> nodeCenters,
            out Vector2Int primaryAxis)
        {
            primaryAxis = Vector2Int.zero;
            if (intent == null || slot == null || nodeCenters == null ||
                slot.slotNode < 0 || slot.slotNode >= nodeCenters.Count)
            {
                return false;
            }

            RouteTraversalIntent exitEdge = default;
            bool foundExitEdge =
                slot.TryGetEdgeId("exit", out string exitEdgeId) &&
                TryGetTraversal(intent, exitEdgeId, out exitEdge) &&
                (exitEdge.fromNode == slot.slotNode || exitEdge.toNode == slot.slotNode);
            if (!foundExitEdge && slot.recipe?.UsesIncidentCardinalSockets == true)
            {
                foreach (RouteTraversalIntent edge in intent.traversalEdges)
                {
                    if (edge.fromNode == slot.slotNode || edge.toNode == slot.slotNode)
                    {
                        exitEdge = edge;
                        foundExitEdge = true;
                        break;
                    }
                }
            }

            if (!foundExitEdge)
            {
                return false;
            }

            int neighbor;
            if (exitEdge.fromNode == slot.slotNode)
            {
                neighbor = exitEdge.toNode;
            }
            else if (exitEdge.toNode == slot.slotNode)
            {
                neighbor = exitEdge.fromNode;
            }
            else
            {
                return false;
            }

            if (neighbor < 0 || neighbor >= nodeCenters.Count)
            {
                return false;
            }

            Vector2Int delta = nodeCenters[neighbor] - nodeCenters[slot.slotNode];
            if (delta.x != 0 && delta.y != 0 || delta == Vector2Int.zero)
            {
                return false;
            }

            primaryAxis = CardinalUnit(delta);
            return true;
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

        private static bool TryBuildBackedShowpieceReservation(
            DungeonRecipeAsset recipe,
            RoomFootprint room,
            IReadOnlyList<RoomFootprint> allRooms,
            Vector2Int center,
            Vector2Int primaryAxis,
            Vector2Int transverseAxis,
            bool mirrored,
            StairForge.BackedShowpiecePlacementContract contract,
            out RecipeShowpieceReservation reservation,
            out string rejectionReason)
        {
            reservation = default;
            rejectionReason = string.Empty;
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

            if (primary == null ||
                focal == null ||
                focal.size.y != contract.widthCells ||
                contract.requiredFloorDepthCells <= 0 ||
                contract.wallEndMarginCells < 1)
            {
                rejectionReason =
                    $"[RECIPE_SHOWPIECE_FIT] '{recipe.recipeId}' did not provide a focal wall span matching '{contract.designName}'";
                return false;
            }

            int wallPrimary = primary.offset.x + primary.size.x - 1;
            int alongStart = focal.offset.y;
            int alongEnd = alongStart + contract.widthCells - 1;
            var requiredFloor = new HashSet<Vector2Int>();
            var wallMargins = new HashSet<Vector2Int>();
            var backdropVoid = new HashSet<Vector2Int>();
            for (int along = alongStart; along <= alongEnd; along++)
            {
                for (int depth = 0; depth < contract.requiredFloorDepthCells; depth++)
                {
                    requiredFloor.Add(TransformRecipeCell(
                        new Vector2Int(wallPrimary - depth, along),
                        center,
                        primaryAxis,
                        transverseAxis,
                        mirrored));
                }
            }

            for (int margin = 1; margin <= contract.wallEndMarginCells; margin++)
            {
                wallMargins.Add(TransformRecipeCell(
                    new Vector2Int(wallPrimary, alongStart - margin),
                    center,
                    primaryAxis,
                    transverseAxis,
                    mirrored));
                wallMargins.Add(TransformRecipeCell(
                    new Vector2Int(wallPrimary, alongEnd + margin),
                    center,
                    primaryAxis,
                    transverseAxis,
                    mirrored));
            }

            for (int along = alongStart - contract.wallEndMarginCells;
                 along <= alongEnd + contract.wallEndMarginCells;
                 along++)
            {
                backdropVoid.Add(TransformRecipeCell(
                    new Vector2Int(wallPrimary + 1, along),
                    center,
                    primaryAxis,
                    transverseAxis,
                    mirrored));
            }

            foreach (Vector2Int cell in requiredFloor)
            {
                if (!room.Contains(cell))
                {
                    rejectionReason =
                        $"[RECIPE_SHOWPIECE_FIT] '{contract.designName}' required unsupported floor cell {cell}";
                    return false;
                }
            }

            foreach (Vector2Int cell in wallMargins)
            {
                if (!room.Contains(cell))
                {
                    rejectionReason =
                        $"[RECIPE_SHOWPIECE_FIT] wall behind '{contract.designName}' ended without its required margin at {cell}";
                    return false;
                }
            }

            foreach (Vector2Int cell in backdropVoid)
            {
                foreach (RoomFootprint candidateRoom in allRooms)
                {
                    if (candidateRoom.Contains(cell))
                    {
                        rejectionReason =
                            $"[RECIPE_SHOWPIECE_FIT] '{contract.designName}' had no exterior wall backdrop at {cell}";
                        return false;
                    }
                }
            }

            reservation = new RecipeShowpieceReservation(
                SortedCells(requiredFloor).ToArray(),
                SortedCells(wallMargins).ToArray(),
                SortedCells(backdropVoid).ToArray());
            return true;
        }

        private static void ResolvePrimaryVisualTransform(
            DungeonRecipeAsset recipe,
            Vector2Int center,
            Vector2Int primaryAxis,
            Vector2Int transverseAxis,
            StairForge.BackedShowpiecePlacementContract contract,
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
            Vector2Int wallCenter = RecipeCell(center, primaryAxis, transverseAxis, primaryMax, 0);
            Vector2Int alongStart = RecipeCell(
                center,
                primaryAxis,
                transverseAxis,
                primaryMax,
                focal.offset.y);
            if (primaryAxis == Vector2Int.up)
            {
                originCell = new Vector2Int(
                    alongStart.x,
                    wallCenter.y - (contract.platformDepthCells - 1));
                yawDegrees = 0f;
            }
            else if (primaryAxis == Vector2Int.down)
            {
                originCell = new Vector2Int(
                    alongStart.x + contract.widthCells,
                    wallCenter.y + contract.platformDepthCells);
                yawDegrees = 180f;
            }
            else if (primaryAxis == Vector2Int.right)
            {
                originCell = new Vector2Int(
                    wallCenter.x - (contract.platformDepthCells - 1),
                    alongStart.y + contract.widthCells);
                yawDegrees = 90f;
            }
            else
            {
                originCell = new Vector2Int(
                    wallCenter.x + contract.platformDepthCells,
                    alongStart.y);
                yawDegrees = 270f;
            }
        }

        private static bool TryRealizeRecipes(
            IReadOnlyList<RecipePlacement> placements,
            DungeonLayout layout,
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

            var pathCells = new HashSet<Vector2Int>();
            foreach (RoomConnection connection in layout.connections)
            {
                pathCells.UnionWith(connection.path);
            }

            foreach (RecipePlacement placement in placements)
            {
                if (placement == null ||
                    placement.roomIndex < 0 ||
                    placement.roomIndex >= layout.rooms.Count)
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

                DungeonRecipePort firstPortContract =
                    FindRecipePort(placement.slot.recipe, placement.ports[0].id);
                if (firstPortContract == null)
                {
                    rejectionReason = $"[RECIPE_LEVELS] '{placement.RecipeId}' had no source contract for its primary port";
                    return false;
                }

                int baseLevel = firstPortLevel - firstPortContract.relativeLevel;
                foreach (RecipePortPlacement port in placement.ports)
                {
                    if (!cellLevels.TryGetValue(port.cell, out int portLevel) ||
                        portLevel != port.expectedRelativeLevel)
                    {
                        rejectionReason = $"[RECIPE_LEVELS] typed port '{port.id}' on '{placement.RecipeId}' resolved at {portLevel}u instead of {port.expectedRelativeLevel}u";
                        return false;
                    }
                }

                var recipeFootprints = new HashSet<Vector2Int>(
                    placement.showpieceReservation.requiredFloorCells ?? Array.Empty<Vector2Int>());
                var recipeLandings = new HashSet<Vector2Int>();
                var recipeTransitionCells = new HashSet<Vector2Int>();
                var recipeClearance = new HashSet<Vector2Int>(
                    placement.showpieceReservation.wallMarginCells ?? Array.Empty<Vector2Int>());
                recipeClearance.UnionWith(
                    placement.showpieceReservation.backdropVoidCells ?? Array.Empty<Vector2Int>());
                var recipeTransitionClearance = new HashSet<Vector2Int>(recipeClearance);

                foreach (RecipePortPlacement port in placement.ports)
                {
                    if (!TryCollectRecipePortApproachCells(
                            placement,
                            port,
                            layout,
                            out Vector2Int[] approachCells,
                            out rejectionReason))
                    {
                        return false;
                    }

                    if (RecipeTransitionAbutsPortWallEnd(placement, port))
                    {
                        recipeClearance.UnionWith(approachCells);
                        recipeTransitionClearance.UnionWith(approachCells);
                    }
                }

                foreach (RecipeTransitionPlacement recipeTransition in placement.transitions)
                {
                    recipeFootprints.UnionWith(recipeTransition.footprintCells);
                    recipeLandings.UnionWith(recipeTransition.lowerLandingCells);
                    recipeLandings.UnionWith(recipeTransition.upperLandingCells);
                    recipeTransitionCells.Add(recipeTransition.lowerTransitionCell);
                    recipeTransitionCells.Add(recipeTransition.upperTransitionCell);
                    if (transitionKeys.Contains(TransitionKey(
                            recipeTransition.upperTransitionCell,
                            recipeTransition.lowerTransitionCell)))
                    {
                        rejectionReason =
                            $"[RECIPE_ATOMICITY] transition '{recipeTransition.id}' on '{placement.RecipeId}' conflicted with an existing transition";
                        return false;
                    }
                }

                foreach (Vector2Int cell in
                         placement.showpieceReservation.requiredFloorCells ?? Array.Empty<Vector2Int>())
                {
                    if (!cellLevels.TryGetValue(cell, out int level) ||
                        level != baseLevel ||
                        ResolvedRecipeRelativeLevel(placement.zones, cell) != 0 ||
                        pathCells.Contains(cell))
                    {
                        rejectionReason =
                            $"[RECIPE_SHOWPIECE_FIT] '{placement.selectedVisualImplementationId}' lacked clear, uniform floor support at {cell}";
                        return false;
                    }
                }

                foreach (Vector2Int cell in
                         placement.showpieceReservation.wallMarginCells ?? Array.Empty<Vector2Int>())
                {
                    if (!layout.floorCells.Contains(cell) || pathCells.Contains(cell))
                    {
                        rejectionReason =
                            $"[RECIPE_SHOWPIECE_FIT] '{placement.selectedVisualImplementationId}' lacked a clear wall-end margin at {cell}";
                        return false;
                    }
                }

                foreach (Vector2Int cell in
                         placement.showpieceReservation.backdropVoidCells ?? Array.Empty<Vector2Int>())
                {
                    if (layout.floorCells.Contains(cell) || pathCells.Contains(cell))
                    {
                        rejectionReason =
                            $"[RECIPE_SHOWPIECE_FIT] '{placement.selectedVisualImplementationId}' did not abut an exterior wall at {cell}";
                        return false;
                    }
                }

                foreach (Vector2Int cell in recipeFootprints)
                {
                    if (recipeLandings.Contains(cell) || recipeClearance.Contains(cell))
                    {
                        rejectionReason =
                            $"[RECIPE_CLEARANCE] '{placement.RecipeId}' overlapped occupied and required-clear geometry at {cell}";
                        return false;
                    }
                }

                foreach (Vector2Int cell in recipeTransitionCells)
                {
                    if (recipeTransitionClearance.Contains(cell))
                    {
                        rejectionReason =
                            $"[RECIPE_CLEARANCE] '{placement.RecipeId}' placed a transition mouth in required-clear geometry at {cell}";
                        return false;
                    }
                }

                if (stairLedger.ConflictsWithReservation(
                        recipeFootprints,
                        recipeLandings,
                        recipeTransitionCells,
                        recipeClearance,
                        recipeTransitionClearance,
                        out Vector2Int conflictCell))
                {
                    rejectionReason =
                        $"[RECIPE_CLEARANCE] '{placement.RecipeId}' conflicted with an existing structural reservation at {conflictCell}";
                    return false;
                }

                stairLedger.Register(
                    SortedCells(recipeFootprints).ToArray(),
                    SortedCells(recipeLandings).ToArray(),
                    Array.Empty<Vector2Int>(),
                    SortedCells(recipeTransitionCells).ToArray(),
                    SortedCells(recipeClearance).ToArray(),
                    SortedCells(recipeTransitionClearance).ToArray());

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
                    transitionKeys.Add(TransitionKey(
                        recipeTransition.upperTransitionCell,
                        recipeTransition.lowerTransitionCell));

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
                }

                if (!string.IsNullOrEmpty(placement.selectedVisualImplementationId))
                {
                    if (!StairForge.TryGetBackedShowpiecePlacementContract(
                            placement.selectedVisualImplementationId,
                            out StairForge.BackedShowpiecePlacementContract showpieceContract))
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
                        pieces = showpieceContract.pieces
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

        private static bool TryCollectRecipePortApproachCells(
            RecipePlacement placement,
            RecipePortPlacement port,
            DungeonLayout layout,
            out Vector2Int[] approachCells,
            out string rejectionReason)
        {
            approachCells = Array.Empty<Vector2Int>();
            rejectionReason = string.Empty;
            if (port.widthCells < 1 ||
                (port.widthCells & 1) == 0 ||
                port.approachDepthCells < 1 ||
                port.headroomLevels < MinHeadroomLevels)
            {
                rejectionReason =
                    $"[RECIPE_PORT_APPROACH] typed port '{port.id}' on '{placement.RecipeId}' had an invalid width, depth, or headroom contract";
                return false;
            }

            RoomConnection boundConnection = default;
            bool found = false;
            bool recipeAtStart = false;
            foreach (RoomConnection connection in layout.connections)
            {
                if (connection.fromRoom == placement.roomIndex &&
                    connection.toRoom == port.neighborRoomIndex)
                {
                    boundConnection = connection;
                    recipeAtStart = true;
                    found = true;
                    break;
                }

                if (connection.toRoom == placement.roomIndex &&
                    connection.fromRoom == port.neighborRoomIndex)
                {
                    boundConnection = connection;
                    recipeAtStart = false;
                    found = true;
                    break;
                }
            }

            if (!found ||
                boundConnection.path == null ||
                boundConnection.path.Count <= port.approachDepthCells)
            {
                rejectionReason =
                    $"[RECIPE_PORT_APPROACH] edge '{port.edgeId}' did not provide {port.approachDepthCells} clear approach cells for port '{port.id}'";
                return false;
            }

            Vector2Int pathPortCell = recipeAtStart
                ? boundConnection.path[0]
                : boundConnection.path[boundConnection.path.Count - 1];
            if (pathPortCell != port.cell)
            {
                rejectionReason =
                    $"[RECIPE_PORT_APPROACH] edge '{port.edgeId}' did not begin at typed port '{port.id}'";
                return false;
            }

            for (int depth = 1; depth <= port.approachDepthCells; depth++)
            {
                Vector2Int centerCell = port.cell + port.outwardDirection * depth;
                Vector2Int actualPathCell = recipeAtStart
                    ? boundConnection.path[depth]
                    : boundConnection.path[boundConnection.path.Count - 1 - depth];
                if (actualPathCell != centerCell)
                {
                    rejectionReason =
                        $"[RECIPE_PORT_APPROACH] edge '{port.edgeId}' turned inside port '{port.id}' approach at depth {depth}";
                    return false;
                }
            }

            Vector2Int[] declaredCells = BuildRecipePortApproachReservationCells(port);
            foreach (Vector2Int cell in declaredCells)
            {
                if (!layout.floorCells.Contains(cell))
                {
                    rejectionReason =
                        $"[RECIPE_PORT_APPROACH] port '{port.id}' on '{placement.RecipeId}' lacked floor width at {cell}";
                    return false;
                }
            }

            approachCells = declaredCells;
            return true;
        }

        private static bool RecipeTransitionAbutsPortWallEnd(
            RecipePlacement placement,
            RecipePortPlacement port)
        {
            foreach (RecipeTransitionPlacement transition in placement.transitions)
            {
                foreach (Vector2Int footprintCell in transition.footprintCells)
                {
                    Vector2Int offset = footprintCell - port.cell;
                    if (Mathf.Abs(offset.x) + Mathf.Abs(offset.y) == 1 &&
                        offset.x * port.outwardDirection.x +
                        offset.y * port.outwardDirection.y == 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static Vector2Int[] BuildRecipePortApproachReservationCells(
            RecipePortPlacement port)
        {
            Vector2Int lateral = new Vector2Int(
                -port.outwardDirection.y,
                port.outwardDirection.x);
            int halfWidth = port.widthCells / 2;
            var cells = new HashSet<Vector2Int>();
            for (int depth = 1; depth <= port.approachDepthCells; depth++)
            {
                Vector2Int centerCell = port.cell + port.outwardDirection * depth;
                for (int offset = -halfWidth; offset <= halfWidth; offset++)
                {
                    cells.Add(centerCell + lateral * offset);
                }
            }

            return SortedCells(cells).ToArray();
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
            bool portCountValid = placement != null &&
                (placement.slot.recipe.UsesIncidentCardinalSockets
                    ? placement.ports.Length >= placement.slot.recipe.minimumActiveSockets &&
                      placement.ports.Length <= placement.slot.recipe.maximumActiveSockets
                    : placement.ports.Length == placement.slot.recipe.ports.Length);
            if (placement == null ||
                placement.transitions.Length != placement.slot.recipe.transitions.Length ||
                !portCountValid)
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

                if (!TryCollectRecipePortApproachCells(
                        placement,
                        port,
                        layout,
                        out Vector2Int[] approachCells,
                        out rejectionReason))
                {
                    return false;
                }

                bool transitionMouthMustStayClear =
                    RecipeTransitionAbutsPortWallEnd(placement, port);
                foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
                {
                    foreach (Vector2Int approachCell in approachCells)
                    {
                        bool transitionMouthConflict =
                            transition.firstCell == approachCell ||
                            transition.secondCell == approachCell;
                        bool footprintConflict =
                            Array.IndexOf(transition.footprintCells, approachCell) >= 0;
                        if (transitionMouthMustStayClear &&
                            (transitionMouthConflict || footprintConflict))
                        {
                            rejectionReason =
                                $"[RECIPE_PORT_APPROACH] transition '{transition.placementClass}' consumed port '{port.id}' approach cell {approachCell}";
                            return false;
                        }
                    }
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

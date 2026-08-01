using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace DungeonLab.Editor
{
    public enum DungeonRecipeValidationLayer
    {
        Schema,
        Structure,
        Variation,
        Neighbor,
        FullDungeon
    }

    public readonly struct DungeonRecipeValidationFinding
    {
        public readonly DungeonRecipeValidationLayer layer;
        public readonly string code;
        public readonly string message;

        public DungeonRecipeValidationFinding(
            DungeonRecipeValidationLayer layer,
            string code,
            string message)
        {
            this.layer = layer;
            this.code = code ?? string.Empty;
            this.message = message ?? string.Empty;
        }
    }

    public sealed class DungeonRecipeValidationResult
    {
        private readonly List<DungeonRecipeValidationFinding> findings =
            new List<DungeonRecipeValidationFinding>();
        private readonly HashSet<DungeonRecipeValidationLayer> executedLayers =
            new HashSet<DungeonRecipeValidationLayer>();

        public IReadOnlyList<DungeonRecipeValidationFinding> Findings => findings;
        public bool Passed => findings.Count == 0;

        public bool WasExecuted(DungeonRecipeValidationLayer layer)
        {
            return executedLayers.Contains(layer);
        }

        public bool LayerPassed(DungeonRecipeValidationLayer layer)
        {
            if (!executedLayers.Contains(layer))
            {
                return false;
            }

            foreach (DungeonRecipeValidationFinding finding in findings)
            {
                if (finding.layer == layer)
                {
                    return false;
                }
            }

            return true;
        }

        internal void MarkExecuted(DungeonRecipeValidationLayer layer)
        {
            executedLayers.Add(layer);
        }

        internal void Add(
            DungeonRecipeValidationLayer layer,
            string code,
            string message)
        {
            findings.Add(new DungeonRecipeValidationFinding(layer, code, message));
        }

        internal void Append(DungeonRecipeValidationResult other)
        {
            if (other == null)
            {
                return;
            }

            foreach (DungeonRecipeValidationLayer layer in Enum.GetValues(typeof(DungeonRecipeValidationLayer)))
            {
                if (other.WasExecuted(layer))
                {
                    executedLayers.Add(layer);
                }
            }

            findings.AddRange(other.findings);
        }
    }

    public readonly struct DungeonRecipeFullDungeonEvidence
    {
        public readonly string recipeId;
        public readonly bool placedAtomically;
        public readonly int boundMandatoryPortCount;
        public readonly int resolvedTransitionCount;
        public readonly bool canonicalPlanValid;
        public readonly bool rendererValid;
        public readonly bool abyssSupportValid;
        public readonly bool collisionValid;
        public readonly bool forcedAuthoringPreview;
        public readonly string previewTopologyId;
        public readonly string previewRecipeSlotId;
        public readonly string previewRouteNodeId;

        public DungeonRecipeFullDungeonEvidence(
            string recipeId,
            bool placedAtomically,
            int boundMandatoryPortCount,
            int resolvedTransitionCount,
            bool canonicalPlanValid,
            bool rendererValid,
            bool abyssSupportValid,
            bool collisionValid,
            bool forcedAuthoringPreview,
            string previewTopologyId,
            string previewRecipeSlotId,
            string previewRouteNodeId)
        {
            this.recipeId = recipeId ?? string.Empty;
            this.placedAtomically = placedAtomically;
            this.boundMandatoryPortCount = boundMandatoryPortCount;
            this.resolvedTransitionCount = resolvedTransitionCount;
            this.canonicalPlanValid = canonicalPlanValid;
            this.rendererValid = rendererValid;
            this.abyssSupportValid = abyssSupportValid;
            this.collisionValid = collisionValid;
            this.forcedAuthoringPreview = forcedAuthoringPreview;
            this.previewTopologyId = previewTopologyId ?? string.Empty;
            this.previewRecipeSlotId = previewRecipeSlotId ?? string.Empty;
            this.previewRouteNodeId = previewRouteNodeId ?? string.Empty;
        }
    }

    public static class DungeonRecipeValidator
    {
        public static DungeonRecipeValidationResult ValidateContract(DungeonRecipeAsset recipe)
        {
            var result = new DungeonRecipeValidationResult();
            ValidateSchema(recipe, result);
            ValidateStructure(recipe, result);
            ValidateVariations(recipe, result);
            ValidateNeighbors(recipe, result);
            return result;
        }

        public static DungeonRecipeValidationResult ValidateWithFullDungeonEvidence(
            DungeonRecipeAsset recipe,
            DungeonRecipeFullDungeonEvidence fullDungeonEvidence)
        {
            DungeonRecipeValidationResult result = ValidateContract(recipe);
            ValidateFullDungeon(recipe, fullDungeonEvidence, result);
            return result;
        }

        public static string ComputeContentDigest(DungeonRecipeAsset recipe)
        {
            if (recipe == null)
            {
                return string.Empty;
            }

            var canonical = new StringBuilder(4096);
            Append(canonical, "id", recipe.recipeId);
            Append(canonical, "display", recipe.displayName);
            Append(canonical, "kind", (int)recipe.kind);
            Append(canonical, "schema", recipe.schemaVersion);
            Append(canonical, "content", recipe.contentVersion);
            Append(canonical, "mirror", recipe.allowMirror ? 1 : 0);
            AppendStrings(canonical, "roles", recipe.eligibleRoles);
            AppendStrings(canonical, "beats", recipe.eligibleBeats);
            AppendInts(canonical, "turns", recipe.legalQuarterTurns);
            if (recipe.UsesIncidentCardinalSockets)
            {
                Append(canonical, "portBindingMode", (int)recipe.portBindingMode);
                Append(canonical, "minimumActiveSockets", recipe.minimumActiveSockets);
                Append(canonical, "maximumActiveSockets", recipe.maximumActiveSockets);
            }

            // Layer fields are appended ONLY by a recipe that declares layers,
            // exactly as the socket fields above are. This is not tidiness: the
            // digest of every recipe feeds `catalogDigest`, `catalogDigest` is
            // in the route-intent projection, and `routeIntentHash` is in
            // `hashes.canonical` — so an unconditional append would move every
            // seed's canonical hash for a schema addition that changed no
            // geometry. Today's recipes declare no layers and hash as before.
            if (recipe.DeclaresLayers)
            {
                foreach (DungeonRecipeLayer layer in recipe.layers)
                {
                    Append(canonical, "layer.id", layer?.layerId);
                    Append(canonical, "layer.level", layer?.relativeLevel ?? 0);
                    Append(canonical, "layer.base", layer != null && layer.isBase ? 1 : 0);
                }
            }

            foreach (DungeonRecipeZone zone in recipe.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                Append(canonical, "zone.id", zone?.id);
                Append(canonical, "zone.kind", zone == null ? -1 : (int)zone.kind);
                Append(canonical, "zone.offset", zone?.offset ?? default);
                Append(canonical, "zone.size", zone?.size ?? default);
                Append(canonical, "zone.level", zone?.relativeLevel ?? 0);
                if (recipe.DeclaresLayers)
                {
                    Append(canonical, "zone.layer", zone?.layerId);
                }

                // D4, and the same conditional for the same hash reason as the
                // layer fields above. `zone.kind` already distinguishes an
                // OpenVolume from a Walkable, but its HEIGHT is what the
                // reservation actually is — without this, two recipes whose
                // atria differ by four levels of air digest identically and the
                // catalog cannot tell them apart.
                if (zone != null && zone.openVolumeHeightLevels != 0)
                {
                    Append(canonical, "zone.openVolume", zone.openVolumeHeightLevels);
                }
            }

            foreach (DungeonRecipePort port in recipe.ports ?? Array.Empty<DungeonRecipePort>())
            {
                Append(canonical, "port.id", port?.id);
                Append(canonical, "port.type", port == null ? -1 : (int)port.type);
                Append(canonical, "port.mandatory", port != null && port.mandatory ? 1 : 0);
                Append(canonical, "port.cell", port?.cell ?? default);
                Append(canonical, "port.out", port?.outwardDirection ?? default);
                Append(canonical, "port.level", port?.relativeLevel ?? 0);
                if (recipe.DeclaresLayers)
                {
                    Append(canonical, "port.layer", port?.layerId);
                }

                Append(canonical, "port.width", port?.widthCells ?? 0);
                Append(canonical, "port.approach", port?.approachDepthCells ?? 0);
                Append(canonical, "port.headroom", port?.headroomLevels ?? 0);
            }

            // Conditional for the same reason the layer fields are: today's
            // recipes author no opening and must hash exactly as they did.
            if (recipe.DeclaresOpenings)
            {
                foreach (DungeonRecipeOpening opening in recipe.openings)
                {
                    Append(canonical, "opening.id", opening?.id);
                    Append(canonical, "opening.cell", opening?.cell ?? default);
                    Append(canonical, "opening.out", opening?.outwardDirection ?? default);
                    Append(canonical, "opening.layer", opening?.layerId);
                    // Aperture was the implicit meaning before Phase E. Keeping
                    // its zero value out preserves every existing recipe digest.
                    if (opening != null && opening.kind != OpeningKind.Aperture)
                    {
                        Append(canonical, "opening.kind", (int)opening.kind);
                    }
                }
            }

            foreach (DungeonRecipeMotif motif in recipe.motifs ?? Array.Empty<DungeonRecipeMotif>())
            {
                Append(canonical, "motif.id", motif?.id);
                Append(canonical, "motif.kind", motif == null ? -1 : (int)motif.kind);
                Append(canonical, "motif.impl", motif?.implementationId);
            }

            foreach (DungeonRecipeTransition transition in recipe.transitions ?? Array.Empty<DungeonRecipeTransition>())
            {
                Append(canonical, "transition.id", transition?.id);
                Append(canonical, "transition.group", transition?.atomicGroupId);
                Append(canonical, "transition.motif", transition?.motifId);
                Append(canonical, "transition.lower", transition?.lowerTransitionCell ?? default);
                Append(canonical, "transition.upper", transition?.upperTransitionCell ?? default);
                AppendVectors(canonical, "transition.lowerLandings", transition?.lowerLandingCells);
                AppendVectors(canonical, "transition.upperLandings", transition?.upperLandingCells);
                AppendVectors(canonical, "transition.footprint", transition?.footprintCells);
                Append(canonical, "transition.climb", transition?.climbDirection ?? default);
                Append(canonical, "transition.rise", transition?.riseLevels ?? 0);
                Append(canonical, "transition.lanes", transition?.laneCount ?? 0);
                Append(canonical, "transition.headroom", transition?.headroomLevels ?? 0);
                if (recipe.DeclaresLayers)
                {
                    Append(canonical, "transition.lowerLayer", transition?.lowerLayerId);
                    Append(canonical, "transition.upperLayer", transition?.upperLayerId);
                }
            }

            foreach (DungeonRecipeSymmetryPair pair in recipe.symmetryPairs ?? Array.Empty<DungeonRecipeSymmetryPair>())
            {
                Append(canonical, "symmetry.id", pair?.id);
                Append(canonical, "symmetry.first", pair?.firstZoneId);
                Append(canonical, "symmetry.second", pair?.secondZoneId);
            }

            foreach (DungeonRecipeVariation variation in recipe.variations ?? Array.Empty<DungeonRecipeVariation>())
            {
                Append(canonical, "variation.id", variation?.id);
                Append(canonical, "variation.motif", variation?.motifId);
                Append(canonical, "variation.weight", variation?.weight ?? 0);
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                var result = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes)
                {
                    result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }

                return result.ToString();
            }
        }

        private static void ValidateSchema(
            DungeonRecipeAsset recipe,
            DungeonRecipeValidationResult result)
        {
            const DungeonRecipeValidationLayer Layer = DungeonRecipeValidationLayer.Schema;
            result.MarkExecuted(Layer);
            if (recipe == null)
            {
                result.Add(Layer, "RECIPE_NULL", "Recipe asset was null.");
                return;
            }

            if (!IsStableId(recipe.recipeId))
            {
                result.Add(Layer, "RECIPE_ID", $"Recipe ID '{recipe.recipeId}' was not a stable lowercase ID.");
            }

            if (recipe.schemaVersion != DungeonRecipeAsset.CurrentSchemaVersion)
            {
                result.Add(Layer, "RECIPE_SCHEMA_VERSION", $"Schema {recipe.schemaVersion} is unsupported.");
            }

            if (recipe.contentVersion < 1)
            {
                result.Add(Layer, "RECIPE_CONTENT_VERSION", "Content version must be positive.");
            }

            if (recipe.eligibleRoles == null || recipe.eligibleRoles.Length == 0 ||
                recipe.eligibleBeats == null || recipe.eligibleBeats.Length == 0)
            {
                result.Add(Layer, "RECIPE_ELIGIBILITY", "At least one eligible role and beat are required.");
            }

            var turns = new HashSet<int>();
            foreach (int turn in recipe.legalQuarterTurns ?? Array.Empty<int>())
            {
                if (turn < 0 || turn > 3 || !turns.Add(turn))
                {
                    result.Add(Layer, "RECIPE_ORIENTATION", $"Quarter turn '{turn}' was invalid or duplicated.");
                }
            }

            if (turns.Count == 0)
            {
                result.Add(Layer, "RECIPE_ORIENTATION", "At least one legal orientation is required.");
            }

            if (!Enum.IsDefined(typeof(DungeonRecipePortBindingMode), recipe.portBindingMode))
            {
                result.Add(
                    Layer,
                    "RECIPE_PORT_BINDING_MODE",
                    $"Port binding mode '{recipe.portBindingMode}' is unsupported.");
            }
            else if (recipe.UsesIncidentCardinalSockets &&
                (recipe.minimumActiveSockets < 1 ||
                 recipe.maximumActiveSockets > 4 ||
                 recipe.minimumActiveSockets > recipe.maximumActiveSockets))
            {
                result.Add(
                    Layer,
                    "RECIPE_SOCKET_POLICY",
                    "Incident cardinal sockets require an active range within 1..4.");
            }

            CheckIds(recipe.zones, value => value?.id, "zone", Layer, result);
            CheckIds(recipe.ports, value => value?.id, "port", Layer, result);
            CheckIds(recipe.motifs, value => value?.id, "motif", Layer, result);
            CheckIds(recipe.transitions, value => value?.id, "transition", Layer, result);
            CheckIds(recipe.symmetryPairs, value => value?.id, "symmetry", Layer, result);
            CheckIds(recipe.variations, value => value?.id, "variation", Layer, result);

        }

        private static void ValidateStructure(
            DungeonRecipeAsset recipe,
            DungeonRecipeValidationResult result)
        {
            const DungeonRecipeValidationLayer Layer = DungeonRecipeValidationLayer.Structure;
            result.MarkExecuted(Layer);
            if (recipe == null)
            {
                return;
            }

            ValidateLayers(recipe, result);

            var footprint = new HashSet<Vector2Int>();
            var zonesById = new Dictionary<string, DungeonRecipeZone>(StringComparer.Ordinal);
            foreach (DungeonRecipeZone zone in recipe.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                if (zone == null || zone.size.x < 1 || zone.size.y < 1)
                {
                    result.Add(Layer, "RECIPE_ZONE_SIZE", $"Zone '{zone?.id}' had an invalid size.");
                    continue;
                }

                zonesById[zone.id] = zone;
                if (zone.kind == DungeonRecipeZoneKind.Walkable || zone.kind == DungeonRecipeZoneKind.Elevated)
                {
                    AddRectCells(footprint, zone);
                }

                if (zone.kind == DungeonRecipeZoneKind.Elevated && zone.relativeLevel <= 0 ||
                    zone.kind != DungeonRecipeZoneKind.Elevated && zone.relativeLevel != 0)
                {
                    result.Add(Layer, "RECIPE_ZONE_LEVEL", $"Zone '{zone.id}' had an incompatible relative level.");
                }

                // A reserved void with no height reserves nothing, and a height
                // on anything else is an authoring slip that would silently do
                // nothing. Both are the same class of mistake as a beat typo:
                // the recipe validates, generation runs, and the feature is
                // absent (design §6, §11).
                bool isOpenVolume = zone.kind == DungeonRecipeZoneKind.OpenVolume;
                if (isOpenVolume ? zone.openVolumeHeightLevels < 1 : zone.openVolumeHeightLevels != 0)
                {
                    result.Add(
                        Layer,
                        "RECIPE_OPEN_VOLUME_HEIGHT",
                        isOpenVolume
                            ? $"OpenVolume zone '{zone.id}' reserved no height."
                            : $"Zone '{zone.id}' declared an open-volume height but is not an OpenVolume.");
                }
            }

            if (footprint.Count == 0)
            {
                result.Add(Layer, "RECIPE_FOOTPRINT", "No walkable recipe footprint was declared.");
            }

            foreach (DungeonRecipeZone zone in recipe.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                if (zone == null ||
                    zone.kind != DungeonRecipeZoneKind.ProtectedCirculation &&
                    zone.kind != DungeonRecipeZoneKind.ProtectedFocal)
                {
                    continue;
                }

                foreach (Vector2Int cell in Cells(zone))
                {
                    if (!footprint.Contains(cell))
                    {
                        result.Add(Layer, "RECIPE_PROTECTED_ZONE", $"Protected zone '{zone.id}' escaped the walkable footprint at {cell}.");
                    }
                }
            }

            int mandatoryPortCount = 0;
            foreach (DungeonRecipePort port in recipe.ports ?? Array.Empty<DungeonRecipePort>())
            {
                if (port == null)
                {
                    result.Add(Layer, "RECIPE_PORT_NULL", "A port entry was null.");
                    continue;
                }

                mandatoryPortCount += port.mandatory ? 1 : 0;
                if (!IsCardinalUnit(port.outwardDirection) ||
                    !footprint.Contains(port.cell) ||
                    footprint.Contains(port.cell + port.outwardDirection) ||
                    port.widthCells != 1 ||
                    port.approachDepthCells < 1 ||
                    port.headroomLevels < 3 ||
                    RelativeLevelAt(recipe, port.cell, port.layerId) != port.relativeLevel)
                {
                    result.Add(Layer, "RECIPE_PORT_GEOMETRY", $"Port '{port.id}' did not declare an exact open boundary, level, approach, and headroom contract.");
                }
            }

            if (recipe.UsesIncidentCardinalSockets)
            {
                DungeonRecipePort[] sockets = recipe.ports ?? Array.Empty<DungeonRecipePort>();
                var socketDirections = new HashSet<Vector2Int>();
                bool socketContractValid =
                    sockets.Length == 4 &&
                    recipe.maximumActiveSockets <= sockets.Length;
                foreach (DungeonRecipePort socket in sockets)
                {
                    socketContractValid &=
                        socket != null &&
                        !socket.mandatory &&
                        socket.type == DungeonRecipePortType.Corridor &&
                        socket.relativeLevel == 0 &&
                        socketDirections.Add(socket.outwardDirection);
                }

                socketContractValid &=
                    socketDirections.SetEquals(new[]
                    {
                        Vector2Int.up,
                        Vector2Int.right,
                        Vector2Int.down,
                        Vector2Int.left
                    });
                if (!socketContractValid)
                {
                    result.Add(
                        Layer,
                        "RECIPE_CARDINAL_SOCKETS",
                        "Incident socket recipes require exactly four non-mandatory level-0 corridor sockets, one on each cardinal side.");
                }

                if (!AllPortsShareConnectedFootprint(sockets, footprint))
                {
                    result.Add(
                        Layer,
                        "RECIPE_SOCKET_CONNECTIVITY",
                        "Every cardinal socket must belong to the same connected room footprint.");
                }
            }
            else if (mandatoryPortCount < 2)
            {
                result.Add(Layer, "RECIPE_MANDATORY_PORTS", "The proven recipe seam requires at least two mandatory route ports.");
            }

            var motifsById = new Dictionary<string, DungeonRecipeMotif>(StringComparer.Ordinal);
            foreach (DungeonRecipeMotif motif in recipe.motifs ?? Array.Empty<DungeonRecipeMotif>())
            {
                if (motif != null)
                {
                    motifsById[motif.id] = motif;
                }
            }

            foreach (DungeonRecipeTransition transition in recipe.transitions ?? Array.Empty<DungeonRecipeTransition>())
            {
                // A stair joining two STOREYS may rise more than 1u — that is
                // what makes a layered recipe's layers connected, and C1's
                // episode separates its two by 4u. Within one layer the rule is
                // unchanged, so a recipe that declares no layers cannot reach
                // the relaxed branch at all.
                bool crossesLayers = transition != null &&
                    !SameLayer(recipe, transition.lowerLayerId, transition.upperLayerId);
                bool riseValid = transition != null &&
                    (crossesLayers ? transition.riseLevels >= 1 : transition.riseLevels == 1);
                if (transition == null ||
                    !motifsById.TryGetValue(transition?.motifId ?? string.Empty, out DungeonRecipeMotif motif) ||
                    motif.kind != DungeonRecipeMotifKind.StairTransition ||
                    !riseValid ||
                    transition.laneCount != 1 ||
                    transition.headroomLevels < 3 ||
                    !IsCardinalUnit(transition.climbDirection) ||
                    transition.lowerLandingCells == null || transition.lowerLandingCells.Length == 0 ||
                    transition.upperLandingCells == null || transition.upperLandingCells.Length == 0 ||
                    transition.footprintCells == null || transition.footprintCells.Length == 0)
                {
                    result.Add(Layer, "RECIPE_TRANSITION_CONTRACT", $"Transition '{transition?.id}' lacked its proven stair, lane, landing, footprint, rise, or headroom contract.");
                    continue;
                }

                // The rise is measured between STOREYS — layer offset included —
                // while the landings must agree with their own end's rise WITHIN
                // its layer. Both collapse to today's arithmetic when every
                // layer offset is 0.
                bool cellsValid = footprint.Contains(transition.lowerTransitionCell) &&
                    footprint.Contains(transition.upperTransitionCell) &&
                    AbsoluteLevelAt(recipe, transition.upperTransitionCell, transition.upperLayerId) -
                    AbsoluteLevelAt(recipe, transition.lowerTransitionCell, transition.lowerLayerId) ==
                    transition.riseLevels;
                cellsValid &= CellsHaveLevel(
                    recipe,
                    transition.lowerLandingCells,
                    transition.lowerLayerId,
                    RelativeLevelAt(recipe, transition.lowerTransitionCell, transition.lowerLayerId));
                cellsValid &= CellsHaveLevel(
                    recipe,
                    transition.upperLandingCells,
                    transition.upperLayerId,
                    RelativeLevelAt(recipe, transition.upperTransitionCell, transition.upperLayerId));
                cellsValid &= CellsInside(footprint, transition.footprintCells);
                if (!cellsValid)
                {
                    result.Add(Layer, "RECIPE_TRANSITION_GEOMETRY", $"Transition '{transition.id}' did not align its cells, levels, footprint, and landings.");
                }
            }

            foreach (DungeonRecipeSymmetryPair pair in recipe.symmetryPairs ?? Array.Empty<DungeonRecipeSymmetryPair>())
            {
                if (pair == null ||
                    !zonesById.TryGetValue(pair.firstZoneId, out DungeonRecipeZone first) ||
                    !zonesById.TryGetValue(pair.secondZoneId, out DungeonRecipeZone second) ||
                    !ZonesMirrorAcrossPrimaryAxis(first, second))
                {
                    result.Add(Layer, "RECIPE_SYMMETRY", $"Symmetry pair '{pair?.id}' was incomplete or not mirrored across the primary axis.");
                }
            }
        }

        private static void ValidateVariations(
            DungeonRecipeAsset recipe,
            DungeonRecipeValidationResult result)
        {
            const DungeonRecipeValidationLayer Layer = DungeonRecipeValidationLayer.Variation;
            result.MarkExecuted(Layer);
            if (recipe == null)
            {
                return;
            }

            var motifs = new Dictionary<string, DungeonRecipeMotif>(StringComparer.Ordinal);
            foreach (DungeonRecipeMotif motif in recipe.motifs ?? Array.Empty<DungeonRecipeMotif>())
            {
                if (motif != null)
                {
                    motifs[motif.id] = motif;
                }
            }

            foreach (DungeonRecipeVariation variation in recipe.variations ?? Array.Empty<DungeonRecipeVariation>())
            {
                if (variation == null || variation.weight < 1 ||
                    !motifs.TryGetValue(variation?.motifId ?? string.Empty, out DungeonRecipeMotif motif) ||
                    motif.kind != DungeonRecipeMotifKind.FocalVisual ||
                    !StairForge.TryGetBackedShowpieceDesign(
                        motif.implementationId,
                        out ElevationEdgeModel.SynthesizedPiecePlacement[] pieces) ||
                    pieces == null || pieces.Length == 0)
                {
                    result.Add(Layer, "RECIPE_VARIATION", $"Variation '{variation?.id}' did not resolve to a weighted backed focal motif.");
                }
            }
        }

        private static void ValidateNeighbors(
            DungeonRecipeAsset recipe,
            DungeonRecipeValidationResult result)
        {
            const DungeonRecipeValidationLayer Layer = DungeonRecipeValidationLayer.Neighbor;
            result.MarkExecuted(Layer);
            if (recipe == null)
            {
                return;
            }

            bool incidentSockets = recipe.UsesIncidentCardinalSockets;
            var mandatoryOutward = new HashSet<Vector2Int>();
            foreach (DungeonRecipePort port in recipe.ports ?? Array.Empty<DungeonRecipePort>())
            {
                if (port == null || (!incidentSockets && !port.mandatory))
                {
                    continue;
                }

                if (port.type != DungeonRecipePortType.Corridor ||
                    !mandatoryOutward.Add(port.outwardDirection))
                {
                    string memberKind = incidentSockets ? "Socket" : "Mandatory port";
                    result.Add(Layer, "RECIPE_NEIGHBOR_PORT", $"{memberKind} '{port.id}' did not expose a distinct generic-corridor counterpart.");
                }
            }

            int requiredDirectionCount = incidentSockets ? 4 : 2;
            if (mandatoryOutward.Count < requiredDirectionCount)
            {
                result.Add(
                    Layer,
                    "RECIPE_NEIGHBOR_MATRIX",
                    incidentSockets
                        ? "Route-bound sockets did not cover all four neighbor approach directions."
                        : "Mandatory ports did not cover two distinct neighbor approach directions.");
            }
        }

        private static void ValidateFullDungeon(
            DungeonRecipeAsset recipe,
            DungeonRecipeFullDungeonEvidence evidence,
            DungeonRecipeValidationResult result)
        {
            const DungeonRecipeValidationLayer Layer = DungeonRecipeValidationLayer.FullDungeon;
            result.MarkExecuted(Layer);
            if (recipe == null)
            {
                return;
            }

            int requiredPorts = 0;
            foreach (DungeonRecipePort port in recipe.ports ?? Array.Empty<DungeonRecipePort>())
            {
                requiredPorts += port != null && port.mandatory ? 1 : 0;
            }

            bool portEvidenceValid = recipe.UsesIncidentCardinalSockets
                ? evidence.boundMandatoryPortCount >= recipe.minimumActiveSockets &&
                  evidence.boundMandatoryPortCount <= recipe.maximumActiveSockets
                : evidence.boundMandatoryPortCount == requiredPorts;
            if (!string.Equals(recipe.recipeId, evidence.recipeId, StringComparison.Ordinal) ||
                !evidence.placedAtomically ||
                !portEvidenceValid ||
                evidence.resolvedTransitionCount != (recipe.transitions?.Length ?? 0) ||
                !evidence.canonicalPlanValid ||
                !evidence.rendererValid ||
                !evidence.abyssSupportValid ||
                !evidence.collisionValid ||
                !evidence.forcedAuthoringPreview ||
                string.IsNullOrEmpty(evidence.previewTopologyId) ||
                string.IsNullOrEmpty(evidence.previewRecipeSlotId) ||
                string.IsNullOrEmpty(evidence.previewRouteNodeId))
            {
                result.Add(Layer, "RECIPE_FULL_DUNGEON", "The recipe lacked complete canonical-plan, renderer, abyss, collision, port, or transition evidence.");
            }
        }

        /// <summary>
        /// The rise a cell has WITHIN one layer: the max over Elevated zones
        /// covering it that belong to that layer.
        /// </summary>
        /// <remarks>
        /// The `max` was §8.2's "heightfield assumption inside the recipe
        /// schema", and scoping it by layer is the whole fix — a cell may now be
        /// covered by zones on two storeys, and taking the max across both would
        /// be the same collapse the level field itself just stopped doing.
        /// Within a layer the max stays: two Elevated zones overlapping on ONE
        /// storey still describe one surface.
        /// </remarks>
        private static int RelativeLevelAt(DungeonRecipeAsset recipe, Vector2Int cell, string layerId)
        {
            int level = 0;
            foreach (DungeonRecipeZone zone in recipe.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                if (zone != null &&
                    zone.kind == DungeonRecipeZoneKind.Elevated &&
                    SameLayer(recipe, zone.layerId, layerId) &&
                    Contains(zone, cell))
                {
                    level = Mathf.Max(level, zone.relativeLevel);
                }
            }

            return level;
        }

        /// <summary>
        /// A cell's level relative to the NODE: its layer's offset plus its rise
        /// within that layer. What a transition's `riseLevels` is measured in.
        /// </summary>
        private static int AbsoluteLevelAt(DungeonRecipeAsset recipe, Vector2Int cell, string layerId)
        {
            DungeonRecipeLayers.TryGetRelativeLevel(recipe, layerId, out int layerLevel);
            return layerLevel + RelativeLevelAt(recipe, cell, layerId);
        }

        /// <summary>
        /// Do two layer references name the same storey? Empty means the base,
        /// so `""` and an explicit base id are the same layer.
        /// </summary>
        private static bool SameLayer(DungeonRecipeAsset recipe, string first, string second)
        {
            if (string.Equals(first, second, StringComparison.Ordinal))
            {
                return true;
            }

            return DungeonRecipeLayers.IsBaseLayer(recipe, first) &&
                DungeonRecipeLayers.IsBaseLayer(recipe, second);
        }

        private static bool CellsHaveLevel(
            DungeonRecipeAsset recipe,
            IEnumerable<Vector2Int> cells,
            string layerId,
            int expected)
        {
            foreach (Vector2Int cell in cells)
            {
                if (RelativeLevelAt(recipe, cell, layerId) != expected)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The layer declarations themselves, plus every reference to one
        /// (design §8.2's `RECIPE_LAYER_CONNECTIVITY` and what it needs first).
        /// </summary>
        /// <remarks>
        /// Every check here is silent for a recipe that declares no layers,
        /// which is every recipe in the catalog today. The one thing that is
        /// NOT silent is a stray `layerId` on a recipe with no layer
        /// declarations — that is a typo, and reading it as "the base layer"
        /// would hide it.
        /// </remarks>
        private static void ValidateLayers(
            DungeonRecipeAsset recipe,
            DungeonRecipeValidationResult result)
        {
            const DungeonRecipeValidationLayer Layer = DungeonRecipeValidationLayer.Structure;
            var declared = new HashSet<string>(StringComparer.Ordinal);
            int baseCount = 0;
            foreach (DungeonRecipeLayer layer in recipe.layers ?? Array.Empty<DungeonRecipeLayer>())
            {
                if (layer == null || string.IsNullOrEmpty(layer.layerId))
                {
                    result.Add(Layer, "RECIPE_LAYER_ID", "A layer entry was null or carried no layerId.");
                    continue;
                }

                if (!declared.Add(layer.layerId))
                {
                    result.Add(Layer, "RECIPE_LAYER_ID", $"Layer '{layer.layerId}' was declared more than once.");
                }

                if (layer.isBase)
                {
                    baseCount++;
                    if (layer.relativeLevel != 0)
                    {
                        result.Add(
                            Layer,
                            "RECIPE_LAYER_BASE",
                            $"Base layer '{layer.layerId}' sat at {layer.relativeLevel}u. The base IS the node's level.");
                    }
                }
                else if (layer.relativeLevel == 0)
                {
                    result.Add(
                        Layer,
                        "RECIPE_LAYER_BASE",
                        $"Layer '{layer.layerId}' is not the base but sits at the base's level.");
                }
            }

            if (recipe.DeclaresLayers && baseCount != 1)
            {
                result.Add(
                    Layer,
                    "RECIPE_LAYER_BASE",
                    $"A layered recipe declares exactly one base layer; this one declared {baseCount}.");
            }

            void CheckReference(string layerId, string what)
            {
                if (!DungeonRecipeLayers.TryGetRelativeLevel(recipe, layerId, out _))
                {
                    result.Add(
                        Layer,
                        "RECIPE_LAYER_ID",
                        $"{what} named layer '{layerId}', which this recipe does not declare.");
                }
            }

            foreach (DungeonRecipeZone zone in recipe.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                if (zone != null)
                {
                    CheckReference(zone.layerId, $"Zone '{zone.id}'");
                }
            }

            foreach (DungeonRecipePort port in recipe.ports ?? Array.Empty<DungeonRecipePort>())
            {
                if (port != null)
                {
                    CheckReference(port.layerId, $"Port '{port.id}'");
                }
            }

            foreach (DungeonRecipeTransition transition in
                     recipe.transitions ?? Array.Empty<DungeonRecipeTransition>())
            {
                if (transition != null)
                {
                    CheckReference(transition.lowerLayerId, $"Transition '{transition.id}' lower end");
                    CheckReference(transition.upperLayerId, $"Transition '{transition.id}' upper end");
                }
            }

            ValidateOpenings(recipe, declared, result);
            ValidateLayerConnectivity(recipe, declared, result);
        }

        /// <summary>
        /// `RECIPE_OPENING`: an authored bare rim must name a real stacked
        /// surface and point at a real hole.
        /// </summary>
        /// <remarks>
        /// The pair of tests is the whole content. A rim whose cell is not on
        /// its layer guards nothing, and a rim pointing at another cell of the
        /// same storey declares an aperture where the floor continues — which
        /// the renderer would happily honour, leaving a bare edge in the middle
        /// of a gallery and no hole to fall through.
        /// </remarks>
        private static void ValidateOpenings(
            DungeonRecipeAsset recipe,
            HashSet<string> declared,
            DungeonRecipeValidationResult result)
        {
            const DungeonRecipeValidationLayer Layer = DungeonRecipeValidationLayer.Structure;
            if (!recipe.DeclaresOpenings)
            {
                return;
            }

            if (!recipe.DeclaresLayers)
            {
                result.Add(
                    Layer,
                    "RECIPE_OPENING",
                    "A recipe declared an opening without declaring the storey it belongs to. A bare rim on the entry storey is exterior void, not an aperture.");
                return;
            }

            Dictionary<string, HashSet<Vector2Int>> footprints =
                BuildLayerFootprints(recipe, declared);
            foreach (DungeonRecipeOpening opening in recipe.openings)
            {
                if (opening == null)
                {
                    result.Add(Layer, "RECIPE_OPENING", "An opening entry was null.");
                    continue;
                }

                if (!Enum.IsDefined(typeof(OpeningKind), opening.kind))
                {
                    result.Add(
                        Layer,
                        "RECIPE_OPENING",
                        $"Opening '{opening.id}' declared unknown kind {(int)opening.kind}.");
                    continue;
                }

                if (string.IsNullOrEmpty(opening.layerId) ||
                    !declared.Contains(opening.layerId) ||
                    (opening.kind == OpeningKind.Aperture &&
                     DungeonRecipeLayers.IsBaseLayer(recipe, opening.layerId)))
                {
                    result.Add(
                        Layer,
                        "RECIPE_OPENING",
                        opening.kind == OpeningKind.Aperture
                            ? $"Opening '{opening.id}' named layer '{opening.layerId}', which is not a declared non-base storey."
                            : $"Opening '{opening.id}' named undeclared layer '{opening.layerId}'.");
                    continue;
                }

                HashSet<Vector2Int> layerCells = footprints[opening.layerId];
                if (!IsCardinalUnit(opening.outwardDirection) ||
                    !layerCells.Contains(opening.cell) ||
                    layerCells.Contains(opening.cell + opening.outwardDirection))
                {
                    result.Add(
                        Layer,
                        "RECIPE_OPENING",
                        $"Opening '{opening.id}' did not declare a cardinal rim standing on layer '{opening.layerId}' and facing a cell that storey does not cover.");
                }
            }
        }

        /// <summary>
        /// Every cell a layer's walkable footprint covers, keyed by layer id.
        /// </summary>
        private static Dictionary<string, HashSet<Vector2Int>> BuildLayerFootprints(
            DungeonRecipeAsset recipe,
            HashSet<string> declared)
        {
            var byLayer = new Dictionary<string, HashSet<Vector2Int>>(StringComparer.Ordinal);
            foreach (string layerId in declared)
            {
                byLayer[layerId] = new HashSet<Vector2Int>();
            }

            foreach (DungeonRecipeZone zone in recipe.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                if (zone == null ||
                    zone.kind != DungeonRecipeZoneKind.Walkable &&
                    zone.kind != DungeonRecipeZoneKind.Elevated)
                {
                    continue;
                }

                foreach (string layerId in declared)
                {
                    if (SameLayer(recipe, zone.layerId, layerId))
                    {
                        byLayer[layerId].UnionWith(Cells(zone));
                    }
                }
            }

            return byLayer;
        }

        /// <summary>
        /// `RECIPE_LAYER_CONNECTIVITY` (design §8.2): every declared layer must
        /// be reachable from the base — over the recipe's own transitions, or
        /// across a flush lateral seam.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Undirected on purpose. A stair is walkable both ways, and the
        /// directed question — can you get back up — is the fall-free
        /// connectivity invariant, which lives on the plan's traversal graph and
        /// not inside one recipe's declaration.
        /// </para>
        /// <para>
        /// **A transition is not the only way two storeys join, and §8.2 assumed
        /// it was.** Two surfaces that are cardinally adjacent at the SAME
        /// absolute level are one walkable place — that is exactly what C1's
        /// flush-seam ruling says, and the episode it proved joins its gallery to
        /// a ground-backed terrace that way. Demanding a stair there is not just
        /// stricter than the plan's own traversal graph, it demands geometry that
        /// cannot be built: a 1u strip from a ground column onto a suspended slab
        /// spans a face with the whole storey gap open underneath it.
        /// </para>
        /// </remarks>
        private static void ValidateLayerConnectivity(
            DungeonRecipeAsset recipe,
            HashSet<string> declared,
            DungeonRecipeValidationResult result)
        {
            if (!recipe.DeclaresLayers || declared.Count <= 1)
            {
                return;
            }

            string baseLayerId = null;
            foreach (DungeonRecipeLayer layer in recipe.layers)
            {
                if (layer != null && layer.isBase && !string.IsNullOrEmpty(layer.layerId))
                {
                    baseLayerId = layer.layerId;
                    break;
                }
            }

            if (baseLayerId == null)
            {
                // Already reported as RECIPE_LAYER_BASE; reachability from an
                // unknown root would only add noise.
                return;
            }

            var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void Link(string first, string second)
            {
                if (!adjacency.TryGetValue(first, out List<string> neighbours))
                {
                    neighbours = new List<string>();
                    adjacency[first] = neighbours;
                }

                neighbours.Add(second);
            }

            foreach (DungeonRecipeTransition transition in
                     recipe.transitions ?? Array.Empty<DungeonRecipeTransition>())
            {
                if (transition == null)
                {
                    continue;
                }

                string lower = string.IsNullOrEmpty(transition.lowerLayerId)
                    ? baseLayerId
                    : transition.lowerLayerId;
                string upper = string.IsNullOrEmpty(transition.upperLayerId)
                    ? baseLayerId
                    : transition.upperLayerId;
                if (!declared.Contains(lower) || !declared.Contains(upper))
                {
                    continue;
                }

                Link(lower, upper);
                Link(upper, lower);
            }

            // The flush lateral seam. Two storeys touch when one has a cell
            // cardinally beside a cell of the other AND both stand at the same
            // absolute level — the same condition the renderer's flush seam uses
            // to drop its guard, stated over the recipe's own declaration.
            Dictionary<string, HashSet<Vector2Int>> footprints =
                BuildLayerFootprints(recipe, declared);
            var layerIds = new List<string>(declared);
            layerIds.Sort(StringComparer.Ordinal);
            for (int first = 0; first < layerIds.Count; first++)
            {
                for (int second = first + 1; second < layerIds.Count; second++)
                {
                    if (LayersMeetFlush(
                            recipe,
                            layerIds[first],
                            footprints[layerIds[first]],
                            layerIds[second],
                            footprints[layerIds[second]]))
                    {
                        Link(layerIds[first], layerIds[second]);
                        Link(layerIds[second], layerIds[first]);
                    }
                }
            }

            var reached = new HashSet<string>(StringComparer.Ordinal) { baseLayerId };
            var pending = new Stack<string>();
            pending.Push(baseLayerId);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if (!adjacency.TryGetValue(current, out List<string> neighbours))
                {
                    continue;
                }

                foreach (string neighbour in neighbours)
                {
                    if (reached.Add(neighbour))
                    {
                        pending.Push(neighbour);
                    }
                }
            }

            var stranded = new List<string>();
            foreach (string layerId in declared)
            {
                if (!reached.Contains(layerId))
                {
                    stranded.Add(layerId);
                }
            }

            if (stranded.Count > 0)
            {
                stranded.Sort(StringComparer.Ordinal);
                result.Add(
                    DungeonRecipeValidationLayer.Structure,
                    "RECIPE_LAYER_CONNECTIVITY",
                    $"Layer(s) '{string.Join("', '", stranded)}' reached the base layer '{baseLayerId}' by neither a transition nor a flush lateral seam.");
            }
        }

        /// <summary>
        /// Do two storeys share a walkable edge — adjacent cells at one level?
        /// </summary>
        private static bool LayersMeetFlush(
            DungeonRecipeAsset recipe,
            string firstLayerId,
            HashSet<Vector2Int> firstCells,
            string secondLayerId,
            HashSet<Vector2Int> secondCells)
        {
            foreach (Vector2Int cell in firstCells)
            {
                int level = AbsoluteLevelAt(recipe, cell, firstLayerId);
                foreach (Vector2Int step in CardinalSteps)
                {
                    Vector2Int neighbour = cell + step;
                    if (secondCells.Contains(neighbour) &&
                        AbsoluteLevelAt(recipe, neighbour, secondLayerId) == level)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static readonly Vector2Int[] CardinalSteps =
        {
            Vector2Int.up,
            Vector2Int.right,
            Vector2Int.down,
            Vector2Int.left
        };

        private static bool CellsInside(HashSet<Vector2Int> footprint, IEnumerable<Vector2Int> cells)
        {
            foreach (Vector2Int cell in cells)
            {
                if (!footprint.Contains(cell))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AllPortsShareConnectedFootprint(
            IReadOnlyList<DungeonRecipePort> ports,
            HashSet<Vector2Int> footprint)
        {
            if (ports == null || ports.Count == 0 || ports[0] == null ||
                !footprint.Contains(ports[0].cell))
            {
                return false;
            }

            var visited = new HashSet<Vector2Int> { ports[0].cell };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(ports[0].cell);
            Vector2Int[] directions =
            {
                Vector2Int.up,
                Vector2Int.right,
                Vector2Int.down,
                Vector2Int.left
            };
            while (queue.Count > 0)
            {
                Vector2Int current = queue.Dequeue();
                foreach (Vector2Int direction in directions)
                {
                    Vector2Int neighbor = current + direction;
                    if (footprint.Contains(neighbor) && visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            foreach (DungeonRecipePort port in ports)
            {
                if (port == null || !visited.Contains(port.cell))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ZonesMirrorAcrossPrimaryAxis(
            DungeonRecipeZone first,
            DungeonRecipeZone second)
        {
            var secondCells = new HashSet<Vector2Int>(Cells(second));
            foreach (Vector2Int cell in Cells(first))
            {
                if (!secondCells.Contains(new Vector2Int(cell.x, -cell.y)))
                {
                    return false;
                }
            }

            return secondCells.Count == first.size.x * first.size.y;
        }

        private static IEnumerable<Vector2Int> Cells(DungeonRecipeZone zone)
        {
            if (zone == null)
            {
                yield break;
            }

            for (int x = zone.offset.x; x < zone.offset.x + zone.size.x; x++)
            {
                for (int y = zone.offset.y; y < zone.offset.y + zone.size.y; y++)
                {
                    yield return new Vector2Int(x, y);
                }
            }
        }

        private static void AddRectCells(HashSet<Vector2Int> cells, DungeonRecipeZone zone)
        {
            foreach (Vector2Int cell in Cells(zone))
            {
                cells.Add(cell);
            }
        }

        private static bool Contains(DungeonRecipeZone zone, Vector2Int cell)
        {
            return cell.x >= zone.offset.x && cell.x < zone.offset.x + zone.size.x &&
                cell.y >= zone.offset.y && cell.y < zone.offset.y + zone.size.y;
        }

        private static bool IsCardinalUnit(Vector2Int value)
        {
            return Mathf.Abs(value.x) + Mathf.Abs(value.y) == 1;
        }

        private static bool IsStableId(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (char character in value)
            {
                if (!(character >= 'a' && character <= 'z') &&
                    !(character >= '0' && character <= '9') &&
                    character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static void CheckIds<T>(
            IEnumerable<T> values,
            Func<T, string> idSelector,
            string label,
            DungeonRecipeValidationLayer layer,
            DungeonRecipeValidationResult result)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (T value in values ?? Array.Empty<T>())
            {
                string id = idSelector(value);
                if (!IsStableId(id) || !ids.Add(id))
                {
                    result.Add(layer, "RECIPE_MEMBER_ID", $"{label} ID '{id}' was missing, invalid, or duplicated.");
                }
            }
        }

        private static void Append(StringBuilder builder, string key, string value)
        {
            builder.Append(key).Append('=').Append(value ?? string.Empty).Append('\n');
        }

        private static void Append(StringBuilder builder, string key, int value)
        {
            builder.Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        private static void Append(StringBuilder builder, string key, Vector2Int value)
        {
            builder.Append(key).Append('=').Append(value.x).Append(',').Append(value.y).Append('\n');
        }

        private static void AppendStrings(StringBuilder builder, string key, IEnumerable<string> values)
        {
            foreach (string value in values ?? Array.Empty<string>())
            {
                Append(builder, key, value);
            }
        }

        private static void AppendInts(StringBuilder builder, string key, IEnumerable<int> values)
        {
            foreach (int value in values ?? Array.Empty<int>())
            {
                Append(builder, key, value);
            }
        }

        private static void AppendVectors(StringBuilder builder, string key, IEnumerable<Vector2Int> values)
        {
            foreach (Vector2Int value in values ?? Array.Empty<Vector2Int>())
            {
                Append(builder, key, value);
            }
        }
    }

}

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

        public DungeonRecipeFullDungeonEvidence(
            string recipeId,
            bool placedAtomically,
            int boundMandatoryPortCount,
            int resolvedTransitionCount,
            bool canonicalPlanValid,
            bool rendererValid,
            bool abyssSupportValid,
            bool collisionValid)
        {
            this.recipeId = recipeId ?? string.Empty;
            this.placedAtomically = placedAtomically;
            this.boundMandatoryPortCount = boundMandatoryPortCount;
            this.resolvedTransitionCount = resolvedTransitionCount;
            this.canonicalPlanValid = canonicalPlanValid;
            this.rendererValid = rendererValid;
            this.abyssSupportValid = abyssSupportValid;
            this.collisionValid = collisionValid;
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

            foreach (DungeonRecipeZone zone in recipe.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                Append(canonical, "zone.id", zone?.id);
                Append(canonical, "zone.kind", zone == null ? -1 : (int)zone.kind);
                Append(canonical, "zone.offset", zone?.offset ?? default);
                Append(canonical, "zone.size", zone?.size ?? default);
                Append(canonical, "zone.level", zone?.relativeLevel ?? 0);
            }

            foreach (DungeonRecipePort port in recipe.ports ?? Array.Empty<DungeonRecipePort>())
            {
                Append(canonical, "port.id", port?.id);
                Append(canonical, "port.type", port == null ? -1 : (int)port.type);
                Append(canonical, "port.mandatory", port != null && port.mandatory ? 1 : 0);
                Append(canonical, "port.cell", port?.cell ?? default);
                Append(canonical, "port.out", port?.outwardDirection ?? default);
                Append(canonical, "port.level", port?.relativeLevel ?? 0);
                Append(canonical, "port.width", port?.widthCells ?? 0);
                Append(canonical, "port.approach", port?.approachDepthCells ?? 0);
                Append(canonical, "port.headroom", port?.headroomLevels ?? 0);
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
                    RelativeLevelAt(recipe, port.cell) != port.relativeLevel)
                {
                    result.Add(Layer, "RECIPE_PORT_GEOMETRY", $"Port '{port.id}' did not declare an exact open boundary, level, approach, and headroom contract.");
                }
            }

            if (mandatoryPortCount < 2)
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
                if (transition == null ||
                    !motifsById.TryGetValue(transition?.motifId ?? string.Empty, out DungeonRecipeMotif motif) ||
                    motif.kind != DungeonRecipeMotifKind.StairTransition ||
                    transition.riseLevels != 1 ||
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

                bool cellsValid = footprint.Contains(transition.lowerTransitionCell) &&
                    footprint.Contains(transition.upperTransitionCell) &&
                    RelativeLevelAt(recipe, transition.upperTransitionCell) -
                    RelativeLevelAt(recipe, transition.lowerTransitionCell) == transition.riseLevels;
                cellsValid &= CellsHaveLevel(recipe, transition.lowerLandingCells, RelativeLevelAt(recipe, transition.lowerTransitionCell));
                cellsValid &= CellsHaveLevel(recipe, transition.upperLandingCells, RelativeLevelAt(recipe, transition.upperTransitionCell));
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

            var mandatoryOutward = new HashSet<Vector2Int>();
            foreach (DungeonRecipePort port in recipe.ports ?? Array.Empty<DungeonRecipePort>())
            {
                if (port == null || !port.mandatory)
                {
                    continue;
                }

                if (port.type != DungeonRecipePortType.Corridor ||
                    !mandatoryOutward.Add(port.outwardDirection))
                {
                    result.Add(Layer, "RECIPE_NEIGHBOR_PORT", $"Mandatory port '{port.id}' did not expose a distinct generic-corridor counterpart.");
                }
            }

            if (mandatoryOutward.Count < 2)
            {
                result.Add(Layer, "RECIPE_NEIGHBOR_MATRIX", "Mandatory ports did not cover two distinct neighbor approach directions.");
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

            if (!string.Equals(recipe.recipeId, evidence.recipeId, StringComparison.Ordinal) ||
                !evidence.placedAtomically ||
                evidence.boundMandatoryPortCount != requiredPorts ||
                evidence.resolvedTransitionCount != (recipe.transitions?.Length ?? 0) ||
                !evidence.canonicalPlanValid ||
                !evidence.rendererValid ||
                !evidence.abyssSupportValid ||
                !evidence.collisionValid)
            {
                result.Add(Layer, "RECIPE_FULL_DUNGEON", "The recipe lacked complete canonical-plan, renderer, abyss, collision, port, or transition evidence.");
            }
        }

        private static int RelativeLevelAt(DungeonRecipeAsset recipe, Vector2Int cell)
        {
            int level = 0;
            foreach (DungeonRecipeZone zone in recipe.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                if (zone != null && zone.kind == DungeonRecipeZoneKind.Elevated && Contains(zone, cell))
                {
                    level = Mathf.Max(level, zone.relativeLevel);
                }
            }

            return level;
        }

        private static bool CellsHaveLevel(
            DungeonRecipeAsset recipe,
            IEnumerable<Vector2Int> cells,
            int expected)
        {
            foreach (Vector2Int cell in cells)
            {
                if (RelativeLevelAt(recipe, cell) != expected)
                {
                    return false;
                }
            }

            return true;
        }

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

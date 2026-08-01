using System;
using UnityEngine;

namespace DungeonLab.Editor
{
    public enum DungeonRecipeKind
    {
        Connector,
        Episode
    }

    public enum DungeonRecipeZoneKind
    {
        Walkable,
        Elevated,
        ProtectedCirculation,
        ProtectedFocal,
        // APPENDED, never inserted: these are serialized as integers in every
        // recipe asset, so reordering the enum silently re-kinds authored zones.
        OpenVolume
    }

    public enum DungeonRecipePortType
    {
        Corridor
    }

    public enum DungeonRecipePortBindingMode
    {
        ExactNamedPorts,
        IncidentCardinalSockets
    }

    public enum DungeonRecipeMotifKind
    {
        StairTransition,
        FocalVisual
    }

    /// <summary>
    /// One walkable storey of a recipe (layered-topology design §8.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A recipe that declares no layers has exactly one, implicit, at the node's
    /// own level — which is what every recipe in the catalog is today, and why
    /// the whole layer schema defaults to today's behaviour.
    /// </para>
    /// <para>
    /// <see cref="relativeLevel"/> is relative to the NODE's level, and the base
    /// layer's is therefore always 0. §8.2 originally derived the base from
    /// "port zero" and proposed an `anchorLayerId`/`anchorLevel` escape hatch for
    /// a base layer with no external port; both dissolved on inspection — the
    /// base IS the node level whether or not a port sits on it.
    /// </para>
    /// <para>
    /// Negative levels are legal here, which is where §8.2's "Elevated zones must
    /// be allowed to go negative" belongs. A sunken chamber is a LAYER below the
    /// base, not a zone with a negative rise, so `RECIPE_ZONE_LEVEL` keeps its
    /// existing rule — an Elevated zone still rises within its own layer.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class DungeonRecipeLayer
    {
        [Tooltip("Stable identifier zones and ports name to join this layer. Must be non-empty and unique.")]
        public string layerId = string.Empty;
        [Tooltip("Levels above (or below) the node's own level. The base layer is 0 by definition.")]
        public int relativeLevel;
        [Tooltip("Exactly one layer is the base. It carries the recipe's entry level and must sit at relativeLevel 0.")]
        public bool isBase;
    }

    [Serializable]
    public sealed class DungeonRecipeZone
    {
        public string id = string.Empty;
        public DungeonRecipeZoneKind kind;
        public Vector2Int offset;
        public Vector2Int size = Vector2Int.one;
        public int relativeLevel;
        [Tooltip("Which declared layer this zone belongs to. Empty means the base layer, which is the only layer most recipes have.")]
        public string layerId = string.Empty;
        [Tooltip("OpenVolume only: how many levels of void this zone reserves above its layer. The band is half-open, so 8 reserves [layer, layer+8).")]
        public int openVolumeHeightLevels;
    }

    [Serializable]
    public sealed class DungeonRecipePort
    {
        public string id = string.Empty;
        public DungeonRecipePortType type;
        public bool mandatory = true;
        public Vector2Int cell;
        public Vector2Int outwardDirection;
        public int relativeLevel;
        public int widthCells = 1;
        public int approachDepthCells = 1;
        public int headroomLevels = 3;
        [Tooltip("Which declared layer this entrance opens onto. Empty means the base layer.")]
        public string layerId = string.Empty;
    }

    /// <summary>
    /// One bare rim on a stacked storey: the edge of an authored aperture
    /// (layered-topology design §5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Guarding is per (rim surface, direction), so an aperture railed on three
    /// sides and open on one is expressible; this declares the OPEN ones. The
    /// cell is the surface you stand on and <see cref="outwardDirection"/> points
    /// at the hole, which is the same shape as the renderer's
    /// <c>OpenFloorEdge</c> — the aperture cell itself declares nothing, because
    /// it is not a surface.
    /// </para>
    /// <para>
    /// Restricted to a declared non-base layer on purpose. A bare rim on the
    /// entry storey is a hole in the ground with nothing under it, which is the
    /// exterior-void case the external connectors already own — not an aperture.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class DungeonRecipeOpening
    {
        public string id = string.Empty;
        [Tooltip("The surface cell whose rim is bare. It must belong to this opening's layer.")]
        public Vector2Int cell;
        [Tooltip("Cardinal direction from that cell toward the hole. The cell it points at must NOT be on this layer.")]
        public Vector2Int outwardDirection;
        [Tooltip("Which declared storey this rim belongs to. Must name a non-base layer.")]
        public string layerId = string.Empty;
    }

    [Serializable]
    public sealed class DungeonRecipeMotif
    {
        public string id = string.Empty;
        public DungeonRecipeMotifKind kind;
        public string implementationId = string.Empty;
    }

    [Serializable]
    public sealed class DungeonRecipeTransition
    {
        public string id = string.Empty;
        public string atomicGroupId = string.Empty;
        public string motifId = string.Empty;
        public Vector2Int lowerTransitionCell;
        public Vector2Int upperTransitionCell;
        public Vector2Int[] lowerLandingCells = Array.Empty<Vector2Int>();
        public Vector2Int[] upperLandingCells = Array.Empty<Vector2Int>();
        public Vector2Int[] footprintCells = Array.Empty<Vector2Int>();
        public Vector2Int climbDirection;
        public int riseLevels = 1;
        public int laneCount = 1;
        public int headroomLevels = 3;
        [Tooltip("Which declared layer the lower end stands on. Empty means the base layer.")]
        public string lowerLayerId = string.Empty;
        [Tooltip("Which declared layer the upper end reaches. Empty means the base layer. A stair between two layers is what makes them connected.")]
        public string upperLayerId = string.Empty;
    }

    [Serializable]
    public sealed class DungeonRecipeSymmetryPair
    {
        public string id = string.Empty;
        public string firstZoneId = string.Empty;
        public string secondZoneId = string.Empty;
    }

    [Serializable]
    public sealed class DungeonRecipeVariation
    {
        public string id = string.Empty;
        public string motifId = string.Empty;
        public int weight = 1;
    }

    [CreateAssetMenu(fileName = "dungeon_recipe", menuName = "Dungeon Lab/Recipe")]
    public sealed class DungeonRecipeAsset : ScriptableObject
    {
        public const int CurrentSchemaVersion = 1;

        [Tooltip("Stable lowercase identifier used by catalogs, deterministic selection, reports, and generated hierarchy names.")]
        public string recipeId = string.Empty;
        [Tooltip("Human-readable recipe name shown in authoring and review tools.")]
        public string displayName = string.Empty;
        [Tooltip("Connector recipes describe traversal rooms; Episode recipes describe larger atomic compositions.")]
        public DungeonRecipeKind kind;
        [Tooltip("Serialized recipe schema version. This must match the version supported by the current generator.")]
        public int schemaVersion = CurrentSchemaVersion;
        [Tooltip("Owner-maintained revision number for authored content changes that keep the same stable recipe ID.")]
        public int contentVersion = 1;
        [Tooltip("Excludes this recipe from ordinary catalog generation while still allowing explicit authoring previews.")]
        public bool disabledForGeneration = true;
        [Tooltip("Route node roles for which this recipe may be selected, such as connector.")]
        public string[] eligibleRoles = Array.Empty<string>();
        [Tooltip("Journey beats for which this recipe may be selected, such as return or compression.")]
        public string[] eligibleBeats = Array.Empty<string>();
        [Tooltip("Exact named ports preserve the existing slot contract. Incident cardinal sockets activate only the declared sides that have route neighbors.")]
        public DungeonRecipePortBindingMode portBindingMode =
            DungeonRecipePortBindingMode.ExactNamedPorts;
        [Tooltip("Minimum number of active route-bound sockets. Ignored for exact named ports.")]
        [Range(1, 4)] public int minimumActiveSockets = 1;
        [Tooltip("Maximum number of active route-bound sockets. Ignored for exact named ports.")]
        [Range(1, 4)] public int maximumActiveSockets = 4;
        [Tooltip("Allows the generator to reflect the recipe across its route-forward axis when matching neighbors.")]
        public bool allowMirror;
        [Tooltip("Allowed clockwise quarter-turns after the recipe's route-forward axis is resolved.")]
        public int[] legalQuarterTurns = { 0, 1, 2, 3 };
        [Tooltip("Walkable storeys this recipe stacks over one plan footprint. Empty means one implicit base layer, which is every recipe today.")]
        public DungeonRecipeLayer[] layers = Array.Empty<DungeonRecipeLayer>();
        [Tooltip("Walkable, elevated, and protected spatial regions declared on the recipe-local cell grid.")]
        public DungeonRecipeZone[] zones = Array.Empty<DungeonRecipeZone>();
        [Tooltip("Typed corridor connections bound to route edges when the recipe is placed.")]
        public DungeonRecipePort[] ports = Array.Empty<DungeonRecipePort>();
        [Tooltip("Bare rims on a stacked storey — the edges of an authored aperture you can walk off.")]
        public DungeonRecipeOpening[] openings = Array.Empty<DungeonRecipeOpening>();
        [Tooltip("Named stair or focal visual implementations used by transitions and weighted variations.")]
        public DungeonRecipeMotif[] motifs = Array.Empty<DungeonRecipeMotif>();
        [Tooltip("Atomic elevation changes with exact cells, landings, footprint, rise, lane, and headroom requirements.")]
        public DungeonRecipeTransition[] transitions = Array.Empty<DungeonRecipeTransition>();
        [Tooltip("Pairs of zones that must remain mirrored across the recipe's primary route axis.")]
        public DungeonRecipeSymmetryPair[] symmetryPairs = Array.Empty<DungeonRecipeSymmetryPair>();
        [Tooltip("Weighted visual alternatives that preserve this recipe's structural contract.")]
        public DungeonRecipeVariation[] variations = Array.Empty<DungeonRecipeVariation>();

        public bool UsesIncidentCardinalSockets =>
            portBindingMode == DungeonRecipePortBindingMode.IncidentCardinalSockets;

        /// <summary>
        /// True when this recipe stacks storeys. False for every recipe in the
        /// catalog today, and the switch every layer rule defaults through.
        /// </summary>
        public bool DeclaresLayers => layers != null && layers.Length > 0;

        /// <summary>
        /// True when this recipe authors a bare rim. False for every recipe in
        /// the catalog today, and what keeps the opening fields out of the
        /// canonical string — an unconditional append there moves every seed's
        /// <c>hashes.canonical</c> for a schema addition that changed no
        /// geometry (C2a's lesson, and the same conditional the layer fields use).
        /// </summary>
        public bool DeclaresOpenings => openings != null && openings.Length > 0;
    }

    /// <summary>
    /// Resolving a zone's or port's layer to a level offset — the one place that
    /// mapping lives, so the validator and the generator cannot disagree about it.
    /// </summary>
    public static class DungeonRecipeLayers
    {
        /// <summary>
        /// The level a layer sits at, relative to the node's own level.
        /// </summary>
        /// <remarks>
        /// An empty <paramref name="layerId"/> means the base layer, whether or
        /// not the recipe declares any — so adding a second layer to an existing
        /// recipe does not require re-tagging the zones that were already there.
        /// </remarks>
        public static bool TryGetRelativeLevel(
            DungeonRecipeAsset recipe,
            string layerId,
            out int relativeLevel)
        {
            relativeLevel = 0;
            if (recipe == null)
            {
                return false;
            }

            bool unnamed = string.IsNullOrEmpty(layerId);
            if (!recipe.DeclaresLayers)
            {
                // One implicit base layer at the node's level. A name that
                // resolves to nothing is a schema error, not a silent zero.
                return unnamed;
            }

            foreach (DungeonRecipeLayer layer in recipe.layers)
            {
                if (layer == null)
                {
                    continue;
                }

                if (unnamed ? layer.isBase : string.Equals(layer.layerId, layerId, StringComparison.Ordinal))
                {
                    relativeLevel = layer.relativeLevel;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The comparable identity of a layer reference: one string per storey,
        /// so two references can be tested with ordinal equality.
        /// </summary>
        /// <remarks>
        /// Needed because a storey has TWO spellings — the empty string and the
        /// base layer's declared id both name the entry storey — so a raw
        /// <c>string.Equals</c> splits one layer in two. The validator states the
        /// same rule as a two-argument predicate (<c>SameLayer</c>); this is the
        /// one-argument form, which is what a placement can store.
        /// <para>
        /// Deliberately NOT a level. Two storeys may not share a
        /// <c>relativeLevel</c> in any sane recipe, but "no sane recipe does
        /// that" is not an identity, and the generator's placements had only the
        /// level to compare with before this existed.
        /// </para>
        /// </remarks>
        public static string CanonicalId(DungeonRecipeAsset recipe, string layerId)
        {
            return IsBaseLayer(recipe, layerId) ? string.Empty : layerId;
        }

        /// <summary>Is this the recipe's entry storey?</summary>
        public static bool IsBaseLayer(DungeonRecipeAsset recipe, string layerId)
        {
            if (recipe == null || !recipe.DeclaresLayers || string.IsNullOrEmpty(layerId))
            {
                return true;
            }

            foreach (DungeonRecipeLayer layer in recipe.layers)
            {
                if (layer != null &&
                    string.Equals(layer.layerId, layerId, StringComparison.Ordinal))
                {
                    return layer.isBase;
                }
            }

            return false;
        }
    }
}

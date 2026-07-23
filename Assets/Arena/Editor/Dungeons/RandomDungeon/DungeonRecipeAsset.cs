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
        ProtectedFocal
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

    [Serializable]
    public sealed class DungeonRecipeZone
    {
        public string id = string.Empty;
        public DungeonRecipeZoneKind kind;
        public Vector2Int offset;
        public Vector2Int size = Vector2Int.one;
        public int relativeLevel;
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
        [Tooltip("Walkable, elevated, and protected spatial regions declared on the recipe-local cell grid.")]
        public DungeonRecipeZone[] zones = Array.Empty<DungeonRecipeZone>();
        [Tooltip("Typed corridor connections bound to route edges when the recipe is placed.")]
        public DungeonRecipePort[] ports = Array.Empty<DungeonRecipePort>();
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
    }
}

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

        public string recipeId = string.Empty;
        public string displayName = string.Empty;
        public DungeonRecipeKind kind;
        public int schemaVersion = CurrentSchemaVersion;
        public int contentVersion = 1;
        public bool disabledForGeneration = true;
        public string[] eligibleRoles = Array.Empty<string>();
        public string[] eligibleBeats = Array.Empty<string>();
        public bool allowMirror;
        public int[] legalQuarterTurns = { 0, 1, 2, 3 };
        public DungeonRecipeZone[] zones = Array.Empty<DungeonRecipeZone>();
        public DungeonRecipePort[] ports = Array.Empty<DungeonRecipePort>();
        public DungeonRecipeMotif[] motifs = Array.Empty<DungeonRecipeMotif>();
        public DungeonRecipeTransition[] transitions = Array.Empty<DungeonRecipeTransition>();
        public DungeonRecipeSymmetryPair[] symmetryPairs = Array.Empty<DungeonRecipeSymmetryPair>();
        public DungeonRecipeVariation[] variations = Array.Empty<DungeonRecipeVariation>();
    }
}

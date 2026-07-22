using UnityEngine;

namespace DungeonLab.Editor
{
    [System.Serializable]
    public struct DungeonRoomSizeRange
    {
        [Min(3)] public int minWidthCells;
        [Min(3)] public int maxWidthCells;
        [Min(3)] public int minDepthCells;
        [Min(3)] public int maxDepthCells;

        public DungeonRoomSizeRange(
            int minWidthCells,
            int maxWidthCells,
            int minDepthCells,
            int maxDepthCells)
        {
            this.minWidthCells = minWidthCells;
            this.maxWidthCells = maxWidthCells;
            this.minDepthCells = minDepthCells;
            this.maxDepthCells = maxDepthCells;
        }

        internal DungeonRoomSizeRange Validated()
        {
            var value = this;
            value.minWidthCells = Mathf.Max(3, value.minWidthCells);
            value.maxWidthCells = Mathf.Max(value.minWidthCells, value.maxWidthCells);
            value.minDepthCells = Mathf.Max(3, value.minDepthCells);
            value.maxDepthCells = Mathf.Max(value.minDepthCells, value.maxDepthCells);
            return value;
        }
    }

    [System.Serializable]
    public struct DungeonTierSeamAdjacencySettings
    {
        [Min(0)]
        [Tooltip("Exact number of declared non-traversal tier seams requested by this pattern.")]
        public int requestedCount;

        [Range(4, 8)]
        [Tooltip("Eligible tier-seam rises: 4 allows 4u seams; 8 allows both 4u and 8u seams.")]
        public int maximumRiseLevels;

        public DungeonTierSeamAdjacencySettings(int requestedCount, int maximumRiseLevels)
        {
            this.requestedCount = requestedCount;
            this.maximumRiseLevels = maximumRiseLevels;
        }

        internal DungeonTierSeamAdjacencySettings Validated()
        {
            var value = this;
            value.requestedCount = Mathf.Max(0, value.requestedCount);
            value.maximumRiseLevels = value.maximumRiseLevels >= 8 ? 8 : 4;
            return value;
        }
    }

    [System.Serializable]
    public struct DungeonPatternSpatialSettings
    {
        [Min(1)] public int horizontalPitchCells;
        [Min(1)] public int verticalPitchCells;
        [Min(4)] public int roomEnvelopeRadiusCells;
        [Min(0)] public int neighborBiasStrengthCells;
        public DungeonTierSeamAdjacencySettings tierSeamAdjacency;
        public DungeonRoomSizeRange terminalRoomSize;
        public DungeonRoomSizeRange hallRoomSize;
        public DungeonRoomSizeRange connectorRoomSize;

        public DungeonPatternSpatialSettings(
            int horizontalPitchCells,
            int verticalPitchCells,
            int roomEnvelopeRadiusCells,
            int neighborBiasStrengthCells,
            DungeonTierSeamAdjacencySettings tierSeamAdjacency,
            DungeonRoomSizeRange terminalRoomSize,
            DungeonRoomSizeRange hallRoomSize,
            DungeonRoomSizeRange connectorRoomSize)
        {
            this.horizontalPitchCells = horizontalPitchCells;
            this.verticalPitchCells = verticalPitchCells;
            this.roomEnvelopeRadiusCells = roomEnvelopeRadiusCells;
            this.neighborBiasStrengthCells = neighborBiasStrengthCells;
            this.tierSeamAdjacency = tierSeamAdjacency;
            this.terminalRoomSize = terminalRoomSize;
            this.hallRoomSize = hallRoomSize;
            this.connectorRoomSize = connectorRoomSize;
        }

        internal DungeonPatternSpatialSettings Validated()
        {
            var value = this;
            value.horizontalPitchCells = Mathf.Max(1, value.horizontalPitchCells);
            value.verticalPitchCells = Mathf.Max(1, value.verticalPitchCells);
            // Every route pattern carries the reviewed landmark recipe, whose
            // authored footprint reaches four cells from its logical anchor.
            value.roomEnvelopeRadiusCells = Mathf.Max(4, value.roomEnvelopeRadiusCells);
            value.neighborBiasStrengthCells = Mathf.Max(0, value.neighborBiasStrengthCells);
            value.tierSeamAdjacency = value.tierSeamAdjacency.Validated();
            value.terminalRoomSize = value.terminalRoomSize.Validated();
            value.hallRoomSize = value.hallRoomSize.Validated();
            value.connectorRoomSize = value.connectorRoomSize.Validated();
            return value;
        }
    }

    [CreateAssetMenu(fileName = "generation_profile", menuName = "Dungeon Lab/Generation Profile")]
    public sealed class DungeonGenerationProfile : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Short label written into generation logs so profile-driven results can be compared across seeds.")]
        public string profileName = "default";

        [Header("Map Envelope")]
        [Min(12)]
        [Tooltip("Maximum plan width in 4u grid cells. Route embeddings that exceed it are rejected.")]
        public int mapWidthMaxCells = 28;

        [Min(12)]
        [Tooltip("Maximum plan depth in 4u grid cells. Route embeddings that exceed it are rejected.")]
        public int mapDepthMaxCells = 28;

        [Header("Density And Loops")]
        [Min(1)]
        [Tooltip("Minimum accepted room count. Layouts below this are rejected before rendering.")]
        public int denseFloorplanMinRooms = 9;

        [Range(0f, 1f)]
        [Tooltip("Minimum accepted floor-fill percentage inside the generated floor bounding box. Lower values allow more open, spread-out dungeons.")]
        public float denseFloorplanMinFillPercent = 0.34f;

        [Range(0f, 1f)]
        [Tooltip("Target loop-edge fraction relative to the room tree. Loops are still gated by level grammar and path validation.")]
        public float loopConnectionFraction = 0.35f;

        [Min(1)]
        [Tooltip("Maximum squared-distance candidate radius is derived from this room-center distance in grid cells. Larger values allow longer loop corridors.")]
        public int maxLoopCandidateDistanceCells = 14;

        [Header("Interior Features")]
        [Range(0f, 1f)]
        [Tooltip("Chance that an eligible room splits into lower and raised 1u zones. The seam still requires valid landings.")]
        public float roomZoneSplitChance = 0.35f;

        [Header("Processional Spatial Configuration")]
        [Tooltip("Processional X/Y pitch, independently validated room envelope, generic role-size ranges, and rise-0 neighbor bias. Reviewed recipe footprints remain authored.")]
        public DungeonPatternSpatialSettings processionalSpatial =
            new DungeonPatternSpatialSettings(
                9,
                9,
                4,
                0,
                new DungeonTierSeamAdjacencySettings(2, 8),
                new DungeonRoomSizeRange(5, 5, 7, 7),
                new DungeonRoomSizeRange(5, 5, 5, 6),
                new DungeonRoomSizeRange(4, 5, 5, 5));

        internal DungeonGenerationSettings ToSettings()
        {
            return new DungeonGenerationSettings
            {
                profileName = profileName,
                mapWidthMaxCells = mapWidthMaxCells,
                mapDepthMaxCells = mapDepthMaxCells,
                denseFloorplanMinRooms = denseFloorplanMinRooms,
                denseFloorplanMinFillPercent = denseFloorplanMinFillPercent,
                loopConnectionFraction = loopConnectionFraction,
                maxLoopCandidateDistanceCells = maxLoopCandidateDistanceCells,
                roomZoneSplitChance = roomZoneSplitChance,
                processionalSpatial = processionalSpatial
            }.Validated();
        }
    }

    internal struct DungeonGenerationSettings
    {
        public string profileName;
        public int mapWidthMaxCells;
        public int mapDepthMaxCells;
        public int denseFloorplanMinRooms;
        public float denseFloorplanMinFillPercent;
        public float loopConnectionFraction;
        public int maxLoopCandidateDistanceCells;
        public float roomZoneSplitChance;
        public DungeonPatternSpatialSettings processionalSpatial;

        public DungeonGenerationSettings Validated()
        {
            var value = this;
            value.profileName = string.IsNullOrWhiteSpace(value.profileName) ? "unnamed" : value.profileName.Trim();
            value.mapWidthMaxCells = Mathf.Max(12, value.mapWidthMaxCells);
            value.mapDepthMaxCells = Mathf.Max(12, value.mapDepthMaxCells);
            value.denseFloorplanMinRooms = Mathf.Max(1, value.denseFloorplanMinRooms);
            value.denseFloorplanMinFillPercent = Mathf.Clamp01(value.denseFloorplanMinFillPercent);
            value.loopConnectionFraction = Mathf.Clamp01(value.loopConnectionFraction);
            value.maxLoopCandidateDistanceCells = Mathf.Max(1, value.maxLoopCandidateDistanceCells);
            value.roomZoneSplitChance = Mathf.Clamp01(value.roomZoneSplitChance);
            value.processionalSpatial = value.processionalSpatial.Validated();
            return value;
        }
    }
}

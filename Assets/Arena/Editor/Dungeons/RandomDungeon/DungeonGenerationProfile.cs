using UnityEngine;

namespace DungeonLab.Editor
{
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

        [Range(0f, 1f)]
        [Tooltip("Chance per eligible unsplit room to try a dais. Failed dais placements are skipped, not repaired.")]
        public float daisChancePerRoom = 0.25f;

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
                daisChancePerRoom = daisChancePerRoom
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
        public float daisChancePerRoom;

        public static DungeonGenerationSettings Default => new DungeonGenerationSettings
        {
            profileName = "default",
            mapWidthMaxCells = 28,
            mapDepthMaxCells = 28,
            denseFloorplanMinRooms = 9,
            denseFloorplanMinFillPercent = 0.34f,
            loopConnectionFraction = 0.35f,
            maxLoopCandidateDistanceCells = 14,
            roomZoneSplitChance = 0.35f,
            daisChancePerRoom = 0.25f
        }.Validated();

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
            value.daisChancePerRoom = Mathf.Clamp01(value.daisChancePerRoom);
            return value;
        }
    }
}

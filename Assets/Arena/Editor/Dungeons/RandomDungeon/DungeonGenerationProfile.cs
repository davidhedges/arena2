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
        [Tooltip("Minimum plan width in 4u grid cells. Larger values give rooms and corridors more room to spread before validation.")]
        public int mapWidthMinCells = 24;

        [Min(12)]
        [Tooltip("Maximum plan width in 4u grid cells. This is a layout search boundary, not a floor prefab size.")]
        public int mapWidthMaxCells = 28;

        [Min(12)]
        [Tooltip("Minimum plan depth in 4u grid cells. Larger values allow longer room spacing and corridor runs.")]
        public int mapDepthMinCells = 24;

        [Min(12)]
        [Tooltip("Maximum plan depth in 4u grid cells. This controls available planning space before validation gates run.")]
        public int mapDepthMaxCells = 28;

        [Header("Room Areas")]
        [Min(4)]
        [Tooltip("Minimum hall area in grid cells. The hall is placed first and remains the largest intended anchor room.")]
        public int hallMinAreaCells = 36;

        [Min(4)]
        [Tooltip("Maximum hall area in grid cells. Raising this produces a broader main anchor without changing stair contracts.")]
        public int hallMaxAreaCells = 48;

        [Min(4)]
        [Tooltip("Minimum area for large rooms in grid cells. Promontory eligibility also uses this threshold.")]
        public int largeRoomMinAreaCells = 25;

        [Min(4)]
        [Tooltip("Maximum area for large rooms in grid cells. Larger values create more spacious secondary rooms.")]
        public int largeRoomMaxAreaCells = 36;

        [Min(4)]
        [Tooltip("Minimum area for mid rooms in grid cells.")]
        public int midRoomMinAreaCells = 12;

        [Min(4)]
        [Tooltip("Maximum area for mid rooms in grid cells.")]
        public int midRoomMaxAreaCells = 20;

        [Min(4)]
        [Tooltip("Minimum area for small rooms in grid cells.")]
        public int smallRoomMinAreaCells = 9;

        [Min(4)]
        [Tooltip("Maximum area for small rooms in grid cells. Lower this if the profile should de-emphasize small side rooms.")]
        public int smallRoomMaxAreaCells = 12;

        [Header("Room Counts")]
        [Min(0)]
        [Tooltip("Minimum number of large rooms after the hall. Minimum counts are always attempted before the floor budget can stop extras.")]
        public int largeRoomMinCount = 4;

        [Min(0)]
        [Tooltip("Maximum number of large rooms after the hall.")]
        public int largeRoomMaxCount = 6;

        [Min(0)]
        [Tooltip("Minimum number of mid rooms. Minimum counts are always attempted before the floor budget can stop extras.")]
        public int midRoomMinCount = 2;

        [Min(0)]
        [Tooltip("Maximum number of mid rooms.")]
        public int midRoomMaxCount = 5;

        [Min(0)]
        [Tooltip("Minimum number of small rooms. Set this low for a more spacious profile with fewer minor rooms.")]
        public int smallRoomMinCount = 2;

        [Min(0)]
        [Tooltip("Maximum number of small rooms.")]
        public int smallRoomMaxCount = 6;

        [Header("Room Shape")]
        [Range(0f, 1f)]
        [Tooltip("Chance that the hall or a large room uses a connected multi-rect footprint instead of a plain rectangle.")]
        public float nonRectChanceGrand = 0.65f;

        [Range(0f, 1f)]
        [Tooltip("Chance that a mid room uses a connected multi-rect footprint instead of a plain rectangle.")]
        public float nonRectChanceMid = 0.25f;

        [Min(2)]
        [Tooltip("Smallest wing width or depth for non-rect rooms, in grid cells. This prevents one-cell slivers.")]
        public int wingMinDimCells = 2;

        [Min(2)]
        [Tooltip("Largest wing depth for non-rect rooms, in grid cells. Raising this allows broader L/T/plus-style rooms.")]
        public int wingMaxDepthCells = 6;

        [Min(2)]
        [Tooltip("Largest side length a rolled rectangular room part may use, in grid cells.")]
        public int roomMaxSideCells = 12;

        [Min(1f)]
        [Tooltip("Maximum long-side to short-side ratio for rolled rectangular room parts. Higher values permit longer room slabs.")]
        public float roomMaxAspectRatio = 2f;

        [Header("Density And Loops")]
        [Min(1)]
        [Tooltip("Target floor-cell budget. Extra rooms in a band stop after this budget once that band's minimum count has been attempted.")]
        public int floorBudgetCells = 265;

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

        [Range(0f, 1f)]
        [Tooltip("Chance per eligible large room to try a promontory pier into void space.")]
        public float promontoryChancePerRoom = 0.3f;

        [Min(1)]
        [Tooltip("Minimum promontory length in grid cells.")]
        public int promontoryMinLengthCells = 7;

        [Min(1)]
        [Tooltip("Maximum promontory length in grid cells.")]
        public int promontoryMaxLengthCells = 14;

        internal DungeonGenerationSettings ToSettings()
        {
            return new DungeonGenerationSettings
            {
                profileName = profileName,
                mapWidthMinCells = mapWidthMinCells,
                mapWidthMaxCells = mapWidthMaxCells,
                mapDepthMinCells = mapDepthMinCells,
                mapDepthMaxCells = mapDepthMaxCells,
                hallMinAreaCells = hallMinAreaCells,
                hallMaxAreaCells = hallMaxAreaCells,
                largeRoomMinAreaCells = largeRoomMinAreaCells,
                largeRoomMaxAreaCells = largeRoomMaxAreaCells,
                largeRoomMinCount = largeRoomMinCount,
                largeRoomMaxCount = largeRoomMaxCount,
                midRoomMinAreaCells = midRoomMinAreaCells,
                midRoomMaxAreaCells = midRoomMaxAreaCells,
                midRoomMinCount = midRoomMinCount,
                midRoomMaxCount = midRoomMaxCount,
                smallRoomMinAreaCells = smallRoomMinAreaCells,
                smallRoomMaxAreaCells = smallRoomMaxAreaCells,
                smallRoomMinCount = smallRoomMinCount,
                smallRoomMaxCount = smallRoomMaxCount,
                nonRectChanceGrand = nonRectChanceGrand,
                nonRectChanceMid = nonRectChanceMid,
                wingMinDimCells = wingMinDimCells,
                wingMaxDepthCells = wingMaxDepthCells,
                roomMaxSideCells = roomMaxSideCells,
                roomMaxAspectRatio = roomMaxAspectRatio,
                floorBudgetCells = floorBudgetCells,
                denseFloorplanMinRooms = denseFloorplanMinRooms,
                denseFloorplanMinFillPercent = denseFloorplanMinFillPercent,
                loopConnectionFraction = loopConnectionFraction,
                maxLoopCandidateDistanceCells = maxLoopCandidateDistanceCells,
                roomZoneSplitChance = roomZoneSplitChance,
                daisChancePerRoom = daisChancePerRoom,
                promontoryChancePerRoom = promontoryChancePerRoom,
                promontoryMinLengthCells = promontoryMinLengthCells,
                promontoryMaxLengthCells = promontoryMaxLengthCells
            }.Validated();
        }
    }

    internal struct DungeonGenerationSettings
    {
        public string profileName;
        public int mapWidthMinCells;
        public int mapWidthMaxCells;
        public int mapDepthMinCells;
        public int mapDepthMaxCells;
        public int hallMinAreaCells;
        public int hallMaxAreaCells;
        public int largeRoomMinAreaCells;
        public int largeRoomMaxAreaCells;
        public int largeRoomMinCount;
        public int largeRoomMaxCount;
        public int midRoomMinAreaCells;
        public int midRoomMaxAreaCells;
        public int midRoomMinCount;
        public int midRoomMaxCount;
        public int smallRoomMinAreaCells;
        public int smallRoomMaxAreaCells;
        public int smallRoomMinCount;
        public int smallRoomMaxCount;
        public float nonRectChanceGrand;
        public float nonRectChanceMid;
        public int wingMinDimCells;
        public int wingMaxDepthCells;
        public int roomMaxSideCells;
        public float roomMaxAspectRatio;
        public int floorBudgetCells;
        public int denseFloorplanMinRooms;
        public float denseFloorplanMinFillPercent;
        public float loopConnectionFraction;
        public int maxLoopCandidateDistanceCells;
        public float roomZoneSplitChance;
        public float daisChancePerRoom;
        public float promontoryChancePerRoom;
        public int promontoryMinLengthCells;
        public int promontoryMaxLengthCells;

        public static DungeonGenerationSettings Default => new DungeonGenerationSettings
        {
            profileName = "default",
            mapWidthMinCells = 24,
            mapWidthMaxCells = 28,
            mapDepthMinCells = 24,
            mapDepthMaxCells = 28,
            hallMinAreaCells = 36,
            hallMaxAreaCells = 48,
            largeRoomMinAreaCells = 25,
            largeRoomMaxAreaCells = 36,
            largeRoomMinCount = 4,
            largeRoomMaxCount = 6,
            midRoomMinAreaCells = 12,
            midRoomMaxAreaCells = 20,
            midRoomMinCount = 2,
            midRoomMaxCount = 5,
            smallRoomMinAreaCells = 9,
            smallRoomMaxAreaCells = 12,
            smallRoomMinCount = 2,
            smallRoomMaxCount = 6,
            nonRectChanceGrand = 0.65f,
            nonRectChanceMid = 0.25f,
            wingMinDimCells = 2,
            wingMaxDepthCells = 6,
            roomMaxSideCells = 12,
            roomMaxAspectRatio = 2f,
            floorBudgetCells = 265,
            denseFloorplanMinRooms = 9,
            denseFloorplanMinFillPercent = 0.34f,
            loopConnectionFraction = 0.35f,
            maxLoopCandidateDistanceCells = 14,
            roomZoneSplitChance = 0.35f,
            daisChancePerRoom = 0.25f,
            promontoryChancePerRoom = 0.3f,
            promontoryMinLengthCells = 7,
            promontoryMaxLengthCells = 14
        }.Validated();

        public DungeonGenerationSettings Validated()
        {
            var value = this;
            value.profileName = string.IsNullOrWhiteSpace(value.profileName) ? "unnamed" : value.profileName.Trim();
            NormalizeRange(ref value.mapWidthMinCells, ref value.mapWidthMaxCells, 12);
            NormalizeRange(ref value.mapDepthMinCells, ref value.mapDepthMaxCells, 12);
            NormalizeRange(ref value.hallMinAreaCells, ref value.hallMaxAreaCells, 4);
            NormalizeRange(ref value.largeRoomMinAreaCells, ref value.largeRoomMaxAreaCells, 4);
            NormalizeRange(ref value.midRoomMinAreaCells, ref value.midRoomMaxAreaCells, 4);
            NormalizeRange(ref value.smallRoomMinAreaCells, ref value.smallRoomMaxAreaCells, 4);
            NormalizeRange(ref value.largeRoomMinCount, ref value.largeRoomMaxCount, 0);
            NormalizeRange(ref value.midRoomMinCount, ref value.midRoomMaxCount, 0);
            NormalizeRange(ref value.smallRoomMinCount, ref value.smallRoomMaxCount, 0);
            value.nonRectChanceGrand = Mathf.Clamp01(value.nonRectChanceGrand);
            value.nonRectChanceMid = Mathf.Clamp01(value.nonRectChanceMid);
            value.wingMinDimCells = Mathf.Max(2, value.wingMinDimCells);
            value.wingMaxDepthCells = Mathf.Max(value.wingMinDimCells, value.wingMaxDepthCells);
            value.roomMaxSideCells = Mathf.Max(value.wingMinDimCells, value.roomMaxSideCells);
            value.roomMaxAspectRatio = Mathf.Max(1f, value.roomMaxAspectRatio);
            value.floorBudgetCells = Mathf.Max(1, value.floorBudgetCells);
            value.denseFloorplanMinRooms = Mathf.Max(1, value.denseFloorplanMinRooms);
            value.denseFloorplanMinFillPercent = Mathf.Clamp01(value.denseFloorplanMinFillPercent);
            value.loopConnectionFraction = Mathf.Clamp01(value.loopConnectionFraction);
            value.maxLoopCandidateDistanceCells = Mathf.Max(1, value.maxLoopCandidateDistanceCells);
            value.roomZoneSplitChance = Mathf.Clamp01(value.roomZoneSplitChance);
            value.daisChancePerRoom = Mathf.Clamp01(value.daisChancePerRoom);
            value.promontoryChancePerRoom = Mathf.Clamp01(value.promontoryChancePerRoom);
            NormalizeRange(ref value.promontoryMinLengthCells, ref value.promontoryMaxLengthCells, 1);
            return value;
        }

        private static void NormalizeRange(ref int min, ref int max, int floor)
        {
            min = Mathf.Max(floor, min);
            max = Mathf.Max(floor, max);
            if (max < min)
            {
                max = min;
            }
        }
    }
}

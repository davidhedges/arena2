using UnityEngine;

namespace DungeonLab.Editor
{
    // The three generic room size classes a route node's role can resolve to.
    // Recipe rooms have authored footprints and never consult this.
    public enum DungeonRoomSizeClass
    {
        Terminal,
        Hall,
        Connector
    }

    // One row of the role -> size-class map. The map is exhaustive on purpose:
    // an undeclared role is an authoring error, not silently a hall.
    [System.Serializable]
    public struct DungeonRoleSizeClass
    {
        public string role;
        public DungeonRoomSizeClass sizeClass;

        public DungeonRoleSizeClass(string role, DungeonRoomSizeClass sizeClass)
        {
            this.role = role;
            this.sizeClass = sizeClass;
        }
    }

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

        [Min(0)]
        [Tooltip("Maximum total cells the rubber-sheet lattice may add to one axis's lane gaps. 0 pins every lane to its authored minimum.")]
        public int latticeSlackMaxCells;

        public DungeonTierSeamAdjacencySettings tierSeamAdjacency;
        public DungeonRoomSizeRange terminalRoomSize;
        public DungeonRoomSizeRange hallRoomSize;
        public DungeonRoomSizeRange connectorRoomSize;

        public DungeonPatternSpatialSettings(
            int horizontalPitchCells,
            int verticalPitchCells,
            int roomEnvelopeRadiusCells,
            int neighborBiasStrengthCells,
            int latticeSlackMaxCells,
            DungeonTierSeamAdjacencySettings tierSeamAdjacency,
            DungeonRoomSizeRange terminalRoomSize,
            DungeonRoomSizeRange hallRoomSize,
            DungeonRoomSizeRange connectorRoomSize)
        {
            this.horizontalPitchCells = horizontalPitchCells;
            this.verticalPitchCells = verticalPitchCells;
            this.roomEnvelopeRadiusCells = roomEnvelopeRadiusCells;
            this.neighborBiasStrengthCells = neighborBiasStrengthCells;
            this.latticeSlackMaxCells = latticeSlackMaxCells;
            this.tierSeamAdjacency = tierSeamAdjacency;
            this.terminalRoomSize = terminalRoomSize;
            this.hallRoomSize = hallRoomSize;
            this.connectorRoomSize = connectorRoomSize;
        }

        internal DungeonRoomSizeRange RoomSizeForClass(DungeonRoomSizeClass sizeClass)
        {
            switch (sizeClass)
            {
                case DungeonRoomSizeClass.Terminal:
                    return terminalRoomSize;
                case DungeonRoomSizeClass.Connector:
                    return connectorRoomSize;
                default:
                    return hallRoomSize;
            }
        }

        internal DungeonPatternSpatialSettings Validated()
        {
            var value = this;
            value.horizontalPitchCells = Mathf.Max(1, value.horizontalPitchCells);
            value.verticalPitchCells = Mathf.Max(1, value.verticalPitchCells);
            value.latticeSlackMaxCells = Mathf.Max(0, value.latticeSlackMaxCells);
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

        [Header("Route Spatial Configuration")]
        [Tooltip("The default lattice pitch, room envelope, size-class ranges, neighbor bias and rubber-sheet slack for every route topology. A topology file may override any of these; anything it does not override comes from here.")]
        public DungeonPatternSpatialSettings processionalSpatial =
            new DungeonPatternSpatialSettings(
                9,
                9,
                4,
                0,
                8,
                new DungeonTierSeamAdjacencySettings(2, 8),
                new DungeonRoomSizeRange(5, 5, 7, 7),
                new DungeonRoomSizeRange(5, 5, 5, 6),
                new DungeonRoomSizeRange(4, 5, 5, 5));

        // Render-stage only, and deliberately absent from DungeonGenerationSettings:
        // that struct is reflected field-by-field into the per-seed settings
        // digest, so a trap-density tweak there would move every plan hash for a
        // decision the plan never sees.
        [Header("Traps (render stage)")]
        [Tooltip("Place proximity traps during the render pass. Disabling reproduces the pre-trap scene exactly.")]
        public bool trapsEnabled = true;

        [Min(1)]
        [Tooltip("Floor cells per trap. 25 gives roughly 18 traps on a ~450-cell plan.")]
        public int trapFloorCellsPerTrap = 25;

        [Min(1)]
        [Tooltip("Selection weight for corridor cells (cells outside any room). Traps read best in circulation.")]
        public int trapCorridorWeight = 3;

        [Min(1)]
        [Tooltip("Selection weight for room cells.")]
        public int trapRoomWeight = 1;

        [Min(0)]
        [Tooltip("Grid cells around the spawn floor kept clear of traps. The whole arrival room is cleared regardless.")]
        public int trapSpawnClearanceCells = 3;

        [Min(0)]
        [Tooltip("Kind mix weight: full-cell spike field.")]
        public int trapSpikesWeight = 5;

        [Min(0)]
        [Tooltip("Kind mix weight: saw rising through a floor slit and spinning in place.")]
        public int trapSawPostWeight = 2;

        [Min(0)]
        [Tooltip("Kind mix weight: saw travelling along a two-cell lane. Needs two collinear free cells.")]
        public int trapSawSweepWeight = 2;

        [Min(0)]
        [Tooltip("Kind mix weight: wall-mounted sweeping arm. Needs a proven wall and a 2x3 clear block.")]
        public int trapSawArmWeight = 1;

        [Header("Room Size Vocabulary")]
        [Tooltip("Every route node role a topology may declare, mapped to a generic room size class. A role missing from this list is rejected at generation rather than silently sized as a hall.")]
        public DungeonRoleSizeClass[] roleSizeClasses = DefaultRoleSizeClasses();

        internal static DungeonRoleSizeClass[] DefaultRoleSizeClasses()
        {
            return new[]
            {
                new DungeonRoleSizeClass("arrival", DungeonRoomSizeClass.Terminal),
                new DungeonRoleSizeClass("culmination", DungeonRoomSizeClass.Terminal),
                new DungeonRoleSizeClass("connector", DungeonRoomSizeClass.Connector),
                new DungeonRoleSizeClass("junction", DungeonRoomSizeClass.Hall),
                new DungeonRoleSizeClass("grand-room", DungeonRoomSizeClass.Hall),
                new DungeonRoleSizeClass("landmark", DungeonRoomSizeClass.Hall),
                new DungeonRoleSizeClass("processional-hall", DungeonRoomSizeClass.Hall),
                new DungeonRoleSizeClass("return-hall", DungeonRoomSizeClass.Hall),
                new DungeonRoleSizeClass("overlook", DungeonRoomSizeClass.Hall),
                new DungeonRoleSizeClass("optional-room", DungeonRoomSizeClass.Hall)
            };
        }

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
                processionalSpatial = processionalSpatial,
                roleSizeClasses = roleSizeClasses
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
        public DungeonRoleSizeClass[] roleSizeClasses;

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
            // An asset saved before the map existed deserializes an empty array.
            // Falling back to the shipped vocabulary keeps that asset loadable;
            // an asset that declares roles is taken verbatim.
            if (value.roleSizeClasses == null || value.roleSizeClasses.Length == 0)
            {
                value.roleSizeClasses = DungeonGenerationProfile.DefaultRoleSizeClasses();
            }

            return value;
        }

        // Exhaustive by contract: a role with no row is an authoring error that
        // TryValidateRouteIntent reports by node, and Validate Topologies reports
        // by key, rather than a silent fall-through to the hall size.
        public bool TryResolveRoomSizeClass(string role, out DungeonRoomSizeClass sizeClass)
        {
            foreach (DungeonRoleSizeClass entry in roleSizeClasses ?? System.Array.Empty<DungeonRoleSizeClass>())
            {
                if (string.Equals(entry.role, role, System.StringComparison.Ordinal))
                {
                    sizeClass = entry.sizeClass;
                    return true;
                }
            }

            sizeClass = DungeonRoomSizeClass.Hall;
            return false;
        }
    }
}

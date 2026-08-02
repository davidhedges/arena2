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

    // One row of the dial. Everything is relative to the profile's authored
    // values, so row 0 is the identity and the sparse end of the dial is
    // whatever the profile says rather than a special case in code.
    internal readonly struct DungeonDensityRow
    {
        public readonly int pitchDeltaCells;
        public readonly int latticeSlackMaxCells;
        // How much of density 0's channel around a room survives. 0 means rooms
        // meet their neighbours; 1 means the profile's own airiness.
        public readonly float roomGapScale;
        public readonly float enclosedRoomChance;
        // How many of the vacant lattice cells — the craters at map positions no
        // node occupies — M3 hands to an adjacent room. Packing cannot touch
        // them: they are outside every room's placement envelope by definition.
        public readonly float annexVacantFraction;
        // How many lattice bands M4 mops up — the channel M2 left around every
        // room, taken down to single cells. Separate from the annex column
        // because it removes a different thing: the crater is space the packer
        // could never reach, the channel is space it left behind.
        public readonly float mopUpVoidFraction;
        // The backstop against a degenerate layout at THIS level. It is not the
        // thing that makes a dungeon dense — the columns above are — so it sits
        // a few points under the level's observed minimum and normally rejects
        // nothing. One flat number could not do that once fill spanned 26-93%.
        public readonly float minLatticeEnvelopeFillPercent;

        public DungeonDensityRow(
            int pitchDeltaCells,
            int latticeSlackMaxCells,
            float roomGapScale,
            float enclosedRoomChance,
            float annexVacantFraction,
            float mopUpVoidFraction,
            float minLatticeEnvelopeFillPercent)
        {
            this.pitchDeltaCells = pitchDeltaCells;
            this.latticeSlackMaxCells = latticeSlackMaxCells;
            this.roomGapScale = roomGapScale;
            this.enclosedRoomChance = enclosedRoomChance;
            this.annexVacantFraction = annexVacantFraction;
            this.mopUpVoidFraction = mopUpVoidFraction;
            this.minLatticeEnvelopeFillPercent = minLatticeEnvelopeFillPercent;
        }
    }

    // The density dial: one integer, chosen at generation time, that says how
    // much incidental void the dungeon keeps. It replaced the spacious/dense
    // profile pair on 2026-07-27 (docs/dungeon-builder/density-scale-design-2026-07-27.md).
    //
    // 0 is a first-class setting, not a legacy mode: it is today's airy dungeon,
    // large voids and all. 5 is the packed end — void minimal, at most two
    // components larger than one cell. There is no density-0 special case
    // anywhere in the generator; sparse and packed are the two ends of one
    // parameter table.
    internal static class DungeonDensity
    {
        public const int MinLevel = 0;
        public const int MaxLevel = 5;

        // The §4.3 tuning table, retuned by measurement in phase 6 (2026-07-28).
        // It is a table on purpose: the dial is moved by editing these six rows,
        // not by rebuilding anything.
        //
        // §4.3's fill column (28/35/45/60/80/95%) and its mechanism columns
        // disagreed, and the fill column is the one anybody can see. Packing
        // alone tops out near 34% — measured — so densities 1 and 2 could not
        // reach 35% and 45% with "vacant cells untouched", and density 4 could
        // not reach 80% with mop-up off. The mechanism columns moved to hit the
        // fill column; where they still disagree, the fill column wins.
        //
        // Achieved, 200 seeds: 26 / 34 / 45 / 61 / 80 / 93%.
        private static readonly DungeonDensityRow[] Rows =
        {
            //                    pitchDelta  slack  gapScale  enclosure  annex  mopUp  minFill
            new DungeonDensityRow(         0,     8,     1.00f,     0.5f,  0.00f, 0.00f,  0.18f),
            new DungeonDensityRow(         0,     6,     0.80f,     0.6f,  0.25f, 0.10f,  0.22f),
            new DungeonDensityRow(         0,     4,     0.60f,     0.7f,  1.00f, 0.15f,  0.26f),
            new DungeonDensityRow(         0,     2,     0.40f,     0.8f,  1.00f, 0.40f,  0.34f),
            new DungeonDensityRow(         0,     1,     0.20f,     0.9f,  1.00f, 0.60f,  0.48f),
            new DungeonDensityRow(         0,     0,     0.00f,     1.0f,  1.00f, 1.00f,  0.85f)
        };

        public static int Clamp(int level)
        {
            return Mathf.Clamp(level, MinLevel, MaxLevel);
        }

        public static DungeonDensityRow Row(int level)
        {
            return Rows[Clamp(level)];
        }
    }

    [CreateAssetMenu(fileName = "generation_profile", menuName = "Dungeon Lab/Generation Profile")]
    public sealed class DungeonGenerationProfile : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Short label written into generation logs so profile-driven results can be compared across seeds.")]
        public string profileName = "default";

        [Header("Density")]
        [Range(DungeonDensity.MinLevel, DungeonDensity.MaxLevel)]
        [Tooltip("Default density for this project: 0 keeps today's large voids, 5 packs them out. Arena > Dungeons > Density and ARENA_DUNGEON_DENSITY override it per run without touching this asset.")]
        public int densityLevel = DungeonDensity.MinLevel;

        [Header("Map Envelope")]
        [Min(12)]
        [Tooltip("Maximum plan width in 4u grid cells. Route embeddings that exceed it are rejected.")]
        public int mapWidthMaxCells = 28;

        [Min(12)]
        [Tooltip("Maximum plan depth in 4u grid cells. Route embeddings that exceed it are rejected.")]
        public int mapDepthMaxCells = 28;

        [Header("Density")]
        [Min(1)]
        [Tooltip("Minimum accepted room count. Layouts below this are rejected before rendering.")]
        public int denseFloorplanMinRooms = 9;

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

        internal DungeonGenerationSettings ToSettings(int requestedDensityLevel)
        {
            int level = DungeonDensity.Clamp(requestedDensityLevel);
            return new DungeonGenerationSettings
            {
                profileName = profileName,
                densityLevel = level,
                mapWidthMaxCells = mapWidthMaxCells,
                mapDepthMaxCells = mapDepthMaxCells,
                denseFloorplanMinRooms = denseFloorplanMinRooms,
                roomZoneSplitChance = roomZoneSplitChance,
                // Not a spatial setting, but it is on the same dial: once rooms
                // abut, an unenclosed pair merges into one open field.
                enclosedRoomChance = DungeonDensity.Row(level).enclosedRoomChance,
                // Density-relative from phase 6: a flat backstop could not mean
                // anything at both ends of a dial spanning 26% to 93% fill.
                minLatticeEnvelopeFillPercent =
                    DungeonDensity.Row(level).minLatticeEnvelopeFillPercent,
                // Also not a spatial setting: M3 runs after corridors, on cells
                // the packer never had a chance at, so it is a pass rather than
                // a parameter of the packing.
                annexVacantFraction = DungeonDensity.Row(level).annexVacantFraction,
                mopUpVoidFraction = DungeonDensity.Row(level).mopUpVoidFraction,
                // The dial becomes geometry HERE and nowhere else. Applied on
                // the load path rather than inside Validated(), because
                // Validated() is called repeatedly on an already-resolved value
                // and has to stay idempotent.
                processionalSpatial = ResolveDensitySpatialSettings(processionalSpatial, level),
                roleSizeClasses = roleSizeClasses
            }.Validated();
        }

        /// <summary>
        /// The one seam where <c>densityLevel</c> turns into spatial settings.
        /// </summary>
        /// <remarks>
        /// M2 of the density-scale design (§4.2): lane pitch shrinks toward room
        /// size, lattice slack toward 0, room size grows toward the pitch, and
        /// enclosure chance rises to 1.0. Density 0 is the identity by
        /// construction — every row below is expressed as a delta or a scale
        /// against the profile's authored values, and row 0 is (0, authored,
        /// 1.0), so the sparse end of the dial is exactly what the profile says
        /// and not a special case in the code.
        /// <para>
        /// <c>roomEnvelopeRadiusCells</c> deliberately does NOT move. It is
        /// floored at 4 by the reviewed landmark recipe's authored footprint, and
        /// with the pitch shrinking underneath it the 9x9 envelope stops binding
        /// on its own — so making it density-driven would only inflate the fill
        /// denominator, which is the opposite of what §3 wanted from it.
        /// </para>
        /// <para>
        /// Vacant lattice cells (M3) and mop-up (M4) are phases 4 and 5 and are
        /// not here; this method only packs what is already occupied.
        /// </para>
        /// </remarks>
        internal static DungeonPatternSpatialSettings ResolveDensitySpatialSettings(
            DungeonPatternSpatialSettings authored,
            int densityLevel)
        {
            DungeonDensityRow row = DungeonDensity.Row(densityLevel);
            var value = authored;
            value.horizontalPitchCells = Mathf.Max(1, authored.horizontalPitchCells + row.pitchDeltaCells);
            value.verticalPitchCells = Mathf.Max(1, authored.verticalPitchCells + row.pitchDeltaCells);
            value.latticeSlackMaxCells = row.latticeSlackMaxCells;
            value.terminalRoomSize = PackRoomSize(
                authored.terminalRoomSize,
                authored,
                value,
                row.roomGapScale);
            value.hallRoomSize = PackRoomSize(authored.hallRoomSize, authored, value, row.roomGapScale);
            value.connectorRoomSize = PackRoomSize(
                authored.connectorRoomSize,
                authored,
                value,
                row.roomGapScale);
            return value;
        }

        /// <summary>
        /// Puts a topology's own room-size override on the same dial.
        /// </summary>
        /// <remarks>
        /// A topology declares its sizes as offsets from the pitch, and §6's
        /// rule is that such an override states the topology's CHARACTER rather
        /// than pinning it to one density. That worked while §4.3 expected the
        /// pitch to fall from 9 to 6-7; phase 3 measured that assumption wrong
        /// and holds the pitch fixed, at which point a pitch-relative override
        /// is numerically constant and the three topologies that declare one
        /// (twin-wing-keep, descent-shaft, ridge-ravine) stopped packing at all
        /// — they were the three lowest-fill topologies at density 5 by a wide
        /// margin. So the override is read as the topology's density-0 size and
        /// packed by the same rule as the profile's own.
        /// </remarks>
        internal static DungeonRoomSizeRange PackAuthoredRoomSize(
            DungeonRoomSizeRange authoredSize,
            DungeonPatternSpatialSettings spatial,
            int densityLevel)
        {
            return PackRoomSize(
                authoredSize,
                spatial,
                spatial,
                DungeonDensity.Row(densityLevel).roomGapScale);
        }

        /// <summary>
        /// Grows a room toward its lane by shrinking the CHANNEL around it.
        /// </summary>
        /// <remarks>
        /// The gap between two rooms centred one pitch apart is
        /// <c>pitch - width</c>, so a room size is really a statement about the
        /// channel beside it. Reading the authored channel off density 0 and
        /// scaling it keeps each size class's authored character — a connector
        /// stays the tightest, a terminal the most generous — instead of
        /// collapsing every class onto one number at the packed end.
        /// </remarks>
        private static DungeonRoomSizeRange PackRoomSize(
            DungeonRoomSizeRange authoredSize,
            DungeonPatternSpatialSettings authoredSpatial,
            DungeonPatternSpatialSettings packedSpatial,
            float gapScale)
        {
            return new DungeonRoomSizeRange(
                PackRoomExtent(
                    authoredSize.minWidthCells,
                    authoredSpatial.horizontalPitchCells,
                    packedSpatial.horizontalPitchCells,
                    gapScale),
                PackRoomExtent(
                    authoredSize.maxWidthCells,
                    authoredSpatial.horizontalPitchCells,
                    packedSpatial.horizontalPitchCells,
                    gapScale),
                PackRoomExtent(
                    authoredSize.minDepthCells,
                    authoredSpatial.verticalPitchCells,
                    packedSpatial.verticalPitchCells,
                    gapScale),
                PackRoomExtent(
                    authoredSize.maxDepthCells,
                    authoredSpatial.verticalPitchCells,
                    packedSpatial.verticalPitchCells,
                    gapScale)).Validated();
        }

        // Each bound keeps its role: the narrower extent leaves the wider
        // channel, and scaling the channel scales both ends of the range toward
        // the pitch together.
        private static int PackRoomExtent(
            int authoredExtent,
            int authoredPitch,
            int packedPitch,
            float gapScale)
        {
            int authoredGap = authoredPitch - authoredExtent;
            int packedGap = Mathf.RoundToInt(authoredGap * gapScale);
            return packedPitch - packedGap;
        }
    }

    internal struct DungeonGenerationSettings
    {
        public string profileName;
        public int densityLevel;
        public int mapWidthMaxCells;
        public int mapDepthMaxCells;
        public int denseFloorplanMinRooms;
        public float minLatticeEnvelopeFillPercent;
        public float roomZoneSplitChance;
        public float enclosedRoomChance;
        public float annexVacantFraction;
        public float mopUpVoidFraction;
        public DungeonPatternSpatialSettings processionalSpatial;
        public DungeonRoleSizeClass[] roleSizeClasses;

        public DungeonGenerationSettings Validated()
        {
            var value = this;
            value.profileName = string.IsNullOrWhiteSpace(value.profileName) ? "unnamed" : value.profileName.Trim();
            value.densityLevel = DungeonDensity.Clamp(value.densityLevel);
            value.mapWidthMaxCells = Mathf.Max(12, value.mapWidthMaxCells);
            value.mapDepthMaxCells = Mathf.Max(12, value.mapDepthMaxCells);
            value.denseFloorplanMinRooms = Mathf.Max(1, value.denseFloorplanMinRooms);
            value.minLatticeEnvelopeFillPercent = Mathf.Clamp01(value.minLatticeEnvelopeFillPercent);
            value.roomZoneSplitChance = Mathf.Clamp01(value.roomZoneSplitChance);
            value.enclosedRoomChance = Mathf.Clamp01(value.enclosedRoomChance);
            value.annexVacantFraction = Mathf.Clamp01(value.annexVacantFraction);
            value.mopUpVoidFraction = Mathf.Clamp01(value.mopUpVoidFraction);
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

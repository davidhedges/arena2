using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    internal sealed partial class DungeonLabGenerator : ScriptableObject
    {
        private const string PackageInventoryPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/package_inventory.json";
        private const string StairProofContractsPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/stair_proof_contracts.json";
        private const string GenerationProfilePath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/generation_profile.asset";
        // The density dial replaced the spacious/dense profile pair on
        // 2026-07-27. There is one profile asset; how packed the dungeon is, is
        // a number chosen per run. -1 in the editor preference means "no per-user
        // choice yet, take the asset's own densityLevel", which keeps the asset
        // the reproducible default for batch mode too.
        private const int UnsetDensityLevel = -1;
        private const string DensityEnvironmentVariable = "ARENA_DUNGEON_DENSITY";
        private const string DensityEditorPreferenceKey = "Arena.DungeonLab.DensityLevel";
        private const string DensityMenuRoot = "Arena/Dungeons/Density/";
        private const string Density0MenuPath = DensityMenuRoot + "0 (sparse)";
        private const string Density1MenuPath = DensityMenuRoot + "1";
        private const string Density2MenuPath = DensityMenuRoot + "2";
        private const string Density3MenuPath = DensityMenuRoot + "3";
        private const string Density4MenuPath = DensityMenuRoot + "4";
        private const string Density5MenuPath = DensityMenuRoot + "5 (packed)";
        // Forge output (design step 6): same contract shape, separate file; entries
        // join planning only with reviewStatus "reviewed" (human review gate).
        private const string ForgedStairContractsPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/forged_stair_contracts.json";
        private const string PackageAssetRoot = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/";
        private const string GeneratedRootName = "Generated Dungeon";
        // Every entry point supplies a profile ID to the single settings resolver.
        // Downstream planning consumes only the resolved settings value and never
        // branches on profile identity.
        // One generated level is 1u of world height (the elevation quantum since
        // the stair-forge recalibration). The plan grid stays 4u cells. Phase E
        // makes the route envelope a topology property: old files omit `ceiling`
        // and therefore retain the historical 24u story, while a new topology may
        // opt into a taller one. Forty is a schema safety limit, not a reason to
        // stretch every existing dungeon.
        private const int DefaultTopologyCeilingLevels = 24;
        private const int MaxTopologyCeilingLevels = 40;
        // Magnificence decision A: inter-room/tier elevation lands on a 4u lattice.
        // A corridor climbs one major (4u) or a steeper double-major (8u); 1u and
        // 2u are reserved for INTRA-room accents, never plain
        // corridors. Phase 3 route edges declare their 4u/8u structural transition
        // type before the tier planner reserves a concrete realization.
        private const int MajorRiseLevels = 4;
        private const int DoubleMajorRiseLevels = 8;
        // One predicate owns the existing structural lattice. Structural
        // topology is still expressed in the generator's long-standing 1u
        // levels and MajorRiseLevels quantum; this only prevents each validator
        // from restating the divisibility arithmetic independently.
        internal static bool IsStructuralLevel(int level)
        {
            return level % MajorRiseLevels == 0;
        }

        private static bool AreStructuralConnectionLevels(int firstLevel, int secondLevel)
        {
            if (!IsStructuralLevel(firstLevel) || !IsStructuralLevel(secondLevel))
            {
                return false;
            }

            int delta = Mathf.Abs(firstLevel - secondLevel);
            return delta == 0 ||
                delta == MajorRiseLevels ||
                delta == DoubleMajorRiseLevels;
        }

        private static bool AreStructuralFlatBridgeLandingLevels(
            int firstLevel,
            int secondLevel)
        {
            return firstLevel == secondLevel &&
                IsStructuralLevel(firstLevel) &&
                IsStructuralLevel(secondLevel);
        }
        // Apertures are optional traversable falls, not lethal voids. Until the
        // runtime owns fall damage, keep their survivable vocabulary inside the
        // already-reviewed double-major vertical move rather than silently
        // allowing an envelope-spanning drop.
        private const int MaxSurvivableFallLevels = DoubleMajorRiseLevels;
        // The primary straight stair climbs 2u and remains the edge model's
        // reviewed primary-stair physical contract. Route corridors use 4u/8u.
        private const int PrimaryStairRiseLevels = 2;
        // Minimum clearance in u between a walkable surface and geometry above it
        // (design decision 2): pass-unders, bridges, overhangs, forge candidates.
        private const int MinHeadroomLevels = 3;
        // Intra-room 1u splits (design step 5): a room may divide into a lower and a
        // raised (+1u) zone along one straight seam; every cell pair across the seam
        // is a rise-1 step strip, so there is no free walk across the 1u delta.
        private const int MinZoneDepthCells = 2;
        private const string SeamStairPlacementClass = "seam";
        // Recipe-owned 1u transitions and intra-room completion strips share this
        // measured transition class with the canonical edge renderer. Backed focal
        // showpieces use their own synthesized piece plans below.
        private const string DaisStairPlacementClass = "dais";
        // Magnificence decision J, hardened by Phase 6e: a promontory is now the
        // source-side walkable prefix of one already-declared named vista. The
        // route planner reserves it before structural fill and leaves the vista's
        // minimum clear void untouched. The canonical tier plan owns target
        // identity; the renderer still consumes only the exact projected cells.
        private const int InternalOpenPathMinRunCells = 3;
        private const int InternalOpenPathRailingPercent = 25;
        // A backed showpiece dais: the approved gallery design instantiated
        // verbatim at a wall anchor. Purely cosmetic in the plan — no
        // cellLevels change and no transitions; the covered cells are
        // ledger-reserved and the piece list carries its own floors, steps
        // and corner pieces (the sculpted tops must NOT get standard cell
        // floors underneath, and the room floor correctly continues under
        // the platform per the gold-scene convention).
        internal sealed class DaisShowpiece
        {
            public string designName;
            public Vector2Int originCell;
            public float yawDegrees;
            public int roomLevel;
            public ElevationEdgeModel.SynthesizedPiecePlacement[] pieces;
        }
        // Name only selects WHICH piece to use for seam strips; the edge model
        // verifies the measured rise from the step piece library before placing.
        private const string SeamStairPieceName = "P_MOD_Stairs_01_E_straight_4";
        private const int MaxActiveStairLaneCount = 2;
        private const string ActiveStraightStairTopology = "straight";
        private const string ActiveTurningStairTopology = "turning";
        // Stairwells (design decisions 26-28): 180-degree towers beside the path.
        private const string ActiveStairwellStairTopology = "stairwell";
        // Aerial decks (step 8, decisions 29-31): rise-0 flat spans.
        private const string ActiveDeckStairTopology = "deck";
        private const string EmbeddedStairPlacementClass = "embedded";
        private const string ExternalSpanStairPlacementClass = "externalSpan";
        private const string StairwellStairPlacementClass = "stairwell";
        private const string DirectDoorwayPlacementClass = "directDoorway";
        private const string RoutedCorridorPlacementClass = "routedCorridor";
        // Tier attempts do not re-roll level assignment: TryAssignRoomLevels is a
        // deterministic copy of the route intent's declared elevations. What varies
        // is stair candidate selection, keyed on the tier attempt index. Route
        // loops and bridges are already topology relationships and are never
        // rolled by a later architectural pass.
        //
        // Measured over seeds 2026072100..2026072299 (dense): max 2 attempts,
        // p95 1, mean 1.02, histogram {1: 194, 2: 4}. The former ceiling of 32 was
        // 16x the observed maximum and made a doomed seed repeat the identical
        // impossible reservation 64 times before failing. Lowering it is
        // output-neutral now that each attempt's streams are keyed by attempt
        // index rather than by how many draws earlier attempts happened to make.
        private const int TierPlacementAttempts = 4;

        // How many tier attempts the accepted plan actually needed. Recorded so the
        // ceiling above can be sized from measurement instead of guessed at.
        private static int lastTierPlacementAttempts;
        // The density at which "at least one open room" stops being a variety
        // guarantee and starts being a hole in the wall grammar. See
        // ChooseEnclosedRooms.
        private const int OpenRoomGuaranteeMaxDensityLevel = 4;

        private int seed;
        private bool createPlayCamera = true;
        private Vector3 origin = Vector3.zero;
        private static DungeonGenerationSettings CurrentGenerationSettings;

        [MenuItem("Tools/Dungeon Lab/Generate")]
        public static void Generate()
        {
            GenerateWithSeed(CreateRandomSeed());
        }

        [MenuItem("Tools/Dungeon Lab/Open Generation Profile")]
        public static void OpenGenerationProfile()
        {
            DungeonGenerationProfile profile = LoadGenerationProfileAsset();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
            EditorUtility.FocusProjectWindow();
        }

        [MenuItem(Density0MenuPath, false, 80)]
        private static void SelectDensity0() => SelectEditorDensityLevel(0);

        [MenuItem(Density0MenuPath, true)]
        private static bool ValidateDensity0() => UpdateDensityMenuItem(Density0MenuPath, 0);

        [MenuItem(Density1MenuPath, false, 81)]
        private static void SelectDensity1() => SelectEditorDensityLevel(1);

        [MenuItem(Density1MenuPath, true)]
        private static bool ValidateDensity1() => UpdateDensityMenuItem(Density1MenuPath, 1);

        [MenuItem(Density2MenuPath, false, 82)]
        private static void SelectDensity2() => SelectEditorDensityLevel(2);

        [MenuItem(Density2MenuPath, true)]
        private static bool ValidateDensity2() => UpdateDensityMenuItem(Density2MenuPath, 2);

        [MenuItem(Density3MenuPath, false, 83)]
        private static void SelectDensity3() => SelectEditorDensityLevel(3);

        [MenuItem(Density3MenuPath, true)]
        private static bool ValidateDensity3() => UpdateDensityMenuItem(Density3MenuPath, 3);

        [MenuItem(Density4MenuPath, false, 84)]
        private static void SelectDensity4() => SelectEditorDensityLevel(4);

        [MenuItem(Density4MenuPath, true)]
        private static bool ValidateDensity4() => UpdateDensityMenuItem(Density4MenuPath, 4);

        [MenuItem(Density5MenuPath, false, 85)]
        private static void SelectDensity5() => SelectEditorDensityLevel(5);

        [MenuItem(Density5MenuPath, true)]
        private static bool ValidateDensity5() => UpdateDensityMenuItem(Density5MenuPath, 5);

        private static void SelectEditorDensityLevel(int densityLevel)
        {
            int level = DungeonDensity.Clamp(densityLevel);
            EditorPrefs.SetInt(DensityEditorPreferenceKey, level);
            Debug.Log($"[DUNGEON_DENSITY] Unity editor selection is now density {level}.");
        }

        private static bool UpdateDensityMenuItem(string menuPath, int densityLevel)
        {
            Menu.SetChecked(menuPath, ResolveRequestedDensityLevel() == densityLevel);
            return !HasDensityEnvironmentOverride();
        }

        // Review tool: regenerate a specific dungeon — a seed the harness reports
        // (e.g. one that used synthesis) or one recovered from a logged
        // GENERATION_SUMMARY ("random dungeon seed N" in the console/Editor.log).
        [MenuItem("Tools/Dungeon Lab/Generate (Specific Seed)")]
        public static void GenerateSpecificSeed()
        {
            ScriptableWizard.DisplayWizard<GenerateSeedWizard>("Generate Dungeon From Seed", "Generate");
        }

        private sealed class GenerateSeedWizard : ScriptableWizard
        {
            public int seed;

            private void OnWizardCreate()
            {
                GenerateWithSeed(seed);
            }
        }

        internal static void GenerateWithSeed(int seed)
        {
            ResetNavigationSurfaceExport();
            var generator = ScriptableObject.CreateInstance<DungeonLabGenerator>();
            try
            {
                generator.seed = seed;
                generator.createPlayCamera = false;
                generator.origin = Vector3.zero;
                CurrentGenerationSettings = LoadActiveGenerationSettings(ResolveRequestedDensityLevel());
                generator.GenerateRandomDungeonLayout();
            }
            finally
            {
                DestroyImmediate(generator);
            }
        }

        // Environment beats the per-user editor choice, which beats the asset's
        // own default. Batch mode has no per-user choice, so with no environment
        // override it is the asset — which is what makes a command-line run
        // reproducible from the repo alone.
        //
        // Kept free of side effects: Unity calls this from every Density menu
        // validate, which is every time the menu is painted.
        private static int ResolveRequestedDensityLevel()
        {
            string configured = Environment.GetEnvironmentVariable(DensityEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!int.TryParse(configured.Trim(), out int parsed) ||
                    parsed < DungeonDensity.MinLevel ||
                    parsed > DungeonDensity.MaxLevel)
                {
                    throw new InvalidOperationException(
                        $"[DUNGEON_DENSITY] {DensityEnvironmentVariable}='{configured}' is not a density " +
                        $"level. Expected an integer {DungeonDensity.MinLevel}..{DungeonDensity.MaxLevel}.");
                }

                return parsed;
            }

            if (!Application.isBatchMode)
            {
                int selected = EditorPrefs.GetInt(DensityEditorPreferenceKey, UnsetDensityLevel);
                if (selected != UnsetDensityLevel)
                {
                    return DungeonDensity.Clamp(selected);
                }
            }

            return DungeonDensity.Clamp(LoadGenerationProfileAsset().densityLevel);
        }

        private static bool HasDensityEnvironmentOverride()
        {
            return !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(DensityEnvironmentVariable));
        }

        private static DungeonGenerationProfile LoadGenerationProfileAsset()
        {
            DungeonGenerationProfile profile =
                AssetDatabase.LoadAssetAtPath<DungeonGenerationProfile>(GenerationProfilePath);
            if (profile == null)
            {
                throw new InvalidOperationException(
                    $"[GENERATION_PROFILE] missing required profile at {GenerationProfilePath}");
            }

            return profile;
        }

        private static DungeonGenerationSettings LoadActiveGenerationSettings(int densityLevel)
        {
            return LoadGenerationProfileAsset().ToSettings(densityLevel);
        }

        /// <summary>
        /// Trap placement is read from the same profile asset but deliberately
        /// kept out of <see cref="DungeonGenerationSettings"/>: that struct is
        /// reflected into the per-seed settings digest, and a render-stage
        /// density knob must not move a plan hash.
        /// </summary>
        private static ElevationEdgeModel.TrapPlacementSettings LoadActiveTrapPlacementSettings(int seed)
        {
            DungeonGenerationProfile profile =
                AssetDatabase.LoadAssetAtPath<DungeonGenerationProfile>(GenerationProfilePath);
            if (profile == null)
            {
                return ElevationEdgeModel.TrapPlacementSettings.Disabled;
            }

            return new ElevationEdgeModel.TrapPlacementSettings(
                seed,
                profile.trapsEnabled,
                profile.trapFloorCellsPerTrap,
                profile.trapCorridorWeight,
                profile.trapRoomWeight,
                profile.trapSpawnClearanceCells,
                profile.trapSpikesWeight,
                profile.trapSawPostWeight,
                profile.trapSawSweepWeight,
                profile.trapSawArmWeight);
        }

        private void GenerateRandomDungeonLayout()
        {
            var rejectionHistogram = new Dictionary<string, int>();
            if (!TryBuildAcceptedPlan(
                    seed,
                    rejectionHistogram,
                    out DungeonLayout layout,
                    out TieredLevelPlan levelPlan,
                    out RouteIntent acceptedRouteIntent,
                    out ElevationEdgeModel.RoomBoundaryContext roomBoundaryContext,
                    out DungeonPlanValidation validation,
                    out int layoutAttemptsUsed,
                    out string rejectionReason))
            {
                Debug.LogError(
                    $"Dungeon Lab: failed to build reachable tiered dungeon after {layoutAttemptsUsed} attempts. Last rejection: {rejectionReason}. " +
                    $"Rejection histogram: {FormatRejectionHistogram(rejectionHistogram)}");
                return;
            }

            if (rejectionHistogram.Count > 0)
            {
                Debug.Log(
                    $"Dungeon Lab: accepted layout after {layoutAttemptsUsed} layout attempt(s). " +
                    $"Rejection histogram: {FormatRejectionHistogram(rejectionHistogram)}");
            }

            // Online synthesis (step 7, decision 19): provisional staircases enter
            // the pending review queue BEFORE the build, so even a render-stage
            // rejection leaves the entry available for diagnosis.
            if (levelPlan.synthesizedStairs != null && levelPlan.synthesizedStairs.Count > 0)
            {
                StairForge.AppendSynthesisLog(seed, levelPlan.synthesizedStairs);
            }

            RoomFootprint largestRoom = GetLargestRoom(layout.rooms);
            Vector3 levelFieldOrigin = CalculateCenteredLevelFieldOrigin(layout.floorCells, origin);
            // The boundary context was built and validated inside the accepted
            // attempt; rebuilding it here would draw from the shared stream twice.
            if (roomBoundaryContext == null)
            {
                Debug.LogError("Dungeon Lab: accepted plan carried no room boundary context.");
                return;
            }

            GameObject root;
            ElevationEdgeModel.BuildReport report;
            Bounds bounds;
            try
            {
                // The full overload, so the renderer is handed the plan's
                // stacked surfaces. The forwarding overload hard-codes `null`
                // there (ElevationEdgeModel.cs), which is why routing through it
                // meant a stacked plan rendered as its column floors alone.
                root = ElevationEdgeModel.BuildLevelField(
                    levelFieldOrigin,
                    levelPlan.surfaces.ColumnFloors(),
                    levelPlan.surfaces.StackedSurfaces(),
                    levelPlan.transitions,
                    null,
                    BuildPlannedOpenEdges(levelPlan),
                    roomBoundaryContext,
                    CollectRenderedPromontoryCells(
                        levelPlan.namedPromontories,
                        levelPlan.externalConnectors),
                    LoadActiveTrapPlacementSettings(seed),
                    GeneratedRootName,
                    out report,
                    out bounds);
            }
            catch (Exception exception)
            {
                Debug.LogError($"Dungeon Lab: shared elevation edge model failed. {exception.Message}");
                return;
            }
            // Unlike best-effort visual diagnostics, the requested 1-4 rendered
            // outer promontories are a save-blocking production invariant.
            RequireRenderedPromontoryDecks(levelPlan, report);

            if (levelPlan.daisShowpieces != null && levelPlan.daisShowpieces.Count > 0)
            {
                try
                {
                    PlaceDaisShowpieces(root.transform, levelPlan.daisShowpieces, levelFieldOrigin, report.levelHeight, ref bounds);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"Dungeon Lab: dais showpiece placement failed. {exception.Message}");
                    return;
                }
            }

            // Capture only after the renderer and authored showpieces succeed.
            // Scene preparation may translate the root before export; the nav
            // projection stores local grid coordinates and resolves world
            // centres against that final transform.
            CaptureNavigationSurfacePlan(
                seed,
                acceptedRouteIntent.patternId,
                levelPlan,
                roomBoundaryContext,
                levelFieldOrigin,
                root);

            if (createPlayCamera)
            {
                EnsurePlayCamera(bounds, 4f);
            }

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            float floorFillPercent = CalculateFloorFillPercent(layout.floorCells);
            int loopEdges = CountLoopEdges(layout);
            Debug.Log(
                $"Dungeon Lab: random dungeon seed {seed}, profile {CurrentGenerationSettings.profileName}, settings {GenerationSettingsDigest(CurrentGenerationSettings)}, archetype {levelPlan.archetypeName}, cells {layout.floorCells.Count}, rooms {layout.rooms.Count}, largest_room {largestRoom.Area}c_{largestRoom.bounds.width}x{largestRoom.bounds.height}p{largestRoom.parts.Count}, " +
                $"connections {layout.connections.Count}, loop edges {loopEdges} (=C-(R-1)), floor-fill {floorFillPercent * 100f:0.#}%, " +
                $"connector candidates from tag = {levelPlan.connectorCandidateCount}, " +
                $"stair usage {levelPlan.stairUsageSummary}, " +
                $"tiers {levelPlan.minLevel}..{levelPlan.maxLevel}, rooms per tier {levelPlan.roomsPerTierSummary}, overlooks {levelPlan.overlookCount} (spatial delta>=2), all reachable, " +
                $"transitions: {levelPlan.transitionSummary}, portGraph {levelPlan.portGraphSummary}; edgeModel {report.Summary} | REJECTED {report.rejected}, OVERLAP 0.");
            Debug.Log(
                "Dungeon Lab GENERATION_SUMMARY " +
                $"profile={CurrentGenerationSettings.profileName}; " +
                $"settingsDigest={GenerationSettingsDigest(CurrentGenerationSettings)}; " +
                $"archetype={levelPlan.archetypeName}; " +
                $"rooms={layout.rooms.Count}; " +
                $"floorRegions={layout.rooms.Count}; " +
                $"floorPrefabs={report.floorCells}; " +
                $"stairCount={report.transitionEdges}; " +
                $"stairHistogram={levelPlan.stairUsageSummary}; " +
                $"riseHistogram={levelPlan.transitionSummary}; " +
                $"topologyHistogram={levelPlan.topologySummary}; " +
                $"placementClassHistogram={levelPlan.placementClassSummary}; " +
                $"stairCandidateHistogram={levelPlan.stairCandidateSummary}; " +
                $"synthesizedStairs={(levelPlan.synthesizedStairs == null ? 0 : levelPlan.synthesizedStairs.Count)}; " +
                $"synthesizedStairUsage={levelPlan.synthesizedStairSummary}; " +
                $"rejectedContracts={report.rejectedContracts}; " +
                $"rejectedContractReasons={report.rejectedContractReasons}; " +
                $"rejectedPlacements={report.rejected}; " +
                $"unsupportedContracts={report.unsupportedContracts}; " +
                $"unsupportedContractReasons={report.unsupportedContractReasons}; " +
                $"internalPathEdges={report.internalPathEdges}; " +
                $"internalPathRailings={report.internalPathRailings}; " +
                $"internalPathBareEdges={report.internalPathBareEdges}; " +
                $"portGraph={levelPlan.portGraphSummary}; " +
                "reachable=Y; " +
                $"validation={(report.rejected == 0 ? "PASS" : "FAIL")}");
        }

        private static bool TryParseDirectionName(string value, out int direction)
        {
            direction = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "north":
                case "n":
                    direction = Direction.North;
                    return true;
                case "east":
                case "e":
                    direction = Direction.East;
                    return true;
                case "south":
                case "s":
                    direction = Direction.South;
                    return true;
                case "west":
                case "w":
                    direction = Direction.West;
                    return true;
                case "discover":
                    return false;
                default:
                    return false;
            }
        }

        private static int OppositeDirection(int direction)
        {
            switch (direction)
            {
                case Direction.North:
                    return Direction.South;
                case Direction.East:
                    return Direction.West;
                case Direction.South:
                    return Direction.North;
                case Direction.West:
                    return Direction.East;
                default:
                    return 0;
            }
        }

        private static int CreateRandomSeed()
        {
            return Guid.NewGuid().GetHashCode();
        }

        // Intra-room 1u splits (design step 5): each room deep enough on some axis
        // may split into a lower and a raised (+1u) zone along one straight seam.
        // Both sides keep at least MinZoneDepthCells of depth so the seam's step
        // strips and their landings have room.
        private static readonly HashSet<Vector2Int> EmptyCellSet = new HashSet<Vector2Int>();

        private static List<RoomZonePlan> ChooseRoomZoneSplits(
            IReadOnlyList<RoomFootprint> rooms,
            IReadOnlyDictionary<int, HashSet<Vector2Int>> roomThresholds,
            System.Random random,
            DungeonGenerationSettings settings)
        {
            var plans = new List<RoomZonePlan>();
            for (int roomIndex = 0; roomIndex < rooms.Count; roomIndex++)
            {
                RoomFootprint room = rooms[roomIndex];
                RectInt bounds = room.bounds;
                bool canSplitAlongX = bounds.width >= MinZoneDepthCells * 2;
                bool canSplitAlongY = bounds.height >= MinZoneDepthCells * 2;
                if (!canSplitAlongX && !canSplitAlongY)
                {
                    continue;
                }

                if (random.NextDouble() >= settings.roomZoneSplitChance)
                {
                    continue;
                }

                bool splitAlongX = canSplitAlongX && (!canSplitAlongY || random.Next(2) == 0);
                int threshold = splitAlongX
                    ? bounds.xMin + random.Next(MinZoneDepthCells, bounds.width - MinZoneDepthCells + 1)
                    : bounds.yMin + random.Next(MinZoneDepthCells, bounds.height - MinZoneDepthCells + 1);

                var first = new HashSet<Vector2Int>();
                var second = new HashSet<Vector2Int>();
                foreach (Vector2Int cell in room.cells)
                {
                    int coordinate = splitAlongX ? cell.x : cell.y;
                    (coordinate < threshold ? first : second).Add(cell);
                }

                if (first.Count == 0 || second.Count == 0)
                {
                    continue;
                }

                // Decision A: the raised (+1) zone is a pure intra-room accent, so
                // it must own no corridor threshold — otherwise the room's base
                // (lattice anchor) would shift to an odd level and corridors to it
                // come out off-grammar. Raise the threshold-free side; if both
                // sides own a threshold, leave the room flat.
                HashSet<Vector2Int> thresholds = roomThresholds.TryGetValue(roomIndex, out HashSet<Vector2Int> t) ? t : EmptyCellSet;
                bool firstHasThreshold = first.Overlaps(thresholds);
                bool secondHasThreshold = second.Overlaps(thresholds);
                bool firstRaised = random.Next(2) == 0;
                if (firstHasThreshold && secondHasThreshold)
                {
                    continue;
                }

                if (firstHasThreshold)
                {
                    firstRaised = false;
                }
                else if (secondHasThreshold)
                {
                    firstRaised = true;
                }

                var plan = new RoomZonePlan(
                    roomIndex,
                    firstRaised ? second : first,
                    firstRaised ? first : second);

                // For non-rect rooms the straight seam may cross a wing where a
                // side is too shallow for the strip's landing: every seam pair
                // needs one walkable room cell beyond it on both sides (the
                // rect version guaranteed this via MinZoneDepthCells per side).
                if (!ZoneSeamHasLandings(plan, room))
                {
                    continue;
                }

                plans.Add(plan);
            }

            return plans;
        }

        private static bool ZoneSeamHasLandings(RoomZonePlan plan, RoomFootprint room)
        {
            List<(Vector2Int lowerCell, Vector2Int raisedCell)> pairs = plan.SeamCellPairs();
            if (pairs.Count == 0)
            {
                return false;
            }

            foreach ((Vector2Int lowerCell, Vector2Int raisedCell) in pairs)
            {
                Vector2Int direction = raisedCell - lowerCell;
                if (!room.Contains(lowerCell - direction) || !room.Contains(raisedCell + direction))
                {
                    return false;
                }
            }

            return true;
        }

        // A connection's effective endpoints are the zones holding its door
        // thresholds: the last path cell inside each endpoint room before the
        // corridor leaves it (design step 5: door thresholds bind to zones).
        private static void ResolveConnectionNodes(
            RoomZoneContext zones,
            IReadOnlyList<RoomFootprint> rooms,
            RoomConnection connection,
            out int fromNode,
            out int toNode)
        {
            fromNode = NodeOfThreshold(zones, rooms[connection.fromRoom], connection.fromRoom, connection.path, forward: true);
            toNode = NodeOfThreshold(zones, rooms[connection.toRoom], connection.toRoom, connection.path, forward: false);
        }

        private static int NodeOfThreshold(
            RoomZoneContext zones,
            RoomFootprint room,
            int roomIndex,
            IReadOnlyList<Vector2Int> path,
            bool forward)
        {
            if (path == null || path.Count == 0)
            {
                return roomIndex;
            }

            return zones.NodeOfCell(roomIndex, ThresholdCell(room, path, forward));
        }

        // The room cell a corridor binds to: the last path cell still inside the
        // room scanning from the connection's near end (forward = fromRoom side).
        private static Vector2Int ThresholdCell(RoomFootprint room, IReadOnlyList<Vector2Int> path, bool forward)
        {
            Vector2Int threshold = forward ? path[0] : path[path.Count - 1];
            if (forward)
            {
                for (int i = 0; i < path.Count && room.Contains(path[i]); i++)
                {
                    threshold = path[i];
                }
            }
            else
            {
                for (int i = path.Count - 1; i >= 0 && room.Contains(path[i]); i--)
                {
                    threshold = path[i];
                }
            }

            return threshold;
        }

        // Per-room corridor threshold cells (decision A): a split room's raised
        // (+1) zone must own none of these, so the 4u lattice anchors on the base
        // zone and every corridor delta stays on-grammar.
        private static Dictionary<int, HashSet<Vector2Int>> BuildRoomThresholdCells(
            IReadOnlyList<RoomFootprint> rooms,
            IReadOnlyList<RoomConnection> connections)
        {
            var thresholds = new Dictionary<int, HashSet<Vector2Int>>();
            void Add(int roomIndex, Vector2Int cell)
            {
                if (!thresholds.TryGetValue(roomIndex, out HashSet<Vector2Int> set))
                {
                    set = new HashSet<Vector2Int>();
                    thresholds[roomIndex] = set;
                }

                set.Add(cell);
            }

            foreach (RoomConnection connection in connections)
            {
                if (connection.path == null || connection.path.Count == 0)
                {
                    continue;
                }

                Add(connection.fromRoom, ThresholdCell(rooms[connection.fromRoom], connection.path, forward: true));
                Add(connection.toRoom, ThresholdCell(rooms[connection.toRoom], connection.path, forward: false));
            }

            return thresholds;
        }

        // Picks where a 1u corridor step strip sits: anywhere between the from-room
        // threshold and the to-room threshold (inclusive), so even a door-to-door
        // corridor can host the step right at a doorway. Cells before the index are
        // at the from level and cells after at the to level; the in-room threshold
        // cells are already zone-leveled to those same values, so any index in this
        // window levels consistently. The middle candidate keeps longer corridors'
        // steps away from doorways.
        private static bool TryChooseCorridorStepIndex(
            IReadOnlyList<Vector2Int> path,
            RoomFootprint fromRoom,
            RoomFootprint toRoom,
            HashSet<Vector2Int> doorwayCells,
            PrismLedger plannedStairLedger,
            bool fromSideIsLower,
            out int transitionIndex)
        {
            int fromThresholdIndex = 0;
            for (int i = 0; i < path.Count && fromRoom.Contains(path[i]); i++)
            {
                fromThresholdIndex = i;
            }

            int toThresholdIndex = path.Count - 1;
            for (int i = path.Count - 1; i >= 0 && toRoom.Contains(path[i]); i--)
            {
                toThresholdIndex = i;
            }

            if (toThresholdIndex <= fromThresholdIndex)
            {
                transitionIndex = -1;
                return false;
            }

            // Prefer the legal pair nearest the corridor midpoint with neither
            // strip cell in a doorway (rule 2026-06-11 — no step half-blocking an
            // entrance). A corridor strip runs PARALLEL to the walk, so one at a
            // door reads as ordinary steps through the opening — when a short
            // door-to-door corridor leaves no doorway-free pair, fall back to the
            // midpoint rather than rejecting the connection (the hard rule lives
            // on the perpendicular seam strips, where the artifact was reported).
            int midpoint = (fromThresholdIndex + toThresholdIndex) / 2;
            int bestIndex = -1;
            int bestDistance = int.MaxValue;
            int fallbackIndex = -1;
            int fallbackDistance = int.MaxValue;
            for (int i = fromThresholdIndex; i < toThresholdIndex; i++)
            {
                Vector2Int lowerCell = fromSideIsLower ? path[i] : path[i + 1];
                if (plannedStairLedger.BlocksFootprint(lowerCell) ||
                    plannedStairLedger.BlocksTransitionMouth(path[i]) ||
                    plannedStairLedger.BlocksTransitionMouth(path[i + 1]))
                {
                    continue;
                }

                int distance = Mathf.Abs(i - midpoint);
                if (distance < fallbackDistance)
                {
                    fallbackDistance = distance;
                    fallbackIndex = i;
                }

                if (doorwayCells.Contains(path[i]) || doorwayCells.Contains(path[i + 1]))
                {
                    continue;
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            transitionIndex = bestIndex >= 0 ? bestIndex : fallbackIndex;
            return transitionIndex >= 0;
        }

        private static string cachedSeamStairPrefabPath;

        // Resolves the seam strip piece path once per session from the package
        // inventory. Planning-path code: must stay headless-safe, so this parses the
        // JSON with Newtonsoft directly (JsonUtility is a native call and crashes
        // headless validation hosts). The edge model re-validates the piece's
        // measured rise from the step piece library before placing it.
        private static string ResolveSeamStairPrefabPath()
        {
            if (string.IsNullOrEmpty(cachedSeamStairPrefabPath))
            {
                cachedSeamStairPrefabPath = ResolveInventoryPrefabPath(SeamStairPieceName);
            }

            return cachedSeamStairPrefabPath;
        }

        private static string ResolveInventoryPrefabPath(string pieceName)
        {
            foreach (JToken item in JArray.Parse(File.ReadAllText(PackageInventoryPath)))
            {
                if (string.Equals(item.Value<string>("name"), pieceName, StringComparison.Ordinal))
                {
                    string path = item.Value<string>("path");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return PackageAssetRoot + path;
                    }
                }
            }

            throw new InvalidOperationException($"Strip piece '{pieceName}' was not found in {PackageInventoryPath}.");
        }

        private static StairConnectorSettings LoadAuthoredStairConnectorTableForGeneration()
        {
            try
            {
                return StairConnectorSettings.Load();
            }
            catch (Exception exception)
            {
                LogPlanningWarning($"Dungeon Lab: could not read authored stair connector candidates from {StairConnectorSettings.Path}; {exception.Message}");
                return null;
            }
        }

        private static int CountConfiguredStairConnectorPrefabs(StairConnectorSettings connectorTable)
        {
            if (connectorTable == null || string.IsNullOrWhiteSpace(connectorTable.StairConnectorDirectory))
            {
                return 0;
            }

            // Reporting-only count; tolerate environments where the AssetDatabase is not
            // available (headless plan validation) so the planning stage stays pure data.
            try
            {
                return AssetDatabase.FindAssets("t:Prefab", new[] { connectorTable.StairConnectorDirectory }).Length;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static RectInt GetCellRect(HashSet<Vector2Int> floorCells)
        {
            GetCellBounds(floorCells, out Vector2Int minCell, out Vector2Int maxCell);
            return new RectInt(minCell.x, minCell.y, maxCell.x - minCell.x + 1, maxCell.y - minCell.y + 1);
        }

        private static RoomFootprint GetLargestRoom(List<RoomFootprint> rooms)
        {
            if (rooms == null || rooms.Count == 0)
            {
                return RoomFootprint.FromRect(new RectInt(0, 0, 0, 0));
            }

            RoomFootprint largest = rooms[0];
            for (int i = 1; i < rooms.Count; i++)
            {
                if (rooms[i].Area > largest.Area)
                {
                    largest = rooms[i];
                }
            }

            return largest;
        }

        private static bool IsConnected(HashSet<Vector2Int> floorCells)
        {
            if (floorCells.Count <= 1)
            {
                return true;
            }

            using var enumerator = floorCells.GetEnumerator();
            enumerator.MoveNext();

            var visited = new HashSet<Vector2Int>();
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(enumerator.Current);
            visited.Add(enumerator.Current);

            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                VisitNeighbor(cell + Vector2Int.up, floorCells, visited, queue);
                VisitNeighbor(cell + Vector2Int.down, floorCells, visited, queue);
                VisitNeighbor(cell + Vector2Int.left, floorCells, visited, queue);
                VisitNeighbor(cell + Vector2Int.right, floorCells, visited, queue);
            }

            return visited.Count == floorCells.Count;
        }

        private static void VisitNeighbor(
            Vector2Int neighbor,
            HashSet<Vector2Int> floorCells,
            HashSet<Vector2Int> visited,
            Queue<Vector2Int> queue)
        {
            if (!floorCells.Contains(neighbor) || visited.Contains(neighbor))
            {
                return;
            }

            visited.Add(neighbor);
            queue.Enqueue(neighbor);
        }

        private static int SquaredDistance(Vector2Int first, Vector2Int second)
        {
            int dx = first.x - second.x;
            int dz = first.y - second.y;
            return dx * dx + dz * dz;
        }

        private static void AddPathCells(HashSet<Vector2Int> floorCells, IReadOnlyList<Vector2Int> path)
        {
            foreach (Vector2Int cell in path)
            {
                floorCells.Add(cell);
            }
        }

        private static List<Vector2Int> BuildCorridorPath(Vector2Int start, Vector2Int end, System.Random random)
        {
            var path = new List<Vector2Int>();
            bool horizontalFirst = random.Next(2) == 0;
            if (horizontalFirst)
            {
                AddHorizontalPath(path, start.x, end.x, start.y);
                AddVerticalPath(path, start.y, end.y, end.x);
                return path;
            }

            AddVerticalPath(path, start.y, end.y, start.x);
            AddHorizontalPath(path, start.x, end.x, end.y);
            return path;
        }

        private static void AddHorizontalPath(List<Vector2Int> path, int fromX, int toX, int z)
        {
            int step = fromX <= toX ? 1 : -1;
            for (int x = fromX; ; x += step)
            {
                AddPathCell(path, new Vector2Int(x, z));
                if (x == toX)
                {
                    break;
                }
            }
        }

        private static void AddVerticalPath(List<Vector2Int> path, int fromZ, int toZ, int x)
        {
            int step = fromZ <= toZ ? 1 : -1;
            for (int z = fromZ; ; z += step)
            {
                AddPathCell(path, new Vector2Int(x, z));
                if (z == toZ)
                {
                    break;
                }
            }
        }

        private static void AddPathCell(List<Vector2Int> path, Vector2Int cell)
        {
            if (path.Count > 0 && path[path.Count - 1] == cell)
            {
                return;
            }

            path.Add(cell);
        }

        private static Vector3 CalculateCenteredLevelFieldOrigin(HashSet<Vector2Int> floorCells, Vector3 generationOrigin)
        {
            GetCellBounds(floorCells, out Vector2Int minCell, out Vector2Int maxCell);
            const float cellSize = 4f;
            int cellWidth = maxCell.x - minCell.x + 1;
            int cellDepth = maxCell.y - minCell.y + 1;
            float baseX = -(cellWidth * cellSize) * 0.5f;
            float baseZ = -(cellDepth * cellSize) * 0.5f;
            return generationOrigin + new Vector3(baseX - minCell.x * cellSize, 0f, baseZ - minCell.y * cellSize);
        }

        // dungeonSeed is the value that seeded `random`; online synthesis keys its
        // per-gap RNG off it (design decision 18) so gaps stay independent of the
        // shared draw stream.
        private static bool TryBuildAcceptedPlan(
            int dungeonSeed,
            Dictionary<string, int> rejectionHistogram,
            out DungeonLayout layout,
            out TieredLevelPlan levelPlan,
            out RouteIntent acceptedRouteIntent,
            out ElevationEdgeModel.RoomBoundaryContext boundaryContext,
            out DungeonPlanValidation validation,
            out int layoutAttemptsUsed,
            out string rejectionReason)
        {
            layout = default;
            levelPlan = default;
            acceptedRouteIntent = null;
            boundaryContext = null;
            validation = null;
            layoutAttemptsUsed = 0;
            rejectionReason = string.Empty;
            for (int attempt = 0; attempt < LayoutAttemptLimit; attempt++)
            {
                layoutAttemptsUsed = attempt + 1;
                bool routeLayoutBuilt = TryBuildRouteFirstDungeonLayout(
                        dungeonSeed,
                        layoutAttemptsUsed,
                        out DungeonLayout candidateLayout,
                        out RouteTierRequirements routeRequirements,
                        out rejectionReason);
                if (!routeLayoutBuilt)
                {
                    RecordRejection(rejectionHistogram, rejectionReason);
                    continue;
                }

                bool tieredPlanBuilt = TryBuildTieredLevelPlan(
                        candidateLayout,
                        routeRequirements,
                        dungeonSeed,
                        new DungeonRandomScope(dungeonSeed, layoutAttemptsUsed, 0),
                        rejectionHistogram,
                        out layout,
                        out levelPlan,
                        out boundaryContext,
                        out validation,
                        out rejectionReason);
                if (tieredPlanBuilt)
                {
                    acceptedRouteIntent = routeRequirements.intent;
                    return true;
                }
            }

            return false;
        }

        // Plan building is pure data and also runs in headless validation hosts where
        // Unity's native logger is unavailable; planning-stage messages must not hard-fail there.
        private static void LogPlanningWarning(string message)
        {
            try
            {
                Debug.LogWarning(message);
            }
            catch (Exception)
            {
                Console.Error.WriteLine(message);
            }
        }

        private static void RecordRejection(Dictionary<string, int> histogram, string reason)
        {
            if (histogram == null || string.IsNullOrEmpty(reason))
            {
                return;
            }

            // Collapse run-specific numbers (room indices, percentages, cell coords) so
            // identical failure modes aggregate under one key.
            string key = System.Text.RegularExpressions.Regex.Replace(reason, @"-?\d+(\.\d+)?", "#");
            histogram.TryGetValue(key, out int count);
            histogram[key] = count + 1;
        }

        private static string FormatRejectionHistogram(Dictionary<string, int> histogram)
        {
            if (histogram == null || histogram.Count == 0)
            {
                return "none";
            }

            var entries = new List<KeyValuePair<string, int>>(histogram);
            entries.Sort((left, right) =>
            {
                int byCount = right.Value.CompareTo(left.Value);
                return byCount != 0 ? byCount : string.CompareOrdinal(left.Key, right.Key);
            });

            const int maxEntries = 10;
            var parts = new List<string>();
            for (int i = 0; i < entries.Count && i < maxEntries; i++)
            {
                parts.Add($"[{entries[i].Value}x] {entries[i].Key}");
            }

            if (entries.Count > maxEntries)
            {
                parts.Add($"(+{entries.Count - maxEntries} more)");
            }

            return string.Join("; ", parts);
        }

        [ThreadStatic]
        private static ReviewedStairPlacementGeometryCache activeReviewedStairPlacementGeometryCache;

        // The connection-identity violations found on the most recently accepted
        // tier attempt (design §8.1). A diagnostic carried the way the route
        // planner already carries its `last*` diagnostics, so the batch reporter
        // can read it without threading `RouteTierRequirements` through
        // TryBuildAcceptedPlan's whole call graph. Nothing in generation reads it.
        private static List<string> lastConnectionIdentityViolations = new List<string>();

        private static bool TryBuildTieredLevelPlan(
            DungeonLayout layout,
            RouteTierRequirements routeRequirements,
            int dungeonSeed,
            DungeonRandomScope rng,
            Dictionary<string, int> rejectionHistogram,
            out DungeonLayout acceptedLayout,
            out TieredLevelPlan plan,
            out ElevationEdgeModel.RoomBoundaryContext boundaryContext,
            out DungeonPlanValidation validation,
            out string rejectionReason)
        {
            ReviewedStairPlacementGeometryCache previousCache =
                activeReviewedStairPlacementGeometryCache;
            activeReviewedStairPlacementGeometryCache = new ReviewedStairPlacementGeometryCache();
            try
            {
                return TryBuildTieredLevelPlanCore(
                    layout,
                    routeRequirements,
                    dungeonSeed,
                    rng,
                    rejectionHistogram,
                    out acceptedLayout,
                    out plan,
                    out boundaryContext,
                    out validation,
                    out rejectionReason);
            }
            finally
            {
                activeReviewedStairPlacementGeometryCache = previousCache;
            }
        }

        private static bool TryBuildTieredLevelPlanCore(
            DungeonLayout layout,
            RouteTierRequirements routeRequirements,
            int dungeonSeed,
            DungeonRandomScope rng,
            Dictionary<string, int> rejectionHistogram,
            out DungeonLayout acceptedLayout,
            out TieredLevelPlan plan,
            out ElevationEdgeModel.RoomBoundaryContext boundaryContext,
            out DungeonPlanValidation validation,
            out string rejectionReason)
        {
            acceptedLayout = default;
            plan = default;
            boundaryContext = null;
            validation = null;
            rejectionReason = string.Empty;
            if (layout.floorCells == null || layout.floorCells.Count == 0)
            {
                rejectionReason = "layout had no floor cells";
                RecordRejection(rejectionHistogram, rejectionReason);
                return false;
            }

            if (layout.rooms == null || layout.rooms.Count <= 1 || layout.connections == null || layout.connections.Count == 0)
            {
                rejectionReason = "layout did not have enough connected regions for multiple levels";
                RecordRejection(rejectionHistogram, rejectionReason);
                return false;
            }

            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions = LoadReviewedActiveStairOptions();

            for (int attempt = 0; attempt < TierPlacementAttempts; attempt++)
            {
                bool tierAttemptBuilt = TryBuildTieredLevelPlanAttempt(
                        layout,
                        routeRequirements,
                        reviewedStairOptions,
                        dungeonSeed,
                        rng.ForTierAttempt(attempt),
                        out acceptedLayout,
                        out plan,
                        out boundaryContext,
                        out validation,
                        out rejectionReason);
                if (tierAttemptBuilt)
                {
                    lastTierPlacementAttempts = attempt + 1;
                    return true;
                }

                RecordRejection(rejectionHistogram, rejectionReason);
            }

            return false;
        }

        private static bool TryBuildTieredLevelPlanAttempt(
            DungeonLayout layout,
            RouteTierRequirements routeRequirements,
            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions,
            int dungeonSeed,
            DungeonRandomScope rng,
            out DungeonLayout acceptedLayout,
            out TieredLevelPlan plan,
            out ElevationEdgeModel.RoomBoundaryContext boundaryContext,
            out DungeonPlanValidation validation,
            out string rejectionReason)
        {
            acceptedLayout = default;
            plan = default;
            boundaryContext = null;
            validation = null;
            RoomZoneContext zones = RoomZoneContext.Build(layout);
            bool levelsAssigned = TryAssignRoomLevels(
                    layout,
                    zones,
                    routeRequirements,
                    reviewedStairOptions,
                    out int[] zoneLevels,
                    out RouteElevationPolicy archetype,
                    out rejectionReason);
            if (!levelsAssigned)
            {
                return false;
            }

            // Slice 5: the composed RouteIntent already owns its loops. The old
            // late loop pass added unnameable corridors after planning, making
            // accepted architecture contain more relationships than its
            // topology. The embedded layout is now the
            // final connection set.
            DungeonLayout plannedLayout = layout;
            List<string> connectionIdentityViolations =
                FindConnectionIdentityViolations(plannedLayout, routeRequirements);
            lastConnectionIdentityViolations = connectionIdentityViolations;
            if (connectionIdentityViolations.Count > 0)
            {
                rejectionReason =
                    $"[ROUTE_CONNECTION_OWNERSHIP] {connectionIdentityViolations[0]}";
                return false;
            }

            int loopEdges = CountLoopEdges(plannedLayout);
            float latticeFillPercent = LatticeEnvelopeFillPercent(
                plannedLayout.floorCells,
                routeRequirements.latticeEnvelope);
            if (loopEdges <= 0)
            {
                rejectionReason = "floorplan had no loop edges";
                return false;
            }

            if (plannedLayout.rooms.Count < CurrentGenerationSettings.denseFloorplanMinRooms)
            {
                rejectionReason = $"floorplan had only {plannedLayout.rooms.Count} rooms";
                return false;
            }

            // Restate the layout-stage density backstop over the final planned
            // connection set. No later pass is allowed to inflate this number.
            if (latticeFillPercent < CurrentGenerationSettings.minLatticeEnvelopeFillPercent)
            {
                rejectionReason = $"lattice-envelope fill {latticeFillPercent * 100f:0.#}% was below the {CurrentGenerationSettings.minLatticeEnvelopeFillPercent * 100f:0.#}% gate";
                return false;
            }

            // D3: the entry-level table is built ONCE, here, from the complete
            // planned connection set, because two readers must agree about the elevation a
            // corridor meets its room at. Deriving it twice is how the rule
            // stated in two places that cost C2 a rejected corpus gets rebuilt.
            ConnectionEntryLevels entryLevels = ConnectionEntryLevels.Build(
                plannedLayout,
                zones,
                zoneLevels,
                routeRequirements?.intent);

            // D4: the enclosure roll lives in the plan rather than the renderer
            // input stage. Planned bridge placement no longer depends on it,
            // but boundary construction and dressing still share this one draw.
            //
            // The STREAM moves with the roll, not just the call. Its draw
            // sequence is N draws for the rooms, then the boundary context's own
            // `Next()` for its dressing seed, and the two are the same stream
            // instance — so hoisting the roll while leaving the stream behind
            // would re-phase that seed and move every dressed dungeon.
            System.Random enclosureRandom = rng.Stream("enclosed-rooms");
            bool[] plannedEnclosedRooms =
                ChooseEnclosedRooms(plannedLayout.rooms.Count, enclosureRandom);
            bool connectedDeltasValid = TryValidateConnectedRoomLevelDeltas(
                plannedLayout,
                entryLevels,
                reviewedStairOptions,
                out rejectionReason);
            if (!connectedDeltasValid)
            {
                return false;
            }

            bool cellLevelFieldBuilt = TryBuildCellLevelField(
                    plannedLayout,
                    zones,
                    zoneLevels,
                    entryLevels,
                    routeRequirements,
                    reviewedStairOptions,
                    dungeonSeed,
                    rng,
                    out SurfaceField surfaces,
                    out List<ElevationEdgeModel.TransitionEdge> transitions,
                    out RouteTransitionResolution[] routeTransitionResolutions,
                    out string stairCandidateSummary,
                    out List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs,
                    out List<DaisShowpiece> daisShowpieces,
                    out NamedVistaPromontoryResolution[] namedPromontories,
                    out ExternalConnectorPromontoryResolution[] externalConnectors,
                    out RecipeResolution[] recipeResolutions,
                    out PlanOpening[] openings,
                    out PrismLedger prisms,
                    out rejectionReason);
            if (!cellLevelFieldBuilt)
            {
                return false;
            }

            GetLevelRange(surfaces, out int minLevel, out int maxLevel);
            int levelCount = CountDistinctLevels(surfaces);
            if (levelCount <= 1)
            {
                rejectionReason = $"room graph resolved to a single level (archetype {archetype})";
                return false;
            }

            int topologyCeiling = routeRequirements.intent.topology.ceilingLevels;
            if (!TryValidateTransitionLevelDeltas(
                    transitions,
                    topologyCeiling,
                    layout,
                    out rejectionReason))
            {
                return false;
            }

            if (!TryBuildFloorStairPortGraph(
                    surfaces,
                    transitions,
                    openings,
                    out FloorStairPortGraph portGraph,
                    out rejectionReason))
            {
                return false;
            }

            if (!portGraph.IsFallFreeConnected(out string portGraphReachability))
            {
                rejectionReason = portGraphReachability;
                return false;
            }

            if (!TryResolveRouteRequirements(
                    routeRequirements,
                    plannedLayout,
                    surfaces,
                    transitions,
                    routeTransitionResolutions,
                    out RouteRequirementResolution routeRequirementResolution,
                    out rejectionReason))
            {
                return false;
            }

            // Reported stat only (demoted from a hard gate 2026-06-10): route
            // intent now guarantees the vertical story and separately proves its
            // named vista, so this older adjacent-cell proxy remains diagnostic.
            int overlookCount = CountSpatialOverlookEdges(surfaces, transitions);

            plan = new TieredLevelPlan(
                surfaces,
                transitions,
                levelCount,
                minLevel,
                maxLevel,
                FormatRoomsPerTier(CountRoomsPerTier(zoneLevels, topologyCeiling)),
                overlookCount,
                FormatTransitionSummary(transitions),
                FormatStairUsageHistogram(transitions),
                FormatStairTopologyHistogram(transitions, reviewedStairOptions),
                FormatStairPlacementClassHistogram(transitions),
                stairCandidateSummary,
                portGraph.Summary,
                plannedLayout.connectorCandidateCount,
                archetype.ToString(),
                synthesizedStairs,
                FormatSynthesizedStairSummary(synthesizedStairs),
                daisShowpieces,
                namedPromontories,
                externalConnectors,
                recipeResolutions,
                openings,
                routeRequirementResolution,
                prisms,
                topologyCeiling);

            // The single acceptance gate. The boundary context is built here (not
            // after acceptance) so it is constructed exactly once: it is both a
            // validation input and a renderer input, and building it twice would
            // draw twice from the shared stream. No RNG is consumed between this
            // point and the old construction site, so the draw sequence for an
            // accepted plan is unchanged.
            bool boundaryValid = TryBuildRoomBoundaryContext(
                plannedLayout,
                surfaces,
                transitions,
                plan.prisms,
                routeRequirements?.recipes,
                plannedEnclosedRooms,
                enclosureRandom,
                out boundaryContext,
                out string boundaryMessage);
            validation = ValidateDungeonPlan(
                dungeonSeed,
                plannedLayout,
                plan,
                routeRequirements.intent,
                boundaryValid,
                boundaryMessage);
            if (!validation.passed)
            {
                rejectionReason = validation.FirstFailure();
                plan = default;
                return false;
            }

            acceptedLayout = plannedLayout;
            return true;
        }


        private static Vector2 EdgeCenterInCellSpace(Vector2Int cell, int direction)
        {
            Vector2 center = new Vector2(cell.x * 4f + 2f, cell.y * 4f + 2f);
            return center + DirectionVector(direction) * 2f;
        }

        private static Vector2Int GridCellFromPlanPoint(Vector2 point)
        {
            const float boundaryBias = 0.001f;
            return new Vector2Int(
                Mathf.FloorToInt((point.x + boundaryBias) / 4f),
                Mathf.FloorToInt((point.y + boundaryBias) / 4f));
        }

        private static bool PathTouchesProtectedCells(
            IReadOnlyList<Vector2Int> path,
            IReadOnlyCollection<Vector2Int> protectedCells)
        {
            if (path == null || protectedCells == null || protectedCells.Count == 0)
            {
                return false;
            }

            var lookup = protectedCells as HashSet<Vector2Int> ??
                new HashSet<Vector2Int>(protectedCells);
            foreach (Vector2Int cell in path)
            {
                if (lookup.Contains(cell))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool PathTouchesExistingFloorOutsideEndpointRooms(
            IReadOnlyList<Vector2Int> path,
            HashSet<Vector2Int> floorCells,
            RoomFootprint firstRoom,
            RoomFootprint secondRoom)
        {
            foreach (Vector2Int cell in path)
            {
                if (!floorCells.Contains(cell) ||
                    firstRoom.Contains(cell) ||
                    secondRoom.Contains(cell))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Does this corridor punch through a room that is not one of its own
        /// endpoints?
        /// </summary>
        /// <remarks>
        /// <para>
        /// The harm the rule names is "an undeclared doorway and an unowned
        /// threshold", and a corridor passing OVER a room at a different
        /// elevation creates neither — which is the design's licence to relax it
        /// (§8.1). The relaxation is authorized by the connection being
        /// layer-bound and decided by the absolute bands, never by layer names.
        /// </para>
        /// <para>
        /// It relaxes UPWARD only, and that is a measured limit rather than
        /// timidity. Passing under a room would have to suspend the room's own
        /// floor, and a room floor is ground-backed by construction: take its
        /// mass away and the boundary decomposition stops giving it walls. So a
        /// corridor must clear the room's ground (index 0 of its declared
        /// elevations) as well as miss every storey the room declares.
        /// </para>
        /// </remarks>
        private static bool PathCrossesThirdRoom(
            IReadOnlyList<Vector2Int> path,
            IReadOnlyList<RoomFootprint> rooms,
            int firstRoom,
            int secondRoom,
            LevelBand plannedBand = default,
            bool layerBound = false,
            int[][] roomDeclaredElevations = null)
        {
            for (int i = 0; i < rooms.Count; i++)
            {
                if (i == firstRoom || i == secondRoom)
                {
                    continue;
                }

                RoomFootprint room = rooms[i];
                foreach (Vector2Int cell in path)
                {
                    if (!room.Contains(cell))
                    {
                        continue;
                    }

                    if (!CorridorClearsRoomVertically(plannedBand, layerBound, roomDeclaredElevations, i))
                    {
                        return true;
                    }

                    break;
                }
            }

            return false;
        }

        private static bool CorridorClearsRoomVertically(
            LevelBand plannedBand,
            bool layerBound,
            int[][] roomDeclaredElevations,
            int room)
        {
            if (roomDeclaredElevations == null || room >= roomDeclaredElevations.Length)
            {
                return false;
            }

            return CorridorClearsRoomVertically(
                plannedBand,
                layerBound,
                roomDeclaredElevations[room]);
        }

        /// <summary>
        /// May a corridor in this band pass over a room standing at these
        /// declared elevations?
        /// </summary>
        /// <remarks>
        /// One predicate, two callers, deliberately: `PathCrossesThirdRoom` at
        /// generation time and the topology validator's lattice-lane rule at
        /// author time. §8.1 asks the two to relax "the same way and on the same
        /// absolute comparison", and the C2 incident that cost a whole corpus
        /// was a validator and a candidate gate stating the same rule twice and
        /// disagreeing.
        /// <para>
        /// Index 0 must be the room's BASE. The band has to clear it, not merely
        /// miss it: passing UNDER would suspend the room's own floor, and a room
        /// floor is ground-backed by construction.
        /// </para>
        /// </remarks>
        private static bool CorridorClearsRoomVertically(
            LevelBand plannedBand,
            bool layerBound,
            int[] declaredElevations)
        {
            if (!layerBound || declaredElevations == null || declaredElevations.Length == 0)
            {
                return false;
            }

            if (plannedBand.minLevel <= declaredElevations[0])
            {
                return false;
            }

            foreach (int elevation in declaredElevations)
            {
                if (plannedBand.Intersects(
                        new LevelBand(elevation, elevation + MinHeadroomLevels)))
                {
                    return false;
                }
            }

            return true;
        }

        private static string RoomPairKey(int firstRoom, int secondRoom)
        {
            int min = Mathf.Min(firstRoom, secondRoom);
            int max = Mathf.Max(firstRoom, secondRoom);
            return $"{min}:{max}";
        }

        private static bool TryAssignRoomLevels(
            DungeonLayout layout,
            RoomZoneContext zones,
            RouteTierRequirements routeRequirements,
            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions,
            out int[] zoneLevels,
            out RouteElevationPolicy archetype,
            out string rejectionReason)
        {
            rejectionReason = string.Empty;
            archetype = routeRequirements?.intent.elevationPolicy ?? RouteElevationPolicy.AscendingSpine;
            zoneLevels = new int[zones.nodeCount];
            if (routeRequirements?.intent == null ||
                routeRequirements.intent.nodes == null ||
                routeRequirements.intent.nodes.Length != layout.rooms.Count)
            {
                rejectionReason = "[ROUTE_ELEVATION_REQUIREMENT] tier planner did not receive one route elevation requirement per room";
                return false;
            }

            int topologyCeiling = routeRequirements.intent.topology.ceilingLevels;

            // Route intent is the active constraint on the retained ascending-spine
            // elevation policy. Rooms sit on its declared 4u-major story; the
            // existing +1u split policy remains a strictly intraroom accent.
            for (int roomIndex = 0; roomIndex < layout.rooms.Count; roomIndex++)
            {
                int requiredLevel = routeRequirements.intent.nodes[roomIndex].relativeElevationLevels;
                if (requiredLevel < 0 || requiredLevel > topologyCeiling ||
                    !IsStructuralLevel(requiredLevel))
                {
                    rejectionReason = $"[ROUTE_ELEVATION_REQUIREMENT] room {roomIndex} declared invalid relative level {requiredLevel}";
                    return false;
                }

                zoneLevels[roomIndex] = requiredLevel;
                int raisedNode = zones.RaisedNodeOfRoom(roomIndex);
                if (raisedNode < 0)
                {
                    continue;
                }

                if (requiredLevel + 1 > topologyCeiling)
                {
                    rejectionReason = $"[ROUTE_ELEVATION_REQUIREMENT] room {roomIndex} could not reserve its +1u zone below the level cap";
                    return false;
                }

                zoneLevels[raisedNode] = requiredLevel + 1;
            }

            foreach (RouteTraversalIntent edge in routeRequirements.intent.traversalEdges)
            {
                // D3: a bound edge resolves at its LAYER's elevation, so the
                // rise is measured between the two ENTRY levels rather than
                // between the two node levels. `requiredRiseLevels` was derived
                // from the same two bound elevations in D1, so an edge that
                // binds nothing compares exactly what it compared before.
                int fromEntryLevel = zoneLevels[edge.fromNode] +
                    routeRequirements.intent.nodes[edge.fromNode].LayerOffset(edge.fromLayerId);
                int toEntryLevel = zoneLevels[edge.toNode] +
                    routeRequirements.intent.nodes[edge.toNode].LayerOffset(edge.toLayerId);
                int actualRise = toEntryLevel - fromEntryLevel;
                if (actualRise != edge.requiredRiseLevels)
                {
                    rejectionReason = $"[ROUTE_ELEVATION_REQUIREMENT] edge '{edge.id}' resolved rise {actualRise}u instead of {edge.requiredRiseLevels}u";
                    return false;
                }

                int absRise = Mathf.Abs(actualRise);
                if (edge.transitionKind == RouteTransitionKind.LevelCorridor && absRise != 0 ||
                    edge.transitionKind != RouteTransitionKind.LevelCorridor &&
                    (absRise != MajorRiseLevels && absRise != DoubleMajorRiseLevels))
                {
                    rejectionReason = $"[ROUTE_ELEVATION_REQUIREMENT] edge '{edge.id}' resolved an incompatible {edge.transitionKind} rise of {absRise}u";
                    return false;
                }

                if (absRise > 0 &&
                    !HasReviewedActiveStairOption(reviewedStairOptions, absRise, maxLaneCount: MaxActiveStairLaneCount))
                {
                    rejectionReason = $"[ROUTE_ELEVATION_REQUIREMENT] edge '{edge.id}' had no reviewed transition contract for rise {absRise}u";
                    return false;
                }
            }

            return true;
        }

        private static List<int>[] BuildRoomAdjacency(int roomCount, IReadOnlyList<RoomConnection> connections)
        {
            var adjacency = new List<int>[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                adjacency[i] = new List<int>();
            }

            foreach (RoomConnection connection in connections)
            {
                if (connection.fromRoom < 0 || connection.fromRoom >= roomCount ||
                    connection.toRoom < 0 || connection.toRoom >= roomCount)
                {
                    continue;
                }

                adjacency[connection.fromRoom].Add(connection.toRoom);
                adjacency[connection.toRoom].Add(connection.fromRoom);
            }

            return adjacency;
        }

        private static bool TryValidateConnectedRoomLevelDeltas(
            DungeonLayout layout,
            ConnectionEntryLevels entryLevels,
            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions,
            out string rejectionReason)
        {
            for (int index = 0; index < layout.connections.Count; index++)
            {
                RoomConnection connection = layout.connections[index];
                // Corridor deltas are measured between the elevations the
                // connection ENTERS its two rooms at — the threshold zone's
                // level plus whatever storey that end bound (D3). Decision A: a
                // flat corridor needs no stair; every structural transition is
                // a 4u/8u major and needs a reviewed contract with that exact
                // rise. The reviewed 2u physical stair remains available to
                // room-local recipe geometry, but cannot reconcile rooms.
                entryLevels.Resolve(index, out int fromLevel, out int toLevel);
                int delta = Mathf.Abs(fromLevel - toLevel);
                if (!AreStructuralConnectionLevels(fromLevel, toLevel))
                {
                    rejectionReason =
                        $"connected regions {connection.fromRoom} and {connection.toRoom} met at " +
                        $"non-structural levels {fromLevel} and {toLevel} (delta {delta})";
                    return false;
                }

                if (delta == 0)
                {
                    continue;
                }

                if (HasReviewedActiveStairOption(reviewedStairOptions, delta, maxLaneCount: MaxActiveStairLaneCount))
                {
                    continue;
                }

                rejectionReason = $"connected regions {connection.fromRoom} and {connection.toRoom} differed by {delta} levels";
                return false;
            }

            rejectionReason = string.Empty;
            return true;
        }

        // A +1u RoomZonePlan is local geometry owned by exactly one room. The
        // producer already chooses the threshold-free side to raise; acceptance
        // restates that ownership over the finished DungeonLayout so a malformed
        // fixture or later producer cannot turn the local offset into an
        // inter-room threshold.
        private static bool TryValidateRoomLocalElevationOwnership(
            DungeonLayout layout,
            out string rejectionReason)
        {
            foreach (RoomZonePlan zone in layout.roomZones ?? Array.Empty<RoomZonePlan>())
            {
                if (zone.roomIndex < 0 || zone.roomIndex >= layout.rooms.Count ||
                    zone.lowerCells == null || zone.lowerCells.Count == 0 ||
                    zone.raisedCells == null || zone.raisedCells.Count == 0)
                {
                    rejectionReason =
                        "room-local elevation declared an invalid or empty owning room";
                    return false;
                }

                RoomFootprint owner = layout.rooms[zone.roomIndex];
                var ownedCells = new HashSet<Vector2Int>();
                foreach (Vector2Int cell in zone.lowerCells)
                {
                    if (!owner.Contains(cell) || !ownedCells.Add(cell))
                    {
                        rejectionReason =
                            $"room-local elevation in room {zone.roomIndex} crossed its ownership boundary at {cell}";
                        return false;
                    }
                }

                foreach (Vector2Int cell in zone.raisedCells)
                {
                    if (!owner.Contains(cell) || !ownedCells.Add(cell))
                    {
                        rejectionReason =
                            $"room-local elevation in room {zone.roomIndex} crossed its ownership boundary at {cell}";
                        return false;
                    }
                }

                if (ownedCells.Count != owner.Area)
                {
                    rejectionReason =
                        $"room-local elevation in room {zone.roomIndex} did not partition its one owning room";
                    return false;
                }

                foreach (RoomConnection connection in layout.connections)
                {
                    bool isFrom = connection.fromRoom == zone.roomIndex;
                    bool isTo = connection.toRoom == zone.roomIndex;
                    if (!isFrom && !isTo)
                    {
                        continue;
                    }

                    Vector2Int threshold = ThresholdCell(
                        owner,
                        connection.path,
                        forward: isFrom);
                    if (zone.raisedCells.Contains(threshold))
                    {
                        rejectionReason =
                            $"room-local elevation in room {zone.roomIndex} owned external threshold {threshold}";
                        return false;
                    }
                }
            }

            rejectionReason = string.Empty;
            return true;
        }

        private static bool TryBuildCellLevelField(
            DungeonLayout layout,
            RoomZoneContext zones,
            int[] zoneLevels,
            ConnectionEntryLevels entryLevels,
            RouteTierRequirements routeRequirements,
            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions,
            int dungeonSeed,
            DungeonRandomScope rng,
            out SurfaceField surfaces,
            out List<ElevationEdgeModel.TransitionEdge> transitions,
            out RouteTransitionResolution[] routeTransitionResolutions,
            out string stairCandidateSummary,
            out List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs,
            out List<DaisShowpiece> daisShowpieces,
            out NamedVistaPromontoryResolution[] namedPromontories,
            out ExternalConnectorPromontoryResolution[] externalConnectors,
            out RecipeResolution[] recipeResolutions,
            out PlanOpening[] openings,
            out PrismLedger prisms,
            out string rejectionReason)
        {
            // The elevation stage's canonical container is the surface field
            // itself, not a heightfield the plan re-wraps afterwards (design
            // §8.2's C2 prerequisite). Every write below goes through one of the
            // field's three named writers, and after C2b-2 every reader takes
            // the field or the transition edge's own recorded levels. No reader
            // resolves an elevation by looking a transition's cell up in the
            // level field any more, which is what a stacked column made
            // ambiguous.
            surfaces = new SurfaceField(new Dictionary<Vector2Int, int>());
            transitions = new List<ElevationEdgeModel.TransitionEdge>();
            prisms = new PrismLedger();
            routeTransitionResolutions = Array.Empty<RouteTransitionResolution>();
            stairCandidateSummary = "[]";
            synthesizedStairs = new List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)>();
            daisShowpieces = new List<DaisShowpiece>();
            namedPromontories = Array.Empty<NamedVistaPromontoryResolution>();
            externalConnectors = Array.Empty<ExternalConnectorPromontoryResolution>();
            recipeResolutions = Array.Empty<RecipeResolution>();
            openings = Array.Empty<PlanOpening>();
            rejectionReason = string.Empty;

            for (int roomIndex = 0; roomIndex < layout.rooms.Count; roomIndex++)
            {
                foreach (Vector2Int cell in layout.rooms[roomIndex].CellsRowMajor())
                {
                    if (!surfaces.TrySetFloorLevel(
                            cell,
                            zoneLevels[roomIndex],
                            out rejectionReason))
                    {
                        return false;
                    }
                }
            }

            var transitionKeys = new HashSet<string>();
            var stairCandidateCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            // The accepted plan carries this same ledger, so the acceptance gate
            // runs the SAME headroom rule over the SAME reservations the planner
            // enforced (design §13 Phase B) rather than re-deriving them from the
            // finished transition list.
            PrismLedger plannedStairLedger = prisms;
            var protectedStructuralCells = new HashSet<Vector2Int>();
            if (routeRequirements?.reservedVistaCells != null)
            {
                protectedStructuralCells.UnionWith(routeRequirements.reservedVistaCells);
                // Publish the generated sight volume through the same reserved-
                // void mechanism used by authored atria before any stair/bridge/
                // stairwell candidate is chosen.
                RegisterReservedVistaOpenVolume(
                    plannedStairLedger,
                    SortedCells(routeRequirements.reservedVistaCells).ToArray());

                // The two facing boundary cells are the final-view anchors. Treat
                // them as shareable landings: route stairs may land there, while
                // stair bodies and later structural passes cannot consume or re-level
                // either endpoint.
                plannedStairLedger.Register(
                    FinalViewAnchorsOwner,
                    Array.Empty<Vector2Int>(),
                    new[] { routeRequirements.vistaSourceCell },
                    new[] { routeRequirements.vistaTargetCell });
            }

            if (routeRequirements?.namedPromontoryCells != null &&
                routeRequirements.namedPromontoryCells.Length > 0)
            {
                protectedStructuralCells.UnionWith(routeRequirements.namedPromontoryCells);
                plannedStairLedger.Register(
                    new OwnerKey(OwnerFamily.Promontory, "named-vista"),
                    routeRequirements.namedPromontoryCells,
                    Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>());
            }

            var resolvedRouteTransitions = new List<RouteTransitionResolution>();
            var externalSpanGapCells = new HashSet<Vector2Int>();

            // New rule (user review 2026-06-11): a 1u step strip must never sit in
            // a doorway cell — a step half-blocking a room entrance disrupts flow
            // and reads wrong. Both cells of every door edge are off limits for
            // seam and corridor strips alike (unfiltered: every path crossing
            // counts, leveled or not).
            var doorwayCells = new HashSet<Vector2Int>();
            foreach (ElevationEdgeModel.DoorwayEdge doorway in BuildDoorwayEdges(layout, surfaces: null))
            {
                doorwayCells.Add(doorway.firstCell);
                doorwayCells.Add(doorway.secondCell);
            }

            string seamStairPrefabPath = ResolveSeamStairPrefabPath();
            if (!TryRealizeRecipes(
                    routeRequirements?.recipes,
                    layout,
                    surfaces,
                    transitions,
                    transitionKeys,
                    plannedStairLedger,
                    seamStairPrefabPath,
                    daisShowpieces,
                    out Dictionary<string, int> recipeBaseLevels,
                    out rejectionReason))
            {
                return false;
            }

            // Slice 3: authored modules retain first claim on their exact
            // footprint. Every unclaimed node with declared storeys is then
            // realized directly from its existing room footprint and bound
            // thresholds into the same SurfaceField and PrismLedger.
            var generatedOpeningCandidates = new List<PlanOpening>();
            if (!TryRealizeGenericStructuralRoomLayers(
                    layout,
                    routeRequirements?.intent?.nodes,
                    routeRequirements?.recipes,
                    surfaces,
                    plannedStairLedger,
                    generatedOpeningCandidates,
                    out rejectionReason))
            {
                return false;
            }

            // RoomZonePlan is a local +1u finish, not structural ownership.
            // Apply it only after recipe and generated structural storeys have
            // published their surfaces. Storeyed generic rooms are excluded
            // from zone splitting at layout time, just as recipe rooms are.
            if (!TryApplyRoomZoneLocalFinishing(
                    layout,
                    zones,
                    zoneLevels,
                    surfaces,
                    out rejectionReason))
            {
                return false;
            }

            foreach (RecipePlacement recipePlacement in routeRequirements.recipes)
            {
                protectedStructuralCells.UnionWith(recipePlacement.roomCells);
            }

            AddZoneSeamStepStrips(
                layout,
                surfaces,
                doorwayCells,
                plannedStairLedger,
                transitionKeys,
                transitions,
                seamStairPrefabPath);

            for (int connectionIndex = 0; connectionIndex < layout.connections.Count; connectionIndex++)
            {
                if (!TryResolveConnectionTransition(
                        layout.connections[connectionIndex],
                        connectionIndex,
                        layout,
                        zones,
                        entryLevels,
                        routeRequirements,
                        reviewedStairOptions,
                        dungeonSeed,
                        rng,
                        surfaces,
                        transitions,
                        transitionKeys,
                        plannedStairLedger,
                        stairCandidateCounts,
                        doorwayCells,
                        externalSpanGapCells,
                        synthesizedStairs,
                        resolvedRouteTransitions,
                        seamStairPrefabPath,
                        out rejectionReason))
                {
                    return false;
                }
            }

            int unreachedFilledCells = FillUnassignedFloorCells(
                layout.floorCells,
                surfaces,
                externalSpanGapCells);
            if (unreachedFilledCells > 0)
            {
                // Silent repair made loud, but deliberately NOT a rejection: this
                // has never been measured firing, so promoting it to a hard gate
                // without evidence could reject seeds that render correctly today.
                // If a sweep shows it firing, that is the evidence needed to turn
                // it into a rejection.
                LogPlanningWarning(
                    $"[LEVEL_FIELD_UNREACHED] {unreachedFilledCells} floor cell(s) had no leveled cardinal " +
                    "neighbour and fell back to level 0. Their elevation is a guess, not a plan.");
            }

            // Decision 43(a): runs after every other level-field feature so
            // it sweeps the FINAL field.
            int sweep1uCount = SweepIntraRoom1uDrops(
                layout,
                surfaces,
                transitions,
                transitionKeys,
                plannedStairLedger,
                doorwayCells,
                seamStairPrefabPath);

            // Phase 6e: realize the route-planned source-side prefix atomically
            // after every other level-field feature. The canonical resolution
            // names the vista and target; no random room or direction is rolled.
            if (!TryResolveNamedVistaPromontory(
                routeRequirements,
                surfaces,
                out namedPromontories,
                out rejectionReason))
            {
                return false;
            }

            List<Vector2Int> promontoryCells = CollectNamedPromontoryCells(namedPromontories);

            if (!TryValidateResolvedRecipes(
                    routeRequirements.recipes,
                    layout,
                    surfaces,
                    transitions,
                    daisShowpieces,
                    promontoryCells,
                    recipeBaseLevels,
                    out recipeResolutions,
                    out rejectionReason))
            {
                return false;
            }

            // Mandatory long, straight external promontories are the final plan
            // mutation. Their hash-isolated policy consumes no
            // shared random and cannot change existing bridge, stair, recipe,
            // sweep, or scenic placement.
            if (!TryResolveExternalConnectorPromontories(
                    dungeonSeed,
                    layout,
                    surfaces,
                    transitions,
                    protectedStructuralCells,
                    doorwayCells,
                    plannedStairLedger,
                    namedPromontories,
                    out externalConnectors,
                    out rejectionReason))
            {
                return false;
            }

            if (!TryBuildPlanOpenings(
                    externalConnectors,
                    routeRequirements?.recipes,
                    generatedOpeningCandidates,
                    surfaces,
                    transitions,
                    out openings,
                    out rejectionReason))
            {
                return false;
            }

            // A2 of the layered-topology design (§3.1, §13): shadow agreement.
            // External promontories are the final plan mutation, so this is "the
            // end of planning" — the one point where every surface exists and no
            // pass reads the shadow again. See ReconcilePlanShadowWithSurfaces
            // for why the repair cannot live at the individual producers.
            ReconcilePlanShadowWithSurfaces(layout, surfaces);

            // Phase B, review finding H3 — THE LATE-PASS ORDERING HAZARD.
            //
            // This gate used to run immediately after FillUnassignedFloorCells,
            // with THREE passes still to come that mutate the level field:
            // SweepIntraRoom1uDrops, TryResolveNamedVistaPromontory and
            // TryResolveExternalConnectorPromontories. A promontory pier landing
            // under a deck therefore arrived after its own clearance had been
            // checked, and the only thing that caught it was a SECOND, separately
            // written headroom check over the accepted plan in `.Batch.cs` —
            // a duplicate formula guarding a gate that had run too early.
            //
            // It now runs where a gate belongs: after the last mutation it must
            // see. The accepted-plan check is the same call over the same ledger,
            // so the two cannot drift apart again.
            if (!plannedStairLedger.TryValidateSurfaceHeadroom(
                    surfaces,
                    out rejectionReason))
            {
                return false;
            }

            stairCandidateSummary = FormatStairCandidateHistogram(stairCandidateCounts);

            if (namedPromontories.Length > 0)
            {
                stairCandidateSummary += $" namedPromontory:{namedPromontories.Length}";
            }

            foreach (DaisShowpiece showpiece in daisShowpieces)
            {
                stairCandidateSummary += $" showpiece:{showpiece.designName}@{showpiece.originCell.x}_{showpiece.originCell.y}";
            }

            if (sweep1uCount > 0)
            {
                stairCandidateSummary += $" sweep1u:{sweep1uCount}";
            }

            routeTransitionResolutions = resolvedRouteTransitions.ToArray();

            return true;
        }

        private static bool TryApplyRoomZoneLocalFinishing(
            DungeonLayout layout,
            RoomZoneContext zones,
            IReadOnlyList<int> zoneLevels,
            SurfaceField surfaces,
            out string rejectionReason)
        {
            foreach (RoomZonePlan zone in layout.roomZones ?? Array.Empty<RoomZonePlan>())
            {
                foreach (Vector2Int cell in SortedCells(zone.raisedCells))
                {
                    int raisedLevel = zoneLevels[zones.NodeOfCell(zone.roomIndex, cell)];
                    if (!surfaces.TryGetFloorLevel(cell, out int currentLevel))
                    {
                        rejectionReason =
                            $"[ROOM_ZONE_LOCAL_FINISH] room {zone.roomIndex} had no floor at {cell}";
                        return false;
                    }

                    if (currentLevel != raisedLevel)
                    {
                        surfaces.RelevelFloor(cell, raisedLevel);
                    }
                }
            }

            rejectionReason = string.Empty;
            return true;
        }

        // Step 4 of the level field, in deterministic room order: every adjacent
        // cell pair across a zone seam carries a rise-1 step strip, so the 1u
        // delta is never freely walkable (design decision 3). The strip's geometry
        // sits in the lower cell, so that cell registers as FOOTPRINT — landings
        // may share other landings but never a footprint, which keeps contract
        // stair landings (and footprints) off the steps. The raised cell is clean
        // floor and stays a shareable landing.
        private static void AddZoneSeamStepStrips(
            DungeonLayout layout,
            SurfaceField surfaces,
            HashSet<Vector2Int> doorwayCells,
            PrismLedger plannedStairLedger,
            HashSet<string> transitionKeys,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            string seamStairPrefabPath)
        {
            foreach (RoomZonePlan zonePlan in layout.roomZones)
            {
                foreach ((Vector2Int lowerCell, Vector2Int raisedCell) in zonePlan.SeamCellPairs())
                {
                    if (!layout.floorCells.Contains(lowerCell) || !layout.floorCells.Contains(raisedCell))
                    {
                        continue;
                    }

                    // No strip in a doorway cell (rule above): the skipped pair
                    // keeps its closed 1u face per the ledge policy; the seam's
                    // other strips carry the zone connectivity.
                    if (doorwayCells.Contains(lowerCell) || doorwayCells.Contains(raisedCell))
                    {
                        continue;
                    }

                    if (plannedStairLedger.BlocksTransitionMouth(lowerCell) ||
                        plannedStairLedger.BlocksTransitionMouth(raisedCell))
                    {
                        continue;
                    }

                    string key = TransitionKey(raisedCell, lowerCell);
                    if (!transitionKeys.Add(key))
                    {
                        continue;
                    }

                    transitions.Add(new ElevationEdgeModel.TransitionEdge(
                        raisedCell,
                        TransitionEndpointLevel(surfaces, raisedCell),
                        lowerCell,
                        TransitionEndpointLevel(surfaces, lowerCell),
                        seamStairPrefabPath,
                        SeamStairPlacementClass));
                    plannedStairLedger.Register(
                        new OwnerKey(OwnerFamily.Transition, $"zone-seam-strip:{key}"),
                        new[] { lowerCell },
                        Array.Empty<Vector2Int>(),
                        new[] { raisedCell },
                        new[] { lowerCell, raisedCell },
                        Array.Empty<Vector2Int>(),
                        Array.Empty<Vector2Int>());
                }
            }
        }

        // Step 5 of the level field, and the one that used to make the whole
        // method unreadable: resolve one corridor connection into leveled cells
        // plus its transition, trying reviewed stair contracts, then online
        // synthesis, then a stairwell tower.
        private static bool TryResolveConnectionTransition(
            RoomConnection connection,
            int connectionIndex,
            DungeonLayout layout,
            RoomZoneContext zones,
            ConnectionEntryLevels entryLevels,
            RouteTierRequirements routeRequirements,
            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions,
            int dungeonSeed,
            DungeonRandomScope rng,
            SurfaceField surfaces,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            HashSet<string> transitionKeys,
            PrismLedger plannedStairLedger,
            SortedDictionary<string, int> stairCandidateCounts,
            HashSet<Vector2Int> doorwayCells,
            HashSet<Vector2Int> externalSpanGapCells,
            List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs,
            List<RouteTransitionResolution> resolvedRouteTransitions,
            string seamStairPrefabPath,
            out string rejectionReason)
        {
            rejectionReason = string.Empty;
            ResolveConnectionNodes(zones, layout.rooms, connection, out int fromNode, out int toNode);
            // D3: the elevation this corridor MEETS each room at, which is the
            // threshold zone's level for an unbound end and the bound storey's
            // for a layer-bound one. Everything below — the delta, the directed
            // rise, the stair search, the levels written into the corridor's own
            // cells — is derived from these two numbers, so binding a layer moves
            // the whole resolution rather than one term of it.
            entryLevels.Resolve(connectionIndex, out int fromLevel, out int toLevel);
            int delta = Mathf.Abs(fromLevel - toLevel);
            RouteTraversalIntent routeTransitionRequirement = default;
            // Optional BY DESIGN: a synthesized loop corridor carries no route
            // intent, and demanding one here would reject every loop in the
            // corpus. The lookup is now by edge id, so it resolves this
            // connection's own edge rather than whichever edge happened to share
            // its room pair.
            bool hasRouteRequirement = routeRequirements != null &&
                routeRequirements.TryGetTransition(
                    connection.edgeId,
                    out routeTransitionRequirement);
            if (hasRouteRequirement)
            {
                int directedRise = routeTransitionRequirement.fromNode == connection.fromRoom
                    ? toLevel - fromLevel
                    : fromLevel - toLevel;
                bool kindMatchesRise =
                    routeTransitionRequirement.transitionKind == RouteTransitionKind.LevelCorridor
                        ? delta == 0
                        : delta == Mathf.Abs(routeTransitionRequirement.requiredRiseLevels) && delta > 1;
                if (directedRise != routeTransitionRequirement.requiredRiseLevels || !kindMatchesRise)
                {
                    rejectionReason =
                        $"[ROUTE_ELEVATION_REQUIREMENT] edge '{routeTransitionRequirement.id}' resolved {directedRise}u/{delta}u for {routeTransitionRequirement.transitionKind}";
                    return false;
                }
            }

            if (delta > PrimaryStairRiseLevels &&
                !HasReviewedActiveStairOption(reviewedStairOptions, delta, maxLaneCount: MaxActiveStairLaneCount))
            {
                rejectionReason = $"connection {connection.fromRoom}->{connection.toRoom} exceeded the primary rise without a reviewed active stair contract";
                return false;
            }

            List<Vector2Int> path = CleanPath(connection.path, layout.floorCells);
            if (path.Count < 2)
            {
                rejectionReason = $"connection {connection.fromRoom}->{connection.toRoom} had no usable corridor path";
                return false;
            }

            if (!ValidatePathCardinality(path, out rejectionReason))
            {
                return false;
            }

            int transitionIndex = -1;
            Vector2Int lowerLandingCell = default;
            Vector2Int upperLandingCell = default;
            ReviewedActiveStairOption stairOption = default;
            Vector2Int[] stairOptionPlannedLowerLandingCells = Array.Empty<Vector2Int>();
            Vector2Int[] stairOptionPlannedUpperLandingCells = Array.Empty<Vector2Int>();
            Vector2Int[] stairOptionPlannedFootprintCells = Array.Empty<Vector2Int>();
            Vector2Int stairOptionPlannedTransitionFirstCell = default;
            Vector2Int stairOptionPlannedTransitionSecondCell = default;
            int stairOptionPlannedLowerPortDirection = 0;
            int stairOptionPlannedUpperPortDirection = 0;
            string stairOptionPlacementClass = EmbeddedStairPlacementClass;
            ElevationEdgeModel.SynthesizedStairSetPiece synthesizedSetPiece = null;
            string synthesizedGapId = string.Empty;
            string requiredPlacementClass = !hasRouteRequirement
                ? string.Empty
                : routeTransitionRequirement.transitionKind == RouteTransitionKind.Bridge
                    ? ExternalSpanStairPlacementClass
                    : routeTransitionRequirement.transitionKind == RouteTransitionKind.Stairwell
                        ? StairwellStairPlacementClass
                        : routeTransitionRequirement.transitionKind == RouteTransitionKind.Stair
                            ? EmbeddedStairPlacementClass
                            : string.Empty;
            // A 1u corridor delta is closed by a single embedded step strip
            // (design decision 3) rather than a reviewed stair contract.
            bool corridorStepStrip = delta == 1;
            if (corridorStepStrip &&
                !TryChooseCorridorStepIndex(
                    path,
                    layout.rooms[connection.fromRoom],
                    layout.rooms[connection.toRoom],
                    doorwayCells,
                    plannedStairLedger,
                    fromLevel <= toLevel,
                    out transitionIndex))
            {
                rejectionReason = $"connection {connection.fromRoom}->{connection.toRoom} had no corridor cell pair for a 1u step strip";
                return false;
            }

            if (delta > 1)
            {
                // The stair search is a pure reader of the surface field. It
                // asks two things: "is this cell void" for a footprint, and "is
                // this cell free for a landing at level L" — both of which the
                // field answers directly, so the heightfield view is gone.
                ZoneArea fromNodeArea = zones.NodeArea(layout.rooms, fromNode);
                ZoneArea toNodeArea = zones.NodeArea(layout.rooms, toNode);
                bool reviewedStairChosen = TryChooseReviewedActiveStairTransition(
                    reviewedStairOptions,
                    delta,
                    maxLaneCount: MaxActiveStairLaneCount,
                    path,
                    fromNodeArea,
                    toNodeArea,
                    layout.floorCells,
                    surfaces,
                    fromLevel,
                    toLevel,
                    // One stream per connection: a neighbouring corridor's
                    // candidate count can no longer change this stair.
                    rng.Stream("stair-choice", connection.RngSubject),
                    allowExternalSpan: true,
                    requiredPlacementClass,
                    stairCandidateCounts,
                    plannedStairLedger,
                    out transitionIndex,
                    out lowerLandingCell,
                    out upperLandingCell,
                    out stairOptionPlannedLowerLandingCells,
                    out stairOptionPlannedUpperLandingCells,
                    out stairOptionPlannedFootprintCells,
                    out stairOptionPlannedTransitionFirstCell,
                    out stairOptionPlannedTransitionSecondCell,
                    out stairOptionPlannedLowerPortDirection,
                    out stairOptionPlannedUpperPortDirection,
                    out stairOptionPlacementClass,
                    out stairOption);
                if (!reviewedStairChosen)
                {
                    // Online synthesis fallback (step 7, decisions 16-21): the
                    // reviewed pool offered no (contract, position) fit for this
                    // corridor, so shape a staircase to the gap. Same placement
                    // search, level gates and ledger as pool contracts; the
                    // per-gap RNG keeps synthesis independent of the shared
                    // draw stream (decision 18).
                    bool activeStairSynthesized = TrySynthesizeActiveStairTransition(
                        dungeonSeed,
                        connection.fromRoom,
                        connection.toRoom,
                        delta,
                        path,
                        fromNodeArea,
                        toNodeArea,
                        layout.floorCells,
                        surfaces,
                        fromLevel,
                        toLevel,
                        requiredPlacementClass,
                        stairCandidateCounts,
                        plannedStairLedger,
                        out transitionIndex,
                        out lowerLandingCell,
                        out upperLandingCell,
                        out stairOptionPlannedLowerLandingCells,
                        out stairOptionPlannedUpperLandingCells,
                        out stairOptionPlannedFootprintCells,
                        out stairOptionPlannedTransitionFirstCell,
                        out stairOptionPlannedTransitionSecondCell,
                        out stairOptionPlannedLowerPortDirection,
                        out stairOptionPlannedUpperPortDirection,
                        out stairOptionPlacementClass,
                        out stairOption,
                        out synthesizedSetPiece,
                            out synthesizedGapId);
                    bool stairwellStairSynthesized = false;
                    bool stairwellEligible = string.IsNullOrEmpty(requiredPlacementClass) ||
                        string.Equals(requiredPlacementClass, StairwellStairPlacementClass, StringComparison.Ordinal);
                    if (!activeStairSynthesized && stairwellEligible)
                    {
                        // Third tier (decision 27): a 180-degree tower on void
                        // cells beside the path, only when nothing in-corridor fit.
                        stairwellStairSynthesized = TrySynthesizeStairwellTransition(
                            dungeonSeed,
                            connection.fromRoom,
                            connection.toRoom,
                            delta,
                            path,
                            fromNodeArea,
                            toNodeArea,
                            layout.floorCells,
                            surfaces,
                            fromLevel,
                            toLevel,
                            stairCandidateCounts,
                            plannedStairLedger,
                            out transitionIndex,
                            out lowerLandingCell,
                            out upperLandingCell,
                            out stairOptionPlannedLowerLandingCells,
                            out stairOptionPlannedUpperLandingCells,
                            out stairOptionPlannedFootprintCells,
                            out stairOptionPlannedTransitionFirstCell,
                            out stairOptionPlannedTransitionSecondCell,
                            out stairOptionPlannedLowerPortDirection,
                            out stairOptionPlannedUpperPortDirection,
                            out stairOptionPlacementClass,
                            out stairOption,
                                out synthesizedSetPiece,
                                out synthesizedGapId);
                    }

                    if (!activeStairSynthesized && !stairwellStairSynthesized)
                    {
                        rejectionReason = StairPlacementFailureReason(
                            hasRouteRequirement,
                            routeTransitionRequirement,
                            connection,
                            delta);
                        return false;
                    }
                }
            }

            // For an externalSpan (bridge) placement the deck replaces the walking
            // surface between the landings: the corridor cells under the span must
            // NOT be leveled, or they render as a fake floor strip with retaining
            // walls — a solid column filling the gap the bridge is meant to cross.
            // The gap stays a gap (no floor, hence no walls); another connection may
            // still claim those cells at its own level and pass underneath, subject
            // to the headroom gate below.
            int spanSkipFromIndex = int.MaxValue;
            int spanSkipToIndex = int.MinValue;
            if (delta > 1 &&
                string.Equals(stairOptionPlacementClass, ExternalSpanStairPlacementClass, StringComparison.Ordinal))
            {
                int spanLowerIndex = IndexOfPathCell(path, lowerLandingCell);
                int spanUpperIndex = IndexOfPathCell(path, upperLandingCell);
                if (spanLowerIndex >= 0 && spanUpperIndex >= 0)
                {
                    spanSkipFromIndex = Mathf.Min(spanLowerIndex, spanUpperIndex) + 1;
                    spanSkipToIndex = Mathf.Max(spanLowerIndex, spanUpperIndex) - 1;
                }

                // The deck's REAL cells are the contract footprint, which may
                // leave the corridor line (an L-shaped bridge folds off it) —
                // headroom validated against the path cells missed a deck
                // crossing a room at head height (in-editor find 2026-06-11).
                // Register the footprint as gap cells and as DECK PRISMS with a
                // conservative floored-linear base level, so the ledger's one
                // headroom rule sees them; Manhattan distance equals deck-walk
                // distance on an L. The replaced corridor cells below stay gaps
                // but carry no deck.
                int spanLength = Mathf.Abs(upperLandingCell.x - lowerLandingCell.x) +
                    Mathf.Abs(upperLandingCell.y - lowerLandingCell.y);
                // The same owner the footprint registers under below: one
                // transition, one owner. The distinction that matters is the
                // BAND — the deck declares where it sits, the footprint does not.
                var deckOwner = new OwnerKey(
                    OwnerFamily.Transition,
                    $"connection-stair:{TransitionKey(stairOptionPlannedTransitionFirstCell, stairOptionPlannedTransitionSecondCell)}");
                var bridgeVoidCells = new HashSet<Vector2Int>(
                    stairOptionPlannedFootprintCells);
                for (int pathIndex = spanSkipFromIndex;
                     pathIndex <= spanSkipToIndex && pathIndex < path.Count;
                     pathIndex++)
                {
                    if (pathIndex >= 0)
                    {
                        bridgeVoidCells.Add(path[pathIndex]);
                    }
                }

                foreach (Vector2Int deckCell in stairOptionPlannedFootprintCells)
                {
                    externalSpanGapCells.Add(deckCell);
                    int deckDistance = Mathf.Abs(deckCell.x - lowerLandingCell.x) +
                        Mathf.Abs(deckCell.y - lowerLandingCell.y);
                    int deckLevel = Mathf.FloorToInt(Mathf.Lerp(
                        Mathf.Min(fromLevel, toLevel),
                        Mathf.Max(fromLevel, toLevel),
                        spanLength > 0 ? (float)deckDistance / spanLength : 0f));
                    plannedStairLedger.RegisterSpanDeck(new[] { deckCell }, deckLevel, deckOwner);
                }

                // A declared Bridge owns both the deck transition and the void
                // it crosses. The deck owner is the sole admitted penetration;
                // fill, unrelated corridors, landings and late structures are
                // rejected by the existing OpenVolume ledger.
                if (!TryRegisterPlannedBridgeOpenVolume(
                        plannedStairLedger,
                        connection.edgeId,
                        bridgeVoidCells,
                        fromLevel,
                        toLevel,
                        deckOwner,
                        out Prism bridgeVoidBlocker))
                {
                    rejectionReason =
                        $"[ROUTE_BRIDGE_VOID] edge '{connection.edgeId}' could not reserve its " +
                        $"bridge void because it conflicted with {bridgeVoidBlocker}";
                    return false;
                }
            }

            for (int i = 0; i < path.Count; i++)
            {
                if (i >= spanSkipFromIndex && i <= spanSkipToIndex)
                {
                    externalSpanGapCells.Add(path[i]);
                    continue;
                }

                // Cells inside the endpoint rooms are already zone-leveled; a path
                // may legally cross a room's seam on its way to the door, so it
                // must not re-level the other zone's cells to the threshold level.
                if (layout.rooms[connection.fromRoom].Contains(path[i]) ||
                    layout.rooms[connection.toRoom].Contains(path[i]))
                {
                    continue;
                }

                int targetLevel = delta == 0 || i <= transitionIndex ? fromLevel : toLevel;
                if (connection.IsLayerBound)
                {
                    // D2's producer, and the half of the relaxation that could
                    // not be shipped without it: the claim rule alone lets two
                    // corridors share a plan cell, and then `TrySetFloorLevel`
                    // REJECTS the conflicting value rather than stacking — so
                    // the relaxation on its own buys a failed tier attempt, not
                    // a second corridor surface.
                    surfaces.AddCorridorSurface(path[i], targetLevel);
                    continue;
                }

                if (!surfaces.TrySetFloorLevel(path[i], targetLevel, out rejectionReason))
                {
                    return false;
                }
            }

            if (corridorStepStrip)
            {
                Vector2Int stripFromCell = path[transitionIndex];
                Vector2Int stripToCell = path[transitionIndex + 1];
                Vector2Int stripLowerCell = fromLevel <= toLevel ? stripFromCell : stripToCell;
                Vector2Int stripRaisedCell = fromLevel <= toLevel ? stripToCell : stripFromCell;
                string stripKey = TransitionKey(stripRaisedCell, stripLowerCell);
                if (transitionKeys.Add(stripKey))
                {
                    transitions.Add(new ElevationEdgeModel.TransitionEdge(
                        stripRaisedCell,
                        TransitionEndpointLevel(surfaces, stripRaisedCell),
                        stripLowerCell,
                        TransitionEndpointLevel(surfaces, stripLowerCell),
                        seamStairPrefabPath,
                        SeamStairPlacementClass));
                    plannedStairLedger.Register(
                        new OwnerKey(OwnerFamily.Transition, $"corridor-step-strip:{stripKey}"),
                        new[] { stripLowerCell },
                        Array.Empty<Vector2Int>(),
                        new[] { stripRaisedCell },
                        new[] { stripLowerCell, stripRaisedCell },
                        Array.Empty<Vector2Int>(),
                        Array.Empty<Vector2Int>());
                }
            }

            if (delta > 1)
            {
                int lowerLevel = Mathf.Min(fromLevel, toLevel);
                int higherLevel = Mathf.Max(fromLevel, toLevel);
                if (!TrySetPlannedStairCells(
                        surfaces,
                        stairOptionPlannedLowerLandingCells,
                        stairOptionPlannedUpperLandingCells,
                        stairOptionPlannedFootprintCells,
                        lowerLevel,
                        higherLevel,
                        stairOptionPlacementClass,
                        out rejectionReason))
                {
                    return false;
                }

                plannedStairLedger.Register(
                    new OwnerKey(
                        OwnerFamily.Transition,
                        $"connection-stair:{TransitionKey(stairOptionPlannedTransitionFirstCell, stairOptionPlannedTransitionSecondCell)}"),
                    stairOptionPlannedFootprintCells,
                    stairOptionPlannedLowerLandingCells,
                    stairOptionPlannedUpperLandingCells,
                    new[]
                    {
                        stairOptionPlannedTransitionFirstCell,
                        stairOptionPlannedTransitionSecondCell
                    },
                    Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>());
            }

            if (delta > 1)
            {
                string key = TransitionKey(stairOptionPlannedTransitionFirstCell, stairOptionPlannedTransitionSecondCell);
                if (transitionKeys.Add(key))
                {
                    if (synthesizedSetPiece != null)
                    {
                        transitions.Add(new ElevationEdgeModel.TransitionEdge(
                            stairOptionPlannedTransitionFirstCell,
                            TransitionEndpointLevel(surfaces, stairOptionPlannedTransitionFirstCell),
                            stairOptionPlannedTransitionSecondCell,
                            TransitionEndpointLevel(surfaces, stairOptionPlannedTransitionSecondCell),
                            stairOption.prefabPath,
                            stairOptionPlannedLowerLandingCells,
                            stairOptionPlannedUpperLandingCells,
                            stairOptionPlannedFootprintCells,
                            stairOptionPlannedLowerPortDirection,
                            stairOptionPlannedUpperPortDirection,
                            stairOptionPlacementClass,
                            synthesizedSetPiece));
                        synthesizedStairs.Add((synthesizedGapId, synthesizedSetPiece));
                    }
                    else
                    {
                        transitions.Add(new ElevationEdgeModel.TransitionEdge(
                            stairOptionPlannedTransitionFirstCell,
                            TransitionEndpointLevel(surfaces, stairOptionPlannedTransitionFirstCell),
                            stairOptionPlannedTransitionSecondCell,
                            TransitionEndpointLevel(surfaces, stairOptionPlannedTransitionSecondCell),
                            stairOption.prefabPath,
                            stairOptionPlannedLowerLandingCells,
                            stairOptionPlannedUpperLandingCells,
                            stairOptionPlannedFootprintCells,
                            stairOptionPlannedLowerPortDirection,
                            stairOptionPlannedUpperPortDirection,
                            stairOptionPlacementClass));
                    }
                }
            }

            if (hasRouteRequirement)
            {
                int directedRise = routeTransitionRequirement.fromNode == connection.fromRoom
                    ? toLevel - fromLevel
                    : fromLevel - toLevel;
                string realizationClass = delta == 0
                    ? LevelConnectionPlacementClass(connection, layout.rooms)
                    : stairOptionPlacementClass;
                resolvedRouteTransitions.Add(new RouteTransitionResolution(
                    routeTransitionRequirement.id,
                    routeTransitionRequirement.fromNode,
                    routeTransitionRequirement.toNode,
                    routeTransitionRequirement.transitionKind,
                    routeTransitionRequirement.requiredRiseLevels,
                    directedRise,
                    realizationClass,
                    delta == 0 ? default : stairOptionPlannedTransitionFirstCell,
                    delta == 0 ? default : stairOptionPlannedTransitionSecondCell,
                    stairOptionPlannedLowerLandingCells,
                    stairOptionPlannedUpperLandingCells,
                    stairOptionPlannedFootprintCells));
            }
            return true;
        }

        private static bool TryRegisterPlannedBridgeOpenVolume(
            PrismLedger prisms,
            string edgeId,
            IEnumerable<Vector2Int> voidCells,
            int firstLevel,
            int secondLevel,
            OwnerKey deckOwner,
            out Prism blocker)
        {
            return prisms.TryRegisterOpenVolume(
                SortedCells(new HashSet<Vector2Int>(voidCells)),
                LevelBand.SpanningEndpoints(firstLevel, secondLevel),
                new OwnerKey(OwnerFamily.Opening, $"bridge-void:{edgeId}"),
                new[] { deckOwner },
                out blocker);
        }

        private static string LevelConnectionPlacementClass(
            RoomConnection connection,
            IReadOnlyList<RoomFootprint> rooms)
        {
            if (connection.path != null &&
                connection.path.Count == 2 &&
                connection.fromRoom >= 0 &&
                connection.fromRoom < rooms.Count &&
                connection.toRoom >= 0 &&
                connection.toRoom < rooms.Count)
            {
                Vector2Int first = connection.path[0];
                Vector2Int second = connection.path[1];
                bool forwardDoorway =
                    rooms[connection.fromRoom].Contains(first) &&
                    rooms[connection.toRoom].Contains(second);
                bool reverseDoorway =
                    rooms[connection.fromRoom].Contains(second) &&
                    rooms[connection.toRoom].Contains(first);
                if ((forwardDoorway || reverseDoorway) &&
                    Mathf.Abs(first.x - second.x) + Mathf.Abs(first.y - second.y) == 1)
                {
                    return DirectDoorwayPlacementClass;
                }
            }

            return RoutedCorridorPlacementClass;
        }

        private static string StairPlacementFailureReason(
            bool hasRouteRequirement,
            RouteTraversalIntent routeTransitionRequirement,
            RoomConnection connection,
            int delta)
        {
            return hasRouteRequirement
                ? $"[ROUTE_TRANSITION_RESERVATION] edge '{routeTransitionRequirement.id}' could not reserve its required {routeTransitionRequirement.transitionKind} for rise {delta}u"
                : $"connection {connection.fromRoom}->{connection.toRoom} had no reviewed active stair contract placement for rise {delta}, lane count <= {MaxActiveStairLaneCount}; synthesis offered no fitting design";
        }


        private static bool TryResolveRouteRequirements(
            RouteTierRequirements requirements,
            DungeonLayout layout,
            SurfaceField surfaces,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            IReadOnlyList<RouteTransitionResolution> resolvedTransitions,
            out RouteRequirementResolution resolution,
            out string rejectionReason)
        {
            resolution = default;
            rejectionReason = string.Empty;
            if (requirements?.intent == null || resolvedTransitions == null)
            {
                rejectionReason = "[ROUTE_ELEVATION_REQUIREMENT] final tier plan had no route requirement evidence";
                return false;
            }

            var resolutionById = new Dictionary<string, RouteTransitionResolution>(StringComparer.Ordinal);
            foreach (RouteTransitionResolution item in resolvedTransitions)
            {
                if (string.IsNullOrEmpty(item.edgeId) || resolutionById.ContainsKey(item.edgeId))
                {
                    rejectionReason = $"[ROUTE_TRANSITION_RESERVATION] duplicate or missing resolution id '{item.edgeId}'";
                    return false;
                }

                resolutionById[item.edgeId] = item;
            }

            foreach (RouteTraversalIntent required in requirements.intent.traversalEdges)
            {
                if (!resolutionById.TryGetValue(required.id, out RouteTransitionResolution actual))
                {
                    rejectionReason = $"[ROUTE_TRANSITION_RESERVATION] edge '{required.id}' had no resolved transition evidence";
                    return false;
                }

                string expectedPlacementClass = required.transitionKind == RouteTransitionKind.LevelCorridor
                    ? ExpectedLevelConnectionPlacementClass(layout, required.id)
                    : required.transitionKind == RouteTransitionKind.Bridge
                        ? ExternalSpanStairPlacementClass
                        : required.transitionKind == RouteTransitionKind.Stairwell
                            ? StairwellStairPlacementClass
                            : EmbeddedStairPlacementClass;
                if (actual.fromRoom != required.fromNode ||
                    actual.toRoom != required.toNode ||
                    actual.transitionKind != required.transitionKind ||
                    actual.requiredRiseLevels != required.requiredRiseLevels ||
                    actual.resolvedRiseLevels != required.requiredRiseLevels ||
                    !string.Equals(actual.placementClass, expectedPlacementClass, StringComparison.Ordinal))
                {
                    rejectionReason = $"[ROUTE_TRANSITION_RESERVATION] edge '{required.id}' did not realize its declared type/rise";
                    return false;
                }

                if (required.transitionKind == RouteTransitionKind.LevelCorridor)
                {
                    continue;
                }

                if (actual.lowerLandingCells.Length == 0 ||
                    actual.upperLandingCells.Length == 0 ||
                    actual.footprintCells.Length == 0)
                {
                    rejectionReason = $"[ROUTE_TRANSITION_RESERVATION] edge '{required.id}' did not reserve footprint and both landing sets before fill";
                    return false;
                }

                int transitionMatches = 0;
                string actualKey = TransitionKey(actual.transitionFirstCell, actual.transitionSecondCell);
                foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
                {
                    if (string.Equals(transition.placementClass, actual.placementClass, StringComparison.Ordinal) &&
                        string.Equals(
                            TransitionKey(transition.firstCell, transition.secondCell),
                            actualKey,
                            StringComparison.Ordinal))
                    {
                        transitionMatches++;
                    }
                }

                if (transitionMatches != 1)
                {
                    rejectionReason =
                        $"[ROUTE_TRANSITION_RESERVATION] edge '{required.id}' reservation had " +
                        $"{transitionMatches} canonical TransitionEdge consumers";
                    return false;
                }

                if (AnyProtectedCell(
                        requirements.reservedVistaCells,
                        actual.lowerLandingCells,
                        actual.upperLandingCells,
                        actual.footprintCells))
                {
                    rejectionReason = $"[ROUTE_VISTA_FINAL_BLOCKED] edge '{required.id}' entered the reserved sight volume";
                    return false;
                }
            }

            if (!TryGetRouteNodeAnchorLevel(
                    requirements.intent.bottomNode,
                    requirements,
                    layout,
                    surfaces,
                    out int bottomLevel) ||
                !TryGetRouteNodeAnchorLevel(
                    requirements.intent.topNode,
                    requirements,
                    layout,
                    surfaces,
                    out int topLevel))
            {
                rejectionReason = "[ROUTE_ELEVATION_REQUIREMENT] declared bottom/top had no final doorway anchor levels";
                return false;
            }
            int routeClimb = topLevel - bottomLevel;
            int topologyCeiling = requirements.intent.topology.ceilingLevels;
            if (bottomLevel != 0 || topLevel != topologyCeiling || routeClimb != topologyCeiling)
            {
                rejectionReason =
                    $"[ROUTE_ELEVATION_REQUIREMENT] declared bottom/top resolved to " +
                    $"{bottomLevel}u/{topLevel}u instead of 0u/{topologyCeiling}u";
                return false;
            }

            RouteVistaIntent vista = requirements.intent.vista;
            RoomFootprint sourceRoom = layout.rooms[vista.sourceNode];
            RoomFootprint targetRoom = layout.rooms[vista.targetNode];
            Vector2Int sourceEdge = requirements.vistaSourceCell;
            Vector2Int targetEdge = requirements.vistaTargetCell;
            if (!sourceRoom.Contains(sourceEdge) ||
                !targetRoom.Contains(targetEdge) ||
                !surfaces.TryGetFloorLevel(sourceEdge, out int sourceLevel) ||
                !surfaces.TryGetFloorLevel(targetEdge, out int targetLevel))
            {
                rejectionReason = "[ROUTE_VISTA_FINAL_BLOCKED] final vista endpoints did not resolve to leveled facing boundary cells";
                return false;
            }

            bool facingOpposed = requirements.vistaSourceFacing != Vector2Int.zero &&
                requirements.vistaSourceFacing == -requirements.vistaTargetFacing;
            bool reservedVolumeClear = requirements.reservedVistaCells.Count >= vista.minimumReservedVoidCells;
            foreach (Vector2Int cell in requirements.reservedVistaCells)
            {
                if (layout.floorCells.Contains(cell) || surfaces.HasFloor(cell))
                {
                    reservedVolumeClear = false;
                    break;
                }
            }

            if (reservedVolumeClear)
            {
                foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
                {
                    if (AnyProtectedCell(
                            requirements.reservedVistaCells,
                            transition.lowerLandingCells,
                            transition.upperLandingCells,
                            transition.footprintCells))
                    {
                        reservedVolumeClear = false;
                        break;
                    }
                }
            }

            int vistaLevelDelta = sourceLevel - targetLevel;
            bool vistaValid = facingOpposed &&
                reservedVolumeClear &&
                vistaLevelDelta >= MajorRiseLevels;
            if (!vistaValid)
            {
                rejectionReason =
                    $"[ROUTE_VISTA_FINAL_BLOCKED] final vista facing={facingOpposed}, clear={reservedVolumeClear}, source-target={vistaLevelDelta}u";
                return false;
            }

            var resolutionArray = new RouteTransitionResolution[resolvedTransitions.Count];
            for (int i = 0; i < resolvedTransitions.Count; i++)
            {
                resolutionArray[i] = resolvedTransitions[i];
            }

            resolution = new RouteRequirementResolution(
                resolutionArray,
                bottomLevel,
                topLevel,
                sourceEdge,
                targetEdge,
                sourceLevel,
                targetLevel,
                requirements.vistaSourceFacing,
                requirements.vistaTargetFacing,
                SortedCells(requirements.reservedVistaCells).ToArray(),
                vistaValid);
            return true;
        }

        private static string ExpectedLevelConnectionPlacementClass(
            DungeonLayout layout,
            string edgeId)
        {
            if (layout.connections != null)
            {
                foreach (RoomConnection connection in layout.connections)
                {
                    if (connection.source == ConnectionSource.RouteEdge &&
                        string.Equals(connection.edgeId, edgeId, StringComparison.Ordinal))
                    {
                        return LevelConnectionPlacementClass(connection, layout.rooms);
                    }
                }
            }

            return string.Empty;
        }

        private static bool TryGetRouteNodeAnchorLevel(
            int node,
            RouteTierRequirements requirements,
            DungeonLayout layout,
            SurfaceField surfaces,
            out int routeNodeLevel)
        {
            foreach (RoomConnection connection in layout.connections)
            {
                if (!requirements.TryGetTransition(connection.edgeId, out _) ||
                    connection.fromRoom != node && connection.toRoom != node)
                {
                    continue;
                }

                bool forward = connection.fromRoom == node;
                Vector2Int anchor = ThresholdCell(
                    layout.rooms[node],
                    connection.path,
                    forward);
                if (surfaces.TryGetFloorLevel(anchor, out int level))
                {
                    routeNodeLevel = level;
                    return true;
                }
            }

            routeNodeLevel = default;
            return false;
        }

        private static bool AnyProtectedCell(
            HashSet<Vector2Int> protectedCells,
            params IReadOnlyList<Vector2Int>[] groups)
        {
            if (protectedCells == null || protectedCells.Count == 0)
            {
                return false;
            }

            foreach (IReadOnlyList<Vector2Int> group in groups)
            {
                if (group == null)
                {
                    continue;
                }

                foreach (Vector2Int cell in group)
                {
                    if (protectedCells.Contains(cell))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryResolveNamedVistaPromontory(
            RouteTierRequirements requirements,
            SurfaceField surfaces,
            out NamedVistaPromontoryResolution[] resolutions,
            out string rejectionReason)
        {
            resolutions = Array.Empty<NamedVistaPromontoryResolution>();
            rejectionReason = string.Empty;
            Vector2Int[] plannedCells = requirements?.namedPromontoryCells ?? Array.Empty<Vector2Int>();
            if (plannedCells.Length == 0)
            {
                return true;
            }

            RouteIntent intent = requirements?.intent;
            if (intent == null || surfaces == null || plannedCells.Length > MaximumNamedVistaPromontoryCells)
            {
                rejectionReason = $"[ROUTE_PROMONTORY] {NamedVistaPromontoryPolicyVersion} had invalid requirements or exceeded the {MaximumNamedVistaPromontoryCells}-cell limit";
                return false;
            }

            RouteVistaIntent vista = intent.vista;
            string targetNodeId = vista.targetNode >= 0 && vista.targetNode < intent.nodes.Length
                ? intent.nodes[vista.targetNode].id
                : string.Empty;
            if (string.IsNullOrEmpty(vista.id) || string.IsNullOrEmpty(targetNodeId))
            {
                rejectionReason = $"[ROUTE_PROMONTORY] {NamedVistaPromontoryPolicyVersion} had no named target identity";
                return false;
            }

            Vector2Int facing = requirements.vistaSourceFacing;
            bool cardinalFacing = Mathf.Abs(facing.x) + Mathf.Abs(facing.y) == 1;
            if (!cardinalFacing || facing != -requirements.vistaTargetFacing)
            {
                rejectionReason = $"[ROUTE_PROMONTORY] {NamedVistaPromontoryPolicyVersion} required cardinal opposed facing";
                return false;
            }

            if (requirements.reservedVistaCells.Count < vista.minimumReservedVoidCells)
            {
                rejectionReason = $"[ROUTE_PROMONTORY] {NamedVistaPromontoryPolicyVersion} left fewer than {vista.minimumReservedVoidCells} void cells";
                return false;
            }

            if (!surfaces.TryGetFloorLevel(requirements.vistaSourceCell, out int sourceLevel) ||
                !surfaces.TryGetFloorLevel(requirements.vistaTargetCell, out int targetLevel) ||
                sourceLevel - targetLevel < MajorRiseLevels)
            {
                rejectionReason = $"[ROUTE_PROMONTORY] {NamedVistaPromontoryPolicyVersion} target was not at least {MajorRiseLevels}u below its source";
                return false;
            }

            for (int index = 0; index < plannedCells.Length; index++)
            {
                Vector2Int expected = requirements.vistaSourceCell + facing * (index + 1);
                if (plannedCells[index] != expected ||
                    requirements.reservedVistaCells.Contains(expected) ||
                    surfaces.HasFloor(expected))
                {
                    rejectionReason = $"[ROUTE_PROMONTORY] {NamedVistaPromontoryPolicyVersion} found an occupied, off-axis, or non-contiguous planned cell {plannedCells[index]}";
                    return false;
                }
            }

            foreach (Vector2Int cell in requirements.reservedVistaCells)
            {
                if (surfaces.HasFloor(cell))
                {
                    rejectionReason = $"[ROUTE_PROMONTORY] {NamedVistaPromontoryPolicyVersion} found occupied remaining vista cell {cell}";
                    return false;
                }
            }

            // Insert-only: the loop above rejects the whole promontory if any
            // planned cell is already surfaced, so this can never displace one.
            foreach (Vector2Int cell in plannedCells)
            {
                surfaces.AddFloorLevel(cell, sourceLevel);
            }

            resolutions = new[]
            {
                new NamedVistaPromontoryResolution(
                    vista.id,
                    targetNodeId,
                    requirements.vistaSourceCell,
                    requirements.vistaTargetCell,
                    facing,
                    sourceLevel,
                    plannedCells)
            };
            return true;
        }

        private static List<Vector2Int> CollectNamedPromontoryCells(
            IReadOnlyList<NamedVistaPromontoryResolution> resolutions)
        {
            var cells = new List<Vector2Int>();
            foreach (NamedVistaPromontoryResolution resolution in
                resolutions ?? Array.Empty<NamedVistaPromontoryResolution>())
            {
                cells.AddRange(resolution.cells ?? Array.Empty<Vector2Int>());
            }

            return cells;
        }

        // Decision 43(a): a 1u drop WITHIN one room always climbs. Every
        // intra-room delta-1 adjacency not already carrying a transition
        // takes a dais-class strip — the full band, so the existing corner
        // machinery dresses its turns (notches at convex turns, concave
        // sweeps at inside turns). Doorway and stair-reservation faces stay
        // the walled fallback per the decision; inter-room delta-1 edges
        // (43b) keep their walls because the sweep never crosses rooms.
        // FLOOR-scoped on purpose, and that is a limit rather than an oversight:
        // it sweeps the 1u drops the elevation stage's own layer-blind leveling
        // produces, which are all between column floors. An authored storey's
        // internal drops are the recipe's to declare — a 1u seam appearing inside
        // a gallery because a sweep found it would be geometry the recipe never
        // asked for. Named here so the gap is a decision, not a silence.
        private static int SweepIntraRoom1uDrops(
            DungeonLayout layout,
            SurfaceField surfaces,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            HashSet<string> transitionKeys,
            PrismLedger plannedStairLedger,
            HashSet<Vector2Int> doorwayCells,
            string seamStairPrefabPath)
        {
            var roomByCell = new Dictionary<Vector2Int, int>();
            for (int roomIndex = 0; roomIndex < layout.rooms.Count; roomIndex++)
            {
                foreach (Vector2Int cell in layout.rooms[roomIndex].CellsRowMajor())
                {
                    roomByCell[cell] = roomIndex;
                }
            }

            int added = 0;
            for (int roomIndex = 0; roomIndex < layout.rooms.Count; roomIndex++)
            {
                foreach (Vector2Int cell in layout.rooms[roomIndex].CellsRowMajor())
                {
                    {
                        if (!surfaces.TryGetFloorLevel(cell, out int cellLevel))
                        {
                            continue;
                        }

                        foreach (Vector2Int step in new[] { Vector2Int.right, Vector2Int.up })
                        {
                            Vector2Int neighbor = cell + step;
                            if (!surfaces.TryGetFloorLevel(neighbor, out int neighborLevel) ||
                                Mathf.Abs(cellLevel - neighborLevel) != 1 ||
                                !roomByCell.TryGetValue(neighbor, out int neighborRoom) ||
                                neighborRoom != roomIndex)
                            {
                                continue;
                            }

                            Vector2Int upperCell = cellLevel > neighborLevel ? cell : neighbor;
                            Vector2Int lowerCell = cellLevel > neighborLevel ? neighbor : cell;
                            if (doorwayCells.Contains(upperCell) ||
                                doorwayCells.Contains(lowerCell) ||
                                plannedStairLedger.BlocksFootprint(lowerCell) ||
                                plannedStairLedger.BlocksFootprint(upperCell) ||
                                plannedStairLedger.BlocksTransitionMouth(lowerCell) ||
                                plannedStairLedger.BlocksTransitionMouth(upperCell))
                            {
                                continue;
                            }

                            if (!transitionKeys.Add(TransitionKey(upperCell, lowerCell)))
                            {
                                continue;
                            }

                            transitions.Add(new ElevationEdgeModel.TransitionEdge(
                                upperCell,
                                Mathf.Max(cellLevel, neighborLevel),
                                lowerCell,
                                Mathf.Min(cellLevel, neighborLevel),
                                seamStairPrefabPath,
                                DaisStairPlacementClass));
                            plannedStairLedger.Register(
                                new OwnerKey(
                                    OwnerFamily.Transition,
                                    $"intra-room-1u-sweep:{TransitionKey(upperCell, lowerCell)}"),
                                new[] { lowerCell },
                                Array.Empty<Vector2Int>(),
                                new[] { upperCell },
                                new[] { lowerCell, upperCell },
                                Array.Empty<Vector2Int>(),
                                Array.Empty<Vector2Int>());
                            added++;
                        }
                    }
                }
            }

            return added;
        }

        // Showpiece render (decision 46 increment 2): one root per showpiece
        // carrying the wall anchor and side yaw; the pieces keep their
        // gallery-local transforms, so the placement is the approved design
        // verbatim by construction.
        private static void PlaceDaisShowpieces(
            Transform parent,
            IReadOnlyList<DaisShowpiece> showpieces,
            Vector3 origin,
            float levelHeight,
            ref Bounds bounds)
        {
            const float cellSize = 4f;
            foreach (DaisShowpiece showpiece in showpieces)
            {
                var showpieceRoot = new GameObject(
                    $"dais_showpiece_{showpiece.designName}_{showpiece.originCell.x}_{showpiece.originCell.y}");
                showpieceRoot.transform.SetParent(parent, worldPositionStays: false);
                showpieceRoot.transform.position = origin + new Vector3(
                    showpiece.originCell.x * cellSize,
                    showpiece.roomLevel * levelHeight,
                    showpiece.originCell.y * cellSize);
                showpieceRoot.transform.rotation = Quaternion.Euler(0f, showpiece.yawDegrees, 0f);
                foreach (ElevationEdgeModel.SynthesizedPiecePlacement piece in showpiece.pieces)
                {
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(piece.sourcePrefab);
                    if (prefab == null)
                    {
                        throw new InvalidOperationException(
                            $"Dais showpiece '{showpiece.designName}' references a missing piece prefab '{piece.sourcePrefab}'.");
                    }

                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, showpieceRoot.transform);
                    instance.name = piece.pieceName;
                    instance.transform.localPosition = piece.localPosition;
                    instance.transform.localRotation = Quaternion.Euler(piece.localPitchDegrees, piece.localYawDegrees, 0f);
                }

                foreach (Renderer renderer in showpieceRoot.GetComponentsInChildren<Renderer>())
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
        }

        // Headroom gate (design decision 2): at least MinHeadroomLevels u of
        // clearance between any walkable surface and geometry above it.
        //
        // Phase B replaced this with PrismLedger.TryValidateSurfaceHeadroom —
        // one rule over the ledger, stated once and called from both the
        // planning path and the accepted-plan validation. The `spanDeckLevels`
        // side table it read is gone: a deck registers its own prism with a
        // declared base, so nothing has to carry a parallel dictionary of
        // heights to the gate and nothing can drop it on the way.

        private static IReadOnlyList<ReviewedActiveStairOption> LoadReviewedActiveStairOptions()
        {
            if (!File.Exists(StairProofContractsPath))
            {
                throw new FileNotFoundException(StairProofContractsPath);
            }

            JObject root = JObject.Parse(File.ReadAllText(StairProofContractsPath));
            if (!(root["contracts"] is JArray records))
            {
                throw new InvalidOperationException($"{StairProofContractsPath} is missing a contracts array.");
            }

            var options = new List<ReviewedActiveStairOption>();
            foreach (JToken token in records)
            {
                if (string.Equals(token.Value<string>("source"), "authored-reviewed", StringComparison.Ordinal))
                {
                    TryAppendReviewedActiveStairOption(token, options);
                }
            }

            // Forged contracts (design step 6) join the planning pool on equal terms
            // once human-reviewed; pending entries stay inert. Plain file IO +
            // Newtonsoft keeps this loader headless-safe.
            if (File.Exists(ForgedStairContractsPath))
            {
                JObject forgedRoot = JObject.Parse(File.ReadAllText(ForgedStairContractsPath));
                if (forgedRoot["contracts"] is JArray forgedRecords)
                {
                    foreach (JToken token in forgedRecords)
                    {
                        if (string.Equals(token.Value<string>("source"), "forge", StringComparison.Ordinal))
                        {
                            TryAppendReviewedActiveStairOption(token, options);
                        }
                    }
                }
            }

            if (options.Count == 0)
            {
                throw new InvalidOperationException($"No reviewed active stair options were usable from {StairProofContractsPath}.");
            }

            return options;
        }

        private static void TryAppendReviewedActiveStairOption(JToken token, List<ReviewedActiveStairOption> options)
        {
            string name = token.Value<string>("name") ?? string.Empty;
            string prefabPath = NormalizeAssetPath(token.Value<string>("prefab") ?? string.Empty);
            string topology = token.Value<string>("topology") ?? string.Empty;
            bool isBridge = token.Value<bool?>("bridgeAllowed") == true;
            if (!string.Equals(token.Value<string>("reviewStatus"), "reviewed", StringComparison.Ordinal) ||
                !ActiveGenerationSupportsStairTopology(topology))
            {
                return;
            }

            int rise = token.Value<int?>("rise") ?? 0;
            int laneCount = token.Value<int?>("laneCount") ?? 0;
            int runLength = token.Value<int?>("runLength") ?? 0;
            if (rise <= 0 || laneCount <= 0 || runLength <= 0 || string.IsNullOrWhiteSpace(prefabPath))
            {
                return;
            }

            if (!TryParseReviewedActiveStairGeometry(
                    token,
                    out Vector2 localBoundsMin,
                    out Vector2 localBoundsMax,
                    out Vector2Int[] footprintCells,
                    out Vector2Int[] entryCells,
                    out Vector2Int[] exitCells,
                    out Vector2 localEntryPoint,
                    out Vector2 localExitPoint,
                    out int entryDirection,
                    out int exitDirection,
                    out string geometryError))
            {
                LogPlanningWarning($"Dungeon Lab Generate: reviewed stair option '{name}' skipped because {geometryError}");
                return;
            }

            if (!ReviewedStairPrefabExists(prefabPath))
            {
                LogPlanningWarning($"Dungeon Lab Generate: reviewed stair option '{name}' skipped because prefab is missing: {prefabPath}");
                return;
            }

            options.Add(new ReviewedActiveStairOption(
                name,
                prefabPath,
                rise,
                laneCount,
                runLength,
                topology,
                isBridge,
                localBoundsMin,
                localBoundsMax,
                footprintCells,
                entryCells,
                exitCells,
                localEntryPoint,
                localExitPoint,
                entryDirection,
                exitDirection));
        }

        private static bool ReviewedStairPrefabExists(string prefabPath)
        {
            // Fall back to a disk check when the AssetDatabase is not available
            // (headless plan validation) so the planning stage stays pure data.
            try
            {
                return AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null;
            }
            catch (Exception)
            {
                return File.Exists(prefabPath);
            }
        }

        private static bool ActiveGenerationSupportsStairTopology(string topology)
        {
            return string.Equals(topology, ActiveStraightStairTopology, StringComparison.Ordinal) ||
                string.Equals(topology, ActiveTurningStairTopology, StringComparison.Ordinal) ||
                string.Equals(topology, ActiveStairwellStairTopology, StringComparison.Ordinal) ||
                string.Equals(topology, ActiveDeckStairTopology, StringComparison.Ordinal);
        }

        private static bool TryParseReviewedActiveStairGeometry(
            JToken token,
            out Vector2 localBoundsMin,
            out Vector2 localBoundsMax,
            out Vector2Int[] footprintCells,
            out Vector2Int[] entryCells,
            out Vector2Int[] exitCells,
            out Vector2 localEntryPoint,
            out Vector2 localExitPoint,
            out int entryDirection,
            out int exitDirection,
            out string error)
        {
            localBoundsMin = Vector2.zero;
            localBoundsMax = Vector2.zero;
            footprintCells = Array.Empty<Vector2Int>();
            entryCells = Array.Empty<Vector2Int>();
            exitCells = Array.Empty<Vector2Int>();
            localEntryPoint = Vector2.zero;
            localExitPoint = Vector2.zero;
            entryDirection = 0;
            exitDirection = 0;
            error = string.Empty;

            if (!TryParseReviewedPlanVector(token["localBoundsMin"], out localBoundsMin) ||
                !TryParseReviewedPlanVector(token["localBoundsSizeCells"], out Vector2 localBoundsSizeCells) ||
                localBoundsSizeCells.x <= 0f ||
                localBoundsSizeCells.y <= 0f)
            {
                error = "contract has no local bounds";
                return false;
            }

            localBoundsMax = localBoundsMin + localBoundsSizeCells * 4f;

            if (!TryParseReviewedCellArray(token["footprintCells"], out footprintCells) || footprintCells.Length == 0)
            {
                error = "contract has no footprint cells";
                return false;
            }

            if (!(token["ports"] is JArray ports) || ports.Count < 2)
            {
                error = "contract has fewer than two ports";
                return false;
            }

            int lowestLevel = int.MaxValue;
            int highestLevel = int.MinValue;
            JToken entryPort = null;
            JToken exitPort = null;
            foreach (JToken port in ports)
            {
                int level = port.Value<int?>("level") ?? 0;
                if (level < lowestLevel)
                {
                    lowestLevel = level;
                    entryPort = port;
                }

                if (level > highestLevel)
                {
                    highestLevel = level;
                    exitPort = port;
                }
            }

            if (entryPort == null || exitPort == null || highestLevel <= lowestLevel)
            {
                // Aerial decks (decision 31): rise-0 contracts carry two ports at
                // EQUAL levels; they resolve by array order instead of by level.
                if (string.Equals(token.Value<string>("topology"), ActiveDeckStairTopology, StringComparison.Ordinal) &&
                    ports.Count == 2 &&
                    highestLevel == lowestLevel)
                {
                    entryPort = ports[0];
                    exitPort = ports[1];
                }
                else
                {
                    error = "contract ports do not define a lower entry and upper exit";
                    return false;
                }
            }

            if (!TryParseReviewedCellArray(entryPort["cells"], out entryCells) || entryCells.Length == 0 ||
                !TryParseReviewedCellArray(exitPort["cells"], out exitCells) || exitCells.Length == 0)
            {
                error = "contract ports do not define cell spans";
                return false;
            }

            if (!TryParseReviewedPlanVector(entryPort["localEdgePosition"], out localEntryPoint) ||
                !TryParseReviewedPlanVector(exitPort["localEdgePosition"], out localExitPoint))
            {
                error = "contract ports do not define local edge positions";
                return false;
            }

            if (!TryParseDirectionName(entryPort.Value<string>("side"), out entryDirection) ||
                !TryParseDirectionName(exitPort.Value<string>("side"), out exitDirection))
            {
                error = "contract ports do not define cardinal sides";
                return false;
            }

            return true;
        }

        private static bool TryParseReviewedPlanVector(JToken token, out Vector2 value)
        {
            value = Vector2.zero;
            if (token == null)
            {
                return false;
            }

            value = new Vector2(token.Value<float>("x"), token.Value<float>("z"));
            return true;
        }

        private static bool TryParseReviewedCellArray(JToken token, out Vector2Int[] cells)
        {
            if (!(token is JArray array) || array.Count == 0)
            {
                cells = Array.Empty<Vector2Int>();
                return false;
            }

            var values = new Vector2Int[array.Count];
            for (int i = 0; i < array.Count; i++)
            {
                JToken cell = array[i];
                values[i] = new Vector2Int(cell.Value<int>("x"), cell.Value<int>("z"));
            }

            cells = values;
            return true;
        }

        private static bool HasReviewedActiveStairOption(
            IReadOnlyList<ReviewedActiveStairOption> options,
            int rise,
            int maxLaneCount)
        {
            foreach (ReviewedActiveStairOption option in options)
            {
                if (option.rise == rise &&
                    option.laneCount > 0 &&
                    option.laneCount <= maxLaneCount)
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim();
        }

        /// <summary>
        /// Build the renderer's boundary context from an enclosure decision the
        /// PLAN already made.
        /// </summary>
        /// <remarks>
        /// D4 moved the roll out of here (design §13). The array arrives decided;
        /// what still happens here is everything that needs geometry — chamber
        /// subdivision resizes it, and the two sealed-room passes demote entries
        /// that turn out to have no doorway. The roll and its consequences were
        /// never the same step; only the roll could move earlier.
        /// </remarks>
        private static bool TryBuildRoomBoundaryContext(
            DungeonLayout layout,
            SurfaceField surfaces,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            PrismLedger prisms,
            IReadOnlyList<RecipePlacement> recipePlacements,
            bool[] plannedEnclosedRooms,
            System.Random random,
            out ElevationEdgeModel.RoomBoundaryContext context,
            out string rejectionReason)
        {
            context = null;
            rejectionReason = string.Empty;
            if (layout.rooms == null || layout.rooms.Count == 0)
            {
                rejectionReason = "layout had no rooms for boundary context";
                return false;
            }

            Dictionary<Vector2Int, int> cellRoomIds = BuildCellRoomIds(layout);
            List<ElevationEdgeModel.DoorwayEdge> doorways = BuildDoorwayEdges(layout, surfaces);
            List<ElevationEdgeModel.GatewayConnectionEnd> gatewayConnectionEnds =
                BuildGatewayConnectionEnds(layout, surfaces);
            List<ElevationEdgeModel.InternalPathEdge> internalPathEdges = BuildInternalPathEdges(layout, surfaces, cellRoomIds, transitions);
            // Copied, because the passes below MUTATE it and the plan's own copy
            // is what a later pass would consult. A shared array would let a
            // demotion here rewrite the answer the bridge pass was given.
            var enclosedRooms = new bool[plannedEnclosedRooms.Length];
            Array.Copy(plannedEnclosedRooms, enclosedRooms, plannedEnclosedRooms.Length);
            // M4b. It runs AFTER BuildInternalPathEdges, which only asks whether
            // a cell is in some room, and BEFORE the two sealed-room passes, so
            // the chambers it adds are validated rather than exempt.
            SubdivideOversizeRoomsIntoChambers(
                layout,
                recipePlacements,
                prisms,
                cellRoomIds,
                doorways,
                ref enclosedRooms);
            DemoteSealedEnclosedRooms(enclosedRooms, doorways, cellRoomIds);
            if (!ValidateEnclosedRoomDoorways(enclosedRooms, doorways, cellRoomIds, out rejectionReason))
            {
                return false;
            }

            context = new ElevationEdgeModel.RoomBoundaryContext(
                cellRoomIds,
                enclosedRooms,
                doorways,
                internalPathEdges,
                random?.Next() ?? 0,
                gatewayConnectionEnds);
            return true;
        }

        private static List<ElevationEdgeModel.InternalPathEdge> BuildInternalPathEdges(
            DungeonLayout layout,
            SurfaceField surfaces,
            IReadOnlyDictionary<Vector2Int, int> cellRoomIds,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            var edges = new Dictionary<WallEdge, bool>();
            HashSet<Vector2Int> blockedCells = BuildInternalPathBlockedCells(transitions);
            for (int connectionIndex = 0; connectionIndex < layout.connections.Count; connectionIndex++)
            {
                RoomConnection connection = layout.connections[connectionIndex];
                List<Vector2Int> path = CleanPath(connection.path, layout.floorCells);
                AddInternalPathRuns(path, connectionIndex, surfaces, cellRoomIds, blockedCells, edges);
            }

            var result = new List<ElevationEdgeModel.InternalPathEdge>(edges.Count);
            foreach (KeyValuePair<WallEdge, bool> item in edges)
            {
                result.Add(new ElevationEdgeModel.InternalPathEdge(
                    item.Key.cell,
                    item.Key.direction,
                    item.Value
                        ? ElevationEdgeModel.InternalPathEdgeGuard.Railing
                        : ElevationEdgeModel.InternalPathEdgeGuard.Bare));
            }

            result.Sort((first, second) =>
            {
                int compare = CompareCells(first.cell, second.cell);
                return compare != 0 ? compare : first.direction.CompareTo(second.direction);
            });
            return result;
        }

        private static HashSet<Vector2Int> BuildInternalPathBlockedCells(IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            var blocked = new HashSet<Vector2Int>();
            if (transitions == null)
            {
                return blocked;
            }

            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                blocked.Add(transition.firstCell);
                blocked.Add(transition.secondCell);
                foreach (Vector2Int cell in transition.lowerLandingCells)
                {
                    blocked.Add(cell);
                }

                foreach (Vector2Int cell in transition.upperLandingCells)
                {
                    blocked.Add(cell);
                }

                foreach (Vector2Int cell in transition.footprintCells)
                {
                    blocked.Add(cell);
                }
            }

            return blocked;
        }

        private static void AddInternalPathRuns(
            IReadOnlyList<Vector2Int> path,
            int connectionIndex,
            SurfaceField surfaces,
            IReadOnlyDictionary<Vector2Int, int> cellRoomIds,
            HashSet<Vector2Int> blockedCells,
            Dictionary<WallEdge, bool> edges)
        {
            var run = new List<Vector2Int>();
            Vector2Int runDirection = Vector2Int.zero;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                Vector2Int first = path[i];
                Vector2Int second = path[i + 1];
                Vector2Int direction = second - first;
                bool validStep =
                    Mathf.Abs(direction.x) + Mathf.Abs(direction.y) == 1 &&
                    IsInternalPathCell(first, surfaces, cellRoomIds, blockedCells) &&
                    IsInternalPathCell(second, surfaces, cellRoomIds, blockedCells);

                if (!validStep)
                {
                    AddInternalPathRun(run, runDirection, connectionIndex, surfaces, cellRoomIds, blockedCells, edges);
                    run.Clear();
                    runDirection = Vector2Int.zero;
                    continue;
                }

                if (run.Count == 0)
                {
                    run.Add(first);
                    run.Add(second);
                    runDirection = direction;
                    continue;
                }

                if (direction == runDirection && run[run.Count - 1] == first)
                {
                    run.Add(second);
                    continue;
                }

                AddInternalPathRun(run, runDirection, connectionIndex, surfaces, cellRoomIds, blockedCells, edges);
                run.Clear();
                run.Add(first);
                run.Add(second);
                runDirection = direction;
            }

            AddInternalPathRun(run, runDirection, connectionIndex, surfaces, cellRoomIds, blockedCells, edges);
        }

        private static bool IsInternalPathCell(
            Vector2Int cell,
            SurfaceField surfaces,
            IReadOnlyDictionary<Vector2Int, int> cellRoomIds,
            HashSet<Vector2Int> blockedCells)
        {
            return surfaces.HasFloor(cell) && !cellRoomIds.ContainsKey(cell) && !blockedCells.Contains(cell);
        }

        private static void AddInternalPathRun(
            IReadOnlyList<Vector2Int> run,
            Vector2Int runDirection,
            int connectionIndex,
            SurfaceField surfaces,
            IReadOnlyDictionary<Vector2Int, int> cellRoomIds,
            HashSet<Vector2Int> blockedCells,
            Dictionary<WallEdge, bool> edges)
        {
            if (run.Count < InternalOpenPathMinRunCells)
            {
                return;
            }

            int[] sideDirections = runDirection.x != 0
                ? new[] { Direction.North, Direction.South }
                : new[] { Direction.East, Direction.West };

            foreach (int sideDirection in sideDirections)
            {
                bool railing = InternalPathSideGetsRailing(connectionIndex, run[0], run[run.Count - 1], sideDirection);
                foreach (Vector2Int cell in run)
                {
                    TryAddInternalPathEdge(cell, sideDirection, railing, surfaces, cellRoomIds, blockedCells, edges);
                }
            }
        }

        private static bool InternalPathSideGetsRailing(
            int connectionIndex,
            Vector2Int start,
            Vector2Int end,
            int sideDirection)
        {
            int hash = StairForge.StableHash($"internalPath:{connectionIndex}:{start.x},{start.y}:{end.x},{end.y}:{sideDirection}");
            return (hash & int.MaxValue) % 100 < InternalOpenPathRailingPercent;
        }

        private static void TryAddInternalPathEdge(
            Vector2Int cell,
            int direction,
            bool railing,
            SurfaceField surfaces,
            IReadOnlyDictionary<Vector2Int, int> cellRoomIds,
            HashSet<Vector2Int> blockedCells,
            Dictionary<WallEdge, bool> edges)
        {
            if (!surfaces.TryGetFloorLevel(cell, out int level))
            {
                return;
            }

            Vector2Int neighbor = cell + DirectionVectorInt(direction);
            if (blockedCells.Contains(neighbor))
            {
                return;
            }

            if (cellRoomIds.ContainsKey(neighbor))
            {
                return;
            }

            if (surfaces.TryGetFloorLevel(neighbor, out int neighborLevel) && neighborLevel == level)
            {
                return;
            }

            var edge = new WallEdge(cell, direction);
            if (!edges.TryGetValue(edge, out bool existingRailing) || railing && !existingRailing)
            {
                edges[edge] = railing;
            }
        }

        private static Dictionary<Vector2Int, int> BuildCellRoomIds(DungeonLayout layout)
        {
            var cellRoomIds = new Dictionary<Vector2Int, int>();
            for (int roomIndex = 0; roomIndex < layout.rooms.Count; roomIndex++)
            {
                foreach (Vector2Int cell in layout.rooms[roomIndex].CellsRowMajor())
                {
                    cellRoomIds[cell] = roomIndex;
                }
            }

            return cellRoomIds;
        }

        // surfaces filters STALE doorways (render path): a doorway is a walk
        // opening, so if either side lost its floor — the corridor was replaced
        // by a bridge span — the gap must not be cut into the room's enclosure
        // wall (seen in-editor 2026-06-11: a lone railing in a partition gap
        // opening onto the span void). Planning passes null: the no-strip-in-
        // doorway rule applies to every path crossing regardless of leveling.
        private static List<ElevationEdgeModel.DoorwayEdge> BuildDoorwayEdges(
            DungeonLayout layout,
            SurfaceField surfaces)
        {
            var doorways = new List<ElevationEdgeModel.DoorwayEdge>();
            var keys = new HashSet<string>();
            for (int connectionIndex = 0; connectionIndex < layout.connections.Count; connectionIndex++)
            {
                RoomConnection connection = layout.connections[connectionIndex];
                List<Vector2Int> path = CleanPath(connection.path, layout.floorCells);
                AddRoomDoorwayEdge(
                    layout.rooms[connection.fromRoom],
                    path,
                    surfaces,
                    connectionIndex,
                    keys,
                    doorways);
                AddRoomDoorwayEdge(
                    layout.rooms[connection.toRoom],
                    path,
                    surfaces,
                    connectionIndex,
                    keys,
                    doorways);
            }

            return doorways;
        }

        private static List<ElevationEdgeModel.GatewayConnectionEnd> BuildGatewayConnectionEnds(
            DungeonLayout layout,
            SurfaceField surfaces)
        {
            var connectionEnds =
                new List<ElevationEdgeModel.GatewayConnectionEnd>(
                    layout.connections.Count * 2);
            for (int connectionIndex = 0;
                 connectionIndex < layout.connections.Count;
                 connectionIndex++)
            {
                RoomConnection connection = layout.connections[connectionIndex];
                List<Vector2Int> path = CleanPath(
                    connection.path,
                    layout.floorCells);
                AddGatewayConnectionEnd(
                    layout.rooms[connection.fromRoom],
                    layout.rooms,
                    connection.fromRoom,
                    path,
                    surfaces,
                    connectionIndex,
                    endIndex: 0,
                    scanForward: true,
                    connectionEnds);
                AddGatewayConnectionEnd(
                    layout.rooms[connection.toRoom],
                    layout.rooms,
                    connection.toRoom,
                    path,
                    surfaces,
                    connectionIndex,
                    endIndex: 1,
                    scanForward: false,
                    connectionEnds);
            }

            return connectionEnds;
        }

        private static void AddGatewayConnectionEnd(
            RoomFootprint room,
            IReadOnlyList<RoomFootprint> rooms,
            int roomId,
            IReadOnlyList<Vector2Int> path,
            SurfaceField surfaces,
            int connectionIndex,
            int endIndex,
            bool scanForward,
            List<ElevationEdgeModel.GatewayConnectionEnd> connectionEnds)
        {
            if (path == null || path.Count < 2)
            {
                return;
            }

            int index = scanForward ? 0 : path.Count - 2;
            int limit = scanForward ? path.Count - 1 : -1;
            int scanStep = scanForward ? 1 : -1;
            for (; index != limit; index += scanStep)
            {
                bool firstInside = room.Contains(path[index]);
                bool secondInside = room.Contains(path[index + 1]);
                if (firstInside == secondInside)
                {
                    continue;
                }

                if (surfaces != null &&
                    (!surfaces.HasFloor(path[index]) ||
                     !surfaces.HasFloor(path[index + 1])))
                {
                    return;
                }

                int insideIndex = firstInside ? index : index + 1;
                int outsideIndex = firstInside ? index + 1 : index;
                int outwardStep = outsideIndex > insideIndex ? 1 : -1;
                var outwardPath = new List<Vector2Int>
                {
                    path[insideIndex]
                };
                for (int pathIndex = outsideIndex;
                     pathIndex >= 0 && pathIndex < path.Count;
                     pathIndex += outwardStep)
                {
                    if (pathIndex != outsideIndex &&
                        (room.Contains(path[pathIndex]) ||
                         CellBelongsToOtherRoom(
                             path[pathIndex],
                             rooms,
                             roomId)))
                    {
                        break;
                    }

                    outwardPath.Add(path[pathIndex]);
                }

                connectionEnds.Add(
                    new ElevationEdgeModel.GatewayConnectionEnd(
                        new ElevationEdgeModel.DoorwayEdge(
                            path[index],
                            path[index + 1],
                            connectionIndex),
                        endIndex,
                        outwardPath));
                return;
            }
        }

        private static bool CellBelongsToOtherRoom(
            Vector2Int cell,
            IReadOnlyList<RoomFootprint> rooms,
            int owningRoomId)
        {
            for (int roomIndex = 0; roomIndex < rooms.Count; roomIndex++)
            {
                if (roomIndex != owningRoomId &&
                    rooms[roomIndex].Contains(cell))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddRoomDoorwayEdge(
            RoomFootprint room,
            IReadOnlyList<Vector2Int> path,
            SurfaceField surfaces,
            int connectionIndex,
            HashSet<string> keys,
            List<ElevationEdgeModel.DoorwayEdge> doorways)
        {
            for (int i = 0; i + 1 < path.Count; i++)
            {
                bool firstInside = room.Contains(path[i]);
                bool secondInside = room.Contains(path[i + 1]);
                if (firstInside == secondInside)
                {
                    continue;
                }

                if (surfaces != null &&
                    (!surfaces.HasFloor(path[i]) || !surfaces.HasFloor(path[i + 1])))
                {
                    return;
                }

                string key = TransitionKey(path[i], path[i + 1]);
                if (keys.Add(key))
                {
                    doorways.Add(new ElevationEdgeModel.DoorwayEdge(
                        path[i],
                        path[i + 1],
                        connectionIndex));
                }

                return;
            }
        }

        // A room whose every path doorway went stale (its corridors were replaced
        // by bridge spans) is still reachable over the bridge ports, but enclosing
        // it would seal partition walls shut around a deck entry; keep such rooms
        // open instead of rejecting the whole build.
        private static void DemoteSealedEnclosedRooms(
            bool[] enclosedRooms,
            IReadOnlyList<ElevationEdgeModel.DoorwayEdge> doorways,
            IReadOnlyDictionary<Vector2Int, int> cellRoomIds)
        {
            var doorwayCounts = new int[enclosedRooms.Length];
            foreach (ElevationEdgeModel.DoorwayEdge doorway in doorways)
            {
                if (cellRoomIds.TryGetValue(doorway.firstCell, out int firstRoom) &&
                    firstRoom >= 0 &&
                    firstRoom < doorwayCounts.Length)
                {
                    doorwayCounts[firstRoom]++;
                }

                if (cellRoomIds.TryGetValue(doorway.secondCell, out int secondRoom) &&
                    secondRoom >= 0 &&
                    secondRoom < doorwayCounts.Length)
                {
                    doorwayCounts[secondRoom]++;
                }
            }

            for (int i = 0; i < enclosedRooms.Length; i++)
            {
                if (enclosedRooms[i] && doorwayCounts[i] == 0)
                {
                    enclosedRooms[i] = false;
                }
            }
        }

        private static bool[] ChooseEnclosedRooms(int roomCount, System.Random random)
        {
            DungeonGenerationSettings settings = CurrentGenerationSettings.Validated();
            var enclosed = new bool[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                enclosed[i] = random.NextDouble() < settings.enclosedRoomChance;
            }

            if (roomCount <= 1)
            {
                enclosed[0] = true;
                return enclosed;
            }

            if (!Any(enclosed, expected: true))
            {
                enclosed[random.Next(roomCount)] = true;
            }

            // "At least one open room" was a variety guarantee for a floorplan
            // where rooms never touched. Once rooms abut it is in direct conflict
            // with "no two rooms silently merge": IsPartitionWallEdge returns
            // false when neither room is enclosed, so an unenclosed room beside
            // an unenclosed neighbour becomes one open field with no wall between
            // them. From density 4 up, the second guarantee wins (design §4.3).
            if (settings.densityLevel < OpenRoomGuaranteeMaxDensityLevel &&
                !Any(enclosed, expected: false))
            {
                enclosed[random.Next(roomCount)] = false;
            }

            return enclosed;
        }

        private static bool Any(bool[] values, bool expected)
        {
            foreach (bool value in values)
            {
                if (value == expected)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ValidateEnclosedRoomDoorways(
            IReadOnlyList<bool> enclosedRooms,
            IReadOnlyList<ElevationEdgeModel.DoorwayEdge> doorways,
            IReadOnlyDictionary<Vector2Int, int> cellRoomIds,
            out string rejectionReason)
        {
            var doorwayCounts = new int[enclosedRooms.Count];
            foreach (ElevationEdgeModel.DoorwayEdge doorway in doorways)
            {
                if (cellRoomIds.TryGetValue(doorway.firstCell, out int firstRoom) &&
                    firstRoom >= 0 &&
                    firstRoom < doorwayCounts.Length)
                {
                    doorwayCounts[firstRoom]++;
                }

                if (cellRoomIds.TryGetValue(doorway.secondCell, out int secondRoom) &&
                    secondRoom >= 0 &&
                    secondRoom < doorwayCounts.Length)
                {
                    doorwayCounts[secondRoom]++;
                }
            }

            for (int i = 0; i < enclosedRooms.Count; i++)
            {
                if (!enclosedRooms[i] || doorwayCounts[i] > 0)
                {
                    continue;
                }

                rejectionReason = $"enclosed room {i} would be sealed with no doorway";
                return false;
            }

            rejectionReason = string.Empty;
            return true;
        }

        private static List<Vector2Int> CleanPath(IReadOnlyList<Vector2Int> path, HashSet<Vector2Int> floorCells)
        {
            var result = new List<Vector2Int>();
            foreach (Vector2Int cell in path)
            {
                if (!floorCells.Contains(cell) || result.Count > 0 && result[result.Count - 1] == cell)
                {
                    continue;
                }

                result.Add(cell);
            }

            return result;
        }

        private static bool ValidatePathCardinality(IReadOnlyList<Vector2Int> path, out string rejectionReason)
        {
            for (int i = 0; i + 1 < path.Count; i++)
            {
                if (AreCardinalNeighbors(path[i], path[i + 1]))
                {
                    continue;
                }

                rejectionReason = $"corridor path had non-cardinal step {path[i]}->{path[i + 1]}";
                return false;
            }

            rejectionReason = string.Empty;
            return true;
        }

        private static bool TryChooseReviewedActiveStairTransition(
            IReadOnlyList<ReviewedActiveStairOption> options,
            int rise,
            int maxLaneCount,
            IReadOnlyList<Vector2Int> path,
            ZoneArea fromRoom,
            ZoneArea toRoom,
            HashSet<Vector2Int> layoutFloorCells,
            SurfaceField surfaces,
            int fromLevel,
            int toLevel,
            System.Random random,
            bool allowExternalSpan,
            string requiredPlacementClass,
            SortedDictionary<string, int> stairCandidateCounts,
            PrismLedger plannedStairLedger,
            out int transitionIndex,
            out Vector2Int lowerLandingCell,
            out Vector2Int upperLandingCell,
            out Vector2Int[] lowerLandingCells,
            out Vector2Int[] upperLandingCells,
            out Vector2Int[] footprintCells,
            out Vector2Int transitionFirstCell,
            out Vector2Int transitionSecondCell,
            out int lowerPortDirection,
            out int upperPortDirection,
            out string placementClass,
            out ReviewedActiveStairOption selected)
        {
            transitionIndex = -1;
            lowerLandingCell = default;
            upperLandingCell = default;
            lowerLandingCells = Array.Empty<Vector2Int>();
            upperLandingCells = Array.Empty<Vector2Int>();
            footprintCells = Array.Empty<Vector2Int>();
            transitionFirstCell = default;
            transitionSecondCell = default;
            lowerPortDirection = 0;
            upperPortDirection = 0;
            placementClass = EmbeddedStairPlacementClass;
            selected = default;
            if (path == null || fromLevel == toLevel)
            {
                return false;
            }

            int lastFromIndex = 0;
            for (int i = 0; i < path.Count; i++)
            {
                if (fromRoom.Contains(path[i]))
                {
                    lastFromIndex = i;
                }
            }

            int firstToIndex = path.Count - 1;
            for (int i = 0; i < path.Count; i++)
            {
                if (toRoom.Contains(path[i]))
                {
                    firstToIndex = i;
                    break;
                }
            }

            bool climbsFromConnectionStart = fromLevel < toLevel;
            // Untyped connections use side-floor support to distinguish an embedded
            // stair from an external span. A declared Stair must stay embedded even
            // in its intentionally narrow corridor; the route contract forbids
            // reclassifying it as a bridge.
            bool requireEmbeddedSideFloorSupport = !string.Equals(
                requiredPlacementClass,
                EmbeddedStairPlacementClass,
                StringComparison.Ordinal);
            var candidates = new List<StairTransitionCandidate>();
            AddReviewedActiveStairTransitionCandidates(
                options,
                rise,
                maxLaneCount,
                path,
                climbsFromConnectionStart,
                lastFromIndex,
                firstToIndex,
                layoutFloorCells,
                surfaces,
                Mathf.Min(fromLevel, toLevel),
                Mathf.Max(fromLevel, toLevel),
                allowExternalSpan,
                preferredOnly: true,
                requireEmbeddedSideFloorSupport,
                candidates);
            RemoveCandidatesOutsideRequiredPlacementClass(candidates, requiredPlacementClass);
            RemovePlannedStairConflicts(
                candidates,
                plannedStairLedger,
                Mathf.Min(fromLevel, toLevel),
                Mathf.Max(fromLevel, toLevel));
            if (candidates.Count == 0)
            {
                AddReviewedActiveStairTransitionCandidates(
                    options,
                    rise,
                    maxLaneCount,
                    path,
                    climbsFromConnectionStart,
                    lastFromIndex,
                    firstToIndex,
                    layoutFloorCells,
                    surfaces,
                    Mathf.Min(fromLevel, toLevel),
                    Mathf.Max(fromLevel, toLevel),
                    allowExternalSpan,
                    preferredOnly: false,
                    requireEmbeddedSideFloorSupport,
                    candidates);
                RemoveCandidatesOutsideRequiredPlacementClass(candidates, requiredPlacementClass);
                RemovePlannedStairConflicts(
                    candidates,
                    plannedStairLedger,
                    Mathf.Min(fromLevel, toLevel),
                    Mathf.Max(fromLevel, toLevel));
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            AccumulateStairCandidateCounts(candidates, stairCandidateCounts);
            StairTransitionCandidate candidate = ChooseStairTransitionCandidate(candidates, random);
            // Keep the original weighted draw stable. Only a drawn multi-lane
            // candidate that immediately narrows at either port is replaced, and
            // the replacement consumes no extra shared random draw.
            if (!StairCandidateHasFullWidthLandingContinuation(
                    candidate,
                    layoutFloorCells,
                    surfaces,
                    Mathf.Min(fromLevel, toLevel),
                    Mathf.Max(fromLevel, toLevel)) &&
                !TryChooseSingleLaneFallback(candidates, candidate, out candidate))
            {
                return false;
            }

            transitionIndex = candidate.transitionIndex;
            lowerLandingCell = candidate.lowerLandingCell;
            upperLandingCell = candidate.upperLandingCell;
            lowerLandingCells = candidate.lowerLandingCells;
            upperLandingCells = candidate.upperLandingCells;
            footprintCells = candidate.footprintCells;
            transitionFirstCell = candidate.transitionFirstCell;
            transitionSecondCell = candidate.transitionSecondCell;
            lowerPortDirection = candidate.lowerPortDirection;
            upperPortDirection = candidate.upperPortDirection;
            placementClass = candidate.placementClass;
            selected = candidate.option;
            return true;
        }

        private static void RemoveCandidatesOutsideRequiredPlacementClass(
            List<StairTransitionCandidate> candidates,
            string requiredPlacementClass)
        {
            if (string.IsNullOrEmpty(requiredPlacementClass))
            {
                return;
            }

            candidates.RemoveAll(candidate => !string.Equals(
                candidate.placementClass,
                requiredPlacementClass,
                StringComparison.Ordinal));
        }

        // Online synthesis (step 7, decisions 16-21): builds straight designs from
        // the forge grammar for this exact rise, validates each through BOTH real
        // contract parsers (the planner-side geometry parser and the edge-model
        // parser in its headless, no-GameObjects form), then runs them through the
        // regular placement search, level gates and ledger pruning. Fallback-only:
        // callers invoke this after the reviewed pool produced zero candidates, so
        // decision 11 (pool competes on equal terms) is untouched and synthesis
        // usage measures exactly where the pool failed.
        private static bool TrySynthesizeActiveStairTransition(
            int dungeonSeed,
            int fromRoomIndex,
            int toRoomIndex,
            int rise,
            IReadOnlyList<Vector2Int> path,
            ZoneArea fromNodeRect,
            ZoneArea toNodeRect,
            HashSet<Vector2Int> layoutFloorCells,
            SurfaceField surfaces,
            int fromLevel,
            int toLevel,
            string requiredPlacementClass,
            SortedDictionary<string, int> stairCandidateCounts,
            PrismLedger plannedStairLedger,
            out int transitionIndex,
            out Vector2Int lowerLandingCell,
            out Vector2Int upperLandingCell,
            out Vector2Int[] lowerLandingCells,
            out Vector2Int[] upperLandingCells,
            out Vector2Int[] footprintCells,
            out Vector2Int transitionFirstCell,
            out Vector2Int transitionSecondCell,
            out int lowerPortDirection,
            out int upperPortDirection,
            out string placementClass,
            out ReviewedActiveStairOption selected,
            out ElevationEdgeModel.SynthesizedStairSetPiece synthesizedSetPiece,
            out string gapId)
        {
            transitionIndex = -1;
            lowerLandingCell = default;
            upperLandingCell = default;
            lowerLandingCells = Array.Empty<Vector2Int>();
            upperLandingCells = Array.Empty<Vector2Int>();
            footprintCells = Array.Empty<Vector2Int>();
            transitionFirstCell = default;
            transitionSecondCell = default;
            lowerPortDirection = 0;
            upperPortDirection = 0;
            placementClass = EmbeddedStairPlacementClass;
            selected = default;
            synthesizedSetPiece = null;
            gapId = $"{fromRoomIndex}->{toRoomIndex}r{rise}";

            List<StairForge.SynthesizedStaircaseDesign> designs;
            try
            {
                designs = StairForge.EnumerateSynthesisDesigns(rise, out string failureSummary);
                if (designs.Count == 0)
                {
                    LogPlanningWarning($"Dungeon Lab Generate: synthesis produced no designs for rise {rise}: {failureSummary}");
                    return false;
                }
            }
            catch (Exception exception)
            {
                LogPlanningWarning($"Dungeon Lab Generate: synthesis failed for rise {rise}: {exception.Message}");
                return false;
            }

            PreparedSynthesizedStairCatalog preparedCatalog =
                PrepareSynthesizedStairCatalog(designs, "synthesized");
            if (preparedCatalog.options.Count == 0)
            {
                return false;
            }

            // Determinism (decision 18): per-gap RNG keyed by dungeon seed + gap id,
            // so adding or removing gaps never reshuffles another gap's synthesis.
            var gapRandom = new System.Random(dungeonSeed ^ StairForge.StableHash($"synth:{fromRoomIndex}:{toRoomIndex}:{rise}"));
            if (!TryChooseReviewedActiveStairTransition(
                    preparedCatalog.options,
                    rise,
                    maxLaneCount: MaxActiveStairLaneCount,
                    path,
                    fromNodeRect,
                    toNodeRect,
                    layoutFloorCells,
                    surfaces,
                    fromLevel,
                    toLevel,
                    gapRandom,
                    // Decision 33: bridge-style designs place as external spans
                    // between landings, on equal terms with embedded designs.
                    allowExternalSpan: true,
                    requiredPlacementClass,
                    stairCandidateCounts,
                    plannedStairLedger,
                    out transitionIndex,
                    out lowerLandingCell,
                    out upperLandingCell,
                    out lowerLandingCells,
                    out upperLandingCells,
                    out footprintCells,
                    out transitionFirstCell,
                    out transitionSecondCell,
                    out lowerPortDirection,
                    out upperPortDirection,
                    out placementClass,
                    out selected))
            {
                return false;
            }

            StairForge.SynthesizedStaircaseDesign chosen = preparedCatalog.designsByName[selected.name];
            synthesizedSetPiece = new ElevationEdgeModel.SynthesizedStairSetPiece(chosen.name, chosen.contract, chosen.pieces);
            return true;
        }

        // StairForge already caches immutable synthesized designs by rise and
        // measured-library version. Preparing those same contracts for the planner
        // was still repeating both canonical parsers for every failed tier attempt.
        // Key this second-stage cache by the forge's returned list instance: a
        // measured-library invalidation produces a new list, while the weak key lets
        // the superseded preparation disappear without a parallel invalidation path.
        private sealed class PreparedSynthesizedStairCatalog
        {
            internal readonly IReadOnlyList<ReviewedActiveStairOption> options;
            internal readonly IReadOnlyDictionary<string, StairForge.SynthesizedStaircaseDesign> designsByName;

            internal PreparedSynthesizedStairCatalog(
                IReadOnlyList<ReviewedActiveStairOption> options,
                IReadOnlyDictionary<string, StairForge.SynthesizedStaircaseDesign> designsByName)
            {
                this.options = options;
                this.designsByName = designsByName;
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            List<StairForge.SynthesizedStaircaseDesign>,
            PreparedSynthesizedStairCatalog> PreparedSynthesizedStairCatalogs =
                new System.Runtime.CompilerServices.ConditionalWeakTable<
                    List<StairForge.SynthesizedStaircaseDesign>,
                    PreparedSynthesizedStairCatalog>();

        private static PreparedSynthesizedStairCatalog PrepareSynthesizedStairCatalog(
            List<StairForge.SynthesizedStaircaseDesign> designs,
            string diagnosticLabel)
        {
            if (PreparedSynthesizedStairCatalogs.TryGetValue(
                    designs,
                    out PreparedSynthesizedStairCatalog cached))
            {
                return cached;
            }

            var options = new List<ReviewedActiveStairOption>(designs.Count);
            var designsByName = new Dictionary<string, StairForge.SynthesizedStaircaseDesign>(
                designs.Count,
                StringComparer.Ordinal);
            foreach (StairForge.SynthesizedStaircaseDesign design in designs)
            {
                string parserError = ElevationEdgeModel.ValidateSynthesizedContractToken(
                    design.contract,
                    StairForge.LevelHeight);
                if (!string.IsNullOrEmpty(parserError))
                {
                    LogPlanningWarning(
                        $"Dungeon Lab Generate: {diagnosticLabel} design '{design.name}' rejected by the edge-model parser: {parserError}");
                    continue;
                }

                if (!TryBuildSynthesizedStairOption(
                        design.contract,
                        out ReviewedActiveStairOption option,
                        out string optionError))
                {
                    LogPlanningWarning(
                        $"Dungeon Lab Generate: {diagnosticLabel} design '{design.name}' rejected by the planner parser: {optionError}");
                    continue;
                }

                options.Add(option);
                designsByName[option.name] = design;
            }

            var prepared = new PreparedSynthesizedStairCatalog(options.ToArray(), designsByName);
            PreparedSynthesizedStairCatalogs.Add(designs, prepared);
            return prepared;
        }

        // The synthesized counterpart of TryAppendReviewedActiveStairOption: same
        // geometry parser, but the prefab is a sentinel (no asset exists) and the
        // option never enters the reviewed pool — it serves exactly one gap.
        private static bool TryBuildSynthesizedStairOption(
            JObject contractToken,
            out ReviewedActiveStairOption option,
            out string error)
        {
            option = default;
            error = string.Empty;
            string name = contractToken.Value<string>("name") ?? string.Empty;
            string prefabPath = NormalizeAssetPath(contractToken.Value<string>("prefab") ?? string.Empty);
            string topology = contractToken.Value<string>("topology") ?? string.Empty;
            bool isBridge = contractToken.Value<bool?>("bridgeAllowed") == true;
            int rise = contractToken.Value<int?>("rise") ?? 0;
            int laneCount = contractToken.Value<int?>("laneCount") ?? 0;
            int runLength = contractToken.Value<int?>("runLength") ?? 0;
            bool deckTopology = string.Equals(topology, ActiveDeckStairTopology, StringComparison.Ordinal);
            if ((deckTopology ? rise != 0 : rise <= 0) || laneCount <= 0 || runLength <= 0 ||
                string.IsNullOrWhiteSpace(prefabPath) ||
                !ActiveGenerationSupportsStairTopology(topology))
            {
                error = "synthesized contract is missing rise/laneCount/runLength/prefab sentinel or has an unsupported topology";
                return false;
            }

            if (!TryParseReviewedActiveStairGeometry(
                    contractToken,
                    out Vector2 localBoundsMin,
                    out Vector2 localBoundsMax,
                    out Vector2Int[] footprintCells,
                    out Vector2Int[] entryCells,
                    out Vector2Int[] exitCells,
                    out Vector2 localEntryPoint,
                    out Vector2 localExitPoint,
                    out int entryDirection,
                    out int exitDirection,
                    out string geometryError))
            {
                error = geometryError;
                return false;
            }

            option = new ReviewedActiveStairOption(
                name,
                prefabPath,
                rise,
                laneCount,
                runLength,
                topology,
                isBridge,
                localBoundsMin,
                localBoundsMax,
                footprintCells,
                entryCells,
                exitCells,
                localEntryPoint,
                localExitPoint,
                entryDirection,
                exitDirection);
            return true;
        }

        // Explicit bridge geometry retained solely by the stacked-crossing
        // fixtures. There is no room-pair scan, candidate lottery or production
        // caller: production bridges come only from RouteTransitionKind.Bridge.
        private const int FixtureBridgeShortcutFactor = 3;
        private readonly struct FixtureBridgeCandidate
        {
            public readonly int roomA;
            public readonly int roomB;
            public readonly Vector2Int landingA;
            public readonly Vector2Int landingB;
            public readonly Vector2Int lineDirection;
            public readonly List<Vector2Int> gapCells;

            public FixtureBridgeCandidate(int roomA, int roomB, Vector2Int landingA, Vector2Int landingB, Vector2Int lineDirection, List<Vector2Int> gapCells)
            {
                this.roomA = roomA;
                this.roomB = roomB;
                this.landingA = landingA;
                this.landingB = landingB;
                this.lineDirection = lineDirection;
                this.gapCells = gapCells;
            }
        }

        private static void AddExplicitFixtureBridge(
            SurfaceField surfaces,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            PrismLedger plannedStairLedger)
        {
            var gapCells = new List<Vector2Int>();
            for (int x = -2; x <= 2; x++)
            {
                gapCells.Add(new Vector2Int(x, 0));
            }

            var candidate = new FixtureBridgeCandidate(
                0,
                1,
                new Vector2Int(-3, 0),
                new Vector2Int(3, 0),
                Vector2Int.right,
                gapCells);
            if (!TryPlaceFixtureBridge(
                    candidate,
                    transitions,
                    new HashSet<string>(),
                    plannedStairLedger,
                    new List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)>(),
                    surfaces))
            {
                throw new InvalidOperationException(
                    "The explicit stacked-crossing fixture bridge could not be realized.");
            }
        }

        // Decision 32: bridges are shortcuts, not alternatives. BFS the live walk
        // network — equal-level SURFACE adjacency plus every placed transition
        // (stairs, strips, seams and earlier fixture decks, so twin bridges
        // self-exclude) — and reject when the landings are already close.
        //
        // Walks (cell, level), not cells. The lateral step always compared levels,
        // so it was only ever a surface walk with a heightfield's single surface
        // per column; the transition hop never resolved a level at all, which is
        // what lets this generalize without waiting on C2b-2.
        private static bool FixtureBridgeIsRedundant(
            FixtureBridgeCandidate candidate,
            SurfaceField surfaces,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            int threshold = FixtureBridgeShortcutFactor * (candidate.gapCells.Count + 2);
            var links = new Dictionary<Vector2Int, List<Vector2Int>>();
            void AddLink(Vector2Int from, Vector2Int to)
            {
                if (!links.TryGetValue(from, out List<Vector2Int> list))
                {
                    list = new List<Vector2Int>();
                    links[from] = list;
                }

                list.Add(to);
            }

            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                AddLink(transition.firstCell, transition.secondCell);
                AddLink(transition.secondCell, transition.firstCell);
                foreach (Vector2Int lower in transition.lowerLandingCells)
                {
                    foreach (Vector2Int upper in transition.upperLandingCells)
                    {
                        AddLink(lower, upper);
                        AddLink(upper, lower);
                    }
                }
            }

            if (!surfaces.TryGetFloorLevel(candidate.landingA, out int startLevel))
            {
                return false;
            }

            var visited = new HashSet<SurfaceKey> { new SurfaceKey(candidate.landingA, startLevel) };
            var frontier = new Queue<(SurfaceKey surface, int distance)>();
            frontier.Enqueue((new SurfaceKey(candidate.landingA, startLevel), 0));
            while (frontier.Count > 0)
            {
                (SurfaceKey surface, int distance) = frontier.Dequeue();
                if (surface.cell == candidate.landingB)
                {
                    return true;
                }

                if (distance >= threshold)
                {
                    continue;
                }

                foreach (int direction in Direction.Cardinals)
                {
                    Vector2Int neighbor = surface.cell + CardinalVector(direction);
                    var step = new SurfaceKey(neighbor, surface.level);
                    if (surfaces.HasSurfaceAt(neighbor, surface.level) && visited.Add(step))
                    {
                        frontier.Enqueue((step, distance + 1));
                    }
                }

                if (links.TryGetValue(surface.cell, out List<Vector2Int> linked))
                {
                    foreach (Vector2Int neighbor in linked)
                    {
                        // A transition connects two COLUMNS and cannot yet say
                        // which surfaces it joins (C2b-2), so the hop reaches
                        // every surface in the linked column. That OVER-states
                        // connectivity, which is the safe direction here: this
                        // is a "reject bridges that duplicate an existing walk"
                        // heuristic, so over-stating rejects more bridges rather
                        // than admitting a redundant one. Single-layer it is
                        // exactly the one surface the old cell hop reached.
                        foreach (int neighborLevel in surfaces.LevelsAt(neighbor))
                        {
                            var hop = new SurfaceKey(neighbor, neighborLevel);
                            if (visited.Add(hop))
                            {
                                frontier.Enqueue((hop, distance + 1));
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static bool TryPlaceFixtureBridge(
            FixtureBridgeCandidate candidate,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            HashSet<string> transitionKeys,
            PrismLedger plannedStairLedger,
            List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs,
            SurfaceField surfaces)
        {
            // Throws on a missing landing exactly as the indexer did. The
            // candidate was only collected because both landings resolved the
            // same structural floor, and no writer can take one away, so this is
            // unreachable — and a silent 0 would place a deck at the abyss datum.
            if (!surfaces.TryGetFloorLevel(candidate.landingA, out int levelA) ||
                !surfaces.TryGetFloorLevel(candidate.landingB, out int levelB))
            {
                throw new KeyNotFoundException(
                    $"fixture bridge landing {candidate.landingA}/{candidate.landingB} lost its floor between " +
                    "candidate collection and placement");
            }
            int rise = Mathf.Abs(levelA - levelB);
            bool aIsLower = levelA <= levelB;
            Vector2Int lowerLanding = aIsLower ? candidate.landingA : candidate.landingB;
            Vector2Int upperLanding = aIsLower ? candidate.landingB : candidate.landingA;
            Vector2Int walkDirection = aIsLower ? candidate.lineDirection : -candidate.lineDirection;
            var orderedGapCells = new List<Vector2Int>(candidate.gapCells);
            if (!aIsLower)
            {
                orderedGapCells.Reverse();
            }

            int deckClearanceLevel = Mathf.Min(levelA, levelB);

            // Decision 32 follow-up: where the deck runs even with adjacent floor
            // (the hug gate permits short stretches), the railing on that side is
            // unnecessary — mask it out of the design. Flat decks only: a railing
            // beside a 1u-offset floor sits on the slope (the stair exception).
            // Bit i = contract cell x, mapped via the anchor's quarter-turn yaw.
            ulong railPlusMask = 0;
            ulong railMinusMask = 0;
            if (rise == 0)
            {
                int maskQuarterTurns = QuarterTurnsMapping(new Vector2Int(1, 0), walkDirection);
                Vector2Int worldPlusSide = RotateCardinalVector(new Vector2Int(0, 1), maskQuarterTurns);
                for (int i = 0; i < orderedGapCells.Count && i < 64; i++)
                {
                    if (surfaces.HasSurfaceAt(orderedGapCells[i] + worldPlusSide, deckClearanceLevel))
                    {
                        railPlusMask |= 1UL << i;
                    }

                    if (surfaces.HasSurfaceAt(orderedGapCells[i] - worldPlusSide, deckClearanceLevel))
                    {
                        railMinusMask |= 1UL << i;
                    }
                }
            }

            StairForge.SynthesizedStaircaseDesign design;
            try
            {
                design = StairForge.SynthesizeDeckDesign(orderedGapCells.Count, rise, railPlusMask, railMinusMask, out string failureSummary);
                if (design == null)
                {
                    LogPlanningWarning($"Dungeon Lab fixture deck synthesis failed for span {orderedGapCells.Count}: {failureSummary}");
                    return false;
                }
            }
            catch (Exception exception)
            {
                LogPlanningWarning($"Dungeon Lab fixture deck synthesis failed for span {candidate.gapCells.Count}: {exception.Message}");
                return false;
            }

            string parserError = ElevationEdgeModel.ValidateSynthesizedContractToken(design.contract, StairForge.LevelHeight);
            if (!string.IsNullOrEmpty(parserError))
            {
                LogPlanningWarning($"Dungeon Lab fixture deck '{design.name}' rejected by the edge-model parser: {parserError}");
                return false;
            }

            if (!TryBuildSynthesizedStairOption(design.contract, out ReviewedActiveStairOption option, out string optionError))
            {
                LogPlanningWarning($"Dungeon Lab fixture deck '{design.name}' rejected by the planner parser: {optionError}");
                return false;
            }

            // Decision 32: only genuine shortcuts get a deck — checked against the
            // LIVE network so earlier fixture decks count as existing paths.
            //
            if (FixtureBridgeIsRedundant(candidate, surfaces, transitions))
            {
                return false;
            }

            int entryPortDirection = DirectionFromVector(new Vector2(-walkDirection.x, -walkDirection.y));
            int exitPortDirection = DirectionFromVector(new Vector2(walkDirection.x, walkDirection.y));
            Vector2Int[] footprint = orderedGapCells.ToArray();
            var stairCandidate = new StairTransitionCandidate(
                0,
                lowerLanding,
                upperLanding,
                lowerLanding,
                upperLanding,
                entryPortDirection,
                exitPortDirection,
                new[] { lowerLanding },
                new[] { upperLanding },
                footprint,
                ExternalSpanStairPlacementClass,
                option);
            // Routine: the picker retries other lines, so conflicts stay quiet.
            if (plannedStairLedger.ConflictsWith(
                    stairCandidate,
                    Mathf.Min(levelA, levelB),
                    Mathf.Max(levelA, levelB)))
            {
                return false;
            }

            string key = TransitionKey(lowerLanding, upperLanding);
            if (!transitionKeys.Add(key))
            {
                return false;
            }

            var setPiece = new ElevationEdgeModel.SynthesizedStairSetPiece(design.name, design.contract, design.pieces);
            transitions.Add(new ElevationEdgeModel.TransitionEdge(
                lowerLanding,
                Mathf.Min(levelA, levelB),
                upperLanding,
                Mathf.Max(levelA, levelB),
                option.prefabPath,
                new[] { lowerLanding },
                new[] { upperLanding },
                footprint,
                entryPortDirection,
                exitPortDirection,
                ExternalSpanStairPlacementClass,
                setPiece));
            var bridgeOwner = new OwnerKey(
                OwnerFamily.Transition,
                $"aerial-bridge:{TransitionKey(lowerLanding, upperLanding)}");
            plannedStairLedger.Register(
                bridgeOwner,
                footprint,
                new[] { lowerLanding },
                new[] { upperLanding },
                new[] { lowerLanding, upperLanding },
                Array.Empty<Vector2Int>(),
                Array.Empty<Vector2Int>());
            // Conservative MIN landing level over every span cell (decision 34):
            // a sloped deck is never lower than this anywhere along its run.
            plannedStairLedger.RegisterSpanDeck(footprint, deckClearanceLevel, bridgeOwner);

            // The deck's cells ARE walkable surfaces (design §13, Phase C
            // systems). `SurfaceKind.Deck` had no producer until here, and the
            // consequence was not cosmetic: with no surface to point at, the port
            // graph read the deck's footprint as a stair body and deleted the
            // whole column, so a span over playable geometry severed the route it
            // crossed.
            //
            // `Deck`, so the field knows this is walkable and SUSPENDED. It
            // therefore never enters the heightfield: a cell the deck flies over
            // keeps whatever floor it has, and a cell over a true gap gains a
            // surface without gaining ground. Every floor-scoped reader — the
            // flood fill, the plan shadow, doorways, the overlook stat — is
            // untouched by construction rather than by audit.
            //
            // ONE level for the whole flat run, which is also the level the
            // ledger declares for it. Both landings were required to resolve to
            // this same structural level during candidate collection.
            foreach (Vector2Int deckCell in footprint)
            {
                surfaces.AddSurface(deckCell, deckClearanceLevel, SurfaceKind.Deck);
            }

            synthesizedStairs.Add(($"fixture-bridge:{candidate.roomA}<->{candidate.roomB}", setPiece));
            return true;
        }

        // Stairwell fallback (design decisions 26-28): third tier after the pool
        // and on-path synthesis. A 180-degree tower anchors on two ADJACENT path
        // cells (lower at fromLevel, upper at toLevel — the seam-strip transition
        // pattern) with its folded footprint on VOID cells beside the path.
        private static bool TrySynthesizeStairwellTransition(
            int dungeonSeed,
            int fromRoomIndex,
            int toRoomIndex,
            int rise,
            IReadOnlyList<Vector2Int> path,
            ZoneArea fromNodeRect,
            ZoneArea toNodeRect,
            HashSet<Vector2Int> layoutFloorCells,
            SurfaceField surfaces,
            int fromLevel,
            int toLevel,
            SortedDictionary<string, int> stairCandidateCounts,
            PrismLedger plannedStairLedger,
            out int transitionIndex,
            out Vector2Int lowerLandingCell,
            out Vector2Int upperLandingCell,
            out Vector2Int[] lowerLandingCells,
            out Vector2Int[] upperLandingCells,
            out Vector2Int[] footprintCells,
            out Vector2Int transitionFirstCell,
            out Vector2Int transitionSecondCell,
            out int lowerPortDirection,
            out int upperPortDirection,
            out string placementClass,
            out ReviewedActiveStairOption selected,
            out ElevationEdgeModel.SynthesizedStairSetPiece synthesizedSetPiece,
            out string gapId)
        {
            transitionIndex = -1;
            lowerLandingCell = default;
            upperLandingCell = default;
            lowerLandingCells = Array.Empty<Vector2Int>();
            upperLandingCells = Array.Empty<Vector2Int>();
            footprintCells = Array.Empty<Vector2Int>();
            transitionFirstCell = default;
            transitionSecondCell = default;
            lowerPortDirection = 0;
            upperPortDirection = 0;
            placementClass = EmbeddedStairPlacementClass;
            selected = default;
            synthesizedSetPiece = null;
            gapId = $"{fromRoomIndex}->{toRoomIndex}r{rise}";

            List<StairForge.SynthesizedStaircaseDesign> designs;
            try
            {
                designs = StairForge.EnumerateStairwellSynthesisDesigns(rise, out string failureSummary);
                if (designs.Count == 0)
                {
                    LogPlanningWarning($"Dungeon Lab Generate: stairwell synthesis produced no designs for rise {rise}: {failureSummary}");
                    return false;
                }
            }
            catch (Exception exception)
            {
                LogPlanningWarning($"Dungeon Lab Generate: stairwell synthesis failed for rise {rise}: {exception.Message}");
                return false;
            }

            PreparedSynthesizedStairCatalog preparedCatalog =
                PrepareSynthesizedStairCatalog(designs, "stairwell");
            if (preparedCatalog.options.Count == 0)
            {
                return false;
            }

            int lastFromIndex = 0;
            for (int i = 0; i < path.Count; i++)
            {
                if (fromNodeRect.Contains(path[i]))
                {
                    lastFromIndex = i;
                }
            }

            int firstToIndex = path.Count - 1;
            for (int i = 0; i < path.Count; i++)
            {
                if (toNodeRect.Contains(path[i]))
                {
                    firstToIndex = i;
                    break;
                }
            }

            bool climbsFromConnectionStart = fromLevel < toLevel;
            int lowerLevel = Mathf.Min(fromLevel, toLevel);
            int higherLevel = Mathf.Max(fromLevel, toLevel);
            var candidates = new List<StairTransitionCandidate>();
            foreach (ReviewedActiveStairOption option in preparedCatalog.options)
            {
                AddValidStairwellTransitionCandidates(
                    path,
                    climbsFromConnectionStart,
                    lastFromIndex,
                    firstToIndex,
                    preferredOnly: true,
                    option,
                    layoutFloorCells,
                    surfaces,
                    lowerLevel,
                    higherLevel,
                    candidates);
            }

            RemovePlannedStairConflicts(
                candidates,
                plannedStairLedger,
                Mathf.Min(fromLevel, toLevel),
                Mathf.Max(fromLevel, toLevel));
            if (candidates.Count == 0)
            {
                foreach (ReviewedActiveStairOption option in preparedCatalog.options)
                {
                    AddValidStairwellTransitionCandidates(
                        path,
                        climbsFromConnectionStart,
                        lastFromIndex,
                        firstToIndex,
                        preferredOnly: false,
                        option,
                        layoutFloorCells,
                        surfaces,
                        lowerLevel,
                        higherLevel,
                        candidates);
                }

                RemovePlannedStairConflicts(
                    candidates,
                    plannedStairLedger,
                    Mathf.Min(fromLevel, toLevel),
                    Mathf.Max(fromLevel, toLevel));
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            AccumulateStairCandidateCounts(candidates, stairCandidateCounts);
            // Determinism (decision 27): own RNG keyed by dungeon seed + gap id,
            // decorrelated from the on-path synthesis draw by the prefix.
            var gapRandom = new System.Random(dungeonSeed ^ StairForge.StableHash($"synthwell:{fromRoomIndex}:{toRoomIndex}:{rise}"));
            StairTransitionCandidate candidate = ChooseStairTransitionCandidate(candidates, gapRandom);
            transitionIndex = candidate.transitionIndex;
            lowerLandingCell = candidate.lowerLandingCell;
            upperLandingCell = candidate.upperLandingCell;
            lowerLandingCells = candidate.lowerLandingCells;
            upperLandingCells = candidate.upperLandingCells;
            footprintCells = candidate.footprintCells;
            transitionFirstCell = candidate.transitionFirstCell;
            transitionSecondCell = candidate.transitionSecondCell;
            lowerPortDirection = candidate.lowerPortDirection;
            upperPortDirection = candidate.upperPortDirection;
            placementClass = candidate.placementClass;
            selected = candidate.option;

            StairForge.SynthesizedStaircaseDesign chosen = preparedCatalog.designsByName[selected.name];
            synthesizedSetPiece = new ElevationEdgeModel.SynthesizedStairSetPiece(chosen.name, chosen.contract, chosen.pieces);
            return true;
        }

        // Anchors a stairwell option beside every adjacent path-cell pair, on
        // both sides. The option's ports sit on the same contract side with port
        // cells column-aligned in adjacent rows; the yaw maps the contract port
        // side onto the world tower->path direction, and the port-cell offset
        // must then map onto the lower->upper path step (this selects which
        // chirality fits which side).
        private static void AddValidStairwellTransitionCandidates(
            IReadOnlyList<Vector2Int> path,
            bool climbsFromConnectionStart,
            int lastFromIndex,
            int firstToIndex,
            bool preferredOnly,
            ReviewedActiveStairOption option,
            HashSet<Vector2Int> layoutFloorCells,
            SurfaceField surfaces,
            int lowerLevel,
            int higherLevel,
            List<StairTransitionCandidate> candidates)
        {
            if (option.entryDirection != option.exitDirection ||
                option.entryCells.Length != 1 ||
                option.exitCells.Length != 1)
            {
                return;
            }

            Vector2Int contractOutward = CardinalVector(option.entryDirection);
            Vector2Int portOffset = option.exitCells[0] - option.entryCells[0];
            if (Mathf.Abs(portOffset.x) + Mathf.Abs(portOffset.y) != 1)
            {
                return;
            }

            for (int i = 0; i + 1 < path.Count; i++)
            {
                if (preferredOnly && (i < lastFromIndex || i + 1 > firstToIndex))
                {
                    continue;
                }

                Vector2Int first = path[i];
                Vector2Int second = path[i + 1];
                Vector2Int step = second - first;
                if (Mathf.Abs(step.x) + Mathf.Abs(step.y) != 1)
                {
                    continue;
                }

                Vector2Int lowerPathCell = climbsFromConnectionStart ? first : second;
                Vector2Int upperPathCell = climbsFromConnectionStart ? second : first;
                Vector2Int pathRise = upperPathCell - lowerPathCell;

                foreach (Vector2Int side in new[] { new Vector2Int(-step.y, step.x), new Vector2Int(step.y, -step.x) })
                {
                    int quarterTurns = QuarterTurnsMapping(contractOutward, -side);
                    if (quarterTurns < 0 || RotateCardinalVector(portOffset, quarterTurns) != pathRise)
                    {
                        continue;
                    }

                    Vector2Int entryAnchor = lowerPathCell + side;
                    bool fits = true;
                    var worldFootprint = new Vector2Int[option.footprintCells.Length];
                    for (int f = 0; f < option.footprintCells.Length; f++)
                    {
                        Vector2Int world = entryAnchor + RotateCardinalVector(option.footprintCells[f] - option.entryCells[0], quarterTurns);
                        // Void only (decision 26): never a floor cell, never leveled.
                        if (layoutFloorCells.Contains(world) || surfaces.HasFloor(world))
                        {
                            fits = false;
                            break;
                        }

                        worldFootprint[f] = world;
                    }

                    if (!fits ||
                        !PlannedCellsAreCompatible(surfaces, new[] { lowerPathCell }, lowerLevel) ||
                        !PlannedCellsAreCompatible(surfaces, new[] { upperPathCell }, higherLevel))
                    {
                        continue;
                    }

                    int portDirection = DirectionFromVector(new Vector2(-side.x, -side.y));
                    candidates.Add(new StairTransitionCandidate(
                        i,
                        lowerPathCell,
                        upperPathCell,
                        first,
                        second,
                        portDirection,
                        portDirection,
                        new[] { lowerPathCell },
                        new[] { upperPathCell },
                        worldFootprint,
                        StairwellStairPlacementClass,
                        option));
                }
            }
        }

        private static Vector2Int CardinalVector(int direction)
        {
            Vector2 vector = DirectionVector(direction);
            return new Vector2Int(Mathf.RoundToInt(vector.x), Mathf.RoundToInt(vector.y));
        }

        // One quarter turn = +90 degrees of yaw in plan space: (x, z) -> (z, -x),
        // matching the forge's RotateCardinal and the edge model's yaw mapping.
        private static int QuarterTurnsMapping(Vector2Int from, Vector2Int to)
        {
            Vector2Int rotated = from;
            for (int i = 0; i < 4; i++)
            {
                if (rotated == to)
                {
                    return i;
                }

                rotated = new Vector2Int(rotated.y, -rotated.x);
            }

            return -1;
        }

        private static Vector2Int RotateCardinalVector(Vector2Int value, int quarterTurns)
        {
            Vector2Int result = value;
            for (int i = 0; i < quarterTurns; i++)
            {
                result = new Vector2Int(result.y, -result.x);
            }

            return result;
        }

        private static void RemovePlannedStairConflicts(
            List<StairTransitionCandidate> candidates,
            PrismLedger plannedStairLedger,
            int lowerLevel,
            int upperLevel)
        {
            if (plannedStairLedger == null)
            {
                return;
            }

            candidates.RemoveAll(candidate => plannedStairLedger.ConflictsWith(
                candidate,
                lowerLevel,
                upperLevel));
        }

        private static bool StairCandidateHasFullWidthLandingContinuation(
            StairTransitionCandidate candidate,
            HashSet<Vector2Int> layoutFloorCells,
            SurfaceField surfaces,
            int lowerLevel,
            int higherLevel)
        {
            return LandingSpanHasFullWidthContinuation(
                layoutFloorCells,
                surfaces,
                candidate.lowerLandingCells,
                candidate.lowerPortDirection,
                lowerLevel,
                candidate.option.laneCount) &&
                LandingSpanHasFullWidthContinuation(
                    layoutFloorCells,
                    surfaces,
                    candidate.upperLandingCells,
                    candidate.upperPortDirection,
                    higherLevel,
                    candidate.option.laneCount);
        }

        private static bool TryChooseSingleLaneFallback(
            IReadOnlyList<StairTransitionCandidate> candidates,
            StairTransitionCandidate rejected,
            out StairTransitionCandidate fallback)
        {
            fallback = default;
            int bestScore = -1;
            foreach (StairTransitionCandidate candidate in candidates)
            {
                if (candidate.option.laneCount != 1 ||
                    !string.Equals(candidate.placementClass, rejected.placementClass, StringComparison.Ordinal))
                {
                    continue;
                }

                int score = 0;
                if (candidate.transitionIndex == rejected.transitionIndex)
                {
                    score += 4;
                }
                if (candidate.lowerLandingCell == rejected.lowerLandingCell)
                {
                    score += 2;
                }
                if (candidate.upperLandingCell == rejected.upperLandingCell)
                {
                    score += 2;
                }

                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                fallback = candidate;
            }

            return bestScore >= 0;
        }

        private static bool LandingSpanHasFullWidthContinuation(
            HashSet<Vector2Int> layoutFloorCells,
            SurfaceField surfaces,
            IReadOnlyList<Vector2Int> landingCells,
            int outwardDirection,
            int expectedLevel,
            int laneCount)
        {
            if (laneCount <= 1)
            {
                return true;
            }

            if (layoutFloorCells == null ||
                landingCells == null ||
                landingCells.Count != laneCount)
            {
                return false;
            }

            Vector2Int outward = CardinalVector(outwardDirection);
            if (outward == Vector2Int.zero)
            {
                return false;
            }

            foreach (Vector2Int landingCell in landingCells)
            {
                Vector2Int continuationCell = landingCell + outward;
                if (!layoutFloorCells.Contains(continuationCell) ||
                    surfaces != null &&
                    surfaces.TryGetFloorLevel(continuationCell, out int continuationLevel) &&
                    continuationLevel != expectedLevel)
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddReviewedActiveStairTransitionCandidates(
            IReadOnlyList<ReviewedActiveStairOption> options,
            int rise,
            int maxLaneCount,
            IReadOnlyList<Vector2Int> path,
            bool climbsFromConnectionStart,
            int lastFromIndex,
            int firstToIndex,
            HashSet<Vector2Int> layoutFloorCells,
            SurfaceField surfaces,
            int lowerLevel,
            int higherLevel,
            bool allowExternalSpan,
            bool preferredOnly,
            bool requireEmbeddedSideFloorSupport,
            List<StairTransitionCandidate> candidates)
        {
            foreach (ReviewedActiveStairOption option in options)
            {
                if (option.rise != rise ||
                    option.laneCount <= 0 ||
                    option.laneCount > maxLaneCount ||
                    option.runLength <= 0 ||
                    path.Count < 3)
                {
                    continue;
                }

                int min;
                int max;
                if (preferredOnly)
                {
                    min = climbsFromConnectionStart
                        ? Mathf.Clamp(lastFromIndex, option.runLength, path.Count - 2)
                        : Mathf.Clamp(lastFromIndex, 0, path.Count - option.runLength - 2);
                    max = climbsFromConnectionStart
                        ? Mathf.Clamp(firstToIndex - 1, option.runLength, path.Count - 2)
                        : Mathf.Clamp(firstToIndex - 1, 0, path.Count - option.runLength - 2);
                    if (max < min)
                    {
                        continue;
                    }
                }
                else
                {
                    min = climbsFromConnectionStart ? option.runLength : 0;
                    max = climbsFromConnectionStart ? path.Count - 2 : path.Count - option.runLength - 2;
                }

                if (!option.isBridge &&
                    string.Equals(option.topology, ActiveStraightStairTopology, StringComparison.Ordinal))
                {
                    if (path.Count < option.runLength + 2)
                    {
                        continue;
                    }

                    AddValidStraightTransitionCandidates(
                        path,
                        climbsFromConnectionStart,
                        option.runLength,
                        min,
                        max,
                        option,
                        layoutFloorCells,
                        surfaces,
                        lowerLevel,
                        higherLevel,
                        requireEmbeddedSideFloorSupport,
                        candidates);
                }
                else if (!option.isBridge &&
                    string.Equals(option.topology, ActiveTurningStairTopology, StringComparison.Ordinal))
                {
                    AddValidTurningTransitionCandidates(
                        path,
                        climbsFromConnectionStart,
                        lastFromIndex,
                        firstToIndex,
                        preferredOnly,
                        option,
                        layoutFloorCells,
                        surfaces,
                        lowerLevel,
                        higherLevel,
                        requireEmbeddedSideFloorSupport,
                        candidates);
                }

                if (allowExternalSpan && option.isBridge)
                {
                    AddValidExternalSpanTransitionCandidates(
                        path,
                        climbsFromConnectionStart,
                        lastFromIndex,
                        firstToIndex,
                        preferredOnly,
                        option,
                        surfaces,
                        lowerLevel,
                        higherLevel,
                        candidates);
                }
            }
        }

        private static void AddValidExternalSpanTransitionCandidates(
            IReadOnlyList<Vector2Int> path,
            bool climbsFromConnectionStart,
            int lastFromIndex,
            int firstToIndex,
            bool preferredOnly,
            ReviewedActiveStairOption option,
            SurfaceField surfaces,
            int lowerLevel,
            int higherLevel,
            List<StairTransitionCandidate> candidates)
        {
            int maxSpan = Mathf.Clamp(option.runLength + 4, 2, 9);
            if (climbsFromConnectionStart)
            {
                for (int lowerIndex = 0; lowerIndex <= path.Count - 2; lowerIndex++)
                {
                    int upperLimit = Mathf.Min(path.Count - 1, lowerIndex + maxSpan);
                    for (int upperIndex = lowerIndex + 1; upperIndex <= upperLimit; upperIndex++)
                    {
                        if (preferredOnly && (lowerIndex < lastFromIndex || upperIndex > firstToIndex))
                        {
                            continue;
                        }

                        AddExternalSpanCandidateIfValid(
                            path,
                            lowerIndex,
                            upperIndex,
                            lowerIndex: lowerIndex,
                            upperIndex: upperIndex,
                            option,
                            surfaces,
                            lowerLevel,
                            higherLevel,
                            candidates);
                    }
                }

                return;
            }

            for (int upperIndex = 0; upperIndex <= path.Count - 2; upperIndex++)
            {
                int lowerLimit = Mathf.Min(path.Count - 1, upperIndex + maxSpan);
                for (int lowerIndex = upperIndex + 1; lowerIndex <= lowerLimit; lowerIndex++)
                {
                    if (preferredOnly && (upperIndex < lastFromIndex || lowerIndex > firstToIndex))
                    {
                        continue;
                    }

                    AddExternalSpanCandidateIfValid(
                        path,
                        upperIndex,
                        lowerIndex,
                        lowerIndex: lowerIndex,
                        upperIndex: upperIndex,
                        option,
                        surfaces,
                        lowerLevel,
                        higherLevel,
                        candidates);
                }
            }
        }

        private static void AddExternalSpanCandidateIfValid(
            IReadOnlyList<Vector2Int> path,
            int firstPathIndex,
            int secondPathIndex,
            int lowerIndex,
            int upperIndex,
            ReviewedActiveStairOption option,
            SurfaceField surfaces,
            int lowerLevel,
            int higherLevel,
            List<StairTransitionCandidate> candidates)
        {
            if (!TryBuildReviewedStairPortPlacementBetweenLandings(
                    option,
                    path[lowerIndex],
                    path[upperIndex],
                    out ReviewedStairPortPlacement placement) ||
                !PlannedExternalSpanCellsAreCompatible(
                    surfaces,
                    placement.lowerLandingCells,
                    placement.upperLandingCells,
                    placement.footprintCells,
                    lowerLevel,
                    higherLevel))
            {
                return;
            }

            int transitionIndex = firstPathIndex < secondPathIndex
                ? Mathf.Clamp(secondPathIndex - 1, 0, path.Count - 2)
                : Mathf.Clamp(firstPathIndex, 0, path.Count - 2);
            candidates.Add(new StairTransitionCandidate(
                transitionIndex,
                path[lowerIndex],
                path[upperIndex],
                path[lowerIndex],
                path[upperIndex],
                placement.worldEntryDirection,
                placement.worldExitDirection,
                placement.lowerLandingCells,
                placement.upperLandingCells,
                placement.footprintCells,
                ExternalSpanStairPlacementClass,
                option));
        }

        private static void AddValidTurningTransitionCandidates(
            IReadOnlyList<Vector2Int> path,
            bool climbsFromConnectionStart,
            int lastFromIndex,
            int firstToIndex,
            bool preferredOnly,
            ReviewedActiveStairOption option,
            HashSet<Vector2Int> layoutFloorCells,
            SurfaceField surfaces,
            int lowerLevel,
            int higherLevel,
            bool requireEmbeddedSideFloorSupport,
            List<StairTransitionCandidate> candidates)
        {
            int maxSpan = Mathf.Clamp(option.runLength + 3, 3, 8);
            if (climbsFromConnectionStart)
            {
                for (int lowerIndex = 0; lowerIndex <= path.Count - 3; lowerIndex++)
                {
                    int upperLimit = Mathf.Min(path.Count - 1, lowerIndex + maxSpan);
                    for (int upperIndex = lowerIndex + 2; upperIndex <= upperLimit; upperIndex++)
                    {
                        if (preferredOnly && (lowerIndex < lastFromIndex || upperIndex > firstToIndex))
                        {
                            continue;
                        }

                        if (!TryBuildReviewedStairPortPlacementBetweenLandings(
                                option,
                                path[lowerIndex],
                                path[upperIndex],
                                out ReviewedStairPortPlacement placement) ||
                            !PathBetweenLandingsIsStairFootprint(path, lowerIndex, upperIndex, placement.footprintCells) ||
                            !PlannedStairCellsAreCompatible(
                                surfaces,
                                layoutFloorCells,
                                placement.lowerLandingCells,
                                placement.upperLandingCells,
                                placement.footprintCells,
                                lowerLevel,
                                higherLevel,
                                requireEmbeddedSideFloorSupport))
                        {
                            continue;
                        }

                        candidates.Add(new StairTransitionCandidate(
                            upperIndex - 1,
                            path[lowerIndex],
                            path[upperIndex],
                            FirstIntermediateCell(path, lowerIndex, upperIndex),
                            path[upperIndex],
                            placement.worldEntryDirection,
                            placement.worldExitDirection,
                            placement.lowerLandingCells,
                            placement.upperLandingCells,
                            placement.footprintCells,
                            EmbeddedStairPlacementClass,
                            option));
                    }
                }

                return;
            }

            for (int upperIndex = 0; upperIndex <= path.Count - 3; upperIndex++)
            {
                int lowerLimit = Mathf.Min(path.Count - 1, upperIndex + maxSpan);
                for (int lowerIndex = upperIndex + 2; lowerIndex <= lowerLimit; lowerIndex++)
                {
                    if (preferredOnly && (upperIndex < lastFromIndex || lowerIndex > firstToIndex))
                    {
                        continue;
                    }

                    if (!TryBuildReviewedStairPortPlacementBetweenLandings(
                            option,
                            path[lowerIndex],
                            path[upperIndex],
                            out ReviewedStairPortPlacement placement) ||
                        !PathBetweenLandingsIsStairFootprint(path, upperIndex, lowerIndex, placement.footprintCells) ||
                        !PlannedStairCellsAreCompatible(
                            surfaces,
                            layoutFloorCells,
                            placement.lowerLandingCells,
                            placement.upperLandingCells,
                            placement.footprintCells,
                            lowerLevel,
                            higherLevel,
                            requireEmbeddedSideFloorSupport))
                    {
                        continue;
                    }

                    candidates.Add(new StairTransitionCandidate(
                        upperIndex,
                        path[lowerIndex],
                        path[upperIndex],
                        FirstIntermediateCell(path, upperIndex, lowerIndex),
                        path[upperIndex],
                        placement.worldEntryDirection,
                        placement.worldExitDirection,
                        placement.lowerLandingCells,
                        placement.upperLandingCells,
                        placement.footprintCells,
                        EmbeddedStairPlacementClass,
                        option));
                }
            }
        }

        private static void AddValidStraightTransitionCandidates(
            IReadOnlyList<Vector2Int> path,
            bool climbsFromConnectionStart,
            int runLength,
            int min,
            int max,
            ReviewedActiveStairOption option,
            HashSet<Vector2Int> layoutFloorCells,
            SurfaceField surfaces,
            int lowerLevel,
            int higherLevel,
            bool requireEmbeddedSideFloorSupport,
            List<StairTransitionCandidate> candidates)
        {
            int clampedMin = climbsFromConnectionStart
                ? Mathf.Max(min, runLength)
                : Mathf.Max(min, 0);
            int clampedMax = climbsFromConnectionStart
                ? Mathf.Min(max, path.Count - 2)
                : Mathf.Min(max, path.Count - runLength - 2);
            for (int i = clampedMin; i <= clampedMax; i++)
            {
                Vector2Int first = path[i];
                Vector2Int second = path[i + 1];
                Vector2Int direction = second - first;
                if (Mathf.Abs(direction.x) + Mathf.Abs(direction.y) != 1)
                {
                    continue;
                }

                if (climbsFromConnectionStart)
                {
                    bool straightRun = true;
                    for (int offset = 1; offset <= runLength; offset++)
                    {
                        if (path[i - offset] != first - direction * offset)
                        {
                            straightRun = false;
                            break;
                        }
                    }

                    if (!straightRun)
                    {
                        continue;
                    }

                    Vector2Int expectedLowerLanding = first - direction * runLength;
                    if (string.IsNullOrEmpty(option.prefabPath))
                    {
                        candidates.Add(new StairTransitionCandidate(
                            i,
                            expectedLowerLanding,
                            second,
                            new[] { expectedLowerLanding },
                            new[] { second },
                            Array.Empty<Vector2Int>(),
                            option));
                        continue;
                    }

                    if (!TryBuildReviewedStairPortPlacement(
                            option,
                            first,
                            second,
                            expectedLowerLanding,
                            out ReviewedStairPortPlacement placement))
                    {
                        continue;
                    }

                    if (!PlannedStairCellsAreCompatible(
                            surfaces,
                            layoutFloorCells,
                            placement.lowerLandingCells,
                            placement.upperLandingCells,
                            placement.footprintCells,
                            lowerLevel,
                            higherLevel,
                            requireEmbeddedSideFloorSupport))
                    {
                        continue;
                    }

                    candidates.Add(new StairTransitionCandidate(
                        i,
                        expectedLowerLanding,
                        second,
                        first,
                        second,
                        placement.worldEntryDirection,
                        placement.worldExitDirection,
                        placement.lowerLandingCells,
                        placement.upperLandingCells,
                        placement.footprintCells,
                        EmbeddedStairPlacementClass,
                        option));
                    continue;
                }

                bool descendingStraightRun = true;
                for (int offset = 1; offset <= runLength; offset++)
                {
                    if (path[i + offset] != first + direction * offset)
                    {
                        descendingStraightRun = false;
                        break;
                    }
                }

                if (!descendingStraightRun)
                {
                    continue;
                }

                Vector2Int expectedLowerLandingDescending = first + direction * (runLength + 1);
                if (path[i + runLength + 1] != expectedLowerLandingDescending)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(option.prefabPath))
                {
                    candidates.Add(new StairTransitionCandidate(
                        i,
                        expectedLowerLandingDescending,
                        first,
                        new[] { expectedLowerLandingDescending },
                        new[] { first },
                        Array.Empty<Vector2Int>(),
                        option));
                    continue;
                }

                if (!TryBuildReviewedStairPortPlacement(
                        option,
                        second,
                        first,
                        expectedLowerLandingDescending,
                        out ReviewedStairPortPlacement descendingPlacement))
                {
                    continue;
                }

                if (!PlannedStairCellsAreCompatible(
                        surfaces,
                        layoutFloorCells,
                        descendingPlacement.lowerLandingCells,
                        descendingPlacement.upperLandingCells,
                        descendingPlacement.footprintCells,
                        lowerLevel,
                        higherLevel,
                        requireEmbeddedSideFloorSupport))
                {
                    continue;
                }

                candidates.Add(new StairTransitionCandidate(
                    i,
                    expectedLowerLandingDescending,
                    first,
                    second,
                    first,
                    descendingPlacement.worldEntryDirection,
                    descendingPlacement.worldExitDirection,
                    descendingPlacement.lowerLandingCells,
                    descendingPlacement.upperLandingCells,
                    descendingPlacement.footprintCells,
                    EmbeddedStairPlacementClass,
                    option));
            }
        }

        private static bool PlannedStairCellsAreCompatible(
            SurfaceField surfaces,
            HashSet<Vector2Int> layoutFloorCells,
            IReadOnlyList<Vector2Int> lowerLandingCells,
            IReadOnlyList<Vector2Int> upperLandingCells,
            IReadOnlyList<Vector2Int> footprintCells,
            int lowerLevel,
            int higherLevel,
            bool requireSideFloorSupport)
        {
            if (surfaces == null)
            {
                return true;
            }

            return PlannedCellsAreCompatible(surfaces, lowerLandingCells, lowerLevel) &&
                PlannedCellsAreCompatible(surfaces, upperLandingCells, higherLevel) &&
                PlannedCellsAreCompatible(surfaces, footprintCells, lowerLevel) &&
                PlannedEmbeddedFootprintCellsHaveFloorSupport(
                    layoutFloorCells,
                    lowerLandingCells,
                    upperLandingCells,
                    footprintCells,
                    requireSideFloorSupport) &&
                !AnyOverlap(lowerLandingCells, footprintCells) &&
                !AnyOverlap(upperLandingCells, footprintCells);
        }

        private static bool PlannedEmbeddedFootprintCellsHaveFloorSupport(
            HashSet<Vector2Int> layoutFloorCells,
            IReadOnlyList<Vector2Int> lowerLandingCells,
            IReadOnlyList<Vector2Int> upperLandingCells,
            IReadOnlyList<Vector2Int> footprintCells,
            bool requireSideFloorSupport)
        {
            if (layoutFloorCells == null)
            {
                return true;
            }

            var stairCells = new HashSet<Vector2Int>(footprintCells);
            stairCells.UnionWith(lowerLandingCells);
            stairCells.UnionWith(upperLandingCells);
            foreach (Vector2Int cell in footprintCells)
            {
                if (!layoutFloorCells.Contains(cell))
                {
                    return false;
                }

                if (!requireSideFloorSupport)
                {
                    continue;
                }

                bool hasSideSupport = false;
                foreach (Vector2Int neighbor in CardinalNeighbors(cell))
                {
                    if (layoutFloorCells.Contains(neighbor) && !stairCells.Contains(neighbor))
                    {
                        hasSideSupport = true;
                        break;
                    }
                }

                // The renderer removes the floor beneath an embedded stair body.
                // Requiring adjacent floor outside the stair and its landings keeps
                // a one-cell corridor over void from qualifying as an embedded stair;
                // that geometry belongs to an externalSpan bridge contract.
                if (!hasSideSupport)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool PlannedExternalSpanCellsAreCompatible(
            SurfaceField surfaces,
            IReadOnlyList<Vector2Int> lowerLandingCells,
            IReadOnlyList<Vector2Int> upperLandingCells,
            IReadOnlyList<Vector2Int> footprintCells,
            int lowerLevel,
            int higherLevel)
        {
            if (surfaces == null ||
                !PlannedCellsAreCompatible(surfaces, lowerLandingCells, lowerLevel) ||
                !PlannedCellsAreCompatible(surfaces, upperLandingCells, higherLevel) ||
                AnyOverlap(lowerLandingCells, footprintCells) ||
                AnyOverlap(upperLandingCells, footprintCells))
            {
                return false;
            }

            // The deck may only fly over true gaps: a leveled footprint cell means
            // the deck would cross walkable interior at head height, or cross a
            // room's boundary mid-span where its enclosure wall may stand (seen
            // in-editor 2026-06-11: an L-bridge descending over a room pierced its
            // partition wall). Ports stay the only edges where a span meets floor.
            foreach (Vector2Int cell in footprintCells)
            {
                if (surfaces.HasFloor(cell))
                {
                    return false;
                }
            }

            return footprintCells.Count > 0;
        }

        private static StairTransitionCandidate ChooseStairTransitionCandidate(
            IReadOnlyList<StairTransitionCandidate> candidates,
            System.Random random)
        {
            var groups = new List<List<StairTransitionCandidate>>();
            var groupIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (StairTransitionCandidate candidate in candidates)
            {
                string key = StairCandidateGroupKey(candidate);
                if (!groupIndexes.TryGetValue(key, out int groupIndex))
                {
                    groupIndex = groups.Count;
                    groupIndexes[key] = groupIndex;
                    groups.Add(new List<StairTransitionCandidate>());
                }

                groups[groupIndex].Add(candidate);
            }

            float totalWeight = 0f;
            var groupWeights = new float[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                float groupWeight = 0.01f;
                foreach (StairTransitionCandidate candidate in groups[i])
                {
                    groupWeight = Mathf.Max(groupWeight, candidate.weight);
                }

                groupWeights[i] = groupWeight;
                totalWeight += groupWeight;
            }

            double roll = random.NextDouble() * totalWeight;
            for (int i = 0; i < groups.Count; i++)
            {
                roll -= groupWeights[i];
                if (roll <= 0.0)
                {
                    List<StairTransitionCandidate> group = groups[i];
                    return group[random.Next(group.Count)];
                }
            }

            List<StairTransitionCandidate> fallbackGroup = groups[groups.Count - 1];
            return fallbackGroup[random.Next(fallbackGroup.Count)];
        }

        private static string StairCandidateGroupKey(StairTransitionCandidate candidate)
        {
            return $"{candidate.option.prefabPath}|{candidate.placementClass}";
        }

        private static void AccumulateStairCandidateCounts(
            IReadOnlyList<StairTransitionCandidate> candidates,
            SortedDictionary<string, int> counts)
        {
            if (counts == null)
            {
                return;
            }

            foreach (StairTransitionCandidate candidate in candidates)
            {
                string name = string.IsNullOrEmpty(candidate.option.prefabPath)
                    ? PrimaryStairSummaryName()
                    : Path.GetFileNameWithoutExtension(candidate.option.prefabPath);
                string key = $"{name}@{candidate.placementClass}";
                counts.TryGetValue(key, out int count);
                counts[key] = count + 1;
            }
        }

        // "Every one of these cells is either unsurfaced, or already carries a
        // surface at exactly this level."
        //
        // The old form asked whether the column's FLOOR differed from `level`,
        // which conflates two questions the moment a column stacks: a cell whose
        // floor is L0 and which also carries a gallery at L4 would reject a
        // landing at L4 — the very surface the landing wants to sit on. Asking
        // whether the column is empty, and otherwise whether the level is
        // present, separates them. Identical on a single-layer field, where a
        // surfaced column has exactly one level to compare.
        // An endpoint whose column carries no surface. The edge owns the marker;
        // this is the generator-side name for it.
        //
        // RECORDED, not thrown, so the transition-contract gate keeps producing
        // the exact rejection it always did — the check moves from the field onto
        // the edge, the outcome does not. It has never fired across the 200-seed
        // corpus, which is a reason to keep it cheap rather than a reason to drop
        // it: a stairwell tower places its body on void cells, and "the endpoint
        // is unleveled" is the shape of mistake that would make.
        private const int UnleveledTransitionEndpoint = ElevationEdgeModel.TransitionEdge.UnknownLevel;

        // Every producer except the recipe resolver puts its endpoints on column
        // FLOORS — none of them stacks. Reading the field at construction is
        // therefore identical to the lookup each consumer did for itself, which
        // is the whole point: the levels move onto the edge without changing what
        // anybody computes. Nothing can move them afterwards, because
        // `TrySetFloorLevel` rejects a conflict, `AddFloorLevel` needs an empty
        // column, and `RelevelFloor` runs before every producer.
        private static int TransitionEndpointLevel(SurfaceField surfaces, Vector2Int cell)
        {
            return surfaces.TryGetFloorLevel(cell, out int level)
                ? level
                : UnleveledTransitionEndpoint;
        }

        // "Every one of these cells is either UNSURFACED, or already carries a
        // surface at exactly this level" — any surface, not just a floor. Every
        // caller runs during the stair search, which precedes the only suspended
        // producer there is, so `HasFloor` would read identically today. It is
        // the wider question on purpose: a stair landing planted in a column
        // whose only surface is a bridge deck is wrong whether or not the pass
        // ordering currently makes it reachable.
        private static bool PlannedCellsAreCompatible(
            SurfaceField surfaces,
            IReadOnlyList<Vector2Int> cells,
            int level)
        {
            foreach (Vector2Int cell in cells)
            {
                if (surfaces.CarriesAnySurface(cell) && !surfaces.HasSurfaceAt(cell, level))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AnyOverlap(IReadOnlyList<Vector2Int> first, IReadOnlyList<Vector2Int> second)
        {
            var cells = new HashSet<Vector2Int>(first);
            foreach (Vector2Int cell in second)
            {
                if (cells.Contains(cell))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool PathBetweenLandingsIsStairFootprint(
            IReadOnlyList<Vector2Int> path,
            int firstLandingIndex,
            int secondLandingIndex,
            IReadOnlyList<Vector2Int> footprintCells)
        {
            if (secondLandingIndex <= firstLandingIndex + 1)
            {
                return false;
            }

            for (int i = firstLandingIndex + 1; i < secondLandingIndex; i++)
            {
                if (!ContainsCell(footprintCells, path[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static Vector2Int FirstIntermediateCell(IReadOnlyList<Vector2Int> path, int firstLandingIndex, int secondLandingIndex)
        {
            return path[Mathf.Clamp(firstLandingIndex + 1, 0, secondLandingIndex - 1)];
        }

        // Geometry mapping depends only on the immutable option arrays/measurements
        // and its requested anchor cells. A tier retry may test the same mapping
        // hundreds of times against different live occupancy; cache only this pure
        // mapping, then run every occupancy, ledger, candidate-order, and random
        // selection check exactly as before.
        private sealed class ReviewedStairPlacementGeometryCache
        {
            private readonly Dictionary<ReviewedStairPlacementGeometryKey, CachedReviewedStairPlacement> entries =
                new Dictionary<ReviewedStairPlacementGeometryKey, CachedReviewedStairPlacement>();

            internal bool TryGet(
                ReviewedStairPlacementGeometryKey key,
                out CachedReviewedStairPlacement placement)
            {
                return entries.TryGetValue(key, out placement);
            }

            internal void Store(
                ReviewedStairPlacementGeometryKey key,
                bool succeeded,
                ReviewedStairPortPlacement placement)
            {
                entries[key] = new CachedReviewedStairPlacement(succeeded, placement);
            }
        }

        private readonly struct CachedReviewedStairPlacement
        {
            internal readonly bool succeeded;
            internal readonly ReviewedStairPortPlacement placement;

            internal CachedReviewedStairPlacement(
                bool succeeded,
                ReviewedStairPortPlacement placement)
            {
                this.succeeded = succeeded;
                this.placement = placement;
            }
        }

        private readonly struct ReviewedStairPlacementGeometryKey :
            IEquatable<ReviewedStairPlacementGeometryKey>
        {
            private readonly Vector2Int[] footprintCells;
            private readonly Vector2Int[] entryCells;
            private readonly Vector2Int[] exitCells;
            private readonly Vector2 localBoundsMin;
            private readonly Vector2 localBoundsMax;
            private readonly Vector2 localEntryPoint;
            private readonly Vector2 localExitPoint;
            private readonly int entryDirection;
            private readonly int exitDirection;
            private readonly Vector2Int first;
            private readonly Vector2Int second;
            private readonly Vector2Int third;
            private readonly bool betweenLandings;

            internal ReviewedStairPlacementGeometryKey(
                ReviewedActiveStairOption option,
                Vector2Int first,
                Vector2Int second,
                Vector2Int third,
                bool betweenLandings)
            {
                footprintCells = option.footprintCells;
                entryCells = option.entryCells;
                exitCells = option.exitCells;
                localBoundsMin = option.localBoundsMin;
                localBoundsMax = option.localBoundsMax;
                localEntryPoint = option.localEntryPoint;
                localExitPoint = option.localExitPoint;
                entryDirection = option.entryDirection;
                exitDirection = option.exitDirection;
                this.first = first;
                this.second = second;
                this.third = third;
                this.betweenLandings = betweenLandings;
            }

            public bool Equals(ReviewedStairPlacementGeometryKey other)
            {
                return ReferenceEquals(footprintCells, other.footprintCells) &&
                    ReferenceEquals(entryCells, other.entryCells) &&
                    ReferenceEquals(exitCells, other.exitCells) &&
                    localBoundsMin == other.localBoundsMin &&
                    localBoundsMax == other.localBoundsMax &&
                    localEntryPoint == other.localEntryPoint &&
                    localExitPoint == other.localExitPoint &&
                    entryDirection == other.entryDirection &&
                    exitDirection == other.exitDirection &&
                    first == other.first &&
                    second == other.second &&
                    third == other.third &&
                    betweenLandings == other.betweenLandings;
            }

            public override bool Equals(object obj)
            {
                return obj is ReviewedStairPlacementGeometryKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + ReferenceHash(footprintCells);
                    hash = hash * 31 + ReferenceHash(entryCells);
                    hash = hash * 31 + ReferenceHash(exitCells);
                    hash = hash * 31 + localBoundsMin.GetHashCode();
                    hash = hash * 31 + localBoundsMax.GetHashCode();
                    hash = hash * 31 + localEntryPoint.GetHashCode();
                    hash = hash * 31 + localExitPoint.GetHashCode();
                    hash = hash * 31 + entryDirection;
                    hash = hash * 31 + exitDirection;
                    hash = hash * 31 + first.GetHashCode();
                    hash = hash * 31 + second.GetHashCode();
                    hash = hash * 31 + third.GetHashCode();
                    hash = hash * 31 + (betweenLandings ? 1 : 0);
                    return hash;
                }
            }

            private static int ReferenceHash(object value)
            {
                return value == null
                    ? 0
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
            }
        }

        private static bool TryBuildReviewedStairPortPlacementBetweenLandings(
            ReviewedActiveStairOption option,
            Vector2Int lowerLandingCell,
            Vector2Int upperLandingCell,
            out ReviewedStairPortPlacement placement)
        {
            ReviewedStairPlacementGeometryCache cache =
                activeReviewedStairPlacementGeometryCache;
            if (cache == null)
            {
                return TryBuildReviewedStairPortPlacementBetweenLandingsUncached(
                    option,
                    lowerLandingCell,
                    upperLandingCell,
                    out placement);
            }

            var key = new ReviewedStairPlacementGeometryKey(
                option,
                lowerLandingCell,
                upperLandingCell,
                default,
                betweenLandings: true);
            if (cache.TryGet(key, out CachedReviewedStairPlacement cached))
            {
                placement = cached.placement;
                return cached.succeeded;
            }

            bool succeeded = TryBuildReviewedStairPortPlacementBetweenLandingsUncached(
                option,
                lowerLandingCell,
                upperLandingCell,
                out placement);
            cache.Store(key, succeeded, placement);
            return succeeded;
        }

        private static bool TryBuildReviewedStairPortPlacementBetweenLandingsUncached(
            ReviewedActiveStairOption option,
            Vector2Int lowerLandingCell,
            Vector2Int upperLandingCell,
            out ReviewedStairPortPlacement placement)
        {
            placement = default;
            if (option.footprintCells == null ||
                option.footprintCells.Length == 0 ||
                option.entryPort.cells == null ||
                option.entryPort.cells.Length == 0 ||
                option.exitPort.cells == null ||
                option.exitPort.cells.Length == 0)
            {
                return false;
            }

            float[] rotations = { 0f, 90f, 180f, 270f };
            foreach (float yRotation in rotations)
            {
                int worldEntryDirection = DirectionFromVector(Rotate2D(DirectionVector(option.entryPort.direction), yRotation));
                int worldExitDirection = DirectionFromVector(Rotate2D(DirectionVector(option.exitPort.direction), yRotation));
                if (worldEntryDirection == 0 || worldExitDirection == 0)
                {
                    continue;
                }

                foreach (Vector2Int exitAnchorCell in option.exitPort.cells)
                {
                    Vector2 localExitAnchor = ReviewedLocalPortCellEdgeCenter(option, exitAnchorCell, option.exitPort.direction);
                    Vector2 exitTarget = EdgeCenterInCellSpace(upperLandingCell, OppositeDirection(worldExitDirection));
                    Vector2 position = exitTarget - Rotate2D(localExitAnchor, yRotation);
                    Vector2Int[] candidateFootprintCells = MapReviewedFootprintCellsToWorldCells(option, position, yRotation);
                    Vector2Int[] candidateLowerLandingCells = MapReviewedPortCellsToLandingCells(option, option.entryPort, worldEntryDirection, position, yRotation);
                    Vector2Int[] candidateUpperLandingCells = MapReviewedPortCellsToLandingCells(option, option.exitPort, worldExitDirection, position, yRotation);
                    if (!ContainsCell(candidateLowerLandingCells, lowerLandingCell) ||
                        !ContainsCell(candidateUpperLandingCells, upperLandingCell) ||
                        AnyOverlap(candidateLowerLandingCells, candidateFootprintCells) ||
                        AnyOverlap(candidateUpperLandingCells, candidateFootprintCells))
                    {
                        continue;
                    }

                    placement = new ReviewedStairPortPlacement(
                        position,
                        yRotation,
                        worldEntryDirection,
                        worldExitDirection,
                        candidateLowerLandingCells,
                        candidateUpperLandingCells,
                        candidateFootprintCells);
                    return true;
                }
            }

            return false;
        }

        private static bool TryBuildReviewedStairPortPlacement(
            ReviewedActiveStairOption option,
            Vector2Int lowerCellAdjacentToUpper,
            Vector2Int upperLandingCell,
            Vector2Int lowerLandingCellOnPath,
            out ReviewedStairPortPlacement placement)
        {
            ReviewedStairPlacementGeometryCache cache =
                activeReviewedStairPlacementGeometryCache;
            if (cache == null)
            {
                return TryBuildReviewedStairPortPlacementUncached(
                    option,
                    lowerCellAdjacentToUpper,
                    upperLandingCell,
                    lowerLandingCellOnPath,
                    out placement);
            }

            var key = new ReviewedStairPlacementGeometryKey(
                option,
                lowerCellAdjacentToUpper,
                upperLandingCell,
                lowerLandingCellOnPath,
                betweenLandings: false);
            if (cache.TryGet(key, out CachedReviewedStairPlacement cached))
            {
                placement = cached.placement;
                return cached.succeeded;
            }

            bool succeeded = TryBuildReviewedStairPortPlacementUncached(
                option,
                lowerCellAdjacentToUpper,
                upperLandingCell,
                lowerLandingCellOnPath,
                out placement);
            cache.Store(key, succeeded, placement);
            return succeeded;
        }

        private static bool TryBuildReviewedStairPortPlacementUncached(
            ReviewedActiveStairOption option,
            Vector2Int lowerCellAdjacentToUpper,
            Vector2Int upperLandingCell,
            Vector2Int lowerLandingCellOnPath,
            out ReviewedStairPortPlacement placement)
        {
            placement = default;
            if (option.footprintCells == null ||
                option.footprintCells.Length == 0 ||
                option.entryPort.cells == null ||
                option.entryPort.cells.Length == 0 ||
                option.exitPort.cells == null ||
                option.exitPort.cells.Length == 0)
            {
                return false;
            }

            Vector2Int worldLowerDirection = lowerCellAdjacentToUpper - upperLandingCell;
            if (Mathf.Abs(worldLowerDirection.x) + Mathf.Abs(worldLowerDirection.y) != 1)
            {
                return false;
            }

            int lowerDirection = DirectionFromVector(new Vector2(worldLowerDirection.x, worldLowerDirection.y));
            Vector2 lowerDirectionVector = DirectionVector(lowerDirection);
            float yRotation = CalculateYawToMap(DirectionVector(option.entryPort.direction), lowerDirectionVector);
            Vector2 measuredExitDirection = Rotate2D(DirectionVector(option.exitPort.direction), yRotation);
            int worldEntryDirection = DirectionFromVector(Rotate2D(DirectionVector(option.entryPort.direction), yRotation));
            int worldExitDirection = DirectionFromVector(measuredExitDirection);
            if (worldEntryDirection == 0 || worldExitDirection == 0)
            {
                return false;
            }

            foreach (Vector2Int exitAnchorCell in option.exitPort.cells)
            {
                Vector2 localExitAnchor = ReviewedLocalPortCellEdgeCenter(option, exitAnchorCell, option.exitPort.direction);
                Vector2 exitTarget = EdgeCenterInCellSpace(upperLandingCell, OppositeDirection(worldExitDirection));
                Vector2 position = exitTarget - Rotate2D(localExitAnchor, yRotation);
                Vector2Int[] candidateFootprintCells = MapReviewedFootprintCellsToWorldCells(option, position, yRotation);
                Vector2Int[] candidateLowerLandingCells = MapReviewedPortCellsToLandingCells(option, option.entryPort, worldEntryDirection, position, yRotation);
                Vector2Int[] candidateUpperLandingCells = MapReviewedPortCellsToLandingCells(option, option.exitPort, worldExitDirection, position, yRotation);
                if (!ContainsCell(candidateFootprintCells, lowerCellAdjacentToUpper) ||
                    !ContainsCell(candidateLowerLandingCells, lowerLandingCellOnPath) ||
                    !ContainsCell(candidateUpperLandingCells, upperLandingCell) ||
                    ContainsCell(candidateFootprintCells, upperLandingCell))
                {
                    continue;
                }

                placement = new ReviewedStairPortPlacement(
                    position,
                    yRotation,
                    worldEntryDirection,
                    worldExitDirection,
                    candidateLowerLandingCells,
                    candidateUpperLandingCells,
                    candidateFootprintCells);
                return true;
            }

            return false;
        }

        private static Vector2Int[] MapReviewedFootprintCellsToWorldCells(
            ReviewedActiveStairOption option,
            Vector2 position,
            float yRotation)
        {
            var result = new Vector2Int[option.footprintCells.Length];
            for (int i = 0; i < option.footprintCells.Length; i++)
            {
                Vector2 localCenter = ReviewedLocalCellCenter(option, option.footprintCells[i]);
                Vector2 worldCenter = position + Rotate2D(localCenter, yRotation);
                result[i] = GridCellFromPlanPoint(worldCenter);
            }

            return result;
        }

        private static Vector2Int[] MapReviewedPortCellsToLandingCells(
            ReviewedActiveStairOption option,
            ReviewedActiveStairPort port,
            int worldDirection,
            Vector2 position,
            float yRotation)
        {
            var result = new Vector2Int[port.cells.Length];
            for (int i = 0; i < port.cells.Length; i++)
            {
                Vector2 localEdgeCenter = ReviewedLocalPortCellEdgeCenter(option, port.cells[i], port.direction);
                Vector2 worldEdgeCenter = position + Rotate2D(localEdgeCenter, yRotation);
                result[i] = GridCellFromPlanPort(worldEdgeCenter, worldDirection);
            }

            return result;
        }

        private static Vector2 ReviewedLocalCellCenter(ReviewedActiveStairOption option, Vector2Int cell)
        {
            return new Vector2(
                option.localBoundsMin.x + (cell.x + 0.5f) * 4f,
                option.localBoundsMin.y + (cell.y + 0.5f) * 4f);
        }

        private static Vector2 ReviewedLocalPortCellEdgeCenter(
            ReviewedActiveStairOption option,
            Vector2Int cell,
            int direction)
        {
            float minX = option.localBoundsMin.x + cell.x * 4f;
            float maxX = Mathf.Min(minX + 4f, option.localBoundsMax.x);
            float minZ = option.localBoundsMin.y + cell.y * 4f;
            float maxZ = Mathf.Min(minZ + 4f, option.localBoundsMax.y);
            minX = Mathf.Clamp(minX, option.localBoundsMin.x, option.localBoundsMax.x);
            maxX = Mathf.Clamp(maxX, option.localBoundsMin.x, option.localBoundsMax.x);
            minZ = Mathf.Clamp(minZ, option.localBoundsMin.y, option.localBoundsMax.y);
            maxZ = Mathf.Clamp(maxZ, option.localBoundsMin.y, option.localBoundsMax.y);

            float x = (minX + maxX) * 0.5f;
            float z = (minZ + maxZ) * 0.5f;
            switch (direction)
            {
                case Direction.North:
                    z = maxZ;
                    break;
                case Direction.East:
                    x = maxX;
                    break;
                case Direction.South:
                    z = minZ;
                    break;
                case Direction.West:
                    x = minX;
                    break;
            }

            return new Vector2(x, z);
        }

        private static Vector2Int GridCellFromPlanPort(Vector2 point, int outwardDirection)
        {
            const float outwardNudge = 0.05f;
            return GridCellFromPlanPoint(point + DirectionVector(outwardDirection) * outwardNudge);
        }

        private static bool ContainsCell(IReadOnlyList<Vector2Int> cells, Vector2Int expected)
        {
            foreach (Vector2Int cell in cells)
            {
                if (cell == expected)
                {
                    return true;
                }
            }

            return false;
        }

        // The ledger moved to DungeonLabGenerator.Prisms.cs in Phase B of the
        // layered 3D topology design: its five flat cell sets are now prisms
        // carrying a half-open level band and a typed owner (design §6).

        private readonly struct StairTransitionCandidate
        {
            public readonly int transitionIndex;
            public readonly Vector2Int lowerLandingCell;
            public readonly Vector2Int upperLandingCell;
            public readonly Vector2Int transitionFirstCell;
            public readonly Vector2Int transitionSecondCell;
            public readonly int lowerPortDirection;
            public readonly int upperPortDirection;
            public readonly Vector2Int[] lowerLandingCells;
            public readonly Vector2Int[] upperLandingCells;
            public readonly Vector2Int[] footprintCells;
            public readonly string placementClass;
            public readonly float weight;
            public readonly ReviewedActiveStairOption option;

            public StairTransitionCandidate(
                int transitionIndex,
                Vector2Int lowerLandingCell,
                Vector2Int upperLandingCell,
                Vector2Int[] lowerLandingCells,
                Vector2Int[] upperLandingCells,
                Vector2Int[] footprintCells,
                ReviewedActiveStairOption option)
                : this(
                    transitionIndex,
                    lowerLandingCell,
                    upperLandingCell,
                    default,
                    default,
                    0,
                    0,
                    lowerLandingCells,
                    upperLandingCells,
                    footprintCells,
                    EmbeddedStairPlacementClass,
                    option)
            {
            }

            public StairTransitionCandidate(
                int transitionIndex,
                Vector2Int lowerLandingCell,
                Vector2Int upperLandingCell,
                Vector2Int transitionFirstCell,
                Vector2Int transitionSecondCell,
                int lowerPortDirection,
                int upperPortDirection,
                Vector2Int[] lowerLandingCells,
                Vector2Int[] upperLandingCells,
                Vector2Int[] footprintCells,
                string placementClass,
                ReviewedActiveStairOption option)
            {
                this.transitionIndex = transitionIndex;
                this.lowerLandingCell = lowerLandingCell;
                this.upperLandingCell = upperLandingCell;
                this.transitionFirstCell = transitionFirstCell;
                this.transitionSecondCell = transitionSecondCell;
                this.lowerPortDirection = lowerPortDirection;
                this.upperPortDirection = upperPortDirection;
                this.lowerLandingCells = lowerLandingCells ?? Array.Empty<Vector2Int>();
                this.upperLandingCells = upperLandingCells ?? Array.Empty<Vector2Int>();
                this.footprintCells = footprintCells ?? Array.Empty<Vector2Int>();
                this.placementClass = string.IsNullOrWhiteSpace(placementClass) ? EmbeddedStairPlacementClass : placementClass;
                this.weight = CalculateStairTransitionCandidateWeight(option, this.placementClass);
                this.option = option;
            }
        }

        private static float CalculateStairTransitionCandidateWeight(
            ReviewedActiveStairOption option,
            string placementClass)
        {
            float weight = 1f;
            if (string.Equals(option.topology, ActiveTurningStairTopology, StringComparison.Ordinal))
            {
                weight *= 2.5f;
                weight *= Mathf.Max(1f, option.footprintCells.Length / (float)Mathf.Max(1, option.runLength));
            }

            if (string.Equals(placementClass, ExternalSpanStairPlacementClass, StringComparison.Ordinal))
            {
                weight *= 2f;
            }

            if (option.laneCount > 1)
            {
                weight *= 1.35f;
            }

            return weight;
        }

        private static bool TrySetPlannedStairCells(
            SurfaceField surfaces,
            IReadOnlyList<Vector2Int> lowerLandingCells,
            IReadOnlyList<Vector2Int> upperLandingCells,
            IReadOnlyList<Vector2Int> footprintCells,
            int lowerLevel,
            int higherLevel,
            string placementClass,
            out string rejectionReason)
        {
            foreach (Vector2Int cell in lowerLandingCells)
            {
                if (!TryEnsurePlannedSurfaceLevel(surfaces, cell, lowerLevel, out rejectionReason))
                {
                    return false;
                }
            }

            // Span decks and stairwell towers own their cells without flooring
            // them: the gap stays a gap (span) and the tower stands on void
            // (decision 26) — only embedded bodies level their footprint.
            bool footprintStaysUnleveled =
                string.Equals(placementClass, ExternalSpanStairPlacementClass, StringComparison.Ordinal) ||
                string.Equals(placementClass, StairwellStairPlacementClass, StringComparison.Ordinal);
            foreach (Vector2Int cell in footprintCells)
            {
                if (footprintStaysUnleveled && !surfaces.HasFloor(cell))
                {
                    continue;
                }

                if (!TryEnsurePlannedSurfaceLevel(surfaces, cell, lowerLevel, out rejectionReason))
                {
                    return false;
                }
            }

            foreach (Vector2Int cell in upperLandingCells)
            {
                if (!TryEnsurePlannedSurfaceLevel(surfaces, cell, higherLevel, out rejectionReason))
                {
                    return false;
                }
            }

            rejectionReason = string.Empty;
            return true;
        }

        /// <summary>
        /// Make a route stair endpoint stand on the requested surface without
        /// confusing a suspended gallery landing for the column floor.
        /// </summary>
        private static bool TryEnsurePlannedSurfaceLevel(
            SurfaceField surfaces,
            Vector2Int cell,
            int level,
            out string rejectionReason)
        {
            if (surfaces.HasSurfaceAt(cell, level))
            {
                rejectionReason = string.Empty;
                return true;
            }

            return surfaces.TrySetFloorLevel(cell, level, out rejectionReason);
        }

        // Returns the number of floor cells the flood fill could not reach, which
        // therefore fell back to level 0. That fallback is a silent repair: it
        // invents an arbitrary elevation and can manufacture a cliff no planning
        // stage asked for. It is expected to be 0; the caller surfaces any
        // non-zero count rather than letting it pass unnoticed.
        private static int FillUnassignedFloorCells(
            HashSet<Vector2Int> floorCells,
            SurfaceField surfaces,
            HashSet<Vector2Int> externalSpanGapCells)
        {
            // Seed the flood fill in sorted order: Dictionary key order is not contractually
            // stable, and which seed reaches a contested unassigned cell first decides its level.
            //
            // FLOORS on both sides of the walk — the seeds and the "already
            // done" test — and that is load-bearing now that a span deck is a
            // surface. This pass runs after the bridges, so a deck may already
            // stand over a corridor cell that has no level yet. Seeding from
            // surfaces would flood the deck's height into the ground beneath it;
            // skipping surfaced cells would leave that corridor cell with no
            // floor at all. It owes a floor to every cell in the shadow,
            // whatever is flying overhead.
            var seeds = new List<Vector2Int>(surfaces.FlooredCells());
            seeds.Sort(CompareCells);
            var queue = new Queue<Vector2Int>();
            foreach (Vector2Int cell in seeds)
            {
                queue.Enqueue(cell);
            }

            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                if (!surfaces.TryGetFloorLevel(cell, out int level))
                {
                    continue;
                }

                foreach (Vector2Int neighbor in CardinalNeighbors(cell))
                {
                    if (!floorCells.Contains(neighbor) ||
                        surfaces.HasFloor(neighbor) ||
                        externalSpanGapCells.Contains(neighbor))
                    {
                        continue;
                    }

                    surfaces.AddFloorLevel(neighbor, level);
                    queue.Enqueue(neighbor);
                }
            }

            int unreachedCells = 0;
            foreach (Vector2Int cell in floorCells)
            {
                if (!surfaces.HasFloor(cell) && !externalSpanGapCells.Contains(cell))
                {
                    surfaces.AddFloorLevel(cell, 0);
                    unreachedCells++;
                }
            }

            return unreachedCells;
        }

        private static int IndexOfPathCell(IReadOnlyList<Vector2Int> path, Vector2Int cell)
        {
            for (int i = 0; i < path.Count; i++)
            {
                if (path[i] == cell)
                {
                    return i;
                }
            }

            return -1;
        }

        // Reads the edge's OWN endpoint levels. Looking them up by cell was the
        // thing that made a cross-layer stair unrepresentable: an upper end
        // standing over its own lower end resolved both to the column floor,
        // computed a delta of 0, and was rejected here as too shallow to be a
        // stair. The rejection for an unleveled endpoint survives verbatim — the
        // producer records a sentinel rather than throwing, so the check moved
        // onto the edge without changing its outcome.
        private static bool TryValidateTransitionLevelDeltas(
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            int topologyCeiling,
            DungeonLayout layout,
            out string rejectionReason)
        {
            var transitionKeys = new HashSet<string>();
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                if (transition.firstLevel == UnleveledTransitionEndpoint ||
                    transition.secondLevel == UnleveledTransitionEndpoint)
                {
                    rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} referenced a missing cell";
                    return false;
                }

                int firstLevel = transition.firstLevel;
                int secondLevel = transition.secondLevel;

                // Seam transitions climb exactly 1u; the primary straight stair climbs
                // the primary rise; anything taller must carry an explicit reviewed
                // stair prefab and stay within the generated level cap.
                int delta = Mathf.Abs(firstLevel - secondLevel);
                bool externalSpan = string.Equals(
                    transition.placementClass,
                    ExternalSpanStairPlacementClass,
                    StringComparison.Ordinal);
                if (externalSpan &&
                    (!IsStructuralLevel(firstLevel) || !IsStructuralLevel(secondLevel)))
                {
                    rejectionReason =
                        $"bridge transition {transition.firstCell}->{transition.secondCell} had " +
                        $"non-structural landing levels {firstLevel} and {secondLevel}";
                    return false;
                }

                // A synthesized fixture deck is flat. Room-local seam/dais
                // strips keep their existing 1u physical contracts below.
                if (delta == 0 &&
                    externalSpan &&
                    transition.synthesizedSetPiece != null)
                {
                    transitionKeys.Add(TransitionKey(transition.firstCell, transition.secondCell));
                    continue;
                }

                if (delta == 1 &&
                    (string.Equals(transition.placementClass, SeamStairPlacementClass, StringComparison.Ordinal) ||
                     string.Equals(transition.placementClass, DaisStairPlacementClass, StringComparison.Ordinal)))
                {
                    if (!IsTransitionOwnedByOneRoom(layout, transition))
                    {
                        rejectionReason =
                            $"room-local transition {transition.firstCell}->{transition.secondCell} " +
                            "crossed a room ownership boundary";
                        return false;
                    }

                    transitionKeys.Add(TransitionKey(transition.firstCell, transition.secondCell));
                    continue;
                }

                if (delta == PrimaryStairRiseLevels)
                {
                    if (!IsTransitionOwnedByOneRoom(layout, transition))
                    {
                        rejectionReason =
                            $"room-local transition {transition.firstCell}->{transition.secondCell} " +
                            "crossed a room ownership boundary";
                        return false;
                    }

                    transitionKeys.Add(TransitionKey(transition.firstCell, transition.secondCell));
                    continue;
                }

                if (!IsStructuralLevel(firstLevel) || !IsStructuralLevel(secondLevel))
                {
                    rejectionReason =
                        $"structural transition {transition.firstCell}->{transition.secondCell} had " +
                        $"non-structural endpoint levels {firstLevel} and {secondLevel}";
                    return false;
                }

                if (delta < PrimaryStairRiseLevels ||
                    delta > topologyCeiling ||
                    string.IsNullOrEmpty(transition.stairPrefabPath))
                {
                    rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} had level delta {delta}";
                    return false;
                }

                transitionKeys.Add(TransitionKey(transition.firstCell, transition.secondCell));
            }

            rejectionReason = string.Empty;
            return true;
        }

        private static bool IsTransitionOwnedByOneRoom(
            DungeonLayout layout,
            ElevationEdgeModel.TransitionEdge transition)
        {
            foreach (RoomFootprint room in layout.rooms)
            {
                if (!room.Contains(transition.firstCell) ||
                    !room.Contains(transition.secondCell) ||
                    !AreCellsOwnedByRoom(room, transition.lowerLandingCells) ||
                    !AreCellsOwnedByRoom(room, transition.upperLandingCells) ||
                    !AreCellsOwnedByRoom(room, transition.footprintCells))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static bool AreCellsOwnedByRoom(
            RoomFootprint room,
            IReadOnlyList<Vector2Int> cells)
        {
            foreach (Vector2Int cell in cells)
            {
                if (!room.Contains(cell))
                {
                    return false;
                }
            }

            return true;
        }

        // Nodes every SURFACE, not every column. The node key was already a
        // `SurfaceKey`; what it could not do was reach a surface the heightfield
        // did not hold, which is why C1b's fixture had to walk its own surfaces
        // instead of asking the port graph. Iterating `Surfaces()` is the whole
        // change, and on a single-layer field that is the same set in the same
        // canonical order.
        private static bool TryBuildFloorStairPortGraph(
            SurfaceField surfaces,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            out FloorStairPortGraph graph,
            out string rejectionReason)
        {
            return TryBuildFloorStairPortGraph(
                surfaces,
                transitions,
                Array.Empty<PlanOpening>(),
                out graph,
                out rejectionReason);
        }

        private static bool TryBuildFloorStairPortGraph(
            SurfaceField surfaces,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            IReadOnlyList<PlanOpening> openings,
            out FloorStairPortGraph graph,
            out string rejectionReason)
        {
            graph = new FloorStairPortGraph();
            rejectionReason = string.Empty;
            if (surfaces.Count == 0)
            {
                rejectionReason = "cell level field had no floor cells";
                return false;
            }

            HashSet<SurfaceKey> stairBodySurfaces =
                BuildTransitionBodySurfaceSet(surfaces, transitions);
            // BACKING-STORE order, not canonical order. Node insertion order is
            // observable — it reaches the port graph's summary and its
            // reachability message, which 5 of the 200 corpus seeds emit — so
            // this walks cells in the field's own order and takes each column's
            // levels floor-first. Single-layer that is exactly the dictionary
            // enumeration this replaces. `Surfaces()` would be tidier and would
            // move seeds, which is the same trap as sorting the shadow reconcile.
            //
            // ALL surfaced cells, not just the floored ones: a span deck over a
            // true gap is the first surface that stands in a column with no
            // floor, and iterating floors alone would leave the deck out of the
            // very graph it is a walkway in.
            var all = new List<SurfaceKey>(surfaces.Count);
            foreach (Vector2Int cell in surfaces.AllSurfacedCells())
            {
                foreach (int level in surfaces.LevelsAt(cell))
                {
                    all.Add(new SurfaceKey(cell, level));
                }
            }

            foreach (SurfaceKey surface in all)
            {
                if (stairBodySurfaces.Contains(surface))
                {
                    continue;
                }

                graph.EnsureNode(PortGraphNode.Floor(surface));
            }

            foreach (SurfaceKey surface in all)
            {
                if (stairBodySurfaces.Contains(surface))
                {
                    continue;
                }

                PortGraphNode floorNode = PortGraphNode.Floor(surface);
                foreach (Vector2Int neighbor in CardinalNeighbors(surface.cell))
                {
                    // Lateral travel joins surfaces at the SAME level. A gallery
                    // and the chamber floor beneath it are not neighbours, which
                    // is exactly what stacking has to mean.
                    //
                    // The body test is per SURFACE on both ends, so a stair
                    // buried under a gallery blocks travel through its treads
                    // and not through the gallery over them.
                    var neighborSurface = new SurfaceKey(neighbor, surface.level);
                    if (stairBodySurfaces.Contains(neighborSurface) ||
                        !surfaces.HasSurfaceAt(neighbor, surface.level))
                    {
                        continue;
                    }

                    graph.AddEdge(
                        floorNode,
                        PortGraphNode.Floor(neighborSurface),
                        PortGraphEdgeKind.FloorAdjacency);
                }
            }

            for (int i = 0; i < transitions.Count; i++)
            {
                ElevationEdgeModel.TransitionEdge transition = transitions[i];
                if (!TryAddTransitionToPortGraph(surfaces, transition, i, graph, out rejectionReason))
                {
                    return false;
                }
            }

            foreach (PlanOpening opening in openings ?? Array.Empty<PlanOpening>())
            {
                if (opening.kind != OpeningKind.Aperture)
                {
                    continue;
                }

                if (!TryValidateApertureOpeningFallColumn(
                        surfaces,
                        opening,
                        out rejectionReason))
                {
                    return false;
                }

                Vector2Int hole = opening.cell + DirectionVectorInt(opening.direction);
                surfaces.TryGetHighestSurfaceBelow(hole, opening.level, out int catchLevel);
                var rimNode = PortGraphNode.Floor(
                    new SurfaceKey(opening.cell, opening.level));
                var catchNode = PortGraphNode.Floor(
                    new SurfaceKey(hole, catchLevel));
                if (!graph.AddDirectedEdge(
                        rimNode,
                        catchNode,
                        PortGraphEdgeKind.OpeningFall))
                {
                    rejectionReason =
                        $"[APERTURE_PORT_ENDPOINT_MISSING] aperture '{opening.id}' " +
                        $"resolved {opening.cell},L{opening.level}->{hole},L{catchLevel}, " +
                        "but a transition consumed one endpoint";
                    return false;
                }
            }

            if (graph.NodeCount == 0)
            {
                rejectionReason = "floor/stair port graph had no nodes";
                return false;
            }

            return true;
        }

        private static bool TryAddTransitionToPortGraph(
            SurfaceField surfaces,
            ElevationEdgeModel.TransitionEdge transition,
            int transitionIndex,
            FloorStairPortGraph graph,
            out string rejectionReason)
        {
            rejectionReason = string.Empty;
            if (transition.firstLevel == UnleveledTransitionEndpoint ||
                transition.secondLevel == UnleveledTransitionEndpoint)
            {
                rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} referenced a missing floor/stair graph cell";
                return false;
            }

            int firstLevel = transition.firstLevel;
            int secondLevel = transition.secondLevel;

            // Aerial decks (decisions 29-31) connect equal-level landings; the
            // lower/upper port labels degenerate to the two ends. Anything else
            // at equal levels is a planning error.
            if (firstLevel == secondLevel && transition.synthesizedSetPiece == null)
            {
                rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} did not connect different levels";
                return false;
            }

            int lowerLevel = Mathf.Min(firstLevel, secondLevel);
            int upperLevel = Mathf.Max(firstLevel, secondLevel);
            IReadOnlyList<Vector2Int> lowerLandingCells;
            IReadOnlyList<Vector2Int> upperLandingCells;
            if (transition.hasLandings)
            {
                lowerLandingCells = transition.lowerLandingCells;
                upperLandingCells = transition.upperLandingCells;
            }
            else if (firstLevel < secondLevel)
            {
                lowerLandingCells = new[] { transition.firstCell };
                upperLandingCells = new[] { transition.secondCell };
            }
            else
            {
                lowerLandingCells = new[] { transition.secondCell };
                upperLandingCells = new[] { transition.firstCell };
            }

            if (!ValidateTransitionLandingCells(surfaces, lowerLandingCells, lowerLevel, transition, "lower", out rejectionReason) ||
                !ValidateTransitionLandingCells(surfaces, upperLandingCells, upperLevel, transition, "upper", out rejectionReason))
            {
                return false;
            }

            PortGraphNode lowerPort = PortGraphNode.StairPort(transitionIndex, "lower", lowerLevel);
            PortGraphNode upperPort = PortGraphNode.StairPort(transitionIndex, "upper", upperLevel);
            graph.EnsureNode(lowerPort);
            graph.EnsureNode(upperPort);
            graph.AddEdge(lowerPort, upperPort, PortGraphEdgeKind.StairInternal);
            foreach (Vector2Int cell in lowerLandingCells)
            {
                graph.AddEdge(
                    lowerPort,
                    PortGraphNode.Floor(new SurfaceKey(cell, lowerLevel)),
                    PortGraphEdgeKind.PortLanding);
            }

            foreach (Vector2Int cell in upperLandingCells)
            {
                graph.AddEdge(
                    upperPort,
                    PortGraphNode.Floor(new SurfaceKey(cell, upperLevel)),
                    PortGraphEdgeKind.PortLanding);
            }

            return true;
        }

        // A landing was ALREADY required to sit at its transition's endpoint
        // level; it just had to prove it against the heightfield. Now that the
        // transition states that level itself, the check is a plain surface
        // query and landings need no level of their own.
        private static bool ValidateTransitionLandingCells(
            SurfaceField surfaces,
            IReadOnlyList<Vector2Int> landingCells,
            int expectedLevel,
            ElevationEdgeModel.TransitionEdge transition,
            string label,
            out string rejectionReason)
        {
            if (landingCells == null || landingCells.Count == 0)
            {
                rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} had no {label} landing cells";
                return false;
            }

            var unique = new HashSet<Vector2Int>();
            foreach (Vector2Int cell in landingCells)
            {
                if (!unique.Add(cell))
                {
                    rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} repeated {label} landing cell {cell}";
                    return false;
                }

                if (!surfaces.TryGetHighestSurfaceLevel(cell, out int level))
                {
                    rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} {label} landing cell {cell} was missing from level field";
                    return false;
                }

                if (!surfaces.HasSurfaceAt(cell, expectedLevel))
                {
                    rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} {label} landing cell {cell} had level {level}, expected {expectedLevel}";
                    return false;
                }

                if (ContainsCell(transition.footprintCells, cell))
                {
                    rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} {label} landing cell {cell} overlapped stair footprint";
                    return false;
                }
            }

            rejectionReason = string.Empty;
            return true;
        }

        /// <summary>
        /// The SURFACES a transition's body consumes, which the port graph may
        /// not put nodes in.
        /// </summary>
        /// <remarks>
        /// A footprint means "treads fill this column": there is no walkable
        /// place inside a stair body, and the way through is the stair's two
        /// ports. But a stair body is not infinitely tall — it fills
        /// <c>[min endpoint level, max endpoint level]</c> and nothing above
        /// that. Consuming the whole COLUMN was the coarse form, and it deleted
        /// any surface stacked over a stair-adjacent cell: a gallery over such a
        /// column lost BOTH of its surfaces from the graph, and no gate could
        /// see it, because an absent node can never be reported unreachable.
        /// <see cref="ElevationEdgeModel.TransitionEdge"/> has carried both
        /// endpoint levels since C2b-2, so the band is available here.
        /// <para>
        /// A SPAN DECK stays a whole-column exemption rather than becoming a
        /// band case, and that is not laziness. Its footprint IS its walkable
        /// surface — which is why the deck cells carry
        /// <see cref="SurfaceKind.Deck"/> at all — so the transition consumes
        /// nothing in those columns, not even at its own level. A band rule
        /// alone would eat the deck (a flat span's band is exactly the deck's
        /// level). Measured on the two-layer episode before C2b-3: 84
        /// of 88 nodes reachable.
        /// </para>
        /// <para>
        /// Read off the PLAN, not off a placement-class string. A span whose deck
        /// never became a surface — the reviewed-contract corridor span, which
        /// may only cross cells proved unsurfaced — still consumes its columns,
        /// and there is nothing in them to consume.
        /// </para>
        /// <para>
        /// An UNLEVELED endpoint falls back to the whole column. It has never
        /// fired across the corpus, and the transition is rejected by name a few
        /// lines later regardless; the fallback exists so that the body set is
        /// never silently narrowed by a missing level.
        /// </para>
        /// </remarks>
        private static HashSet<SurfaceKey> BuildTransitionBodySurfaceSet(
            SurfaceField surfaces,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            var result = new HashSet<SurfaceKey>();
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                bool leveled = transition.HasLevels;
                int lowerLevel = leveled ? Mathf.Min(transition.firstLevel, transition.secondLevel) : 0;
                int upperLevel = leveled ? Mathf.Max(transition.firstLevel, transition.secondLevel) : 0;
                foreach (Vector2Int cell in transition.footprintCells)
                {
                    if (surfaces.CarriesDeck(cell))
                    {
                        continue;
                    }

                    foreach (int level in surfaces.LevelsAt(cell))
                    {
                        if (leveled && (level < lowerLevel || level > upperLevel))
                        {
                            continue;
                        }

                        result.Add(new SurfaceKey(cell, level));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Surfaces that stand in a transition footprint column and OUTSIDE that
        /// transition's body band — the ones the old whole-column rule deleted
        /// and the band rule keeps.
        /// </summary>
        /// <remarks>
        /// Reported per seed because it is the number that says whether the
        /// narrowing changed anything: 0 across the corpus means single-layer
        /// generation never stacked over a stair body, so the fix is latent in
        /// production and bites only authored or generated multi-layer rooms.
        /// Deck columns are excluded on both sides, since neither rule touches
        /// them.
        /// </remarks>
        private static int CountSurfacesOverTransitionBodies(
            SurfaceField surfaces,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            if (surfaces == null || transitions == null)
            {
                return 0;
            }

            var counted = new HashSet<SurfaceKey>();
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                if (!transition.HasLevels)
                {
                    continue;
                }

                int lowerLevel = Mathf.Min(transition.firstLevel, transition.secondLevel);
                int upperLevel = Mathf.Max(transition.firstLevel, transition.secondLevel);
                foreach (Vector2Int cell in transition.footprintCells)
                {
                    if (surfaces.CarriesDeck(cell))
                    {
                        continue;
                    }

                    foreach (int level in surfaces.LevelsAt(cell))
                    {
                        if (level >= lowerLevel && level <= upperLevel)
                        {
                            continue;
                        }

                        counted.Add(new SurfaceKey(cell, level));
                    }
                }
            }

            return counted.Count;
        }

        /// <summary>
        /// Connection ENDS whose entry level sits above their room's own — the
        /// ends D3's table resolves somewhere other than <c>zoneLevels</c>.
        /// </summary>
        /// <remarks>
        /// The number that decides the slice, and the same shape of evidence D0
        /// shipped: 0 across the corpus means every entry level in production is
        /// the value the pre-D3 code read at that site, so the widening is
        /// latent and bites only the layer-bound routes D5 will author.
        /// <para>
        /// Deliberately re-derived from the ACCEPTED layout and the route intent
        /// rather than carried out of the planner on a static. A rejected tier
        /// attempt would leave a static describing a dungeon that was thrown
        /// away, and the two inputs here are exactly what the accepted plan is
        /// made of.
        /// </para>
        /// </remarks>
        private static int CountLayerOffsetConnectionEnds(DungeonLayout layout, RouteIntent intent)
        {
            if (layout.connections == null || intent?.nodes == null)
            {
                return 0;
            }

            int ends = 0;
            foreach (RoomConnection connection in layout.connections)
            {
                ends += LayerOffsetAt(intent, connection.fromRoom, connection.fromLayerId) != 0 ? 1 : 0;
                ends += LayerOffsetAt(intent, connection.toRoom, connection.toLayerId) != 0 ? 1 : 0;
            }

            return ends;
        }

        /// <summary>
        /// Plan cells carrying a reserved void — the <see cref="PrismKind.OpenVolume"/>
        /// producer's output.
        /// </summary>
        /// <remarks>
        /// This includes authored recipe voids and generated vista volumes. It
        /// catches the failure §11 names: a volume that silently stopped being
        /// registered would disappear from this count on a seed that owns one.
        /// </remarks>
        private static int CountOpenVolumeCells(PrismLedger prisms)
        {
            if (prisms == null)
            {
                return 0;
            }

            int cells = 0;
            foreach (Vector2Int _ in prisms.CellsOfKind(PrismKind.OpenVolume))
            {
                cells++;
            }

            return cells;
        }

        /// <summary>
        /// Room pairs that share at least one plan cell — what the volumetric
        /// <c>Overlaps</c> now permits and the flat one forbade outright.
        /// </summary>
        /// <remarks>
        /// The counterpart number to D3's `layerOffsetConnectionEnds`: 0 across
        /// the corpus means no generic room started stacking, which is §4.1's
        /// stated failure mode for relaxing this test on the band alone.
        /// </remarks>
        private static int CountStackedRoomPairs(DungeonLayout layout)
        {
            if (layout.rooms == null)
            {
                return 0;
            }

            int pairs = 0;
            for (int first = 0; first < layout.rooms.Count; first++)
            {
                for (int second = first + 1; second < layout.rooms.Count; second++)
                {
                    if (layout.rooms[first].Overlaps(layout.rooms[second]))
                    {
                        pairs++;
                    }
                }
            }

            return pairs;
        }

        // Over SURFACES, not columns. A gallery on a storey no column floor
        // reaches is a level the dungeon has, and counting floors alone would let
        // a two-storey plan read as flat and be rejected as "a single level".
        private static int CountDistinctLevels(SurfaceField surfaces)
        {
            var levels = new HashSet<int>();
            foreach (SurfaceKey surface in surfaces.Surfaces())
            {
                levels.Add(surface.level);
            }

            return levels.Count;
        }

        private static float CalculateFloorFillPercent(HashSet<Vector2Int> floorCells)
        {
            if (floorCells == null || floorCells.Count == 0)
            {
                return 0f;
            }

            RectInt bounds = GetCellRect(floorCells);
            int area = Mathf.Max(1, bounds.width * bounds.height);
            return floorCells.Count / (float)area;
        }

        private static int CountLoopEdges(DungeonLayout layout)
        {
            if (layout.rooms == null || layout.connections == null)
            {
                return 0;
            }

            return Mathf.Max(0, layout.connections.Count - Mathf.Max(0, layout.rooms.Count - 1));
        }

        // Over SURFACES: `plan.maxLevel` is the dungeon's vertical extent, and a
        // gallery above every column floor raises it. It also feeds the
        // `bottomToTop` validation check, so answering from floors alone would
        // understate a stacked plan's range.
        private static void GetLevelRange(SurfaceField surfaces, out int minLevel, out int maxLevel)
        {
            minLevel = int.MaxValue;
            maxLevel = int.MinValue;
            foreach (SurfaceKey surface in surfaces.Surfaces())
            {
                minLevel = Mathf.Min(minLevel, surface.level);
                maxLevel = Mathf.Max(maxLevel, surface.level);
            }

            if (surfaces.Count == 0)
            {
                minLevel = 0;
                maxLevel = 0;
            }
        }

        private static int[] CountRoomsPerTier(
            IReadOnlyList<int> roomLevels,
            int topologyCeiling)
        {
            var counts = new int[topologyCeiling + 1];
            foreach (int level in roomLevels)
            {
                if (level >= 0 && level < counts.Length)
                {
                    counts[level]++;
                }
            }

            return counts;
        }

        private static string FormatRoomsPerTier(IReadOnlyList<int> counts)
        {
            var parts = new List<string>();
            for (int level = 0; level < counts.Count; level++)
            {
                parts.Add($"{level}:{counts[level]}");
            }

            return string.Join("/", parts);
        }

        private static string FormatTransitionSummary(
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            var countsByDelta = new SortedDictionary<int, int>();
            var setPiecesByDelta = new SortedDictionary<int, HashSet<string>>();
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                if (transition.firstLevel == UnleveledTransitionEndpoint ||
                    transition.secondLevel == UnleveledTransitionEndpoint)
                {
                    continue;
                }

                int delta = transition.RiseLevels;
                countsByDelta.TryGetValue(delta, out int count);
                countsByDelta[delta] = count + 1;
                string name = string.IsNullOrEmpty(transition.stairPrefabPath)
                    ? PrimaryStairSummaryName()
                    : Path.GetFileNameWithoutExtension(transition.stairPrefabPath);
                if (!setPiecesByDelta.TryGetValue(delta, out HashSet<string> names))
                {
                    names = new HashSet<string>();
                    setPiecesByDelta[delta] = names;
                }

                names.Add(name);
            }

            var parts = new List<string>();
            foreach (var item in countsByDelta)
            {
                string names = string.Join("+", setPiecesByDelta[item.Key]);
                parts.Add($"d{item.Key}={item.Value}({names})");
            }

            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        private static string FormatSynthesizedStairSummary(
            IReadOnlyList<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs)
        {
            if (synthesizedStairs == null || synthesizedStairs.Count == 0)
            {
                return "[]";
            }

            var parts = new List<string>();
            foreach ((string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece) in synthesizedStairs)
            {
                parts.Add($"{setPiece.name}@{gapId}");
            }

            parts.Sort(StringComparer.Ordinal);
            return $"[{string.Join(", ", parts)}]";
        }

        private static string FormatStairUsageHistogram(IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                string name = string.IsNullOrEmpty(transition.stairPrefabPath)
                    ? PrimaryStairSummaryName()
                    : Path.GetFileNameWithoutExtension(transition.stairPrefabPath);
                counts.TryGetValue(name, out int count);
                counts[name] = count + 1;
            }

            if (counts.Count == 0)
            {
                return "[]";
            }

            var parts = new List<string>();
            foreach (var item in counts)
            {
                parts.Add($"{item.Key}:{item.Value}");
            }

            return $"[{string.Join(", ", parts)}]";
        }

        private static string FormatStairTopologyHistogram(
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            IReadOnlyList<ReviewedActiveStairOption> reviewedOptions)
        {
            var topologyByPath = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ReviewedActiveStairOption option in reviewedOptions)
            {
                if (!string.IsNullOrWhiteSpace(option.prefabPath) &&
                    !string.IsNullOrWhiteSpace(option.topology))
                {
                    topologyByPath[option.prefabPath] = option.topology;
                }
            }

            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                string topology = "legacy";
                if (transition.synthesizedSetPiece != null)
                {
                    string synthesizedTopology = transition.synthesizedSetPiece.contractToken.Value<string>("topology") ?? ActiveStraightStairTopology;
                    topology = $"{synthesizedTopology}(synth)";
                }
                else if (!string.IsNullOrEmpty(transition.stairPrefabPath) &&
                    topologyByPath.TryGetValue(transition.stairPrefabPath, out string reviewedTopology))
                {
                    topology = reviewedTopology;
                }
                else if (string.IsNullOrEmpty(transition.stairPrefabPath))
                {
                    topology = ActiveStraightStairTopology;
                }

                counts.TryGetValue(topology, out int count);
                counts[topology] = count + 1;
            }

            var parts = new List<string>();
            foreach (var item in counts)
            {
                parts.Add($"{item.Key}:{item.Value}");
            }

            return "{" + string.Join(", ", parts) + "}";
        }

        private static string FormatStairPlacementClassHistogram(IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                string placementClass = string.IsNullOrWhiteSpace(transition.placementClass)
                    ? EmbeddedStairPlacementClass
                    : transition.placementClass;
                counts.TryGetValue(placementClass, out int count);
                counts[placementClass] = count + 1;
            }

            var parts = new List<string>();
            foreach (var item in counts)
            {
                parts.Add($"{item.Key}:{item.Value}");
            }

            return "{" + string.Join(", ", parts) + "}";
        }

        private static string FormatStairCandidateHistogram(SortedDictionary<string, int> counts)
        {
            if (counts == null || counts.Count == 0)
            {
                return "[]";
            }

            var parts = new List<string>();
            foreach (var item in counts)
            {
                parts.Add($"{item.Key}:{item.Value}");
            }

            return "[" + string.Join(", ", parts) + "]";
        }

        private static string PrimaryStairSummaryName()
        {
            try
            {
                return StairConnectorSettings.Load().PrimaryStair;
            }
            catch
            {
                return "primaryStair";
            }
        }

        // A reported stat about COLUMN FLOORS meeting each other, which is what
        // an overlook is: you stand on one floor and look down at the next. It
        // takes the floors deliberately — a gallery and the chamber under it are
        // one column, not a sheer drop between two.
        private static int CountSpatialOverlookEdges(
            SurfaceField surfaces,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            var transitionKeys = new HashSet<string>();
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                transitionKeys.Add(TransitionKey(transition.firstCell, transition.secondCell));
            }

            int count = 0;
            foreach (Vector2Int cell in surfaces.FlooredCells())
            {
                if (!surfaces.TryGetFloorLevel(cell, out int level))
                {
                    continue;
                }

                foreach (Vector2Int direction in CardinalDirections())
                {
                    Vector2Int neighbor = cell + direction;
                    if (surfaces.TryGetFloorLevel(neighbor, out int neighborLevel))
                    {
                        // Overlook stat (reported only): a sheer vista drop of at
                        // least one 4u major (decision A's tier step).
                        if (CompareCells(cell, neighbor) < 0 &&
                            !transitionKeys.Contains(TransitionKey(cell, neighbor)) &&
                            Mathf.Abs(level - neighborLevel) >= MajorRiseLevels)
                        {
                            count++;
                        }

                        continue;
                    }
                }
            }

            return count;
        }

        private static int CompareCells(Vector2Int first, Vector2Int second)
        {
            if (first.x != second.x)
            {
                return first.x.CompareTo(second.x);
            }

            return first.y.CompareTo(second.y);
        }

        private static IEnumerable<Vector2Int> CardinalDirections()
        {
            yield return Vector2Int.up;
            yield return Vector2Int.right;
            yield return Vector2Int.down;
            yield return Vector2Int.left;
        }

        private static IEnumerable<Vector2Int> CardinalNeighbors(Vector2Int cell)
        {
            yield return cell + Vector2Int.up;
            yield return cell + Vector2Int.right;
            yield return cell + Vector2Int.down;
            yield return cell + Vector2Int.left;
        }

        private static bool AreCardinalNeighbors(Vector2Int first, Vector2Int second)
        {
            int dx = Mathf.Abs(first.x - second.x);
            int dz = Mathf.Abs(first.y - second.y);
            return dx + dz == 1;
        }

        private static string TransitionKey(Vector2Int first, Vector2Int second)
        {
            if (first.x < second.x || first.x == second.x && first.y <= second.y)
            {
                return $"{first.x},{first.y}|{second.x},{second.y}";
            }

            return $"{second.x},{second.y}|{first.x},{first.y}";
        }

        private static void GetCellBounds(HashSet<Vector2Int> cells, out Vector2Int minCell, out Vector2Int maxCell)
        {
            bool initialized = false;
            minCell = Vector2Int.zero;
            maxCell = Vector2Int.zero;

            foreach (var cell in cells)
            {
                if (!initialized)
                {
                    minCell = cell;
                    maxCell = cell;
                    initialized = true;
                    continue;
                }

                minCell = Vector2Int.Min(minCell, cell);
                maxCell = Vector2Int.Max(maxCell, cell);
            }
        }

        private static GameObject InstantiatePrefab(
            string prefabPath,
            string name,
            Transform parent,
            Vector3 position,
            float yRotation)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogError($"Dungeon Lab: missing prefab at '{prefabPath}'.");
                return null;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (!Application.isBatchMode)
            {
                Undo.RegisterCreatedObjectUndo(instance, $"Create {name}");
            }
            instance.name = name;
            instance.transform.SetParent(parent);
            instance.transform.position = position;
            instance.transform.rotation = Quaternion.Euler(0f, yRotation, 0f);

            return instance;
        }

        private static float CalculateYawToMap(Vector2 localDirection, Vector2 worldDirection)
        {
            float localAngle = Mathf.Atan2(localDirection.y, localDirection.x) * Mathf.Rad2Deg;
            float worldAngle = Mathf.Atan2(worldDirection.y, worldDirection.x) * Mathf.Rad2Deg;
            return NormalizeAngle(localAngle - worldAngle);
        }

        private static Vector2 Rotate2D(Vector2 vector, float yRotation)
        {
            float radians = -yRotation * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            return new Vector2(
                vector.x * cos - vector.y * sin,
                vector.x * sin + vector.y * cos);
        }

        private static float NormalizeAngle(float value)
        {
            float normalized = value % 360f;
            return normalized < 0f ? normalized + 360f : normalized;
        }

        private static void EnsurePlayCamera(Bounds dungeonBounds, float cellSize)
        {
            var camera = Camera.main;
            GameObject cameraObject;

            if (camera != null)
            {
                cameraObject = camera.gameObject;
            }
            else
            {
                cameraObject = GameObject.Find("Dungeon Lab Camera");
                if (cameraObject == null)
                {
                    cameraObject = new GameObject("Dungeon Lab Camera");
                    Undo.RegisterCreatedObjectUndo(cameraObject, "Create Dungeon Lab Camera");
                }

                camera = cameraObject.GetComponent<Camera>();
                if (camera == null)
                {
                    camera = cameraObject.AddComponent<Camera>();
                }

                if (cameraObject.GetComponent<AudioListener>() == null)
                {
                    cameraObject.AddComponent<AudioListener>();
                }

                cameraObject.tag = "MainCamera";
            }

            var flyCamera = cameraObject.GetComponent<DungeonLab.FlyCameraController>();
            if (flyCamera == null)
            {
                flyCamera = Undo.AddComponent<DungeonLab.FlyCameraController>(cameraObject);
            }

            Undo.RecordObject(flyCamera, "Configure Dungeon Lab Camera Controller");
            flyCamera.Configure(8f, 3f, 0.12f, false);
            EditorUtility.SetDirty(flyCamera);

            float distance = Mathf.Max(cellSize * 2.5f, dungeonBounds.size.magnitude * 0.35f);
            Vector3 target = dungeonBounds.center;
            Vector3 cameraPosition = target + new Vector3(0f, Mathf.Max(8f, cellSize * 1.5f), -distance);

            Undo.RecordObject(cameraObject.transform, "Position Dungeon Lab Camera");
            cameraObject.transform.position = cameraPosition;
            cameraObject.transform.rotation = Quaternion.LookRotation((target - cameraPosition).normalized, Vector3.up);

            Undo.RecordObject(camera, "Configure Dungeon Lab Camera");
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 1000f;
            camera.fieldOfView = 70f;
        }

        private static void Encapsulate(ref Bounds bounds, ref bool hasBounds, Vector3 position)
        {
            if (!hasBounds)
            {
                bounds = new Bounds(position, Vector3.zero);
                hasBounds = true;
                return;
            }

            bounds.Encapsulate(position);
        }

        // A room's floor footprint: the union of 1-3 axis-aligned rect parts,
        // parts[0] being the dominant rect (magnificence decision B.2: hall and
        // large rooms may be non-rectangular L/T/plus unions of rects; parts
        // never overlap and every wing shares an edge run of >= 2 cells with
        // the dominant rect, so the footprint is connected and sliver-free).
        private sealed class RoomFootprint
        {
            public readonly IReadOnlyList<RectInt> parts;
            public readonly HashSet<Vector2Int> cells;
            public readonly RectInt bounds;

            public RoomFootprint(List<RectInt> parts)
            {
                this.parts = parts;
                cells = new HashSet<Vector2Int>();
                foreach (RectInt part in parts)
                {
                    for (int x = part.xMin; x < part.xMax; x++)
                    {
                        for (int y = part.yMin; y < part.yMax; y++)
                        {
                            cells.Add(new Vector2Int(x, y));
                        }
                    }
                }

                bounds = parts[0];
                for (int i = 1; i < parts.Count; i++)
                {
                    int xMin = Mathf.Min(bounds.xMin, parts[i].xMin);
                    int yMin = Mathf.Min(bounds.yMin, parts[i].yMin);
                    int xMax = Mathf.Max(bounds.xMax, parts[i].xMax);
                    int yMax = Mathf.Max(bounds.yMax, parts[i].yMax);
                    bounds = new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
                }
            }

            public static RoomFootprint FromRect(RectInt rect)
            {
                return new RoomFootprint(new List<RectInt> { rect });
            }

            public RectInt Dominant => parts[0];

            public int Area => cells.Count;

            // A member cell by construction (dominant rect center): safe as a
            // corridor endpoint even when the bbox center falls in a notch.
            public Vector2Int Center => new Vector2Int(
                Dominant.xMin + Dominant.width / 2,
                Dominant.yMin + Dominant.height / 2);

            public bool Contains(Vector2Int cell)
            {
                return cells.Contains(cell);
            }

            public bool Overlaps(RoomFootprint other)
            {
                foreach (RectInt mine in parts)
                {
                    foreach (RectInt theirs in other.parts)
                    {
                        if (mine.Overlaps(theirs))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            // Deterministic cell order (bbox row-major, y outer) for level fills
            // and id maps — HashSet iteration order must never reach output.
            public IEnumerable<Vector2Int> CellsRowMajor()
            {
                for (int y = bounds.yMin; y < bounds.yMax; y++)
                {
                    for (int x = bounds.xMin; x < bounds.xMax; x++)
                    {
                        var cell = new Vector2Int(x, y);
                        if (cells.Contains(cell))
                        {
                            yield return cell;
                        }
                    }
                }
            }

            // Maximal straight runs of boundary cells facing `direction`: cells
            // in the room whose neighbor in `direction` is outside the
            // footprint, grouped per facing line, consecutive along the
            // transverse axis. For a plain rect this is the single full edge
            // row; for unions each part edge on the true outline contributes
            // its own run. `start` is the lowest-transverse cell of the run.
            public List<(Vector2Int start, int length)> BoundaryRuns(Vector2Int direction)
            {
                bool transverseIsX = direction.x == 0;
                var runs = new List<(Vector2Int start, int length)>();
                int lineMin = transverseIsX ? bounds.yMin : bounds.xMin;
                int lineMax = transverseIsX ? bounds.yMax : bounds.xMax;
                int transverseMin = transverseIsX ? bounds.xMin : bounds.yMin;
                int transverseMax = transverseIsX ? bounds.xMax : bounds.yMax;
                for (int line = lineMin; line < lineMax; line++)
                {
                    int runStart = int.MinValue;
                    for (int t = transverseMin; t <= transverseMax; t++)
                    {
                        Vector2Int cell = transverseIsX ? new Vector2Int(t, line) : new Vector2Int(line, t);
                        bool isBoundary = t < transverseMax && cells.Contains(cell) && !cells.Contains(cell + direction);
                        if (isBoundary && runStart == int.MinValue)
                        {
                            runStart = t;
                        }
                        else if (!isBoundary && runStart != int.MinValue)
                        {
                            Vector2Int start = transverseIsX
                                ? new Vector2Int(runStart, line)
                                : new Vector2Int(line, runStart);
                            runs.Add((start, t - runStart));
                            runStart = int.MinValue;
                        }
                    }
                }

                return runs;
            }

            // The room cell furthest along `direction` at the given transverse
            // coordinate (the row for ±x, the column for ±y) — the true edge
            // cell a cardinal boundary query leaves from. False when the footprint
            // has no cell on that line.
            public bool TryGetEdgeCellTowards(Vector2Int direction, int transverse, out Vector2Int edgeCell)
            {
                edgeCell = default;
                bool found = false;
                int best = 0;
                foreach (RectInt part in parts)
                {
                    bool rowQuery = direction.x != 0;
                    if (rowQuery ? (transverse < part.yMin || transverse >= part.yMax)
                                 : (transverse < part.xMin || transverse >= part.xMax))
                    {
                        continue;
                    }

                    int extreme = direction.x > 0 ? part.xMax - 1
                        : direction.x < 0 ? part.xMin
                        : direction.y > 0 ? part.yMax - 1
                        : part.yMin;
                    int along = direction.x != 0 ? extreme : extreme;
                    bool better = !found ||
                        (direction.x + direction.y > 0 ? along > best : along < best);
                    if (better)
                    {
                        best = along;
                        edgeCell = direction.x != 0
                            ? new Vector2Int(extreme, transverse)
                            : new Vector2Int(transverse, extreme);
                        found = true;
                    }
                }

                return found;
            }
        }

        // A zone node's walkable area: exact cell membership plus the bbox for
        // geometric extent queries. Bbox Contains is NOT membership for non-rect
        // rooms (corridor cells can thread an L-notch inside the bbox), so every
        // path/cell test must go through Contains.
        private readonly struct ZoneArea
        {
            public readonly RectInt bounds;
            public readonly HashSet<Vector2Int> cells;

            public ZoneArea(RectInt bounds, HashSet<Vector2Int> cells)
            {
                this.bounds = bounds;
                this.cells = cells;
            }

            public bool Contains(Vector2Int cell)
            {
                return cells.Contains(cell);
            }
        }

        private readonly struct DungeonLayout
        {
            // The PRE-elevation plan shadow (design §3.1). `floorCells` is the
            // same set it always was and is still the domain the level field is
            // computed over; A1 gives it a name for the role it plays, so that
            // the post-elevation surface field can be a separate thing rather
            // than a redefinition of this one.
            public readonly PlanShadow shadow;
            public HashSet<Vector2Int> floorCells => shadow?.Cells;
            public readonly List<RoomFootprint> rooms;
            public readonly List<RoomConnection> connections;
            public readonly IReadOnlyList<RoomZonePlan> roomZones;
            public readonly int connectorCandidateCount;
            // The shaft windows the annex pass kept clear beside each transition
            // corridor, so a stairwell tower has somewhere to stand. Authored
            // void by design §4.1, and carried here rather than recomputed
            // because it depends on what was free at the moment it was taken.
            public readonly IReadOnlyCollection<Vector2Int> reservedShaftCells;

            public DungeonLayout(HashSet<Vector2Int> floorCells, List<RoomFootprint> rooms, List<RoomConnection> connections)
                : this(floorCells, rooms, connections, Array.Empty<RoomZonePlan>(), 0)
            {
            }

            public DungeonLayout(
                HashSet<Vector2Int> floorCells,
                List<RoomFootprint> rooms,
                List<RoomConnection> connections,
                IReadOnlyList<RoomZonePlan> roomZones,
                int connectorCandidateCount,
                IReadOnlyCollection<Vector2Int> reservedShaftCells = null)
            {
                shadow = new PlanShadow(floorCells);
                this.rooms = rooms;
                this.connections = connections;
                this.roomZones = roomZones ?? Array.Empty<RoomZonePlan>();
                this.connectorCandidateCount = connectorCandidateCount;
                this.reservedShaftCells = reservedShaftCells ?? Array.Empty<Vector2Int>();
            }
        }

        // An intra-room 1u split: the room's footprint cells divide into a lower
        // and a raised (+1u) zone along one straight seam line; every adjacent
        // cell pair across the seam carries a rise-1 step strip (seam
        // transition). For non-rect rooms the seam line may cross a notch, so
        // pairs are precomputed against the footprint and the seam can run in
        // segments; lowerRect/raisedRect are the side BBOXES (extent only, not
        // membership — use the cell sets).
        private readonly struct RoomZonePlan
        {
            public readonly int roomIndex;
            public readonly RectInt lowerRect;
            public readonly RectInt raisedRect;
            public readonly HashSet<Vector2Int> lowerCells;
            public readonly HashSet<Vector2Int> raisedCells;
            private readonly List<(Vector2Int lowerCell, Vector2Int raisedCell)> seamCellPairs;

            public RoomZonePlan(
                int roomIndex,
                HashSet<Vector2Int> lowerCells,
                HashSet<Vector2Int> raisedCells)
            {
                this.roomIndex = roomIndex;
                this.lowerCells = lowerCells;
                this.raisedCells = raisedCells;
                lowerRect = GetCellRect(lowerCells);
                raisedRect = GetCellRect(raisedCells);

                seamCellPairs = new List<(Vector2Int, Vector2Int)>();
                var sorted = new List<Vector2Int>(lowerCells);
                sorted.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
                foreach (Vector2Int lower in sorted)
                {
                    foreach (Vector2Int offset in new[]
                        { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) })
                    {
                        Vector2Int neighbor = lower + offset;
                        if (raisedCells.Contains(neighbor))
                        {
                            seamCellPairs.Add((lower, neighbor));
                        }
                    }
                }
            }

            // Adjacent (lowerCell, raisedCell) pairs across the seam line, in
            // ascending order for determinism.
            public List<(Vector2Int lowerCell, Vector2Int raisedCell)> SeamCellPairs()
            {
                return seamCellPairs;
            }
        }

        // Zone node space for level assignment: nodes 0..roomCount-1 are the rooms'
        // base (lower) zones; raised zones of split rooms get appended node indices.
        private sealed class RoomZoneContext
        {
            public readonly int roomCount;
            public readonly int nodeCount;
            private readonly int[] raisedNodeByRoom;
            private readonly RoomZonePlan[] planByRoom;
            private readonly int[] roomByNode;

            private RoomZoneContext(int roomCount, int nodeCount, int[] raisedNodeByRoom, RoomZonePlan[] planByRoom, int[] roomByNode)
            {
                this.roomCount = roomCount;
                this.nodeCount = nodeCount;
                this.raisedNodeByRoom = raisedNodeByRoom;
                this.planByRoom = planByRoom;
                this.roomByNode = roomByNode;
            }

            public static RoomZoneContext Build(DungeonLayout layout)
            {
                int roomCount = layout.rooms.Count;
                var raisedNodeByRoom = new int[roomCount];
                var planByRoom = new RoomZonePlan[roomCount];
                for (int i = 0; i < roomCount; i++)
                {
                    raisedNodeByRoom[i] = -1;
                }

                int nodeCount = roomCount;
                var roomByNode = new List<int>();
                for (int i = 0; i < roomCount; i++)
                {
                    roomByNode.Add(i);
                }

                foreach (RoomZonePlan plan in layout.roomZones)
                {
                    raisedNodeByRoom[plan.roomIndex] = nodeCount;
                    planByRoom[plan.roomIndex] = plan;
                    roomByNode.Add(plan.roomIndex);
                    nodeCount++;
                }

                return new RoomZoneContext(roomCount, nodeCount, raisedNodeByRoom, planByRoom, roomByNode.ToArray());
            }

            public int RaisedNodeOfRoom(int roomIndex)
            {
                return raisedNodeByRoom[roomIndex];
            }

            public bool TryGetPlan(int roomIndex, out RoomZonePlan plan, out int raisedNode)
            {
                raisedNode = raisedNodeByRoom[roomIndex];
                plan = planByRoom[roomIndex];
                return raisedNode >= 0;
            }

            public int RoomOfNode(int node)
            {
                return roomByNode[node];
            }

            public bool IsRaisedNode(int node)
            {
                return node >= roomCount;
            }

            public bool IsSeamEdge(int firstNode, int secondNode)
            {
                return RoomOfNode(firstNode) == RoomOfNode(secondNode) &&
                    IsRaisedNode(firstNode) != IsRaisedNode(secondNode);
            }

            public int NodeOfCell(int roomIndex, Vector2Int cell)
            {
                int raisedNode = raisedNodeByRoom[roomIndex];
                if (raisedNode >= 0 && planByRoom[roomIndex].raisedCells.Contains(cell))
                {
                    return raisedNode;
                }

                return roomIndex;
            }

            public RectInt NodeRect(IReadOnlyList<RoomFootprint> rooms, int node)
            {
                int roomIndex = RoomOfNode(node);
                if (raisedNodeByRoom[roomIndex] < 0)
                {
                    return rooms[roomIndex].bounds;
                }

                return IsRaisedNode(node) ? planByRoom[roomIndex].raisedRect : planByRoom[roomIndex].lowerRect;
            }

            public ZoneArea NodeArea(IReadOnlyList<RoomFootprint> rooms, int node)
            {
                int roomIndex = RoomOfNode(node);
                if (raisedNodeByRoom[roomIndex] < 0)
                {
                    return new ZoneArea(rooms[roomIndex].bounds, rooms[roomIndex].cells);
                }

                RoomZonePlan plan = planByRoom[roomIndex];
                return IsRaisedNode(node)
                    ? new ZoneArea(plan.raisedRect, plan.raisedCells)
                    : new ZoneArea(plan.lowerRect, plan.lowerCells);
            }
        }

        // A corridor, and — as of A1 — a corridor that can be NAMED.
        //
        // It used to be `{fromRoom, toRoom, path}`, and a connection was matched
        // to its route edge by ROOM PAIR. That is the defect design review
        // finding 1 named: two corridors between one room pair are
        // indistinguishable at the lookup, before their paths are ever compared,
        // so any authored layer binding would be discarded exactly there. The
        // identity has to exist before it can be bound to anything, and corridors
        // are claimed pre-elevation, so no later phase can retrofit it.
        //
        // `plannedBand` is DATA in A1 and nothing reads it as a rule. Corridor
        // exclusivity stays unconditional until Phase D: relaxing it to "may
        // share a cell iff the bands are disjoint" would accept embeddings that
        // are rejected today — `atrium-ring` alone spans levels 0..24, so disjoint
        // bands are common rather than hypothetical — and that is not an
        // output-neutral change.
        private readonly struct RoomConnection
        {
            public readonly int fromRoom;
            public readonly int toRoom;
            // Whether this corridor realizes a declared route edge or was
            // synthesized as a loop. A loop legitimately has no edge.
            public readonly ConnectionSource source;
            // The route edge this corridor realizes; empty for a SynthesizedLoop.
            public readonly string edgeId;
            // Stable, unique, ALWAYS present — so a loop corridor is nameable in
            // diagnostics and reservations even though it has no edge.
            public readonly string connectionId;
            // The corridor's planned vertical extent, from the topology's
            // DECLARED ABSOLUTE node levels (design §8.1). Absolute on purpose: a
            // layer name is room-local, so `(cell, layerId)` is not a vertical
            // identity and cannot separate two unrelated rooms.
            public readonly LevelBand plannedBand;
            // Which declared storey each end binds to; empty is the room's base,
            // which is every connection in the shipped corpus. A SynthesizedLoop
            // can never carry one — it has no route edge to declare it.
            public readonly string fromLayerId;
            public readonly string toLayerId;
            public readonly List<Vector2Int> path;

            private RoomConnection(
                int fromRoom,
                int toRoom,
                ConnectionSource source,
                string edgeId,
                string connectionId,
                LevelBand plannedBand,
                string fromLayerId,
                string toLayerId,
                List<Vector2Int> path)
            {
                this.fromRoom = fromRoom;
                this.toRoom = toRoom;
                this.source = source;
                this.edgeId = edgeId ?? string.Empty;
                this.connectionId = connectionId ?? string.Empty;
                this.plannedBand = plannedBand;
                this.fromLayerId = fromLayerId ?? string.Empty;
                this.toLayerId = toLayerId ?? string.Empty;
                this.path = path;
            }

            /// <summary>
            /// Whether this corridor AUTHORIZES a relaxation of corridor
            /// exclusivity or third-room crossing (design §8.1).
            /// </summary>
            /// <remarks>
            /// Authorization only. What DECIDES either relaxation is
            /// <see cref="plannedBand"/> — two connections may share a cell when
            /// both are layer-bound AND their absolute bands are disjoint. A
            /// layer id can never decide it: one room's "gallery" and another's
            /// "floor" may sit at the same absolute level.
            /// </remarks>
            public bool IsLayerBound =>
                !string.IsNullOrEmpty(fromLayerId) || !string.IsNullOrEmpty(toLayerId);

            public static RoomConnection ForRouteEdge(
                int fromRoom,
                int toRoom,
                string edgeId,
                LevelBand plannedBand,
                List<Vector2Int> path,
                string fromLayerId = "",
                string toLayerId = "")
            {
                return new RoomConnection(
                    fromRoom,
                    toRoom,
                    ConnectionSource.RouteEdge,
                    edgeId,
                    $"edge:{edgeId}",
                    plannedBand,
                    fromLayerId,
                    toLayerId,
                    path);
            }

            public static RoomConnection ForSynthesizedLoop(
                int firstRoom,
                int secondRoom,
                LevelBand plannedBand,
                List<Vector2Int> path)
            {
                return new RoomConnection(
                    firstRoom,
                    secondRoom,
                    ConnectionSource.SynthesizedLoop,
                    string.Empty,
                    $"loop:{firstRoom}:{secondRoom}",
                    plannedBand,
                    string.Empty,
                    string.Empty,
                    path);
            }

            /// <summary>
            /// The per-connection RNG subject.
            /// </summary>
            /// <remarks>
            /// Renders the room pair, which is what the two subject-keyed streams
            /// have always used. It is deliberately NOT `connectionId` yet:
            /// changing a stream's subject re-phases its draws and moves every
            /// affected seed, and A1's claim is that nothing moves. Routing both
            /// sites through one accessor makes that widening a one-line change
            /// in the phase that is allowed to rebaseline.
            /// </remarks>
            public string RngSubject => RngSubjectFor(fromRoom, toRoom);

            /// <summary>
            /// The same subject for a corridor that does not exist yet — the
            /// loop pass draws its path before it has a connection to name.
            /// </summary>
            public static string RngSubjectFor(int firstRoom, int secondRoom)
            {
                return $"{firstRoom}:{secondRoom}";
            }
        }

        /// <summary>
        /// The elevation each END of each connection meets its room at
        /// (design §13, phase D3).
        /// </summary>
        /// <remarks>
        /// BESIDE <c>zoneLevels</c>, not instead of it. A zone level is a
        /// property of a PLACE — one room zone, one elevation — and that was
        /// enough while every corridor met its room on the ground. A layer-bound
        /// edge breaks the assumption: two connections may reach the same room
        /// at two storeys, so the elevation a corridor resolves at is a property
        /// of the <c>(connection, end)</c> pair and of nothing smaller.
        /// <para>
        /// The entry level is the zone level PLUS the offset of whatever layer
        /// that end bound. Additive on purpose, and that is what makes the whole
        /// table output-neutral by construction: an unbound end has no offset
        /// and resolves at exactly the <c>zoneLevels[node]</c> every caller read
        /// before this type existed.
        /// </para>
        /// <para>
        /// The additive form also composes with the +1 raised-zone accent
        /// instead of overriding it. Slice 3 keeps that composition from being
        /// requested by excluding every node with a real structural storey from
        /// local <c>RoomZonePlan</c> splitting, whether the room is authored or
        /// generic. A base-only layer table — D2's authorization for a stacked
        /// crossing — may still sit on a split room, and its offset is 0.
        /// </para>
        /// </remarks>
        private sealed class ConnectionEntryLevels
        {
            private readonly int[] fromLevels;
            private readonly int[] toLevels;

            /// <summary>
            /// How many connection ends resolved somewhere OTHER than their zone
            /// level. It is the number that decides this slice: 0 means every
            /// entry level in the corpus is the value the old code read.
            /// </summary>
            public readonly int layerOffsetEnds;

            private ConnectionEntryLevels(int[] fromLevels, int[] toLevels, int layerOffsetEnds)
            {
                this.fromLevels = fromLevels;
                this.toLevels = toLevels;
                this.layerOffsetEnds = layerOffsetEnds;
            }

            public static ConnectionEntryLevels Build(
                DungeonLayout layout,
                RoomZoneContext zones,
                IReadOnlyList<int> zoneLevels,
                RouteIntent intent)
            {
                int count = layout.connections.Count;
                var fromLevels = new int[count];
                var toLevels = new int[count];
                int layerOffsetEnds = 0;
                for (int index = 0; index < count; index++)
                {
                    RoomConnection connection = layout.connections[index];
                    ResolveConnectionNodes(
                        zones,
                        layout.rooms,
                        connection,
                        out int fromNode,
                        out int toNode);
                    int fromOffset = LayerOffsetAt(intent, connection.fromRoom, connection.fromLayerId);
                    int toOffset = LayerOffsetAt(intent, connection.toRoom, connection.toLayerId);
                    fromLevels[index] = zoneLevels[fromNode] + fromOffset;
                    toLevels[index] = zoneLevels[toNode] + toOffset;
                    layerOffsetEnds += fromOffset != 0 ? 1 : 0;
                    layerOffsetEnds += toOffset != 0 ? 1 : 0;
                }

                return new ConnectionEntryLevels(fromLevels, toLevels, layerOffsetEnds);
            }

            public void Resolve(int connectionIndex, out int fromLevel, out int toLevel)
            {
                fromLevel = fromLevels[connectionIndex];
                toLevel = toLevels[connectionIndex];
            }
        }

        /// <summary>
        /// The offset a connection end's layer binding adds to its zone level.
        /// </summary>
        /// <remarks>
        /// A synthesized loop carries no binding by construction — it has no
        /// route edge to declare one — so it always lands on 0 here.
        /// </remarks>
        private static int LayerOffsetAt(RouteIntent intent, int room, string layerId)
        {
            if (string.IsNullOrEmpty(layerId) ||
                intent?.nodes == null ||
                room < 0 ||
                room >= intent.nodes.Length)
            {
                return 0;
            }

            return intent.nodes[room].LayerOffset(layerId);
        }

        private readonly struct ReviewedActiveStairOption
        {
            public readonly string name;
            public readonly string prefabPath;
            public readonly int rise;
            public readonly int laneCount;
            public readonly int runLength;
            public readonly string topology;
            public readonly bool isBridge;
            public readonly Vector2 localBoundsMin;
            public readonly Vector2 localBoundsMax;
            public readonly Vector2Int[] footprintCells;
            public readonly Vector2Int[] entryCells;
            public readonly Vector2Int[] exitCells;
            public readonly Vector2 localEntryPoint;
            public readonly Vector2 localExitPoint;
            public readonly int entryDirection;
            public readonly int exitDirection;
            public readonly ReviewedActiveStairPort entryPort;
            public readonly ReviewedActiveStairPort exitPort;

            public ReviewedActiveStairOption(
                string name,
                string prefabPath,
                int rise,
                int laneCount,
                int runLength,
                string topology,
                bool isBridge,
                Vector2 localBoundsMin,
                Vector2 localBoundsMax,
                Vector2Int[] footprintCells,
                Vector2Int[] entryCells,
                Vector2Int[] exitCells,
                Vector2 localEntryPoint,
                Vector2 localExitPoint,
                int entryDirection,
                int exitDirection)
            {
                this.name = name;
                this.prefabPath = prefabPath;
                this.rise = rise;
                this.laneCount = laneCount;
                this.runLength = runLength;
                this.topology = topology ?? string.Empty;
                this.isBridge = isBridge;
                this.localBoundsMin = localBoundsMin;
                this.localBoundsMax = localBoundsMax;
                this.footprintCells = footprintCells ?? Array.Empty<Vector2Int>();
                this.entryCells = entryCells ?? Array.Empty<Vector2Int>();
                this.exitCells = exitCells ?? Array.Empty<Vector2Int>();
                this.localEntryPoint = localEntryPoint;
                this.localExitPoint = localExitPoint;
                this.entryDirection = entryDirection;
                this.exitDirection = exitDirection;
                entryPort = new ReviewedActiveStairPort(entryCells, localEntryPoint, entryDirection, level: 0);
                exitPort = new ReviewedActiveStairPort(exitCells, localExitPoint, exitDirection, level: rise);
            }
        }

        private readonly struct ReviewedActiveStairPort
        {
            public readonly Vector2Int[] cells;
            public readonly Vector2 localEdgePoint;
            public readonly int direction;
            public readonly int level;

            public ReviewedActiveStairPort(
                Vector2Int[] cells,
                Vector2 localEdgePoint,
                int direction,
                int level)
            {
                this.cells = cells ?? Array.Empty<Vector2Int>();
                this.localEdgePoint = localEdgePoint;
                this.direction = direction;
                this.level = level;
            }
        }

        private readonly struct ReviewedStairPortPlacement
        {
            public readonly Vector2 position;
            public readonly float yRotation;
            public readonly int worldEntryDirection;
            public readonly int worldExitDirection;
            public readonly Vector2Int[] lowerLandingCells;
            public readonly Vector2Int[] upperLandingCells;
            public readonly Vector2Int[] footprintCells;

            public ReviewedStairPortPlacement(
                Vector2 position,
                float yRotation,
                int worldEntryDirection,
                int worldExitDirection,
                Vector2Int[] lowerLandingCells,
                Vector2Int[] upperLandingCells,
                Vector2Int[] footprintCells)
            {
                this.position = position;
                this.yRotation = yRotation;
                this.worldEntryDirection = worldEntryDirection;
                this.worldExitDirection = worldExitDirection;
                this.lowerLandingCells = lowerLandingCells ?? Array.Empty<Vector2Int>();
                this.upperLandingCells = upperLandingCells ?? Array.Empty<Vector2Int>();
                this.footprintCells = footprintCells ?? Array.Empty<Vector2Int>();
            }
        }

        private enum PortGraphNodeKind
        {
            Floor,
            StairPort
        }

        private enum PortGraphEdgeKind
        {
            FloorAdjacency,
            PortLanding,
            StairInternal,
            OpeningFall
        }

        private readonly struct PortGraphNode
        {
            public readonly string key;
            public readonly PortGraphNodeKind kind;

            private PortGraphNode(string key, PortGraphNodeKind kind)
            {
                this.key = key;
                this.kind = kind;
            }

            // The traversal graph has always been keyed on (cell, level) — this
            // is where the design's canonical surface identity already lived
            // (§1.3 finding 1). A1 gives it the type; SurfaceKey.Token renders
            // the historical string verbatim, so no node key moves.
            public static PortGraphNode Floor(SurfaceKey surface)
            {
                return new PortGraphNode($"F:{surface.Token}", PortGraphNodeKind.Floor);
            }

            public static PortGraphNode StairPort(int transitionIndex, string label, int level)
            {
                return new PortGraphNode($"P:{transitionIndex}:{label}:L{level}", PortGraphNodeKind.StairPort);
            }
        }

        private readonly struct PortGraphEdge
        {
            public readonly string first;
            public readonly string second;
            public readonly PortGraphEdgeKind kind;
            // True only for a one-way descent (design §3.2's `Fall`, and `Drop`
            // if it is ever adopted). Slice 5's planned apertures publish these
            // edges; ordinary floors and physical transitions remain reversible.
            public readonly bool directed;

            public PortGraphEdge(string first, string second, PortGraphEdgeKind kind, bool directed = false)
            {
                this.first = first;
                this.second = second;
                this.kind = kind;
                this.directed = directed;
            }
        }

        private sealed class FloorStairPortGraph
        {
            private readonly Dictionary<string, PortGraphNode> nodes = new Dictionary<string, PortGraphNode>(StringComparer.Ordinal);
            private readonly List<PortGraphEdge> edges = new List<PortGraphEdge>();
            private readonly HashSet<string> edgeKeys = new HashSet<string>(StringComparer.Ordinal);

            public int NodeCount => nodes.Count;
            public int DirectedEdgeCount
            {
                get
                {
                    int count = 0;
                    foreach (PortGraphEdge edge in edges)
                    {
                        count += edge.directed ? 1 : 0;
                    }

                    return count;
                }
            }

            public string Summary
            {
                get
                {
                    int floorNodes = 0;
                    int stairPortNodes = 0;
                    var edgeKinds = new Dictionary<PortGraphEdgeKind, int>();
                    foreach (PortGraphNode node in nodes.Values)
                    {
                        if (node.kind == PortGraphNodeKind.Floor)
                        {
                            floorNodes++;
                        }
                        else if (node.kind == PortGraphNodeKind.StairPort)
                        {
                            stairPortNodes++;
                        }
                    }

                    foreach (PortGraphEdge edge in edges)
                    {
                        edgeKinds.TryGetValue(edge.kind, out int count);
                        edgeKinds[edge.kind] = count + 1;
                    }

                    return $"nodes:{nodes.Count},floor:{floorNodes},stairPorts:{stairPortNodes},edges:{edges.Count},edgeKinds:{FormatEdgeKindHistogram(edgeKinds)}";
                }
            }

            public void EnsureNode(PortGraphNode node)
            {
                if (!nodes.ContainsKey(node.key))
                {
                    nodes[node.key] = node;
                }
            }

            public void AddEdge(PortGraphNode first, PortGraphNode second, PortGraphEdgeKind kind)
            {
                EnsureNode(first);
                EnsureNode(second);
                if (first.key == second.key)
                {
                    return;
                }

                string a = string.CompareOrdinal(first.key, second.key) <= 0 ? first.key : second.key;
                string b = a == first.key ? second.key : first.key;
                string edgeKey = $"{a}|{b}|{kind}";
                if (!edgeKeys.Add(edgeKey))
                {
                    return;
                }

                edges.Add(new PortGraphEdge(a, b, kind));
            }

            public bool AddDirectedEdge(
                PortGraphNode first,
                PortGraphNode second,
                PortGraphEdgeKind kind)
            {
                if (!nodes.ContainsKey(first.key) ||
                    !nodes.ContainsKey(second.key) ||
                    first.key == second.key)
                {
                    return false;
                }

                string edgeKey = $"{first.key}>{second.key}|{kind}";
                if (!edgeKeys.Add(edgeKey))
                {
                    return true;
                }

                edges.Add(new PortGraphEdge(first.key, second.key, kind, directed: true));
                return true;
            }

            /// <summary>
            /// The fall-free subgraph must be connected (design §3.3).
            /// </summary>
            /// <remarks>
            /// Delete every directed edge, treat the rest as undirected, and
            /// require one component. This REPLACES the old
            /// `IsGloballyConnected` rather than sitting beside it, and it is
            /// strictly stronger than the strong connectivity an earlier draft
            /// proposed: strong connectivity permits a region that can only be
            /// left by taking a second fall, which contradicts the rule that a
            /// pit's return route be reversible. Fall-free connectivity implies
            /// full strong connectivity AND per-fall reversibility, and it is
            /// the literal statement of "pits create optional branches".
            /// <para>
            /// With no aperture it is arithmetically identical to the check it
            /// replaced. With an aperture, deleting the directed edge proves
            /// that the topology supplied a reversible return route.
            /// </para>
            /// </remarks>
            public bool IsFallFreeConnected(out string message)
            {
                Dictionary<string, List<string>> adjacency = BuildAdjacency();
                if (adjacency.Count == 0)
                {
                    message = "floor/stair port graph had no nodes";
                    return false;
                }

                var visited = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<string>();
                foreach (string node in adjacency.Keys)
                {
                    visited.Add(node);
                    queue.Enqueue(node);
                    break;
                }

                while (queue.Count > 0)
                {
                    string node = queue.Dequeue();
                    foreach (string neighbor in adjacency[node])
                    {
                        if (visited.Add(neighbor))
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }

                if (visited.Count != adjacency.Count)
                {
                    message = $"floor/stair port graph reached {visited.Count}/{adjacency.Count} nodes";
                    return false;
                }

                message = $"floor/stair port graph connected {visited.Count}/{adjacency.Count} nodes";
                return true;
            }

            private Dictionary<string, List<string>> BuildAdjacency()
            {
                var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (string node in nodes.Keys)
                {
                    adjacency[node] = new List<string>();
                }

                foreach (PortGraphEdge edge in edges)
                {
                    // A directed edge is a one-way descent and contributes
                    // nothing to fall-free reachability in either direction:
                    // deleting it is the point of the rule, not an omission.
                    if (edge.directed ||
                        !adjacency.ContainsKey(edge.first) || !adjacency.ContainsKey(edge.second))
                    {
                        continue;
                    }

                    adjacency[edge.first].Add(edge.second);
                    adjacency[edge.second].Add(edge.first);
                }

                return adjacency;
            }

            private static string FormatEdgeKindHistogram(Dictionary<PortGraphEdgeKind, int> histogram)
            {
                var parts = new List<string>();
                var keys = new List<PortGraphEdgeKind>(histogram.Keys);
                keys.Sort();
                foreach (PortGraphEdgeKind key in keys)
                {
                    parts.Add($"{key}:{histogram[key]}");
                }

                return "{" + string.Join(", ", parts) + "}";
            }

        }

        private readonly struct RouteTransitionResolution
        {
            public readonly string edgeId;
            public readonly int fromRoom;
            public readonly int toRoom;
            public readonly RouteTransitionKind transitionKind;
            public readonly int requiredRiseLevels;
            public readonly int resolvedRiseLevels;
            public readonly string placementClass;
            public readonly Vector2Int transitionFirstCell;
            public readonly Vector2Int transitionSecondCell;
            public readonly Vector2Int[] lowerLandingCells;
            public readonly Vector2Int[] upperLandingCells;
            public readonly Vector2Int[] footprintCells;

            public RouteTransitionResolution(
                string edgeId,
                int fromRoom,
                int toRoom,
                RouteTransitionKind transitionKind,
                int requiredRiseLevels,
                int resolvedRiseLevels,
                string placementClass,
                Vector2Int transitionFirstCell,
                Vector2Int transitionSecondCell,
                Vector2Int[] lowerLandingCells,
                Vector2Int[] upperLandingCells,
                Vector2Int[] footprintCells)
            {
                this.edgeId = edgeId;
                this.fromRoom = fromRoom;
                this.toRoom = toRoom;
                this.transitionKind = transitionKind;
                this.requiredRiseLevels = requiredRiseLevels;
                this.resolvedRiseLevels = resolvedRiseLevels;
                this.placementClass = placementClass ?? string.Empty;
                this.transitionFirstCell = transitionFirstCell;
                this.transitionSecondCell = transitionSecondCell;
                this.lowerLandingCells = lowerLandingCells ?? Array.Empty<Vector2Int>();
                this.upperLandingCells = upperLandingCells ?? Array.Empty<Vector2Int>();
                this.footprintCells = footprintCells ?? Array.Empty<Vector2Int>();
            }
        }

        private readonly struct RouteRequirementResolution
        {
            public readonly RouteTransitionResolution[] transitions;
            public readonly int bottomLevel;
            public readonly int topLevel;
            public readonly Vector2Int vistaSourceCell;
            public readonly Vector2Int vistaTargetCell;
            public readonly int vistaSourceLevel;
            public readonly int vistaTargetLevel;
            public readonly Vector2Int vistaSourceFacing;
            public readonly Vector2Int vistaTargetFacing;
            public readonly Vector2Int[] reservedVistaCells;
            public readonly bool finalVistaValid;

            public RouteRequirementResolution(
                RouteTransitionResolution[] transitions,
                int bottomLevel,
                int topLevel,
                Vector2Int vistaSourceCell,
                Vector2Int vistaTargetCell,
                int vistaSourceLevel,
                int vistaTargetLevel,
                Vector2Int vistaSourceFacing,
                Vector2Int vistaTargetFacing,
                Vector2Int[] reservedVistaCells,
                bool finalVistaValid)
            {
                this.transitions = transitions ?? Array.Empty<RouteTransitionResolution>();
                this.bottomLevel = bottomLevel;
                this.topLevel = topLevel;
                this.vistaSourceCell = vistaSourceCell;
                this.vistaTargetCell = vistaTargetCell;
                this.vistaSourceLevel = vistaSourceLevel;
                this.vistaTargetLevel = vistaTargetLevel;
                this.vistaSourceFacing = vistaSourceFacing;
                this.vistaTargetFacing = vistaTargetFacing;
                this.reservedVistaCells = reservedVistaCells ?? Array.Empty<Vector2Int>();
                this.finalVistaValid = finalVistaValid;
            }

            public int RouteClimbLevels => topLevel - bottomLevel;
        }

        private readonly struct NamedVistaPromontoryResolution
        {
            public readonly string vistaId;
            public readonly string targetNodeId;
            public readonly Vector2Int sourceCell;
            public readonly Vector2Int targetCell;
            public readonly Vector2Int facing;
            public readonly int level;
            public readonly Vector2Int[] cells;

            public NamedVistaPromontoryResolution(
                string vistaId,
                string targetNodeId,
                Vector2Int sourceCell,
                Vector2Int targetCell,
                Vector2Int facing,
                int level,
                Vector2Int[] cells)
            {
                this.vistaId = vistaId ?? string.Empty;
                this.targetNodeId = targetNodeId ?? string.Empty;
                this.sourceCell = sourceCell;
                this.targetCell = targetCell;
                this.facing = facing;
                this.level = level;
                this.cells = cells ?? Array.Empty<Vector2Int>();
            }
        }

        private readonly struct TieredLevelPlan
        {
            // The POST-elevation canonical surfaces (design §3.1). The plan
            // STORES surfaces; every consumer now reads them.
            public readonly SurfaceField surfaces;

            public readonly List<ElevationEdgeModel.TransitionEdge> transitions;
            public readonly int levelCount;
            public readonly int minLevel;
            public readonly int maxLevel;
            public readonly string roomsPerTierSummary;
            public readonly int overlookCount;
            public readonly string transitionSummary;
            public readonly int connectorCandidateCount;
            public readonly string stairUsageSummary;
            public readonly string topologySummary;
            public readonly string placementClassSummary;
            public readonly string stairCandidateSummary;
            public readonly string portGraphSummary;
            public readonly string archetypeName;
            // Online synthesis (step 7): the provisional staircases this plan uses,
            // for the per-dungeon stats line and the pending review log.
            public readonly List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs;
            public readonly string synthesizedStairSummary;
            // Showpiece dais (decision 46 increment 2): approved gallery
            // designs placed verbatim as wall-anchored set pieces.
            public readonly List<DaisShowpiece> daisShowpieces;
            // Phase 6e target-aware promontories. Canonical identity stays here;
            // renderer/abyss consumers receive only the derived cell projection.
            public readonly NamedVistaPromontoryResolution[] namedPromontories;
            // Mandatory production-facing long promontories remain canonical,
            // cardinal, and separate from the optional scenic vista promontory.
            public readonly ExternalConnectorPromontoryResolution[] externalConnectors;
            public readonly RecipeResolution[] recipeResolutions;
            // Every bare rim in the accepted plan. Recipe, external and later
            // generated topology producers publish the same absolute record;
            // renderer/navigation/validation never recover one by walking its
            // producer-specific resolution.
            public readonly PlanOpening[] openings;
            public readonly RouteRequirementResolution routeRequirementResolution;
            // Phase B: the volumetric reservations the planner enforced (design
            // §6). Carried on the plan so the acceptance gate can run the one
            // headroom rule over the very same ledger instead of reconstructing
            // deck heights from the transition list with a second copy of the
            // formula — which is how the gate and its post-hoc twin drifted.
            public readonly PrismLedger prisms;
            // Plan policy, deliberately not an unconditional report/hash field:
            // old topology files must retain byte-identical projections.
            public readonly int topologyCeilingLevels;

            public TieredLevelPlan(
                SurfaceField surfaces,
                List<ElevationEdgeModel.TransitionEdge> transitions,
                int levelCount,
                int minLevel,
                int maxLevel,
                string roomsPerTierSummary,
                int overlookCount,
                string transitionSummary,
                string stairUsageSummary,
                string topologySummary,
                string placementClassSummary,
                string stairCandidateSummary,
                string portGraphSummary,
                int connectorCandidateCount,
                string archetypeName,
                List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs,
                string synthesizedStairSummary,
                List<DaisShowpiece> daisShowpieces,
                NamedVistaPromontoryResolution[] namedPromontories,
                ExternalConnectorPromontoryResolution[] externalConnectors,
                RecipeResolution[] recipeResolutions,
                PlanOpening[] openings,
                RouteRequirementResolution routeRequirementResolution,
                PrismLedger prisms,
                int topologyCeilingLevels)
            {
                this.archetypeName = archetypeName;
                this.prisms = prisms ?? new PrismLedger();
                this.topologyCeilingLevels = topologyCeilingLevels;
                // The elevation stage's own field, carried through rather than
                // rebuilt from its heightfield — a rewrap would have dropped
                // both the stacked surfaces and the kinds it took C1b to model.
                this.surfaces = surfaces ?? new SurfaceField(new Dictionary<Vector2Int, int>());
                this.transitions = transitions;
                this.levelCount = levelCount;
                this.minLevel = minLevel;
                this.maxLevel = maxLevel;
                this.roomsPerTierSummary = roomsPerTierSummary;
                this.overlookCount = overlookCount;
                this.transitionSummary = transitionSummary;
                this.stairUsageSummary = stairUsageSummary;
                this.topologySummary = topologySummary;
                this.placementClassSummary = placementClassSummary;
                this.stairCandidateSummary = stairCandidateSummary;
                this.portGraphSummary = portGraphSummary;
                this.connectorCandidateCount = connectorCandidateCount;
                this.synthesizedStairs = synthesizedStairs;
                this.synthesizedStairSummary = synthesizedStairSummary;
                this.daisShowpieces = daisShowpieces;
                this.namedPromontories = namedPromontories ?? Array.Empty<NamedVistaPromontoryResolution>();
                this.externalConnectors = externalConnectors ?? Array.Empty<ExternalConnectorPromontoryResolution>();
                this.recipeResolutions = recipeResolutions ?? Array.Empty<RecipeResolution>();
                this.openings = openings ?? Array.Empty<PlanOpening>();
                this.routeRequirementResolution = routeRequirementResolution;
            }
        }

        private readonly struct PlanBounds
        {
            public readonly float minX;
            public readonly float maxX;
            public readonly float minZ;
            public readonly float maxZ;

            public Vector2 Min => new Vector2(minX, minZ);
            public Vector2 Max => new Vector2(maxX, maxZ);
            public Vector2 Center => new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
            public Vector2 Size => new Vector2(maxX - minX, maxZ - minZ);

            public PlanBounds(float minX, float maxX, float minZ, float maxZ)
            {
                this.minX = minX;
                this.maxX = maxX;
                this.minZ = minZ;
                this.maxZ = maxZ;
            }

            public override string ToString()
            {
                return $"X[{minX:0.###},{maxX:0.###}] Z[{minZ:0.###},{maxZ:0.###}]";
            }
        }

        private readonly struct WallEdge : IEquatable<WallEdge>
        {
            public readonly Vector2Int cell;
            public readonly int direction;

            public WallEdge(Vector2Int cell, int direction)
            {
                this.cell = cell;
                this.direction = direction;
            }

            public bool Equals(WallEdge other)
            {
                return cell == other.cell && direction == other.direction;
            }

            public override bool Equals(object obj)
            {
                return obj is WallEdge other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (cell.GetHashCode() * 397) ^ direction;
                }
            }

            public override string ToString()
            {
                return $"{cell}/{DirectionName(direction)}";
            }
        }

        private static string DirectionName(int direction)
        {
            switch (direction)
            {
                case Direction.North:
                    return "North";
                case Direction.East:
                    return "East";
                case Direction.South:
                    return "South";
                case Direction.West:
                    return "West";
                default:
                    return $"Unknown({direction})";
            }
        }

        private static Vector2 DirectionVector(int direction)
        {
            switch (direction)
            {
                case Direction.North:
                    return Vector2.up;
                case Direction.East:
                    return Vector2.right;
                case Direction.South:
                    return Vector2.down;
                case Direction.West:
                    return Vector2.left;
                default:
                    return Vector2.zero;
            }
        }

        private static Vector2Int DirectionVectorInt(int direction)
        {
            switch (direction)
            {
                case Direction.North:
                    return Vector2Int.up;
                case Direction.East:
                    return Vector2Int.right;
                case Direction.South:
                    return Vector2Int.down;
                case Direction.West:
                    return Vector2Int.left;
                default:
                    return Vector2Int.zero;
            }
        }

        private static int DirectionFromVector(Vector2 vector)
        {
            if (Mathf.Abs(vector.x) >= Mathf.Abs(vector.y))
            {
                return vector.x >= 0f ? Direction.East : Direction.West;
            }

            return vector.y >= 0f ? Direction.North : Direction.South;
        }

        private static class Direction
        {
            public const int North = 1;
            public const int East = 2;
            public const int South = 4;
            public const int West = 8;
            public const int All = North | East | South | West;
            public static readonly int[] Cardinals = { North, East, South, West };
        }

    }
}

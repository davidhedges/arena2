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
        private const string PrefabContractsPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/prefab_contracts.json";
        private const string PackageInventoryPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/package_inventory.json";
        private const string StepLibraryIndexPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/step_library_index.json";
        private const string StairProofContractsPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/stair_proof_contracts.json";
        private const string GenerationProfilePath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/generation_profile.asset";
        // Forge output (design step 6): same contract shape, separate file; entries
        // join planning only with reviewStatus "reviewed" (human review gate).
        private const string ForgedStairContractsPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/forged_stair_contracts.json";
        private const string PackageAssetRoot = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/";
        private const string GeneratedRootName = "Generated Dungeon";
        // Active floorplan tuning lives in generation_profile.asset and is
        // validated through DungeonGenerationSettings before planning.
        // One generated level is 1u of world height (the elevation quantum since the
        // stair-forge recalibration). The plan grid stays 4u cells. MaxGeneratedLevel
        // is in 1u levels; magnificence decision A (2026-06-13) raised the world
        // height cap from 10u to 24u so the 4u-major grammar can stack gold-like
        // tiers (gold level 1 spans 26u) and the dungeon net-climbs ~24u
        // (subsumes decision I).
        private const int MaxGeneratedLevel = 24;
        // Magnificence decision A: inter-room/tier elevation lands on a 4u lattice.
        // A corridor climbs one major (4u) or a steeper double-major (8u); 1u and
        // 2u are reserved for INTRA-room accents (zone seams, dais), never plain
        // corridors. The one gold-style exception: a single 2u "bridge" per dungeon
        // may join two 4u sub-lattices (gold level 1's -2->0 seam), shifting one
        // region onto the +2 coset. PrimaryStairRiseLevels (2u) keeps its physical
        // meaning — the reviewed primary straight stair and the bridge rise.
        private const int MajorRiseLevels = 4;
        private const int DoubleMajorRiseLevels = 8;
        private const int MaxTwoBridgesPerDungeon = 1;
        // The primary straight stair climbs 2u; it serves the intra-coset 2u bridge
        // (decision A) and is the edge model's reviewed primary stair. Plain
        // corridors no longer use it (they use the 4u/8u majors).
        private const int PrimaryStairRiseLevels = 2;
        // Minimum clearance in u between a walkable surface and geometry above it
        // (design decision 2): pass-unders, bridges, overhangs, forge candidates.
        private const int MinHeadroomLevels = 3;
        // Intra-room 1u splits (design step 5): a room may divide into a lower and a
        // raised (+1u) zone along one straight seam; every cell pair across the seam
        // is a rise-1 step strip, so there is no free walk across the 1u delta.
        private const int MinZoneDepthCells = 2;
        private const string SeamStairPlacementClass = "seam";
        // Dais platforms (step 9, decision 37): cosmetic interior 1u platforms
        // ringed by the same step strips as zone seams, carved after level
        // assignment from a per-room RNG. Distinct class for histograms and
        // review; behaves exactly like "seam" everywhere it is consumed.
        private const string DaisStairPlacementClass = "dais";
        private const int MaxDaisPerDungeon = 2;
        private const int MaxDaisSpanCells = 2;
        // Magnificence decision J (2026-06-15): promontory piers. A 1-cell-wide
        // walkway juts out of a grand room straight into the open void and
        // dead-ends — gold's "long pier" jutting over the chasm. The strip cells
        // join cellLevels at the room's level (walkable + reachable); decision C's
        // cliffs make every side drop to the abyss automatically, so the spur
        // rides over the void. Dense support columns are a follow-up increment.
        // 0-2 per dungeon, off large rooms only (stays a grand, rare focal piece).
        private const int MaxPromontoriesPerDungeon = 2;
        private const int InternalOpenPathMinRunCells = 3;
        private const int InternalOpenPathRailingPercent = 25;
        // Dais variants (decision 41, gallery-approved 2026-06-12): sunken
        // pits, rise-2 rims and a second tier draw from the same per-room RNG.
        private const float DaisSunkenChance = 0.25f;
        private const float DaisSteepChance = 0.25f;
        private const float DaisTieredChance = 0.3f;
        // Backed dais (decisions 44+46): a raised non-tiered dais tries a
        // wall-flush placement half the time, falling back to its interior
        // rect when no wall side is eligible. Proportions bias
        // wide-along-wall (up to 3 cells along, depth bounded as usual).
        private const float DaisBackedChance = 0.5f;
        private const int BackedDaisMaxAlongWall = 3;
        // Showpiece dais (decision 46 increment 2): both approved gallery
        // showpieces (the bay and the gold scallop) occupy a 5-cells-along x
        // 3-deep footprint against a TRUE outer wall. When a backed room
        // fits one, it always gets one; plain rects serve smaller rooms.
        private const int ShowpieceAlongCells = 5;
        private const int ShowpieceDepthCells = 3;

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
        // The 2u strip for rise-2 dais rims: same family one steepness up; the
        // edge model validates the measured rise against the transition delta.
        private const string SteepDaisStairPieceName = "P_MOD_Stairs_01_E_straight_3";
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
        private const int LevelAssignmentAttempts = 32;
        private const int LayoutRegenerationAttempts = 40;
        private const float EnclosedRoomChance = 0.5f;
        private static readonly bool ActiveStepFormationPlacementEnabled = false;
        private const string AuthoredFlatRailingModuleName = "LVL_01_O_rail_straight_S";
        private static readonly string[] DropFaceCandidateNames =
        {
            "COMP_Wall_01_O_straight_small",
            "P_MOD_Wall_01_O_straight_small",
            "P_MOD_Base_01_straight_small",
            "COMP_Wall_01_O_straight_med",
            "P_MOD_Base_01_straight_med"
        };

        private int seed;
        private bool createPlayCamera = true;
        private Vector3 origin = Vector3.zero;
        private static DungeonGenerationSettings CurrentGenerationSettings = DungeonGenerationSettings.Default;

        [MenuItem("Tools/Dungeon Lab/Generate")]
        public static void Generate()
        {
            GenerateWithSeed(CreateRandomSeed());
        }

        [MenuItem("Tools/Dungeon Lab/Open Generation Profile")]
        public static void OpenGenerationProfile()
        {
            DungeonGenerationProfile profile = AssetDatabase.LoadAssetAtPath<DungeonGenerationProfile>(GenerationProfilePath);
            if (profile == null)
            {
                string directory = Path.GetDirectoryName(GenerationProfilePath);
                if (!string.IsNullOrEmpty(directory) && !AssetDatabase.IsValidFolder(directory))
                {
                    Directory.CreateDirectory(directory);
                    AssetDatabase.Refresh();
                }

                profile = CreateInstance<DungeonGenerationProfile>();
                AssetDatabase.CreateAsset(profile, GenerationProfilePath);
                AssetDatabase.SaveAssets();
            }

            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
            EditorUtility.FocusProjectWindow();
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
            var generator = ScriptableObject.CreateInstance<DungeonLabGenerator>();
            try
            {
                generator.seed = seed;
                generator.createPlayCamera = false;
                generator.origin = Vector3.zero;
                CurrentGenerationSettings = LoadActiveGenerationSettings();
                generator.GenerateRandomDungeonLayout(new System.Random(seed));
            }
            finally
            {
                DestroyImmediate(generator);
            }
        }

        private static DungeonGenerationSettings LoadActiveGenerationSettings()
        {
            DungeonGenerationProfile profile = AssetDatabase.LoadAssetAtPath<DungeonGenerationProfile>(GenerationProfilePath);
            return profile != null ? profile.ToSettings() : DungeonGenerationSettings.Default;
        }

        private void GenerateRandomDungeonLayout(System.Random random)
        {
            var rejectionHistogram = new Dictionary<string, int>();
            if (!TryBuildAcceptedPlan(
                    seed,
                    random,
                    rejectionHistogram,
                    out DungeonLayout layout,
                    out TieredLevelPlan levelPlan,
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
            if (!TryBuildRoomBoundaryContext(
                    layout,
                    levelPlan.cellLevels,
                    levelPlan.transitions,
                    random,
                    out ElevationEdgeModel.RoomBoundaryContext roomBoundaryContext,
                    out string roomBoundaryError))
            {
                Debug.LogError($"Dungeon Lab: rejected enclosed room edge treatment. {roomBoundaryError}");
                return;
            }

            GameObject root;
            ElevationEdgeModel.BuildReport report;
            Bounds bounds;
            try
            {
                root = ElevationEdgeModel.BuildLevelField(
                    levelFieldOrigin,
                    levelPlan.cellLevels,
                    levelPlan.transitions,
                    null,
                    null,
                    roomBoundaryContext,
                    levelPlan.promontoryCells,
                    GeneratedRootName,
                    out report,
                    out bounds);
            }
            catch (Exception exception)
            {
                Debug.LogError($"Dungeon Lab: shared elevation edge model failed. {exception.Message}");
                return;
            }

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

            string stepFormationSummary = "PARKED (structure-first)";
            if (ActiveStepFormationPlacementEnabled)
            {
                if (!TryPlaceActiveStepFormation(
                        root.transform,
                        layout,
                        levelPlan,
                        levelFieldOrigin,
                        report.levelHeight,
                        ref bounds,
                        out string stepFormationName,
                        out string stepFormationKind,
                        out string stepFormationMode,
                        out string stepFormationError))
                {
                    Debug.LogError($"Dungeon Lab: rejected active step formation placement. {stepFormationError}");
                    return;
                }

                stepFormationSummary = $"{stepFormationName}, kind {stepFormationKind}, placementMode {stepFormationMode}";
            }
            else
            {
                Debug.Log("Dungeon Lab: step formations: PARKED (structure-first).");
            }

            if (createPlayCamera)
            {
                EnsurePlayCamera(bounds, 4f);
            }

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            float floorFillPercent = CalculateFloorFillPercent(layout.floorCells);
            int loopEdges = CountLoopEdges(layout);
            Debug.Log(
                $"Dungeon Lab: random dungeon seed {seed}, profile {CurrentGenerationSettings.profileName}, archetype {levelPlan.archetypeName}, cells {layout.floorCells.Count}, rooms {layout.rooms.Count}, largest_room {largestRoom.Area}c_{largestRoom.bounds.width}x{largestRoom.bounds.height}p{largestRoom.parts.Count}, " +
                $"connections {layout.connections.Count}, loop edges {loopEdges} (=C-(R-1)), floor-fill {floorFillPercent * 100f:0.#}%, " +
                $"connector candidates from tag = {levelPlan.connectorCandidateCount}, " +
                $"stair usage {levelPlan.stairUsageSummary}, " +
                $"tiers {levelPlan.minLevel}..{levelPlan.maxLevel}, rooms per tier {levelPlan.roomsPerTierSummary}, overlooks {levelPlan.overlookCount} (spatial delta>=2), all reachable, " +
                $"transitions: {levelPlan.transitionSummary}, portGraph {levelPlan.portGraphSummary}, step formations: {stepFormationSummary}; edgeModel {report.Summary} | REJECTED {report.rejected}, OVERLAP 0.");
            Debug.Log(
                "Dungeon Lab GENERATION_SUMMARY " +
                $"profile={CurrentGenerationSettings.profileName}; " +
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

        private static bool TryPlaceActiveStepFormation(
            Transform root,
            DungeonLayout layout,
            TieredLevelPlan levelPlan,
            Vector3 levelFieldOrigin,
            float levelHeight,
            ref Bounds bounds,
            out string stepFormationName,
            out string stepFormationKind,
            out string stepFormationMode,
            out string error)
        {
            stepFormationName = "none";
            stepFormationKind = "none";
            stepFormationMode = "none";
            error = string.Empty;

            if (!TryChooseActiveStepFormations(
                    layout,
                    levelPlan,
                    levelFieldOrigin,
                    levelHeight,
                    out List<ActiveStepFormationPlacement> placements,
                    out error))
            {
                return false;
            }

            var stepRoot = CreateChild(root, "Step Formation");
            var placedNames = new List<string>();
            var placedKinds = new List<string>();
            var placedModes = new List<string>();
            foreach (ActiveStepFormationPlacement placement in placements)
            {
                string instanceName = $"stepFormation_{placement.record.name}";
                GameObject instance = InstantiatePrefab(
                    placement.record.path,
                    instanceName,
                    stepRoot.transform,
                    placement.pivotPosition,
                    placement.yRotation);

                if (!TryGetRendererOrColliderWorldBounds(instance, out Bounds placedBounds))
                {
                    Undo.DestroyObjectImmediate(stepRoot);
                    error = $"Step formation '{placement.record.name}' has no renderer or collider bounds.";
                    return false;
                }

                if (!ValidateActiveStepFormationInstance(instance, placedBounds, placement, out List<Vector2Int> footprintCells, out error))
                {
                    Undo.DestroyObjectImmediate(stepRoot);
                    return false;
                }

                if (StepFormationClearsFlatFloor(placement.record) &&
                    !RemoveFlatFloorsForStepFormation(root, levelPlan.cellLevels, footprintCells, out error))
                {
                    Undo.DestroyObjectImmediate(stepRoot);
                    return false;
                }

                bounds.Encapsulate(placedBounds);
                string kind = StepFormationKind(placement.record);
                int clearedFootprintCells = StepFormationClearsFlatFloor(placement.record) ? footprintCells.Count : 0;
                placedNames.Add(placement.record.name);
                placedKinds.Add(kind);
                placedModes.Add($"{placement.record.name}:{placement.placementMode}");
                Debug.Log(
                    $"Dungeon Lab: placed step formation {placement.record.name} in room {placement.room.width}x{placement.room.height} at level {placement.roomLevel}; " +
                    $"kind {kind}, authored placementMode {placement.placementMode}, yaw {placement.yRotation:0.###}, clearedFootprintCells {clearedFootprintCells}, " +
                    $"footprint {placedBounds.size.x / 4f:0.###}x{placedBounds.size.z / 4f:0.###} cells, connectionPlaneY {GetWorldConnectionPlaneY(placement.record, placedBounds):0.###}, floorY {placement.floorY:0.###}.");
            }

            stepFormationName = string.Join(",", placedNames);
            stepFormationKind = string.Join(",", placedKinds);
            stepFormationMode = string.Join(",", placedModes);
            return true;
        }

        private static bool TryChooseActiveStepFormations(
            DungeonLayout layout,
            TieredLevelPlan levelPlan,
            Vector3 levelFieldOrigin,
            float levelHeight,
            out List<ActiveStepFormationPlacement> selected,
            out string error)
        {
            selected = new List<ActiveStepFormationPlacement>();
            error = string.Empty;

            StepLibraryIndex index = LoadStepLibraryIndex();
            if (index == null || index.records == null || index.records.Length == 0)
            {
                error = $"{StepLibraryIndexPath} has no records.";
                return false;
            }

            List<RoomFootprint> rooms = new List<RoomFootprint>(layout.rooms);
            rooms.Sort((left, right) => right.Area.CompareTo(left.Area));

            List<PlanBounds> blockedBounds = BuildActiveStepBlockedBounds(levelPlan.cellLevels, levelPlan.transitions, levelFieldOrigin);
            var skippedConnective = new HashSet<string>();
            var skippedInvalidCoverage = new HashSet<string>();
            int skippedInvalid = 0;
            var loggedDiscoveredBackSides = new HashSet<string>();

            foreach (RoomFootprint room in rooms)
            {
                if (!TryGetUniformRoomLevel(room, levelPlan.cellLevels, out int roomLevel))
                {
                    continue;
                }

                if (TryChooseActiveStepFormationForRoom(
                        index.records,
                        room,
                        roomLevel,
                        levelFieldOrigin,
                        levelHeight,
                        blockedBounds,
                        skippedConnective,
                        skippedInvalidCoverage,
                        loggedDiscoveredBackSides,
                        ref skippedInvalid,
                        out ActiveStepFormationPlacement placement))
                {
                    selected.Add(placement);
                    blockedBounds.Add(placement.expectedFootprint);
                }
            }

            if (selected.Count == 0)
            {
                error = skippedInvalid > 0
                    ? $"no usable StepFormations records were available; {skippedInvalid} records need {StepLibraryIndexPath} regeneration by a reviewed measurement/contract tool"
                    : "no authored StepFormations fit any uniform-level room";
                return false;
            }

            if (skippedConnective.Count > 0)
            {
                var names = new List<string>(skippedConnective);
                names.Sort(StringComparer.Ordinal);
                Debug.Log($"Dungeon Lab: skipped room_entrance step formations for deferred doorway placement: {string.Join(", ", names)}.");
            }

            if (skippedInvalidCoverage.Count > 0)
            {
                var names = new List<string>(skippedInvalidCoverage);
                names.Sort(StringComparer.Ordinal);
                Debug.LogError($"Dungeon Lab: skipped sunken interior step formations with invalid coverageMask: {string.Join(", ", names)}.");
            }

            return true;
        }

        private static bool TryChooseActiveStepFormationForRoom(
            StepLibraryRecord[] records,
            RoomFootprint room,
            int roomLevel,
            Vector3 levelFieldOrigin,
            float levelHeight,
            List<PlanBounds> blockedBounds,
            HashSet<string> skippedConnective,
            HashSet<string> skippedInvalidCoverage,
            HashSet<string> loggedDiscoveredBackSides,
            ref int skippedInvalid,
            out ActiveStepFormationPlacement placement)
        {
            placement = default;

            // B.6: each rect part of the footprint is searched as a host area
            // (wall_abutting on every part before any interior fallback, the
            // original mode priority). Wall-abutting placement additionally
            // requires the part side to lie on the room's true outline.
            foreach (string placementMode in new[] { "wall_abutting", "interior" })
            {
                foreach (RectInt part in room.parts)
                {
                    PlanBounds partBounds = RectToLevelFieldPlanBounds(levelFieldOrigin, part);
                    PlanBounds clearanceBounds = InsetPlanBounds(partBounds, 4f);
                    if (clearanceBounds.Size.x <= 0f || clearanceBounds.Size.y <= 0f)
                    {
                        continue;
                    }

                    if (TryChooseActiveStepFormationForRoomMode(
                            records,
                            room,
                            part,
                            roomLevel,
                            levelFieldOrigin,
                            levelHeight,
                            partBounds,
                            clearanceBounds,
                            blockedBounds,
                            placementMode,
                            skippedConnective,
                            skippedInvalidCoverage,
                            loggedDiscoveredBackSides,
                            ref skippedInvalid,
                            out placement))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryChooseActiveStepFormationForRoomMode(
            StepLibraryRecord[] records,
            RoomFootprint room,
            RectInt part,
            int roomLevel,
            Vector3 levelFieldOrigin,
            float levelHeight,
            PlanBounds roomBounds,
            PlanBounds clearanceBounds,
            List<PlanBounds> blockedBounds,
            string placementMode,
            HashSet<string> skippedConnective,
            HashSet<string> skippedInvalidCoverage,
            HashSet<string> loggedDiscoveredBackSides,
            ref int skippedInvalid,
            out ActiveStepFormationPlacement selected)
        {
            selected = default;
            float bestScore = float.NegativeInfinity;

            foreach (StepLibraryRecord record in records)
            {
                if (!IsFlatStepFormationRecord(record))
                {
                    continue;
                }

                if (!StepMeasurementRecordIsValid(record, out string recordError))
                {
                    skippedInvalid++;
                    Debug.LogWarning($"Dungeon Lab: skipped step formation '{record.name}'; {recordError}.");
                    continue;
                }

                if (record.placementMode == "room_entrance")
                {
                    skippedConnective.Add(record.name);
                    continue;
                }

                if (record.placementMode != placementMode)
                {
                    continue;
                }

                if (StepFormationClearsFlatFloor(record) &&
                    !StepFormationCoverageMaskIsValid(record, out string coverageError))
                {
                    skippedInvalidCoverage.Add($"{record.name} ({coverageError})");
                    Debug.LogError($"Dungeon Lab: skipped sunken step formation '{record.name}'; {coverageError}.");
                    continue;
                }

                foreach (float yRotation in StepFormationRotations(record))
                {
                    if (!TryBuildActiveStepFormationFootprint(
                            record,
                            room,
                            part,
                            roomBounds,
                            clearanceBounds,
                            yRotation,
                            loggedDiscoveredBackSides,
                            out PlanBounds footprint))
                    {
                        continue;
                    }

                    if (IntersectsAnyPlanBounds(footprint, blockedBounds, 0.05f))
                    {
                        continue;
                    }

                    float area = footprint.Size.x * footprint.Size.y;
                    float confidenceBonus = record.placementModeConfidence == "high" ? 100000000f : 0f;
                    float sunkenBonus = placementMode == "interior" && StepFormationClearsFlatFloor(record) ? 1000000f : 0f;
                    float score = area + confidenceBonus + sunkenBonus;
                    if (score <= bestScore)
                    {
                        continue;
                    }

                    float floorY = levelFieldOrigin.y + roomLevel * levelHeight;
                    Vector3 pivotPosition = CalculateStepFormationPivotPosition(record, footprint, floorY, yRotation);
                    selected = new ActiveStepFormationPlacement(
                        part,
                        roomLevel,
                        record,
                        roomBounds,
                        clearanceBounds,
                        footprint,
                        pivotPosition,
                        floorY,
                        yRotation,
                        record.placementMode);
                    bestScore = score;
                }
            }

            return bestScore > float.NegativeInfinity;
        }

        private static bool TryBuildActiveStepFormationFootprint(
            StepLibraryRecord record,
            RoomFootprint room,
            RectInt part,
            PlanBounds roomBounds,
            PlanBounds clearanceBounds,
            float yRotation,
            HashSet<string> loggedDiscoveredBackSides,
            out PlanBounds footprint)
        {
            Vector2 rotatedSize = RotatedStepFormationSize(record, yRotation);
            if (record.placementMode == "wall_abutting")
            {
                if (!TryResolveBackSide(record, loggedDiscoveredBackSides, out int localBackSide))
                {
                    footprint = default;
                    return false;
                }

                int worldBackSide = DirectionFromVector(Rotate2D(DirectionVector(localBackSide), yRotation));
                // B.6: the part edge backing the formation must be the room's
                // true outline — the side of a wing facing the dominant rect
                // is open floor, not a wall.
                if (!PartSideOnRoomBoundary(room, part, worldBackSide))
                {
                    footprint = default;
                    return false;
                }

                footprint = BuildWallAbuttingFootprint(roomBounds, rotatedSize, worldBackSide);
                return WallAbuttingFootprintHasClearance(roomBounds, footprint, worldBackSide);
            }

            if (record.placementMode == "interior")
            {
                if (rotatedSize.x > clearanceBounds.Size.x || rotatedSize.y > clearanceBounds.Size.y)
                {
                    footprint = default;
                    return false;
                }

                Vector2 center = clearanceBounds.Center;
                footprint = new PlanBounds(
                    center.x - rotatedSize.x * 0.5f,
                    center.x + rotatedSize.x * 0.5f,
                    center.y - rotatedSize.y * 0.5f,
                    center.y + rotatedSize.y * 0.5f);
                return PlanBoundsContains(roomBounds, footprint, 0.05f) &&
                    PlanBoundsContains(clearanceBounds, footprint, 0.05f);
            }

            footprint = default;
            return false;
        }

        private static bool PartSideOnRoomBoundary(RoomFootprint room, RectInt part, int side)
        {
            Vector2Int direction =
                side == Direction.North ? new Vector2Int(0, 1)
                : side == Direction.East ? new Vector2Int(1, 0)
                : side == Direction.South ? new Vector2Int(0, -1)
                : new Vector2Int(-1, 0);
            bool alongX = direction.x == 0;
            int line = direction.y > 0 ? part.yMax - 1
                : direction.y < 0 ? part.yMin
                : direction.x > 0 ? part.xMax - 1
                : part.xMin;
            int transverseMin = alongX ? part.xMin : part.yMin;
            int transverseMax = alongX ? part.xMax : part.yMax;
            for (int t = transverseMin; t < transverseMax; t++)
            {
                Vector2Int cell = alongX ? new Vector2Int(t, line) : new Vector2Int(line, t);
                if (room.Contains(cell + direction))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<PlanBounds> BuildActiveStepBlockedBounds(
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            Vector3 origin)
        {
            var blocked = new List<PlanBounds>();
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                if (levels.ContainsKey(transition.firstCell))
                {
                    blocked.Add(CellToLevelFieldPlanBounds(origin, transition.firstCell));
                }

                if (levels.ContainsKey(transition.secondCell))
                {
                    blocked.Add(CellToLevelFieldPlanBounds(origin, transition.secondCell));
                }
            }

            return blocked;
        }

        private static bool TryGetUniformRoomLevel(
            RoomFootprint room,
            IReadOnlyDictionary<Vector2Int, int> levels,
            out int roomLevel)
        {
            roomLevel = 0;
            bool initialized = false;
            foreach (Vector2Int cell in room.CellsRowMajor())
            {
                if (!levels.TryGetValue(cell, out int level))
                {
                    continue;
                }

                if (!initialized)
                {
                    roomLevel = level;
                    initialized = true;
                    continue;
                }

                if (level != roomLevel)
                {
                    return false;
                }
            }

            return initialized;
        }

        private static bool StepMeasurementRecordIsValid(StepLibraryRecord record, out string error)
        {
            if (string.IsNullOrWhiteSpace(record.connectionPlane))
            {
                error = "missing connectionPlane data; rebuild a reviewed step-library contract index";
                return false;
            }

            if (record.connectionPlaneY < record.boundsMin.y - 0.08f ||
                record.connectionPlaneY > record.boundsMax.y + 0.08f)
            {
                error = $"connection plane {record.connectionPlaneY:0.###} is outside indexed bounds {record.boundsMin.y:0.###}..{record.boundsMax.y:0.###}";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool StepFormationCoverageMaskIsValid(StepLibraryRecord record, out string error)
        {
            error = string.Empty;
            if (record.coverageCells == null ||
                record.coverageCellGrid.x <= 0 ||
                record.coverageCellGrid.z <= 0 ||
                record.coverageCellCount <= 0)
            {
                error = "coverageMask is empty; rerebuild a reviewed step-library contract index before placing sunken interiors";
                return false;
            }

            int fullCellCount = record.coverageCellGrid.x * record.coverageCellGrid.z;
            if (record.coverageCellCount >= fullCellCount)
            {
                error = $"coverageMask is full ({record.coverageMask}); sunken octagonal interiors must be partial";
                return false;
            }

            if (record.coverageCellGrid.x >= 2 &&
                record.coverageCellGrid.z >= 2 &&
                CoverageContainsCell(record, 0, 0) &&
                CoverageContainsCell(record, record.coverageCellGrid.x - 1, 0) &&
                CoverageContainsCell(record, 0, record.coverageCellGrid.z - 1) &&
                CoverageContainsCell(record, record.coverageCellGrid.x - 1, record.coverageCellGrid.z - 1))
            {
                error = $"coverageMask covers all footprint corners ({record.coverageMask}); sunken octagonal interiors must leave corner cells capped";
                return false;
            }

            return true;
        }

        private static bool CoverageContainsCell(StepLibraryRecord record, int x, int z)
        {
            if (record.coverageCells == null)
            {
                return false;
            }

            foreach (OccupiedCellRecord cell in record.coverageCells)
            {
                if (cell.x == x && cell.z == z)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveBackSide(
            StepLibraryRecord record,
            HashSet<string> loggedDiscoveredBackSides,
            out int backSide)
        {
            if (TryParseDirectionName(record.authoredBackSide, out backSide) &&
                record.authoredBackSide != "discover")
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(record.authoredBackSide) &&
                record.authoredBackSide != "discover")
            {
                Debug.LogError($"Dungeon Lab: wall_abutting step formation '{record.name}' has invalid authored backSide '{record.authoredBackSide}'.");
                return false;
            }

            if (!TryFindMinimumSideHeight(record.sideHeights, out int frontSide, out float frontHeight))
            {
                Debug.LogError($"Dungeon Lab: wall_abutting step formation '{record.name}' has backSide=discover but no sideHeights.");
                backSide = 0;
                return false;
            }

            backSide = OppositeDirection(frontSide);
            if (loggedDiscoveredBackSides.Add(record.name))
            {
                Debug.Log(
                    $"Dungeon Lab: derived wall_abutting backSide for {record.name}: front {DirectionName(frontSide)} min sideHeight {frontHeight:0.###} -> back {DirectionName(backSide)}.");
            }

            return true;
        }

        private static bool TryFindMinimumSideHeight(
            PerimeterSideHeightRecord[] sideHeights,
            out int side,
            out float height)
        {
            side = 0;
            height = float.PositiveInfinity;
            if (sideHeights == null || sideHeights.Length == 0)
            {
                return false;
            }

            bool found = false;
            foreach (PerimeterSideHeightRecord sideHeight in sideHeights)
            {
                if (!TryParseDirectionName(sideHeight.side, out int candidateSide))
                {
                    continue;
                }

                if (sideHeight.maxVerticalFaceHeight >= height)
                {
                    continue;
                }

                side = candidateSide;
                height = sideHeight.maxVerticalFaceHeight;
                found = true;
            }

            return found;
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

        private static PlanBounds BuildWallAbuttingFootprint(PlanBounds roomBounds, Vector2 size, int wallSide)
        {
            Vector2 center = roomBounds.Center;
            switch (wallSide)
            {
                case Direction.North:
                    return new PlanBounds(
                        center.x - size.x * 0.5f,
                        center.x + size.x * 0.5f,
                        roomBounds.maxZ - size.y,
                        roomBounds.maxZ);
                case Direction.East:
                    return new PlanBounds(
                        roomBounds.maxX - size.x,
                        roomBounds.maxX,
                        center.y - size.y * 0.5f,
                        center.y + size.y * 0.5f);
                case Direction.South:
                    return new PlanBounds(
                        center.x - size.x * 0.5f,
                        center.x + size.x * 0.5f,
                        roomBounds.minZ,
                        roomBounds.minZ + size.y);
                case Direction.West:
                    return new PlanBounds(
                        roomBounds.minX,
                        roomBounds.minX + size.x,
                        center.y - size.y * 0.5f,
                        center.y + size.y * 0.5f);
                default:
                    return roomBounds;
            }
        }

        private static bool WallAbuttingFootprintHasClearance(PlanBounds roomBounds, PlanBounds footprint, int wallSide)
        {
            const float clearance = 4f;
            if (!PlanBoundsContains(roomBounds, footprint, 0.05f))
            {
                return false;
            }

            switch (wallSide)
            {
                case Direction.North:
                    return footprint.minX >= roomBounds.minX + clearance &&
                        footprint.maxX <= roomBounds.maxX - clearance &&
                        footprint.minZ >= roomBounds.minZ + clearance;
                case Direction.East:
                    return footprint.minZ >= roomBounds.minZ + clearance &&
                        footprint.maxZ <= roomBounds.maxZ - clearance &&
                        footprint.minX >= roomBounds.minX + clearance;
                case Direction.South:
                    return footprint.minX >= roomBounds.minX + clearance &&
                        footprint.maxX <= roomBounds.maxX - clearance &&
                        footprint.maxZ <= roomBounds.maxZ - clearance;
                case Direction.West:
                    return footprint.minZ >= roomBounds.minZ + clearance &&
                        footprint.maxZ <= roomBounds.maxZ - clearance &&
                        footprint.maxX <= roomBounds.maxX - clearance;
                default:
                    return false;
            }
        }

        private static Vector3 CalculateStepFormationPivotPosition(
            StepLibraryRecord record,
            PlanBounds footprint,
            float floorY,
            float yRotation)
        {
            PlanBounds rotatedLocalBounds = RotatedLocalStepPlanBounds(record, yRotation);
            return new Vector3(
                footprint.minX - rotatedLocalBounds.minX,
                floorY - record.connectionPlaneY,
                footprint.minZ - rotatedLocalBounds.minZ);
        }

        private static PlanBounds RotatedLocalStepPlanBounds(StepLibraryRecord record, float yRotation)
        {
            Quaternion rotation = Quaternion.Euler(0f, yRotation, 0f);
            Vector3[] corners =
            {
                new Vector3(record.originOffset.x, 0f, record.originOffset.z),
                new Vector3(record.boundsMax.x, 0f, record.originOffset.z),
                new Vector3(record.boundsMax.x, 0f, record.boundsMax.z),
                new Vector3(record.originOffset.x, 0f, record.boundsMax.z)
            };

            bool initialized = false;
            PlanBounds bounds = default;
            foreach (Vector3 corner in corners)
            {
                Vector3 rotated = rotation * corner;
                EncapsulatePlanBounds(ref bounds, ref initialized, rotated);
            }

            return bounds;
        }

        private static bool ValidateActiveStepFormationInstance(
            GameObject instance,
            Bounds placedBounds,
            ActiveStepFormationPlacement placement,
            out List<Vector2Int> footprintCells,
            out string error)
        {
            footprintCells = null;
            error = string.Empty;

            if (!StepMeasurementMatchesIndex(placement.record, placedBounds, compareOriginOffset: false, yRotation: placement.yRotation, out string measurementError))
            {
                error = $"Step formation '{placement.record.name}' live measurement disagrees with {StepLibraryIndexPath}. {measurementError}";
                return false;
            }

            const float tolerance = 0.08f;
            float connectionPlaneY = GetWorldConnectionPlaneY(placement.record, placedBounds);
            if (Mathf.Abs(connectionPlaneY - placement.floorY) > tolerance)
            {
                error = $"Step formation '{placement.record.name}' connection plane is not flush with room floor. Expected Y {placement.floorY:0.###}, measured {connectionPlaneY:0.###}.";
                return false;
            }

            PlanBounds measuredFootprint = PlanBoundsFromWorldBounds(placedBounds);
            if (!PlanBoundsContains(placement.roomBounds, measuredFootprint, tolerance))
            {
                error = $"Step formation '{placement.record.name}' footprint is outside the room. Room {placement.roomBounds}, measured {measuredFootprint}.";
                return false;
            }

            if (placement.placementMode == "interior" &&
                !PlanBoundsContains(placement.requiredClearanceBounds, measuredFootprint, tolerance))
            {
                error = $"Step formation '{placement.record.name}' lacks one-cell wall clearance. Required inside {placement.requiredClearanceBounds}, measured {measuredFootprint}.";
                return false;
            }

            if (!PlanBoundsContains(placement.expectedFootprint, measuredFootprint, tolerance) ||
                !PlanBoundsContains(measuredFootprint, placement.expectedFootprint, tolerance))
            {
                error = $"Step formation '{placement.record.name}' measured footprint does not match the planned footprint. Planned {placement.expectedFootprint}, measured {measuredFootprint}.";
                return false;
            }

            footprintCells = BuildActiveStepFormationFootprintCells(placement);
            if (footprintCells.Count == 0)
            {
                error = $"Step formation '{placement.record.name}' planned footprint did not map to any room floor cells.";
                return false;
            }

            return true;
        }

        private static List<Vector2Int> BuildActiveStepFormationFootprintCells(ActiveStepFormationPlacement placement)
        {
            if (StepFormationClearsFlatFloor(placement.record))
            {
                return BuildActiveStepFormationCoverageCells(placement);
            }

            var cells = new List<Vector2Int>();
            for (int z = placement.room.yMin; z < placement.room.yMax; z++)
            {
                for (int x = placement.room.xMin; x < placement.room.xMax; x++)
                {
                    var cell = new Vector2Int(x, z);
                    if (PlanBoundsIntersect(placement.expectedFootprint, CellToLevelFieldPlanBoundsFromRoom(placement, cell), 0.02f))
                    {
                        cells.Add(cell);
                    }
                }
            }

            return cells;
        }

        private static List<Vector2Int> BuildActiveStepFormationCoverageCells(ActiveStepFormationPlacement placement)
        {
            var cells = new HashSet<Vector2Int>();
            var coverageBounds = new List<PlanBounds>();
            foreach (OccupiedCellRecord coverageCell in placement.record.coverageCells)
            {
                coverageBounds.Add(BuildRotatedCoverageCellBounds(placement, coverageCell));
            }

            for (int z = placement.room.yMin; z < placement.room.yMax; z++)
            {
                for (int x = placement.room.xMin; x < placement.room.xMax; x++)
                {
                    var cell = new Vector2Int(x, z);
                    PlanBounds cellBounds = CellToLevelFieldPlanBoundsFromRoom(placement, cell);
                    foreach (PlanBounds coverage in coverageBounds)
                    {
                        if (!PlanBoundsIntersect(coverage, cellBounds, 0.02f))
                        {
                            continue;
                        }

                        cells.Add(cell);
                        break;
                    }
                }
            }

            return new List<Vector2Int>(cells);
        }

        private static PlanBounds BuildRotatedCoverageCellBounds(ActiveStepFormationPlacement placement, OccupiedCellRecord coverageCell)
        {
            StepLibraryRecord record = placement.record;
            float minX = record.boundsMin.x + coverageCell.x * 4f;
            float minZ = record.boundsMin.z + coverageCell.z * 4f;
            float maxX = Mathf.Min(minX + 4f, record.boundsMax.x);
            float maxZ = Mathf.Min(minZ + 4f, record.boundsMax.z);
            Quaternion rotation = Quaternion.Euler(0f, placement.yRotation, 0f);
            Vector3[] corners =
            {
                new Vector3(minX, 0f, minZ),
                new Vector3(maxX, 0f, minZ),
                new Vector3(maxX, 0f, maxZ),
                new Vector3(minX, 0f, maxZ)
            };

            bool initialized = false;
            PlanBounds bounds = default;
            foreach (Vector3 corner in corners)
            {
                Vector3 world = placement.pivotPosition + rotation * corner;
                EncapsulatePlanBounds(ref bounds, ref initialized, world);
            }

            return bounds;
        }

        private static PlanBounds CellToLevelFieldPlanBoundsFromRoom(ActiveStepFormationPlacement placement, Vector2Int cell)
        {
            float roomMinX = placement.roomBounds.minX;
            float roomMinZ = placement.roomBounds.minZ;
            float minX = roomMinX + (cell.x - placement.room.xMin) * 4f;
            float minZ = roomMinZ + (cell.y - placement.room.yMin) * 4f;
            return new PlanBounds(minX, minX + 4f, minZ, minZ + 4f);
        }

        private static bool RemoveFlatFloorsForStepFormation(
            Transform root,
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<Vector2Int> footprintCells,
            out string error)
        {
            error = string.Empty;
            Transform floorsRoot = root.Find("Floors");
            if (floorsRoot == null)
            {
                error = "Generated root has no Floors child.";
                return false;
            }

            foreach (Vector2Int cell in footprintCells)
            {
                if (!levels.TryGetValue(cell, out int level))
                {
                    error = $"Step footprint cell {cell} has no room level.";
                    return false;
                }

                string floorName = $"floor_{cell.x}_{cell.y}_level_{level}";
                Transform floor = floorsRoot.Find(floorName);
                if (floor == null)
                {
                    error = $"Could not find flat floor '{floorName}' to exclude under the step formation.";
                    return false;
                }

                Undo.DestroyObjectImmediate(floor.gameObject);
            }

            return true;
        }

        private static PlanBounds RectToLevelFieldPlanBounds(Vector3 origin, RectInt rect)
        {
            return new PlanBounds(
                origin.x + rect.xMin * 4f,
                origin.x + rect.xMax * 4f,
                origin.z + rect.yMin * 4f,
                origin.z + rect.yMax * 4f);
        }

        private static PlanBounds CellToLevelFieldPlanBounds(Vector3 origin, Vector2Int cell)
        {
            float minX = origin.x + cell.x * 4f;
            float minZ = origin.z + cell.y * 4f;
            return new PlanBounds(minX, minX + 4f, minZ, minZ + 4f);
        }

        private static int CreateRandomSeed()
        {
            return Guid.NewGuid().GetHashCode();
        }

        private static GameObject CreateChild(Transform parent, string name)
        {
            var child = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(child, $"Create {name}");
            child.transform.SetParent(parent, false);
            return child;
        }

        private static DungeonLayout BuildRandomDungeonLayoutData(System.Random random)
        {
            DungeonGenerationSettings settings = CurrentGenerationSettings.Validated();
            int width = random.Next(settings.mapWidthMinCells, settings.mapWidthMaxCells + 1);
            int depth = random.Next(settings.mapDepthMinCells, settings.mapDepthMaxCells + 1);
            StepFormationModeTable connectorTable = LoadAuthoredStairConnectorTableForGeneration();
            int connectorCandidateCount = connectorTable != null ? CountConfiguredStairConnectorPrefabs(connectorTable) : 0;

            HashSet<Vector2Int> floorCells = BuildDungeonFloorMask(
                width,
                depth,
                random,
                settings,
                out List<RoomFootprint> rooms,
                out List<RoomConnection> connections);

            if (!IsConnected(floorCells))
            {
                LogPlanningWarning("Dungeon Lab: generated disconnected mask; falling back to a connected chamber.");
                floorCells = BuildRandomRoomShape(random);
                rooms = new List<RoomFootprint> { RoomFootprint.FromRect(GetCellRect(floorCells)) };
                connections = new List<RoomConnection>();
            }

            Dictionary<int, HashSet<Vector2Int>> roomThresholds = BuildRoomThresholdCells(rooms, connections);
            List<RoomZonePlan> roomZones = ChooseRoomZoneSplits(rooms, roomThresholds, random, settings);
            return new DungeonLayout(floorCells, rooms, connections, roomZones, connectorCandidateCount);
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

        // Profile-driven floorplan: one hall placed first, then room bands
        // corridor-connected to their nearest placed room. The renderer still
        // consumes only the accepted plan; it does not repair placement.
        private static HashSet<Vector2Int> BuildDungeonFloorMask(
            int width,
            int depth,
            System.Random random,
            DungeonGenerationSettings settings,
            out List<RoomFootprint> rooms,
            out List<RoomConnection> connections)
        {
            var floorCells = new HashSet<Vector2Int>();
            rooms = new List<RoomFootprint>();
            connections = new List<RoomConnection>();

            RoomFootprint hall = null;
            for (int attempt = 0; attempt < 80 && hall == null; attempt++)
            {
                List<RectInt> shape = RollRoomShapeParts(
                    random,
                    settings.hallMinAreaCells,
                    settings.hallMaxAreaCells,
                    settings.nonRectChanceGrand,
                    settings);
                if (TryPlaceRoomShape(shape, width, depth, floorCells, random, out RoomFootprint candidate))
                {
                    hall = candidate;
                }
            }

            if (hall == null)
            {
                // An empty grid of >= 24 cells always fits a 6x7 hall; this is
                // unreachable, but the mask must never be hall-less.
                hall = RoomFootprint.FromRect(new RectInt(width / 2 - 3, depth / 2 - 3, 6, 7));
            }

            rooms.Add(hall);
            floorCells.UnionWith(hall.cells);

            PlaceRoomBand(
                settings.largeRoomMinCount,
                random.Next(settings.largeRoomMinCount, settings.largeRoomMaxCount + 1),
                settings.largeRoomMinAreaCells, settings.largeRoomMaxAreaCells, settings.nonRectChanceGrand,
                width, depth, floorCells, rooms, connections, random, settings);
            PlaceRoomBand(
                settings.midRoomMinCount,
                random.Next(settings.midRoomMinCount, settings.midRoomMaxCount + 1),
                settings.midRoomMinAreaCells, settings.midRoomMaxAreaCells, settings.nonRectChanceMid,
                width, depth, floorCells, rooms, connections, random, settings);
            PlaceRoomBand(
                settings.smallRoomMinCount,
                random.Next(settings.smallRoomMinCount, settings.smallRoomMaxCount + 1),
                settings.smallRoomMinAreaCells, settings.smallRoomMaxAreaCells, 0f,
                width, depth, floorCells, rooms, connections, random, settings);

            return floorCells;
        }

        private static void PlaceRoomBand(
            int minCount,
            int count,
            int minArea,
            int maxArea,
            float nonRectChance,
            int width,
            int depth,
            HashSet<Vector2Int> floorCells,
            List<RoomFootprint> rooms,
            List<RoomConnection> connections,
            System.Random random,
            DungeonGenerationSettings settings)
        {
            for (int roomIndex = 0; roomIndex < count; roomIndex++)
            {
                if (roomIndex >= minCount && floorCells.Count >= settings.floorBudgetCells)
                {
                    break;
                }
                for (int attempt = 0; attempt < 160; attempt++)
                {
                    List<RectInt> shape = RollRoomShapeParts(random, minArea, maxArea, nonRectChance, settings);
                    if (!TryPlaceRoomShape(shape, width, depth, floorCells, random, out RoomFootprint candidate))
                    {
                        continue;
                    }

                    int fromRoom = FindNearestRoomIndex(rooms, candidate);
                    int toRoom = rooms.Count;
                    List<Vector2Int> path = BuildCorridorPath(rooms[fromRoom].Center, candidate.Center, random);
                    if (!ValidatePathCardinality(path, out _) ||
                        PathTouchesExistingFloorOutsideEndpointRooms(path, floorCells, rooms[fromRoom], candidate))
                    {
                        continue;
                    }

                    AddPathCells(floorCells, path);
                    connections.Add(new RoomConnection(fromRoom, toRoom, path));
                    rooms.Add(candidate);
                    floorCells.UnionWith(candidate.cells);
                    break;
                }
            }
        }

        // Rolls a local-space room shape with total area in [minArea, maxArea]:
        // a single rect, or (B.2) a dominant rect plus 1-2 edge-adjacent wings.
        // Wings keep the profile's minimum dimension and stay within the dominant
        // edge band, so parts are disjoint and the union is connected.
        private static List<RectInt> RollRoomShapeParts(
            System.Random random,
            int minArea,
            int maxArea,
            float nonRectChance,
            DungeonGenerationSettings settings)
        {
            if (random.NextDouble() >= nonRectChance)
            {
                return new List<RectInt> { RollRectWithArea(random, minArea, maxArea, settings) };
            }

            for (int attempt = 0; attempt < 24; attempt++)
            {
                int targetArea = random.Next(minArea, maxArea + 1);
                int dominantMin = Mathf.Max(settings.wingMinDimCells * settings.wingMinDimCells, Mathf.CeilToInt(targetArea * 0.55f));
                int dominantMax = Mathf.FloorToInt(targetArea * 0.8f);
                if (dominantMax < dominantMin)
                {
                    continue;
                }

                RectInt dominant = RollRectWithArea(random, dominantMin, dominantMax, settings);
                if (dominant.width == 0)
                {
                    continue;
                }

                var parts = new List<RectInt> { dominant };
                var usedSides = new List<int>();
                int area = dominant.width * dominant.height;
                int wingCount = random.NextDouble() < 0.35 ? 2 : 1;
                for (int wing = 0; wing < wingCount; wing++)
                {
                    int remaining = targetArea - area;
                    if (remaining < settings.wingMinDimCells * settings.wingMinDimCells)
                    {
                        break;
                    }

                    if (TryRollWing(random, dominant, remaining, usedSides, settings, out RectInt wingRect, out int side))
                    {
                        parts.Add(wingRect);
                        usedSides.Add(side);
                        area += wingRect.width * wingRect.height;
                    }
                }

                if (parts.Count >= 2 && area >= minArea && area <= maxArea)
                {
                    return parts;
                }
            }

            return new List<RectInt> { RollRectWithArea(random, minArea, maxArea, settings) };
        }

        private static RectInt RollRectWithArea(
            System.Random random,
            int minArea,
            int maxArea,
            DungeonGenerationSettings settings)
        {
            var candidates = new List<Vector2Int>();
            for (int w = settings.wingMinDimCells; w <= settings.roomMaxSideCells; w++)
            {
                for (int d = settings.wingMinDimCells; d <= settings.roomMaxSideCells; d++)
                {
                    int area = w * d;
                    if (area < minArea || area > maxArea)
                    {
                        continue;
                    }

                    // Very long slabs read as corridors, not rooms, so the
                    // profile still supplies an aspect cap for rolled parts.
                    if (Mathf.Max(w, d) > Mathf.Min(w, d) * settings.roomMaxAspectRatio)
                    {
                        continue;
                    }

                    candidates.Add(new Vector2Int(w, d));
                }
            }

            if (candidates.Count == 0)
            {
                return new RectInt(0, 0, 0, 0);
            }

            Vector2Int size = candidates[random.Next(candidates.Count)];
            return new RectInt(0, 0, size.x, size.y);
        }

        private static bool TryRollWing(
            System.Random random,
            RectInt dominant,
            int targetArea,
            List<int> usedSides,
            DungeonGenerationSettings settings,
            out RectInt wing,
            out int side)
        {
            wing = default;
            side = random.Next(4);
            for (int i = 0; i < 4; i++, side = (side + 1) % 4)
            {
                if (usedSides.Contains(side))
                {
                    continue;
                }

                bool alongX = side == 0 || side == 2;
                int edgeRun = alongX ? dominant.width : dominant.height;
                if (edgeRun < settings.wingMinDimCells)
                {
                    continue;
                }

                int along = random.Next(settings.wingMinDimCells, edgeRun + 1);
                int depthCap = Mathf.Min(settings.wingMaxDepthCells, targetArea / along);
                if (depthCap < settings.wingMinDimCells)
                {
                    continue;
                }

                int wingDepth = random.Next(settings.wingMinDimCells, depthCap + 1);
                int offset = random.Next(0, edgeRun - along + 1);
                switch (side)
                {
                    case 0: // +y of dominant
                        wing = new RectInt(dominant.xMin + offset, dominant.yMax, along, wingDepth);
                        break;
                    case 1: // +x
                        wing = new RectInt(dominant.xMax, dominant.yMin + offset, wingDepth, along);
                        break;
                    case 2: // -y
                        wing = new RectInt(dominant.xMin + offset, dominant.yMin - wingDepth, along, wingDepth);
                        break;
                    default: // -x
                        wing = new RectInt(dominant.xMin - wingDepth, dominant.yMin + offset, wingDepth, along);
                        break;
                }

                return true;
            }

            return false;
        }

        // Translates a local-space shape to a random grid origin keeping a
        // 1-cell margin to the mask border, rejecting any overlap with
        // existing floor (rooms or corridors).
        private static bool TryPlaceRoomShape(
            List<RectInt> shapeParts,
            int width,
            int depth,
            HashSet<Vector2Int> floorCells,
            System.Random random,
            out RoomFootprint footprint)
        {
            footprint = null;
            if (shapeParts.Count == 0 || shapeParts[0].width == 0)
            {
                return false;
            }

            int xMin = int.MaxValue;
            int yMin = int.MaxValue;
            int xMax = int.MinValue;
            int yMax = int.MinValue;
            foreach (RectInt part in shapeParts)
            {
                xMin = Mathf.Min(xMin, part.xMin);
                yMin = Mathf.Min(yMin, part.yMin);
                xMax = Mathf.Max(xMax, part.xMax);
                yMax = Mathf.Max(yMax, part.yMax);
            }

            int originXMin = 1 - xMin;
            int originXMax = width - 1 - xMax;
            int originYMin = 1 - yMin;
            int originYMax = depth - 1 - yMax;
            if (originXMax < originXMin || originYMax < originYMin)
            {
                return false;
            }

            int originX = random.Next(originXMin, originXMax + 1);
            int originY = random.Next(originYMin, originYMax + 1);
            var placedParts = new List<RectInt>(shapeParts.Count);
            foreach (RectInt part in shapeParts)
            {
                placedParts.Add(new RectInt(part.xMin + originX, part.yMin + originY, part.width, part.height));
            }

            var candidate = new RoomFootprint(placedParts);
            foreach (Vector2Int cell in candidate.cells)
            {
                if (floorCells.Contains(cell))
                {
                    return false;
                }
            }

            footprint = candidate;
            return true;
        }

        private static bool DirectionsAreOpposed(int first, int second)
        {
            return first == Direction.North && second == Direction.South ||
                first == Direction.South && second == Direction.North ||
                first == Direction.East && second == Direction.West ||
                first == Direction.West && second == Direction.East;
        }

        private static bool PortCellsAreStraightThrough(
            SetPiecePortRecord entry,
            SetPiecePortRecord exit,
            int entryDirection,
            int exitDirection)
        {
            if (!TryGetSinglePortCell(entry, out Int2Record entryCell) ||
                !TryGetSinglePortCell(exit, out Int2Record exitCell))
            {
                return false;
            }

            if ((entryDirection == Direction.North || entryDirection == Direction.South) &&
                (exitDirection == Direction.North || exitDirection == Direction.South))
            {
                return entryCell.x == exitCell.x && entryCell.z != exitCell.z;
            }

            if ((entryDirection == Direction.East || entryDirection == Direction.West) &&
                (exitDirection == Direction.East || exitDirection == Direction.West))
            {
                return entryCell.z == exitCell.z && entryCell.x != exitCell.x;
            }

            return false;
        }

        private static bool TryGetSinglePortCell(SetPiecePortRecord port, out Int2Record cell)
        {
            if (port != null && port.cellSpan != null && port.cellSpan.Length == 1)
            {
                cell = port.cellSpan[0];
                return true;
            }

            cell = default;
            return false;
        }

        private static bool TryFindLowestHighestCardinalPorts(
            SetPiecePortRecord[] ports,
            out SetPiecePortRecord lowest,
            out SetPiecePortRecord highest)
        {
            lowest = null;
            highest = null;
            foreach (SetPiecePortRecord port in ports)
            {
                if (port == null || !TryParseDirectionName(port.direction, out _))
                {
                    continue;
                }

                if (lowest == null || port.level < lowest.level)
                {
                    lowest = port;
                }

                if (highest == null || port.level > highest.level)
                {
                    highest = port;
                }
            }

            return lowest != null && highest != null && highest.level > lowest.level;
        }

        private static bool IsTrustedStairConnectorSource(string source)
        {
            return string.Equals(source, "measured-geometry", StringComparison.Ordinal) ||
                string.Equals(source, "measured-usage", StringComparison.Ordinal) ||
                string.Equals(source, "contract-arithmetic", StringComparison.Ordinal);
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
            for (int i = fromThresholdIndex; i < toThresholdIndex; i++)
            {
                if (doorwayCells.Contains(path[i]) || doorwayCells.Contains(path[i + 1]))
                {
                    continue;
                }

                int distance = Mathf.Abs(i - midpoint);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            transitionIndex = bestIndex >= 0 ? bestIndex : midpoint;
            return true;
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

        private static string ResolveSteepDaisStairPrefabPath()
        {
            if (string.IsNullOrEmpty(cachedSteepDaisStairPrefabPath))
            {
                cachedSteepDaisStairPrefabPath = ResolveInventoryPrefabPath(SteepDaisStairPieceName);
            }

            return cachedSteepDaisStairPrefabPath;
        }

        private static string cachedSteepDaisStairPrefabPath;

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

        private static StepFormationModeTable LoadAuthoredStairConnectorTableForGeneration()
        {
            try
            {
                return StepFormationModeTable.Load();
            }
            catch (Exception exception)
            {
                LogPlanningWarning($"Dungeon Lab: could not read authored stair connector candidates from {StepFormationModeTable.Path}; {exception.Message}");
                return null;
            }
        }

        private static int CountConfiguredStairConnectorPrefabs(StepFormationModeTable connectorTable)
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

        private static int FindNearestRoomIndex(IReadOnlyList<RoomFootprint> rooms, RoomFootprint candidate)
        {
            Vector2Int candidateCenter = candidate.Center;
            int nearestIndex = 0;
            int nearestDistance = int.MaxValue;
            for (int i = 0; i < rooms.Count; i++)
            {
                int distance = SquaredDistance(candidateCenter, rooms[i].Center);
                if (distance >= nearestDistance)
                {
                    continue;
                }

                nearestDistance = distance;
                nearestIndex = i;
            }

            return nearestIndex;
        }

        private static HashSet<Vector2Int> BuildRandomRoomShape(System.Random random)
        {
            var floorCells = new HashSet<Vector2Int>();

            int baseWidth = random.Next(4, 9);
            int baseDepth = random.Next(4, 8);
            AddRoomCells(floorCells, new RectInt(0, 0, baseWidth, baseDepth));

            int attachmentCount = random.Next(2, 6);
            for (int i = 0; i < attachmentCount; i++)
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    RectInt bounds = GetCellRect(floorCells);
                    int side = random.Next(4);
                    int width = random.Next(2, 5);
                    int depth = random.Next(2, 5);
                    RectInt attachment;

                    switch (side)
                    {
                        case 0:
                            attachment = new RectInt(
                                random.Next(bounds.xMin, Mathf.Max(bounds.xMin + 1, bounds.xMax - width + 1)),
                                bounds.yMax,
                                width,
                                depth);
                            break;
                        case 1:
                            attachment = new RectInt(
                                random.Next(bounds.xMin, Mathf.Max(bounds.xMin + 1, bounds.xMax - width + 1)),
                                bounds.yMin - depth,
                                width,
                                depth);
                            break;
                        case 2:
                            attachment = new RectInt(
                                bounds.xMin - width,
                                random.Next(bounds.yMin, Mathf.Max(bounds.yMin + 1, bounds.yMax - depth + 1)),
                                width,
                                depth);
                            break;
                        default:
                            attachment = new RectInt(
                                bounds.xMax,
                                random.Next(bounds.yMin, Mathf.Max(bounds.yMin + 1, bounds.yMax - depth + 1)),
                                width,
                                depth);
                            break;
                    }

                    var candidate = new HashSet<Vector2Int>(floorCells);
                    AddRoomCells(candidate, attachment);

                    if (!IsConnected(candidate))
                    {
                        continue;
                    }

                    floorCells = candidate;
                    break;
                }
            }

            PunchNotches(floorCells, random.Next(1, 4), random);
            return floorCells;
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

        private static void PunchNotches(HashSet<Vector2Int> floorCells, int count, System.Random random)
        {
            for (int i = 0; i < count; i++)
            {
                var candidates = new List<Vector2Int>();
                foreach (var cell in floorCells)
                {
                    int neighbors = 0;
                    if (floorCells.Contains(cell + Vector2Int.up)) neighbors++;
                    if (floorCells.Contains(cell + Vector2Int.down)) neighbors++;
                    if (floorCells.Contains(cell + Vector2Int.left)) neighbors++;
                    if (floorCells.Contains(cell + Vector2Int.right)) neighbors++;

                    if (neighbors >= 2 && neighbors <= 3 && IsBoundaryCell(floorCells, cell))
                    {
                        candidates.Add(cell);
                    }
                }

                if (candidates.Count == 0)
                {
                    return;
                }

                // Candidates were gathered from HashSet enumeration; sort before the random
                // pick so the same seed selects the same notch on every runtime.
                candidates.Sort(CompareCells);
                Vector2Int removed = candidates[random.Next(candidates.Count)];
                floorCells.Remove(removed);
                if (!IsConnected(floorCells))
                {
                    floorCells.Add(removed);
                }
            }
        }

        private static bool IsBoundaryCell(HashSet<Vector2Int> floorCells, Vector2Int cell)
        {
            return !floorCells.Contains(cell + Vector2Int.up) ||
                !floorCells.Contains(cell + Vector2Int.down) ||
                !floorCells.Contains(cell + Vector2Int.left) ||
                !floorCells.Contains(cell + Vector2Int.right);
        }

        private static HashSet<WallEdge> PickOpenings(HashSet<Vector2Int> floorCells, int requestedCount, System.Random random)
        {
            var candidates = new List<WallEdge>();

            foreach (var cell in floorCells)
            {
                if (!floorCells.Contains(cell + Vector2Int.down))
                {
                    candidates.Add(new WallEdge(cell, Direction.South));
                }

                if (!floorCells.Contains(cell + Vector2Int.up))
                {
                    candidates.Add(new WallEdge(cell, Direction.North));
                }

                if (!floorCells.Contains(cell + Vector2Int.left))
                {
                    candidates.Add(new WallEdge(cell, Direction.West));
                }

                if (!floorCells.Contains(cell + Vector2Int.right))
                {
                    candidates.Add(new WallEdge(cell, Direction.East));
                }
            }

            // Same hazard as PunchNotches: order the HashSet-derived candidates before the
            // seeded shuffle so openings are reproducible per seed across runtimes.
            candidates.Sort((left, right) =>
            {
                int byCell = CompareCells(left.cell, right.cell);
                return byCell != 0 ? byCell : left.direction.CompareTo(right.direction);
            });
            Shuffle(candidates, random);

            var openings = new HashSet<WallEdge>();
            int targetCount = Mathf.Clamp(requestedCount, 1, Mathf.Min(3, candidates.Count));

            foreach (var candidate in candidates)
            {
                if (IsNearExistingOpening(candidate, openings))
                {
                    continue;
                }

                openings.Add(candidate);
                if (openings.Count >= targetCount)
                {
                    break;
                }
            }

            if (openings.Count == 0 && candidates.Count > 0)
            {
                openings.Add(candidates[0]);
            }

            return openings;
        }

        private static bool IsNearExistingOpening(WallEdge candidate, HashSet<WallEdge> openings)
        {
            foreach (var opening in openings)
            {
                int distance = Mathf.Abs(candidate.cell.x - opening.cell.x) + Mathf.Abs(candidate.cell.y - opening.cell.y);
                if (distance <= 2)
                {
                    return true;
                }
            }

            return false;
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

        private static void AddRoomCells(HashSet<Vector2Int> floorCells, RectInt room)
        {
            for (int z = room.yMin; z < room.yMax; z++)
            {
                for (int x = room.xMin; x < room.xMax; x++)
                {
                    floorCells.Add(new Vector2Int(x, z));
                }
            }
        }

        private static void AddPathCells(HashSet<Vector2Int> floorCells, IReadOnlyList<Vector2Int> path)
        {
            foreach (Vector2Int cell in path)
            {
                floorCells.Add(cell);
            }
        }

        private static List<Vector2Int> CarveCorridor(
            HashSet<Vector2Int> floorCells,
            Vector2Int start,
            Vector2Int end,
            System.Random random)
        {
            var path = new List<Vector2Int>();
            bool horizontalFirst = random.Next(2) == 0;
            if (horizontalFirst)
            {
                CarveHorizontal(floorCells, path, start.x, end.x, start.y);
                CarveVertical(floorCells, path, start.y, end.y, end.x);
                return path;
            }

            CarveVertical(floorCells, path, start.y, end.y, start.x);
            CarveHorizontal(floorCells, path, start.x, end.x, end.y);
            return path;
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

        private static void CarveHorizontal(HashSet<Vector2Int> floorCells, int fromX, int toX, int z)
        {
            CarveHorizontal(floorCells, null, fromX, toX, z);
        }

        private static void CarveHorizontal(HashSet<Vector2Int> floorCells, List<Vector2Int> path, int fromX, int toX, int z)
        {
            int min = Mathf.Min(fromX, toX);
            int max = Mathf.Max(fromX, toX);
            int step = fromX <= toX ? 1 : -1;
            for (int x = fromX; ; x += step)
            {
                AddCorridorCell(floorCells, path, new Vector2Int(x, z));
                if (x == toX)
                {
                    break;
                }
            }
        }

        private static void CarveVertical(HashSet<Vector2Int> floorCells, int fromZ, int toZ, int x)
        {
            CarveVertical(floorCells, null, fromZ, toZ, x);
        }

        private static void CarveVertical(HashSet<Vector2Int> floorCells, List<Vector2Int> path, int fromZ, int toZ, int x)
        {
            int step = fromZ <= toZ ? 1 : -1;
            for (int z = fromZ; ; z += step)
            {
                AddCorridorCell(floorCells, path, new Vector2Int(x, z));
                if (z == toZ)
                {
                    break;
                }
            }
        }

        private static void AddCorridorCell(HashSet<Vector2Int> floorCells, List<Vector2Int> path, Vector2Int cell)
        {
            floorCells.Add(cell);
            if (path == null || path.Count > 0 && path[path.Count - 1] == cell)
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
            System.Random random,
            Dictionary<string, int> rejectionHistogram,
            out DungeonLayout layout,
            out TieredLevelPlan levelPlan,
            out int layoutAttemptsUsed,
            out string rejectionReason)
        {
            layout = default;
            levelPlan = default;
            layoutAttemptsUsed = 0;
            rejectionReason = string.Empty;
            int layoutAttemptLimit = phase1RouteFirstPilotSelected
                ? Phase1LayoutAttemptLimit
                : LayoutRegenerationAttempts;
            for (int attempt = 0; attempt < layoutAttemptLimit; attempt++)
            {
                layoutAttemptsUsed = attempt + 1;
                DungeonLayout candidateLayout;
                // Phase 1's sole temporary comparison selector. Remove this
                // branch and BuildRandomDungeonLayoutData in Phase 2.
                if (phase1RouteFirstPilotSelected)
                {
                    if (!TryBuildProcessionalSpineDungeonLayout(
                            dungeonSeed,
                            layoutAttemptsUsed,
                            out candidateLayout,
                            out rejectionReason))
                    {
                        RecordRejection(rejectionHistogram, rejectionReason);
                        continue;
                    }
                }
                else
                {
                    candidateLayout = BuildRandomDungeonLayoutData(random);
                }

                if (TryBuildTieredLevelPlan(candidateLayout, dungeonSeed, random, rejectionHistogram, out layout, out levelPlan, out rejectionReason))
                {
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

        private static void LogPlanningInfo(string message)
        {
            try
            {
                Debug.Log(message);
            }
            catch (Exception)
            {
                Console.WriteLine(message);
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

        private static bool TryBuildTieredLevelPlan(
            DungeonLayout layout,
            int dungeonSeed,
            System.Random random,
            Dictionary<string, int> rejectionHistogram,
            out DungeonLayout acceptedLayout,
            out TieredLevelPlan plan,
            out string rejectionReason)
        {
            acceptedLayout = default;
            plan = default;
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

            // Hold one archetype for most attempts so archetypes that need a few
            // re-rolls (level conflicts depend on the BFS shuffle) keep a fair share of
            // the mix; the tail attempts re-roll freely as a safety valve.
            ElevationArchetype lockedArchetype = ElevationArchetypePlanner.Choose(random);
            int lockedAttempts = LevelAssignmentAttempts * 3 / 4;
            for (int attempt = 0; attempt < LevelAssignmentAttempts; attempt++)
            {
                bool forceElevation = attempt == LevelAssignmentAttempts - 1;
                ElevationArchetype? archetypeOverride = attempt < lockedAttempts
                    ? lockedArchetype
                    : (ElevationArchetype?)null;
                if (TryBuildTieredLevelPlanAttempt(
                        layout,
                        reviewedStairOptions,
                        dungeonSeed,
                        random,
                        forceElevation,
                        archetypeOverride,
                        out acceptedLayout,
                        out plan,
                        out rejectionReason))
                {
                    return true;
                }

                RecordRejection(rejectionHistogram, rejectionReason);
            }

            return false;
        }

        private static bool TryBuildTieredLevelPlanAttempt(
            DungeonLayout layout,
            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions,
            int dungeonSeed,
            System.Random random,
            bool forceElevation,
            ElevationArchetype? archetypeOverride,
            out DungeonLayout acceptedLayout,
            out TieredLevelPlan plan,
            out string rejectionReason)
        {
            acceptedLayout = default;
            plan = default;
            RoomZoneContext zones = RoomZoneContext.Build(layout);
            if (!TryAssignRoomLevels(
                    layout,
                    zones,
                    reviewedStairOptions,
                    random,
                    forceElevation,
                    archetypeOverride,
                    out int[] zoneLevels,
                    out ElevationArchetype archetype,
                    out rejectionReason))
            {
                return false;
            }

            DungeonLayout loopedLayout = AddLevelSafeLoopConnections(layout, zones, zoneLevels, random, CurrentGenerationSettings);
            int loopEdges = CountLoopEdges(loopedLayout);
            if (loopEdges <= 0)
            {
                rejectionReason = "floorplan had no loop edges";
                return false;
            }

            if (loopedLayout.rooms.Count < CurrentGenerationSettings.denseFloorplanMinRooms)
            {
                rejectionReason = $"floorplan had only {loopedLayout.rooms.Count} rooms";
                return false;
            }

            float floorFillPercent = CalculateFloorFillPercent(loopedLayout.floorCells);
            if (floorFillPercent < CurrentGenerationSettings.denseFloorplanMinFillPercent)
            {
                rejectionReason = $"floor-fill {floorFillPercent * 100f:0.#}% was below dense gate {CurrentGenerationSettings.denseFloorplanMinFillPercent * 100f:0.#}%";
                return false;
            }

            // Loop connections never change rooms or zone plans, so the zone context
            // stays valid for the looped layout; only its connection list grew.
            if (!TryValidateConnectedRoomLevelDeltas(loopedLayout, zones, zoneLevels, reviewedStairOptions, out rejectionReason))
            {
                return false;
            }

            if (!TryBuildCellLevelField(loopedLayout, zones, zoneLevels, reviewedStairOptions, dungeonSeed, random, out Dictionary<Vector2Int, int> cellLevels, out List<ElevationEdgeModel.TransitionEdge> transitions, out string stairCandidateSummary, out List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs, out List<DaisShowpiece> daisShowpieces, out List<Vector2Int> promontoryCells, out rejectionReason))
            {
                return false;
            }

            GetLevelRange(cellLevels, out int minLevel, out int maxLevel);
            int levelCount = CountDistinctLevels(cellLevels);
            if (levelCount <= 1)
            {
                rejectionReason = $"room graph resolved to a single level (archetype {archetype})";
                return false;
            }

            if (!TryValidateTransitionLevelDeltas(cellLevels, transitions, out rejectionReason))
            {
                return false;
            }

            if (!TryBuildFloorStairPortGraph(cellLevels, transitions, out FloorStairPortGraph portGraph, out rejectionReason))
            {
                return false;
            }

            if (!portGraph.IsGloballyConnected(out string portGraphReachability))
            {
                rejectionReason = portGraphReachability;
                return false;
            }

            // Reported stat only (demoted from a hard gate 2026-06-10): archetypes
            // already guarantee vertical structure, so a dungeon without a delta>=2
            // vista line is acceptable.
            int overlookCount = CountSpatialOverlookEdges(cellLevels, transitions);

            plan = new TieredLevelPlan(
                cellLevels,
                transitions,
                levelCount,
                minLevel,
                maxLevel,
                FormatRoomsPerTier(CountRoomsPerTier(zoneLevels)),
                overlookCount,
                FormatTransitionSummary(cellLevels, transitions),
                FormatStairUsageHistogram(transitions),
                FormatStairTopologyHistogram(transitions, reviewedStairOptions),
                FormatStairPlacementClassHistogram(transitions),
                stairCandidateSummary,
                portGraph.Summary,
                loopedLayout.connectorCandidateCount,
                archetype.ToString(),
                synthesizedStairs,
                FormatSynthesizedStairSummary(synthesizedStairs),
                daisShowpieces,
                promontoryCells);
            acceptedLayout = loopedLayout;
            return true;
        }

        private static IReadOnlyList<WeightedStairConnectorOption> LoadWeightedStraightStairConnectorOptions()
        {
            if (!File.Exists(StepLibraryIndexPath))
            {
                LogPlanningWarning(
                    $"Dungeon Lab: {StepLibraryIndexPath} is not present in this clean repo; transitions will use the configured primary stair fallback.");
                return Array.Empty<WeightedStairConnectorOption>();
            }

            StepLibraryIndex index = LoadStepLibraryIndex();
            StepFormationModeTable connectorTable;
            try
            {
                connectorTable = StepFormationModeTable.Load();
            }
            catch (Exception exception)
            {
                LogPlanningWarning($"Dungeon Lab: could not read stair connector weights from {StepFormationModeTable.Path}; {exception.Message}");
                return Array.Empty<WeightedStairConnectorOption>();
            }

            var options = new List<WeightedStairConnectorOption>();
            foreach (StepLibraryRecord record in index.records)
            {
                if (record == null || !connectorTable.IsStairConnectorPath(record.path))
                {
                    continue;
                }

                if (TryBuildWeightedStairConnectorOption(record, connectorTable, out WeightedStairConnectorOption option, out _))
                {
                    options.Add(option);
                }
            }

            if (options.Count == 0)
            {
                LogPlanningWarning("Dungeon Lab: no measured straight-through stair connector options were available; transitions will use the configured primary stair fallback.");
            }

            return options;
        }

        private static bool TryBuildWeightedStairConnectorOption(
            StepLibraryRecord record,
            StepFormationModeTable connectorTable,
            out WeightedStairConnectorOption option,
            out string skipReason)
        {
            option = default;
            skipReason = string.Empty;
            if (!IsTrustedStairConnectorSource(record.portSource) ||
                !string.Equals(record.portConfidence, "high", StringComparison.Ordinal) ||
                record.ports == null ||
                record.ports.Length != 2)
            {
                skipReason = "not a clean 2-port measured connector";
                return false;
            }

            if (!TryFindLowestHighestCardinalPorts(record.ports, out SetPiecePortRecord entry, out SetPiecePortRecord exit) ||
                !TryParseDirectionName(entry.direction, out int entryDirection) ||
                !TryParseDirectionName(exit.direction, out int exitDirection))
            {
                skipReason = "no cardinal lowest/highest ports";
                return false;
            }

            if (!DirectionsAreOpposed(entryDirection, exitDirection) ||
                !PortCellsAreStraightThrough(entry, exit, entryDirection, exitDirection))
            {
                skipReason = $"measured ports were not straight-through/opposed ({entry.direction}@L{entry.level} -> {exit.direction}@L{exit.level})";
                return false;
            }

            // CONSTANTS AUDIT NOTE (1u recalibration): port levels in the legacy
            // step_library_index.json are still old 2u levels. That index is absent
            // in this repo so this loader is inert; if the index is ever regenerated
            // it must be measured in 1u levels (see StepPieceMetrology) before these
            // rises can be trusted against MaxGeneratedLevel.
            int rise = exit.level - entry.level;
            if (rise < 1 || rise > MaxGeneratedLevel)
            {
                skipReason = $"measured rise {rise}, not a supported transition rise";
                return false;
            }

            float weight = connectorTable.TryGetStairConnectorWeight(record.name, out float configuredWeight)
                ? configuredWeight
                : DefaultStairWeightForRise(rise);
            if (weight <= 0f)
            {
                skipReason = "configured weight was zero";
                return false;
            }

            option = new WeightedStairConnectorOption(record.name, record.path, rise, weight);
            return true;
        }

        private static float DefaultStairWeightForRise(int rise)
        {
            return Mathf.Pow(0.18f, Mathf.Max(0, rise - 1));
        }

        private static string ChooseWeightedStairPrefabPath(
            IReadOnlyList<WeightedStairConnectorOption> options,
            int rise,
            System.Random random)
        {
            if (options == null || options.Count == 0)
            {
                return string.Empty;
            }

            float totalWeight = 0f;
            foreach (WeightedStairConnectorOption option in options)
            {
                if (option.rise == rise)
                {
                    totalWeight += option.weight;
                }
            }

            if (totalWeight <= 0f)
            {
                return string.Empty;
            }

            double roll = random.NextDouble() * totalWeight;
            foreach (WeightedStairConnectorOption option in options)
            {
                if (option.rise != rise)
                {
                    continue;
                }

                roll -= option.weight;
                if (roll <= 0.0)
                {
                    return option.prefabPath;
                }
            }

            foreach (WeightedStairConnectorOption option in options)
            {
                if (option.rise == rise)
                {
                    return option.prefabPath;
                }
            }

            return string.Empty;
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

        private static DungeonLayout AddLevelSafeLoopConnections(
            DungeonLayout layout,
            RoomZoneContext zones,
            IReadOnlyList<int> zoneLevels,
            System.Random random,
            DungeonGenerationSettings settings)
        {
            var floorCells = new HashSet<Vector2Int>(layout.floorCells);
            var connections = new List<RoomConnection>(layout.connections);
            var connectedPairs = new HashSet<string>();
            foreach (RoomConnection connection in connections)
            {
                connectedPairs.Add(RoomPairKey(connection.fromRoom, connection.toRoom));
            }

            var candidates = new List<LoopConnectionCandidate>();
            for (int first = 0; first < layout.rooms.Count; first++)
            {
                for (int second = first + 1; second < layout.rooms.Count; second++)
                {
                    if (connectedPairs.Contains(RoomPairKey(first, second)))
                    {
                        continue;
                    }

                    // Loop corridors run between room centers, so gate on the level
                    // of the zone holding each center. Decision A: a loop is added
                    // only when the two rooms sit flat or one clean major apart
                    // (4u/8u) — never an off-grammar delta (e.g. 6u into the +2
                    // bridge region), which would have no servable corridor stair.
                    int firstLevel = zoneLevels[zones.NodeOfCell(first, layout.rooms[first].Center)];
                    int secondLevel = zoneLevels[zones.NodeOfCell(second, layout.rooms[second].Center)];
                    int levelDelta = Mathf.Abs(firstLevel - secondLevel);
                    if (levelDelta != 0 && levelDelta != MajorRiseLevels && levelDelta != DoubleMajorRiseLevels)
                    {
                        continue;
                    }

                    int distance = SquaredDistance(layout.rooms[first].Center, layout.rooms[second].Center);
                    if (distance > settings.maxLoopCandidateDistanceCells * settings.maxLoopCandidateDistanceCells)
                    {
                        continue;
                    }

                    candidates.Add(new LoopConnectionCandidate(first, second, distance));
                }
            }

            candidates.Sort((left, right) => left.distance.CompareTo(right.distance));
            int desiredLoops = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(1, layout.rooms.Count - 1) * settings.loopConnectionFraction));
            int loopCount = 0;
            foreach (LoopConnectionCandidate candidate in candidates)
            {
                if (loopCount >= desiredLoops)
                {
                    break;
                }

                List<Vector2Int> path = BuildCorridorPath(layout.rooms[candidate.firstRoom].Center, layout.rooms[candidate.secondRoom].Center, random);
                if (!ValidatePathCardinality(path, out _) ||
                    PathCrossesThirdRoom(path, layout.rooms, candidate.firstRoom, candidate.secondRoom) ||
                    PathTouchesExistingFloorOutsideEndpointRooms(
                        path,
                        floorCells,
                        layout.rooms[candidate.firstRoom],
                        layout.rooms[candidate.secondRoom]))
                {
                    continue;
                }

                foreach (Vector2Int cell in path)
                {
                    floorCells.Add(cell);
                }

                connections.Add(new RoomConnection(candidate.firstRoom, candidate.secondRoom, path));
                connectedPairs.Add(RoomPairKey(candidate.firstRoom, candidate.secondRoom));
                loopCount++;
            }

            return new DungeonLayout(
                floorCells,
                layout.rooms,
                connections,
                layout.roomZones,
                layout.connectorCandidateCount);
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

        private static bool PathCrossesThirdRoom(
            IReadOnlyList<Vector2Int> path,
            IReadOnlyList<RoomFootprint> rooms,
            int firstRoom,
            int secondRoom)
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
                    if (room.Contains(cell))
                    {
                        return true;
                    }
                }
            }

            return false;
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
            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions,
            System.Random random,
            bool forceElevation,
            ElevationArchetype? archetypeOverride,
            out int[] zoneLevels,
            out ElevationArchetype archetype,
            out string rejectionReason)
        {
            rejectionReason = string.Empty;
            archetype = archetypeOverride ?? ElevationArchetypePlanner.Choose(random);
            zoneLevels = new int[zones.nodeCount];
            for (int i = 0; i < zoneLevels.Length; i++)
            {
                zoneLevels[i] = -1;
            }

            List<int>[] adjacency = BuildZoneAdjacency(layout, zones);
            if (!TryBuildRoomBfsTree(
                    adjacency,
                    random,
                    out int[] parents,
                    out int[] depths,
                    out List<int> order,
                    out rejectionReason))
            {
                return false;
            }

            int maxDepth = 0;
            foreach (int depth in depths)
            {
                maxDepth = Mathf.Max(maxDepth, depth);
            }

            // Archetype targets and level repair run natively in 1u levels.
            // Decision A: each corridor hop climbs one 4u major (occasionally an
            // 8u double-major), so the field amplitude scales at the major rise
            // per graph hop, capped by the 24u world height limit. Targets stay at
            // 1u resolution on purpose — the repair lands them on the 4u lattice,
            // and a target that falls between majors is what lets the single 2u
            // bridge find its place.
            int targetMaxLevel = Mathf.Min(MaxGeneratedLevel, MajorRiseLevels * maxDepth);
            if (targetMaxLevel <= 0)
            {
                rejectionReason = "room graph did not have enough depth for elevation";
                return false;
            }

            int targetNode = ChooseDeepestRoom(depths, random);
            int[] targetLevels = ElevationArchetypePlanner.BuildTargetLevels(
                BuildZoneNodePositions(layout, zones),
                depths,
                maxDepth,
                targetNode,
                targetMaxLevel,
                archetype,
                random);

            IReadOnlyList<int> allowedAbsDeltas = BuildAllowedLevelDeltas();
            // Decision A: the root anchors the 4u lattice, so it snaps to a major.
            // {4,8} moves from a major-aligned root keep every corridor-reached
            // zone on-grammar; only the budgeted 2u bridge leaves the lattice.
            int twoBridgeBudget = MaxTwoBridgesPerDungeon;

            // The root is room 0's base zone; if that room is split, leave headroom
            // for its +1 raised sibling.
            int rootMaxLevel = zones.RaisedNodeOfRoom(0) >= 0 ? MaxGeneratedLevel - 1 : MaxGeneratedLevel;
            zoneLevels[0] = SnapLevelToMajor(Mathf.Clamp(targetLevels[0], 0, rootMaxLevel), rootMaxLevel);
            for (int i = 1; i < order.Count; i++)
            {
                int node = order[i];
                int parent = parents[node];
                if (parent < 0 || zoneLevels[parent] < 0)
                {
                    rejectionReason = $"zone graph parent for region {node} was not assigned";
                    return false;
                }

                // A seam edge fixes the delta at exactly 1u: the raised zone sits one
                // level above its room's base zone, whichever side the BFS reaches first.
                if (zones.IsSeamEdge(parent, node))
                {
                    int seamLevel = zoneLevels[parent] + (zones.IsRaisedNode(node) ? 1 : -1);
                    if (seamLevel < 0 || seamLevel > MaxGeneratedLevel)
                    {
                        rejectionReason = $"raised zone of room {zones.RoomOfNode(node)} left the level range 0..{MaxGeneratedLevel}";
                        return false;
                    }

                    zoneLevels[node] = seamLevel;
                    continue;
                }

                // Keep split rooms inside the level range up front: the base zone of
                // a split room must leave headroom for its +1 raised sibling, and a
                // raised zone reached via corridor can never sit at 0.
                int nodeRoom = zones.RoomOfNode(node);
                bool nodeIsRaised = zones.IsRaisedNode(node);
                bool roomIsSplit = zones.RaisedNodeOfRoom(nodeRoom) >= 0;
                int nodeMinLevel = nodeIsRaised ? 1 : 0;
                int nodeMaxLevel = roomIsSplit && !nodeIsRaised ? MaxGeneratedLevel - 1 : MaxGeneratedLevel;
                int parentLevel = zoneLevels[parent];
                int chosen = PickLevelTowardTarget(
                    parentLevel,
                    targetLevels[node],
                    allowedAbsDeltas,
                    nodeMinLevel,
                    nodeMaxLevel,
                    random,
                    forceProgress: forceElevation);

                // Gold-style 2u bridge (decision A): if the dungeon's single bridge
                // is unspent and a 2u move lands STRICTLY closer to the field than
                // any 4u/8u move, spend it here — this is the one corridor that
                // shifts its subtree onto the +2 coset.
                if (twoBridgeBudget > 0)
                {
                    int withBridge = PickLevelTowardTarget(
                        parentLevel,
                        targetLevels[node],
                        AllowedLevelDeltasWithBridge,
                        nodeMinLevel,
                        nodeMaxLevel,
                        random,
                        forceProgress: forceElevation);
                    if (Mathf.Abs(withBridge - targetLevels[node]) < Mathf.Abs(chosen - targetLevels[node]) &&
                        Mathf.Abs(withBridge - parentLevel) == PrimaryStairRiseLevels)
                    {
                        chosen = withBridge;
                        twoBridgeBudget--;
                    }
                }

                zoneLevels[node] = chosen;
            }

            for (int i = 0; i < zoneLevels.Length; i++)
            {
                if (zoneLevels[i] >= 0)
                {
                    continue;
                }

                rejectionReason = $"zone graph left region {i} unreachable";
                return false;
            }

            // Non-tree seam edges cannot be repaired (the +1 is structural), so any
            // seam whose delta came out wrong via another path rejects the attempt.
            foreach (RoomZonePlan plan in layout.roomZones)
            {
                int raisedNode = zones.RaisedNodeOfRoom(plan.roomIndex);
                if (zoneLevels[raisedNode] - zoneLevels[plan.roomIndex] != 1)
                {
                    rejectionReason = $"raised zone of room {plan.roomIndex} was not exactly 1u above its base zone";
                    return false;
                }
            }

            return true;
        }

        private static List<int>[] BuildZoneAdjacency(DungeonLayout layout, RoomZoneContext zones)
        {
            var adjacency = new List<int>[zones.nodeCount];
            for (int i = 0; i < adjacency.Length; i++)
            {
                adjacency[i] = new List<int>();
            }

            void AddEdge(int first, int second)
            {
                if (first == second)
                {
                    return;
                }

                adjacency[first].Add(second);
                adjacency[second].Add(first);
            }

            foreach (RoomConnection connection in layout.connections)
            {
                ResolveConnectionNodes(zones, layout.rooms, connection, out int fromNode, out int toNode);
                AddEdge(fromNode, toNode);
            }

            foreach (RoomZonePlan plan in layout.roomZones)
            {
                AddEdge(plan.roomIndex, zones.RaisedNodeOfRoom(plan.roomIndex));
            }

            return adjacency;
        }

        private static List<Vector2> BuildZoneNodePositions(DungeonLayout layout, RoomZoneContext zones)
        {
            var positions = new List<Vector2>(zones.nodeCount);
            for (int node = 0; node < zones.nodeCount; node++)
            {
                positions.Add(zones.NodeRect(layout.rooms, node).center);
            }

            return positions;
        }

        private static bool TryBuildRoomBfsTree(
            IReadOnlyList<int>[] adjacency,
            System.Random random,
            out int[] parents,
            out int[] depths,
            out List<int> order,
            out string rejectionReason)
        {
            parents = new int[adjacency.Length];
            depths = new int[adjacency.Length];
            order = new List<int>();
            for (int i = 0; i < adjacency.Length; i++)
            {
                parents[i] = -1;
                depths[i] = -1;
            }

            var queue = new Queue<int>();
            depths[0] = 0;
            queue.Enqueue(0);
            order.Add(0);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                var neighbors = new List<int>(adjacency[current]);
                Shuffle(neighbors, random);
                foreach (int neighbor in neighbors)
                {
                    if (depths[neighbor] >= 0)
                    {
                        continue;
                    }

                    parents[neighbor] = current;
                    depths[neighbor] = depths[current] + 1;
                    order.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }

            for (int i = 0; i < depths.Length; i++)
            {
                if (depths[i] >= 0)
                {
                    continue;
                }

                rejectionReason = $"room graph left region {i} unreachable";
                return false;
            }

            rejectionReason = string.Empty;
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

        private static int ChooseDeepestRoom(IReadOnlyList<int> depths, System.Random random)
        {
            int maxDepth = 0;
            var candidates = new List<int>();
            for (int i = 0; i < depths.Count; i++)
            {
                if (depths[i] > maxDepth)
                {
                    maxDepth = depths[i];
                    candidates.Clear();
                }

                if (depths[i] == maxDepth)
                {
                    candidates.Add(i);
                }
            }

            return candidates[random.Next(candidates.Count)];
        }

        // Allowed parent->child level moves for corridor edges, in u-levels
        // (decision A): one 4u major or one 8u double-major. 1u and 2u are
        // intra-room accents (seams, dais) and never come from this list — except
        // the single per-dungeon 2u bridge, which uses the with-bridge variant
        // below at exactly one corridor. Both rises are served by the reviewed
        // pool (rise-4 and rise-8 contracts exist) with synthesis as fallback.
        private static readonly int[] AllowedLevelDeltas = { MajorRiseLevels, DoubleMajorRiseLevels };
        private static readonly int[] AllowedLevelDeltasWithBridge = { PrimaryStairRiseLevels, MajorRiseLevels, DoubleMajorRiseLevels };

        private static IReadOnlyList<int> BuildAllowedLevelDeltas()
        {
            return AllowedLevelDeltas;
        }

        // Rounds a level to the nearest 4u major without exceeding maxLevel (so a
        // split room's snapped base still leaves headroom for its +1 raised zone).
        private static int SnapLevelToMajor(int level, int maxLevel)
        {
            int snapped = Mathf.RoundToInt(level / (float)MajorRiseLevels) * MajorRiseLevels;
            while (snapped > maxLevel)
            {
                snapped -= MajorRiseLevels;
            }

            return Mathf.Max(0, snapped);
        }

        private static int PickLevelTowardTarget(
            int parentLevel,
            int targetLevel,
            IReadOnlyList<int> allowedAbsDeltas,
            int minLevel,
            int maxLevel,
            System.Random random,
            bool forceProgress)
        {
            var best = new List<int>();
            int bestDistance = int.MaxValue;
            void Consider(int candidate)
            {
                if (candidate < minLevel || candidate > maxLevel)
                {
                    return;
                }

                if (forceProgress && candidate == parentLevel && targetLevel != parentLevel)
                {
                    return;
                }

                int distance = Mathf.Abs(candidate - targetLevel);
                if (distance > bestDistance)
                {
                    return;
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best.Clear();
                }

                best.Add(candidate);
            }

            Consider(parentLevel);
            foreach (int absDelta in allowedAbsDeltas)
            {
                Consider(parentLevel + absDelta);
                Consider(parentLevel - absDelta);
            }

            if (best.Count == 0)
            {
                return parentLevel;
            }

            // Decision A: no 1u plateau-texture softening — a 1u corridor move is
            // no longer on the grammar (1u is intra-room only). Tier texture now
            // comes from the 4u majors and intra-room seams/dais.
            return best[random.Next(best.Count)];
        }

        private static bool TryValidateConnectedRoomLevelDeltas(
            DungeonLayout layout,
            RoomZoneContext zones,
            int[] zoneLevels,
            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions,
            out string rejectionReason)
        {
            foreach (RoomConnection connection in layout.connections)
            {
                // Corridor deltas are measured between the threshold zones the
                // connection binds to. Decision A: a flat corridor needs no stair;
                // the 2u bridge uses the reviewed primary rise; every other delta
                // (4u/8u majors) needs a reviewed contract with that exact rise.
                ResolveConnectionNodes(zones, layout.rooms, connection, out int fromNode, out int toNode);
                int delta = Mathf.Abs(zoneLevels[fromNode] - zoneLevels[toNode]);
                if (delta == 0)
                {
                    continue;
                }

                // Decision A grammar safety net: a corridor delta must be a 4u/8u
                // major or the single 2u bridge — never an off-grammar value (e.g.
                // a 12u multi-major or an odd delta). A non-tree edge between two
                // on-grammar rooms can produce these; reject and let the attempt
                // retry rather than render an off-grammar stair.
                if (delta != PrimaryStairRiseLevels && delta != MajorRiseLevels && delta != DoubleMajorRiseLevels)
                {
                    rejectionReason = $"connected regions {connection.fromRoom} and {connection.toRoom} differed by off-grammar {delta} levels";
                    return false;
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

        private static bool TryBuildCellLevelField(
            DungeonLayout layout,
            RoomZoneContext zones,
            int[] zoneLevels,
            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions,
            int dungeonSeed,
            System.Random random,
            out Dictionary<Vector2Int, int> cellLevels,
            out List<ElevationEdgeModel.TransitionEdge> transitions,
            out string stairCandidateSummary,
            out List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs,
            out List<DaisShowpiece> daisShowpieces,
            out List<Vector2Int> promontoryCells,
            out string rejectionReason)
        {
            cellLevels = new Dictionary<Vector2Int, int>();
            transitions = new List<ElevationEdgeModel.TransitionEdge>();
            stairCandidateSummary = "[]";
            synthesizedStairs = new List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)>();
            daisShowpieces = new List<DaisShowpiece>();
            promontoryCells = new List<Vector2Int>();
            rejectionReason = string.Empty;

            for (int roomIndex = 0; roomIndex < layout.rooms.Count; roomIndex++)
            {
                foreach (Vector2Int cell in layout.rooms[roomIndex].CellsRowMajor())
                {
                    if (!TrySetCellLevel(cellLevels, cell, zoneLevels[zones.NodeOfCell(roomIndex, cell)], out rejectionReason))
                    {
                        return false;
                    }
                }
            }

            var transitionKeys = new HashSet<string>();
            var stairCandidateCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var plannedStairLedger = new StairPlacementLedger();
            var externalSpanGapCells = new HashSet<Vector2Int>();
            var spanDeckLevels = new Dictionary<Vector2Int, int>();

            // New rule (user review 2026-06-11): a 1u step strip must never sit in
            // a doorway cell — a step half-blocking a room entrance disrupts flow
            // and reads wrong. Both cells of every door edge are off limits for
            // seam and corridor strips alike (unfiltered: every path crossing
            // counts, leveled or not).
            var doorwayCells = new HashSet<Vector2Int>();
            foreach (ElevationEdgeModel.DoorwayEdge doorway in BuildDoorwayEdges(layout, cellLevels: null))
            {
                doorwayCells.Add(doorway.firstCell);
                doorwayCells.Add(doorway.secondCell);
            }

            // Seam transitions first (deterministic room order): every adjacent cell
            // pair across a zone seam carries a rise-1 step strip, so the 1u delta is
            // never freely walkable (design decision 3). The strip's geometry sits in
            // the lower cell, so that cell registers as FOOTPRINT — landings may share
            // other landings but never a footprint, which keeps contract stair landings
            // (and footprints) off the steps. The raised cell is clean floor and stays
            // a shareable landing.
            string seamStairPrefabPath = ResolveSeamStairPrefabPath();
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

                    string key = TransitionKey(raisedCell, lowerCell);
                    if (!transitionKeys.Add(key))
                    {
                        continue;
                    }

                    transitions.Add(new ElevationEdgeModel.TransitionEdge(
                        raisedCell,
                        lowerCell,
                        seamStairPrefabPath,
                        SeamStairPlacementClass));
                    plannedStairLedger.Register(
                        new[] { lowerCell },
                        Array.Empty<Vector2Int>(),
                        new[] { raisedCell });
                }
            }

            foreach (RoomConnection connection in layout.connections)
            {
                ResolveConnectionNodes(zones, layout.rooms, connection, out int fromNode, out int toNode);
                int fromLevel = zoneLevels[fromNode];
                int toLevel = zoneLevels[toNode];
                int delta = Mathf.Abs(fromLevel - toLevel);
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
                // A 1u corridor delta is closed by a single embedded step strip
                // (design decision 3) rather than a reviewed stair contract.
                bool corridorStepStrip = delta == 1;
                if (corridorStepStrip &&
                    !TryChooseCorridorStepIndex(
                        path,
                        layout.rooms[connection.fromRoom],
                        layout.rooms[connection.toRoom],
                        doorwayCells,
                        out transitionIndex))
                {
                    rejectionReason = $"connection {connection.fromRoom}->{connection.toRoom} had no corridor cell pair for a 1u step strip";
                    return false;
                }

                if (delta > 1 &&
                    !TryChooseReviewedActiveStairTransition(
                        reviewedStairOptions,
                        delta,
                        maxLaneCount: MaxActiveStairLaneCount,
                        path,
                        zones.NodeArea(layout.rooms, fromNode),
                        zones.NodeArea(layout.rooms, toNode),
                        layout.floorCells,
                        cellLevels,
                        fromLevel,
                        toLevel,
                        random,
                        allowExternalSpan: true,
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
                        out stairOption))
                {
                    // Online synthesis fallback (step 7, decisions 16-21): the
                    // reviewed pool offered no (contract, position) fit for this
                    // corridor, so shape a staircase to the gap. Same placement
                    // search, level gates and ledger as pool contracts; the
                    // per-gap RNG keeps synthesis independent of the shared
                    // draw stream (decision 18).
                    if (!TrySynthesizeActiveStairTransition(
                            dungeonSeed,
                            connection.fromRoom,
                            connection.toRoom,
                            delta,
                            path,
                            zones.NodeArea(layout.rooms, fromNode),
                            zones.NodeArea(layout.rooms, toNode),
                            layout.floorCells,
                            cellLevels,
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
                            out synthesizedGapId) &&
                        // Third tier (decision 27): a 180-degree tower on void
                        // cells beside the path, only when nothing in-corridor fit.
                        !TrySynthesizeStairwellTransition(
                            dungeonSeed,
                            connection.fromRoom,
                            connection.toRoom,
                            delta,
                            path,
                            zones.NodeArea(layout.rooms, fromNode),
                            zones.NodeArea(layout.rooms, toNode),
                            layout.floorCells,
                            cellLevels,
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
                            out synthesizedGapId))
                    {
                        rejectionReason = $"connection {connection.fromRoom}->{connection.toRoom} had no reviewed active stair contract placement for rise {delta}, lane count <= {MaxActiveStairLaneCount}; synthesis offered no fitting design";
                        return false;
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
                    // Register the footprint as gap cells with a conservative
                    // floored-linear deck height for the final pass-under gate;
                    // Manhattan distance equals deck-walk distance on an L. The
                    // replaced corridor cells below stay gaps but carry no deck.
                    int spanLength = Mathf.Abs(upperLandingCell.x - lowerLandingCell.x) +
                        Mathf.Abs(upperLandingCell.y - lowerLandingCell.y);
                    foreach (Vector2Int deckCell in stairOptionPlannedFootprintCells)
                    {
                        externalSpanGapCells.Add(deckCell);
                        int deckDistance = Mathf.Abs(deckCell.x - lowerLandingCell.x) +
                            Mathf.Abs(deckCell.y - lowerLandingCell.y);
                        int deckLevel = Mathf.FloorToInt(Mathf.Lerp(
                            Mathf.Min(fromLevel, toLevel),
                            Mathf.Max(fromLevel, toLevel),
                            spanLength > 0 ? (float)deckDistance / spanLength : 0f));
                        if (!spanDeckLevels.TryGetValue(deckCell, out int existingDeck) || deckLevel < existingDeck)
                        {
                            spanDeckLevels[deckCell] = deckLevel;
                        }
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
                    if (!TrySetCellLevel(cellLevels, path[i], targetLevel, out rejectionReason))
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
                            stripLowerCell,
                            seamStairPrefabPath,
                            SeamStairPlacementClass));
                        plannedStairLedger.Register(
                            new[] { stripLowerCell },
                            Array.Empty<Vector2Int>(),
                            new[] { stripRaisedCell });
                    }
                }

                if (delta > 1)
                {
                    int lowerLevel = Mathf.Min(fromLevel, toLevel);
                    int higherLevel = Mathf.Max(fromLevel, toLevel);
                    if (!TrySetPlannedStairCells(
                            cellLevels,
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
                        stairOptionPlannedFootprintCells,
                        stairOptionPlannedLowerLandingCells,
                        stairOptionPlannedUpperLandingCells);
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
                                stairOptionPlannedTransitionSecondCell,
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
                                stairOptionPlannedTransitionSecondCell,
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
            }

            // Aerial loop bridges (step 8, decisions 29-31): after every corridor
            // has leveled its cells, equal-level room pairs with a straight clear
            // line between facing boundary cells may gain a flat deck — a new
            // loop edge that is a transition + port-graph edge, never a corridor
            // (no leveling, no doorway; the deck lands ON TOP of the walls).
            // Runs before the fill pass: overflown corridor cells filled later
            // are still validated by the deck-level headroom gate below.
            AddAerialBridges(
                layout,
                cellLevels,
                random,
                transitions,
                transitionKeys,
                plannedStairLedger,
                spanDeckLevels,
                synthesizedStairs);

            FillUnassignedFloorCells(layout.floorCells, cellLevels, externalSpanGapCells);
            if (!TryValidateSpanHeadroom(cellLevels, spanDeckLevels, out rejectionReason))
            {
                return false;
            }

            // Dais platforms (step 9, decision 37) carve last, over the finished
            // cell field: every corridor, bridge and headroom decision is already
            // made, so a dais can only decorate — never reject or re-roll.
            int backedDaisCount = CarveDaisPlatforms(
                layout,
                dungeonSeed,
                cellLevels,
                transitions,
                transitionKeys,
                plannedStairLedger,
                doorwayCells,
                seamStairPrefabPath,
                daisShowpieces,
                CurrentGenerationSettings);

            // Decision 43(a): runs after every other level-field feature so
            // it sweeps the FINAL field.
            int sweep1uCount = SweepIntraRoom1uDrops(
                layout,
                cellLevels,
                transitions,
                transitionKeys,
                plannedStairLedger,
                doorwayCells,
                seamStairPrefabPath);

            // Decision J: promontory piers jut out into the void at the end of the
            // level-field build, so they read the final cell levels (and never
            // collide with stairs/dais, which are all placed by now).
            int promontoryCount = ChoosePromontorySpurs(layout, cellLevels, dungeonSeed, random, promontoryCells, CurrentGenerationSettings);

            stairCandidateSummary = FormatStairCandidateHistogram(stairCandidateCounts);

            if (backedDaisCount > 0)
            {
                stairCandidateSummary += $" backedDais:{backedDaisCount}";
            }

            if (promontoryCount > 0)
            {
                stairCandidateSummary += $" promontory:{promontoryCount}";
            }

            foreach (DaisShowpiece showpiece in daisShowpieces)
            {
                stairCandidateSummary += $" showpiece:{showpiece.designName}@{showpiece.originCell.x}_{showpiece.originCell.y}";
            }

            if (sweep1uCount > 0)
            {
                stairCandidateSummary += $" sweep1u:{sweep1uCount}";
            }

            return true;
        }

        // Decision J: carve 0-2 promontory piers. Each is a straight 1-cell-wide
        // strip of floor extending from a large room's void-facing boundary out
        // into the open void, at that boundary cell's level. Added to cellLevels
        // only (not a room) — walkable and reachable via floor-adjacency, with
        // decision C's cliffs dropping every exposed side to the abyss. Per-room
        // RNG keeps the choice independent of the shared draw stream (the dais
        // pattern), so adding/removing the feature never reshuffles other rooms.
        private static int ChoosePromontorySpurs(
            DungeonLayout layout,
            Dictionary<Vector2Int, int> cellLevels,
            int dungeonSeed,
            System.Random random,
            List<Vector2Int> promontoryCells,
            DungeonGenerationSettings settings)
        {
            int placed = 0;
            for (int roomIndex = 0; roomIndex < layout.rooms.Count && placed < MaxPromontoriesPerDungeon; roomIndex++)
            {
                if (layout.rooms[roomIndex].Area < settings.largeRoomMinAreaCells)
                {
                    continue;
                }

                var spurRandom = new System.Random(dungeonSeed ^ StairForge.StableHash($"promontory:{roomIndex}"));
                if (spurRandom.NextDouble() >= settings.promontoryChancePerRoom)
                {
                    continue;
                }

                int length = spurRandom.Next(settings.promontoryMinLengthCells, settings.promontoryMaxLengthCells + 1);

                // Collect every void-facing boundary cell whose outward run stays
                // void for the full length, then pick one deterministically.
                var candidates = new List<(Vector2Int start, Vector2Int direction, int level)>();
                foreach (Vector2Int cell in layout.rooms[roomIndex].CellsRowMajor())
                {
                    if (!cellLevels.TryGetValue(cell, out int level))
                    {
                        continue;
                    }

                    foreach (Vector2Int direction in new[] { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left })
                    {
                        if (cellLevels.ContainsKey(cell + direction) ||
                            !VoidRunFits(cellLevels, cell, direction, length))
                        {
                            continue;
                        }

                        candidates.Add((cell, direction, level));
                    }
                }

                if (candidates.Count == 0)
                {
                    continue;
                }

                (Vector2Int start, Vector2Int direction, int level) chosen = candidates[spurRandom.Next(candidates.Count)];
                for (int i = 1; i <= length; i++)
                {
                    Vector2Int spurCell = chosen.start + chosen.direction * i;
                    cellLevels[spurCell] = chosen.level;
                    promontoryCells.Add(spurCell);
                }

                placed++;
            }

            return placed;
        }

        // The `length` cells stepping out from `cell` in `direction` are all void
        // (and the flanks of the first cell too, so the pier starts genuinely
        // exposed rather than hugging the room's outer wall).
        private static bool VoidRunFits(
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            Vector2Int cell,
            Vector2Int direction,
            int length)
        {
            var lateral = new Vector2Int(-direction.y, direction.x);
            Vector2Int first = cell + direction;
            if (cellLevels.ContainsKey(first + lateral) || cellLevels.ContainsKey(first - lateral))
            {
                return false;
            }

            for (int i = 1; i <= length; i++)
            {
                if (cellLevels.ContainsKey(cell + direction * i))
                {
                    return false;
                }
            }

            return true;
        }

        // Step 9, decision 37: a dais is a cosmetic interior 1u platform — an
        // interior rect raised one level inside an UNSPLIT room, ringed by the
        // same 1u step strips as zone seams (the gold scene's throne dais: the
        // rim IS walkable steps, so rule 3 holds with no exceptions; ring
        // corners stay bare per the ledge policy until the round corner pieces
        // are measured). The dais is not a level-plan node: it draws from a
        // per-room RNG (the forge per-request pattern — other features never
        // reshuffle a dais decision), and a dais that fails any check is
        // skipped outright, never re-rolled. Flat dungeons are left untouched
        // so the single-level rejection gate keeps its meaning.
        private static int CarveDaisPlatforms(
            DungeonLayout layout,
            int dungeonSeed,
            Dictionary<Vector2Int, int> cellLevels,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            HashSet<string> transitionKeys,
            StairPlacementLedger plannedStairLedger,
            HashSet<Vector2Int> doorwayCells,
            string seamStairPrefabPath,
            List<DaisShowpiece> showpieces,
            DungeonGenerationSettings settings)
        {
            if (CountDistinctLevels(cellLevels) <= 1)
            {
                return 0;
            }

            var splitRooms = new HashSet<int>();
            foreach (RoomZonePlan zonePlan in layout.roomZones)
            {
                splitRooms.Add(zonePlan.roomIndex);
            }

            // Corridor paths cross room interiors; a raised path cell would break
            // the corridor's level continuity, so the dais body avoids every path
            // cell (raw paths — a superset of the cleaned walk is the safe side).
            var pathCells = new HashSet<Vector2Int>();
            foreach (RoomConnection connection in layout.connections)
            {
                foreach (Vector2Int cell in connection.path)
                {
                    pathCells.Add(cell);
                }
            }

            int placed = 0;
            int backedPlaced = 0;
            for (int roomIndex = 0; roomIndex < layout.rooms.Count && placed < MaxDaisPerDungeon; roomIndex++)
            {
                if (splitRooms.Contains(roomIndex))
                {
                    continue;
                }

                RoomFootprint room = layout.rooms[roomIndex];
                RectInt roomBox = room.bounds;
                int interiorWidth = roomBox.width - 2;
                int interiorDepth = roomBox.height - 2;
                if (interiorWidth < 1 || interiorDepth < 1)
                {
                    continue;
                }

                var daisRandom = new System.Random(dungeonSeed ^ StairForge.StableHash($"dais:{roomIndex}"));
                if (daisRandom.NextDouble() >= settings.daisChancePerRoom)
                {
                    continue;
                }

                // Variant draws (decision 41, gallery-approved constructions).
                // Every draw happens unconditionally so adding eligibility
                // rules later never reshuffles another room's dais.
                bool sunken = daisRandom.NextDouble() < DaisSunkenChance;
                int rise = daisRandom.NextDouble() < DaisSteepChance ? 2 : 1;
                bool tiered = daisRandom.NextDouble() < DaisTieredChance;
                // Pits need a 2x2 bowl minimum; a second tier needs a 3x3 base
                // and only stacks on raised rise-1 (the approved design set).
                if (sunken && (interiorWidth < 2 || interiorDepth < 2))
                {
                    sunken = false;
                }

                tiered = tiered && !sunken && rise == 1 && interiorWidth >= 3 && interiorDepth >= 3;
                int minSize = sunken ? 2 : tiered ? 3 : 1;
                int maxSize = tiered ? 3 : MaxDaisSpanCells;

                // Interior rect with >= 1 cell margin on every side: doorway cells
                // live on the room boundary, so they can never be dais cells.
                int width = minSize + daisRandom.Next(Mathf.Min(maxSize, interiorWidth) - minSize + 1);
                int depth = minSize + daisRandom.Next(Mathf.Min(maxSize, interiorDepth) - minSize + 1);
                var daisRect = new RectInt(
                    roomBox.xMin + 1 + daisRandom.Next(interiorWidth - width + 1),
                    roomBox.yMin + 1 + daisRandom.Next(interiorDepth - depth + 1),
                    width,
                    depth);

                // Backed placement (decisions 44+46): every backed draw is
                // APPENDED after the existing draws so current dais stay
                // byte-identical; eligibility never reshuffles anything. A
                // raised non-tiered dais tries the four wall sides starting
                // from a rolled one; if none carves, the interior rect
                // already drawn above is the fallback.
                bool backedRoll = daisRandom.NextDouble() < DaisBackedChance;
                int sideRoll = daisRandom.Next(4);
                int alongRoll = 1 + daisRandom.Next(BackedDaisMaxAlongWall);
                int deepRoll = 1 + daisRandom.Next(MaxDaisSpanCells);
                double offsetRoll = daisRandom.NextDouble();
                // Increment-2 draws, appended after the increment-1 draws so
                // every committed dais outcome stays byte-identical.
                int showpieceKindRoll = daisRandom.Next(2);
                int showpieceStyleRoll = daisRandom.Next(2);
                bool carved = false;
                if (backedRoll && !sunken && !tiered)
                {
                    // Showpiece pass first (decision 46: ALWAYS when it
                    // fits): a wall side hosting the full 5x3 footprint at
                    // uniform level, clear of paths/doorways/reservations,
                    // with VOID behind all five wall cells (a true outer
                    // wall — showpieces need a wall backdrop, not a cliff
                    // or another room).
                    string showpieceName = showpieceKindRoll == 0
                        ? $"dais_backed_{(showpieceStyleRoll == 0 ? "angle" : "round")}_bay_r1"
                        : "dais_gold_backed_scallop";
                    for (int sideStep = 0; sideStep < 4 && !carved; sideStep++)
                    {
                        int side = (sideRoll + sideStep) & 3;
                        Vector2Int backDirection = side == 0 ? Vector2Int.up
                            : side == 1 ? Vector2Int.right
                            : side == 2 ? Vector2Int.down
                            : Vector2Int.left;
                        bool alongX = backDirection.x == 0;
                        // Non-rect rooms: the wall row is the room's true
                        // outline facing this side, not the bbox edge — take
                        // the longest straight boundary run.
                        (Vector2Int start, int length) run = LongestBoundaryRun(room, backDirection);
                        if (run.length < ShowpieceAlongCells + 2)
                        {
                            continue;
                        }

                        int wallLine = alongX ? run.start.y : run.start.x;
                        int alongStart = (alongX ? run.start.x : run.start.y) + 1 +
                            (int)(offsetRoll * (run.length - 2 - ShowpieceAlongCells + 1));
                        bool eligible = true;
                        bool hasPlatformLevel = false;
                        int platformLevel = 0;
                        var coveredCells = new List<Vector2Int>(ShowpieceAlongCells * ShowpieceDepthCells);
                        for (int a = 0; a < ShowpieceAlongCells && eligible; a++)
                        {
                            Vector2Int wallCell = alongX
                                ? new Vector2Int(alongStart + a, wallLine)
                                : new Vector2Int(wallLine, alongStart + a);
                            if (cellLevels.ContainsKey(wallCell + backDirection))
                            {
                                eligible = false;
                                break;
                            }

                            for (int r = 0; r < ShowpieceDepthCells && eligible; r++)
                            {
                                Vector2Int cell = wallCell - backDirection * r;
                                if (!room.Contains(cell) ||
                                    !cellLevels.TryGetValue(cell, out int cellLevel) ||
                                    pathCells.Contains(cell) ||
                                    doorwayCells.Contains(cell) ||
                                    plannedStairLedger.footprintCells.Contains(cell) ||
                                    plannedStairLedger.landingCells.Contains(cell))
                                {
                                    eligible = false;
                                    break;
                                }

                                if (!hasPlatformLevel)
                                {
                                    platformLevel = cellLevel;
                                    hasPlatformLevel = true;
                                }
                                else if (cellLevel != platformLevel)
                                {
                                    eligible = false;
                                    break;
                                }

                                coveredCells.Add(cell);
                            }
                        }

                        if (!eligible)
                        {
                            continue;
                        }

                        if (!StairForge.TryGetBackedShowpieceDesign(showpieceName, out ElevationEdgeModel.SynthesizedPiecePlacement[] showpiecePieces))
                        {
                            break;
                        }

                        plannedStairLedger.Register(coveredCells.ToArray(), Array.Empty<Vector2Int>(), Array.Empty<Vector2Int>());
                        Vector2Int originCell;
                        float showpieceYaw;
                        if (backDirection == Vector2Int.up)
                        {
                            originCell = new Vector2Int(alongStart, wallLine - 1);
                            showpieceYaw = 0f;
                        }
                        else if (backDirection == Vector2Int.down)
                        {
                            originCell = new Vector2Int(alongStart + ShowpieceAlongCells, wallLine + 2);
                            showpieceYaw = 180f;
                        }
                        else if (backDirection == Vector2Int.right)
                        {
                            originCell = new Vector2Int(wallLine - 1, alongStart + ShowpieceAlongCells);
                            showpieceYaw = 90f;
                        }
                        else
                        {
                            originCell = new Vector2Int(wallLine + 2, alongStart);
                            showpieceYaw = 270f;
                        }

                        showpieces.Add(new DaisShowpiece
                        {
                            designName = showpieceName,
                            originCell = originCell,
                            yawDegrees = showpieceYaw,
                            roomLevel = platformLevel,
                            pieces = showpiecePieces,
                        });
                        carved = true;
                        backedPlaced++;
                    }

                    for (int sideStep = 0; sideStep < 4 && !carved; sideStep++)
                    {
                        int side = (sideRoll + sideStep) & 3;
                        Vector2Int backDirection = side == 0 ? Vector2Int.up
                            : side == 1 ? Vector2Int.right
                            : side == 2 ? Vector2Int.down
                            : Vector2Int.left;
                        bool alongX = backDirection.x == 0;
                        (Vector2Int start, int length) run = LongestBoundaryRun(room, backDirection);
                        int depthSpan = alongX ? roomBox.height : roomBox.width;
                        int along = Mathf.Min(alongRoll, run.length - 2);
                        int deep = Mathf.Min(deepRoll, depthSpan - 1);
                        if (along < 1 || deep < 1)
                        {
                            continue;
                        }

                        int wallLine = alongX ? run.start.y : run.start.x;
                        int alongOffset = (alongX ? run.start.x : run.start.y) + 1 +
                            (int)(offsetRoll * (run.length - 2 - along + 1));
                        RectInt backedRect =
                            backDirection == Vector2Int.up ? new RectInt(alongOffset, wallLine - deep + 1, along, deep)
                            : backDirection == Vector2Int.down ? new RectInt(alongOffset, wallLine, along, deep)
                            : backDirection == Vector2Int.right ? new RectInt(wallLine - deep + 1, alongOffset, deep, along)
                            : new RectInt(wallLine, alongOffset, deep, along);
                        carved = TryCarveSingleDais(
                            room,
                            backedRect,
                            sunken: false,
                            rise,
                            tiered: false,
                            cellLevels,
                            pathCells,
                            doorwayCells,
                            plannedStairLedger,
                            transitions,
                            transitionKeys,
                            seamStairPrefabPath,
                            backDirection);
                        if (carved)
                        {
                            backedPlaced++;
                        }
                    }
                }

                if (!carved)
                {
                    carved = TryCarveSingleDais(
                        room,
                        daisRect,
                        sunken,
                        rise,
                        tiered,
                        cellLevels,
                        pathCells,
                        doorwayCells,
                        plannedStairLedger,
                        transitions,
                        transitionKeys,
                        seamStairPrefabPath,
                        default);
                }

                if (carved)
                {
                    placed++;
                }
            }

            return backedPlaced;
        }

        // Decision 43(a): a 1u drop WITHIN one room always climbs. Every
        // intra-room delta-1 adjacency not already carrying a transition
        // takes a dais-class strip — the full band, so the existing corner
        // machinery dresses its turns (notches at convex turns, concave
        // sweeps at inside turns). Doorway and stair-reservation faces stay
        // the walled fallback per the decision; inter-room delta-1 edges
        // (43b) keep their walls because the sweep never crosses rooms.
        private static int SweepIntraRoom1uDrops(
            DungeonLayout layout,
            Dictionary<Vector2Int, int> cellLevels,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            HashSet<string> transitionKeys,
            StairPlacementLedger plannedStairLedger,
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
                        if (!cellLevels.TryGetValue(cell, out int cellLevel))
                        {
                            continue;
                        }

                        foreach (Vector2Int step in new[] { Vector2Int.right, Vector2Int.up })
                        {
                            Vector2Int neighbor = cell + step;
                            if (!cellLevels.TryGetValue(neighbor, out int neighborLevel) ||
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
                                plannedStairLedger.footprintCells.Contains(lowerCell) ||
                                plannedStairLedger.landingCells.Contains(lowerCell) ||
                                plannedStairLedger.footprintCells.Contains(upperCell) ||
                                plannedStairLedger.landingCells.Contains(upperCell))
                            {
                                continue;
                            }

                            if (!transitionKeys.Add(TransitionKey(upperCell, lowerCell)))
                            {
                                continue;
                            }

                            transitions.Add(new ElevationEdgeModel.TransitionEdge(
                                upperCell,
                                lowerCell,
                                seamStairPrefabPath,
                                DaisStairPlacementClass));
                            plannedStairLedger.Register(
                                new[] { lowerCell },
                                Array.Empty<Vector2Int>(),
                                new[] { upperCell });
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

        // backDirection (decisions 44+46): non-zero for a BACKED dais flush
        // against a room wall on that side — the back face emits no strips
        // (cells beyond the boundary belong to other features; a corridor
        // hugging the far side of the wall must never gain a dais strip)
        // and rise-2 corner sweeps exist only at the front corners.
        // The longest straight outline run facing `direction` (ties keep the
        // first in BoundaryRuns' deterministic order) — the non-rect room's
        // stand-in for "the wall side" in backed dais and showpiece placement.
        private static (Vector2Int start, int length) LongestBoundaryRun(RoomFootprint room, Vector2Int direction)
        {
            (Vector2Int start, int length) best = (default, 0);
            foreach ((Vector2Int start, int length) run in room.BoundaryRuns(direction))
            {
                if (run.length > best.length)
                {
                    best = run;
                }
            }

            return best;
        }

        private static bool TryCarveSingleDais(
            RoomFootprint room,
            RectInt daisRect,
            bool sunken,
            int rise,
            bool tiered,
            Dictionary<Vector2Int, int> cellLevels,
            HashSet<Vector2Int> pathCells,
            HashSet<Vector2Int> doorwayCells,
            StairPlacementLedger plannedStairLedger,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            HashSet<string> transitionKeys,
            string seamStairPrefabPath,
            Vector2Int backDirection)
        {
            int daisLevel = 0;
            bool hasLevel = false;
            for (int z = daisRect.yMin; z < daisRect.yMax; z++)
            {
                for (int x = daisRect.xMin; x < daisRect.xMax; x++)
                {
                    var cell = new Vector2Int(x, z);
                    // Non-rect rooms: every dais cell must belong to THIS room
                    // — a bbox-rolled rect can reach into a notch occupied by
                    // void, a corridor, or another room at the same level.
                    if (!room.Contains(cell) ||
                        !cellLevels.TryGetValue(cell, out int level) ||
                        pathCells.Contains(cell) ||
                        doorwayCells.Contains(cell) ||
                        plannedStairLedger.footprintCells.Contains(cell) ||
                        plannedStairLedger.landingCells.Contains(cell))
                    {
                        return false;
                    }

                    if (!hasLevel)
                    {
                        daisLevel = level;
                        hasLevel = true;
                    }
                    else if (level != daisLevel)
                    {
                        return false;
                    }
                }
            }

            // A pit floor never sinks below the ground plane.
            if (sunken && daisLevel < rise)
            {
                return false;
            }

            string stripPrefabPath = rise == 1 ? seamStairPrefabPath : ResolveSteepDaisStairPrefabPath();

            // Rise-2 raised rims put a full-cell corner sweep on each diagonal
            // ring cell (gallery construction); those cells must be clean floor
            // at the base level and register as ledger footprint so contract
            // stairs stay off the sweep.
            var sweepCells = new List<Vector2Int>();
            if (!sunken && rise == 2)
            {
                foreach (Vector2Int diagonal in new[]
                         {
                             new Vector2Int(daisRect.xMin - 1, daisRect.yMin - 1),
                             new Vector2Int(daisRect.xMax, daisRect.yMin - 1),
                             new Vector2Int(daisRect.xMin - 1, daisRect.yMax),
                             new Vector2Int(daisRect.xMax, daisRect.yMax)
                         })
                {
                    // Backed: the wall-side corners take no sweeps.
                    int cornerSignX = diagonal.x < daisRect.xMin ? -1 : 1;
                    int cornerSignZ = diagonal.y < daisRect.yMin ? -1 : 1;
                    if ((backDirection.x != 0 && cornerSignX == backDirection.x) ||
                        (backDirection.y != 0 && cornerSignZ == backDirection.y))
                    {
                        continue;
                    }

                    if (!room.Contains(diagonal) ||
                        !cellLevels.TryGetValue(diagonal, out int diagonalLevel) ||
                        diagonalLevel != daisLevel ||
                        pathCells.Contains(diagonal) ||
                        doorwayCells.Contains(diagonal) ||
                        plannedStairLedger.footprintCells.Contains(diagonal) ||
                        plannedStairLedger.landingCells.Contains(diagonal))
                    {
                        return false;
                    }

                    sweepCells.Add(diagonal);
                }
            }

            // Rim strips on every eligible ring face. The strip's geometry
            // lives on the LOWER side of the edge — the ring cell for a raised
            // dais, the pit cell for a sunken one — and that lower cell
            // registers as ledger FOOTPRINT exactly like a seam strip. Doorway
            // ring cells are skipped per rule 24; at least one strip must
            // survive or the platform would be unreachable (pit corner cells
            // render as concave sweeps instead of strips, but their transitions
            // still carry the port-graph connectivity).
            var strips = new List<(Vector2Int upperCell, Vector2Int lowerCell)>();
            int exposedFaces = 0;
            for (int z = daisRect.yMin; z < daisRect.yMax; z++)
            {
                for (int x = daisRect.xMin; x < daisRect.xMax; x++)
                {
                    var daisCell = new Vector2Int(x, z);
                    foreach (Vector2Int ringCell in CardinalNeighbors(daisCell))
                    {
                        if (daisRect.Contains(ringCell))
                        {
                            continue;
                        }

                        if (backDirection != Vector2Int.zero && ringCell - daisCell == backDirection)
                        {
                            continue;
                        }

                        exposedFaces++;
                        // An out-of-room ring cell can never take a strip; it
                        // stays a bare exposed face, so the closed-band rule
                        // below correctly rejects the carve.
                        if (!room.Contains(ringCell) ||
                            !cellLevels.TryGetValue(ringCell, out int ringLevel) ||
                            ringLevel != daisLevel ||
                            doorwayCells.Contains(ringCell) ||
                            plannedStairLedger.footprintCells.Contains(ringCell) ||
                            plannedStairLedger.landingCells.Contains(ringCell))
                        {
                            continue;
                        }

                        strips.Add(sunken ? (ringCell, daisCell) : (daisCell, ringCell));
                    }
                }
            }

            if (strips.Count == 0)
            {
                return false;
            }

            // The band model (decision 45), planning-side, universal (user
            // rule 2026-06-13: "don't show a dais at all if there is no
            // space for it"). Two in-scene defects established it: a 2x2
            // pit rendered with three corner sweeps (a rim face was
            // ledger-blocked, its corner cell lost its second face, the
            // bowl broke), then a backed dais with a bare front face (its
            // ring cell sat at another tier's level). A dais carves only
            // when its rim is CLOSED — every exposed face takes a strip;
            // the suppressed back face of a backed dais is wall-terminated
            // and exempt by construction.
            if (strips.Count < exposedFaces)
            {
                return false;
            }

            int carvedLevel = daisLevel + (sunken ? -rise : rise);
            for (int z = daisRect.yMin; z < daisRect.yMax; z++)
            {
                for (int x = daisRect.xMin; x < daisRect.xMax; x++)
                {
                    cellLevels[new Vector2Int(x, z)] = carvedLevel;
                }
            }

            // Second tier (raised rise-1 3x3 base only): the inner cell climbs
            // one more level and rims against the first tier with plain 1u
            // strips — just more dais transitions; the render derives the rest.
            if (tiered)
            {
                var inner = new RectInt(daisRect.xMin + 1, daisRect.yMin + 1, daisRect.width - 2, daisRect.height - 2);
                for (int z = inner.yMin; z < inner.yMax; z++)
                {
                    for (int x = inner.xMin; x < inner.xMax; x++)
                    {
                        var cell = new Vector2Int(x, z);
                        cellLevels[cell] = carvedLevel + 1;
                        foreach (Vector2Int tierRing in CardinalNeighbors(cell))
                        {
                            if (!inner.Contains(tierRing) && daisRect.Contains(tierRing))
                            {
                                strips.Add((cell, tierRing));
                            }
                        }
                    }
                }
            }

            foreach ((Vector2Int upperCell, Vector2Int lowerCell) in strips)
            {
                if (!transitionKeys.Add(TransitionKey(upperCell, lowerCell)))
                {
                    continue;
                }

                int stripDelta = Mathf.Abs(cellLevels[upperCell] - cellLevels[lowerCell]);
                transitions.Add(new ElevationEdgeModel.TransitionEdge(
                    upperCell,
                    lowerCell,
                    stripDelta == 1 ? seamStairPrefabPath : stripPrefabPath,
                    DaisStairPlacementClass));
                plannedStairLedger.Register(
                    new[] { lowerCell },
                    Array.Empty<Vector2Int>(),
                    new[] { upperCell });
            }

            foreach (Vector2Int sweepCell in sweepCells)
            {
                plannedStairLedger.Register(new[] { sweepCell }, Array.Empty<Vector2Int>(), Array.Empty<Vector2Int>());
            }

            return true;
        }

        // Headroom gate (design decision 2): at least MinHeadroomLevels u of clearance
        // between any walkable surface and geometry above it. Today the only stacked
        // geometry the planner produces is a bridge deck over a pass-under cell;
        // embedded stairs own their cells outright. Forge candidates and overhangs
        // get the same gate when they exist.
        private static bool TryValidateSpanHeadroom(
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            Dictionary<Vector2Int, int> spanDeckLevels,
            out string rejectionReason)
        {
            rejectionReason = string.Empty;
            if (spanDeckLevels.Count == 0)
            {
                return true;
            }

            var spanCells = new List<Vector2Int>(spanDeckLevels.Keys);
            spanCells.Sort(CompareCells);
            foreach (Vector2Int cell in spanCells)
            {
                if (!cellLevels.TryGetValue(cell, out int floorLevel))
                {
                    continue;
                }

                int clearance = spanDeckLevels[cell] - floorLevel;
                if (clearance < MinHeadroomLevels)
                {
                    rejectionReason =
                        $"bridge span over cell ({cell.x}, {cell.y}) left only {clearance}u headroom above the walkable floor (minimum {MinHeadroomLevels}u)";
                    return false;
                }
            }

            return true;
        }

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

        private static bool TryBuildRoomBoundaryContext(
            DungeonLayout layout,
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
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
            List<ElevationEdgeModel.DoorwayEdge> doorways = BuildDoorwayEdges(layout, cellLevels);
            List<ElevationEdgeModel.InternalPathEdge> internalPathEdges = BuildInternalPathEdges(layout, cellLevels, cellRoomIds, transitions);
            bool[] enclosedRooms = ChooseEnclosedRooms(layout.rooms.Count, random);
            DemoteSealedEnclosedRooms(enclosedRooms, doorways, cellRoomIds);
            if (!ValidateEnclosedRoomDoorways(enclosedRooms, doorways, cellRoomIds, out rejectionReason))
            {
                return false;
            }

            context = new ElevationEdgeModel.RoomBoundaryContext(cellRoomIds, enclosedRooms, doorways, internalPathEdges);
            return true;
        }

        private static List<ElevationEdgeModel.InternalPathEdge> BuildInternalPathEdges(
            DungeonLayout layout,
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            IReadOnlyDictionary<Vector2Int, int> cellRoomIds,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            var edges = new Dictionary<WallEdge, bool>();
            HashSet<Vector2Int> blockedCells = BuildInternalPathBlockedCells(transitions);
            for (int connectionIndex = 0; connectionIndex < layout.connections.Count; connectionIndex++)
            {
                RoomConnection connection = layout.connections[connectionIndex];
                List<Vector2Int> path = CleanPath(connection.path, layout.floorCells);
                AddInternalPathRuns(path, connectionIndex, cellLevels, cellRoomIds, blockedCells, edges);
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
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
                    IsInternalPathCell(first, cellLevels, cellRoomIds, blockedCells) &&
                    IsInternalPathCell(second, cellLevels, cellRoomIds, blockedCells);

                if (!validStep)
                {
                    AddInternalPathRun(run, runDirection, connectionIndex, cellLevels, cellRoomIds, blockedCells, edges);
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

                AddInternalPathRun(run, runDirection, connectionIndex, cellLevels, cellRoomIds, blockedCells, edges);
                run.Clear();
                run.Add(first);
                run.Add(second);
                runDirection = direction;
            }

            AddInternalPathRun(run, runDirection, connectionIndex, cellLevels, cellRoomIds, blockedCells, edges);
        }

        private static bool IsInternalPathCell(
            Vector2Int cell,
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            IReadOnlyDictionary<Vector2Int, int> cellRoomIds,
            HashSet<Vector2Int> blockedCells)
        {
            return cellLevels.ContainsKey(cell) && !cellRoomIds.ContainsKey(cell) && !blockedCells.Contains(cell);
        }

        private static void AddInternalPathRun(
            IReadOnlyList<Vector2Int> run,
            Vector2Int runDirection,
            int connectionIndex,
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
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
                    TryAddInternalPathEdge(cell, sideDirection, railing, cellLevels, cellRoomIds, blockedCells, edges);
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            IReadOnlyDictionary<Vector2Int, int> cellRoomIds,
            HashSet<Vector2Int> blockedCells,
            Dictionary<WallEdge, bool> edges)
        {
            if (!cellLevels.TryGetValue(cell, out int level))
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

            if (cellLevels.TryGetValue(neighbor, out int neighborLevel) && neighborLevel == level)
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

        // cellLevels filters STALE doorways (render path): a doorway is a walk
        // opening, so if either side lost its floor — the corridor was replaced
        // by a bridge span — the gap must not be cut into the room's enclosure
        // wall (seen in-editor 2026-06-11: a lone railing in a partition gap
        // opening onto the span void). Planning passes null: the no-strip-in-
        // doorway rule applies to every path crossing regardless of leveling.
        private static List<ElevationEdgeModel.DoorwayEdge> BuildDoorwayEdges(
            DungeonLayout layout,
            IReadOnlyDictionary<Vector2Int, int> cellLevels)
        {
            var doorways = new List<ElevationEdgeModel.DoorwayEdge>();
            var keys = new HashSet<string>();
            foreach (RoomConnection connection in layout.connections)
            {
                List<Vector2Int> path = CleanPath(connection.path, layout.floorCells);
                AddRoomDoorwayEdge(layout.rooms[connection.fromRoom], path, cellLevels, keys, doorways);
                AddRoomDoorwayEdge(layout.rooms[connection.toRoom], path, cellLevels, keys, doorways);
            }

            return doorways;
        }

        private static void AddRoomDoorwayEdge(
            RoomFootprint room,
            IReadOnlyList<Vector2Int> path,
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
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

                if (cellLevels != null &&
                    (!cellLevels.ContainsKey(path[i]) || !cellLevels.ContainsKey(path[i + 1])))
                {
                    return;
                }

                string key = TransitionKey(path[i], path[i + 1]);
                if (keys.Add(key))
                {
                    doorways.Add(new ElevationEdgeModel.DoorwayEdge(path[i], path[i + 1]));
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
            var enclosed = new bool[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                enclosed[i] = random.NextDouble() < EnclosedRoomChance;
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

            if (!Any(enclosed, expected: false))
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            int fromLevel,
            int toLevel,
            System.Random random,
            bool allowExternalSpan,
            SortedDictionary<string, int> stairCandidateCounts,
            StairPlacementLedger plannedStairLedger,
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
                cellLevels,
                Mathf.Min(fromLevel, toLevel),
                Mathf.Max(fromLevel, toLevel),
                allowExternalSpan,
                preferredOnly: true,
                candidates);
            RemovePlannedStairConflicts(candidates, plannedStairLedger);
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
                    cellLevels,
                    Mathf.Min(fromLevel, toLevel),
                    Mathf.Max(fromLevel, toLevel),
                    allowExternalSpan,
                    preferredOnly: false,
                    candidates);
                RemovePlannedStairConflicts(candidates, plannedStairLedger);
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            AccumulateStairCandidateCounts(candidates, stairCandidateCounts);
            StairTransitionCandidate candidate = ChooseStairTransitionCandidate(candidates, random);
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            int fromLevel,
            int toLevel,
            SortedDictionary<string, int> stairCandidateCounts,
            StairPlacementLedger plannedStairLedger,
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

            var options = new List<ReviewedActiveStairOption>();
            var designsByName = new Dictionary<string, StairForge.SynthesizedStaircaseDesign>(StringComparer.Ordinal);
            foreach (StairForge.SynthesizedStaircaseDesign design in designs)
            {
                string parserError = ElevationEdgeModel.ValidateSynthesizedContractToken(design.contract, StairForge.LevelHeight);
                if (!string.IsNullOrEmpty(parserError))
                {
                    LogPlanningWarning($"Dungeon Lab Generate: synthesized design '{design.name}' rejected by the edge-model parser: {parserError}");
                    continue;
                }

                if (!TryBuildSynthesizedStairOption(design.contract, out ReviewedActiveStairOption option, out string optionError))
                {
                    LogPlanningWarning($"Dungeon Lab Generate: synthesized design '{design.name}' rejected by the planner parser: {optionError}");
                    continue;
                }

                options.Add(option);
                designsByName[option.name] = design;
            }

            if (options.Count == 0)
            {
                return false;
            }

            // Determinism (decision 18): per-gap RNG keyed by dungeon seed + gap id,
            // so adding or removing gaps never reshuffles another gap's synthesis.
            var gapRandom = new System.Random(dungeonSeed ^ StairForge.StableHash($"synth:{fromRoomIndex}:{toRoomIndex}:{rise}"));
            if (!TryChooseReviewedActiveStairTransition(
                    options,
                    rise,
                    maxLaneCount: MaxActiveStairLaneCount,
                    path,
                    fromNodeRect,
                    toNodeRect,
                    layoutFloorCells,
                    cellLevels,
                    fromLevel,
                    toLevel,
                    gapRandom,
                    // Decision 33: bridge-style designs place as external spans
                    // between landings, on equal terms with embedded designs.
                    allowExternalSpan: true,
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

            StairForge.SynthesizedStaircaseDesign chosen = designsByName[selected.name];
            synthesizedSetPiece = new ElevationEdgeModel.SynthesizedStairSetPiece(chosen.name, chosen.contract, chosen.pieces);
            return true;
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

        // Aerial loop bridges (step 8, decisions 29-32).
        private const int MaxAerialBridgesPerDungeon = 2;
        private const int MinAerialBridgeLevel = 3;
        private const int MinAerialBridgeSpanCells = 2;
        private const int MaxAerialBridgeSpanCells = 8;
        // Decision 32: a bridge must be a genuine shortcut — landings already
        // reachable within factor x (span + 2) walk cells make it redundant —
        // and may not hug a parallel walkway within this level tolerance.
        private const int AerialBridgeShortcutFactor = 3;
        private const int AerialBridgeHugLevelTolerance = 2;
        // Decision 34: aerial endpoints may differ by this much (sloped spans).
        private const int MaxAerialBridgeEndDeltaLevels = 2;

        private readonly struct AerialBridgeCandidate
        {
            public readonly int roomA;
            public readonly int roomB;
            public readonly Vector2Int landingA;
            public readonly Vector2Int landingB;
            public readonly Vector2Int lineDirection;
            public readonly List<Vector2Int> gapCells;

            public AerialBridgeCandidate(int roomA, int roomB, Vector2Int landingA, Vector2Int landingB, Vector2Int lineDirection, List<Vector2Int> gapCells)
            {
                this.roomA = roomA;
                this.roomB = roomB;
                this.landingA = landingA;
                this.landingB = landingB;
                this.lineDirection = lineDirection;
                this.gapCells = gapCells;
            }
        }

        private static void AddAerialBridges(
            DungeonLayout layout,
            Dictionary<Vector2Int, int> cellLevels,
            System.Random random,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            HashSet<string> transitionKeys,
            StairPlacementLedger plannedStairLedger,
            Dictionary<Vector2Int, int> spanDeckLevels,
            List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs)
        {
            // Directly connected pairs already have a walk; bridges are for peers
            // without one (loop edges, decision 6).
            var connectedPairs = new HashSet<(int, int)>();
            foreach (RoomConnection connection in layout.connections)
            {
                int low = Mathf.Min(connection.fromRoom, connection.toRoom);
                int high = Mathf.Max(connection.fromRoom, connection.toRoom);
                connectedPairs.Add((low, high));
            }

            var candidates = new List<AerialBridgeCandidate>();
            for (int roomA = 0; roomA < layout.rooms.Count; roomA++)
            {
                for (int roomB = roomA + 1; roomB < layout.rooms.Count; roomB++)
                {
                    if (connectedPairs.Contains((roomA, roomB)))
                    {
                        continue;
                    }

                    CollectAerialBridgeCandidates(layout, cellLevels, roomA, roomB, candidates);
                }
            }

            int placed = 0;
            var bridgedPairs = new HashSet<(int, int)>();
            while (placed < MaxAerialBridgesPerDungeon && candidates.Count > 0)
            {
                int pick = random.Next(candidates.Count);
                AerialBridgeCandidate candidate = candidates[pick];
                candidates.RemoveAt(pick);
                if (bridgedPairs.Contains((candidate.roomA, candidate.roomB)))
                {
                    continue;
                }

                if (TryPlaceAerialBridge(candidate, transitions, transitionKeys, plannedStairLedger, spanDeckLevels, synthesizedStairs, cellLevels))
                {
                    bridgedPairs.Add((candidate.roomA, candidate.roomB));
                    placed++;
                }
            }
        }

        private static void CollectAerialBridgeCandidates(
            DungeonLayout layout,
            Dictionary<Vector2Int, int> cellLevels,
            int roomA,
            int roomB,
            List<AerialBridgeCandidate> candidates)
        {
            RoomFootprint roomFootprintA = layout.rooms[roomA];
            RoomFootprint roomFootprintB = layout.rooms[roomB];
            RectInt a = roomFootprintA.bounds;
            RectInt b = roomFootprintB.bounds;

            // Horizontal lines: A strictly west of B (or the mirror) with
            // overlapping rows; vertical lines symmetric. Landings are the
            // rooms' true facing EDGE CELLS on each shared line (for non-rect
            // rooms a line may miss a footprint entirely — skip it).
            if (a.xMax < b.xMin)
            {
                for (int z = Mathf.Max(a.yMin, b.yMin); z < Mathf.Min(a.yMax, b.yMax); z++)
                {
                    if (roomFootprintA.TryGetEdgeCellTowards(new Vector2Int(1, 0), z, out Vector2Int landingA) &&
                        roomFootprintB.TryGetEdgeCellTowards(new Vector2Int(-1, 0), z, out Vector2Int landingB))
                    {
                        TryCollectAerialBridgeLine(layout, cellLevels, roomA, roomB, landingA, landingB, new Vector2Int(1, 0), candidates);
                    }
                }
            }
            else if (b.xMax < a.xMin)
            {
                for (int z = Mathf.Max(a.yMin, b.yMin); z < Mathf.Min(a.yMax, b.yMax); z++)
                {
                    if (roomFootprintA.TryGetEdgeCellTowards(new Vector2Int(-1, 0), z, out Vector2Int landingA) &&
                        roomFootprintB.TryGetEdgeCellTowards(new Vector2Int(1, 0), z, out Vector2Int landingB))
                    {
                        TryCollectAerialBridgeLine(layout, cellLevels, roomA, roomB, landingA, landingB, new Vector2Int(-1, 0), candidates);
                    }
                }
            }

            if (a.yMax < b.yMin)
            {
                for (int x = Mathf.Max(a.xMin, b.xMin); x < Mathf.Min(a.xMax, b.xMax); x++)
                {
                    if (roomFootprintA.TryGetEdgeCellTowards(new Vector2Int(0, 1), x, out Vector2Int landingA) &&
                        roomFootprintB.TryGetEdgeCellTowards(new Vector2Int(0, -1), x, out Vector2Int landingB))
                    {
                        TryCollectAerialBridgeLine(layout, cellLevels, roomA, roomB, landingA, landingB, new Vector2Int(0, 1), candidates);
                    }
                }
            }
            else if (b.yMax < a.yMin)
            {
                for (int x = Mathf.Max(a.xMin, b.xMin); x < Mathf.Min(a.xMax, b.xMax); x++)
                {
                    if (roomFootprintA.TryGetEdgeCellTowards(new Vector2Int(0, -1), x, out Vector2Int landingA) &&
                        roomFootprintB.TryGetEdgeCellTowards(new Vector2Int(0, 1), x, out Vector2Int landingB))
                    {
                        TryCollectAerialBridgeLine(layout, cellLevels, roomA, roomB, landingA, landingB, new Vector2Int(0, -1), candidates);
                    }
                }
            }
        }

        private static void TryCollectAerialBridgeLine(
            DungeonLayout layout,
            Dictionary<Vector2Int, int> cellLevels,
            int roomA,
            int roomB,
            Vector2Int landingA,
            Vector2Int landingB,
            Vector2Int lineDirection,
            List<AerialBridgeCandidate> candidates)
        {
            // Decision 34: endpoints may differ by up to the end-delta cap; all
            // clearance gates use the conservative LOWER landing level.
            if (!cellLevels.TryGetValue(landingA, out int levelA) ||
                !cellLevels.TryGetValue(landingB, out int levelB) ||
                Mathf.Abs(levelA - levelB) > MaxAerialBridgeEndDeltaLevels ||
                Mathf.Min(levelA, levelB) < MinAerialBridgeLevel)
            {
                return;
            }

            int deckClearanceLevel = Mathf.Min(levelA, levelB);

            int span = Mathf.Abs(landingB.x - landingA.x) + Mathf.Abs(landingB.y - landingA.y) - 1;
            if (span < MinAerialBridgeSpanCells || span > MaxAerialBridgeSpanCells)
            {
                return;
            }

            var gapCells = new List<Vector2Int>(span);
            var lateral = new Vector2Int(-lineDirection.y, lineDirection.x);
            int huggedCells = 0;
            for (int i = 1; i <= span; i++)
            {
                Vector2Int cell = landingA + lineDirection * i;
                // Decision 30: room interiors are off-limits; anything else is
                // overflyable if it leaves headroom (cells still unleveled here
                // get filled later and re-checked by the late headroom gate).
                foreach (RoomFootprint room in layout.rooms)
                {
                    if (room.Contains(cell))
                    {
                        return;
                    }
                }

                if (cellLevels.TryGetValue(cell, out int cellLevel) && cellLevel > deckClearanceLevel - MinHeadroomLevels)
                {
                    return;
                }

                if (CellHugsWalkway(cellLevels, cell + lateral, deckClearanceLevel) ||
                    CellHugsWalkway(cellLevels, cell - lateral, deckClearanceLevel))
                {
                    huggedCells++;
                }

                gapCells.Add(cell);
            }

            // Decision 32: a deck running laterally beside an existing walkway
            // for half its span or more reads as a duplicate of that walkway.
            if (huggedCells * 2 >= span)
            {
                return;
            }

            candidates.Add(new AerialBridgeCandidate(roomA, roomB, landingA, landingB, lineDirection, gapCells));
        }

        private static bool CellHugsWalkway(Dictionary<Vector2Int, int> cellLevels, Vector2Int cell, int deckLevel)
        {
            return cellLevels.TryGetValue(cell, out int level) &&
                Mathf.Abs(level - deckLevel) <= AerialBridgeHugLevelTolerance;
        }

        // Decision 32: bridges are shortcuts, not alternatives. BFS the live walk
        // network — equal-level floor adjacency plus every placed transition
        // (stairs, strips, seams and earlier aerial decks, so twin bridges
        // self-exclude) — and reject when the landings are already close.
        private static bool AerialBridgeIsRedundant(
            AerialBridgeCandidate candidate,
            Dictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            int threshold = AerialBridgeShortcutFactor * (candidate.gapCells.Count + 2);
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

            var visited = new HashSet<Vector2Int> { candidate.landingA };
            var frontier = new Queue<(Vector2Int cell, int distance)>();
            frontier.Enqueue((candidate.landingA, 0));
            while (frontier.Count > 0)
            {
                (Vector2Int cell, int distance) = frontier.Dequeue();
                if (cell == candidate.landingB)
                {
                    return true;
                }

                if (distance >= threshold || !cellLevels.TryGetValue(cell, out int level))
                {
                    continue;
                }

                foreach (int direction in Direction.Cardinals)
                {
                    Vector2Int neighbor = cell + CardinalVector(direction);
                    if (cellLevels.TryGetValue(neighbor, out int neighborLevel) &&
                        neighborLevel == level &&
                        visited.Add(neighbor))
                    {
                        frontier.Enqueue((neighbor, distance + 1));
                    }
                }

                if (links.TryGetValue(cell, out List<Vector2Int> linked))
                {
                    foreach (Vector2Int neighbor in linked)
                    {
                        if (cellLevels.ContainsKey(neighbor) && visited.Add(neighbor))
                        {
                            frontier.Enqueue((neighbor, distance + 1));
                        }
                    }
                }
            }

            return false;
        }

        private static bool TryPlaceAerialBridge(
            AerialBridgeCandidate candidate,
            List<ElevationEdgeModel.TransitionEdge> transitions,
            HashSet<string> transitionKeys,
            StairPlacementLedger plannedStairLedger,
            Dictionary<Vector2Int, int> spanDeckLevels,
            List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs,
            Dictionary<Vector2Int, int> cellLevels)
        {
            // Decision 34: the walk runs lower -> upper, so the contract's entry
            // (level 0) anchors at the LOWER landing whatever the candidate's
            // collection order was.
            int levelA = cellLevels[candidate.landingA];
            int levelB = cellLevels[candidate.landingB];
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
                    if (cellLevels.TryGetValue(orderedGapCells[i] + worldPlusSide, out int plusLevel) && plusLevel == deckClearanceLevel)
                    {
                        railPlusMask |= 1UL << i;
                    }

                    if (cellLevels.TryGetValue(orderedGapCells[i] - worldPlusSide, out int minusLevel) && minusLevel == deckClearanceLevel)
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
                    LogPlanningWarning($"Dungeon Lab Generate: aerial deck synthesis failed for span {orderedGapCells.Count}: {failureSummary}");
                    return false;
                }
            }
            catch (Exception exception)
            {
                LogPlanningWarning($"Dungeon Lab Generate: aerial deck synthesis failed for span {candidate.gapCells.Count}: {exception.Message}");
                return false;
            }

            string parserError = ElevationEdgeModel.ValidateSynthesizedContractToken(design.contract, StairForge.LevelHeight);
            if (!string.IsNullOrEmpty(parserError))
            {
                LogPlanningWarning($"Dungeon Lab Generate: aerial deck '{design.name}' rejected by the edge-model parser: {parserError}");
                return false;
            }

            if (!TryBuildSynthesizedStairOption(design.contract, out ReviewedActiveStairOption option, out string optionError))
            {
                LogPlanningWarning($"Dungeon Lab Generate: aerial deck '{design.name}' rejected by the planner parser: {optionError}");
                return false;
            }

            // Decision 32: only genuine shortcuts get a deck — checked against the
            // LIVE network so earlier aerial decks count as existing paths.
            if (AerialBridgeIsRedundant(candidate, cellLevels, transitions))
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
            if (plannedStairLedger.ConflictsWith(stairCandidate))
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
                upperLanding,
                option.prefabPath,
                new[] { lowerLanding },
                new[] { upperLanding },
                footprint,
                entryPortDirection,
                exitPortDirection,
                ExternalSpanStairPlacementClass,
                setPiece));
            plannedStairLedger.Register(footprint, new[] { lowerLanding }, new[] { upperLanding });
            // Conservative MIN landing level over every span cell (decision 34):
            // a sloped deck is never lower than this anywhere along its run.
            foreach (Vector2Int cell in footprint)
            {
                if (!spanDeckLevels.TryGetValue(cell, out int existingDeck) || deckClearanceLevel < existingDeck)
                {
                    spanDeckLevels[cell] = deckClearanceLevel;
                }
            }

            synthesizedStairs.Add(($"aerial:{candidate.roomA}<->{candidate.roomB}", setPiece));
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            int fromLevel,
            int toLevel,
            SortedDictionary<string, int> stairCandidateCounts,
            StairPlacementLedger plannedStairLedger,
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

            var options = new List<ReviewedActiveStairOption>();
            var designsByName = new Dictionary<string, StairForge.SynthesizedStaircaseDesign>(StringComparer.Ordinal);
            foreach (StairForge.SynthesizedStaircaseDesign design in designs)
            {
                string parserError = ElevationEdgeModel.ValidateSynthesizedContractToken(design.contract, StairForge.LevelHeight);
                if (!string.IsNullOrEmpty(parserError))
                {
                    LogPlanningWarning($"Dungeon Lab Generate: stairwell design '{design.name}' rejected by the edge-model parser: {parserError}");
                    continue;
                }

                if (!TryBuildSynthesizedStairOption(design.contract, out ReviewedActiveStairOption option, out string optionError))
                {
                    LogPlanningWarning($"Dungeon Lab Generate: stairwell design '{design.name}' rejected by the planner parser: {optionError}");
                    continue;
                }

                options.Add(option);
                designsByName[option.name] = design;
            }

            if (options.Count == 0)
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
            foreach (ReviewedActiveStairOption option in options)
            {
                AddValidStairwellTransitionCandidates(
                    path,
                    climbsFromConnectionStart,
                    lastFromIndex,
                    firstToIndex,
                    preferredOnly: true,
                    option,
                    layoutFloorCells,
                    cellLevels,
                    lowerLevel,
                    higherLevel,
                    candidates);
            }

            RemovePlannedStairConflicts(candidates, plannedStairLedger);
            if (candidates.Count == 0)
            {
                foreach (ReviewedActiveStairOption option in options)
                {
                    AddValidStairwellTransitionCandidates(
                        path,
                        climbsFromConnectionStart,
                        lastFromIndex,
                        firstToIndex,
                        preferredOnly: false,
                        option,
                        layoutFloorCells,
                        cellLevels,
                        lowerLevel,
                        higherLevel,
                        candidates);
                }

                RemovePlannedStairConflicts(candidates, plannedStairLedger);
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

            StairForge.SynthesizedStaircaseDesign chosen = designsByName[selected.name];
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
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
                        if (layoutFloorCells.Contains(world) || cellLevels.ContainsKey(world))
                        {
                            fits = false;
                            break;
                        }

                        worldFootprint[f] = world;
                    }

                    if (!fits ||
                        !PlannedCellsAreCompatible(cellLevels, new[] { lowerPathCell }, lowerLevel) ||
                        !PlannedCellsAreCompatible(cellLevels, new[] { upperPathCell }, higherLevel))
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
            StairPlacementLedger plannedStairLedger)
        {
            if (plannedStairLedger == null)
            {
                return;
            }

            candidates.RemoveAll(plannedStairLedger.ConflictsWith);
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            int lowerLevel,
            int higherLevel,
            bool allowExternalSpan,
            bool preferredOnly,
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
                        cellLevels,
                        lowerLevel,
                        higherLevel,
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
                        cellLevels,
                        lowerLevel,
                        higherLevel,
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
                        cellLevels,
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
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
                            cellLevels,
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
                        cellLevels,
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
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
                    cellLevels,
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            int lowerLevel,
            int higherLevel,
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
                                cellLevels,
                                layoutFloorCells,
                                placement.lowerLandingCells,
                                placement.upperLandingCells,
                                placement.footprintCells,
                                lowerLevel,
                                higherLevel))
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
                            cellLevels,
                            layoutFloorCells,
                            placement.lowerLandingCells,
                            placement.upperLandingCells,
                            placement.footprintCells,
                            lowerLevel,
                            higherLevel))
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            int lowerLevel,
            int higherLevel,
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
                            cellLevels,
                            layoutFloorCells,
                            placement.lowerLandingCells,
                            placement.upperLandingCells,
                            placement.footprintCells,
                            lowerLevel,
                            higherLevel))
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
                        cellLevels,
                        layoutFloorCells,
                        descendingPlacement.lowerLandingCells,
                        descendingPlacement.upperLandingCells,
                        descendingPlacement.footprintCells,
                        lowerLevel,
                        higherLevel))
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            HashSet<Vector2Int> layoutFloorCells,
            IReadOnlyList<Vector2Int> lowerLandingCells,
            IReadOnlyList<Vector2Int> upperLandingCells,
            IReadOnlyList<Vector2Int> footprintCells,
            int lowerLevel,
            int higherLevel)
        {
            if (cellLevels == null)
            {
                return true;
            }

            return PlannedCellsAreCompatible(cellLevels, lowerLandingCells, lowerLevel) &&
                PlannedCellsAreCompatible(cellLevels, upperLandingCells, higherLevel) &&
                PlannedCellsAreCompatible(cellLevels, footprintCells, lowerLevel) &&
                PlannedEmbeddedFootprintCellsHaveFloorSupport(layoutFloorCells, footprintCells) &&
                !AnyOverlap(lowerLandingCells, footprintCells) &&
                !AnyOverlap(upperLandingCells, footprintCells);
        }

        private static bool PlannedEmbeddedFootprintCellsHaveFloorSupport(
            HashSet<Vector2Int> layoutFloorCells,
            IReadOnlyList<Vector2Int> footprintCells)
        {
            if (layoutFloorCells == null)
            {
                return true;
            }

            foreach (Vector2Int cell in footprintCells)
            {
                if (!layoutFloorCells.Contains(cell))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool PlannedExternalSpanCellsAreCompatible(
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<Vector2Int> lowerLandingCells,
            IReadOnlyList<Vector2Int> upperLandingCells,
            IReadOnlyList<Vector2Int> footprintCells,
            int lowerLevel,
            int higherLevel)
        {
            if (cellLevels == null ||
                !PlannedCellsAreCompatible(cellLevels, lowerLandingCells, lowerLevel) ||
                !PlannedCellsAreCompatible(cellLevels, upperLandingCells, higherLevel) ||
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
                if (cellLevels.ContainsKey(cell))
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

        private static bool PlannedCellsAreCompatible(
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<Vector2Int> cells,
            int level)
        {
            foreach (Vector2Int cell in cells)
            {
                if (cellLevels.TryGetValue(cell, out int existingLevel) && existingLevel != level)
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

        private static bool TryBuildReviewedStairPortPlacementBetweenLandings(
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

        // Cells already claimed by planned stairs in the current level field. Every stair
        // lane must keep at least one walkable floor cell at each landing, so a new
        // stair may not put its footprint on another stair's landing (or footprint), and
        // may not place its own landings on another stair's footprint. Landings may be
        // shared: a shared flat cell still gives both stairs their walkable landing.
        private sealed class StairPlacementLedger
        {
            public readonly HashSet<Vector2Int> footprintCells = new HashSet<Vector2Int>();
            public readonly HashSet<Vector2Int> landingCells = new HashSet<Vector2Int>();

            public void Register(
                IReadOnlyList<Vector2Int> footprint,
                IReadOnlyList<Vector2Int> lowerLandings,
                IReadOnlyList<Vector2Int> upperLandings)
            {
                foreach (Vector2Int cell in footprint)
                {
                    footprintCells.Add(cell);
                }

                foreach (Vector2Int cell in lowerLandings)
                {
                    landingCells.Add(cell);
                }

                foreach (Vector2Int cell in upperLandings)
                {
                    landingCells.Add(cell);
                }
            }

            public bool ConflictsWith(StairTransitionCandidate candidate)
            {
                foreach (Vector2Int cell in candidate.footprintCells)
                {
                    if (footprintCells.Contains(cell) || landingCells.Contains(cell))
                    {
                        return true;
                    }
                }

                foreach (Vector2Int cell in candidate.lowerLandingCells)
                {
                    if (footprintCells.Contains(cell))
                    {
                        return true;
                    }
                }

                foreach (Vector2Int cell in candidate.upperLandingCells)
                {
                    if (footprintCells.Contains(cell))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

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

        private static bool TrySetCellLevel(
            Dictionary<Vector2Int, int> cellLevels,
            Vector2Int cell,
            int level,
            out string rejectionReason)
        {
            if (cellLevels.TryGetValue(cell, out int existing) && existing != level)
            {
                rejectionReason = $"cell {cell} was assigned both level {existing} and level {level}";
                return false;
            }

            cellLevels[cell] = level;
            rejectionReason = string.Empty;
            return true;
        }

        private static bool TrySetPlannedStairCells(
            Dictionary<Vector2Int, int> cellLevels,
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
                if (!TrySetCellLevel(cellLevels, cell, lowerLevel, out rejectionReason))
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
                if (footprintStaysUnleveled && !cellLevels.ContainsKey(cell))
                {
                    continue;
                }

                if (!TrySetCellLevel(cellLevels, cell, lowerLevel, out rejectionReason))
                {
                    return false;
                }
            }

            foreach (Vector2Int cell in upperLandingCells)
            {
                if (!TrySetCellLevel(cellLevels, cell, higherLevel, out rejectionReason))
                {
                    return false;
                }
            }

            rejectionReason = string.Empty;
            return true;
        }

        private static void FillUnassignedFloorCells(
            HashSet<Vector2Int> floorCells,
            Dictionary<Vector2Int, int> cellLevels,
            HashSet<Vector2Int> externalSpanGapCells)
        {
            // Seed the flood fill in sorted order: Dictionary key order is not contractually
            // stable, and which seed reaches a contested unassigned cell first decides its level.
            var seeds = new List<Vector2Int>(cellLevels.Keys);
            seeds.Sort(CompareCells);
            var queue = new Queue<Vector2Int>();
            foreach (Vector2Int cell in seeds)
            {
                queue.Enqueue(cell);
            }

            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                foreach (Vector2Int neighbor in CardinalNeighbors(cell))
                {
                    if (!floorCells.Contains(neighbor) ||
                        cellLevels.ContainsKey(neighbor) ||
                        externalSpanGapCells.Contains(neighbor))
                    {
                        continue;
                    }

                    cellLevels[neighbor] = cellLevels[cell];
                    queue.Enqueue(neighbor);
                }
            }

            foreach (Vector2Int cell in floorCells)
            {
                if (!cellLevels.ContainsKey(cell) && !externalSpanGapCells.Contains(cell))
                {
                    cellLevels[cell] = 0;
                }
            }
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

        private static bool TryValidateTransitionLevelDeltas(
            Dictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            out string rejectionReason)
        {
            var transitionKeys = new HashSet<string>();
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                if (!cellLevels.TryGetValue(transition.firstCell, out int firstLevel) ||
                    !cellLevels.TryGetValue(transition.secondCell, out int secondLevel))
                {
                    rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} referenced a missing cell";
                    return false;
                }

                // Seam transitions climb exactly 1u; the primary straight stair climbs
                // the primary rise; anything taller must carry an explicit reviewed
                // stair prefab and stay within the generated level cap.
                int delta = Mathf.Abs(firstLevel - secondLevel);
                // Aerial decks are flat (rise 0, decisions 29-31) or absorb a
                // small end delta (decision 34: a rise-1 sloped span is legal for
                // synthesized transitions; the gate otherwise admits only seam
                // strips at delta 1).
                if (delta <= 1 && transition.synthesizedSetPiece != null)
                {
                    transitionKeys.Add(TransitionKey(transition.firstCell, transition.secondCell));
                    continue;
                }

                if (delta == 1 &&
                    (string.Equals(transition.placementClass, SeamStairPlacementClass, StringComparison.Ordinal) ||
                     string.Equals(transition.placementClass, DaisStairPlacementClass, StringComparison.Ordinal)))
                {
                    transitionKeys.Add(TransitionKey(transition.firstCell, transition.secondCell));
                    continue;
                }

                if (delta == PrimaryStairRiseLevels)
                {
                    transitionKeys.Add(TransitionKey(transition.firstCell, transition.secondCell));
                    continue;
                }

                if (delta < PrimaryStairRiseLevels || delta > MaxGeneratedLevel || string.IsNullOrEmpty(transition.stairPrefabPath))
                {
                    rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} had level delta {delta}";
                    return false;
                }

                transitionKeys.Add(TransitionKey(transition.firstCell, transition.secondCell));
            }

            rejectionReason = string.Empty;
            return true;
        }

        private static bool TryBuildFloorStairPortGraph(
            Dictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            out FloorStairPortGraph graph,
            out string rejectionReason)
        {
            graph = new FloorStairPortGraph();
            rejectionReason = string.Empty;
            if (cellLevels.Count == 0)
            {
                rejectionReason = "cell level field had no floor cells";
                return false;
            }

            HashSet<Vector2Int> stairFootprintCells = BuildTransitionFootprintCellSet(transitions);
            foreach (var item in cellLevels)
            {
                if (stairFootprintCells.Contains(item.Key))
                {
                    continue;
                }

                graph.EnsureNode(PortGraphNode.Floor(item.Key, item.Value));
            }

            foreach (var item in cellLevels)
            {
                if (stairFootprintCells.Contains(item.Key))
                {
                    continue;
                }

                PortGraphNode floorNode = PortGraphNode.Floor(item.Key, item.Value);
                foreach (Vector2Int neighbor in CardinalNeighbors(item.Key))
                {
                    if (stairFootprintCells.Contains(neighbor) ||
                        !cellLevels.TryGetValue(neighbor, out int neighborLevel) ||
                        neighborLevel != item.Value)
                    {
                        continue;
                    }

                    graph.AddEdge(floorNode, PortGraphNode.Floor(neighbor, neighborLevel), PortGraphEdgeKind.FloorAdjacency);
                }
            }

            for (int i = 0; i < transitions.Count; i++)
            {
                ElevationEdgeModel.TransitionEdge transition = transitions[i];
                if (!TryAddTransitionToPortGraph(cellLevels, transition, i, graph, out rejectionReason))
                {
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            ElevationEdgeModel.TransitionEdge transition,
            int transitionIndex,
            FloorStairPortGraph graph,
            out string rejectionReason)
        {
            rejectionReason = string.Empty;
            if (!cellLevels.TryGetValue(transition.firstCell, out int firstLevel) ||
                !cellLevels.TryGetValue(transition.secondCell, out int secondLevel))
            {
                rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} referenced a missing floor/stair graph cell";
                return false;
            }

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

            if (!ValidateTransitionLandingCells(cellLevels, lowerLandingCells, lowerLevel, transition, "lower", out rejectionReason) ||
                !ValidateTransitionLandingCells(cellLevels, upperLandingCells, upperLevel, transition, "upper", out rejectionReason))
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
                graph.AddEdge(lowerPort, PortGraphNode.Floor(cell, lowerLevel), PortGraphEdgeKind.PortLanding);
            }

            foreach (Vector2Int cell in upperLandingCells)
            {
                graph.AddEdge(upperPort, PortGraphNode.Floor(cell, upperLevel), PortGraphEdgeKind.PortLanding);
            }

            return true;
        }

        private static bool ValidateTransitionLandingCells(
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
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

                if (!cellLevels.TryGetValue(cell, out int level))
                {
                    rejectionReason = $"transition {transition.firstCell}->{transition.secondCell} {label} landing cell {cell} was missing from level field";
                    return false;
                }

                if (level != expectedLevel)
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

        private static HashSet<Vector2Int> BuildTransitionFootprintCellSet(IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            var result = new HashSet<Vector2Int>();
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                foreach (Vector2Int cell in transition.footprintCells)
                {
                    result.Add(cell);
                }
            }

            return result;
        }

        private static int CountDistinctLevels(Dictionary<Vector2Int, int> cellLevels)
        {
            var levels = new HashSet<int>();
            foreach (int level in cellLevels.Values)
            {
                levels.Add(level);
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

        private static void GetLevelRange(Dictionary<Vector2Int, int> cellLevels, out int minLevel, out int maxLevel)
        {
            minLevel = int.MaxValue;
            maxLevel = int.MinValue;
            foreach (int level in cellLevels.Values)
            {
                minLevel = Mathf.Min(minLevel, level);
                maxLevel = Mathf.Max(maxLevel, level);
            }

            if (cellLevels.Count == 0)
            {
                minLevel = 0;
                maxLevel = 0;
            }
        }

        private static int[] CountRoomsPerTier(IReadOnlyList<int> roomLevels)
        {
            var counts = new int[MaxGeneratedLevel + 1];
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            var countsByDelta = new SortedDictionary<int, int>();
            var setPiecesByDelta = new SortedDictionary<int, HashSet<string>>();
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                if (!cellLevels.TryGetValue(transition.firstCell, out int firstLevel) ||
                    !cellLevels.TryGetValue(transition.secondCell, out int secondLevel))
                {
                    continue;
                }

                int delta = Mathf.Abs(firstLevel - secondLevel);
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
                return StepFormationModeTable.Load().PrimaryStair;
            }
            catch
            {
                return "primaryStair";
            }
        }

        private static int CountSpatialOverlookEdges(
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions)
        {
            var transitionKeys = new HashSet<string>();
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                transitionKeys.Add(TransitionKey(transition.firstCell, transition.secondCell));
            }

            int count = 0;
            foreach (var item in cellLevels)
            {
                foreach (Vector2Int direction in CardinalDirections())
                {
                    Vector2Int neighbor = item.Key + direction;
                    if (cellLevels.TryGetValue(neighbor, out int neighborLevel))
                    {
                        // Overlook stat (reported only): a sheer vista drop of at
                        // least one 4u major (decision A's tier step).
                        if (CompareCells(item.Key, neighbor) < 0 &&
                            !transitionKeys.Contains(TransitionKey(item.Key, neighbor)) &&
                            Mathf.Abs(item.Value - neighborLevel) >= MajorRiseLevels)
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

        private static void PlaceStepFormation(
            bool hasPlacement,
            StepFormationPlacement placement,
            PlacementValidationState validator,
            Transform parent,
            float tileSize,
            ref DungeonGenerationStats stats,
            ref Bounds bounds,
            ref bool hasBounds,
            out string stepFormationName)
        {
            stepFormationName = "none";
            if (!hasPlacement)
            {
                return;
            }

            StepLibraryRecord record = placement.record;
            var stepRoot = CreateChild(parent, "Step Formation");
            string instanceName = $"stepFormation_{record.name}";
            GameObject instance = InstantiatePrefab(record.path, instanceName, stepRoot.transform, Vector3.zero, 0f);
            if (instance == null)
            {
                stats.rejected++;
                Undo.DestroyObjectImmediate(stepRoot);
                return;
            }

            if (!TryGetRendererOrColliderWorldBounds(instance, out Bounds initialBounds))
            {
                stats.rejected++;
                Debug.LogError($"Dungeon Lab: rejected step formation '{record.name}' because it has no renderer or collider bounds.");
                Undo.DestroyObjectImmediate(stepRoot);
                return;
            }

            if (!StepMeasurementMatchesIndex(record, initialBounds, compareOriginOffset: true, yRotation: 0f, out string measurementError))
            {
                stats.rejected++;
                Debug.LogError($"Dungeon Lab: rejected step formation '{record.name}' because live measurement disagrees with {StepLibraryIndexPath}. {measurementError}");
                Undo.DestroyObjectImmediate(stepRoot);
                return;
            }

            instance.transform.rotation = Quaternion.Euler(0f, placement.yRotation, 0f);
            if (!TryGetRendererOrColliderWorldBounds(instance, out Bounds rotatedBounds))
            {
                stats.rejected++;
                Debug.LogError($"Dungeon Lab: rejected step formation '{record.name}' because it had no measurable bounds after rotation.");
                Undo.DestroyObjectImmediate(stepRoot);
                return;
            }

            float initialConnectionPlaneY = GetWorldConnectionPlaneY(record, rotatedBounds);
            Vector3 offset = new Vector3(
                placement.targetCenter.x - rotatedBounds.center.x,
                placement.targetCenter.y - initialConnectionPlaneY,
                placement.targetCenter.z - rotatedBounds.center.z);
            instance.transform.position += offset;

            if (!TryGetRendererOrColliderWorldBounds(instance, out Bounds placedBounds))
            {
                stats.rejected++;
                Debug.LogError($"Dungeon Lab: rejected step formation '{record.name}' because it had no measurable bounds after placement.");
                Undo.DestroyObjectImmediate(stepRoot);
                return;
            }

            if (!ValidateStepFormationInstance(
                    instance,
                    record,
                    placedBounds,
                    placement,
                    validator,
                    instanceName))
            {
                stats.rejected++;
                Undo.DestroyObjectImmediate(stepRoot);
                return;
            }

            stepFormationName = record.name;
            stats.stepFormations++;
            Encapsulate(ref bounds, ref hasBounds, placedBounds.min);
            Encapsulate(ref bounds, ref hasBounds, placedBounds.max);
            Debug.Log(
                $"Dungeon Lab: placed step formation {record.name} in largest room {placement.room.width}x{placement.room.height}; mode {placement.placementMode}, yaw {placement.yRotation:0.###}, index footprint {record.footprintCells.x:0.###}x{record.footprintCells.z:0.###} cells, measured footprint {(placedBounds.size.x / tileSize):0.###}x{(placedBounds.size.z / tileSize):0.###} cells, connectionPlaneY {GetWorldConnectionPlaneY(record, placedBounds):0.###}, baseY {placedBounds.min.y:0.###}.");
        }

        private static List<Vector2Int> BuildStepFormationFootprintCells(
            RectInt room,
            PlanBounds footprint,
            Vector3 origin,
            float tileSize,
            Vector2Int minCell,
            float baseX,
            float baseZ)
        {
            var cells = new List<Vector2Int>();
            for (int z = room.yMin; z < room.yMax; z++)
            {
                for (int x = room.xMin; x < room.xMax; x++)
                {
                    var cell = new Vector2Int(x, z);
                    PlanBounds cellBounds = CellToWorldPlanBounds(origin, tileSize, minCell, baseX, baseZ, cell);
                    if (PlanBoundsIntersect(footprint, cellBounds, 0.02f))
                    {
                        cells.Add(cell);
                    }
                }
            }

            return cells;
        }

        private static float GetWorldConnectionPlaneY(StepLibraryRecord record, Bounds worldBounds)
        {
            return worldBounds.min.y + (record.connectionPlaneY - record.boundsMin.y);
        }

        private static bool TryChooseStepFormation(
            HashSet<Vector2Int> floorCells,
            RectInt room,
            PlanBounds roomBounds,
            Vector3 origin,
            float tileSize,
            Vector2Int minCell,
            float baseX,
            float baseZ,
            List<PlanBounds> blockedBounds,
            out StepFormationPlacement selected,
            out string reason)
        {
            StepLibraryIndex index = LoadStepLibraryIndex();
            if (index == null || index.records == null || index.records.Length == 0)
            {
                throw new InvalidOperationException($"{StepLibraryIndexPath} has no records.");
            }

            selected = default;
            PlanBounds requiredClearanceBounds = InsetPlanBounds(roomBounds, tileSize);
            StepFormationPlacement best = default;
            float bestArea = -1f;
            float bestScore = float.NegativeInfinity;
            int considered = 0;
            int candidates = 0;
            int skippedInvalid = 0;
            var skippedConnective = new HashSet<string>();
            var skippedSunken = new HashSet<string>();

            foreach (StepLibraryRecord record in index.records)
            {
                if (!IsFlatStepFormationRecord(record))
                {
                    continue;
                }

                if (StepFormationIsSunken(record))
                {
                    skippedSunken.Add(record.name);
                    continue;
                }

                if (record.connectionPlane != "bottom")
                {
                    continue;
                }

                if (!StepMeasurementRecordIsValid(record, out string recordError))
                {
                    skippedInvalid++;
                    Debug.LogWarning($"Dungeon Lab: skipped step formation '{record.name}'; {recordError}.");
                    continue;
                }

                if (record.placementMode == "room_entrance")
                {
                    skippedConnective.Add(record.name);
                    continue;
                }

                if (record.placementMode != "interior")
                {
                    continue;
                }

                considered++;
                foreach (float yRotation in StepFormationRotations(record))
                {
                    if (!TryBuildStepFormationPlacement(
                            floorCells,
                            room,
                            roomBounds,
                            requiredClearanceBounds,
                            origin,
                            tileSize,
                            minCell,
                            baseX,
                            baseZ,
                            blockedBounds,
                            record,
                            yRotation,
                            out StepFormationPlacement candidate))
                    {
                        continue;
                    }

                    candidates++;
                    float area = candidate.expectedFootprint.Size.x * candidate.expectedFootprint.Size.y;
                    float score = StepFormationSelectionScore(record, area);
                    if (score > bestScore)
                    {
                        best = candidate;
                        bestArea = area;
                        bestScore = score;
                    }
                }
            }

            if (skippedConnective.Count > 0)
            {
                var names = new List<string>(skippedConnective);
                names.Sort(StringComparer.Ordinal);
                Debug.Log($"Dungeon Lab: skipped connective step formations for room-center placement: {string.Join(", ", names)}.");
            }

            if (skippedSunken.Count > 0)
            {
                var names = new List<string>(skippedSunken);
                names.Sort(StringComparer.Ordinal);
                Debug.Log($"Dungeon Lab: skipped sunken step formations for raised-only placement: {string.Join(", ", names)}.");
            }

            if (bestArea < 0f)
            {
                reason = considered == 0 && skippedInvalid > 0
                    ? $"no usable raised StepFormations records were available; {skippedInvalid} records have invalid measured connection data"
                    : considered == 0
                    ? $"no raised StepFormations records were found in {StepLibraryIndexPath}"
                    : candidates == 0
                        ? "no raised StepFormations footprint fit the room with measured perimeter/entrance constraints"
                        : "no raised StepFormations footprint fit the one-cell-clearance interior without reserved-feature overlap";
                return false;
            }

            selected = best;
            reason = string.Empty;
            return true;
        }

        private static bool StepFormationClearsFlatFloor(StepLibraryRecord record)
        {
            return record != null && record.connectionPlane == "top";
        }

        private static bool StepFormationIsSunken(StepLibraryRecord record)
        {
            return record != null && record.connectionPlane == "top";
        }

        private static string StepFormationKind(StepLibraryRecord record)
        {
            return StepFormationIsSunken(record) ? "sunken" : "raised";
        }

        private static float StepFormationSelectionScore(StepLibraryRecord record, float area)
        {
            return area;
        }

        private static IEnumerable<float> StepFormationRotations(StepLibraryRecord record)
        {
            if (record != null && (record.placementMode == "room_entrance" || record.placementMode == "wall_abutting"))
            {
                yield return 0f;
                yield return 90f;
                yield return 180f;
                yield return 270f;
                yield break;
            }

            yield return 0f;
        }

        private static bool TryBuildStepFormationPlacement(
            HashSet<Vector2Int> floorCells,
            RectInt room,
            PlanBounds roomBounds,
            PlanBounds requiredClearanceBounds,
            Vector3 origin,
            float tileSize,
            Vector2Int minCell,
            float baseX,
            float baseZ,
            List<PlanBounds> blockedBounds,
            StepLibraryRecord record,
            float yRotation,
            out StepFormationPlacement placement)
        {
            placement = default;
            bool roomEntranceMode = record.placementMode == "room_entrance";
            Vector2 rotatedSize = RotatedStepFormationSize(record, yRotation);
            PlanBounds expectedFootprint;
            int entranceSide = 0;

            if (roomEntranceMode)
            {
                if (!TryBuildRoomEntranceFootprint(
                        floorCells,
                        room,
                        roomBounds,
                        origin,
                        tileSize,
                        minCell,
                        baseX,
                        baseZ,
                        rotatedSize,
                        record,
                        yRotation,
                        out expectedFootprint,
                        out entranceSide))
                {
                    return false;
                }
            }
            else
            {
                if (rotatedSize.x > requiredClearanceBounds.Size.x || rotatedSize.y > requiredClearanceBounds.Size.y)
                {
                    return false;
                }

                Vector2 center = requiredClearanceBounds.Center;
                expectedFootprint = new PlanBounds(
                    center.x - rotatedSize.x * 0.5f,
                    center.x + rotatedSize.x * 0.5f,
                    center.y - rotatedSize.y * 0.5f,
                    center.y + rotatedSize.y * 0.5f);
            }

            if (!PlanBoundsContains(roomBounds, expectedFootprint, 0.05f))
            {
                return false;
            }

            if (!roomEntranceMode && !PlanBoundsContains(requiredClearanceBounds, expectedFootprint, 0.05f))
            {
                return false;
            }

            if (IntersectsAnyPlanBounds(expectedFootprint, blockedBounds, 0.05f))
            {
                return false;
            }

            List<Vector2Int> footprintCells = BuildStepFormationFootprintCells(
                room,
                expectedFootprint,
                origin,
                tileSize,
                minCell,
                baseX,
                baseZ);
            if (footprintCells.Count == 0)
            {
                return false;
            }

            if (roomEntranceMode &&
                !UnsupportedEdgesAreBoundaryConstrained(record, yRotation, footprintCells, floorCells, room, entranceSide))
            {
                return false;
            }

            Vector2 footprintCenter = expectedFootprint.Center;
            Vector3 targetCenter = new Vector3(footprintCenter.x, origin.y, footprintCenter.y);
            placement = new StepFormationPlacement(
                room,
                record,
                targetCenter,
                roomBounds,
                requiredClearanceBounds,
                expectedFootprint,
                footprintCells,
                blockedBounds,
                yRotation,
                record.placementMode,
                entranceSide);
            return true;
        }

        private static Vector2 RotatedStepFormationSize(StepLibraryRecord record, float yRotation)
        {
            int quarterTurns = Mathf.RoundToInt(Mathf.Repeat(yRotation, 360f) / 90f) % 4;
            if ((quarterTurns & 1) == 0)
            {
                return new Vector2(record.footprintUnits.x, record.footprintUnits.z);
            }

            return new Vector2(record.footprintUnits.z, record.footprintUnits.x);
        }

        private static bool TryBuildRoomEntranceFootprint(
            HashSet<Vector2Int> floorCells,
            RectInt room,
            PlanBounds roomBounds,
            Vector3 origin,
            float tileSize,
            Vector2Int minCell,
            float baseX,
            float baseZ,
            Vector2 rotatedSize,
            StepLibraryRecord record,
            float yRotation,
            out PlanBounds footprint,
            out int entranceSide)
        {
            footprint = default;
            entranceSide = 0;
            int worldUnsupportedMask = RotateOpenEdges(record.perimeterUnsupportedMask, yRotation);
            if (worldUnsupportedMask == 0)
            {
                return false;
            }

            foreach (int side in Direction.Cardinals)
            {
                if ((worldUnsupportedMask & side) == 0)
                {
                    continue;
                }

                if (!TryFindRoomEntranceCenter(room, floorCells, side, out Vector2 entranceCenter))
                {
                    continue;
                }

                Vector2 entranceWorldCenter = new Vector2(
                    origin.x + baseX + (entranceCenter.x - minCell.x) * tileSize,
                    origin.z + baseZ + (entranceCenter.y - minCell.y) * tileSize);
                footprint = BuildEdgeAlignedFootprint(roomBounds, rotatedSize, side, entranceWorldCenter);
                entranceSide = side;
                return true;
            }

            return false;
        }

        private static bool TryFindRoomEntranceCenter(RectInt room, HashSet<Vector2Int> floorCells, int side, out Vector2 centerCell)
        {
            var cells = new List<Vector2Int>();
            switch (side)
            {
                case Direction.North:
                    for (int x = room.xMin; x < room.xMax; x++)
                    {
                        var inside = new Vector2Int(x, room.yMax - 1);
                        var outside = inside + Vector2Int.up;
                        if (!room.Contains(outside) && floorCells.Contains(outside))
                        {
                            cells.Add(inside);
                        }
                    }
                    break;
                case Direction.East:
                    for (int z = room.yMin; z < room.yMax; z++)
                    {
                        var inside = new Vector2Int(room.xMax - 1, z);
                        var outside = inside + Vector2Int.right;
                        if (!room.Contains(outside) && floorCells.Contains(outside))
                        {
                            cells.Add(inside);
                        }
                    }
                    break;
                case Direction.South:
                    for (int x = room.xMin; x < room.xMax; x++)
                    {
                        var inside = new Vector2Int(x, room.yMin);
                        var outside = inside + Vector2Int.down;
                        if (!room.Contains(outside) && floorCells.Contains(outside))
                        {
                            cells.Add(inside);
                        }
                    }
                    break;
                case Direction.West:
                    for (int z = room.yMin; z < room.yMax; z++)
                    {
                        var inside = new Vector2Int(room.xMin, z);
                        var outside = inside + Vector2Int.left;
                        if (!room.Contains(outside) && floorCells.Contains(outside))
                        {
                            cells.Add(inside);
                        }
                    }
                    break;
            }

            if (cells.Count == 0)
            {
                centerCell = default;
                return false;
            }

            Vector2 sum = Vector2.zero;
            foreach (Vector2Int cell in cells)
            {
                sum += new Vector2(cell.x + 0.5f, cell.y + 0.5f);
            }

            centerCell = sum / cells.Count;
            return true;
        }

        private static PlanBounds BuildEdgeAlignedFootprint(
            PlanBounds roomBounds,
            Vector2 size,
            int side,
            Vector2 entranceWorldCenter)
        {
            float centerX = entranceWorldCenter.x;
            float centerZ = entranceWorldCenter.y;
            switch (side)
            {
                case Direction.North:
                {
                    float clampedX = Mathf.Clamp(centerX, roomBounds.minX + size.x * 0.5f, roomBounds.maxX - size.x * 0.5f);
                    return new PlanBounds(clampedX - size.x * 0.5f, clampedX + size.x * 0.5f, roomBounds.maxZ - size.y, roomBounds.maxZ);
                }
                case Direction.East:
                {
                    float clampedZ = Mathf.Clamp(centerZ, roomBounds.minZ + size.y * 0.5f, roomBounds.maxZ - size.y * 0.5f);
                    return new PlanBounds(roomBounds.maxX - size.x, roomBounds.maxX, clampedZ - size.y * 0.5f, clampedZ + size.y * 0.5f);
                }
                case Direction.South:
                {
                    float clampedX = Mathf.Clamp(centerX, roomBounds.minX + size.x * 0.5f, roomBounds.maxX - size.x * 0.5f);
                    return new PlanBounds(clampedX - size.x * 0.5f, clampedX + size.x * 0.5f, roomBounds.minZ, roomBounds.minZ + size.y);
                }
                case Direction.West:
                {
                    float clampedZ = Mathf.Clamp(centerZ, roomBounds.minZ + size.y * 0.5f, roomBounds.maxZ - size.y * 0.5f);
                    return new PlanBounds(roomBounds.minX, roomBounds.minX + size.x, clampedZ - size.y * 0.5f, clampedZ + size.y * 0.5f);
                }
                default:
                    return roomBounds;
            }
        }

        private static bool UnsupportedEdgesAreBoundaryConstrained(
            StepLibraryRecord record,
            float yRotation,
            List<Vector2Int> footprintCells,
            HashSet<Vector2Int> floorCells,
            RectInt room,
            int entranceSide)
        {
            int unsupportedMask = RotateOpenEdges(record.perimeterUnsupportedMask, yRotation);
            bool hasEntranceSide = false;
            foreach (int side in Direction.Cardinals)
            {
                if ((unsupportedMask & side) == 0)
                {
                    continue;
                }

                bool sideHasCell = false;
                foreach (Vector2Int cell in footprintCells)
                {
                    if (!IsFootprintEdgeCell(cell, footprintCells, side))
                    {
                        continue;
                    }

                    sideHasCell = true;
                    Vector2Int outside = cell + DirectionVectorInt(side);
                    if (room.Contains(outside))
                    {
                        return false;
                    }

                    bool outsideIsFloor = floorCells.Contains(outside);
                    if (side == entranceSide)
                    {
                        hasEntranceSide |= outsideIsFloor;
                    }
                    else if (outsideIsFloor)
                    {
                        return false;
                    }
                }

                if (sideHasCell && side != entranceSide)
                {
                    continue;
                }
            }

            return hasEntranceSide;
        }

        private static bool IsFootprintEdgeCell(Vector2Int cell, List<Vector2Int> footprintCells, int side)
        {
            Vector2Int outside = cell + DirectionVectorInt(side);
            return !footprintCells.Contains(outside);
        }

        private static bool IsFlatStepFormationRecord(StepLibraryRecord record)
        {
            return record != null &&
                record.folder == "StepFormations" &&
                record.status != "empty" &&
                !string.IsNullOrWhiteSpace(record.path) &&
                record.path.StartsWith("Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/StepFormations/", StringComparison.Ordinal);
        }

        private static bool ValidateStepFormationInstance(
            GameObject instance,
            StepLibraryRecord record,
            Bounds placedBounds,
            StepFormationPlacement placement,
            PlacementValidationState validator,
            string name)
        {
            if (!StepMeasurementMatchesIndex(record, placedBounds, compareOriginOffset: false, yRotation: placement.yRotation, out string measurementError))
            {
                Debug.LogError($"Dungeon Lab: rejected step formation '{record.name}' because placed measurement disagrees with {StepLibraryIndexPath}. {measurementError}");
                return false;
            }

            const float placementTolerance = 0.08f;
            float connectionPlaneY = GetWorldConnectionPlaneY(record, placedBounds);
            if (Mathf.Abs(connectionPlaneY - placement.targetCenter.y) > placementTolerance)
            {
                Debug.LogError(
                    $"Dungeon Lab: rejected step formation '{record.name}' because its measured connection plane is not flush with the room floor. Expected Y {placement.targetCenter.y:0.###}, measured {connectionPlaneY:0.###} ({record.connectionPlane}).");
                return false;
            }

            Vector2 measuredCenter = new Vector2(placedBounds.center.x, placedBounds.center.z);
            Vector2 expectedCenter = new Vector2(placement.targetCenter.x, placement.targetCenter.z);
            if (Vector2.Distance(measuredCenter, expectedCenter) > placementTolerance)
            {
                Debug.LogError(
                    $"Dungeon Lab: rejected step formation '{record.name}' because its measured footprint center is not centered in the room. Expected {FormatVector2(expectedCenter)}, measured {FormatVector2(measuredCenter)}.");
                return false;
            }

            PlanBounds footprint = PlanBoundsFromWorldBounds(placedBounds);
            if (!PlanBoundsContains(placement.expectedFootprint, footprint, placementTolerance) ||
                !PlanBoundsContains(footprint, placement.expectedFootprint, placementTolerance))
            {
                Debug.LogError(
                    $"Dungeon Lab: rejected step formation '{record.name}' because its measured footprint does not match the planned footprint. Planned {placement.expectedFootprint}, measured {footprint}.");
                return false;
            }

            if (placement.placementMode == "interior" &&
                !PlanBoundsContains(placement.requiredClearanceBounds, footprint, placementTolerance))
            {
                Debug.LogError(
                    $"Dungeon Lab: rejected step formation '{record.name}' because its footprint lacks one-cell wall clearance. Required inside {placement.requiredClearanceBounds}, measured {footprint}.");
                return false;
            }

            if (!PlanBoundsContains(placement.roomBounds, footprint, placementTolerance))
            {
                Debug.LogError(
                    $"Dungeon Lab: rejected step formation '{record.name}' because its footprint is outside the room. Room {placement.roomBounds}, measured {footprint}.");
                return false;
            }

            return validator.TryRegisterStepFormation(footprint, placement.footprintCells, placement.blockedBounds, name);
        }

        private static bool StepMeasurementMatchesIndex(
            StepLibraryRecord record,
            Bounds bounds,
            bool compareOriginOffset,
            float yRotation,
            out string error)
        {
            const float tolerance = 0.08f;
            if (string.IsNullOrWhiteSpace(record.connectionPlane))
            {
                error = "Record is missing connectionPlane data; rerebuild a reviewed step-library contract index.";
                return false;
            }

            if (record.connectionPlaneY < record.boundsMin.y - tolerance ||
                record.connectionPlaneY > record.boundsMax.y + tolerance)
            {
                error =
                    $"Connection plane {record.connectionPlaneY:0.###} is outside indexed bounds {record.boundsMin.y:0.###}..{record.boundsMax.y:0.###}.";
                return false;
            }

            Vector2 expectedPlanSize = RotatedStepFormationSize(record, yRotation);
            if (Mathf.Abs(bounds.size.x - expectedPlanSize.x) > tolerance ||
                Mathf.Abs(bounds.size.z - expectedPlanSize.y) > tolerance ||
                Mathf.Abs(bounds.size.y - record.heightUnits) > tolerance)
            {
                error =
                    $"Expected size ({expectedPlanSize.x:0.###},{record.heightUnits:0.###},{expectedPlanSize.y:0.###}) at yaw {yRotation:0.###}, measured {FormatVector3(bounds.size)}.";
                return false;
            }

            if (compareOriginOffset &&
                (Mathf.Abs(bounds.min.x - record.originOffset.x) > tolerance ||
                    Mathf.Abs(bounds.min.y - record.originOffset.y) > tolerance ||
                    Mathf.Abs(bounds.min.z - record.originOffset.z) > tolerance))
            {
                error =
                    $"Expected originOffset/min {FormatVector3(new Vector3(record.originOffset.x, record.originOffset.y, record.originOffset.z))}, measured {FormatVector3(bounds.min)}.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static StepLibraryIndex LoadStepLibraryIndex()
        {
            if (!File.Exists(StepLibraryIndexPath))
            {
                throw new FileNotFoundException(StepLibraryIndexPath);
            }

            var index = JsonUtility.FromJson<StepLibraryIndex>(File.ReadAllText(StepLibraryIndexPath));
            if (index == null || index.records == null)
            {
                throw new InvalidOperationException($"{StepLibraryIndexPath} could not be parsed.");
            }

            StepFormationModeTable.ApplyToRecords(
                index.records,
                StepFormationModeTable.Load(),
                requireAllStepFormations: true);
            return index;
        }

        private static PlanBounds RectToWorldPlanBounds(
            Vector3 origin,
            float tileSize,
            Vector2Int minCell,
            float baseX,
            float baseZ,
            RectInt rect)
        {
            Vector3 min = CellMinToWorld(
                origin,
                tileSize,
                minCell,
                baseX,
                baseZ,
                new Vector2Int(rect.xMin, rect.yMin),
                0f);
            return new PlanBounds(
                min.x,
                min.x + rect.width * tileSize,
                min.z,
                min.z + rect.height * tileSize);
        }

        private static PlanBounds CellToWorldPlanBounds(
            Vector3 origin,
            float tileSize,
            Vector2Int minCell,
            float baseX,
            float baseZ,
            Vector2Int cell)
        {
            Vector3 min = CellMinToWorld(origin, tileSize, minCell, baseX, baseZ, cell, 0f);
            return new PlanBounds(min.x, min.x + tileSize, min.z, min.z + tileSize);
        }

        private static PlanBounds InsetPlanBounds(PlanBounds bounds, float inset)
        {
            return new PlanBounds(
                bounds.minX + inset,
                bounds.maxX - inset,
                bounds.minZ + inset,
                bounds.maxZ - inset);
        }

        private static PlanBounds PlanBoundsFromWorldBounds(Bounds bounds)
        {
            return new PlanBounds(bounds.min.x, bounds.max.x, bounds.min.z, bounds.max.z);
        }

        private static bool PlanBoundsContains(PlanBounds outer, PlanBounds inner, float tolerance)
        {
            return inner.minX >= outer.minX - tolerance &&
                inner.maxX <= outer.maxX + tolerance &&
                inner.minZ >= outer.minZ - tolerance &&
                inner.maxZ <= outer.maxZ + tolerance;
        }

        private static bool IntersectsAnyPlanBounds(PlanBounds bounds, List<PlanBounds> candidates, float tolerance)
        {
            foreach (PlanBounds candidate in candidates)
            {
                if (PlanBoundsIntersect(bounds, candidate, tolerance))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool PlanBoundsIntersect(PlanBounds left, PlanBounds right, float tolerance)
        {
            return left.minX < right.maxX - tolerance &&
                left.maxX > right.minX + tolerance &&
                left.minZ < right.maxZ - tolerance &&
                left.maxZ > right.minZ + tolerance;
        }

        private static Transform FindFirstChild(Transform parent, Predicate<string> predicate)
        {
            foreach (Transform child in parent)
            {
                if (predicate(child.name))
                {
                    return child;
                }
            }

            return null;
        }

        private static Vector2 TransformRelativePlanPoint(RelativeTransform transform, Vector2 localPoint)
        {
            Vector3 transformed = transform.position + transform.rotation * new Vector3(localPoint.x, 0f, localPoint.y);
            return new Vector2(transformed.x, transformed.z);
        }

        private static int FindNearestFloorEdge(PlanBounds floorBounds, Vector2 start, Vector2 end)
        {
            Vector2 midpoint = (start + end) * 0.5f;
            float south = Mathf.Abs(midpoint.y - floorBounds.minZ);
            float north = Mathf.Abs(midpoint.y - floorBounds.maxZ);
            float west = Mathf.Abs(midpoint.x - floorBounds.minX);
            float east = Mathf.Abs(midpoint.x - floorBounds.maxX);
            float min = Mathf.Min(Mathf.Min(south, north), Mathf.Min(west, east));

            if (Mathf.Approximately(min, north))
            {
                return Direction.North;
            }

            if (Mathf.Approximately(min, east))
            {
                return Direction.East;
            }

            if (Mathf.Approximately(min, west))
            {
                return Direction.West;
            }

            return Direction.South;
        }

        private static bool TryFindEndpointColumns(
            List<RelativeTransform> columns,
            Vector2 start,
            Vector2 end,
            out RelativeTransform startColumn,
            out RelativeTransform endColumn)
        {
            startColumn = default;
            endColumn = default;
            int startIndex = FindNearestColumn(columns, start, -1);
            int endIndex = FindNearestColumn(columns, end, startIndex);
            if (startIndex < 0 || endIndex < 0)
            {
                return false;
            }

            startColumn = columns[startIndex];
            endColumn = columns[endIndex];
            return true;
        }

        private static int FindNearestColumn(List<RelativeTransform> columns, Vector2 point, int excludedIndex)
        {
            int bestIndex = -1;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == excludedIndex)
                {
                    continue;
                }

                Vector2 columnPoint = new Vector2(columns[i].position.x, columns[i].position.z);
                float distance = Vector2.Distance(columnPoint, point);
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestIndex = i;
            }

            return bestDistance <= 0.2f ? bestIndex : -1;
        }

        private static float MeasureStairRiseFromUpperPivot(Bounds bounds)
        {
            if (bounds.min.y < -0.05f && Mathf.Abs(bounds.max.y) < 0.2f)
            {
                return Mathf.Abs(bounds.min.y);
            }

            return bounds.size.y;
        }

        private static bool AreRegionCellsPresent(HashSet<Vector2Int> floorCells, RectInt region)
        {
            for (int z = region.yMin; z < region.yMax; z++)
            {
                for (int x = region.xMin; x < region.xMax; x++)
                {
                    if (!floorCells.Contains(new Vector2Int(x, z)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void IncrementVertexCount(Dictionary<Vector2Int, int> counts, Vector2Int vertex)
        {
            counts.TryGetValue(vertex, out int count);
            counts[vertex] = count + 1;
        }

        private static bool TryGetAdjacentRailingEdgeForVertex(
            List<WallEdge> dropFaceEdges,
            Vector2Int vertex,
            out WallEdge edge,
            out bool vertexIsStart)
        {
            foreach (WallEdge candidate in dropFaceEdges)
            {
                GetEdgeVertices(candidate, out Vector2Int first, out Vector2Int second);
                if (first == vertex)
                {
                    edge = candidate;
                    vertexIsStart = true;
                    return true;
                }

                if (second == vertex)
                {
                    edge = candidate;
                    vertexIsStart = false;
                    return true;
                }
            }

            edge = default;
            vertexIsStart = false;
            return false;
        }

        private static void GetEdgeVertices(WallEdge edge, out Vector2Int first, out Vector2Int second)
        {
            Vector2Int cell = edge.cell;
            switch (edge.direction)
            {
                case Direction.North:
                    first = new Vector2Int(cell.x, cell.y + 1);
                    second = new Vector2Int(cell.x + 1, cell.y + 1);
                    break;
                case Direction.East:
                    first = new Vector2Int(cell.x + 1, cell.y);
                    second = new Vector2Int(cell.x + 1, cell.y + 1);
                    break;
                case Direction.South:
                    first = new Vector2Int(cell.x, cell.y);
                    second = new Vector2Int(cell.x + 1, cell.y);
                    break;
                case Direction.West:
                    first = new Vector2Int(cell.x, cell.y);
                    second = new Vector2Int(cell.x, cell.y + 1);
                    break;
                default:
                    first = cell;
                    second = cell;
                    break;
            }
        }

        private static Vector3 GridVertexToWorld(
            Vector3 origin,
            float tileSize,
            Vector2Int minCell,
            float baseX,
            float baseZ,
            Vector2Int vertex,
            float y)
        {
            return origin + new Vector3(
                baseX + (vertex.x - minCell.x) * tileSize,
                y,
                baseZ + (vertex.y - minCell.y) * tileSize);
        }

        private static Vector3 CellMinToWorld(
            Vector3 origin,
            float tileSize,
            Vector2Int minCell,
            float baseX,
            float baseZ,
            Vector2Int cell,
            float y)
        {
            return origin + new Vector3(
                baseX + (cell.x - minCell.x) * tileSize,
                y,
                baseZ + (cell.y - minCell.y) * tileSize);
        }

        private static int ComputeOpenEdges(
            HashSet<Vector2Int> floorCells,
            HashSet<WallEdge> openings,
            Vector2Int cell)
        {
            int openEdges = 0;
            if (floorCells.Contains(cell + Vector2Int.up) || openings.Contains(new WallEdge(cell, Direction.North)))
            {
                openEdges |= Direction.North;
            }

            if (floorCells.Contains(cell + Vector2Int.right) || openings.Contains(new WallEdge(cell, Direction.East)))
            {
                openEdges |= Direction.East;
            }

            if (floorCells.Contains(cell + Vector2Int.down) || openings.Contains(new WallEdge(cell, Direction.South)))
            {
                openEdges |= Direction.South;
            }

            if (floorCells.Contains(cell + Vector2Int.left) || openings.Contains(new WallEdge(cell, Direction.West)))
            {
                openEdges |= Direction.West;
            }

            return openEdges;
        }

        private static void PlaceGatewayOpenings(
            DungeonPrefabFamilyContract prefabContract,
            MeasuredDungeonContracts measuredContracts,
            PlacementValidationState validator,
            HashSet<WallEdge> openings,
            Transform parent,
            Vector3 origin,
            float tileSize,
            Vector2Int minCell,
            float baseX,
            float baseZ,
            float contractScale,
            ref DungeonGenerationStats stats,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            foreach (var opening in openings)
            {
                Vector3 cellMin = origin + new Vector3(
                    baseX + (opening.cell.x - minCell.x) * tileSize,
                    0f,
                    baseZ + (opening.cell.y - minCell.y) * tileSize);
                Vector3 cellMax = cellMin + new Vector3(tileSize, 0f, tileSize);

                GetEdgePlacement(opening, cellMin, cellMax, out Vector3 edgeA, out Vector3 edgeB, out Vector2 inwardNormal);
                if (PlaceGatewayEdge(
                        prefabContract,
                        measuredContracts,
                        validator,
                        $"gateway_{DirectionName(opening.direction).ToLowerInvariant()}_{opening.cell.x}_{opening.cell.y}",
                        parent,
                        opening,
                        edgeA,
                        edgeB,
                        inwardNormal,
                        contractScale))
                {
                    stats.gateways++;
                }
                else
                {
                    stats.rejected++;
                }

                Encapsulate(ref bounds, ref hasBounds, edgeA);
                Encapsulate(ref bounds, ref hasBounds, edgeB);
            }
        }

        private static void GetEdgePlacement(
            WallEdge edge,
            Vector3 cellMin,
            Vector3 cellMax,
            out Vector3 edgeA,
            out Vector3 edgeB,
            out Vector2 inwardNormal)
        {
            switch (edge.direction)
            {
                case Direction.North:
                    edgeA = new Vector3(cellMin.x, cellMin.y, cellMax.z);
                    edgeB = new Vector3(cellMax.x, cellMin.y, cellMax.z);
                    inwardNormal = Vector2.down;
                    break;
                case Direction.East:
                    edgeA = new Vector3(cellMax.x, cellMin.y, cellMin.z);
                    edgeB = new Vector3(cellMax.x, cellMin.y, cellMax.z);
                    inwardNormal = Vector2.left;
                    break;
                case Direction.South:
                    edgeA = cellMin;
                    edgeB = new Vector3(cellMax.x, cellMin.y, cellMin.z);
                    inwardNormal = Vector2.up;
                    break;
                case Direction.West:
                    edgeA = cellMin;
                    edgeB = new Vector3(cellMin.x, cellMin.y, cellMax.z);
                    inwardNormal = Vector2.right;
                    break;
                default:
                    edgeA = cellMin;
                    edgeB = cellMin;
                    inwardNormal = Vector2.zero;
                    break;
            }
        }

        private static bool PlaceLevelModuleCell(
            LevelModulePlacement placement,
            PlacementValidationState validator,
            string name,
            Transform parent,
            Vector2Int cell,
            Vector3 cellMin,
            float tileSize)
        {
            Vector2 position2 = CalculatePositionForRotatedBounds(
                placement.module.measured.localPlanBounds,
                placement.yRotation,
                new Vector2(cellMin.x, cellMin.z));
            var instance = InstantiatePrefab(
                placement.module.prefabPath,
                name,
                parent,
                new Vector3(position2.x, cellMin.y, position2.y),
                placement.yRotation);

            if (instance == null)
            {
                return false;
            }

            var expectedBounds = new PlanBounds(cellMin.x, cellMin.x + tileSize, cellMin.z, cellMin.z + tileSize);
            if (!ValidateLevelModuleInstance(instance, placement, expectedBounds, cell, validator, 0.18f, name))
            {
                Undo.DestroyObjectImmediate(instance);
                return false;
            }

            return true;
        }

        private static void PlacePrimitiveCell(
            DungeonPrefabFamilyContract prefabContract,
            MeasuredDungeonContracts measuredContracts,
            PlacementValidationState validator,
            Transform parent,
            Vector2Int cell,
            Vector3 cellMin,
            float tileSize,
            int openEdges,
            HashSet<WallEdge> cornerClaimedEdges,
            float contractScale,
            ref DungeonGenerationStats stats)
        {
            stats.fallback++;
            var cellRoot = CreateChild(parent, $"cell_{cell.x}_{cell.y}");
            if (!PlaceFloorCell(prefabContract, measuredContracts, validator, $"floor_{cell.x}_{cell.y}", cellRoot.transform, cell, cellMin, tileSize, contractScale))
            {
                stats.rejected++;
                return;
            }

            stats.floors++;
            Vector3 cellMax = cellMin + new Vector3(tileSize, 0f, tileSize);
            var southEdge = new WallEdge(cell, Direction.South);
            if (ShouldPlacePrimitiveWall(openEdges, cornerClaimedEdges, southEdge))
            {
                if (PlaceWallEdge(prefabContract, measuredContracts, validator, $"wall_south_{cell.x}_{cell.y}", cellRoot.transform, southEdge, cellMin, new Vector3(cellMax.x, cellMin.y, cellMin.z), Vector2.up, contractScale))
                {
                    stats.walls++;
                }
                else
                {
                    stats.rejected++;
                }
            }

            var northEdge = new WallEdge(cell, Direction.North);
            if (ShouldPlacePrimitiveWall(openEdges, cornerClaimedEdges, northEdge))
            {
                if (PlaceWallEdge(prefabContract, measuredContracts, validator, $"wall_north_{cell.x}_{cell.y}", cellRoot.transform, northEdge, new Vector3(cellMin.x, cellMin.y, cellMax.z), new Vector3(cellMax.x, cellMin.y, cellMax.z), Vector2.down, contractScale))
                {
                    stats.walls++;
                }
                else
                {
                    stats.rejected++;
                }
            }

            var westEdge = new WallEdge(cell, Direction.West);
            if (ShouldPlacePrimitiveWall(openEdges, cornerClaimedEdges, westEdge))
            {
                if (PlaceWallEdge(prefabContract, measuredContracts, validator, $"wall_west_{cell.x}_{cell.y}", cellRoot.transform, westEdge, cellMin, new Vector3(cellMin.x, cellMin.y, cellMax.z), Vector2.right, contractScale))
                {
                    stats.walls++;
                }
                else
                {
                    stats.rejected++;
                }
            }

            var eastEdge = new WallEdge(cell, Direction.East);
            if (ShouldPlacePrimitiveWall(openEdges, cornerClaimedEdges, eastEdge))
            {
                if (PlaceWallEdge(prefabContract, measuredContracts, validator, $"wall_east_{cell.x}_{cell.y}", cellRoot.transform, eastEdge, new Vector3(cellMax.x, cellMin.y, cellMin.z), new Vector3(cellMax.x, cellMin.y, cellMax.z), Vector2.left, contractScale))
                {
                    stats.walls++;
                }
                else
                {
                    stats.rejected++;
                }
            }
        }

        private static bool ShouldPlacePrimitiveWall(int openEdges, HashSet<WallEdge> cornerClaimedEdges, WallEdge edge)
        {
            return (openEdges & edge.direction) == 0 && !cornerClaimedEdges.Contains(edge);
        }

        private static Vector2 CalculatePositionForRotatedBounds(PlanBounds localBounds, float yRotation, Vector2 targetMin)
        {
            Vector2 a = Rotate2D(new Vector2(localBounds.minX, localBounds.minZ), yRotation);
            Vector2 b = Rotate2D(new Vector2(localBounds.minX, localBounds.maxZ), yRotation);
            Vector2 c = Rotate2D(new Vector2(localBounds.maxX, localBounds.minZ), yRotation);
            Vector2 d = Rotate2D(new Vector2(localBounds.maxX, localBounds.maxZ), yRotation);
            Vector2 rotatedMin = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d));
            return targetMin - rotatedMin;
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

        private static bool PlaceFloorCell(
            DungeonPrefabFamilyContract prefabContract,
            MeasuredDungeonContracts measuredContracts,
            PlacementValidationState validator,
            string name,
            Transform parent,
            Vector2Int cell,
            Vector3 cellMin,
            float tileSize,
            float contractScale)
        {
            Vector2 localMin = measuredContracts.floor.localPlanBounds.Min * contractScale;
            Vector3 position = cellMin - new Vector3(localMin.x, 0f, localMin.y);
            var instance = InstantiatePrefab(
                prefabContract.floorPrefab,
                name,
                parent,
                position,
                0f);

            if (instance == null)
            {
                return false;
            }

            var expectedBounds = new PlanBounds(cellMin.x, cellMin.x + tileSize, cellMin.z, cellMin.z + tileSize);
            if (!ValidateFloorInstance(instance, expectedBounds, cell, validator, 0.15f, name))
            {
                Undo.DestroyObjectImmediate(instance);
                return false;
            }

            return true;
        }

        private static bool PlaceWallEdge(
            DungeonPrefabFamilyContract prefabContract,
            MeasuredDungeonContracts measuredContracts,
            PlacementValidationState validator,
            string name,
            Transform parent,
            WallEdge occupancyEdge,
            Vector3 edgeA,
            Vector3 edgeB,
            Vector2 inwardNormal,
            float contractScale)
        {
            return PlaceEdgePrefab(
                measuredContracts.wall,
                prefabContract.wallPrefab,
                validator,
                name,
                parent,
                occupancyEdge,
                edgeA,
                edgeB,
                inwardNormal,
                contractScale);
        }

        private static bool PlaceGatewayEdge(
            DungeonPrefabFamilyContract prefabContract,
            MeasuredDungeonContracts measuredContracts,
            PlacementValidationState validator,
            string name,
            Transform parent,
            WallEdge occupancyEdge,
            Vector3 edgeA,
            Vector3 edgeB,
            Vector2 inwardNormal,
            float contractScale)
        {
            if (string.IsNullOrWhiteSpace(prefabContract.gatewayPrefab))
            {
                Debug.LogError($"Dungeon Lab: rejected gateway '{name}' because the active prefab family has no gateway prefab.");
                return false;
            }

            return PlaceEdgePrefab(
                measuredContracts.gateway,
                prefabContract.gatewayPrefab,
                validator,
                name,
                parent,
                occupancyEdge,
                edgeA,
                edgeB,
                inwardNormal,
                contractScale);
        }

        private static bool PlaceEdgePrefab(
            MeasuredPrefabContract measuredWallContract,
            string prefabPath,
            PlacementValidationState validator,
            string name,
            Transform parent,
            WallEdge occupancyEdge,
            Vector3 edgeA,
            Vector3 edgeB,
            Vector2 inwardNormal,
            float contractScale)
        {
            Vector2 localStart = measuredWallContract.localSegmentStart * contractScale;
            Vector2 localEnd = measuredWallContract.localSegmentEnd * contractScale;
            Vector2 localDirection = localEnd - localStart;
            Vector2 worldDirection = new Vector2(edgeB.x - edgeA.x, edgeB.z - edgeA.z);

            if (localDirection.sqrMagnitude <= 0.0001f || worldDirection.sqrMagnitude <= 0.0001f)
            {
                Debug.LogError($"Dungeon Lab: wall edge '{name}' has invalid local or world length.");
                return false;
            }

            float yRotation = CalculateYawToMap(localDirection, worldDirection);
            Vector2 transformedFace = Rotate2D(measuredWallContract.faceNormal, yRotation);

            Vector3 start = edgeA;
            if (Vector2.Dot(transformedFace.normalized, inwardNormal.normalized) < 0f)
            {
                start = edgeB;
                worldDirection = -worldDirection;
                yRotation = CalculateYawToMap(localDirection, worldDirection);
            }

            Quaternion rotation = Quaternion.Euler(0f, yRotation, 0f);
            Vector3 localStart3 = new Vector3(localStart.x, 0f, localStart.y);
            Vector3 position = start - rotation * localStart3;

            var instance = InstantiatePrefab(
                prefabPath,
                name,
                parent,
                position,
                yRotation);

            if (instance == null)
            {
                return false;
            }

            if (measuredWallContract.role == PrefabRole.Gateway)
            {
                DisableGatewayBlockingParts(instance);
            }

            if (!ValidateEdgeInstance(instance, measuredWallContract, name, occupancyEdge, edgeA, edgeB, inwardNormal, yRotation, validator))
            {
                Undo.DestroyObjectImmediate(instance);
                return false;
            }

            return true;
        }

        private static List<CornerPlacement> BuildCornerPlacements(
            DungeonPrefabFamilyContract prefabContract,
            MeasuredDungeonContracts measuredContracts,
            HashSet<Vector2Int> floorCells,
            HashSet<WallEdge> openings,
            out HashSet<WallEdge> claimedEdges)
        {
            var placements = new List<CornerPlacement>();
            claimedEdges = new HashSet<WallEdge>();

            if (!prefabContract.HasHardCornerPrefab())
            {
                return placements;
            }

            GetCellBounds(floorCells, out Vector2Int minCell, out Vector2Int maxCell);
            for (int y = minCell.y; y <= maxCell.y + 1; y++)
            {
                for (int x = minCell.x; x <= maxCell.x + 1; x++)
                {
                    var vertex = new Vector2Int(x, y);
                    if (!TryGetCornerPlacement(prefabContract, measuredContracts, floorCells, openings, vertex, out CornerPlacement placement))
                    {
                        continue;
                    }

                    placements.Add(placement);
                    claimedEdges.Add(placement.firstEdge);
                    claimedEdges.Add(placement.secondEdge);
                }
            }

            return placements;
        }

        private static void PlaceCornerPieces(
            MeasuredDungeonContracts measuredContracts,
            PlacementValidationState validator,
            List<CornerPlacement> placements,
            Transform parent,
            Vector3 origin,
            float tileSize,
            Vector2Int minCell,
            Vector2Int maxCell,
            float baseX,
            float baseZ,
            ref DungeonGenerationStats stats,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            foreach (var placement in placements)
            {
                Vector3 position = origin + new Vector3(
                    baseX + (placement.vertex.x - minCell.x) * tileSize,
                    0f,
                    baseZ + (placement.vertex.y - minCell.y) * tileSize);
                var instance = InstantiateFirstPrefab(
                    placement.prefabPaths,
                    $"corner_{placement.vertex.x}_{placement.vertex.y}",
                    parent,
                    position,
                    placement.yRotation,
                    "corner");
                if (instance != null &&
                    !ValidateCornerInstance(
                        instance,
                        measuredContracts.hardCorner,
                        $"corner_{placement.vertex.x}_{placement.vertex.y}",
                        position,
                        placement,
                        validator))
                {
                    Undo.DestroyObjectImmediate(instance);
                    stats.rejected++;
                }
                else if (instance != null)
                {
                    stats.corners++;
                    if (PlaceColumnAtCorner(
                            measuredContracts.column,
                            parent,
                            position,
                            placement,
                            ref bounds,
                            ref hasBounds))
                    {
                        stats.columns++;
                    }
                    else
                    {
                        stats.rejected++;
                    }
                }
                else
                {
                    stats.rejected++;
                }

                Encapsulate(ref bounds, ref hasBounds, position);
            }
        }

        private static bool PlaceColumnAtCorner(
            MeasuredPrefabContract measuredColumn,
            Transform parent,
            Vector3 vertexPosition,
            CornerPlacement placement,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            if (!measuredColumn.isDefined)
            {
                return false;
            }

            Vector2 measuredAnchor = Rotate2D(measuredColumn.localPlanBounds.Center, placement.yRotation);
            Vector3 position = vertexPosition - new Vector3(measuredAnchor.x, 0f, measuredAnchor.y);
            var instance = InstantiatePrefab(
                measuredColumn.prefabPath,
                $"column_{placement.vertex.x}_{placement.vertex.y}",
                parent,
                position,
                placement.yRotation);
            if (instance == null)
            {
                return false;
            }

            if (TryGetPlanBounds(instance, out PlanBounds measuredBounds))
            {
                Encapsulate(ref bounds, ref hasBounds, new Vector3(measuredBounds.minX, 0f, measuredBounds.minZ));
                Encapsulate(ref bounds, ref hasBounds, new Vector3(measuredBounds.maxX, 0f, measuredBounds.maxZ));
            }
            else
            {
                Encapsulate(ref bounds, ref hasBounds, position);
            }

            return true;
        }

        private static bool TryGetCornerPlacement(
            DungeonPrefabFamilyContract prefabContract,
            MeasuredDungeonContracts measuredContracts,
            HashSet<Vector2Int> floorCells,
            HashSet<WallEdge> openings,
            Vector2Int vertex,
            out CornerPlacement placement)
        {
            bool southWest = floorCells.Contains(new Vector2Int(vertex.x - 1, vertex.y - 1));
            bool southEast = floorCells.Contains(new Vector2Int(vertex.x, vertex.y - 1));
            bool northWest = floorCells.Contains(new Vector2Int(vertex.x - 1, vertex.y));
            bool northEast = floorCells.Contains(new Vector2Int(vertex.x, vertex.y));
            int occupiedCount = (southWest ? 1 : 0) + (southEast ? 1 : 0) + (northWest ? 1 : 0) + (northEast ? 1 : 0);

            placement = default;
            if (occupiedCount == 3)
            {
                return false;
            }

            if (occupiedCount != 1)
            {
                return false;
            }

            if (!TryGetIncidentCornerEdges(floorCells, openings, vertex, out WallEdge firstEdge, out WallEdge secondEdge))
            {
                return false;
            }

            List<string> prefabPaths = prefabContract.GetRectilinearCornerPrefabCandidates();
            if (prefabPaths.Count == 0)
            {
                return false;
            }

            int targetQuadrant;
            if (!TryGetSpecialCornerQuadrant(
                    true,
                    southWest,
                    southEast,
                    northWest,
                    northEast,
                    out targetQuadrant))
            {
                return false;
            }

            int baseQuadrant = measuredContracts.hardCorner.baseQuadrant;
            float yRotation = CalculateCornerYaw(baseQuadrant, targetQuadrant);
            if (!DoesRotatedBoundsOccupyQuadrant(measuredContracts.hardCorner.localPlanBounds, yRotation, targetQuadrant))
            {
                Debug.LogError(
                    $"Dungeon Lab: hard corner contract refused vertex {vertex}. Rotation {yRotation:0.###} from measured base {CornerQuadrantName(baseQuadrant)} does not occupy target quadrant {CornerQuadrantName(targetQuadrant)}. Leaving straight walls unclaimed.");
                return false;
            }

            placement = new CornerPlacement(
                vertex,
                prefabPaths,
                yRotation,
                targetQuadrant,
                firstEdge,
                secondEdge);
            return true;
        }

        private static bool TryGetIncidentCornerEdges(
            HashSet<Vector2Int> floorCells,
            HashSet<WallEdge> openings,
            Vector2Int vertex,
            out WallEdge firstEdge,
            out WallEdge secondEdge)
        {
            firstEdge = default;
            secondEdge = default;

            bool east = TryGetHorizontalBoundaryEdge(floorCells, vertex.x, vertex.y, out WallEdge eastEdge) &&
                !openings.Contains(eastEdge);
            bool west = TryGetHorizontalBoundaryEdge(floorCells, vertex.x - 1, vertex.y, out WallEdge westEdge) &&
                !openings.Contains(westEdge);
            bool north = TryGetVerticalBoundaryEdge(floorCells, vertex.x, vertex.y, out WallEdge northEdge) &&
                !openings.Contains(northEdge);
            bool south = TryGetVerticalBoundaryEdge(floorCells, vertex.x, vertex.y - 1, out WallEdge southEdge) &&
                !openings.Contains(southEdge);

            int horizontalCount = (east ? 1 : 0) + (west ? 1 : 0);
            int verticalCount = (north ? 1 : 0) + (south ? 1 : 0);
            if (horizontalCount != 1 || verticalCount != 1)
            {
                return false;
            }

            firstEdge = east ? eastEdge : westEdge;
            secondEdge = north ? northEdge : southEdge;
            return true;
        }

        private static bool TryGetHorizontalBoundaryEdge(
            HashSet<Vector2Int> floorCells,
            int x,
            int y,
            out WallEdge edge)
        {
            var below = new Vector2Int(x, y - 1);
            var above = new Vector2Int(x, y);
            bool hasBelow = floorCells.Contains(below);
            bool hasAbove = floorCells.Contains(above);

            edge = default;
            if (hasBelow == hasAbove)
            {
                return false;
            }

            edge = hasAbove ? new WallEdge(above, Direction.South) : new WallEdge(below, Direction.North);
            return true;
        }

        private static bool TryGetVerticalBoundaryEdge(
            HashSet<Vector2Int> floorCells,
            int x,
            int y,
            out WallEdge edge)
        {
            var left = new Vector2Int(x - 1, y);
            var right = new Vector2Int(x, y);
            bool hasLeft = floorCells.Contains(left);
            bool hasRight = floorCells.Contains(right);

            edge = default;
            if (hasLeft == hasRight)
            {
                return false;
            }

            edge = hasRight ? new WallEdge(right, Direction.West) : new WallEdge(left, Direction.East);
            return true;
        }

        private static bool TryGetSpecialCornerQuadrant(
            bool convex,
            bool southWest,
            bool southEast,
            bool northWest,
            bool northEast,
            out int quadrant)
        {
            bool keySouthWest = convex ? southWest : !southWest;
            bool keySouthEast = convex ? southEast : !southEast;
            bool keyNorthWest = convex ? northWest : !northWest;
            bool keyNorthEast = convex ? northEast : !northEast;

            if (keySouthEast)
            {
                quadrant = CornerQuadrant.SouthEast;
                return true;
            }

            if (keySouthWest)
            {
                quadrant = CornerQuadrant.SouthWest;
                return true;
            }

            if (keyNorthWest)
            {
                quadrant = CornerQuadrant.NorthWest;
                return true;
            }

            if (keyNorthEast)
            {
                quadrant = CornerQuadrant.NorthEast;
                return true;
            }

            quadrant = CornerQuadrant.SouthEast;
            return false;
        }

        private static float CalculateCornerYaw(int baseQuadrant, int targetQuadrant)
        {
            return NormalizeAngle((targetQuadrant - baseQuadrant) * 90f);
        }

        private static bool IsOffsetInQuadrant(Vector2 offset, int quadrant, float tolerance)
        {
            switch (quadrant)
            {
                case CornerQuadrant.NorthEast:
                    return offset.x > tolerance && offset.y > tolerance;
                case CornerQuadrant.SouthEast:
                    return offset.x > tolerance && offset.y < -tolerance;
                case CornerQuadrant.SouthWest:
                    return offset.x < -tolerance && offset.y < -tolerance;
                case CornerQuadrant.NorthWest:
                    return offset.x < -tolerance && offset.y > tolerance;
                default:
                    return false;
            }
        }

        private static bool DoesRotatedBoundsOccupyQuadrant(PlanBounds localBounds, float yRotation, int targetQuadrant)
        {
            Vector2 rotatedCenter = Rotate2D(localBounds.Center, yRotation);
            return IsOffsetInQuadrant(rotatedCenter, targetQuadrant, 0.05f);
        }

        private static string CornerQuadrantName(int quadrant)
        {
            switch (quadrant)
            {
                case CornerQuadrant.NorthEast:
                    return "NorthEast";
                case CornerQuadrant.SouthEast:
                    return "SouthEast";
                case CornerQuadrant.SouthWest:
                    return "SouthWest";
                case CornerQuadrant.NorthWest:
                    return "NorthWest";
                default:
                    return $"Unknown({quadrant})";
            }
        }

        private static int QuadrantFromOffset(Vector2 offset, float tolerance)
        {
            if (Mathf.Abs(offset.x) <= tolerance || Mathf.Abs(offset.y) <= tolerance)
            {
                throw new InvalidOperationException($"Cannot infer quadrant from near-axis offset {offset}.");
            }

            if (offset.x > 0f)
            {
                return offset.y > 0f ? CornerQuadrant.NorthEast : CornerQuadrant.SouthEast;
            }

            return offset.y > 0f ? CornerQuadrant.NorthWest : CornerQuadrant.SouthWest;
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
            Undo.RegisterCreatedObjectUndo(instance, $"Create {name}");
            instance.name = name;
            instance.transform.SetParent(parent);
            instance.transform.position = position;
            instance.transform.rotation = Quaternion.Euler(0f, yRotation, 0f);

            return instance;
        }

        private static bool DisableGatewayBlockingParts(GameObject instance)
        {
            bool disabledAny = false;
            foreach (var transform in instance.GetComponentsInChildren<Transform>(true))
            {
                if (transform == instance.transform)
                {
                    continue;
                }

                if (!IsGatewayBlockingPartName(transform.name))
                {
                    continue;
                }

                transform.gameObject.SetActive(false);
                disabledAny = true;
            }

            return disabledAny;
        }

        private static bool IsGatewayBlockingPartName(string name)
        {
            return name.StartsWith("P_MOD_Gateway_Door_", StringComparison.Ordinal);
        }

        private static GameObject InstantiateFirstPrefab(
            IReadOnlyList<string> prefabPaths,
            string name,
            Transform parent,
            Vector3 position,
            float yRotation,
            string role)
        {
            foreach (var prefabPath in prefabPaths)
            {
                if (string.IsNullOrWhiteSpace(prefabPath))
                {
                    continue;
                }

                var instance = InstantiatePrefab(prefabPath, name, parent, position, yRotation);
                if (instance != null)
                {
                    return instance;
                }
            }

            Debug.LogWarning($"Dungeon Lab: no valid {role} prefab could be placed for '{name}'.");
            return null;
        }

        private static bool ValidateFloorInstance(
            GameObject instance,
            PlanBounds expectedBounds,
            Vector2Int cell,
            PlacementValidationState validator,
            float tolerance,
            string name)
        {
            if (!TryGetPlanBounds(instance, out PlanBounds bounds))
            {
                Debug.LogError($"Dungeon Lab: rejected floor '{name}' because it has no measurable renderer or collider footprint.");
                return false;
            }

            if (Mathf.Abs(bounds.minX - expectedBounds.minX) <= tolerance &&
                Mathf.Abs(bounds.maxX - expectedBounds.maxX) <= tolerance &&
                Mathf.Abs(bounds.minZ - expectedBounds.minZ) <= tolerance &&
                Mathf.Abs(bounds.maxZ - expectedBounds.maxZ) <= tolerance)
            {
                return validator.TryRegisterFloor(cell, expectedBounds, name);
            }

            Debug.LogError(
                $"Dungeon Lab: rejected floor '{name}' due to footprint mismatch. Expected {expectedBounds}, measured {bounds}.");
            return false;
        }

        private static bool ValidateLevelModuleInstance(
            GameObject instance,
            LevelModulePlacement placement,
            PlanBounds expectedBounds,
            Vector2Int cell,
            PlacementValidationState validator,
            float tolerance,
            string name)
        {
            if (!TryGetFloorPlanBounds(instance, out PlanBounds bounds))
            {
                Debug.LogError($"Dungeon Lab: rejected module '{name}' because it has no measurable floor footprint.");
                return false;
            }

            if (Mathf.Abs(bounds.minX - expectedBounds.minX) > tolerance ||
                Mathf.Abs(bounds.maxX - expectedBounds.maxX) > tolerance ||
                Mathf.Abs(bounds.minZ - expectedBounds.minZ) > tolerance ||
                Mathf.Abs(bounds.maxZ - expectedBounds.maxZ) > tolerance)
            {
                Debug.LogError(
                    $"Dungeon Lab: rejected module '{name}' due to footprint mismatch. Expected {expectedBounds}, measured {bounds}, prefab {placement.module.prefabPath}.");
                return false;
            }

            int rotatedExits = RotateOpenEdges(placement.module.openEdges, placement.yRotation);
            if (rotatedExits != placement.targetOpenEdges)
            {
                Debug.LogError(
                    $"Dungeon Lab: rejected module '{name}' due to connector mismatch. Expected open {DirectionMaskName(placement.targetOpenEdges)}, measured {DirectionMaskName(rotatedExits)} after yaw {placement.yRotation:0.###}, prefab {placement.module.prefabPath}.");
                return false;
            }

            return validator.TryRegisterModule(cell, placement.targetOpenEdges, name);
        }

        private static bool ValidateEdgeInstance(
            GameObject instance,
            MeasuredPrefabContract measuredContract,
            string name,
            WallEdge occupancyEdge,
            Vector3 edgeA,
            Vector3 edgeB,
            Vector2 inwardNormal,
            float yRotation,
            PlacementValidationState validator)
        {
            if (!TryGetPlanBounds(instance, out PlanBounds bounds))
            {
                Debug.LogError($"Dungeon Lab: rejected edge '{name}' because it has no measurable renderer or collider footprint.");
                return false;
            }

            Vector2 measuredStart = TransformPlanPoint(instance.transform, measuredContract.localSegmentStart);
            Vector2 measuredEnd = TransformPlanPoint(instance.transform, measuredContract.localSegmentEnd);
            Vector2 expectedStart = new Vector2(edgeA.x, edgeA.z);
            Vector2 expectedEnd = new Vector2(edgeB.x, edgeB.z);
            const float segmentTolerance = 0.15f;
            if (!SegmentsMatch(measuredStart, measuredEnd, expectedStart, expectedEnd, segmentTolerance))
            {
                Debug.LogError(
                    $"Dungeon Lab: rejected edge '{name}' due to measured segment mismatch. Expected {FormatSegment(expectedStart, expectedEnd)}, measured {FormatSegment(measuredStart, measuredEnd)}, renderer footprint {bounds}.");
                return false;
            }

            Vector2 transformedFace = Rotate2D(measuredContract.faceNormal, yRotation).normalized;
            float faceDot = Vector2.Dot(transformedFace, inwardNormal.normalized);
            if (faceDot < 0.65f)
            {
                Debug.LogError(
                    $"Dungeon Lab: rejected edge '{name}' because its face normal points the wrong way. Expected inward {inwardNormal.normalized}, measured {transformedFace}, dot {faceDot:0.###}.");
                return false;
            }

            return validator.TryRegisterEdge(occupancyEdge, name);
        }

        private static bool ValidateCornerInstance(
            GameObject instance,
            MeasuredPrefabContract measuredContract,
            string name,
            Vector3 vertexPosition,
            CornerPlacement placement,
            PlacementValidationState validator)
        {
            if (!TryGetPlanBounds(instance, out PlanBounds bounds))
            {
                Debug.LogError($"Dungeon Lab: rejected corner '{name}' because it has no measurable renderer or collider footprint.");
                return false;
            }

            Vector2 measuredCenterOffset = new Vector2(bounds.Center.x - vertexPosition.x, bounds.Center.y - vertexPosition.z);
            if (IsOffsetInQuadrant(measuredCenterOffset, placement.targetQuadrant, 0.05f))
            {
                return validator.TryRegisterCorner(placement, name);
            }

            Debug.LogError(
                $"Dungeon Lab: rejected corner '{name}' because it occupies the wrong quadrant. Expected {CornerQuadrantName(placement.targetQuadrant)} from vertex {vertexPosition}, measured center offset {measuredCenterOffset}, local base {CornerQuadrantName(measuredContract.baseQuadrant)}.");
            return false;
        }

        private static Vector2 TransformPlanPoint(Transform transform, Vector2 localPoint)
        {
            Vector3 world = transform.TransformPoint(new Vector3(localPoint.x, 0f, localPoint.y));
            return new Vector2(world.x, world.z);
        }

        private static bool SegmentsMatch(Vector2 actualStart, Vector2 actualEnd, Vector2 expectedStart, Vector2 expectedEnd, float tolerance)
        {
            bool forward =
                Vector2.Distance(actualStart, expectedStart) <= tolerance &&
                Vector2.Distance(actualEnd, expectedEnd) <= tolerance;
            bool reverse =
                Vector2.Distance(actualStart, expectedEnd) <= tolerance &&
                Vector2.Distance(actualEnd, expectedStart) <= tolerance;
            return forward || reverse;
        }

        private static string FormatSegment(Vector2 start, Vector2 end)
        {
            return $"{FormatVector2(start)} -> {FormatVector2(end)}";
        }

        private static string FormatVector2(Vector2 value)
        {
            return $"({value.x:0.###},{value.y:0.###})";
        }

        private static string FormatVector3(Vector3 value)
        {
            return $"({value.x:0.###},{value.y:0.###},{value.z:0.###})";
        }

        private static bool TryGetPlanBounds(GameObject instance, out PlanBounds bounds)
        {
            bounds = default;
            bool initialized = false;

            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                EncapsulatePlanBounds(ref bounds, ref initialized, renderer.bounds.min);
                EncapsulatePlanBounds(ref bounds, ref initialized, renderer.bounds.max);
            }

            if (initialized)
            {
                return true;
            }

            foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
            {
                EncapsulatePlanBounds(ref bounds, ref initialized, collider.bounds.min);
                EncapsulatePlanBounds(ref bounds, ref initialized, collider.bounds.max);
            }

            return initialized;
        }

        private static bool TryGetRendererOrColliderWorldBounds(GameObject instance, out Bounds bounds)
        {
            bounds = default;
            bool initialized = false;

            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                EncapsulateWorldBounds(ref bounds, ref initialized, renderer.bounds);
            }

            if (initialized)
            {
                return true;
            }

            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
            {
                EncapsulateWorldBounds(ref bounds, ref initialized, collider.bounds);
            }

            return initialized;
        }

        private static bool TryGetFloorPlanBounds(GameObject instance, out PlanBounds bounds)
        {
            bounds = default;
            bool initialized = false;

            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (!IsFloorRenderer(renderer))
                {
                    continue;
                }

                EncapsulatePlanBounds(ref bounds, ref initialized, renderer.bounds.min);
                EncapsulatePlanBounds(ref bounds, ref initialized, renderer.bounds.max);
            }

            return initialized;
        }

        private static bool IsFloorRenderer(Renderer renderer)
        {
            Transform current = renderer.transform;
            while (current != null)
            {
                string name = current.name;
                if (name.Contains("Floor"))
                {
                    return true;
                }

                if (name.Contains("Wall") || name.Contains("Column") || name.Contains("Railing"))
                {
                    return false;
                }

                current = current.parent;
            }

            return false;
        }

        private static void EncapsulatePlanBounds(ref PlanBounds bounds, ref bool initialized, Vector3 point)
        {
            if (!initialized)
            {
                bounds = new PlanBounds(point.x, point.x, point.z, point.z);
                initialized = true;
                return;
            }

            bounds = new PlanBounds(
                Mathf.Min(bounds.minX, point.x),
                Mathf.Max(bounds.maxX, point.x),
                Mathf.Min(bounds.minZ, point.z),
                Mathf.Max(bounds.maxZ, point.z));
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

        private static DungeonPrefabContractCatalog LoadPrefabContracts()
        {
            if (!File.Exists(PrefabContractsPath))
            {
                throw new FileNotFoundException(PrefabContractsPath);
            }

            var json = File.ReadAllText(PrefabContractsPath);
            var contracts = JsonUtility.FromJson<DungeonPrefabContractCatalog>(json);

            if (contracts == null || contracts.families == null || contracts.families.Count == 0)
            {
                throw new InvalidOperationException("Prefab contract catalog has no families.");
            }

            if (contracts.cellSize <= 0f)
            {
                throw new InvalidOperationException("Prefab contract catalog cellSize must be greater than zero.");
            }

            return contracts;
        }

        private static List<PackageInventoryRecord> LoadPackageInventory()
        {
            if (!File.Exists(PackageInventoryPath))
            {
                throw new FileNotFoundException(PackageInventoryPath);
            }

            string wrappedJson = "{\"items\":" + File.ReadAllText(PackageInventoryPath) + "}";
            var inventory = JsonUtility.FromJson<PackageInventory>(wrappedJson);
            if (inventory == null || inventory.items == null)
            {
                throw new InvalidOperationException("Package inventory could not be parsed.");
            }

            return inventory.items;
        }

        private static string GetPrefabPathByName(List<PackageInventoryRecord> inventory, string prefabName)
        {
            if (TryGetPrefabPathByName(inventory, prefabName, out string prefabPath))
            {
                return prefabPath;
            }

            throw new InvalidOperationException($"Prefab '{prefabName}' was not found in {PackageInventoryPath}.");
        }

        private static bool TryGetPrefabPathByName(
            List<PackageInventoryRecord> inventory,
            string prefabName,
            out string prefabPath)
        {
            foreach (PackageInventoryRecord record in inventory)
            {
                if (record == null || record.name != prefabName || string.IsNullOrWhiteSpace(record.path))
                {
                    continue;
                }

                prefabPath = PackageAssetRoot + record.path;
                return true;
            }

            prefabPath = string.Empty;
            return false;
        }

        private static Bounds MeasurePrefabWorldBounds(string prefabName, string prefabPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException($"Missing prefab '{prefabName}' at '{prefabPath}'.");
            }

            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.hideFlags = HideFlags.HideAndDontSave;
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;

                if (!TryGetWorldBounds(instance, out Bounds bounds))
                {
                    throw new InvalidOperationException($"Prefab '{prefabName}' has no renderer or collider bounds.");
                }

                return bounds;
            }
            finally
            {
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
        }

        private static bool TryGetWorldBounds(GameObject instance, out Bounds bounds)
        {
            bounds = default;
            bool initialized = false;

            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                EncapsulateWorldBounds(ref bounds, ref initialized, renderer.bounds);
            }

            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
            {
                EncapsulateWorldBounds(ref bounds, ref initialized, collider.bounds);
            }

            return initialized;
        }

        private static void EncapsulateWorldBounds(ref Bounds bounds, ref bool initialized, Bounds candidate)
        {
            if (!initialized)
            {
                bounds = candidate;
                initialized = true;
                return;
            }

            bounds.Encapsulate(candidate);
        }

        private static void ClearGeneratedDungeon()
        {
            var existing = GameObject.Find(GeneratedRootName);
            if (existing == null)
            {
                return;
            }

            Undo.DestroyObjectImmediate(existing);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        private static void Shuffle<T>(IList<T> items, System.Random random)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int swapIndex = random.Next(i + 1);
                (items[i], items[swapIndex]) = (items[swapIndex], items[i]);
            }
        }

        private sealed class MeasuredDungeonContracts
        {
            public float cellSize;
            public MeasuredPrefabContract floor;
            public MeasuredPrefabContract wall;
            public MeasuredPrefabContract hardCorner;
            public MeasuredPrefabContract column;
            public MeasuredPrefabContract gateway;
            public MeasuredPrefabContract roundedConvex;
            public MeasuredPrefabContract roundedConcave;

            public static MeasuredDungeonContracts Build(float cellSize, DungeonPrefabFamilyContract family)
            {
                var measured = new MeasuredDungeonContracts
                {
                    floor = PrefabMeasurer.Measure(family.floorPrefab, PrefabRole.Floor),
                    wall = PrefabMeasurer.Measure(family.wallPrefab, PrefabRole.StraightWall),
                    hardCorner = PrefabMeasurer.Measure(family.cornerPrefab, PrefabRole.HardCorner),
                    column = PrefabMeasurer.Measure(family.columnPrefab, PrefabRole.Column),
                    gateway = PrefabMeasurer.Measure(family.gatewayPrefab, PrefabRole.Gateway),
                    roundedConvex = PrefabMeasurer.MeasureOptional(family.roundedConvexPrefab, PrefabRole.RoundedConvex),
                    roundedConcave = PrefabMeasurer.MeasureOptional(family.roundedConcavePrefab, PrefabRole.RoundedConcave),
                };

                measured.cellSize = MeasureCellSize(measured.floor);
                measured.hardCorner = measured.hardCorner.WithBaseQuadrant(DominantQuadrantFromBounds(measured.hardCorner.localPlanBounds));
                measured.RunSelfTests(cellSize);
                measured.ValidateRoleShapes(measured.cellSize);

                Debug.Log(
                    $"Dungeon Lab: measured prefab contracts. cellSize {measured.cellSize:0.###}; floor {measured.floor.localPlanBounds}; wall segment {FormatSegment(measured.wall.localSegmentStart, measured.wall.localSegmentEnd)}, face {FormatVector2(measured.wall.faceNormal)}; gateway segment {FormatSegment(measured.gateway.localSegmentStart, measured.gateway.localSegmentEnd)}, face {FormatVector2(measured.gateway.faceNormal)}, bounds {measured.gateway.localPlanBounds}; hard corner base {CornerQuadrantName(measured.hardCorner.baseQuadrant)}; column {measured.column.localPlanBounds}. Rounded roles registered: convex={measured.roundedConvex.isDefined}, concave={measured.roundedConcave.isDefined}.");
                return measured;
            }

            private void RunSelfTests(float expectedCellSize)
            {
                const float verifiedCellSize = 4f;
                if (Mathf.Abs(cellSize - verifiedCellSize) > 0.08f)
                {
                    throw new InvalidOperationException(
                        $"Prefab measurer self-test failed for cellSize. Expected {verifiedCellSize:0.###}, measured {cellSize:0.###}.");
                }

                if (Mathf.Abs(expectedCellSize - verifiedCellSize) > 0.08f)
                {
                    throw new InvalidOperationException(
                        $"Prefab catalog cellSize disagrees with verified contract. Expected {verifiedCellSize:0.###}, configured {expectedCellSize:0.###}.");
                }

                AssertMeasuredPlan("floor", floor.localPlanBounds, new PlanBounds(-verifiedCellSize, 0f, 0f, verifiedCellSize), 0.08f);
                AssertMeasuredVector("wall start", wall.localSegmentStart, new Vector2(-verifiedCellSize, 0f), 0.08f);
                AssertMeasuredVector("wall end", wall.localSegmentEnd, Vector2.zero, 0.08f);
                AssertMeasuredVector("wall face", wall.faceNormal, Vector2.up, 0.08f);

                if (hardCorner.baseQuadrant != CornerQuadrant.NorthWest)
                {
                    throw new InvalidOperationException(
                        $"Hard corner measurement disagrees with authored-module ground truth. Expected yaw-0 base NorthWest, measured {CornerQuadrantName(hardCorner.baseQuadrant)} from {hardCorner.localPlanBounds}.");
                }

                Debug.Log(
                    $"Dungeon Lab Tier0 Self-Test: PASS floor measured {floor.localPlanBounds} expected X[-4,0] Z[0,4]; wall segment measured {FormatSegment(wall.localSegmentStart, wall.localSegmentEnd)} expected (-4,0) -> (0,0); wall face measured {FormatVector2(wall.faceNormal)} expected (0,1); cellSize measured {cellSize:0.###} expected 4.");
            }

            private void ValidateRoleShapes(float cellSize)
            {
                Vector2 floorSize = floor.localPlanBounds.Size;
                if (Mathf.Abs(floorSize.x - cellSize) > 0.12f || Mathf.Abs(floorSize.y - cellSize) > 0.12f)
                {
                    throw new InvalidOperationException($"Floor prefab does not measure as one {cellSize:0.###}x{cellSize:0.###} cell. Measured {floor.localPlanBounds}.");
                }

                Vector2 wallSize = wall.colliderPlanBounds.Size;
                if (Mathf.Max(wallSize.x, wallSize.y) < cellSize - 0.12f || Mathf.Min(wallSize.x, wallSize.y) > cellSize * 0.55f)
                {
                    throw new InvalidOperationException($"Straight wall prefab does not measure as a one-cell thin slab. Collider bounds {wall.colliderPlanBounds}, renderer bounds {wall.localPlanBounds}.");
                }

                if (hardCorner.localPlanBounds.Size.x < cellSize * 0.3f || hardCorner.localPlanBounds.Size.y < cellSize * 0.3f)
                {
                    throw new InvalidOperationException($"Hard corner prefab does not have a usable rectilinear corner footprint. Measured {hardCorner.localPlanBounds}.");
                }
            }

            private static void AssertMeasuredPlan(string label, PlanBounds actual, PlanBounds expected, float tolerance)
            {
                if (Mathf.Abs(actual.minX - expected.minX) <= tolerance &&
                    Mathf.Abs(actual.maxX - expected.maxX) <= tolerance &&
                    Mathf.Abs(actual.minZ - expected.minZ) <= tolerance &&
                    Mathf.Abs(actual.maxZ - expected.maxZ) <= tolerance)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Prefab measurer self-test failed for {label}. Expected {expected}, measured {actual}.");
            }

            private static void AssertMeasuredVector(string label, Vector2 actual, Vector2 expected, float tolerance)
            {
                if (Vector2.Distance(actual, expected) <= tolerance)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Prefab measurer self-test failed for {label}. Expected {expected}, measured {actual}.");
            }

            private static int DominantQuadrantFromBounds(PlanBounds bounds)
            {
                return QuadrantFromOffset(bounds.Center, 0.01f);
            }

            private static float MeasureCellSize(MeasuredPrefabContract floor)
            {
                Vector2 size = floor.localPlanBounds.Size;
                if (size.x <= 0f || size.y <= 0f)
                {
                    throw new InvalidOperationException($"Floor prefab has invalid measured footprint {floor.localPlanBounds}.");
                }

                if (Mathf.Abs(size.x - size.y) > 0.08f)
                {
                    throw new InvalidOperationException($"Floor prefab is not a square cell. Measured {floor.localPlanBounds}.");
                }

                return (size.x + size.y) * 0.5f;
            }
        }

        private static class PrefabMeasurer
        {
            public static MeasuredPrefabContract MeasureOptional(string prefabPath, PrefabRole role)
            {
                if (string.IsNullOrWhiteSpace(prefabPath))
                {
                    return default;
                }

                return Measure(prefabPath, role);
            }

            public static MeasuredPrefabContract Measure(string prefabPath, PrefabRole role)
            {
                if (string.IsNullOrWhiteSpace(prefabPath))
                {
                    throw new InvalidOperationException($"Missing prefab path for role {role}.");
                }

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    throw new InvalidOperationException($"Missing prefab at '{prefabPath}' for role {role}.");
                }

                GameObject instance = null;
                try
                {
                    instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    instance.hideFlags = HideFlags.HideAndDontSave;
                    instance.transform.position = Vector3.zero;
                    instance.transform.rotation = Quaternion.identity;
                    instance.transform.localScale = Vector3.one;

                    if (role == PrefabRole.Gateway)
                    {
                        DisableGatewayBlockingParts(instance);
                    }

                    if (!TryGetPlanBounds(instance, out PlanBounds rendererBounds))
                    {
                        throw new InvalidOperationException($"Prefab '{prefabPath}' has no renderer or collider bounds.");
                    }

                    if (role == PrefabRole.LevelModule)
                    {
                        if (!TryGetFloorPlanBounds(instance, out PlanBounds floorBounds))
                        {
                            throw new InvalidOperationException($"Level module prefab '{prefabPath}' has no measurable floor footprint.");
                        }

                        rendererBounds = floorBounds;
                    }

                    if (!TryGetColliderPlanBounds(instance, out PlanBounds colliderBounds))
                    {
                        colliderBounds = rendererBounds;
                    }

                    Vector2 faceNormal = Vector2.up;
                    if (role == PrefabRole.StraightWall || role == PrefabRole.Gateway)
                    {
                        if (!TryMeasureDominantHorizontalFaceNormal(instance, out faceNormal))
                        {
                            throw new InvalidOperationException($"Could not measure a dominant horizontal face normal for {role} prefab '{prefabPath}'.");
                        }

                        faceNormal = SnapCardinal(faceNormal);
                    }

                    PlanBounds segmentBounds = role == PrefabRole.StraightWall || role == PrefabRole.Gateway ? colliderBounds : rendererBounds;
                    (Vector2 segmentStart, Vector2 segmentEnd) = MeasureSegment(segmentBounds);
                    if (role == PrefabRole.Gateway &&
                        TryMeasureGatewayAnchorSegment(instance, out Vector2 gatewayStart, out Vector2 gatewayEnd))
                    {
                        segmentStart = gatewayStart;
                        segmentEnd = gatewayEnd;
                    }

                    return new MeasuredPrefabContract(
                        true,
                        prefabPath,
                        role,
                        rendererBounds,
                        colliderBounds,
                        segmentStart,
                        segmentEnd,
                        faceNormal,
                        MeasureHeight(instance),
                        CornerQuadrant.SouthEast);
                }
                finally
                {
                    if (instance != null)
                    {
                        UnityEngine.Object.DestroyImmediate(instance);
                    }
                }
            }

            public static MeasuredLevelModuleContract MeasureLevelModule(string prefabPath, int namedOpenEdges, float cellSize)
            {
                MeasuredPrefabContract measured = Measure(prefabPath, PrefabRole.LevelModule);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    throw new InvalidOperationException($"Missing level module prefab at '{prefabPath}'.");
                }

                GameObject instance = null;
                try
                {
                    instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    instance.hideFlags = HideFlags.HideAndDontSave;
                    instance.transform.position = Vector3.zero;
                    instance.transform.rotation = Quaternion.identity;
                    instance.transform.localScale = Vector3.one;
                    int measuredOpenEdges = MeasureOpenEdgesFromWallGeometry(instance, measured.localPlanBounds, cellSize);
                    int trustedOpenEdges = ChooseTrustedModuleOpenEdges(namedOpenEdges, measuredOpenEdges);
                    return new MeasuredLevelModuleContract(prefabPath, measured, namedOpenEdges, trustedOpenEdges);
                }
                finally
                {
                    if (instance != null)
                    {
                        UnityEngine.Object.DestroyImmediate(instance);
                    }
                }
            }

            private static int ChooseTrustedModuleOpenEdges(int namedOpenEdges, int measuredOpenEdges)
            {
                if (namedOpenEdges == 0 || namedOpenEdges == Direction.All)
                {
                    return measuredOpenEdges;
                }

                // Composed module renderer bounds can include trims/caps that touch extra edges.
                // Only let geometry override the catalog when it reports a complete connector class.
                if (measuredOpenEdges == 0 ||
                    measuredOpenEdges == Direction.All ||
                    CountOpenEdges(measuredOpenEdges) != CountOpenEdges(namedOpenEdges))
                {
                    return namedOpenEdges;
                }

                return measuredOpenEdges;
            }

            private static int CountOpenEdges(int mask)
            {
                int count = 0;
                if ((mask & Direction.North) != 0)
                {
                    count++;
                }

                if ((mask & Direction.East) != 0)
                {
                    count++;
                }

                if ((mask & Direction.South) != 0)
                {
                    count++;
                }

                if ((mask & Direction.West) != 0)
                {
                    count++;
                }

                return count;
            }

            private static (Vector2 start, Vector2 end) MeasureSegment(PlanBounds bounds)
            {
                Vector2 size = bounds.Size;
                if (size.x >= size.y)
                {
                    return (new Vector2(bounds.minX, 0f), new Vector2(bounds.maxX, 0f));
                }

                return (new Vector2(0f, bounds.minZ), new Vector2(0f, bounds.maxZ));
            }

            private static bool TryMeasureGatewayAnchorSegment(GameObject instance, out Vector2 start, out Vector2 end)
            {
                start = default;
                end = default;
                if (instance.transform.childCount < 2)
                {
                    return false;
                }

                bool initialized = false;
                float minX = 0f;
                float maxX = 0f;
                float minZ = 0f;
                float maxZ = 0f;

                for (int i = 0; i < instance.transform.childCount; i++)
                {
                    Vector3 localPosition = instance.transform.GetChild(i).localPosition;
                    if (!initialized)
                    {
                        minX = localPosition.x;
                        maxX = localPosition.x;
                        minZ = localPosition.z;
                        maxZ = localPosition.z;
                        initialized = true;
                        continue;
                    }

                    minX = Mathf.Min(minX, localPosition.x);
                    maxX = Mathf.Max(maxX, localPosition.x);
                    minZ = Mathf.Min(minZ, localPosition.z);
                    maxZ = Mathf.Max(maxZ, localPosition.z);
                }

                if (!initialized)
                {
                    return false;
                }

                float spanX = maxX - minX;
                float spanZ = maxZ - minZ;
                if (spanX <= 0.001f && spanZ <= 0.001f)
                {
                    return false;
                }

                if (spanX >= spanZ)
                {
                    start = new Vector2(minX, 0f);
                    end = new Vector2(maxX, 0f);
                }
                else
                {
                    start = new Vector2(0f, minZ);
                    end = new Vector2(0f, maxZ);
                }

                return true;
            }

            private static bool TryGetColliderPlanBounds(GameObject instance, out PlanBounds bounds)
            {
                bounds = default;
                bool initialized = false;

                foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
                {
                    EncapsulatePlanBounds(ref bounds, ref initialized, collider.bounds.min);
                    EncapsulatePlanBounds(ref bounds, ref initialized, collider.bounds.max);
                }

                return initialized;
            }

            private static float MeasureHeight(GameObject instance)
            {
                bool initialized = false;
                float minY = 0f;
                float maxY = 0f;

                foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                {
                    if (!initialized)
                    {
                        minY = renderer.bounds.min.y;
                        maxY = renderer.bounds.max.y;
                        initialized = true;
                        continue;
                    }

                    minY = Mathf.Min(minY, renderer.bounds.min.y);
                    maxY = Mathf.Max(maxY, renderer.bounds.max.y);
                }

                if (!initialized)
                {
                    foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
                    {
                        if (!initialized)
                        {
                            minY = collider.bounds.min.y;
                            maxY = collider.bounds.max.y;
                            initialized = true;
                            continue;
                        }

                        minY = Mathf.Min(minY, collider.bounds.min.y);
                        maxY = Mathf.Max(maxY, collider.bounds.max.y);
                    }
                }

                return initialized ? maxY - minY : 0f;
            }

            private static bool TryMeasureDominantHorizontalFaceNormal(GameObject instance, out Vector2 faceNormal)
            {
                var buckets = new FaceNormalBuckets();

                foreach (var meshFilter in instance.GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh mesh = meshFilter.sharedMesh;
                    if (mesh == null || mesh.vertexCount == 0)
                    {
                        continue;
                    }

                    Matrix4x4 localToRoot = instance.transform.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix;
                    Vector3[] vertices = mesh.vertices;
                    int[] triangles = mesh.triangles;

                    for (int i = 0; i + 2 < triangles.Length; i += 3)
                    {
                        Vector3 a = localToRoot.MultiplyPoint3x4(vertices[triangles[i]]);
                        Vector3 b = localToRoot.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                        Vector3 c = localToRoot.MultiplyPoint3x4(vertices[triangles[i + 2]]);
                        Vector3 cross = Vector3.Cross(b - a, c - a);
                        float doubleArea = cross.magnitude;
                        if (doubleArea <= 0.000001f)
                        {
                            continue;
                        }

                        Vector3 normal = cross / doubleArea;
                        Vector2 horizontal = new Vector2(normal.x, normal.z);
                        if (horizontal.sqrMagnitude < 0.2f)
                        {
                            continue;
                        }

                        Vector3 center = (a + b + c) / 3f;
                        buckets.Add(horizontal.normalized, new Vector2(center.x, center.z), doubleArea);
                    }
                }

                if (!buckets.TryGetDominant(out faceNormal))
                {
                    faceNormal = Vector2.zero;
                    return false;
                }

                return true;
            }

            private static int MeasureOpenEdgesFromWallGeometry(GameObject instance, PlanBounds bounds, float cellSize)
            {
                if (TryMeasureOpenEdgesFromWallRenderers(instance, bounds, cellSize, out int openEdges))
                {
                    return openEdges;
                }

                float northArea = 0f;
                float eastArea = 0f;
                float southArea = 0f;
                float westArea = 0f;
                const float edgeBand = 0.7f;

                foreach (var meshFilter in instance.GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh mesh = meshFilter.sharedMesh;
                    if (mesh == null || mesh.vertexCount == 0)
                    {
                        continue;
                    }

                    Matrix4x4 localToRoot = instance.transform.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix;
                    Vector3[] vertices = mesh.vertices;
                    int[] triangles = mesh.triangles;

                    for (int i = 0; i + 2 < triangles.Length; i += 3)
                    {
                        Vector3 a = localToRoot.MultiplyPoint3x4(vertices[triangles[i]]);
                        Vector3 b = localToRoot.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                        Vector3 c = localToRoot.MultiplyPoint3x4(vertices[triangles[i + 2]]);
                        Vector3 cross = Vector3.Cross(b - a, c - a);
                        float doubleArea = cross.magnitude;
                        if (doubleArea <= 0.000001f)
                        {
                            continue;
                        }

                        Vector3 normal = cross / doubleArea;
                        Vector2 horizontal = new Vector2(normal.x, normal.z);
                        if (horizontal.sqrMagnitude < 0.2f)
                        {
                            continue;
                        }

                        Vector3 center = (a + b + c) / 3f;
                        if (center.y < 0.35f)
                        {
                            continue;
                        }

                        Vector2 horizontalNormal = horizontal.normalized;
                        if (Mathf.Abs(center.z - bounds.maxZ) <= edgeBand && Vector2.Dot(horizontalNormal, Vector2.down) > 0.45f)
                        {
                            northArea += doubleArea;
                        }

                        if (Mathf.Abs(center.x - bounds.maxX) <= edgeBand && Vector2.Dot(horizontalNormal, Vector2.left) > 0.45f)
                        {
                            eastArea += doubleArea;
                        }

                        if (Mathf.Abs(center.z - bounds.minZ) <= edgeBand && Vector2.Dot(horizontalNormal, Vector2.up) > 0.45f)
                        {
                            southArea += doubleArea;
                        }

                        if (Mathf.Abs(center.x - bounds.minX) <= edgeBand && Vector2.Dot(horizontalNormal, Vector2.right) > 0.45f)
                        {
                            westArea += doubleArea;
                        }
                    }
                }

                float wallAreaThreshold = cellSize * cellSize * 0.08f;
                int closedEdges = 0;
                if (northArea >= wallAreaThreshold)
                {
                    closedEdges |= Direction.North;
                }

                if (eastArea >= wallAreaThreshold)
                {
                    closedEdges |= Direction.East;
                }

                if (southArea >= wallAreaThreshold)
                {
                    closedEdges |= Direction.South;
                }

                if (westArea >= wallAreaThreshold)
                {
                    closedEdges |= Direction.West;
                }

                return Direction.All & ~closedEdges;
            }

            private static bool TryMeasureOpenEdgesFromWallRenderers(GameObject instance, PlanBounds bounds, float cellSize, out int openEdges)
            {
                int closedEdges = 0;
                float edgeBand = Mathf.Max(0.3f, cellSize * 0.18f);
                float minSpan = Mathf.Max(0.35f, cellSize * 0.18f);
                bool foundWallRenderer = false;

                foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                {
                    if (!IsWallRenderer(renderer))
                    {
                        continue;
                    }

                    foundWallRenderer = true;
                    PlanBounds rendererBounds = PlanBoundsFromWorldBounds(instance.transform, renderer.bounds);
                    Vector2 size = rendererBounds.Size;

                    if (Mathf.Abs(rendererBounds.minZ - bounds.minZ) <= edgeBand &&
                        size.x >= minSpan)
                    {
                        closedEdges |= Direction.South;
                    }

                    if (Mathf.Abs(rendererBounds.maxZ - bounds.maxZ) <= edgeBand &&
                        size.x >= minSpan)
                    {
                        closedEdges |= Direction.North;
                    }

                    if (Mathf.Abs(rendererBounds.minX - bounds.minX) <= edgeBand &&
                        size.y >= minSpan)
                    {
                        closedEdges |= Direction.West;
                    }

                    if (Mathf.Abs(rendererBounds.maxX - bounds.maxX) <= edgeBand &&
                        size.y >= minSpan)
                    {
                        closedEdges |= Direction.East;
                    }
                }

                openEdges = Direction.All & ~closedEdges;
                return foundWallRenderer;
            }

            private static bool IsWallRenderer(Renderer renderer)
            {
                Transform current = renderer.transform;
                while (current != null)
                {
                    string name = current.name;
                    if (name.Contains("Floor") || name.Contains("Column"))
                    {
                        return false;
                    }

                    if (name.Contains("Wall"))
                    {
                        return true;
                    }

                    current = current.parent;
                }

                return false;
            }

            private static PlanBounds PlanBoundsFromWorldBounds(Transform root, Bounds worldBounds)
            {
                Vector3 min = worldBounds.min;
                Vector3 max = worldBounds.max;
                Vector3 a = root.InverseTransformPoint(new Vector3(min.x, min.y, min.z));
                Vector3 b = root.InverseTransformPoint(new Vector3(min.x, min.y, max.z));
                Vector3 c = root.InverseTransformPoint(new Vector3(max.x, min.y, min.z));
                Vector3 d = root.InverseTransformPoint(new Vector3(max.x, min.y, max.z));
                return new PlanBounds(
                    Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)),
                    Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x)),
                    Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z)),
                    Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z)));
            }

            private struct FaceNormalBuckets
            {
                private float positiveXArea;
                private float negativeXArea;
                private float positiveZArea;
                private float negativeZArea;
                private float positiveXOffset;
                private float negativeXOffset;
                private float positiveZOffset;
                private float negativeZOffset;
                private Vector2 signedArea;

                public void Add(Vector2 normal, Vector2 center, float area)
                {
                    signedArea += normal * area;
                    if (Mathf.Abs(normal.x) >= Mathf.Abs(normal.y))
                    {
                        if (normal.x >= 0f)
                        {
                            positiveXArea += area;
                            positiveXOffset += center.x * area;
                        }
                        else
                        {
                            negativeXArea += area;
                            negativeXOffset += center.x * area;
                        }
                    }
                    else if (normal.y >= 0f)
                    {
                        positiveZArea += area;
                        positiveZOffset += center.y * area;
                    }
                    else
                    {
                        negativeZArea += area;
                        negativeZOffset += center.y * area;
                    }
                }

                public bool TryGetDominant(out Vector2 normal)
                {
                    float xArea = positiveXArea + negativeXArea;
                    float zArea = positiveZArea + negativeZArea;
                    if (xArea <= 0.0001f && zArea <= 0.0001f)
                    {
                        normal = Vector2.zero;
                        return false;
                    }

                    bool useX = xArea > zArea;
                    float positiveArea = useX ? positiveXArea : positiveZArea;
                    float negativeArea = useX ? negativeXArea : negativeZArea;
                    float sign = ChooseDominantSign(
                        positiveArea,
                        negativeArea,
                        useX ? signedArea.x : signedArea.y,
                        useX ? positiveXOffset : positiveZOffset,
                        useX ? negativeXOffset : negativeZOffset);

                    normal = useX ? new Vector2(sign, 0f) : new Vector2(0f, sign);
                    return true;
                }

                private static float ChooseDominantSign(
                    float positiveArea,
                    float negativeArea,
                    float signedArea,
                    float positiveOffset,
                    float negativeOffset)
                {
                    float maxArea = Mathf.Max(positiveArea, negativeArea);
                    if (maxArea <= 0.0001f)
                    {
                        return 1f;
                    }

                    if (positiveArea > negativeArea + maxArea * 0.05f)
                    {
                        return 1f;
                    }

                    if (negativeArea > positiveArea + maxArea * 0.05f)
                    {
                        return -1f;
                    }

                    if (Mathf.Abs(signedArea) > maxArea * 0.01f)
                    {
                        return Mathf.Sign(signedArea);
                    }

                    float positiveCenter = positiveArea > 0.0001f ? positiveOffset / positiveArea : float.NegativeInfinity;
                    float negativeCenter = negativeArea > 0.0001f ? negativeOffset / negativeArea : float.PositiveInfinity;
                    return positiveCenter >= -negativeCenter ? 1f : -1f;
                }
            }

            private static Vector2 SnapCardinal(Vector2 vector)
            {
                if (Mathf.Abs(vector.x) >= Mathf.Abs(vector.y))
                {
                    return new Vector2(Mathf.Sign(vector.x), 0f);
                }

                return new Vector2(0f, Mathf.Sign(vector.y));
            }
        }

        private sealed class LevelModuleCatalog
        {
            private readonly Dictionary<int, LevelModulePlacement> placementsByOpenEdges =
                new Dictionary<int, LevelModulePlacement>();

            public static LevelModuleCatalog Build(float cellSize)
            {
                var catalog = new LevelModuleCatalog();
                int measuredCount = 0;

                foreach (var record in LoadPackageInventory())
                {
                    if (!IsOneSidedMedBasicWallModule(record))
                    {
                        continue;
                    }

                    int namedExits = DirectionMaskFromExits(record.exits);
                    string prefabPath = PackageAssetRoot + record.path;
                    MeasuredLevelModuleContract measured = PrefabMeasurer.MeasureLevelModule(prefabPath, namedExits, cellSize);
                    measuredCount++;

                    if (measured.openEdges != namedExits)
                    {
                        Debug.LogWarning(
                            $"Dungeon Lab: module exits from geometry disagree with inventory for '{record.name}'. Inventory {DirectionMaskName(namedExits)}, measured {DirectionMaskName(measured.openEdges)}. Using measured geometry.");
                    }

                    catalog.RegisterRotations(measured);
                }

                Debug.Log(
                    $"Dungeon Lab: measured {measuredCount} OneSided med basic wall level modules; connector placements available for {catalog.placementsByOpenEdges.Count} open-edge patterns.");
                return catalog;
            }

            public bool TryGetPlacement(int openEdges, out LevelModulePlacement placement)
            {
                return placementsByOpenEdges.TryGetValue(openEdges, out placement);
            }

            private void RegisterRotations(MeasuredLevelModuleContract module)
            {
                for (int i = 0; i < 4; i++)
                {
                    float yRotation = i * 90f;
                    int targetOpenEdges = RotateOpenEdges(module.openEdges, yRotation);
                    if (targetOpenEdges == 0 || targetOpenEdges == Direction.All)
                    {
                        continue;
                    }

                    if (placementsByOpenEdges.ContainsKey(targetOpenEdges))
                    {
                        continue;
                    }

                    placementsByOpenEdges.Add(targetOpenEdges, new LevelModulePlacement(module, yRotation, targetOpenEdges));
                }
            }

            private static bool IsOneSidedMedBasicWallModule(PackageInventoryRecord record)
            {
                if (record == null ||
                    record.tier != "LEVEL_MODULE" ||
                    record.family != "Wall" ||
                    record.sided != "OneSided" ||
                    record.size != "med" ||
                    string.IsNullOrWhiteSpace(record.path) ||
                    !record.path.Contains("/03_LEVEL_MODULES/01/OneSided/Wall med/basic/"))
                {
                    return false;
                }

                if (ContainsShape(record, "convex") || ContainsShape(record, "concave") || ContainsShape(record, "half") || ContainsShape(record, "tiny"))
                {
                    return false;
                }

                return ContainsShape(record, "straight");
            }

            private static bool ContainsShape(PackageInventoryRecord record, string shape)
            {
                if (record.shape == null)
                {
                    return false;
                }

                foreach (string candidate in record.shape)
                {
                    if (candidate == shape)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        [Serializable]
        private sealed class PackageInventory
        {
            public List<PackageInventoryRecord> items = new List<PackageInventoryRecord>();
        }

        [Serializable]
        private sealed class PackageInventoryRecord
        {
            public string name = string.Empty;
            public string path = string.Empty;
            public string tier = string.Empty;
            public string family = string.Empty;
            public string sided = string.Empty;
            public string size = string.Empty;
            public List<string> shape = new List<string>();
            public string exits = string.Empty;
        }

        [Serializable]
        private sealed class DungeonPrefabContractCatalog
        {
            public float cellSize = 4f;
            public string activeFamilyId = string.Empty;
            public List<DungeonPrefabFamilyContract> families = new List<DungeonPrefabFamilyContract>();

            public DungeonPrefabFamilyContract GetActiveFamily()
            {
                if (!string.IsNullOrEmpty(activeFamilyId))
                {
                    foreach (var family in families)
                    {
                        if (family.id == activeFamilyId)
                        {
                            family.Validate();
                            return family;
                        }
                    }

                    throw new InvalidOperationException($"Active prefab family '{activeFamilyId}' was not found.");
                }

                families[0].Validate();
                return families[0];
            }
        }

        [Serializable]
        private sealed class DungeonPrefabFamilyContract
        {
            public string id = string.Empty;
            public string description = string.Empty;
            public string floorPrefab = string.Empty;
            public string wallPrefab = string.Empty;
            public string cornerPrefab = string.Empty;
            public string columnPrefab = string.Empty;
            public string roundedConvexPrefab = string.Empty;
            public string roundedConcavePrefab = string.Empty;
            public string gatewayPrefab = string.Empty;

            public void Validate()
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException("Prefab family is missing an id.");
                }

                if (string.IsNullOrWhiteSpace(floorPrefab))
                {
                    throw new InvalidOperationException($"Prefab family '{id}' is missing a floor prefab.");
                }

                if (string.IsNullOrWhiteSpace(wallPrefab))
                {
                    throw new InvalidOperationException($"Prefab family '{id}' is missing a wall prefab.");
                }

                if (string.IsNullOrWhiteSpace(columnPrefab))
                {
                    throw new InvalidOperationException($"Prefab family '{id}' is missing a column prefab.");
                }

                if (string.IsNullOrWhiteSpace(gatewayPrefab))
                {
                    throw new InvalidOperationException($"Prefab family '{id}' is missing a gateway prefab.");
                }
            }

            public bool HasHardCornerPrefab()
            {
                return !string.IsNullOrWhiteSpace(cornerPrefab);
            }

            public List<string> GetRectilinearCornerPrefabCandidates()
            {
                var candidates = new List<string>();
                AddUniquePrefabPath(candidates, cornerPrefab);

                return candidates;
            }

            private static void AddUniquePrefabPath(List<string> paths, string path)
            {
                if (string.IsNullOrWhiteSpace(path) || paths.Contains(path))
                {
                    return;
                }

                paths.Add(path);
            }
        }

        private readonly struct MeasuredPrefabContract
        {
            public readonly bool isDefined;
            public readonly string prefabPath;
            public readonly PrefabRole role;
            public readonly PlanBounds localPlanBounds;
            public readonly PlanBounds colliderPlanBounds;
            public readonly Vector2 localSegmentStart;
            public readonly Vector2 localSegmentEnd;
            public readonly Vector2 faceNormal;
            public readonly float height;
            public readonly int baseQuadrant;

            public MeasuredPrefabContract(
                bool isDefined,
                string prefabPath,
                PrefabRole role,
                PlanBounds localPlanBounds,
                PlanBounds colliderPlanBounds,
                Vector2 localSegmentStart,
                Vector2 localSegmentEnd,
                Vector2 faceNormal,
                float height,
                int baseQuadrant)
            {
                this.isDefined = isDefined;
                this.prefabPath = prefabPath;
                this.role = role;
                this.localPlanBounds = localPlanBounds;
                this.colliderPlanBounds = colliderPlanBounds;
                this.localSegmentStart = localSegmentStart;
                this.localSegmentEnd = localSegmentEnd;
                this.faceNormal = faceNormal;
                this.height = height;
                this.baseQuadrant = baseQuadrant;
            }

            public MeasuredPrefabContract WithBaseQuadrant(int quadrant)
            {
                return new MeasuredPrefabContract(
                    isDefined,
                    prefabPath,
                    role,
                    localPlanBounds,
                    colliderPlanBounds,
                    localSegmentStart,
                    localSegmentEnd,
                    faceNormal,
                    height,
                    quadrant);
            }
        }

        private enum PrefabRole
        {
            Floor,
            StraightWall,
            HardCorner,
            Column,
            Gateway,
            RoundedConvex,
            RoundedConcave,
            LevelModule,
            Railing,
            RailingColumn
        }

        private readonly struct LevelModulePlacement
        {
            public readonly MeasuredLevelModuleContract module;
            public readonly float yRotation;
            public readonly int targetOpenEdges;

            public LevelModulePlacement(MeasuredLevelModuleContract module, float yRotation, int targetOpenEdges)
            {
                this.module = module;
                this.yRotation = yRotation;
                this.targetOpenEdges = targetOpenEdges;
            }
        }

        private readonly struct MeasuredLevelModuleContract
        {
            public readonly string prefabPath;
            public readonly MeasuredPrefabContract measured;
            public readonly int namedOpenEdges;
            public readonly int openEdges;

            public MeasuredLevelModuleContract(
                string prefabPath,
                MeasuredPrefabContract measured,
                int namedOpenEdges,
                int openEdges)
            {
                this.prefabPath = prefabPath;
                this.measured = measured;
                this.namedOpenEdges = namedOpenEdges;
                this.openEdges = openEdges;
            }
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
            // cell an aerial bridge line leaves from. False when the footprint
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
            public readonly HashSet<Vector2Int> floorCells;
            public readonly List<RoomFootprint> rooms;
            public readonly List<RoomConnection> connections;
            public readonly IReadOnlyList<RoomZonePlan> roomZones;
            public readonly int connectorCandidateCount;

            public DungeonLayout(HashSet<Vector2Int> floorCells, List<RoomFootprint> rooms, List<RoomConnection> connections)
                : this(floorCells, rooms, connections, Array.Empty<RoomZonePlan>(), 0)
            {
            }

            public DungeonLayout(
                HashSet<Vector2Int> floorCells,
                List<RoomFootprint> rooms,
                List<RoomConnection> connections,
                IReadOnlyList<RoomZonePlan> roomZones,
                int connectorCandidateCount)
            {
                this.floorCells = floorCells;
                this.rooms = rooms;
                this.connections = connections;
                this.roomZones = roomZones ?? Array.Empty<RoomZonePlan>();
                this.connectorCandidateCount = connectorCandidateCount;
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

        private readonly struct RoomConnection
        {
            public readonly int fromRoom;
            public readonly int toRoom;
            public readonly List<Vector2Int> path;

            public RoomConnection(int fromRoom, int toRoom, List<Vector2Int> path)
            {
                this.fromRoom = fromRoom;
                this.toRoom = toRoom;
                this.path = path;
            }
        }

        private readonly struct LoopConnectionCandidate
        {
            public readonly int firstRoom;
            public readonly int secondRoom;
            public readonly int distance;

            public LoopConnectionCandidate(int firstRoom, int secondRoom, int distance)
            {
                this.firstRoom = firstRoom;
                this.secondRoom = secondRoom;
                this.distance = distance;
            }
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

        private readonly struct WeightedStairConnectorOption
        {
            public readonly string name;
            public readonly string prefabPath;
            public readonly int rise;
            public readonly float weight;

            public WeightedStairConnectorOption(string name, string prefabPath, int rise, float weight)
            {
                this.name = name;
                this.prefabPath = prefabPath;
                this.rise = rise;
                this.weight = weight;
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
            StairInternal
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

            public static PortGraphNode Floor(Vector2Int cell, int level)
            {
                return new PortGraphNode($"F:{cell.x},{cell.y},L{level}", PortGraphNodeKind.Floor);
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

            public PortGraphEdge(string first, string second, PortGraphEdgeKind kind)
            {
                this.first = first;
                this.second = second;
                this.kind = kind;
            }
        }

        private sealed class FloorStairPortGraph
        {
            private readonly Dictionary<string, PortGraphNode> nodes = new Dictionary<string, PortGraphNode>(StringComparer.Ordinal);
            private readonly List<PortGraphEdge> edges = new List<PortGraphEdge>();
            private readonly HashSet<string> edgeKeys = new HashSet<string>(StringComparer.Ordinal);

            public int NodeCount => nodes.Count;

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

            public bool IsGloballyConnected(out string message)
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
                    if (!adjacency.ContainsKey(edge.first) || !adjacency.ContainsKey(edge.second))
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

        private readonly struct TieredLevelPlan
        {
            public readonly Dictionary<Vector2Int, int> cellLevels;
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
            // Decision J: promontory pier cells (jut into the void) — the render
            // places dense support columns under these down to the abyss base.
            public readonly List<Vector2Int> promontoryCells;

            public TieredLevelPlan(
                Dictionary<Vector2Int, int> cellLevels,
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
                List<Vector2Int> promontoryCells)
            {
                this.archetypeName = archetypeName;
                this.cellLevels = cellLevels;
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
                this.promontoryCells = promontoryCells;
            }
        }

        private readonly struct RelativeTransform
        {
            public readonly Vector3 position;
            public readonly Quaternion rotation;

            public RelativeTransform(Vector3 position, Quaternion rotation)
            {
                this.position = position;
                this.rotation = rotation;
            }
        }

        private readonly struct RailingCandidate
        {
            public readonly RelativeTransform relative;
            public readonly Vector2 start;
            public readonly Vector2 end;
            public readonly int side;

            public RailingCandidate(RelativeTransform relative, Vector2 start, Vector2 end, int side)
            {
                this.relative = relative;
                this.start = start;
                this.end = end;
                this.side = side;
            }
        }

        private readonly struct StepFormationPlacement
        {
            public readonly RectInt room;
            public readonly StepLibraryRecord record;
            public readonly Vector3 targetCenter;
            public readonly PlanBounds roomBounds;
            public readonly PlanBounds requiredClearanceBounds;
            public readonly PlanBounds expectedFootprint;
            public readonly List<Vector2Int> footprintCells;
            public readonly List<PlanBounds> blockedBounds;
            public readonly float yRotation;
            public readonly string placementMode;
            public readonly int entranceSide;

            public StepFormationPlacement(
                RectInt room,
                StepLibraryRecord record,
                Vector3 targetCenter,
                PlanBounds roomBounds,
                PlanBounds requiredClearanceBounds,
                PlanBounds expectedFootprint,
                List<Vector2Int> footprintCells,
                List<PlanBounds> blockedBounds,
                float yRotation,
                string placementMode,
                int entranceSide)
            {
                this.room = room;
                this.record = record;
                this.targetCenter = targetCenter;
                this.roomBounds = roomBounds;
                this.requiredClearanceBounds = requiredClearanceBounds;
                this.expectedFootprint = expectedFootprint;
                this.footprintCells = footprintCells;
                this.blockedBounds = blockedBounds;
                this.yRotation = yRotation;
                this.placementMode = placementMode;
                this.entranceSide = entranceSide;
            }
        }

        private readonly struct ActiveStepFormationPlacement
        {
            public readonly RectInt room;
            public readonly int roomLevel;
            public readonly StepLibraryRecord record;
            public readonly PlanBounds roomBounds;
            public readonly PlanBounds requiredClearanceBounds;
            public readonly PlanBounds expectedFootprint;
            public readonly Vector3 pivotPosition;
            public readonly float floorY;
            public readonly float yRotation;
            public readonly string placementMode;

            public ActiveStepFormationPlacement(
                RectInt room,
                int roomLevel,
                StepLibraryRecord record,
                PlanBounds roomBounds,
                PlanBounds requiredClearanceBounds,
                PlanBounds expectedFootprint,
                Vector3 pivotPosition,
                float floorY,
                float yRotation,
                string placementMode)
            {
                this.room = room;
                this.roomLevel = roomLevel;
                this.record = record;
                this.roomBounds = roomBounds;
                this.requiredClearanceBounds = requiredClearanceBounds;
                this.expectedFootprint = expectedFootprint;
                this.pivotPosition = pivotPosition;
                this.floorY = floorY;
                this.yRotation = yRotation;
                this.placementMode = placementMode;
            }
        }

        private sealed class PlacementValidationState
        {
            private readonly Dictionary<Vector2Int, string> floors = new Dictionary<Vector2Int, string>();
            private readonly Dictionary<WallEdge, string> edges = new Dictionary<WallEdge, string>();
            private readonly Dictionary<CornerKey, string> corners = new Dictionary<CornerKey, string>();
            private readonly Dictionary<WallEdge, string> daisRailingEdges = new Dictionary<WallEdge, string>();
            private readonly Dictionary<Vector2Int, string> daisRailingColumns = new Dictionary<Vector2Int, string>();
            private readonly List<(PlanBounds footprint, string name)> stepFormations = new List<(PlanBounds footprint, string name)>();

            public int overlapCount { get; private set; }

            public bool TryRegisterFloor(Vector2Int cell, PlanBounds footprint, string name)
            {
                if (floors.TryGetValue(cell, out string existing))
                {
                    overlapCount++;
                    Debug.LogError(
                        $"Dungeon Lab: rejected floor '{name}' due to footprint overlap. Cell {cell} footprint {footprint} is already occupied by '{existing}'.");
                    return false;
                }

                floors.Add(cell, name);
                return true;
            }

            public bool TryRegisterEdge(WallEdge edge, string name)
            {
                if (edges.TryGetValue(edge, out string existing))
                {
                    overlapCount++;
                    Debug.LogError(
                        $"Dungeon Lab: rejected edge '{name}' due to footprint overlap. Edge {edge} is already occupied by '{existing}'.");
                    return false;
                }

                edges.Add(edge, name);
                return true;
            }

            public bool TryRegisterCorner(CornerPlacement placement, string name)
            {
                var key = new CornerKey(placement.vertex, placement.targetQuadrant);
                if (corners.TryGetValue(key, out string existingCorner))
                {
                    overlapCount++;
                    Debug.LogError(
                        $"Dungeon Lab: rejected corner '{name}' due to footprint overlap. Corner {key} is already occupied by '{existingCorner}'.");
                    return false;
                }

                if (edges.TryGetValue(placement.firstEdge, out string existingFirstEdge))
                {
                    overlapCount++;
                    Debug.LogError(
                        $"Dungeon Lab: rejected corner '{name}' due to footprint overlap. Claimed edge {placement.firstEdge} is already occupied by '{existingFirstEdge}'.");
                    return false;
                }

                if (edges.TryGetValue(placement.secondEdge, out string existingSecondEdge))
                {
                    overlapCount++;
                    Debug.LogError(
                        $"Dungeon Lab: rejected corner '{name}' due to footprint overlap. Claimed edge {placement.secondEdge} is already occupied by '{existingSecondEdge}'.");
                    return false;
                }

                corners.Add(key, name);
                edges.Add(placement.firstEdge, name);
                edges.Add(placement.secondEdge, name);
                return true;
            }

            public bool TryRegisterModule(Vector2Int cell, int openEdges, string name)
            {
                if (floors.TryGetValue(cell, out string existingFloor))
                {
                    overlapCount++;
                    Debug.LogError(
                        $"Dungeon Lab: rejected module '{name}' due to footprint overlap. Cell {cell} is already occupied by '{existingFloor}'.");
                    return false;
                }

                int closedEdges = Direction.All & ~openEdges;
                foreach (int direction in Direction.Cardinals)
                {
                    if ((closedEdges & direction) == 0)
                    {
                        continue;
                    }

                    var edge = new WallEdge(cell, direction);
                    if (edges.TryGetValue(edge, out string existingEdge))
                    {
                        overlapCount++;
                        Debug.LogError(
                            $"Dungeon Lab: rejected module '{name}' due to footprint overlap. Closed edge {edge} is already occupied by '{existingEdge}'.");
                        return false;
                    }
                }

                floors.Add(cell, name);
                foreach (int direction in Direction.Cardinals)
                {
                    if ((closedEdges & direction) != 0)
                    {
                        edges.Add(new WallEdge(cell, direction), name);
                    }
                }

                return true;
            }

            public bool TryRegisterDaisRailing(WallEdge edge, PlanBounds footprint, string name)
            {
                if (daisRailingEdges.TryGetValue(edge, out string existing))
                {
                    overlapCount++;
                    Debug.LogError(
                        $"Dungeon Lab: rejected dais railing '{name}' due to footprint overlap. Edge {edge} footprint {footprint} is already occupied by '{existing}'.");
                    return false;
                }

                daisRailingEdges.Add(edge, name);
                return true;
            }

            public bool TryRegisterDaisRailingColumn(Vector2Int vertex, PlanBounds footprint, string name)
            {
                if (daisRailingColumns.TryGetValue(vertex, out string existing))
                {
                    overlapCount++;
                    Debug.LogError(
                        $"Dungeon Lab: rejected dais railing column '{name}' due to footprint overlap. Vertex {vertex} footprint {footprint} is already occupied by '{existing}'.");
                    return false;
                }

                daisRailingColumns.Add(vertex, name);
                return true;
            }

            public bool TryRegisterStepFormation(
                PlanBounds footprint,
                List<Vector2Int> footprintCells,
                List<PlanBounds> blockedBounds,
                string name)
            {
                foreach (Vector2Int cell in footprintCells)
                {
                    if (!floors.TryGetValue(cell, out string existingFloor))
                    {
                        continue;
                    }

                    overlapCount++;
                    Debug.LogError(
                        $"Dungeon Lab: rejected step formation '{name}' due to footprint overlap. Cell {cell} footprint {footprint} is already occupied by floor '{existingFloor}'.");
                    return false;
                }

                foreach ((PlanBounds existingFootprint, string existingName) in stepFormations)
                {
                    if (!PlanBoundsIntersect(footprint, existingFootprint, 0.05f))
                    {
                        continue;
                    }

                    overlapCount++;
                    Debug.LogError(
                        $"Dungeon Lab: rejected step formation '{name}' due to footprint overlap. Footprint {footprint} intersects existing step formation '{existingName}' footprint {existingFootprint}.");
                    return false;
                }

                foreach (PlanBounds blocked in blockedBounds)
                {
                    if (!PlanBoundsIntersect(footprint, blocked, 0.05f))
                    {
                        continue;
                    }

                    overlapCount++;
                    Debug.LogError(
                        $"Dungeon Lab: rejected step formation '{name}' due to footprint overlap. Footprint {footprint} intersects reserved feature footprint {blocked}.");
                    return false;
                }

                stepFormations.Add((footprint, name));
                return true;
            }
        }

        private struct DungeonGenerationStats
        {
            public int floors;
            public int walls;
            public int corners;
            public int columns;
            public int gateways;
            public int dais;
            public int stairs;
            public int daisRailings;
            public int stepFormations;
            public int fallback;
            public int rejected;
            public int overlap;
        }

        private readonly struct CornerKey : IEquatable<CornerKey>
        {
            public readonly Vector2Int vertex;
            public readonly int quadrant;

            public CornerKey(Vector2Int vertex, int quadrant)
            {
                this.vertex = vertex;
                this.quadrant = quadrant;
            }

            public bool Equals(CornerKey other)
            {
                return vertex == other.vertex && quadrant == other.quadrant;
            }

            public override bool Equals(object obj)
            {
                return obj is CornerKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (vertex.GetHashCode() * 397) ^ quadrant;
                }
            }

            public override string ToString()
            {
                return $"{vertex}/{CornerQuadrantName(quadrant)}";
            }
        }

        private readonly struct CornerPlacement
        {
            public readonly Vector2Int vertex;
            public readonly IReadOnlyList<string> prefabPaths;
            public readonly float yRotation;
            public readonly int targetQuadrant;
            public readonly WallEdge firstEdge;
            public readonly WallEdge secondEdge;

            public CornerPlacement(
                Vector2Int vertex,
                IReadOnlyList<string> prefabPaths,
                float yRotation,
                int targetQuadrant,
                WallEdge firstEdge,
                WallEdge secondEdge)
            {
                this.vertex = vertex;
                this.prefabPaths = prefabPaths;
                this.yRotation = yRotation;
                this.targetQuadrant = targetQuadrant;
                this.firstEdge = firstEdge;
                this.secondEdge = secondEdge;
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

        private static int DirectionMaskFromExits(string exits)
        {
            int mask = 0;
            if (string.IsNullOrWhiteSpace(exits))
            {
                return mask;
            }

            foreach (char exit in exits)
            {
                switch (exit)
                {
                    case 'N':
                        mask |= Direction.North;
                        break;
                    case 'E':
                        mask |= Direction.East;
                        break;
                    case 'S':
                        mask |= Direction.South;
                        break;
                    case 'W':
                        mask |= Direction.West;
                        break;
                }
            }

            return mask;
        }

        private static string DirectionMaskName(int mask)
        {
            if (mask == 0)
            {
                return "(none)";
            }

            string value = string.Empty;
            if ((mask & Direction.North) != 0)
            {
                value += "N";
            }

            if ((mask & Direction.East) != 0)
            {
                value += "E";
            }

            if ((mask & Direction.South) != 0)
            {
                value += "S";
            }

            if ((mask & Direction.West) != 0)
            {
                value += "W";
            }

            return value;
        }

        private static int RotateOpenEdges(int openEdges, float yRotation)
        {
            int rotated = 0;
            foreach (int direction in Direction.Cardinals)
            {
                if ((openEdges & direction) == 0)
                {
                    continue;
                }

                rotated |= DirectionFromVector(Rotate2D(DirectionVector(direction), yRotation));
            }

            return rotated;
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

        private static class CornerQuadrant
        {
            public const int NorthEast = 0;
            public const int SouthEast = 1;
            public const int SouthWest = 2;
            public const int NorthWest = 3;
        }
    }
}

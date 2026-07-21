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
        // corridors. Phase 3 route edges declare their 4u/8u structural transition
        // type before the tier planner reserves a concrete realization.
        private const int MajorRiseLevels = 4;
        private const int DoubleMajorRiseLevels = 8;
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
        // Dais platforms (step 9, decision 37): cosmetic interior 1u platforms
        // ringed by the same step strips as zone seams, carved after level
        // assignment from a per-room RNG. Distinct class for histograms and
        // review; behaves exactly like "seam" everywhere it is consumed.
        private const string DaisStairPlacementClass = "dais";
        private const int MaxDaisPerDungeon = 2;
        private const int MaxDaisSpanCells = 2;
        // Magnificence decision J, hardened by Phase 6e: a promontory is now the
        // source-side walkable prefix of one already-declared named vista. The
        // route planner reserves it before structural fill and leaves the vista's
        // minimum clear void untouched. The canonical tier plan owns target
        // identity; the renderer still consumes only the exact projected cells.
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
        private const float EnclosedRoomChance = 0.5f;

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
            if (profile == null)
            {
                throw new InvalidOperationException(
                    $"[GENERATION_PROFILE] missing required production profile at {GenerationProfilePath}");
            }

            return profile.ToSettings();
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
                    CollectNamedPromontoryCells(levelPlan.namedPromontories),
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
                $"transitions: {levelPlan.transitionSummary}, portGraph {levelPlan.portGraphSummary}; edgeModel {report.Summary} | REJECTED {report.rejected}, OVERLAP 0.");
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
            for (int attempt = 0; attempt < Phase1LayoutAttemptLimit; attempt++)
            {
                layoutAttemptsUsed = attempt + 1;
                long routeLayoutStart = BeginPhase7OutlierStage();
                bool routeLayoutBuilt = TryBuildRouteFirstDungeonLayout(
                        dungeonSeed,
                        layoutAttemptsUsed,
                        out DungeonLayout candidateLayout,
                        out RouteTierRequirements routeRequirements,
                        out rejectionReason);
                EndPhase7OutlierStage("routeLayout", routeLayoutStart);
                if (!routeLayoutBuilt)
                {
                    RecordRejection(rejectionHistogram, rejectionReason);
                    continue;
                }

                long tieredPlanStart = BeginPhase7OutlierStage();
                bool tieredPlanBuilt = TryBuildTieredLevelPlan(
                        candidateLayout,
                        routeRequirements,
                        dungeonSeed,
                        random,
                        rejectionHistogram,
                        out layout,
                        out levelPlan,
                        out rejectionReason);
                EndPhase7OutlierStage("tieredLevelPlan", tieredPlanStart);
                if (tieredPlanBuilt)
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

        private static bool TryBuildTieredLevelPlan(
            DungeonLayout layout,
            RouteTierRequirements routeRequirements,
            int dungeonSeed,
            System.Random random,
            Dictionary<string, int> rejectionHistogram,
            out DungeonLayout acceptedLayout,
            out TieredLevelPlan plan,
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
                    random,
                    rejectionHistogram,
                    out acceptedLayout,
                    out plan,
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

            long reviewedStairsStart = BeginPhase7OutlierStage();
            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions = LoadReviewedActiveStairOptions();
            EndPhase7OutlierStage("tiered.loadReviewedStairs", reviewedStairsStart);

            for (int attempt = 0; attempt < LevelAssignmentAttempts; attempt++)
            {
                long tierAttemptStart = BeginPhase7OutlierStage();
                bool tierAttemptBuilt = TryBuildTieredLevelPlanAttempt(
                        layout,
                        routeRequirements,
                        reviewedStairOptions,
                        dungeonSeed,
                        random,
                        out acceptedLayout,
                        out plan,
                        out rejectionReason);
                EndPhase7OutlierStage("tierAttempt.total", tierAttemptStart);
                if (tierAttemptBuilt)
                {
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
            System.Random random,
            out DungeonLayout acceptedLayout,
            out TieredLevelPlan plan,
            out string rejectionReason)
        {
            acceptedLayout = default;
            plan = default;
            long zoneAndLevelsStart = BeginPhase7OutlierStage();
            RoomZoneContext zones = RoomZoneContext.Build(layout);
            bool levelsAssigned = TryAssignRoomLevels(
                    layout,
                    zones,
                    routeRequirements,
                    reviewedStairOptions,
                    out int[] zoneLevels,
                    out RouteElevationPolicy archetype,
                    out rejectionReason);
            EndPhase7OutlierStage("tierAttempt.zoneAndLevels", zoneAndLevelsStart);
            if (!levelsAssigned)
            {
                return false;
            }

            long loopConnectionsStart = BeginPhase7OutlierStage();
            DungeonLayout loopedLayout = AddLevelSafeLoopConnections(
                layout,
                zones,
                zoneLevels,
                routeRequirements,
                random,
                CurrentGenerationSettings);
            int loopEdges = CountLoopEdges(loopedLayout);
            float floorFillPercent = CalculateFloorFillPercent(loopedLayout.floorCells);
            EndPhase7OutlierStage("tierAttempt.loopConnectionsAndDensity", loopConnectionsStart);
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

            if (floorFillPercent < CurrentGenerationSettings.denseFloorplanMinFillPercent)
            {
                rejectionReason = $"floor-fill {floorFillPercent * 100f:0.#}% was below dense gate {CurrentGenerationSettings.denseFloorplanMinFillPercent * 100f:0.#}%";
                return false;
            }

            // Loop connections never change rooms or zone plans, so the zone context
            // stays valid for the looped layout; only its connection list grew.
            long connectedDeltaStart = BeginPhase7OutlierStage();
            bool connectedDeltasValid = TryValidateConnectedRoomLevelDeltas(
                loopedLayout,
                zones,
                zoneLevels,
                reviewedStairOptions,
                out rejectionReason);
            EndPhase7OutlierStage("tierAttempt.connectedDeltaValidation", connectedDeltaStart);
            if (!connectedDeltasValid)
            {
                return false;
            }

            long cellLevelFieldStart = BeginPhase7OutlierStage();
            bool cellLevelFieldBuilt = TryBuildCellLevelField(
                    loopedLayout,
                    zones,
                    zoneLevels,
                    routeRequirements,
                    reviewedStairOptions,
                    dungeonSeed,
                    random,
                    out Dictionary<Vector2Int, int> cellLevels,
                    out List<ElevationEdgeModel.TransitionEdge> transitions,
                    out RouteTransitionResolution[] routeTransitionResolutions,
                    out string stairCandidateSummary,
                    out List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs,
                    out List<DaisShowpiece> daisShowpieces,
                    out NamedVistaPromontoryResolution[] namedPromontories,
                    out RecipeResolution[] recipeResolutions,
                    out rejectionReason);
            EndPhase7OutlierStage("tierAttempt.cellLevelField", cellLevelFieldStart);
            if (!cellLevelFieldBuilt)
            {
                return false;
            }

            long postFieldValidationStart = BeginPhase7OutlierStage();
            GetLevelRange(cellLevels, out int minLevel, out int maxLevel);
            int levelCount = CountDistinctLevels(cellLevels);
            if (levelCount <= 1)
            {
                EndPhase7OutlierStage("tierAttempt.postFieldValidation", postFieldValidationStart);
                rejectionReason = $"room graph resolved to a single level (archetype {archetype})";
                return false;
            }

            if (!TryValidateTransitionLevelDeltas(cellLevels, transitions, out rejectionReason))
            {
                EndPhase7OutlierStage("tierAttempt.postFieldValidation", postFieldValidationStart);
                return false;
            }

            if (!TryBuildFloorStairPortGraph(cellLevels, transitions, out FloorStairPortGraph portGraph, out rejectionReason))
            {
                EndPhase7OutlierStage("tierAttempt.postFieldValidation", postFieldValidationStart);
                return false;
            }

            if (!portGraph.IsGloballyConnected(out string portGraphReachability))
            {
                EndPhase7OutlierStage("tierAttempt.postFieldValidation", postFieldValidationStart);
                rejectionReason = portGraphReachability;
                return false;
            }

            if (!TryResolveRouteRequirements(
                    routeRequirements,
                    loopedLayout,
                    cellLevels,
                    transitions,
                    routeTransitionResolutions,
                    out RouteRequirementResolution routeRequirementResolution,
                    out rejectionReason))
            {
                EndPhase7OutlierStage("tierAttempt.postFieldValidation", postFieldValidationStart);
                return false;
            }
            EndPhase7OutlierStage("tierAttempt.postFieldValidation", postFieldValidationStart);

            // Reported stat only (demoted from a hard gate 2026-06-10): route
            // intent now guarantees the vertical story and separately proves its
            // named vista, so this older adjacent-cell proxy remains diagnostic.
            long planAssemblyStart = BeginPhase7OutlierStage();
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
                namedPromontories,
                recipeResolutions,
                routeRequirementResolution);
            acceptedLayout = loopedLayout;
            EndPhase7OutlierStage("tierAttempt.planAssembly", planAssemblyStart);
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

        private static DungeonLayout AddLevelSafeLoopConnections(
            DungeonLayout layout,
            RoomZoneContext zones,
            IReadOnlyList<int> zoneLevels,
            RouteTierRequirements routeRequirements,
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
                    if (FindRecipePlacement(routeRequirements?.recipes, first) != null ||
                        FindRecipePlacement(routeRequirements?.recipes, second) != null)
                    {
                        // Authored recipe boundaries open only at declared
                        // thresholds; generic loops cannot add a center-routed
                        // doorway to an authored room.
                        continue;
                    }

                    if (connectedPairs.Contains(RoomPairKey(first, second)))
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
                        layout.rooms[candidate.secondRoom]) ||
                    PathTouchesProtectedCells(path, routeRequirements?.reservedVistaCells))
                {
                    continue;
                }

                // A center-to-center loop can leave either endpoint room through a
                // different split zone than the one holding its center. Validate the
                // actual doorway thresholds that the connection will bind to, so a
                // nominal 4u/8u candidate cannot become an off-grammar 5u/9u edge.
                int firstNode = zones.NodeOfCell(
                    candidate.firstRoom,
                    ThresholdCell(layout.rooms[candidate.firstRoom], path, forward: true));
                int secondNode = zones.NodeOfCell(
                    candidate.secondRoom,
                    ThresholdCell(layout.rooms[candidate.secondRoom], path, forward: false));
                int levelDelta = Mathf.Abs(zoneLevels[firstNode] - zoneLevels[secondNode]);
                if (levelDelta != 0 &&
                    levelDelta != MajorRiseLevels &&
                    levelDelta != DoubleMajorRiseLevels)
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

        private static bool PathTouchesProtectedCells(
            IReadOnlyList<Vector2Int> path,
            HashSet<Vector2Int> protectedCells)
        {
            if (path == null || protectedCells == null || protectedCells.Count == 0)
            {
                return false;
            }

            foreach (Vector2Int cell in path)
            {
                if (protectedCells.Contains(cell))
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

            // Route intent is the active constraint on the retained ascending-spine
            // elevation policy. Rooms sit on its declared 4u-major story; the
            // existing +1u split policy remains a strictly intraroom accent.
            for (int roomIndex = 0; roomIndex < layout.rooms.Count; roomIndex++)
            {
                int requiredLevel = routeRequirements.intent.nodes[roomIndex].relativeElevationLevels;
                if (requiredLevel < 0 || requiredLevel > MaxGeneratedLevel ||
                    requiredLevel % MajorRiseLevels != 0)
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

                if (requiredLevel + 1 > MaxGeneratedLevel)
                {
                    rejectionReason = $"[ROUTE_ELEVATION_REQUIREMENT] room {roomIndex} could not reserve its +1u zone below the level cap";
                    return false;
                }

                zoneLevels[raisedNode] = requiredLevel + 1;
            }

            foreach (RouteTraversalIntent edge in routeRequirements.intent.traversalEdges)
            {
                int actualRise = zoneLevels[edge.toNode] - zoneLevels[edge.fromNode];
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
            RouteTierRequirements routeRequirements,
            IReadOnlyList<ReviewedActiveStairOption> reviewedStairOptions,
            int dungeonSeed,
            System.Random random,
            out Dictionary<Vector2Int, int> cellLevels,
            out List<ElevationEdgeModel.TransitionEdge> transitions,
            out RouteTransitionResolution[] routeTransitionResolutions,
            out string stairCandidateSummary,
            out List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs,
            out List<DaisShowpiece> daisShowpieces,
            out NamedVistaPromontoryResolution[] namedPromontories,
            out RecipeResolution[] recipeResolutions,
            out string rejectionReason)
        {
            cellLevels = new Dictionary<Vector2Int, int>();
            transitions = new List<ElevationEdgeModel.TransitionEdge>();
            routeTransitionResolutions = Array.Empty<RouteTransitionResolution>();
            stairCandidateSummary = "[]";
            synthesizedStairs = new List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)>();
            daisShowpieces = new List<DaisShowpiece>();
            namedPromontories = Array.Empty<NamedVistaPromontoryResolution>();
            recipeResolutions = Array.Empty<RecipeResolution>();
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
            var protectedStructuralCells = new HashSet<Vector2Int>();
            if (routeRequirements?.reservedVistaCells != null)
            {
                protectedStructuralCells.UnionWith(routeRequirements.reservedVistaCells);
                // Treat the sight volume as protected structural space before any
                // stair/bridge/stairwell candidate is chosen. The ledger already
                // owns footprint/landing conflict rules, so this adds no parallel
                // geometry implementation.
                plannedStairLedger.Register(
                    SortedCells(routeRequirements.reservedVistaCells).ToArray(),
                    Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>());

                // The two facing boundary cells are the final-view anchors. Treat
                // them as shareable landings: route stairs may land there, while
                // stair bodies and later dais carving cannot consume or re-level
                // either endpoint.
                plannedStairLedger.Register(
                    Array.Empty<Vector2Int>(),
                    new[] { routeRequirements.vistaSourceCell },
                    new[] { routeRequirements.vistaTargetCell });
            }

            if (routeRequirements?.namedPromontoryCells != null &&
                routeRequirements.namedPromontoryCells.Length > 0)
            {
                protectedStructuralCells.UnionWith(routeRequirements.namedPromontoryCells);
                plannedStairLedger.Register(
                    routeRequirements.namedPromontoryCells,
                    Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>());
            }

            var resolvedRouteTransitions = new List<RouteTransitionResolution>();
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

            string seamStairPrefabPath = ResolveSeamStairPrefabPath();
            if (!TryRealizeRecipes(
                    routeRequirements?.recipes,
                    layout.rooms,
                    cellLevels,
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

            foreach (RecipePlacement recipePlacement in routeRequirements.recipes)
            {
                protectedStructuralCells.UnionWith(recipePlacement.roomCells);
            }

            // Seam transitions first (deterministic room order): every adjacent cell
            // pair across a zone seam carries a rise-1 step strip, so the 1u delta is
            // never freely walkable (design decision 3). The strip's geometry sits in
            // the lower cell, so that cell registers as FOOTPRINT — landings may share
            // other landings but never a footprint, which keeps contract stair landings
            // (and footprints) off the steps. The raised cell is clean floor and stays
            // a shareable landing.
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
                RouteTraversalIntent routeTransitionRequirement = default;
                bool hasRouteRequirement = routeRequirements != null &&
                    routeRequirements.TryGetTransition(
                        connection.fromRoom,
                        connection.toRoom,
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
                        out transitionIndex))
                {
                    rejectionReason = $"connection {connection.fromRoom}->{connection.toRoom} had no corridor cell pair for a 1u step strip";
                    return false;
                }

                if (delta > 1)
                {
                    ZoneArea fromNodeArea = zones.NodeArea(layout.rooms, fromNode);
                    ZoneArea toNodeArea = zones.NodeArea(layout.rooms, toNode);
                    long reviewedStairSearchStart = BeginPhase7OutlierStage();
                    bool reviewedStairChosen = TryChooseReviewedActiveStairTransition(
                        reviewedStairOptions,
                        delta,
                        maxLaneCount: MaxActiveStairLaneCount,
                        path,
                        fromNodeArea,
                        toNodeArea,
                        layout.floorCells,
                        cellLevels,
                        fromLevel,
                        toLevel,
                        random,
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
                    EndPhase7OutlierStage("cellField.reviewedStairSearch", reviewedStairSearchStart);
                    if (!reviewedStairChosen)
                    {
                        // Online synthesis fallback (step 7, decisions 16-21): the
                        // reviewed pool offered no (contract, position) fit for this
                        // corridor, so shape a staircase to the gap. Same placement
                        // search, level gates and ledger as pool contracts; the
                        // per-gap RNG keeps synthesis independent of the shared
                        // draw stream (decision 18).
                        long activeSynthesisStart = BeginPhase7OutlierStage();
                        bool activeStairSynthesized = TrySynthesizeActiveStairTransition(
                            dungeonSeed,
                            connection.fromRoom,
                            connection.toRoom,
                            delta,
                            path,
                            fromNodeArea,
                            toNodeArea,
                            layout.floorCells,
                            cellLevels,
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
                        EndPhase7OutlierStage("cellField.activeSynthesis", activeSynthesisStart);
                        bool stairwellStairSynthesized = false;
                        bool stairwellEligible = string.IsNullOrEmpty(requiredPlacementClass) ||
                            string.Equals(requiredPlacementClass, StairwellStairPlacementClass, StringComparison.Ordinal);
                        if (!activeStairSynthesized && stairwellEligible)
                        {
                            // Third tier (decision 27): a 180-degree tower on void
                            // cells beside the path, only when nothing in-corridor fit.
                            long stairwellSynthesisStart = BeginPhase7OutlierStage();
                            stairwellStairSynthesized = TrySynthesizeStairwellTransition(
                                dungeonSeed,
                                connection.fromRoom,
                                connection.toRoom,
                                delta,
                                path,
                                fromNodeArea,
                                toNodeArea,
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
                                    out synthesizedGapId);
                            EndPhase7OutlierStage("cellField.stairwellSynthesis", stairwellSynthesisStart);
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

                if (hasRouteRequirement)
                {
                    int directedRise = routeTransitionRequirement.fromNode == connection.fromRoom
                        ? toLevel - fromLevel
                        : fromLevel - toLevel;
                    resolvedRouteTransitions.Add(new RouteTransitionResolution(
                        routeTransitionRequirement.id,
                        routeTransitionRequirement.fromNode,
                        routeTransitionRequirement.toNode,
                        routeTransitionRequirement.transitionKind,
                        routeTransitionRequirement.requiredRiseLevels,
                        directedRise,
                        delta == 0 ? "level-corridor" : stairOptionPlacementClass,
                        delta == 0 ? default : stairOptionPlannedTransitionFirstCell,
                        delta == 0 ? default : stairOptionPlannedTransitionSecondCell,
                        stairOptionPlannedLowerLandingCells,
                        stairOptionPlannedUpperLandingCells,
                        stairOptionPlannedFootprintCells));
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
                synthesizedStairs,
                protectedStructuralCells);

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

            // Phase 6e: realize the route-planned source-side prefix atomically
            // after every other level-field feature. The canonical resolution
            // names the vista and target; no random room or direction is rolled.
            if (!TryResolveNamedVistaPromontory(
                routeRequirements,
                cellLevels,
                out namedPromontories,
                out rejectionReason))
            {
                return false;
            }

            List<Vector2Int> promontoryCells = CollectNamedPromontoryCells(namedPromontories);

            if (!TryValidateResolvedRecipes(
                    routeRequirements.recipes,
                    layout,
                    cellLevels,
                    transitions,
                    daisShowpieces,
                    promontoryCells,
                    recipeBaseLevels,
                    out recipeResolutions,
                    out rejectionReason))
            {
                return false;
            }

            stairCandidateSummary = FormatStairCandidateHistogram(stairCandidateCounts);

            if (backedDaisCount > 0)
            {
                stairCandidateSummary += $" backedDais:{backedDaisCount}";
            }

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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
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
                    ? "level-corridor"
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

                bool transitionFound = false;
                string actualKey = TransitionKey(actual.transitionFirstCell, actual.transitionSecondCell);
                foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
                {
                    if (string.Equals(transition.placementClass, actual.placementClass, StringComparison.Ordinal) &&
                        string.Equals(
                            TransitionKey(transition.firstCell, transition.secondCell),
                            actualKey,
                            StringComparison.Ordinal))
                    {
                        transitionFound = true;
                        break;
                    }
                }

                if (!transitionFound)
                {
                    rejectionReason = $"[ROUTE_TRANSITION_RESERVATION] edge '{required.id}' reservation had no canonical TransitionEdge consumer";
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
                    cellLevels,
                    out int bottomLevel) ||
                !TryGetRouteNodeAnchorLevel(
                    requirements.intent.topNode,
                    requirements,
                    layout,
                    cellLevels,
                    out int topLevel))
            {
                rejectionReason = "[ROUTE_ELEVATION_REQUIREMENT] declared bottom/top had no final doorway anchor levels";
                return false;
            }
            int routeClimb = topLevel - bottomLevel;
            if (bottomLevel != 0 || topLevel != MaxGeneratedLevel || routeClimb != MaxGeneratedLevel)
            {
                rejectionReason = $"[ROUTE_ELEVATION_REQUIREMENT] declared bottom/top resolved to {bottomLevel}u/{topLevel}u instead of 0u/{MaxGeneratedLevel}u";
                return false;
            }

            RouteVistaIntent vista = requirements.intent.vista;
            RoomFootprint sourceRoom = layout.rooms[vista.sourceNode];
            RoomFootprint targetRoom = layout.rooms[vista.targetNode];
            Vector2Int sourceEdge = requirements.vistaSourceCell;
            Vector2Int targetEdge = requirements.vistaTargetCell;
            if (!sourceRoom.Contains(sourceEdge) ||
                !targetRoom.Contains(targetEdge) ||
                !cellLevels.TryGetValue(sourceEdge, out int sourceLevel) ||
                !cellLevels.TryGetValue(targetEdge, out int targetLevel))
            {
                rejectionReason = "[ROUTE_VISTA_FINAL_BLOCKED] final vista endpoints did not resolve to leveled facing boundary cells";
                return false;
            }

            bool facingOpposed = requirements.vistaSourceFacing != Vector2Int.zero &&
                requirements.vistaSourceFacing == -requirements.vistaTargetFacing;
            bool reservedVolumeClear = requirements.reservedVistaCells.Count >= vista.minimumReservedVoidCells;
            foreach (Vector2Int cell in requirements.reservedVistaCells)
            {
                if (layout.floorCells.Contains(cell) || cellLevels.ContainsKey(cell))
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

        private static bool TryGetRouteNodeAnchorLevel(
            int node,
            RouteTierRequirements requirements,
            DungeonLayout layout,
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            out int routeNodeLevel)
        {
            foreach (RoomConnection connection in layout.connections)
            {
                if (!requirements.TryGetTransition(
                        connection.fromRoom,
                        connection.toRoom,
                        out _) ||
                    connection.fromRoom != node && connection.toRoom != node)
                {
                    continue;
                }

                bool forward = connection.fromRoom == node;
                Vector2Int anchor = ThresholdCell(
                    layout.rooms[node],
                    connection.path,
                    forward);
                if (cellLevels.TryGetValue(anchor, out int level))
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
            Dictionary<Vector2Int, int> cellLevels,
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
            if (intent == null || cellLevels == null || plannedCells.Length > MaximumNamedVistaPromontoryCells)
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

            if (!cellLevels.TryGetValue(requirements.vistaSourceCell, out int sourceLevel) ||
                !cellLevels.TryGetValue(requirements.vistaTargetCell, out int targetLevel) ||
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
                    cellLevels.ContainsKey(expected))
                {
                    rejectionReason = $"[ROUTE_PROMONTORY] {NamedVistaPromontoryPolicyVersion} found an occupied, off-axis, or non-contiguous planned cell {plannedCells[index]}";
                    return false;
                }
            }

            foreach (Vector2Int cell in requirements.reservedVistaCells)
            {
                if (cellLevels.ContainsKey(cell))
                {
                    rejectionReason = $"[ROUTE_PROMONTORY] {NamedVistaPromontoryPolicyVersion} found occupied remaining vista cell {cell}";
                    return false;
                }
            }

            foreach (Vector2Int cell in plannedCells)
            {
                cellLevels[cell] = sourceLevel;
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
            string requiredPlacementClass,
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
            RemoveCandidatesOutsideRequiredPlacementClass(candidates, requiredPlacementClass);
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
                RemoveCandidatesOutsideRequiredPlacementClass(candidates, requiredPlacementClass);
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
            IReadOnlyDictionary<Vector2Int, int> cellLevels,
            int fromLevel,
            int toLevel,
            string requiredPlacementClass,
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
                    cellLevels,
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
            List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> synthesizedStairs,
            HashSet<Vector2Int> protectedCells)
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

                if (PathTouchesProtectedCells(candidate.gapCells, protectedCells))
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
                    cellLevels,
                    lowerLevel,
                    higherLevel,
                    candidates);
            }

            RemovePlannedStairConflicts(candidates, plannedStairLedger);
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
                return StairConnectorSettings.Load().PrimaryStair;
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
            // Phase 6e target-aware promontories. Canonical identity stays here;
            // renderer/abyss consumers receive only the derived cell projection.
            public readonly NamedVistaPromontoryResolution[] namedPromontories;
            public readonly RecipeResolution[] recipeResolutions;
            public readonly RouteRequirementResolution routeRequirementResolution;

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
                NamedVistaPromontoryResolution[] namedPromontories,
                RecipeResolution[] recipeResolutions,
                RouteRequirementResolution routeRequirementResolution)
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
                this.namedPromontories = namedPromontories ?? Array.Empty<NamedVistaPromontoryResolution>();
                this.recipeResolutions = recipeResolutions ?? Array.Empty<RecipeResolution>();
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

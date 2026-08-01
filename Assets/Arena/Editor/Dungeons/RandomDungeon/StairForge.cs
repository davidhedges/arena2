using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    /// <summary>
    /// Stair-forge step 6: the offline forge tool. Assembles staircases from the
    /// measured piece library by walking a cursor over (cell, direction, level)
    /// and emits a prefab + contract pair FROM THE SAME PLACEMENT DATA, so the
    /// two can never disagree. Output lands in a review queue
    /// (Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/Stairs/Forged + forged_stair_contracts.json with
    /// reviewStatus "pending"); a human flips entries to "reviewed" before they
    /// join the active pool on equal cost terms with hand-authored contracts.
    ///
    /// Grammar (design step 6): segments = flight (1|2|3|4u rise, uniform
    /// steepness per staircase), flat span (half-cell parity pad), turn-90
    /// landing. Curved pieces are not in the measured library yet, so the curve
    /// segment is deliberately absent. Search is cost-based (pieces + turns +
    /// detour cells); the minimal candidate wins, with a small flourish chance
    /// for an ornate candidate within 2x the minimal detour.
    /// </summary>
    internal static class StairForge
    {
        private const string StepPieceLibraryPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/step_piece_library.json";
        private const string ForgedContractsPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/forged_stair_contracts.json";
        private const string ForgedPrefabFolder = "Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/Stairs/Forged";
        private const string ReviewQueueRootName = "DungeonLab_ForgeReviewQueue";
        private const string ContextFloorName = "P_MOD_Floor_01_O_straight_med";

        private const float CellSize = 4f;
        private const float HalfCell = 2f;
        internal const float LevelHeight = 1f;
        private const float MinHeadroomUnits = 3f;
        private const int TurnCost = 3;
        private const int DetourCellCost = 2;
        private const float FlourishChance = 0.12f;
        // Same dungeon, same forge: a fixed seed keeps re-runs byte-identical
        // until the batch spec or code changes.
        private const int BatchSeed = 20260611;
        // Deck bottom caps sink below the walk slab so the two faces never fight.
        private const float DeckBottomCapSink = 0.1f;

        [MenuItem("Tools/Dungeon Lab/Forge Staircases (Batch)")]
        public static void ForgeDefaultBatch()
        {
            ForgeBatch(DefaultBatchRequests(), BatchSeed);
        }

        private static List<ForgeRequest> DefaultBatchRequests()
        {
            return new List<ForgeRequest>
            {
                // rise 2 for a side-by-side with the hand-authored primary stair.
                new ForgeRequest(2, 1, ForgeSideStyle.Walled),
                // Odd rises are the gap-fillers: no hand-authored contracts exist,
                // and reviewed odd-rise output also unlocks the odd corridor deltas
                // the planner currently rejects.
                new ForgeRequest(3, 1, ForgeSideStyle.Walled),
                new ForgeRequest(3, 2, ForgeSideStyle.Walled),
                new ForgeRequest(3, 1, ForgeSideStyle.Bridge),
                new ForgeRequest(4, 1, ForgeSideStyle.Walled),
                new ForgeRequest(4, 1, ForgeSideStyle.Walled, forceTurn: true),
                new ForgeRequest(5, 1, ForgeSideStyle.Walled),
                new ForgeRequest(5, 2, ForgeSideStyle.Walled),
                new ForgeRequest(5, 1, ForgeSideStyle.Bridge),
                new ForgeRequest(6, 1, ForgeSideStyle.Walled),
                new ForgeRequest(6, 1, ForgeSideStyle.Walled, forceTurn: true),
                // Rises 7-10 complete the reviewed per-transition vocabulary:
                // even a topology at the 40u global ceiling reaches it through
                // several transitions rather than one envelope-spanning stair. With full
                // 0..10 tier spans common, these were the dominant remaining
                // rejection ("no reviewed contract for rise N"). Tall straights get
                // long (4-5 cells), so each tall rise also ships a turn variant
                // that folds the footprint; the tall bridges are the spectacle
                // pieces (and grist for the aerial-bridge phase).
                new ForgeRequest(7, 1, ForgeSideStyle.Walled),
                new ForgeRequest(7, 1, ForgeSideStyle.Walled, forceTurn: true),
                new ForgeRequest(8, 1, ForgeSideStyle.Walled),
                new ForgeRequest(8, 2, ForgeSideStyle.Walled),
                new ForgeRequest(8, 1, ForgeSideStyle.Bridge),
                new ForgeRequest(9, 1, ForgeSideStyle.Walled),
                new ForgeRequest(9, 1, ForgeSideStyle.Walled, forceTurn: true),
                new ForgeRequest(10, 1, ForgeSideStyle.Walled),
                new ForgeRequest(10, 1, ForgeSideStyle.Walled, forceTurn: true),
                new ForgeRequest(10, 1, ForgeSideStyle.Bridge),
            };
        }

        // Dressing refresh (2026-06-13): reviewed prefabs are frozen assets, so
        // kit improvements (e.g. bridge bottom caps) never reach them. This
        // re-derives every reviewed batch design deterministically and rebuilds
        // the PREFAB in place ONLY when the regenerated contract is identical —
        // a pure dressing delta. Geometry drift is flagged for the normal
        // flip-to-pending re-review loop instead of silently adopted.
        [MenuItem("Tools/Dungeon Lab/Reforge Reviewed Staircases (Dressing Refresh)")]
        public static void ReforgeReviewedStaircases()
        {
            ForgePieceLibrary library = ForgePieceLibrary.Load();
            string missing = library.DescribeMissingCategories();
            if (!string.IsNullOrEmpty(missing))
            {
                Debug.LogError($"Dungeon Lab Reforge: the measured piece library is missing {missing}. Run the metrology tool first.");
                return;
            }

            if (!File.Exists(ForgedContractsPath))
            {
                Debug.Log("Dungeon Lab Reforge: no forged contracts file; nothing to refresh.");
                return;
            }

            JObject contractsRoot = JObject.Parse(File.ReadAllText(ForgedContractsPath));
            if (!(contractsRoot["contracts"] is JArray existing))
            {
                Debug.Log("Dungeon Lab Reforge: contracts file has no contracts array.");
                return;
            }

            var refreshed = new List<string>();
            var drifted = new List<string>();
            var failed = new List<string>();
            foreach (ForgeRequest request in DefaultBatchRequests())
            {
                try
                {
                    switch (ClassifyReviewedRefresh(request, library, existing, out string name, out ForgedStaircasePlan stairPlan))
                    {
                        case ReviewedRefreshOutcome.Identical:
                            MaterializePrefab(stairPlan);
                            string error = ElevationEdgeModel.ValidateForgedContractRoundTrip(stairPlan.contract, LevelHeight);
                            if (!string.IsNullOrEmpty(error))
                            {
                                failed.Add($"{name}: round-trip after refresh: {error}");
                            }
                            else
                            {
                                refreshed.Add(name);
                            }

                            break;
                        case ReviewedRefreshOutcome.Drifted:
                            drifted.Add(name);
                            break;
                    }
                }
                catch (Exception exception)
                {
                    failed.Add($"{request.Identity()}: {exception.Message}");
                }
            }

            Debug.Log(
                $"Dungeon Lab Reforge: refreshed {refreshed.Count} reviewed prefab(s) in place (contracts identical)." +
                (refreshed.Count > 0 ? $" [{string.Join(", ", refreshed)}]" : string.Empty) +
                (drifted.Count > 0 ? $" GEOMETRY DRIFT (untouched — flip to pending + re-forge to adopt): {string.Join(", ", drifted)}." : string.Empty) +
                (failed.Count > 0 ? $" FAILED: {string.Join(" | ", failed)}" : string.Empty));
        }

        private enum ReviewedRefreshOutcome
        {
            NotReviewed,
            Identical,
            Drifted,
        }

        private static ReviewedRefreshOutcome ClassifyReviewedRefresh(
            ForgeRequest request,
            ForgePieceLibrary library,
            JArray existing,
            out string name,
            out ForgedStaircasePlan stairPlan)
        {
            name = request.Identity();
            stairPlan = null;
            var rng = new System.Random(BatchSeed ^ StableHash(request.Identity()));
            ForgeStyle style = SampleStyle(request, library, rng, out _);
            if (style == null)
            {
                return ReviewedRefreshOutcome.NotReviewed;
            }

            List<ForgeCandidate> candidates = EnumerateCandidates(request, style, library);
            if (candidates.Count == 0)
            {
                return ReviewedRefreshOutcome.NotReviewed;
            }

            ForgeCandidate winner = PickWinner(candidates, request, rng);
            name = $"forge_r{request.rise}_l{request.lanes}_{StyleTag(request.style)}_{winner.shapeTag}_s{style.steepness}";
            if (!(FindContract(existing, name) is JObject stored) ||
                !string.Equals(stored.Value<string>("reviewStatus"), "reviewed", StringComparison.Ordinal))
            {
                return ReviewedRefreshOutcome.NotReviewed;
            }

            stairPlan = BuildStaircasePlan(name, request, style, winner, library);
            // The stored contract round-tripped through the JSON file (doubles);
            // the regenerated one is float-backed. Normalize through the same
            // serializer before comparing, or every float field reads as drift.
            var comparable = JObject.Parse(stairPlan.contract.ToString(Unity.Plastic.Newtonsoft.Json.Formatting.None));
            comparable["reviewStatus"] = "reviewed";
            return JToken.DeepEquals(comparable, stored)
                ? ReviewedRefreshOutcome.Identical
                : ReviewedRefreshOutcome.Drifted;
        }

        // Headless dry-run of the dressing refresh: classifies every reviewed
        // batch design WITHOUT touching prefabs, so the smoke harness can prove
        // what the editor tool will do before anyone clicks it.
        internal static string DescribeReviewedRefreshPlan()
        {
            ForgePieceLibrary library = LoadSynthesisLibrary();
            string missing = library.DescribeMissingCategories();
            if (!string.IsNullOrEmpty(missing))
            {
                return $"library missing {missing}";
            }

            if (!File.Exists(ForgedContractsPath))
            {
                return "no forged contracts file";
            }

            JObject contractsRoot = JObject.Parse(File.ReadAllText(ForgedContractsPath));
            if (!(contractsRoot["contracts"] is JArray existing))
            {
                return "no contracts array";
            }

            var identical = new List<string>();
            var drifted = new List<string>();
            foreach (ForgeRequest request in DefaultBatchRequests())
            {
                switch (ClassifyReviewedRefresh(request, library, existing, out string name, out _))
                {
                    case ReviewedRefreshOutcome.Identical:
                        identical.Add(name);
                        break;
                    case ReviewedRefreshOutcome.Drifted:
                        drifted.Add(name);
                        break;
                }
            }

            return $"would refresh {identical.Count}: [{string.Join(", ", identical)}]" +
                (drifted.Count > 0 ? $" | DRIFTED {drifted.Count}: [{string.Join(", ", drifted)}]" : string.Empty);
        }

        [MenuItem("Tools/Dungeon Lab/Review Selected Forged Staircase")]
        public static void ReviewSelectedForgedStaircase()
        {
            if (!TryResolveSelectedForgedStaircase(out string selectedName, out string selectedPrefabPath, out string selectionError))
            {
                EditorUtility.DisplayDialog("Review Forged Staircase", selectionError, "OK");
                Debug.LogError($"Dungeon Lab Forge Review: {selectionError}");
                return;
            }

            if (!File.Exists(ForgedContractsPath))
            {
                string missingError = $"Missing forged stair contracts file at '{ForgedContractsPath}'. Run Tools > Dungeon Lab > Forge Staircases (Batch) first.";
                EditorUtility.DisplayDialog("Review Forged Staircase", missingError, "OK");
                Debug.LogError($"Dungeon Lab Forge Review: {missingError}");
                return;
            }

            JObject contractsRoot = JObject.Parse(File.ReadAllText(ForgedContractsPath));
            if (!(contractsRoot["contracts"] is JArray contracts))
            {
                string malformedError = $"Forged stair contracts file '{ForgedContractsPath}' has no contracts array.";
                EditorUtility.DisplayDialog("Review Forged Staircase", malformedError, "OK");
                Debug.LogError($"Dungeon Lab Forge Review: {malformedError}");
                return;
            }

            List<JObject> matches = contracts
                .OfType<JObject>()
                .Where(contract => MatchesSelectedForgedContract(contract, selectedName, selectedPrefabPath))
                .ToList();

            if (matches.Count != 1)
            {
                string matchError = matches.Count == 0
                    ? $"No forged stair contract matched the selected staircase '{selectedName}'."
                    : $"Selection '{selectedName}' matched {matches.Count} forged stair contracts; refusing to update an ambiguous reviewStatus.";
                EditorUtility.DisplayDialog("Review Forged Staircase", matchError, "OK");
                Debug.LogError($"Dungeon Lab Forge Review: {matchError}");
                return;
            }

            JObject match = matches[0];
            string contractName = match.Value<string>("name") ?? selectedName;
            string priorStatus = match.Value<string>("reviewStatus") ?? string.Empty;
            match["reviewStatus"] = "reviewed";

            File.WriteAllText(ForgedContractsPath, contractsRoot.ToString(Unity.Plastic.Newtonsoft.Json.Formatting.Indented));
            AssetDatabase.ImportAsset(ForgedContractsPath);
            Debug.Log($"Dungeon Lab Forge Review: marked forged stair contract '{contractName}' reviewStatus \"reviewed\" (was \"{priorStatus}\").");
        }

        [MenuItem("Tools/Dungeon Lab/Review Selected Forged Staircase", true)]
        private static bool CanReviewSelectedForgedStaircase()
        {
            return TryResolveSelectedForgedStaircase(out _, out _, out _);
        }

        private static void ForgeBatch(IReadOnlyList<ForgeRequest> requests, int seed)
        {
            ForgePieceLibrary library = ForgePieceLibrary.Load();
            string missing = library.DescribeMissingCategories();
            if (!string.IsNullOrEmpty(missing))
            {
                Debug.LogError(
                    $"Dungeon Lab Forge: the measured piece library is missing {missing}. " +
                    "Run Tools > Dungeon Lab > Measure Step Piece Library first (the metrology pass now ingests these families), then forge again.");
                return;
            }

            EnsureForgedFolder();
            JObject contractsRoot = LoadOrCreateContractsRoot();
            var existing = (JArray)contractsRoot["contracts"];
            var emitted = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();
            var galleryPrefabs = new List<(string name, string prefabPath, int rise, int runCells)>();

            foreach (ForgeRequest request in requests)
            {
                // Per-request RNG keyed by the request identity: adding requests to
                // the batch never reshuffles earlier outputs.
                var rng = new System.Random(seed ^ StableHash(request.Identity()));
                ForgeStyle style = SampleStyle(request, library, rng, out string styleError);
                if (style == null)
                {
                    failed.Add($"{request.Identity()}: {styleError}");
                    continue;
                }

                List<ForgeCandidate> candidates = EnumerateCandidates(request, style, library);
                if (candidates.Count == 0)
                {
                    failed.Add($"{request.Identity()}: no candidate shape fits (steepness {style.steepness})");
                    continue;
                }

                ForgeCandidate winner = PickWinner(candidates, request, rng);
                string name = $"forge_r{request.rise}_l{request.lanes}_{StyleTag(request.style)}_{winner.shapeTag}_s{style.steepness}";

                if (FindContract(existing, name) is JObject prior &&
                    string.Equals(prior.Value<string>("reviewStatus"), "reviewed", StringComparison.Ordinal))
                {
                    skipped.Add($"{name} (already human-reviewed; not regenerated)");
                    continue;
                }

                try
                {
                    JObject contract = BuildPrefabAndContract(name, request, style, winner, library);
                    string error = ElevationEdgeModel.ValidateForgedContractRoundTrip(contract, LevelHeight);
                    if (!string.IsNullOrEmpty(error))
                    {
                        AssetDatabase.DeleteAsset(PrefabPathFor(name));
                        failed.Add($"{name}: round-trip validation failed: {error}");
                        continue;
                    }

                    ReplaceContract(existing, name, contract);
                    emitted.Add(name);
                    galleryPrefabs.Add((name, PrefabPathFor(name), request.rise, winner.footprint.Count));
                }
                catch (Exception exception)
                {
                    AssetDatabase.DeleteAsset(PrefabPathFor(name));
                    failed.Add($"{name}: {exception.Message}");
                }
            }

            File.WriteAllText(ForgedContractsPath, contractsRoot.ToString(Unity.Plastic.Newtonsoft.Json.Formatting.Indented));
            AssetDatabase.ImportAsset(ForgedContractsPath);
            BuildReviewGallery(galleryPrefabs);

            Debug.Log(
                $"Dungeon Lab Forge: emitted {emitted.Count} staircase(s) into the review queue " +
                $"({ForgedPrefabFolder}, contracts in {ForgedContractsPath}, reviewStatus \"pending\")." +
                (skipped.Count > 0 ? $" Skipped {skipped.Count} already-reviewed: {string.Join(", ", skipped)}." : string.Empty) +
                (failed.Count > 0 ? $" FAILED {failed.Count}: {string.Join(" | ", failed)}" : string.Empty) +
                $" Review the '{ReviewQueueRootName}' scene gallery, then flip reviewStatus to \"reviewed\" per keeper to activate it.");
        }

        // ---------------------------------------------------------------------
        // Style tuple (design decision: sampled ONCE per staircase).
        // ---------------------------------------------------------------------

        private sealed class ForgeStyle
        {
            public int steepness;
            public ForgePiece flight;
            public ForgePiece stairRailing;     // may be null (warned)
            public ForgePiece sideCover;        // walled style; may be null (warned)
            public ForgePiece botCap;           // bridge style; may be null (warned)

            // Same-angle doubled piece and its dressing (user preference: two 1u
            // strips in a row read better as one 2u flight — same slope, fewer
            // tread seams, full-length railing, wider column spacing). Null when
            // the library has no piece at exactly twice the rise and the same
            // measured angle.
            public ForgePiece pairFlight;
            public ForgePiece pairRailing;
            public ForgePiece pairCover;
            public ForgePiece pairBotCap;
        }

        private static ForgeStyle SampleStyle(ForgeRequest request, ForgePieceLibrary library, System.Random rng, out string error)
        {
            error = string.Empty;
            List<int> divisors = library.FlightRises().Where(s => request.rise % s == 0).OrderBy(s => s).ToList();
            if (request.forceTurn)
            {
                // A turn needs at least one flight per leg.
                divisors = divisors.Where(s => request.rise / s >= 2).ToList();
            }

            if (divisors.Count == 0)
            {
                error = $"no measured flight rise divides {request.rise}" + (request.forceTurn ? " into two legs" : string.Empty);
                return null;
            }

            // Weighted toward steeper flights: fewer pieces reads simpler, which is
            // the default aesthetic; the 1u family still appears for tall rises.
            int totalWeight = divisors.Sum();
            int roll = rng.Next(totalWeight);
            int steepness = divisors[divisors.Count - 1];
            foreach (int s in divisors)
            {
                roll -= s;
                if (roll < 0)
                {
                    steepness = s;
                    break;
                }
            }

            return BuildStyleForSteepness(steepness, library, out error);
        }

        private static ForgeStyle BuildStyleForSteepness(int steepness, ForgePieceLibrary library, out string error)
        {
            error = string.Empty;
            ForgePiece flight = library.FindFlight(steepness);
            if (flight == null)
            {
                error = $"no measured full-width flight with rise {steepness}";
                return null;
            }

            var style = new ForgeStyle
            {
                steepness = steepness,
                flight = flight,
                stairRailing = library.FindStairRailing(flight),
                sideCover = library.FindBySizeMatch("stairSideCover", flight.runUnits, 0.3f, flight.lateralWidthUnits, 0.5f, flight.riseUnits, 0.3f),
                botCap = library.FindBySizeMatch("stairBotCap", flight.runUnits, 0.3f, flight.lateralWidthUnits, 0.5f, flight.riseUnits, 0.3f),
            };

            ForgePiece pairFlight = library.FindSameAngleDoubledFlight(flight);
            if (pairFlight != null)
            {
                style.pairFlight = pairFlight;
                style.pairRailing = library.FindStairRailing(pairFlight);
                style.pairCover = library.FindBySizeMatch("stairSideCover", pairFlight.runUnits, 0.3f, pairFlight.lateralWidthUnits, 0.5f, pairFlight.riseUnits, 0.3f);
                style.pairBotCap = library.FindBySizeMatch("stairBotCap", pairFlight.runUnits, 0.3f, pairFlight.lateralWidthUnits, 0.5f, pairFlight.riseUnits, 0.3f);
            }

            return style;
        }

        // ---------------------------------------------------------------------
        // Candidate shapes: cursor walk in plan space.
        // ---------------------------------------------------------------------

        private enum SegmentKind { Flight, FlatSpan, TurnLanding, Curve }

        private readonly struct WalkSegment
        {
            public readonly SegmentKind kind;
            public readonly Vector2Int direction;     // walk direction of this segment
            public readonly Vector3 exitEdgeCenter;   // local walk edge center AFTER the segment (y = level)
            public readonly int exitLevel;
            public readonly Vector2Int cell;          // landing/span anchor cell (plan units of 1 cell)
            public readonly bool spanIsHalf;
            public readonly int turnSign;             // -1 left, +1 right (TurnLanding)

            public WalkSegment(SegmentKind kind, Vector2Int direction, Vector3 exitEdgeCenter, int exitLevel, Vector2Int cell, bool spanIsHalf, int turnSign)
            {
                this.kind = kind;
                this.direction = direction;
                this.exitEdgeCenter = exitEdgeCenter;
                this.exitLevel = exitLevel;
                this.cell = cell;
                this.spanIsHalf = spanIsHalf;
                this.turnSign = turnSign;
            }
        }

        private sealed class ForgeCandidate
        {
            public string shapeTag;
            public List<WalkSegment> segments;
            public List<Vector2Int> footprint;        // raw (un-reindexed) cell coords
            public Vector3 entryEdge;                 // local entry edge center (level 0)
            public Vector3 exitEdge;                  // local exit edge center (level rise)
            public Vector2Int entryDirection;         // climb direction at entry
            public Vector2Int exitDirection;
            public int turns;
            public int pieceCount;
            public int detourCells;
            public int Cost => pieceCount + turns * TurnCost + detourCells * DetourCellCost;
        }

        private static List<ForgeCandidate> EnumerateCandidates(ForgeRequest request, ForgeStyle style, ForgePieceLibrary library)
        {
            var candidates = new List<ForgeCandidate>();
            int flightCount = request.rise / style.steepness;
            bool needsHalfSpan = HalfCellsPerFlight(style.steepness) * flightCount % 2 != 0;
            if (needsHalfSpan && library.FindFloor(HalfCell, CellSize) == null)
            {
                // Parity pad needs a measured half floor; without one this steepness
                // cannot produce whole-cell runs.
                return candidates;
            }

            ForgeCandidate straight = BuildStraightCandidate(request, style, flightCount, needsHalfSpan);
            int minimalFootprint = straight.footprint.Count;
            straight.detourCells = 0;
            if (!request.forceTurn)
            {
                candidates.Add(straight);
            }

            if (request.lanes == 1 && flightCount >= 2)
            {
                foreach (int turnSign in new[] { -1, 1 })
                {
                    ForgeCandidate turn = BuildTurnCandidate(request, style, flightCount, needsHalfSpan, turnSign);
                    if (turn != null)
                    {
                        turn.detourCells = Mathf.Max(0, turn.footprint.Count - minimalFootprint);
                        candidates.Add(turn);
                    }
                }

                // Flourish-only shape: a switchback (two same-direction turns).
                ForgeCandidate switchback = BuildSwitchbackCandidate(request, style, flightCount, needsHalfSpan);
                if (switchback != null)
                {
                    switchback.detourCells = Mathf.Max(0, switchback.footprint.Count - minimalFootprint);
                    candidates.Add(switchback);
                }
            }

            // A measured quarter-turn flight of the staircase's exact steepness
            // turns AND climbs in one cell — it replaces a turn landing plus one
            // flight, so it beats the landing turn on cost wherever it exists
            // (uniform steepness holds: the curve's rise equals the style's).
            if (request.lanes == 1)
            {
                foreach (int turnSign in new[] { -1, 1 })
                {
                    ForgeCandidate curve = BuildCurveCandidate(request, style, flightCount, turnSign, library);
                    if (curve != null)
                    {
                        curve.detourCells = Mathf.Max(0, curve.footprint.Count - minimalFootprint);
                        candidates.Add(curve);
                    }
                }
            }

            foreach (ForgeCandidate candidate in candidates.ToList())
            {
                if (!ValidateHeadroom(candidate, style))
                {
                    candidates.Remove(candidate);
                }
            }

            return candidates;
        }

        private static int HalfCellsPerFlight(int steepness)
        {
            // Measured grid runs: the 1u flight runs ~2.085u (half a cell, embedded
            // overlap hidden under the next tread); 2-4u flights run ~4.1u (one cell
            // with the pack's ~0.1u railing-stub overhang past the entry edge).
            return steepness == 1 ? 1 : 2;
        }

        private sealed class WalkCursor
        {
            // The walk starts at the west edge CENTER of cell (0,0): cells span
            // z in [cz*4, cz*4+4], so the edge center line is z=2. Every later
            // edge stays on a cell-edge center by construction.
            public Vector3 edge = new Vector3(0f, 0f, HalfCell);
            public Vector2Int direction = Vector2Int.right;
            public int level;
            public readonly List<WalkSegment> segments = new List<WalkSegment>();
            public readonly List<Vector2Int> footprint = new List<Vector2Int>();
            public float halfCellsIntoCell;               // run progress inside the current cell (x direction only matters per-segment)

            public void ClaimCellAhead(int lanes)
            {
                // The cell the cursor is about to walk into, plus lane neighbours
                // (lanes extend to the cursor's left so lane 0 keeps the canonical
                // frame).
                Vector2Int lateral = new Vector2Int(-direction.y, direction.x);
                Vector2Int baseCell = CellOfEdgeAhead();
                for (int lane = 0; lane < lanes; lane++)
                {
                    Vector2Int cell = baseCell + lateral * lane;
                    if (!footprint.Contains(cell))
                    {
                        footprint.Add(cell);
                    }
                }
            }

            public Vector2Int CellOfEdgeAhead()
            {
                // The edge center sits on a cell boundary; nudge half a cell forward
                // and floor to find the cell being entered.
                float cx = (edge.x + direction.x * HalfCell) / CellSize;
                float cz = (edge.z + direction.y * HalfCell) / CellSize;
                return new Vector2Int(Mathf.FloorToInt(cx), Mathf.FloorToInt(cz));
            }

            public void AdvanceFlight(int steepness, int lanes)
            {
                float run = HalfCellsPerFlight(steepness) * HalfCell;
                if (halfCellsIntoCell <= 0.001f)
                {
                    ClaimCellAhead(lanes);
                }

                edge += new Vector3(direction.x * run, steepness * LevelHeight, direction.y * run);
                level += steepness;
                halfCellsIntoCell = (halfCellsIntoCell + HalfCellsPerFlight(steepness) * 0.5f) % 1f;
                segments.Add(new WalkSegment(SegmentKind.Flight, direction, edge, level, default, false, 0));
            }

            public void AdvanceHalfSpan(int lanes)
            {
                if (halfCellsIntoCell <= 0.001f)
                {
                    ClaimCellAhead(lanes);
                }

                Vector2Int cell = CellOfEdgeAhead();
                edge += new Vector3(direction.x * HalfCell, 0f, direction.y * HalfCell);
                halfCellsIntoCell = (halfCellsIntoCell + 0.5f) % 1f;
                segments.Add(new WalkSegment(SegmentKind.FlatSpan, direction, edge, level, cell, true, 0));
            }

            public void Turn(int turnSign, int lanes)
            {
                // A turn consumes one whole landing cell: enter it, rotate, exit by
                // the perpendicular edge.
                Vector2Int cell = CellOfEdgeAhead();
                ClaimCellAhead(lanes);
                Vector3 cellCenter = new Vector3(cell.x * CellSize + HalfCell, level * LevelHeight, cell.y * CellSize + HalfCell);
                Vector2Int newDirection = turnSign > 0
                    ? new Vector2Int(direction.y, -direction.x)
                    : new Vector2Int(-direction.y, direction.x);
                edge = cellCenter + new Vector3(newDirection.x * HalfCell, 0f, newDirection.y * HalfCell);
                segments.Add(new WalkSegment(SegmentKind.TurnLanding, direction, edge, level, cell, false, turnSign));
                direction = newDirection;
                halfCellsIntoCell = 0f;
            }

            public void AdvanceCurve(int turnSign, int riseLevels, int lanes)
            {
                // A curve is a turn that climbs: one whole cell, rotate, exit by
                // the perpendicular edge at the raised level.
                Vector2Int cell = CellOfEdgeAhead();
                ClaimCellAhead(lanes);
                level += riseLevels;
                Vector3 cellCenter = new Vector3(cell.x * CellSize + HalfCell, level * LevelHeight, cell.y * CellSize + HalfCell);
                Vector2Int newDirection = turnSign > 0
                    ? new Vector2Int(direction.y, -direction.x)
                    : new Vector2Int(-direction.y, direction.x);
                edge = cellCenter + new Vector3(newDirection.x * HalfCell, 0f, newDirection.y * HalfCell);
                segments.Add(new WalkSegment(SegmentKind.Curve, direction, edge, level, cell, false, turnSign));
                direction = newDirection;
                halfCellsIntoCell = 0f;
            }
        }

        private static ForgeCandidate BuildStraightCandidate(ForgeRequest request, ForgeStyle style, int flightCount, bool needsHalfSpan)
        {
            var cursor = new WalkCursor();
            int spanAfter = needsHalfSpan ? flightCount / 2 : -1;
            for (int i = 0; i < flightCount; i++)
            {
                if (i == spanAfter)
                {
                    cursor.AdvanceHalfSpan(request.lanes);
                }

                cursor.AdvanceFlight(style.steepness, request.lanes);
            }

            return FinishCandidate(cursor, request, "straight");
        }

        private static ForgeCandidate BuildTurnCandidate(ForgeRequest request, ForgeStyle style, int flightCount, bool needsHalfSpan, int turnSign)
        {
            // Legs must each end on a whole cell for the turn landing to sit on
            // the grid. The balanced split is not always cell-aligned (an even
            // count of 1u strips can leave BOTH halves mid-cell with no parity
            // pad to spend, e.g. rise 6 at steepness 1), so splits are tried in
            // order of balance and the first that walks cleanly wins.
            foreach (int firstLeg in LegSplits(flightCount))
            {
                ForgeCandidate candidate = TryBuildTurnCandidate(request, style, flightCount, firstLeg, needsHalfSpan, turnSign);
                if (candidate != null)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<int> LegSplits(int flightCount)
        {
            int half = (flightCount + 1) / 2;
            yield return half;
            for (int offset = 1; offset < flightCount; offset++)
            {
                if (half + offset <= flightCount - 1)
                {
                    yield return half + offset;
                }

                if (half - offset >= 1)
                {
                    yield return half - offset;
                }
            }
        }

        private static ForgeCandidate BuildCurveCandidate(ForgeRequest request, ForgeStyle style, int flightCount, int turnSign, ForgePieceLibrary library)
        {
            if (library.FindCurvedFlight(style.steepness, turnSign) == null || flightCount < 1)
            {
                return null;
            }

            // The curve contributes one flight's worth of rise; the rest are
            // straight legs around it (either may be empty — the hand-authored
            // curved_stair_90 is exactly a bare curve).
            int legFlights = flightCount - 1;
            bool needsHalfSpan = HalfCellsPerFlight(style.steepness) * legFlights % 2 != 0;
            if (needsHalfSpan && library.FindFloor(HalfCell, CellSize) == null)
            {
                return null;
            }

            int half = (legFlights + 1) / 2;
            for (int offset = 0; offset <= legFlights; offset++)
            {
                foreach (int firstLeg in new[] { half + offset, half - offset }.Distinct())
                {
                    if (firstLeg < 0 || firstLeg > legFlights)
                    {
                        continue;
                    }

                    ForgeCandidate candidate = TryBuildCurveCandidate(request, style, legFlights, firstLeg, needsHalfSpan, turnSign);
                    if (candidate != null)
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static ForgeCandidate TryBuildCurveCandidate(ForgeRequest request, ForgeStyle style, int legFlights, int firstLeg, bool needsHalfSpan, int turnSign)
        {
            var cursor = new WalkCursor();
            bool padUsed = false;
            for (int i = 0; i < firstLeg; i++)
            {
                cursor.AdvanceFlight(style.steepness, request.lanes);
            }

            if (cursor.halfCellsIntoCell > 0.001f)
            {
                if (!needsHalfSpan)
                {
                    return null;
                }

                cursor.AdvanceHalfSpan(request.lanes);
                padUsed = true;
            }

            cursor.AdvanceCurve(turnSign, style.steepness, request.lanes);
            for (int i = firstLeg; i < legFlights; i++)
            {
                cursor.AdvanceFlight(style.steepness, request.lanes);
            }

            if (cursor.halfCellsIntoCell > 0.001f)
            {
                if (!needsHalfSpan || padUsed)
                {
                    return null;
                }

                cursor.AdvanceHalfSpan(request.lanes);
                padUsed = true;
            }

            if (needsHalfSpan && !padUsed)
            {
                return null;
            }

            return FinishCandidate(cursor, request, turnSign > 0 ? "curveR" : "curveL");
        }

        private static ForgeCandidate TryBuildTurnCandidate(ForgeRequest request, ForgeStyle style, int flightCount, int firstLeg, bool needsHalfSpan, int turnSign)
        {
            var cursor = new WalkCursor();
            bool padUsed = false;
            for (int i = 0; i < firstLeg; i++)
            {
                cursor.AdvanceFlight(style.steepness, request.lanes);
            }

            if (cursor.halfCellsIntoCell > 0.001f)
            {
                if (!needsHalfSpan)
                {
                    return null;
                }

                cursor.AdvanceHalfSpan(request.lanes);
                padUsed = true;
            }

            cursor.Turn(turnSign, request.lanes);
            for (int i = firstLeg; i < flightCount; i++)
            {
                cursor.AdvanceFlight(style.steepness, request.lanes);
            }

            if (cursor.halfCellsIntoCell > 0.001f)
            {
                if (!needsHalfSpan || padUsed)
                {
                    return null;
                }

                cursor.AdvanceHalfSpan(request.lanes);
                padUsed = true;
            }

            if (needsHalfSpan && !padUsed)
            {
                return null;
            }

            return FinishCandidate(cursor, request, turnSign > 0 ? "turnR" : "turnL");
        }

        private static ForgeCandidate BuildSwitchbackCandidate(ForgeRequest request, ForgeStyle style, int flightCount, bool needsHalfSpan)
        {
            if (needsHalfSpan || flightCount < 2)
            {
                // Keep the flourish shape simple: even half-cell legs only.
                return null;
            }

            foreach (int firstLeg in LegSplits(flightCount))
            {
                ForgeCandidate candidate = TryBuildSwitchbackCandidate(request, style, flightCount, firstLeg);
                if (candidate != null)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static ForgeCandidate TryBuildSwitchbackCandidate(ForgeRequest request, ForgeStyle style, int flightCount, int firstLeg)
        {
            var cursor = new WalkCursor();
            for (int i = 0; i < firstLeg; i++)
            {
                cursor.AdvanceFlight(style.steepness, request.lanes);
            }

            if (cursor.halfCellsIntoCell > 0.001f)
            {
                return null;
            }

            cursor.Turn(-1, request.lanes);
            cursor.Turn(-1, request.lanes);
            for (int i = firstLeg; i < flightCount; i++)
            {
                cursor.AdvanceFlight(style.steepness, request.lanes);
            }

            if (cursor.halfCellsIntoCell > 0.001f)
            {
                return null;
            }

            return FinishCandidate(cursor, request, "switchback");
        }

        private static ForgeCandidate FinishCandidate(WalkCursor cursor, ForgeRequest request, string shapeTag)
        {
            return new ForgeCandidate
            {
                shapeTag = shapeTag,
                segments = cursor.segments.ToList(),
                footprint = cursor.footprint.ToList(),
                entryEdge = new Vector3(0f, 0f, HalfCell),
                exitEdge = cursor.edge,
                entryDirection = Vector2Int.right,
                exitDirection = cursor.direction,
                turns = cursor.segments.Count(s => s.kind == SegmentKind.TurnLanding || s.kind == SegmentKind.Curve),
                pieceCount = cursor.segments.Count * request.lanes,
            };
        }

        private static ForgeCandidate PickWinner(List<ForgeCandidate> candidates, ForgeRequest request, System.Random rng)
        {
            List<ForgeCandidate> ordered = candidates
                .OrderBy(c => c.Cost)
                .ThenBy(c => c.shapeTag, StringComparer.Ordinal)
                .ToList();
            ForgeCandidate minimal = ordered[0];
            bool flourish = rng.NextDouble() < FlourishChance;
            if (!flourish)
            {
                return minimal;
            }

            // Coolness opportunistic (design decision 5): an ornate candidate may
            // win only within twice the minimal detour.
            int detourBudget = Mathf.Max(2, minimal.detourCells * 2);
            ForgeCandidate ornate = ordered
                .Where(c => c.detourCells <= detourBudget)
                .OrderByDescending(c => c.Cost)
                .ThenBy(c => c.shapeTag, StringComparer.Ordinal)
                .First();
            return ornate;
        }

        // Decision 2: no walkable surface may have geometry within 3u above it.
        // Current shapes never stack walk surfaces vertically, but the gate stays
        // active so future shapes (true switchback towers) cannot regress it.
        private static bool ValidateHeadroom(ForgeCandidate candidate, ForgeStyle style)
        {
            var columns = new Dictionary<(int, int), List<float>>();
            foreach (WalkSegment segment in candidate.segments)
            {
                Vector3 exit = segment.exitEdgeCenter;
                var key = (Mathf.RoundToInt(exit.x / HalfCell), Mathf.RoundToInt(exit.z / HalfCell));
                if (!columns.TryGetValue(key, out List<float> heights))
                {
                    heights = new List<float>();
                    columns[key] = heights;
                }

                heights.Add(segment.exitLevel * LevelHeight);
            }

            foreach (List<float> heights in columns.Values)
            {
                heights.Sort();
                for (int i = 1; i < heights.Count; i++)
                {
                    if (heights[i] - heights[i - 1] > 0.001f && heights[i] - heights[i - 1] < MinHeadroomUnits)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // ---------------------------------------------------------------------
        // Online synthesis (step 7, design decisions 16-21). Headless-safe:
        // pure cursor math + the measured library JSON — no AssetDatabase, no
        // GameObjects. First increment: straight walks only (single lane,
        // walled style); the remaining grammar activates once the plumbing
        // survives review. One design per legal steepness — the planner runs
        // them through the regular placement search, level gates and ledger,
        // and the per-gap RNG picks among the survivors.
        // ---------------------------------------------------------------------

        internal sealed class SynthesizedStaircaseDesign
        {
            public string name;
            public int steepness;
            public string shapeTag;
            public JObject contract;
            public ElevationEdgeModel.SynthesizedPiecePlacement[] pieces;
        }

        internal static string SynthesizedPrefabSentinel(string name)
        {
            // Never a real asset: the renderer materializes the piece plan. The
            // sentinel keeps every "named set piece" code path (stats, candidate
            // grouping, transition keys) working unchanged.
            return $"synthesized://{name}";
        }

        private static ForgePieceLibrary synthesisLibraryCache;
        private static DateTime synthesisLibraryCacheWriteTimeUtc;

        // Metrology lesson (review round 1): static caches keyed on the library
        // must invalidate on the library file's write time, because re-running
        // the measurement tool does not domain-reload.
        private static ForgePieceLibrary LoadSynthesisLibrary()
        {
            DateTime writeTimeUtc = File.GetLastWriteTimeUtc(StepPieceLibraryPath);
            if (synthesisLibraryCache == null || writeTimeUtc != synthesisLibraryCacheWriteTimeUtc)
            {
                synthesisLibraryCache = ForgePieceLibrary.Load();
                synthesisLibraryCacheWriteTimeUtc = writeTimeUtc;
                synthesisDesignCache.Clear();
                synthesisFailureCache.Clear();
                stairwellDesignCache.Clear();
                stairwellFailureCache.Clear();
                deckDesignCache.Clear();
            }

            return synthesisLibraryCache;
        }

        // Designs depend only on (rise, measured library), and the fallback fires
        // once per misfit connection per attempt — cache per rise, invalidated
        // with the library cache. The shared JObject/pieces are read-only
        // downstream (the review log deep-clones before persisting).
        private static readonly Dictionary<int, List<SynthesizedStaircaseDesign>> synthesisDesignCache =
            new Dictionary<int, List<SynthesizedStaircaseDesign>>();
        private static readonly Dictionary<int, string> synthesisFailureCache = new Dictionary<int, string>();

        internal static List<SynthesizedStaircaseDesign> EnumerateSynthesisDesigns(int rise, out string failureSummary)
        {
            ForgePieceLibrary library = LoadSynthesisLibrary();
            if (synthesisDesignCache.TryGetValue(rise, out List<SynthesizedStaircaseDesign> cached))
            {
                failureSummary = synthesisFailureCache.TryGetValue(rise, out string cachedFailure) ? cachedFailure : string.Empty;
                return cached;
            }

            failureSummary = string.Empty;
            var designs = new List<SynthesizedStaircaseDesign>();
            var failures = new List<string>();

            string missing = library.DescribeMissingCategories();
            if (!string.IsNullOrEmpty(missing))
            {
                failureSummary = $"measured piece library is missing {missing}";
                return designs;
            }

            // Both side styles (decision 33): walled designs place EMBEDDED on the
            // corridor; bridge designs (bottom caps, no bulk) place as external
            // SPANS between landings over true gaps — one tier, equal terms.
            foreach (ForgeSideStyle sideStyle in new[] { ForgeSideStyle.Walled, ForgeSideStyle.Bridge })
            {
                var request = new ForgeRequest(rise, 1, sideStyle);
                string styleTag = StyleTag(sideStyle);
                foreach (int steepness in library.FlightRises().Where(s => rise % s == 0).OrderBy(s => s))
                {
                    ForgeStyle style = BuildStyleForSteepness(steepness, library, out string styleError);
                    if (style == null)
                    {
                        failures.Add($"s{steepness}: {styleError}");
                        continue;
                    }

                    int flightCount = rise / steepness;
                    bool needsHalfSpan = HalfCellsPerFlight(steepness) * flightCount % 2 != 0;
                    if (needsHalfSpan && library.FindFloor(HalfCell, CellSize) == null)
                    {
                        failures.Add($"s{steepness}: no measured half floor for the parity pad");
                        continue;
                    }

                    TryAddSynthesisDesign(
                        designs,
                        failures,
                        $"synth_r{rise}_l1_{styleTag}_straight_s{steepness}",
                        request,
                        style,
                        steepness,
                        library,
                        () => BuildStraightCandidate(request, style, flightCount, needsHalfSpan));

                    // The per-gap win over the fixed pool: a pool turn contract
                    // folds at ONE leg split, but a corridor corner can sit
                    // anywhere along the path — enumerate every split and both
                    // chiralities and let the placement search find the one
                    // matching the actual corner.
                    if (flightCount >= 2)
                    {
                        foreach (int turnSign in new[] { -1, 1 })
                        {
                            foreach (int firstLeg in LegSplits(flightCount))
                            {
                                int capturedFirstLeg = firstLeg;
                                int capturedTurnSign = turnSign;
                                TryAddSynthesisDesign(
                                    designs,
                                    failures,
                                    $"synth_r{rise}_l1_{styleTag}_turn{(turnSign > 0 ? "R" : "L")}{firstLeg}_s{steepness}",
                                    request,
                                    style,
                                    steepness,
                                    library,
                                    () => TryBuildTurnCandidate(request, style, flightCount, capturedFirstLeg, needsHalfSpan, capturedTurnSign));
                            }
                        }
                    }

                    // Curves: the measured quarter-turn kit turns AND climbs in
                    // one cell where a landing-turn spends a flat cell. The kit
                    // exists only at its measured steepness (med family), so
                    // FindCurvedFlight gates per chirality.
                    int curveLegFlights = flightCount - 1;
                    bool curveLegsNeedHalfSpan = HalfCellsPerFlight(steepness) * curveLegFlights % 2 != 0;
                    if (!curveLegsNeedHalfSpan || library.FindFloor(HalfCell, CellSize) != null)
                    {
                        foreach (int turnSign in new[] { -1, 1 })
                        {
                            if (library.FindCurvedFlight(steepness, turnSign) == null)
                            {
                                continue;
                            }

                            for (int firstLeg = 0; firstLeg <= curveLegFlights; firstLeg++)
                            {
                                int capturedFirstLeg = firstLeg;
                                int capturedTurnSign = turnSign;
                                TryAddSynthesisDesign(
                                    designs,
                                    failures,
                                    $"synth_r{rise}_l1_{styleTag}_curve{(turnSign > 0 ? "R" : "L")}{firstLeg}_s{steepness}",
                                    request,
                                    style,
                                    steepness,
                                    library,
                                    () => TryBuildCurveCandidate(request, style, curveLegFlights, capturedFirstLeg, curveLegsNeedHalfSpan, capturedTurnSign));
                            }
                        }
                    }
                }
            }

            if (designs.Count == 0)
            {
                failureSummary = failures.Count > 0 ? string.Join(" | ", failures) : "no legal steepness divides the rise";
            }

            synthesisDesignCache[rise] = designs;
            synthesisFailureCache[rise] = failureSummary;
            return designs;
        }

        private static void TryAddSynthesisDesign(
            List<SynthesizedStaircaseDesign> designs,
            List<string> failures,
            string name,
            ForgeRequest request,
            ForgeStyle style,
            int steepness,
            ForgePieceLibrary library,
            Func<ForgeCandidate> buildCandidate,
            string topologyOverride = null)
        {
            try
            {
                ForgeCandidate candidate = buildCandidate();
                if (candidate == null)
                {
                    return;
                }

                if (!ValidateHeadroom(candidate, style))
                {
                    failures.Add($"{name}: headroom");
                    return;
                }

                ForgedStaircasePlan stairPlan = BuildStaircasePlan(name, request, style, candidate, library);
                stairPlan.contract["source"] = "synthesis";
                stairPlan.contract["reviewStatus"] = "provisional";
                stairPlan.contract["prefab"] = SynthesizedPrefabSentinel(name);
                if (topologyOverride != null)
                {
                    stairPlan.contract["topology"] = topologyOverride;
                }
                designs.Add(new SynthesizedStaircaseDesign
                {
                    name = name,
                    steepness = steepness,
                    shapeTag = candidate.shapeTag,
                    contract = stairPlan.contract,
                    pieces = stairPlan.placements.ToArray(),
                });
            }
            catch (Exception exception)
            {
                failures.Add($"{name}: {exception.Message}");
            }
        }

        // ---------------------------------------------------------------------
        // Stairwells (design decisions 26-28): 180-degree towers that stand
        // BESIDE the corridor on void cells. Equal legs by construction so the
        // exit lands column-aligned one row from the entry, both ports facing
        // the path. Legs occupy parallel rows — the walk never stacks
        // vertically, so the headroom gate is structurally satisfied.
        // ---------------------------------------------------------------------

        private static readonly Dictionary<int, List<SynthesizedStaircaseDesign>> stairwellDesignCache =
            new Dictionary<int, List<SynthesizedStaircaseDesign>>();
        private static readonly Dictionary<int, string> stairwellFailureCache = new Dictionary<int, string>();

        internal static List<SynthesizedStaircaseDesign> EnumerateStairwellSynthesisDesigns(int rise, out string failureSummary)
        {
            ForgePieceLibrary library = LoadSynthesisLibrary();
            if (stairwellDesignCache.TryGetValue(rise, out List<SynthesizedStaircaseDesign> cached))
            {
                failureSummary = stairwellFailureCache.TryGetValue(rise, out string cachedFailure) ? cachedFailure : string.Empty;
                return cached;
            }

            failureSummary = string.Empty;
            var designs = new List<SynthesizedStaircaseDesign>();
            var failures = new List<string>();

            string missing = library.DescribeMissingCategories();
            if (!string.IsNullOrEmpty(missing))
            {
                failureSummary = $"measured piece library is missing {missing}";
                return designs;
            }

            var request = new ForgeRequest(rise, 1, ForgeSideStyle.Walled);
            foreach (int steepness in library.FlightRises().Where(s => rise % s == 0).OrderBy(s => s))
            {
                ForgeStyle style = BuildStyleForSteepness(steepness, library, out string styleError);
                if (style == null)
                {
                    failures.Add($"s{steepness}: {styleError}");
                    continue;
                }

                int flightCount = rise / steepness;
                foreach (int turnSign in new[] { -1, 1 })
                {
                    int capturedTurnSign = turnSign;
                    TryAddSynthesisDesign(
                        designs,
                        failures,
                        $"synth_r{rise}_l1_walled_well{(turnSign > 0 ? "R" : "L")}_s{steepness}",
                        request,
                        style,
                        steepness,
                        library,
                        () => TryBuildStairwellSwitchbackCandidate(request, style, flightCount, capturedTurnSign),
                        topologyOverride: "stairwell");
                    TryAddSynthesisDesign(
                        designs,
                        failures,
                        $"synth_r{rise}_l1_walled_well180{(turnSign > 0 ? "R" : "L")}_s{steepness}",
                        request,
                        style,
                        steepness,
                        library,
                        () => TryBuildStairwellCurve180Candidate(request, style, flightCount, capturedTurnSign, library),
                        topologyOverride: "stairwell");
                }
            }

            if (designs.Count == 0)
            {
                failureSummary = failures.Count > 0 ? string.Join(" | ", failures) : "no stairwell shape closes the rise with equal legs";
            }

            stairwellDesignCache[rise] = designs;
            stairwellFailureCache[rise] = failureSummary;
            return designs;
        }

        // ---------------------------------------------------------------------
        // Aerial decks (step 8, design decisions 29-31): rise-0 flat spans for
        // aerial loop bridges. One design per length; the walk is 2N half-spans
        // that coalesce into whole floors, bridge style (no bulk), railings with
        // end posts along both sides. Topology "deck": ports at EQUAL levels on
        // opposite ends, resolved by array order.
        // ---------------------------------------------------------------------

        private static readonly Dictionary<(int length, int rise, ulong plusMask, ulong minusMask), SynthesizedStaircaseDesign> deckDesignCache =
            new Dictionary<(int, int, ulong, ulong), SynthesizedStaircaseDesign>();

        internal static SynthesizedStaircaseDesign SynthesizeDeckDesign(int lengthCells, out string failureSummary)
        {
            return SynthesizeDeckDesign(lengthCells, 0, 0, 0, out failureSummary);
        }

        // rise 0 = a flat deck (topology "deck"); rise 1-2 = a sloped span
        // (decision 34): flat pads with the rise's flight at the UPPER end, an
        // ordinary "straight" bridge contract. Steepness equals the rise, so the
        // slope is one flight.
        internal static SynthesizedStaircaseDesign SynthesizeDeckDesign(int lengthCells, int rise, ulong railPlusMask, ulong railMinusMask, out string failureSummary)
        {
            failureSummary = string.Empty;
            ForgePieceLibrary library = LoadSynthesisLibrary();
            if (deckDesignCache.TryGetValue((lengthCells, rise, railPlusMask, railMinusMask), out SynthesizedStaircaseDesign cached))
            {
                return cached;
            }

            if (lengthCells < 1)
            {
                failureSummary = "deck length must be at least one cell";
                return null;
            }

            ForgeStyle style;
            int flightHalves = 0;
            if (rise > 0)
            {
                style = BuildStyleForSteepness(rise, library, out string styleError);
                if (style == null)
                {
                    failureSummary = $"sloped span needs a rise-{rise} flight: {styleError}";
                    return null;
                }

                flightHalves = HalfCellsPerFlight(rise);
                if (flightHalves > lengthCells * 2)
                {
                    failureSummary = $"span of {lengthCells} cells is too short for a rise-{rise} flight";
                    return null;
                }
            }
            else
            {
                style = BuildStyleForSteepness(library.FlightRises().Min(), library, out _);
            }

            string missing = library.DescribeMissingCategories();
            if (!string.IsNullOrEmpty(missing))
            {
                failureSummary = $"measured piece library is missing {missing}";
                return null;
            }

            var request = new ForgeRequest(rise, 1, ForgeSideStyle.Bridge, forceTurn: false, deck: true, railPlusMask, railMinusMask);
            string maskTag = railPlusMask == 0 && railMinusMask == 0 ? string.Empty : $"_m{railPlusMask:X}_{railMinusMask:X}";
            string riseTag = rise == 0 ? string.Empty : $"d{rise}";
            string name = $"synth_deck{lengthCells}{riseTag}{maskTag}_bridge";
            try
            {
                var cursor = new WalkCursor();
                for (int i = 0; i < lengthCells * 2 - flightHalves; i++)
                {
                    cursor.AdvanceHalfSpan(request.lanes);
                }

                if (rise > 0)
                {
                    cursor.AdvanceFlight(rise, request.lanes);
                }

                ForgeCandidate candidate = FinishCandidate(cursor, request, rise == 0 ? "deck" : $"deckd{rise}");
                ForgedStaircasePlan stairPlan = BuildStaircasePlan(name, request, style, candidate, library);
                stairPlan.contract["source"] = "synthesis";
                stairPlan.contract["reviewStatus"] = "provisional";
                stairPlan.contract["prefab"] = SynthesizedPrefabSentinel(name);
                if (rise == 0)
                {
                    stairPlan.contract["topology"] = "deck";
                }

                var design = new SynthesizedStaircaseDesign
                {
                    name = name,
                    steepness = rise,
                    shapeTag = rise == 0 ? "deck" : $"deckd{rise}",
                    contract = stairPlan.contract,
                    pieces = stairPlan.placements.ToArray(),
                };
                deckDesignCache[(lengthCells, rise, railPlusMask, railMinusMask)] = design;
                return design;
            }
            catch (Exception exception)
            {
                failureSummary = exception.Message;
                return null;
            }
        }

        private static ForgeCandidate TryBuildStairwellSwitchbackCandidate(
            ForgeRequest request,
            ForgeStyle style,
            int flightCount,
            int turnSign)
        {
            if (flightCount < 2)
            {
                return null;
            }

            return BuildStairwellCandidate(
                request,
                style,
                firstLegFlights: (flightCount + 1) / 2,
                secondLegFlights: flightCount / 2,
                useCurves: false,
                turnSign,
                turnSign > 0 ? "wellR" : "wellL");
        }

        private static ForgeCandidate TryBuildStairwellCurve180Candidate(
            ForgeRequest request,
            ForgeStyle style,
            int flightCount,
            int turnSign,
            ForgePieceLibrary library)
        {
            // Two stacked quarter-turn curves (the hand-authored 180 convention)
            // climb 2x the steepness through the turn column; the straight legs
            // split the rest. Zero-flight legs are legal — a bare 1x2 tower.
            if (library.FindCurvedFlight(style.steepness, turnSign) == null || flightCount < 2)
            {
                return null;
            }

            int legFlightsTotal = flightCount - 2;
            return BuildStairwellCandidate(
                request,
                style,
                firstLegFlights: (legFlightsTotal + 1) / 2,
                secondLegFlights: legFlightsTotal / 2,
                useCurves: true,
                turnSign,
                turnSign > 0 ? "well180R" : "well180L");
        }

        // The ports align when the legs have equal RUN, not equal rise: each leg
        // pads with flat half-spans up to a shared whole-cell column count, so
        // the exit edge lands directly beside the entry edge whatever the flight
        // split. Pads sit adjacent to the turn column and read as a gallery.
        private static ForgeCandidate BuildStairwellCandidate(
            ForgeRequest request,
            ForgeStyle style,
            int firstLegFlights,
            int secondLegFlights,
            bool useCurves,
            int turnSign,
            string shapeTag)
        {
            int halfCellsPerFlight = HalfCellsPerFlight(style.steepness);
            int targetHalfCells = Mathf.Max(firstLegFlights, secondLegFlights) * halfCellsPerFlight;
            if (targetHalfCells % 2 != 0)
            {
                targetHalfCells++;
            }

            int firstLegPads = targetHalfCells - firstLegFlights * halfCellsPerFlight;
            int secondLegPads = targetHalfCells - secondLegFlights * halfCellsPerFlight;

            var cursor = new WalkCursor();
            for (int i = 0; i < firstLegFlights; i++)
            {
                cursor.AdvanceFlight(style.steepness, request.lanes);
            }

            for (int i = 0; i < firstLegPads; i++)
            {
                cursor.AdvanceHalfSpan(request.lanes);
            }

            if (useCurves)
            {
                cursor.AdvanceCurve(turnSign, style.steepness, request.lanes);
                cursor.AdvanceCurve(turnSign, style.steepness, request.lanes);
            }
            else
            {
                cursor.Turn(turnSign, request.lanes);
                cursor.Turn(turnSign, request.lanes);
            }

            for (int i = 0; i < secondLegPads; i++)
            {
                cursor.AdvanceHalfSpan(request.lanes);
            }

            for (int i = 0; i < secondLegFlights; i++)
            {
                cursor.AdvanceFlight(style.steepness, request.lanes);
            }

            return FinishCandidate(cursor, request, shapeTag);
        }

        // ---------------------------------------------------------------------
        // Geometry + contract from ONE plan (the core anti-drift property).
        // Step 7 reification: placements are recorded as pure data first; the
        // offline prefab writer and the online-synthesis scene materializer are
        // both consumers of the same plan, so geometry can never depend on
        // which one ran (and the plan builder stays headless-safe).
        // ---------------------------------------------------------------------

        private sealed class ForgePlanRecorder
        {
            public readonly List<ElevationEdgeModel.SynthesizedPiecePlacement> placements =
                new List<ElevationEdgeModel.SynthesizedPiecePlacement>();

            public void Add(string sourcePrefab, string pieceName, Vector3 localPosition, float yawDegrees)
            {
                placements.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(sourcePrefab, pieceName, localPosition, yawDegrees));
            }

            public void Add(string sourcePrefab, string pieceName, Vector3 localPosition, float yawDegrees, float pitchDegrees)
            {
                placements.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(sourcePrefab, pieceName, localPosition, yawDegrees, pitchDegrees));
            }
        }

        private sealed class ForgedStaircasePlan
        {
            public string name;
            public JObject contract;
            public List<ElevationEdgeModel.SynthesizedPiecePlacement> placements;
        }

        private static JObject BuildPrefabAndContract(
            string name,
            ForgeRequest request,
            ForgeStyle style,
            ForgeCandidate candidate,
            ForgePieceLibrary library)
        {
            ForgedStaircasePlan stairPlan = BuildStaircasePlan(name, request, style, candidate, library);
            MaterializePrefab(stairPlan);
            return stairPlan.contract;
        }

        private static void MaterializePrefab(ForgedStaircasePlan stairPlan)
        {
            var root = new GameObject(stairPlan.name);
            try
            {
                foreach (ElevationEdgeModel.SynthesizedPiecePlacement piece in stairPlan.placements)
                {
                    Instantiate(root.transform, piece.sourcePrefab, piece.pieceName, piece.localPosition, piece.localYawDegrees, piece.localPitchDegrees);
                }

                string prefabPath = PrefabPathFor(stairPlan.name);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool savedOk);
                if (!savedOk)
                {
                    throw new InvalidOperationException($"prefab save failed for {prefabPath}");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static ForgedStaircasePlan BuildStaircasePlan(
            string name,
            ForgeRequest request,
            ForgeStyle style,
            ForgeCandidate candidate,
            ForgePieceLibrary library)
        {
            var plan = new ForgePlanRecorder();
            var flightPoses = new List<(string sourcePrefab, Vector3 position, float yaw)>();
            var columnPositions = new HashSet<string>(StringComparer.Ordinal);
            int laneCount = request.lanes;

            // Coalesce same-direction pairs into the larger piece (greedy,
            // left to right): two strips become the same-angle doubled flight,
            // two half spans become one whole floor. The consumed first
            // segment places nothing; its successor places the larger piece
            // whose start lands exactly where the pair began.
            var pairConsumed = new HashSet<int>();
            for (int i = 0; i + 1 < candidate.segments.Count;)
            {
                bool flightPair = style.pairFlight != null &&
                    candidate.segments[i].kind == SegmentKind.Flight &&
                    candidate.segments[i + 1].kind == SegmentKind.Flight;
                bool spanPair =
                    candidate.segments[i].kind == SegmentKind.FlatSpan &&
                    candidate.segments[i + 1].kind == SegmentKind.FlatSpan;
                if ((flightPair || spanPair) &&
                    candidate.segments[i].direction == candidate.segments[i + 1].direction)
                {
                    pairConsumed.Add(i);
                    i += 2;
                }
                else
                {
                    i++;
                }
            }

            for (int segmentIndex = 0; segmentIndex < candidate.segments.Count; segmentIndex++)
            {
                if (pairConsumed.Contains(segmentIndex))
                {
                    continue;
                }

                WalkSegment segment = candidate.segments[segmentIndex];
                bool coalescedPair = pairConsumed.Contains(segmentIndex - 1);
                for (int lane = 0; lane < laneCount; lane++)
                {
                    Vector2Int lateral = new Vector2Int(-segment.direction.y, segment.direction.x);
                    Vector3 laneOffset = new Vector3(lateral.x, 0f, lateral.y) * (CellSize * lane);
                    switch (segment.kind)
                    {
                        case SegmentKind.Flight:
                            PlaceFlight(plan, segment, laneOffset, request, style, library, flightPoses, columnPositions, lane, coalescedPair);
                            break;
                        case SegmentKind.FlatSpan:
                            PlaceFlatSpan(plan, segment, laneOffset, request, style, library, flightPoses, columnPositions, lane, coalescedPair);
                            break;
                        case SegmentKind.TurnLanding:
                            PlaceTurnLanding(plan, segment, laneOffset, request, style, library, columnPositions);
                            break;
                        case SegmentKind.Curve:
                            PlaceCurve(plan, segment, request, style, library, flightPoses, columnPositions);
                            break;
                    }
                }
            }

            return new ForgedStaircasePlan
            {
                name = name,
                contract = BuildContract(name, request, style, candidate, flightPoses),
                placements = plan.placements,
            };
        }

        private static void PlaceFlight(
            ForgePlanRecorder plan,
            WalkSegment segment,
            Vector3 laneOffset,
            ForgeRequest request,
            ForgeStyle style,
            ForgePieceLibrary library,
            List<(string, Vector3, float)> flightPoses,
            HashSet<string> columnPositions,
            int lane,
            bool coalescedPair)
        {
            // The proven seam-strip math, generalized: anchor the flight by its exit
            // socket at the walk edge, pivot y on the exact exit level (the ~0.02u
            // walk-top dust rides above the port plane, matching the hand-authored
            // convention). A coalesced pair anchors the doubled piece at the SECOND
            // segment's exit; its foot lands where the pair began.
            ForgePiece flight = coalescedPair ? style.pairFlight : style.flight;
            ForgePiece sideCover = coalescedPair ? style.pairCover : style.sideCover;
            ForgePiece botCap = coalescedPair ? style.pairBotCap : style.botCap;
            ForgePiece stairRailing = coalescedPair ? style.pairRailing : style.stairRailing;
            int riseLevels = coalescedPair ? style.steepness * 2 : style.steepness;
            float gridRun = (coalescedPair ? 2 : 1) * HalfCellsPerFlight(style.steepness) * HalfCell;

            float yaw = YawFromDirection(segment.direction);
            Vector3 exitTarget = segment.exitEdgeCenter + laneOffset;
            Vector3 position = exitTarget - RotateYaw(new Vector3(flight.exitSocketLocal.x, 0f, flight.exitSocketLocal.z), yaw);
            // Pivot exactly on the integer exit level: the ~0.02u walk-top dust
            // rides above the port plane, the hand-authored convention, and the
            // recorded source-root pose stays a clean number.
            position.y = segment.exitLevel * LevelHeight;
            plan.Add(flight.path, $"flight_{flightPoses.Count}", position, yaw);
            flightPoses.Add((flight.path, position, yaw));

            int footLevel = segment.exitLevel - riseLevels;

            // Side dressing per style.
            if (request.style == ForgeSideStyle.Walled && sideCover != null)
            {
                plan.Add(sideCover.path, $"flight_cover_{flightPoses.Count - 1}", position, yaw);
            }
            else if (request.style == ForgeSideStyle.Bridge && botCap != null)
            {
                plan.Add(botCap.path, $"flight_botcap_{flightPoses.Count - 1}", position, yaw);
            }

            // Masonry fill below an elevated flight (walled style): top-anchored at
            // the foot level, sinking any odd remainder below the entry plane, the
            // same convention as the engine's drop-face walls.
            if (request.style == ForgeSideStyle.Walled && footLevel > 0)
            {
                // The flight pivot sits at the exit edge's z=0 corner (piece width
                // runs +z in piece frame); the fill rect center is half a run back
                // and half a width across.
                Vector3 planCenter = position + RotateYaw(new Vector3(-gridRun * 0.5f, 0f, flight.lateralWidthUnits * 0.5f), yaw);
                PlaceBaseFill(plan, library, planCenter, yaw, gridRun, flight.lateralWidthUnits, footLevel * LevelHeight, $"flight_fill_{flightPoses.Count - 1}");
            }

            // Railings + their end columns: outer sides only (a dual-lane stair is
            // one wide stair; the inner seam stays open).
            bool nearSideOuter = lane == 0;
            bool farSideOuter = lane == request.lanes - 1;
            if (stairRailing != null)
            {
                if (nearSideOuter)
                {
                    PlaceFlightRailing(plan, stairRailing, position, yaw, 0f, gridRun, riseLevels, columnPositions, library);
                }

                if (farSideOuter)
                {
                    PlaceFlightRailing(plan, stairRailing, position, yaw, flight.lateralWidthUnits, gridRun, riseLevels, columnPositions, library);
                }
            }
        }

        private static void PlaceFlightRailing(
            ForgePlanRecorder plan,
            ForgePiece stairRailing,
            Vector3 flightPosition,
            float yaw,
            float lateralOffset,
            float gridRun,
            int riseLevels,
            HashSet<string> columnPositions,
            ForgePieceLibrary library)
        {
            // The pack's stair railings are authored co-located with their flight,
            // one side line through the pivot; the far side is the same piece offset
            // by the flight width (the hand-authored stairs use exactly this).
            Vector3 position = flightPosition + RotateYaw(new Vector3(0f, 0f, lateralOffset), yaw);
            plan.Add(stairRailing.path, $"flight_railing_{position.x:0.#}_{position.z:0.#}", position, yaw);

            ForgePiece column = library.railingColumn;
            if (column == null)
            {
                return;
            }

            Vector3[] ends =
            {
                position,
                position + RotateYaw(new Vector3(-gridRun, 0f, 0f), yaw) + Vector3.down * (riseLevels * LevelHeight),
            };
            foreach (Vector3 end in ends)
            {
                string key = $"{end.x:0.##}_{end.y:0.##}_{end.z:0.##}";
                if (columnPositions.Add(key))
                {
                    plan.Add(column.path, $"railing_column_{key}", end, yaw);
                }
            }
        }

        private static void PlaceFlatSpan(
            ForgePlanRecorder plan,
            WalkSegment segment,
            Vector3 laneOffset,
            ForgeRequest request,
            ForgeStyle style,
            ForgePieceLibrary library,
            List<(string, Vector3, float)> flightPoses,
            HashSet<string> columnPositions,
            int lane,
            bool coalescedPair)
        {
            // A coalesced pair of half spans places one whole-cell deck (and the
            // full-length railing) instead of two seamed halves.
            float runSize = coalescedPair ? CellSize : HalfCell;
            ForgePiece floor = library.FindFloor(runSize, CellSize);
            if (floor == null)
            {
                throw new InvalidOperationException($"no measured floor ({runSize:0.#}u x {CellSize:0.#}u) for the flat span");
            }

            // The span deck ends at the segment's exit edge and extends back along
            // the walk.
            float yaw = YawFromDirection(segment.direction);
            Vector3 exit = segment.exitEdgeCenter + laneOffset;
            Vector3 deckCenter = exit - RotateYaw(new Vector3(runSize * 0.5f, 0f, 0f), yaw);
            // A deck has no flights, so its floors are the contract's visual
            // anchors (sourceRootPoses); stairs keep anchoring by flights only.
            // Bridge-style undersides always get a bottom cap (user reviews
            // 2026-06-12/13) — walled spans cover themselves with masonry fill.
            PlaceFloorDeck(
                plan,
                floor,
                deckCenter,
                segment.direction,
                segment.exitLevel,
                $"span_{deckCenter.x:0.#}_{deckCenter.z:0.#}",
                request.deck ? flightPoses : null,
                addBottomCap: request.style == ForgeSideStyle.Bridge);

            if (request.style == ForgeSideStyle.Walled && segment.exitLevel > 0)
            {
                PlaceBaseFill(plan, library, deckCenter, yaw, runSize, CellSize, segment.exitLevel * LevelHeight, $"span_fill_{deckCenter.x:0.#}_{deckCenter.z:0.#}");
            }

            // The span railing keeps the stairwell's guard CONTIGUOUS between the
            // flight railings on either side — a mid-stair gap reads broken even
            // at low levels.
            // A deck is elevated wholesale at placement, so its railings place at
            // the contract base; ordinary spans guard only above ground level.
            ForgePiece railing = coalescedPair ? library.fullRailing : library.halfRailing;
            if ((segment.exitLevel > 0 || request.deck) && railing != null && (lane == 0 || lane == request.lanes - 1))
            {
                foreach (float side in RailSidesForLane(lane, request.lanes))
                {
                    Vector3 railCenter = deckCenter + RotateYaw(new Vector3(0f, 0f, side * (CellSize * 0.5f)), yaw);
                    Vector3 along = RotateYaw(new Vector3(runSize * 0.5f, 0f, 0f), yaw);
                    Vector3 postBase = new Vector3(railCenter.x, segment.exitLevel * LevelHeight, railCenter.z);

                    // Decision 32 follow-up: no railing where the deck runs even
                    // with adjacent floor. The bit index comes from the DECK
                    // CENTER — a coalesced pair's placing segment reports the
                    // NEXT cell (its walk edge sits mid-cell), which silently
                    // missed the mask in the first build.
                    if (request.deck)
                    {
                        int maskCellIndex = Mathf.FloorToInt(deckCenter.x / CellSize);
                        ulong mask = side > 0f ? request.deckRailPlusMask : request.deckRailMinusMask;
                        if ((mask >> maskCellIndex & 1UL) == 1UL)
                        {
                            // The railing goes (floor even on both sides), and it
                            // leaves NO posts of its own: the corner column where
                            // the open stretch meets a surviving railing comes
                            // from THAT railing's end posts, and the landing-side
                            // end stands in open walkable floor where a lone post
                            // would float (user review 2026-06-12).
                            continue;
                        }
                    }

                    PlaceEdgeRailing(plan, railing, railCenter, segment.direction, segment.exitLevel, $"span_rail_{railCenter.x:0.#}_{railCenter.z:0.#}");

                    // Deck railing joints get posts (review round 4: postless
                    // joints read as gaps), deduped at shared cell boundaries.
                    if (request.deck && library.railingColumn != null)
                    {
                        AddDeckRailingPost(plan, library, postBase - along, yaw, columnPositions);
                        AddDeckRailingPost(plan, library, postBase + along, yaw, columnPositions);
                    }
                }
            }
        }

        private static void AddDeckRailingPost(
            ForgePlanRecorder plan,
            ForgePieceLibrary library,
            Vector3 position,
            float yaw,
            HashSet<string> columnPositions)
        {
            string key = $"{position.x:0.##}_{position.y:0.##}_{position.z:0.##}";
            if (columnPositions.Add(key))
            {
                plan.Add(library.railingColumn.path, $"railing_column_{key}", position, yaw);
            }
        }

        private static void PlaceTurnLanding(
            ForgePlanRecorder plan,
            WalkSegment segment,
            Vector3 laneOffset,
            ForgeRequest request,
            ForgeStyle style,
            ForgePieceLibrary library,
            HashSet<string> columnPositions)
        {
            ForgePiece floor = library.FindFloor(CellSize, CellSize);
            if (floor == null)
            {
                throw new InvalidOperationException("no measured full-cell floor for the turn landing");
            }

            Vector3 cellCenter = new Vector3(
                segment.cell.x * CellSize + HalfCell,
                segment.exitLevel * LevelHeight,
                segment.cell.y * CellSize + HalfCell) + laneOffset;
            // Bridge-style turn landings hang in the air: their undersides get
            // the flipped-slab bottom cap like every bridge flat (user review
            // 2026-06-13: a missing cap on a synthesized bridge turn).
            PlaceFloorDeck(
                plan,
                floor,
                cellCenter,
                segment.direction,
                segment.exitLevel,
                $"turn_{segment.cell.x}_{segment.cell.y}",
                addBottomCap: request.style == ForgeSideStyle.Bridge);

            if (request.style == ForgeSideStyle.Walled && segment.exitLevel > 0)
            {
                PlaceBaseFill(plan, library, cellCenter, 0f, CellSize, CellSize, segment.exitLevel * LevelHeight, $"turn_fill_{segment.cell.x}_{segment.cell.y}");
            }

            // Guard the two open edges (not the incoming edge, not the outgoing
            // edge) when the deck is >=2u above the prefab base (ledge policy).
            if (segment.exitLevel < 2 || library.fullRailing == null)
            {
                return;
            }

            Vector2Int inDir = segment.direction;
            Vector2Int outDir = segment.turnSign > 0
                ? new Vector2Int(inDir.y, -inDir.x)
                : new Vector2Int(-inDir.y, inDir.x);
            var openSides = new List<Vector2Int>();
            foreach (Vector2Int side in new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) })
            {
                if (side == -inDir || side == outDir)
                {
                    continue;
                }

                openSides.Add(side);
                Vector3 railCenter = cellCenter + new Vector3(side.x, 0f, side.y) * HalfCell;
                Vector2Int railDirection = new Vector2Int(-side.y, side.x);
                PlaceEdgeRailing(plan, library.fullRailing, railCenter, railDirection, segment.exitLevel, $"turn_rail_{railCenter.x:0.#}_{railCenter.z:0.#}");
            }

            // The two open-edge railings always share the landing's outward
            // corner; a column closes their joint the same way flight railings
            // get end columns.
            if (openSides.Count == 2 && library.railingColumn != null)
            {
                Vector3 corner = cellCenter + new Vector3(
                    openSides[0].x + openSides[1].x,
                    0f,
                    openSides[0].y + openSides[1].y) * HalfCell;
                string key = $"{corner.x:0.##}_{corner.y:0.##}_{corner.z:0.##}";
                if (columnPositions.Add(key))
                {
                    plan.Add(library.railingColumn.path, $"railing_column_{key}", corner, 0f);
                }
            }
        }

        private static void PlaceCurve(
            ForgePlanRecorder plan,
            WalkSegment segment,
            ForgeRequest request,
            ForgeStyle style,
            ForgePieceLibrary library,
            List<(string, Vector3, float)> flightPoses,
            HashSet<string> columnPositions)
        {
            ForgePiece curve = library.FindCurvedFlight(style.steepness, segment.turnSign);
            if (curve == null)
            {
                throw new InvalidOperationException($"no measured curved flight (rise {style.steepness}, turn {segment.turnSign})");
            }

            // Anchor by the measured exit socket like every flight; the yaw maps
            // the piece's measured exit direction onto the world exit direction,
            // and the measured entry direction must then land on the incoming
            // walk edge — guaranteed by selecting the piece by turn sign, and
            // verified here so a mis-measured chirality fails loudly.
            Vector2Int worldExit = segment.turnSign > 0
                ? new Vector2Int(segment.direction.y, -segment.direction.x)
                : new Vector2Int(-segment.direction.y, segment.direction.x);
            float yaw = YawMappingDirections(curve.exitSocketDirection, worldExit);
            if (RotateCardinal(curve.entrySocketDirection, yaw) != -segment.direction)
            {
                throw new InvalidOperationException(
                    $"curved flight '{curve.name}' chirality mismatch: entry face does not meet the incoming walk edge at turn sign {segment.turnSign}");
            }

            Vector3 position = segment.exitEdgeCenter - RotateYaw(new Vector3(curve.exitSocketLocal.x, 0f, curve.exitSocketLocal.z), yaw);
            position.y = segment.exitLevel * LevelHeight;
            plan.Add(curve.path, $"curve_{flightPoses.Count}", position, yaw);
            flightPoses.Add((curve.path, position, yaw));

            // The hand-authored curved stairs co-locate the whole kit: arc railing
            // on the outer side, the curved wall (walled) or curved botcap
            // (bridge) as the body.
            ForgePiece railing = library.FindCurvedRailing(curve);
            if (railing != null)
            {
                plan.Add(railing.path, $"curve_railing_{flightPoses.Count - 1}", position, yaw);
            }

            ForgePiece dressing = request.style == ForgeSideStyle.Walled
                ? library.FindCurvedDressing(curve, "stairCurvedWall")
                : library.FindCurvedDressing(curve, "stairCurvedBotCap");
            if (dressing != null)
            {
                plan.Add(dressing.path, $"curve_dressing_{flightPoses.Count - 1}", position, yaw);
            }

            int footLevel = segment.exitLevel - style.steepness;
            Vector3 cellCenter = new Vector3(
                segment.cell.x * CellSize + HalfCell,
                0f,
                segment.cell.y * CellSize + HalfCell);

            // Masonry fill below an elevated curve (walled style), same convention
            // as flights: the curved wall shell covers the curve's own body, the
            // stack below tops out at the foot level. The fill follows the curve's
            // silhouette: quarter-round (convex) bases rotated so their arc opens
            // toward the curve's outer corner, falling back to straight blocks if
            // the convex family is missing or rotationally unmeasurable.
            if (request.style == ForgeSideStyle.Walled && footLevel > 0)
            {
                var outerDiagonal = new Vector2Int(
                    segment.direction.x - worldExit.x,
                    segment.direction.y - worldExit.y);
                if (!TryPlaceCurvedBaseFill(plan, library, cellCenter, outerDiagonal, footLevel * LevelHeight, $"curve_fill_{flightPoses.Count - 1}"))
                {
                    PlaceBaseFill(plan, library, cellCenter, 0f, CellSize, CellSize, footLevel * LevelHeight, $"curve_fill_{flightPoses.Count - 1}");
                }
            }

            // The arc railing runs along the outer rim, from the entry edge's
            // outer corner (foot height) to the exit edge's outer corner (top);
            // both ends get posts, deduped against the neighbouring flights'.
            if (railing != null && library.railingColumn != null)
            {
                var inDir = new Vector3(segment.direction.x, 0f, segment.direction.y);
                var outDir = new Vector3(worldExit.x, 0f, worldExit.y);
                Vector3[] posts =
                {
                    cellCenter - inDir * HalfCell - outDir * HalfCell + Vector3.up * (footLevel * LevelHeight),
                    cellCenter + inDir * HalfCell + outDir * HalfCell + Vector3.up * (segment.exitLevel * LevelHeight),
                };
                foreach (Vector3 post in posts)
                {
                    string key = $"{post.x:0.##}_{post.y:0.##}_{post.z:0.##}";
                    if (columnPositions.Add(key))
                    {
                        plan.Add(library.railingColumn.path, $"railing_column_{key}", post, 0f);
                    }
                }
            }
        }

        private static bool TryPlaceCurvedBaseFill(
            ForgePlanRecorder plan,
            ForgePieceLibrary library,
            Vector3 cellCenter,
            Vector2Int outerDiagonal,
            float topHeight,
            string name)
        {
            List<ForgePiece> denominations = library.ConvexBases();
            if (denominations.Count == 0)
            {
                return false;
            }

            // Resolve the whole stack first so a single unorientable piece falls
            // the entire fill back to straight blocks (mixed families read odd).
            var courses = new List<(ForgePiece piece, float top)>();
            float top = topHeight;
            float remaining = topHeight;
            while (remaining > 0.01f)
            {
                ForgePiece piece = denominations.LastOrDefault(d => d.sizeUnits.y <= remaining + 1.01f) ?? denominations[0];
                if (piece.OuterOpenQuadrant() == Vector2Int.zero)
                {
                    return false;
                }

                courses.Add((piece, top));
                top -= piece.sizeUnits.y;
                remaining -= piece.sizeUnits.y;
                if (courses.Count > 8)
                {
                    return false;
                }
            }

            for (int i = 0; i < courses.Count; i++)
            {
                (ForgePiece piece, float courseTop) = courses[i];
                float yaw = YawMappingDirections(piece.OuterOpenQuadrant(), outerDiagonal);
                Vector3 rotatedCenter = RotateYaw(piece.boundsCenter, yaw);
                Vector3 position = new Vector3(
                    cellCenter.x - rotatedCenter.x,
                    courseTop - piece.boundsMax.y,
                    cellCenter.z - rotatedCenter.z);
                plan.Add(piece.path, $"fill_{name}_{i}", position, yaw);
            }

            return true;
        }

        // Cardinal-yaw rotation as pure math: Quaternion.Euler/eulerAngles are
        // native ECalls and the plan builder must stay headless-safe (online
        // synthesis runs inside the 100-seed planning harness). One quarter turn
        // about +Y in Unity's frame maps (x, z) -> (z, -x).
        private static Vector3 RotateYaw(Vector3 value, float yawDegrees)
        {
            int quarterTurns = Mathf.RoundToInt(Mathf.Repeat(yawDegrees, 360f) / 90f) % 4;
            Vector3 result = value;
            for (int i = 0; i < quarterTurns; i++)
            {
                result = new Vector3(result.z, result.y, -result.x);
            }

            return result;
        }

        private static Vector2Int RotateCardinal(Vector2Int direction, float yaw)
        {
            int quarterTurns = Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 90f) % 4;
            Vector2Int result = direction;
            for (int i = 0; i < quarterTurns; i++)
            {
                result = new Vector2Int(result.y, -result.x);
            }

            return result;
        }

        private static float YawMappingDirections(Vector2Int from, Vector2Int to)
        {
            foreach (float yaw in new[] { 0f, 90f, 180f, 270f })
            {
                if (RotateCardinal(from, yaw) == to)
                {
                    return yaw;
                }
            }

            throw new InvalidOperationException($"no cardinal yaw maps {from} onto {to}");
        }

        private static IEnumerable<float> RailSidesForLane(int lane, int lanes)
        {
            if (lane == 0)
            {
                yield return -1f;
            }

            if (lane == lanes - 1)
            {
                yield return 1f;
            }
        }

        private static void PlaceFloorDeck(
            ForgePlanRecorder plan,
            ForgePiece floor,
            Vector3 deckCenter,
            Vector2Int direction,
            int level,
            string name,
            List<(string, Vector3, float)> anchorPoses = null,
            bool addBottomCap = false)
        {
            // Floors place by measured bounds: top face on the exact level plane,
            // plan bounds centered on the deck rect, rotated so the measured SHORT
            // axis follows the walk (a half floor runs 2u deep along the path and
            // spans the full 4u width across it).
            bool longAxisIsX = floor.sizeUnits.x >= floor.sizeUnits.z;
            bool walkIsX = Mathf.Abs(direction.x) > 0;
            float yaw = longAxisIsX == walkIsX ? 90f : 0f;
            if (Mathf.Abs(floor.sizeUnits.x - floor.sizeUnits.z) <= 0.25f)
            {
                yaw = 0f;
            }

            Vector3 rotatedCenter = RotateYaw(floor.boundsCenter, yaw);
            Vector3 position = new Vector3(
                deckCenter.x - rotatedCenter.x,
                level * LevelHeight - floor.boundsMax.y,
                deckCenter.z - rotatedCenter.z);
            plan.Add(floor.path, $"deck_{name}", position, yaw);
            anchorPoses?.Add((floor.path, position, yaw));

            // Bottom cap (user reviews 2026-06-12/13): the pack has no ceiling
            // family, so the SAME slab flips face-down, sunk slightly so the
            // faces never fight. It must share THIS function's yaw choice — a
            // half floor may sit at 90 degrees to the walk, and a cap rotated by
            // the walk yaw instead came out perpendicular.
            if (addBottomCap)
            {
                Vector3 pitchedCenter = RotateYaw(new Vector3(floor.boundsCenter.x, 0f, -floor.boundsCenter.z), yaw);
                var capPosition = new Vector3(
                    deckCenter.x - pitchedCenter.x,
                    level * LevelHeight - DeckBottomCapSink + floor.boundsMax.y,
                    deckCenter.z - pitchedCenter.z);
                plan.Add(floor.path, $"deck_cap_{name}", capPosition, yaw, 180f);
            }
        }

        private static void PlaceEdgeRailing(ForgePlanRecorder plan, ForgePiece railing, Vector3 edgeCenter, Vector2Int edgeDirection, int level, string name)
        {
            // Flat railings place by measured plan bounds (length along the edge,
            // thickness centered on the edge line) but PIVOT-anchor vertically:
            // the pack authors them with the pivot on the floor plane and a
            // decorative skirt hanging below it to overlap the deck edge, so the
            // railing's y must equal the floor piece's y, not sit bounds-bottom
            // on the deck (that floated every flat railing ~0.5u high).
            float yaw = railing.widthAxisIsX == (Mathf.Abs(edgeDirection.x) > 0) ? 0f : 90f;
            Vector3 rotatedCenter = RotateYaw(railing.boundsCenter, yaw);
            Vector3 position = new Vector3(
                edgeCenter.x - rotatedCenter.x,
                level * LevelHeight,
                edgeCenter.z - rotatedCenter.z);
            plan.Add(railing.path, $"rail_{name}", position, yaw);
        }

        private static void PlaceBaseFill(
            ForgePlanRecorder plan,
            ForgePieceLibrary library,
            Vector3 planCenter,
            float yawDegrees,
            float runSize,
            float wideSize,
            float topHeight,
            string name)
        {
            // Change-making over the measured straight base heights, top-anchored:
            // the stack top sits exactly at topHeight and any odd remainder sinks
            // below the entry plane (hidden in the tier mass), mirroring the
            // engine's top-anchored drop-face walls. Narrow rects use the 2u-plan
            // denominations, paired laterally to span the full width.
            List<ForgePiece> denominations = library.StraightBases(runSize, wideSize);
            if (denominations.Count == 0)
            {
                return;
            }

            float top = topHeight;
            float remaining = topHeight;
            int course = 0;
            while (remaining > 0.01f)
            {
                ForgePiece piece = denominations.LastOrDefault(d => d.sizeUnits.y <= remaining + 1.01f) ?? denominations[0];
                int lateralCount = Mathf.Max(1, Mathf.RoundToInt(wideSize / piece.sizeUnits.z));
                for (int i = 0; i < lateralCount; i++)
                {
                    float lateral = (i + 0.5f) * piece.sizeUnits.z - wideSize * 0.5f;
                    Vector3 pieceCenter = planCenter + RotateYaw(new Vector3(0f, 0f, lateral), yawDegrees);
                    Vector3 rotatedCenter = RotateYaw(piece.boundsCenter, yawDegrees);
                    Vector3 position = new Vector3(
                        pieceCenter.x - rotatedCenter.x,
                        top - piece.boundsMax.y,
                        pieceCenter.z - rotatedCenter.z);
                    plan.Add(piece.path, $"fill_{name}_{course}_{i}", position, yawDegrees);
                }

                top -= piece.sizeUnits.y;
                remaining -= piece.sizeUnits.y;
                course++;
                if (course > 8)
                {
                    throw new InvalidOperationException($"base fill runaway under '{name}'");
                }
            }
        }

        private static GameObject Instantiate(Transform root, string prefabPath, string name, Vector3 localPosition, float yaw, float pitch = 0f)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException($"missing prefab '{prefabPath}'");
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = name;
            instance.transform.SetParent(root, worldPositionStays: false);
            instance.transform.localPosition = localPosition;
            instance.transform.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            return instance;
        }

        // ---------------------------------------------------------------------
        // Contract emission (same plan data as the geometry above).
        // ---------------------------------------------------------------------

        private static JObject BuildContract(
            string name,
            ForgeRequest request,
            ForgeStyle style,
            ForgeCandidate candidate,
            List<(string sourcePrefab, Vector3 position, float yaw)> flightPoses)
        {
            // Re-index footprint cells against the min cell so the contract grid
            // starts at (0,0); local edge positions stay in the prefab frame. The
            // cursor already claimed lane neighbours during the walk.
            List<Vector2Int> rawCells = candidate.footprint;
            int minX = rawCells.Min(c => c.x);
            int minZ = rawCells.Min(c => c.y);
            int sizeX = rawCells.Max(c => c.x) - minX + 1;
            int sizeZ = rawCells.Max(c => c.y) - minZ + 1;
            var boundsMin = new Vector3(minX * CellSize, 0f, minZ * CellSize);

            List<Vector2Int> footprint = rawCells.Select(c => new Vector2Int(c.x - minX, c.y - minZ)).ToList();
            footprint.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));

            List<Vector2Int> entryCells = PortCells(candidate.entryEdge, candidate.entryDirection, request.lanes, minX, minZ, entry: true);
            List<Vector2Int> exitCells = PortCells(candidate.exitEdge, candidate.exitDirection, request.lanes, minX, minZ, entry: false);
            Vector3 entryEdgeCenter = PortEdgeCenter(candidate.entryEdge, candidate.entryDirection, request.lanes, 0);
            Vector3 exitEdgeCenter = PortEdgeCenter(candidate.exitEdge, candidate.exitDirection, request.lanes, request.rise);

            int runLength = candidate.segments.TakeWhile(s => s.kind == SegmentKind.Flight || s.kind == SegmentKind.FlatSpan).Count() > 0
                ? Mathf.Max(1, Mathf.CeilToInt(candidate.segments
                    .TakeWhile(s => s.kind == SegmentKind.Flight || s.kind == SegmentKind.FlatSpan)
                    .Sum(s => s.kind == SegmentKind.Flight ? HalfCellsPerFlight(style.steepness) : 1) * 0.5f))
                : 1;

            var contract = new JObject
            {
                ["name"] = name,
                ["prefab"] = PrefabPathFor(name),
                ["source"] = "forge",
                ["reviewStatus"] = "pending",
                ["forgeStyle"] = new JObject
                {
                    ["steepness"] = style.steepness,
                    ["flight"] = style.flight.name,
                    ["sideStyle"] = StyleTag(request.style),
                    ["shape"] = candidate.shapeTag,
                    ["cost"] = candidate.Cost,
                },
                ["rise"] = request.rise,
                ["laneCount"] = request.lanes,
                ["runLength"] = runLength,
                ["topology"] = candidate.turns > 0 ? "turning" : "straight",
                ["bridgeAllowed"] = request.style == ForgeSideStyle.Bridge,
                ["localBoundsMin"] = VectorToken(boundsMin),
                ["localBoundsSizeCells"] = new JObject { ["x"] = sizeX, ["z"] = sizeZ },
                ["footprintCells"] = CellArray(footprint),
                ["occupiedCells"] = CellArray(footprint),
                ["reservedCells"] = new JArray(),
                ["ports"] = new JArray(
                    PortToken(SideName(-candidate.entryDirection), 0, entryCells, entryEdgeCenter),
                    PortToken(SideName(candidate.exitDirection), request.rise, exitCells, exitEdgeCenter)),
                ["visualAnchors"] = new JArray(new JObject
                {
                    ["role"] = "exitSurfaceRoots",
                    ["sourcePrefabs"] = new JArray(flightPoses.Select(p => p.sourcePrefab).Distinct().OrderBy(p => p, StringComparer.Ordinal)),
                }),
                ["sourceRootPoses"] = new JArray(flightPoses.Select(p => new JObject
                {
                    ["sourcePrefab"] = p.sourcePrefab,
                    ["localPosition"] = VectorToken(p.position),
                    ["localYawDegrees"] = Round(p.yaw),
                })),
            };

            return contract;
        }

        private static List<Vector2Int> PortCells(Vector3 edge, Vector2Int direction, int lanes, int minX, int minZ, bool entry)
        {
            // The port's cells are the footprint cells whose boundary carries the
            // walk edge: just inside the footprint (ahead of the entry edge, behind
            // the exit edge), one per lane stacked to the walker's left.
            Vector2Int lateral = new Vector2Int(-direction.y, direction.x);
            Vector2Int probe = entry ? direction : new Vector2Int(-direction.x, -direction.y);
            var cells = new List<Vector2Int>();
            var baseCell = new Vector2Int(
                Mathf.FloorToInt((edge.x + probe.x * HalfCell) / CellSize),
                Mathf.FloorToInt((edge.z + probe.y * HalfCell) / CellSize));
            for (int lane = 0; lane < lanes; lane++)
            {
                Vector2Int cell = baseCell + lateral * lane;
                cells.Add(new Vector2Int(cell.x - minX, cell.y - minZ));
            }

            cells.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            return cells;
        }

        private static Vector3 PortEdgeCenter(Vector3 edge, Vector2Int direction, int lanes, int level)
        {
            // Lane 0 carries the walk edge; the port spans all lanes, so its edge
            // position is the center of the full span (lanes extend to the
            // walker's left).
            Vector2Int lateral = new Vector2Int(-direction.y, direction.x);
            Vector3 center = edge + new Vector3(lateral.x, 0f, lateral.y) * (CellSize * 0.5f * (lanes - 1));
            return new Vector3(center.x, level * LevelHeight, center.z);
        }

        private static string SideName(Vector2Int outward)
        {
            if (outward == Vector2Int.right) return "E";
            if (outward == Vector2Int.left) return "W";
            if (outward == Vector2Int.up) return "N";
            return "S";
        }

        private static JObject PortToken(string side, int level, List<Vector2Int> cells, Vector3 localEdgePosition)
        {
            return new JObject
            {
                ["side"] = side,
                ["level"] = level,
                ["cells"] = CellArray(cells),
                ["localEdgePosition"] = VectorToken(localEdgePosition),
            };
        }

        private static JArray CellArray(IEnumerable<Vector2Int> cells)
        {
            return new JArray(cells.Select(c => new JObject { ["x"] = c.x, ["z"] = c.y }));
        }

        private static JObject VectorToken(Vector3 value)
        {
            return new JObject { ["x"] = Round(value.x), ["y"] = Round(value.y), ["z"] = Round(value.z) };
        }

        private static float Round(float value)
        {
            return Mathf.Round(value * 1000f) / 1000f;
        }

        private static float YawFromDirection(Vector2Int direction)
        {
            // The flight family climbs +x at yaw 0.
            if (direction == Vector2Int.right) return 0f;
            if (direction == Vector2Int.down) return 90f;
            if (direction == Vector2Int.left) return 180f;
            return 270f;
        }

        private static string StyleTag(ForgeSideStyle style)
        {
            return style == ForgeSideStyle.Bridge ? "bridge" : "walled";
        }

        private static string PrefabPathFor(string name)
        {
            return $"{ForgedPrefabFolder}/{name}.prefab";
        }

        private static void EnsureForgedFolder()
        {
            if (!AssetDatabase.IsValidFolder(ForgedPrefabFolder))
            {
                AssetDatabase.CreateFolder("Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/Stairs", "Forged");
            }
        }

        private static JObject LoadOrCreateContractsRoot()
        {
            if (File.Exists(ForgedContractsPath))
            {
                JObject root = JObject.Parse(File.ReadAllText(ForgedContractsPath));
                if (!(root["contracts"] is JArray))
                {
                    root["contracts"] = new JArray();
                }

                return root;
            }

            return new JObject
            {
                ["_doc"] = "Forge output (stair-forge step 6). Same contract shape as stair_proof_contracts.json; " +
                    "entries stay reviewStatus \"pending\" until a human reviews the prefab in-editor and flips them to \"reviewed\", " +
                    "which admits them to active generation on equal cost terms with the hand-authored pool.",
                ["cellSize"] = CellSize,
                ["levelHeight"] = LevelHeight,
                ["contracts"] = new JArray(),
            };
        }

        private static JObject FindContract(JArray contracts, string name)
        {
            foreach (JToken token in contracts)
            {
                if (string.Equals(token.Value<string>("name"), name, StringComparison.Ordinal))
                {
                    return token as JObject;
                }
            }

            return null;
        }

        private static void ReplaceContract(JArray contracts, string name, JObject contract)
        {
            JObject prior = FindContract(contracts, name);
            if (prior != null)
            {
                prior.Replace(contract);
            }
            else
            {
                contracts.Add(contract);
            }
        }

        private static bool TryResolveSelectedForgedStaircase(out string contractName, out string prefabPath, out string error)
        {
            contractName = string.Empty;
            prefabPath = string.Empty;
            error = string.Empty;

            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                error = $"Select a forged stair prefab instance under '{ReviewQueueRootName}' first.";
                return false;
            }

            Transform cursor = selected.transform;
            while (cursor != null)
            {
                string path = AssetDatabase.GetAssetPath(cursor.gameObject);
                if (string.IsNullOrEmpty(path))
                {
                    GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(cursor.gameObject);
                    path = source != null ? AssetDatabase.GetAssetPath(source) : string.Empty;
                }

                if (!string.IsNullOrEmpty(path) &&
                    path.StartsWith(ForgedPrefabFolder + "/", StringComparison.Ordinal) &&
                    path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    prefabPath = path;
                    contractName = Path.GetFileNameWithoutExtension(path);
                    return true;
                }

                if (cursor.name.StartsWith("review_", StringComparison.Ordinal))
                {
                    contractName = cursor.name.Substring("review_".Length);
                    return true;
                }

                cursor = cursor.parent;
            }

            error = $"Selected object '{selected.name}' is not a forged stair prefab instance or review gallery slot.";
            return false;
        }

        private static bool MatchesSelectedForgedContract(JObject contract, string selectedName, string selectedPrefabPath)
        {
            if (!string.Equals(contract.Value<string>("source"), "forge", StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(selectedPrefabPath) &&
                string.Equals(contract.Value<string>("prefab"), selectedPrefabPath, StringComparison.Ordinal))
            {
                return true;
            }

            return !string.IsNullOrEmpty(selectedName) &&
                string.Equals(contract.Value<string>("name"), selectedName, StringComparison.Ordinal);
        }

        internal static int StableHash(string value)
        {
            unchecked
            {
                int hash = 23;
                foreach (char c in value)
                {
                    hash = hash * 31 + c;
                }

                return hash;
            }
        }

        // ---------------------------------------------------------------------
        // Review gallery: a row of forged stairs with entry/exit context floors,
        // rebuilt under one root in the open scene each run.
        // ---------------------------------------------------------------------

        private static void BuildReviewGallery(List<(string name, string prefabPath, int rise, int runCells)> prefabs)
        {
            GameObject prior = GameObject.Find(ReviewQueueRootName);
            if (prior != null)
            {
                UnityEngine.Object.DestroyImmediate(prior);
            }

            if (prefabs.Count == 0)
            {
                return;
            }

            var root = new GameObject(ReviewQueueRootName);
            float offsetZ = 0f;
            foreach ((string name, string prefabPath, int rise, int runCells) in prefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    continue;
                }

                var slot = new GameObject($"review_{name}");
                slot.transform.SetParent(root.transform, false);
                slot.transform.localPosition = new Vector3(0f, 0f, offsetZ);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, slot.transform);
                instance.transform.localPosition = Vector3.zero;
                offsetZ += (runCells + 4) * CellSize;
            }

            Debug.Log($"Dungeon Lab Forge: review gallery rebuilt under '{ReviewQueueRootName}' ({prefabs.Count} staircases).");
        }

        // ---------------------------------------------------------------------
        // Synthesis pending-review queue (step 7, design decision 19): every
        // staircase a generated dungeon used provisionally is logged with its
        // contract + piece plan; the gallery is rebuilt from the log on demand.
        // The log measures whether the automated gates can be trusted without
        // eyes — full autonomy only once it stops catching anything.
        // ---------------------------------------------------------------------

        private const string SynthesisLogPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/synthesized_stair_log.json";
        private const string SynthesisReviewRootName = "DungeonLab_SynthesisReviewQueue";

        internal static void AppendSynthesisLog(
            int dungeonSeed,
            IReadOnlyList<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)> records)
        {
            if (records == null || records.Count == 0)
            {
                return;
            }

            JObject root = File.Exists(SynthesisLogPath)
                ? JObject.Parse(File.ReadAllText(SynthesisLogPath))
                : new JObject
                {
                    ["_doc"] = "Online-synthesis pending review queue (stair-forge step 7). Each entry is a staircase a " +
                        "generated dungeon used provisionally: contract + piece plan from one forge plan. Build the gallery " +
                        "(Tools > Dungeon Lab > Synthesis Review: Build Gallery), eyeball it, then mark entries reviewed.",
                    ["entries"] = new JArray(),
                };
            if (!(root["entries"] is JArray entries))
            {
                entries = new JArray();
                root["entries"] = entries;
            }

            foreach ((string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece) in records)
            {
                // Same dungeon regenerated: replace the prior entry, never duplicate.
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i].Value<int?>("seed") == dungeonSeed &&
                        string.Equals(entries[i].Value<string>("gapId"), gapId, StringComparison.Ordinal))
                    {
                        entries.RemoveAt(i);
                    }
                }

                entries.Add(new JObject
                {
                    ["seed"] = dungeonSeed,
                    ["gapId"] = gapId,
                    ["name"] = setPiece.name,
                    ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                    ["reviewStatus"] = "provisional",
                    ["contract"] = setPiece.contractToken.DeepClone(),
                    ["pieces"] = new JArray(setPiece.pieces.Select(p => new JObject
                    {
                        ["sourcePrefab"] = p.sourcePrefab,
                        ["name"] = p.pieceName,
                        ["localPosition"] = VectorToken(p.localPosition),
                        ["localYawDegrees"] = Round(p.localYawDegrees),
                        ["localPitchDegrees"] = Round(p.localPitchDegrees),
                    })),
                });
            }

            File.WriteAllText(SynthesisLogPath, root.ToString(Unity.Plastic.Newtonsoft.Json.Formatting.Indented));
            AssetDatabase.ImportAsset(SynthesisLogPath);
            Debug.Log($"Dungeon Lab Synthesis: logged {records.Count} provisional staircase(s) to {SynthesisLogPath} (pending review queue).");
        }

        [MenuItem("Tools/Dungeon Lab/Synthesis Review: Build Gallery")]
        public static void BuildSynthesisReviewGallery()
        {
            GameObject prior = GameObject.Find(SynthesisReviewRootName);
            if (prior != null)
            {
                UnityEngine.Object.DestroyImmediate(prior);
            }

            if (!File.Exists(SynthesisLogPath))
            {
                Debug.Log("Dungeon Lab Synthesis: no synthesis log yet — nothing to review.");
                return;
            }

            JObject root = JObject.Parse(File.ReadAllText(SynthesisLogPath));
            List<JObject> provisional = ((root["entries"] as JArray) ?? new JArray())
                .OfType<JObject>()
                .Where(e => string.Equals(e.Value<string>("reviewStatus"), "provisional", StringComparison.Ordinal))
                .ToList();
            if (provisional.Count == 0)
            {
                Debug.Log("Dungeon Lab Synthesis: review queue is empty (no provisional entries).");
                return;
            }

            var galleryRoot = new GameObject(SynthesisReviewRootName);
            float offsetZ = 0f;
            foreach (JObject entry in provisional)
            {
                var slot = new GameObject(SynthesisReviewSlotName(entry));
                slot.transform.SetParent(galleryRoot.transform, false);
                slot.transform.localPosition = new Vector3(0f, 0f, offsetZ);
                float maxRunCells = 1f;
                if (entry["pieces"] is JArray pieces)
                {
                    foreach (JObject piece in pieces.OfType<JObject>())
                    {
                        var position = new Vector3(
                            piece["localPosition"].Value<float>("x"),
                            piece["localPosition"].Value<float>("y"),
                            piece["localPosition"].Value<float>("z"));
                        Instantiate(
                            slot.transform,
                            piece.Value<string>("sourcePrefab"),
                            piece.Value<string>("name"),
                            position,
                            piece.Value<float?>("localYawDegrees") ?? 0f,
                            piece.Value<float?>("localPitchDegrees") ?? 0f);
                        maxRunCells = Mathf.Max(maxRunCells, position.x / CellSize);
                    }
                }

                offsetZ += (Mathf.CeilToInt(maxRunCells) + 4) * CellSize;
            }

            Debug.Log(
                $"Dungeon Lab Synthesis: review gallery rebuilt under '{SynthesisReviewRootName}' ({provisional.Count} staircases). " +
                "Select a slot and run Synthesis Review: Mark Selected Reviewed once it passes your eyes.");
        }

        // Out-of-context review (like the forge batch gallery): every design the
        // synthesizer can currently produce, per rise, without waiting for a
        // dungeon to need one. The in-context queue (synthesized_stair_log.json)
        // stays the trust bar; this answers "what would it build?" on demand.
        [MenuItem("Tools/Dungeon Lab/Synthesis Review: Build Design Gallery")]
        public static void BuildSynthesisDesignGallery()
        {
            const string rootName = "DungeonLab_SynthesisDesignGallery";
            GameObject prior = GameObject.Find(rootName);
            if (prior != null)
            {
                UnityEngine.Object.DestroyImmediate(prior);
            }

            var root = new GameObject(rootName);
            float offsetZ = 0f;
            int designCount = 0;
            var failures = new List<string>();
            for (int rise = 2; rise <= 10; rise++)
            {
                var designs = new List<SynthesizedStaircaseDesign>(EnumerateSynthesisDesigns(rise, out string failureSummary));
                designs.AddRange(EnumerateStairwellSynthesisDesigns(rise, out string stairwellFailureSummary));
                // Aerial decks are per-length, not per-rise; show them on the
                // matching row so every span length appears once.
                SynthesizedStaircaseDesign deck = SynthesizeDeckDesign(rise, out _);
                if (deck != null)
                {
                    designs.Add(deck);
                }

                if (designs.Count == 0)
                {
                    failures.Add($"r{rise}: {failureSummary} | stairwells: {stairwellFailureSummary}");
                    continue;
                }

                float offsetX = 0f;
                int rowDepthCells = 1;
                foreach (SynthesizedStaircaseDesign design in designs)
                {
                    float minX = design.contract["localBoundsMin"].Value<float>("x");
                    float minZ = design.contract["localBoundsMin"].Value<float>("z");
                    int sizeX = design.contract["localBoundsSizeCells"].Value<int>("x");
                    int sizeZ = design.contract["localBoundsSizeCells"].Value<int>("z");

                    var slot = new GameObject($"design_{design.name}");
                    slot.transform.SetParent(root.transform, false);
                    slot.transform.localPosition = new Vector3(offsetX - minX, 0f, offsetZ - minZ);
                    foreach (ElevationEdgeModel.SynthesizedPiecePlacement piece in design.pieces)
                    {
                        Instantiate(slot.transform, piece.sourcePrefab, piece.pieceName, piece.localPosition, piece.localYawDegrees, piece.localPitchDegrees);
                    }

                    offsetX += (sizeX + 1) * CellSize;
                    rowDepthCells = Mathf.Max(rowDepthCells, sizeZ);
                    designCount++;
                }

                offsetZ += (rowDepthCells + 2) * CellSize;
            }

            Debug.Log(
                $"Dungeon Lab Synthesis: design gallery rebuilt under '{rootName}' ({designCount} designs, one row per rise 2-10)." +
                (failures.Count > 0 ? $" No designs for: {string.Join(" | ", failures)}" : string.Empty));
        }

        // ===== Dais forge (step 9, decisions 39-42) =====
        //
        // A dais design is a contour-dressed raised or sunken rect: full-cell
        // round/angle corner pieces + floorRound caps at the contour's corner
        // cells (the gold throne construction), straight strips along its edge
        // faces, plain floors elsewhere, optional flanking columns on the top
        // tier. EmitDaisTier is the seed of the unified contour dresser
        // (decision 39): it walks a rect boundary today; arbitrary contour
        // paths (zone seams) reuse the same emission later.
        //
        // GEOMETRY CONVENTIONS (calibrate via the gallery; each is a one-table
        // fix if review shows rotation/seating errors — the curve-socket
        // pattern): full-cell pieces pivot at the TOP of the rise, footprint
        // (-x,+z) at yaw 0, and the curved/chamfered corner is ASSUMED at
        // local (-4,0); strips anchor by their measured exit socket with local
        // climb +x. The concave step family measures ~0.25u short of nominal
        // rise — gallery review decides whether that needs a seat offset.
        // Sunken corner yaw calibration: +90 from the original assumed table
        // (gallery round 2 approved the sunken rows at this offset). Raised
        // corners use the notch quadrant map directly — see EmitDaisTier.
        private const float DaisConcaveCornerYawOffset = 90f;

        internal sealed class DaisDesign
        {
            public string name;
            public readonly List<ElevationEdgeModel.SynthesizedPiecePlacement> pieces = new List<ElevationEdgeModel.SynthesizedPiecePlacement>();
            public int sizeCellsX;
            public int sizeCellsZ;
            public bool sunken;
            public bool backed;
            public bool gold;

            // Contoured (non-rect) designs list their mass cells explicitly;
            // null means the full sizeCellsX x sizeCellsZ rect. An EMPTY set
            // means no cell is mass (gold reproductions: the scene grounds
            // run under the whole composition).
            public HashSet<Vector2Int> cells;
        }

        // Placement metadata for the backed-dais grammar. The generator must
        // validate this contract before it accepts a recipe; the renderer still
        // consumes only the immutable piece plan.
        internal readonly struct BackedShowpiecePlacementContract
        {
            public readonly string designName;
            public readonly int widthCells;
            public readonly int platformDepthCells;
            public readonly int requiredFloorDepthCells;
            public readonly int wallEndMarginCells;
            public readonly ElevationEdgeModel.SynthesizedPiecePlacement[] pieces;

            public BackedShowpiecePlacementContract(DaisDesign design)
            {
                designName = design.name ?? string.Empty;
                widthCells = design.sizeCellsX;
                platformDepthCells = design.sizeCellsZ;
                // Raised backed contours descend through a one-cell step apron
                // in front of their platform mass. This is the same 5x3 floor
                // envelope enforced by the retired wall-search producer, now
                // derived from the selected design instead of hard-coded.
                requiredFloorDepthCells = design.sizeCellsZ + 1;
                // A backed composition cannot terminate at a wall corner. One
                // full wall cell at each end keeps its flank strips and returns
                // supported, matching the reviewed producer's fit invariant.
                wallEndMarginCells = 1;
                pieces = design.pieces.ToArray();
            }
        }

        // Top-down ASCII raster of a design from MEASURED piece bounds —
        // the headless eye for contour work (decision 45): band gaps,
        // overlaps and chopped noses are visible in the probe dump before
        // anything reaches the editor. One character per 1u ground cell,
        // north (+z) at the top. Glyphs encode piece kind; upper-case sits
        // at the raised level, lower-case at ground. Later pieces overwrite
        // earlier ones except floors never overwrite non-floors (steps stay
        // visible over the slabs they dress).
        private static List<(float minX, float maxX, float minZ, float maxZ, char glyph, bool floor, float yaw)> CollectDaisStamps(DaisDesign design)
        {
            ForgePieceLibrary library = ForgePieceLibrary.Load();
            var byPath = new Dictionary<string, ForgePiece>();
            foreach (ForgePiece piece in library.pieces)
            {
                byPath[piece.path] = piece;
            }

            var stamps = new List<(float minX, float maxX, float minZ, float maxZ, char glyph, bool floor, float yaw)>();
            foreach (ElevationEdgeModel.SynthesizedPiecePlacement placement in design.pieces)
            {
                if (!byPath.TryGetValue(placement.sourcePrefab, out ForgePiece piece))
                {
                    stamps.Add((placement.localPosition.x - 1f, placement.localPosition.x + 1f,
                        placement.localPosition.z - 1f, placement.localPosition.z + 1f, '?', false, 0f));
                    continue;
                }

                Vector3 a = RotateYaw(piece.boundsMin, placement.localYawDegrees) + placement.localPosition;
                Vector3 b = RotateYaw(piece.boundsMax, placement.localYawDegrees) + placement.localPosition;
                bool raised = Mathf.Max(a.y, b.y) > 0.5f;
                string name = placement.sourcePrefab;
                char glyph =
                    name.Contains("E_straight") ? (raised ? 'S' : 's') :
                    name.Contains("E_angle_concave") || name.Contains("E_concave") ? (raised ? 'V' : 'v') :
                    name.Contains("E_angle_convex") || name.Contains("E_convex") ? (raised ? 'C' : 'c') :
                    name.Contains("WallTrim") ? 'T' :
                    name.Contains("Railing") ? 'r' :
                    name.Contains("_tiny") || name.Contains("_small") ? (raised ? 'P' : 'p') :
                    name.Contains("Floor") && (name.Contains("concave") || name.Contains("convex") || name.Contains("angle"))
                        ? (raised ? 'P' : 'p') :
                    name.Contains("Floor") ? (raised ? 'F' : 'f') :
                    name.Contains("Wall") ? '#' : '?';
                bool isFloor = glyph == 'F' || glyph == 'f' || glyph == 'P' || glyph == 'p';
                stamps.Add((Mathf.Min(a.x, b.x), Mathf.Max(a.x, b.x), Mathf.Min(a.z, b.z), Mathf.Max(a.z, b.z), glyph, isFloor, Mathf.Repeat(placement.localYawDegrees, 360f)));
            }

            return stamps;
        }

        // Band invariants (decision 45) — the machine form of "stairs never
        // end abruptly in a 1u ledge":
        // (1) raised step pieces never overlap each other;
        // (2) every strip END abuts another band piece, a pad, or the wall;
        // (3) every raised TOP boundary segment — slabs AND pads — faces
        //     band coverage or lies on the back wall line. Pads were
        //     initially exempt ("sculpted ledge edges"), but gallery round 5
        //     falsified that: the gold scallop's pad front read as a chopped
        //     nose once its columns were removed — furniture never excuses a
        //     bare edge. FloorRound caps pass because their co-located step
        //     piece covers the same footprint.
        internal static List<string> ValidateDaisBand(DaisDesign design)
        {
            var violations = new List<string>();
            List<(float minX, float maxX, float minZ, float maxZ, char glyph, bool floor, float yaw)> stamps = CollectDaisStamps(design);
            var steps = stamps.Where(s => s.glyph == 'S' || s.glyph == 'V' || s.glyph == 'C').ToList();
            for (int i = 0; i < steps.Count; i++)
            {
                for (int j = i + 1; j < steps.Count; j++)
                {
                    float overlapX = Mathf.Min(steps[i].maxX, steps[j].maxX) - Mathf.Max(steps[i].minX, steps[j].minX);
                    float overlapZ = Mathf.Min(steps[i].maxZ, steps[j].maxZ) - Mathf.Max(steps[i].minZ, steps[j].minZ);
                    if (overlapX > 0.25f && overlapZ > 0.25f)
                    {
                        violations.Add($"step pieces overlap near ({Mathf.Max(steps[i].minX, steps[j].minX):0.#},{Mathf.Max(steps[i].minZ, steps[j].minZ):0.#})");
                    }
                }
            }

            float wallZ = design.backed ? design.sizeCellsZ * CellSize : float.NaN;
            bool NearWall(float z) => design.backed && Mathf.Abs(z - wallZ) < 0.6f;
            // The probe box sits fully OUTSIDE the edge being checked, so a
            // covering band piece must truly overlap it in BOTH axes.
            // (Gallery round 6 bug: a sum-of-overlaps heuristic accepted
            // zero-overlap axis contact — a strip's end probe "touched" the
            // strip's own stamp along its length and two missing corner
            // pieces sailed through.)
            bool TouchesBand(float minX, float maxX, float minZ, float maxZ) => stamps.Any(s =>
                (s.glyph == 'S' || s.glyph == 'V' || s.glyph == 'C' || s.glyph == 'P' || s.glyph == 'T') &&
                Mathf.Min(s.maxX, maxX) - Mathf.Max(s.minX, minX) > 0.2f &&
                Mathf.Min(s.maxZ, maxZ) - Mathf.Max(s.minZ, minZ) > 0.2f);

            foreach ((float minX, float maxX, float minZ, float maxZ, char glyph, bool _, float yaw) in stamps.Where(s => s.glyph == 'S'))
            {
                // Strip width (the step line) is local z; the run is local
                // x. The steep E_straight_3 runs LONGER than its width, so
                // axis inference from AABB proportions probed climb ends
                // instead of side flanks (gallery round 6).
                bool widthAlongX = Mathf.Abs(Mathf.DeltaAngle(yaw, 90f)) < 1f || Mathf.Abs(Mathf.DeltaAngle(yaw, 270f)) < 1f;
                var ends = widthAlongX
                    ? new[] { (minX - 0.5f, minX, minZ, maxZ), (maxX, maxX + 0.5f, minZ, maxZ) }
                    : new[] { (minX, maxX, minZ - 0.5f, minZ), (minX, maxX, maxZ, maxZ + 0.5f) };
                foreach ((float exMin, float exMax, float ezMin, float ezMax) in ends)
                {
                    if (NearWall(ezMin) || NearWall(ezMax) ||
                        TouchesBand(exMin, exMax, ezMin, ezMax))
                    {
                        continue;
                    }

                    violations.Add($"strip end hangs open near ({(exMin + exMax) / 2f:0.#},{(ezMin + ezMax) / 2f:0.#})");
                }
            }

            foreach ((float minX, float maxX, float minZ, float maxZ, char glyph, bool _, float _) in stamps.Where(s => s.glyph == 'F' || s.glyph == 'P'))
            {
                // FloorRound caps co-located with their step piece are the
                // step's own top surface — the curved descent is inside the
                // shared footprint, invisible to AABB probes. Skip them.
                if (glyph == 'P' && stamps.Any(s =>
                        (s.glyph == 'S' || s.glyph == 'V' || s.glyph == 'C') &&
                        Mathf.Min(s.maxX, maxX) - Mathf.Max(s.minX, minX) > (maxX - minX) * 0.5f &&
                        Mathf.Min(s.maxZ, maxZ) - Mathf.Max(s.minZ, minZ) > (maxZ - minZ) * 0.5f))
                {
                    continue;
                }

                // Probe each 1u sub-segment of every slab edge: the strip
                // just outside the edge must be covered by raised floor/pad
                // (interior edge), band steps, or the back wall line.
                foreach ((bool vertical, float line, float outward) in new[]
                         {
                             (true, minX, -1f), (true, maxX, 1f), (false, minZ, -1f), (false, maxZ, 1f)
                         })
                {
                    if (!vertical && NearWall(line))
                    {
                        continue;
                    }

                    float spanMin = vertical ? minZ : minX;
                    float spanMax = vertical ? maxZ : maxX;
                    for (float t = spanMin; t < spanMax - 0.1f; t += 1f)
                    {
                        float probeNearMin = line + (outward < 0 ? -0.6f : 0.1f);
                        float probeNearMax = line + (outward < 0 ? -0.1f : 0.6f);
                        float exMin = vertical ? probeNearMin : t + 0.1f;
                        float exMax = vertical ? probeNearMax : Mathf.Min(t + 0.9f, spanMax - 0.1f);
                        float ezMin = vertical ? t + 0.1f : probeNearMin;
                        float ezMax = vertical ? Mathf.Min(t + 0.9f, spanMax - 0.1f) : probeNearMax;
                        bool covered = stamps.Any(s => (s.glyph == 'F' || s.glyph == 'P') &&
                            s.minX <= exMin + 0.05f && s.maxX >= exMax - 0.05f &&
                            s.minZ <= ezMin + 0.05f && s.maxZ >= ezMax - 0.05f);
                        if (covered || TouchesBand(exMin, exMax, ezMin, ezMax))
                        {
                            continue;
                        }

                        violations.Add($"bare slab edge near ({(exMin + exMax) / 2f:0.#},{(ezMin + ezMax) / 2f:0.#})");
                    }
                }
            }

            return violations;
        }

        internal static string RasterizeDaisDesign(DaisDesign design)
        {
            List<(float minX, float maxX, float minZ, float maxZ, char glyph, bool floor, float yaw)> stamps = CollectDaisStamps(design);
            if (stamps.Count == 0)
            {
                return "(empty design)";
            }

            int minX = Mathf.FloorToInt(stamps.Min(s => s.minX));
            int maxX = Mathf.CeilToInt(stamps.Max(s => s.maxX));
            int minZ = Mathf.FloorToInt(stamps.Min(s => s.minZ));
            int maxZ = Mathf.CeilToInt(stamps.Max(s => s.maxZ));
            var grid = new char[maxZ - minZ, maxX - minX];
            for (int z = 0; z < maxZ - minZ; z++)
            {
                for (int x = 0; x < maxX - minX; x++)
                {
                    grid[z, x] = '.';
                }
            }

            foreach ((float sMinX, float sMaxX, float sMinZ, float sMaxZ, char glyph, bool isFloor, float _) in stamps)
            {
                // Zero-thickness pieces (walls, trims) still stamp one row.
                int z0 = Mathf.RoundToInt(sMinZ);
                int z1 = Mathf.Max(Mathf.RoundToInt(sMaxZ), z0 + 1);
                int x0 = Mathf.RoundToInt(sMinX);
                int x1 = Mathf.Max(Mathf.RoundToInt(sMaxX), x0 + 1);
                for (int z = z0; z < z1; z++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        if (z < minZ || z >= maxZ || x < minX || x >= maxX)
                        {
                            continue;
                        }

                        char existing = grid[z - minZ, x - minX];
                        if (isFloor && existing != '.' && existing != 'F' && existing != 'f')
                        {
                            continue;
                        }

                        grid[z - minZ, x - minX] = glyph;
                    }
                }
            }

            var rows = new List<string> { $"    raster x[{minX},{maxX}] z[{minZ},{maxZ}] (north up; 1 char = 1u)" };
            for (int z = maxZ - minZ - 1; z >= 0; z--)
            {
                var row = new char[maxX - minX];
                for (int x = 0; x < maxX - minX; x++)
                {
                    row[x] = grid[z, x];
                }

                rows.Add($"    z={z + minZ,3} {new string(row)}");
            }

            return string.Join("\n", rows);
        }

        // Showpiece lookup for dungeon integration (decision 46 increment
        // 2): the generator instantiates approved gallery designs verbatim
        // as backed set-piece dais. Designs are seed-independent, so one
        // cache per editor session is exact.
        private static Dictionary<string, DaisDesign> cachedShowpieceDesigns;

        internal static bool TryGetBackedShowpieceDesign(string designName, out ElevationEdgeModel.SynthesizedPiecePlacement[] pieces)
        {
            if (!TryGetBackedShowpiecePlacementContract(
                    designName,
                    out BackedShowpiecePlacementContract contract))
            {
                pieces = null;
                return false;
            }

            pieces = contract.pieces;
            return true;
        }

        internal static bool TryGetBackedShowpiecePlacementContract(
            string designName,
            out BackedShowpiecePlacementContract contract)
        {
            if (cachedShowpieceDesigns == null)
            {
                cachedShowpieceDesigns = new Dictionary<string, DaisDesign>();
                foreach (DaisDesign design in SynthesizeDaisDesigns(out _))
                {
                    cachedShowpieceDesigns[design.name] = design;
                }
            }

            if (!cachedShowpieceDesigns.TryGetValue(designName, out DaisDesign match) ||
                !match.backed ||
                match.sizeCellsX <= 0 ||
                match.sizeCellsZ <= 0)
            {
                contract = default;
                return false;
            }

            contract = new BackedShowpiecePlacementContract(match);
            return true;
        }

        internal static List<DaisDesign> SynthesizeDaisDesigns(out string failureSummary)
        {
            var failures = new List<string>();
            var designs = new List<DaisDesign>();
            ForgePieceLibrary library = ForgePieceLibrary.Load();
            foreach (string style in new[] { "angle", "round" })
            {
                TryAddDaisDesign(designs, failures, library, style, sunken: false, widthCells: 2, depthCells: 2, riseLevels: 1, tiers: 1);
                TryAddDaisDesign(designs, failures, library, style, sunken: false, widthCells: 3, depthCells: 3, riseLevels: 1, tiers: 2);
                TryAddDaisDesign(designs, failures, library, style, sunken: false, widthCells: 2, depthCells: 2, riseLevels: 2, tiers: 1);
                TryAddDaisDesign(designs, failures, library, style, sunken: true, widthCells: 2, depthCells: 2, riseLevels: 1, tiers: 1);
                TryAddDaisDesign(designs, failures, library, style, sunken: true, widthCells: 3, depthCells: 2, riseLevels: 1, tiers: 1);
                TryAddDaisDesign(designs, failures, library, style, sunken: true, widthCells: 2, depthCells: 2, riseLevels: 2, tiers: 1);
                // Backed variants (decision 44): wall-flush along the +z side,
                // wide-along-wall throne proportions. Gallery round 1 scrapped
                // the flanking columns and asked for elaborate contoured
                // fronts — the lobed/bay/wings shapes are the first non-rect
                // tiers through the contour walker (decision 39).
                TryAddDaisDesign(designs, failures, library, style, sunken: false, widthCells: 3, depthCells: 2, riseLevels: 1, tiers: 1, backed: true);
                TryAddDaisDesign(designs, failures, library, style, sunken: false, widthCells: 2, depthCells: 1, riseLevels: 1, tiers: 1, backed: true);
                TryAddDaisDesign(designs, failures, library, style, sunken: false, widthCells: 3, depthCells: 2, riseLevels: 2, tiers: 1, backed: true);
                TryAddGoldGrammarBayDesign(designs, failures, library, style);
            }

            // Verbatim reproductions of the two gold-scene backed dais (the
            // user's demoscene level-1 platforms) — the binding references for
            // backed contour grammar. Pieces stay at scene coordinates; the
            // emitter rotates 180 (their fronts face +z in the scene) and
            // translates into the gallery's wall-at-north frame.
            TryAddGoldDaisDesign(designs, failures, library, "dais_gold_backed_bay", GoldBackedBayPieces, originX: -20f, originY: 3f, originZ: -40f, sizeCellsX: 5, sizeCellsZ: 2);
            TryAddGoldDaisDesign(designs, failures, library, "dais_gold_backed_scallop", GoldBackedScallopPieces, originX: 8f, originY: -4f, originZ: -24f, sizeCellsX: 5, sizeCellsZ: 2);

            failureSummary = failures.Count == 0 ? string.Empty : string.Join("; ", failures);
            return designs;
        }

        private static void TryAddDaisDesign(
            List<DaisDesign> designs,
            List<string> failures,
            ForgePieceLibrary library,
            string style,
            bool sunken,
            int widthCells,
            int depthCells,
            int riseLevels,
            int tiers,
            bool backed = false)
        {
            var design = new DaisDesign
            {
                name = backed
                    ? $"dais_backed_{style}_{widthCells}x{depthCells}_r{riseLevels}"
                    : $"dais_{(sunken ? "sunken" : "raised")}_{style}_{widthCells}x{depthCells}_r{riseLevels}" + (tiers > 1 ? $"_t{tiers}" : string.Empty),
                sizeCellsX = widthCells,
                sizeCellsZ = depthCells,
                sunken = sunken,
                backed = backed,
            };
            try
            {
                if (sunken)
                {
                    EmitDaisTier(design, library, style, new RectInt(0, 0, widthCells, depthCells), topUnits: 0f, riseLevels: riseLevels, sunken: true);
                }
                else
                {
                    var rect = new RectInt(0, 0, widthCells, depthCells);
                    float top = 0f;
                    for (int tier = 0; tier < tiers; tier++)
                    {
                        top += riseLevels * LevelHeight;
                        EmitDaisTier(design, library, style, rect, top, riseLevels, sunken: false, backedNorth: backed);
                        if (tier + 1 < tiers)
                        {
                            // Concentric shrink; a tier that cannot shrink ends
                            // the stack (1x1 top tiers are legal).
                            if (rect.width <= 2 && rect.height <= 2)
                            {
                                break;
                            }

                            rect = new RectInt(
                                rect.xMin + 1,
                                rect.yMin + 1,
                                Mathf.Max(1, rect.width - 2),
                                Mathf.Max(1, rect.height - 2));
                        }
                    }

                }

                designs.Add(design);
            }
            catch (InvalidOperationException error)
            {
                failures.Add($"{design.name}: {error.Message}");
            }
        }

        // ===== Gold-grammar synthesized backed contours (gallery round 3).
        // The round-2 walker put lobes at OUTER (1-mass) vertices where the
        // disc only point-touches the platform; the gold bay shows lobes
        // only ever wrap a half-cell pad at JUNCTION vertices, fusing with
        // the mass along a full edge, while protrusion lips stay bare. These
        // emitters rebuild the bay/wings designs from the gold shoulder
        // ensemble at its exact relative offsets.
        //
        // Shoulder ensemble beside a protrusion edge (gold local frame,
        // mass front line z=mf, protrusion side line x=edgeX, side=+1 when
        // the shoulder lies east of the edge): a straight_tiny pad hugs the
        // edge in the ring row's inner half; the lobe disc continues the
        // pad outboard with the _5 sweep wrapping it; the angle concave
        // return steps down the pad's outer half toward open ground.
        private static void EmitGoldShoulder(
            DaisDesign design,
            ForgePieceLibrary library,
            string style,
            float edgeX,
            int side,
            float mf,
            float topUnits,
            string name)
        {
            ForgePiece pad = RequireDaisPiece(library, "P_MOD_Floor_01_O_straight_tiny");
            ForgePiece lobe = RequireDaisPiece(library, style == "angle" ? "P_MOD_Stairs_01_E_angle_convex_5" : "P_MOD_Stairs_01_E_convex_5");
            ForgePiece disc = RequireDaisPiece(library, style == "angle" ? "P_MOD_Floor_01_O_angle_tiny" : "P_MOD_Floor_01_O_convex_tiny");
            ForgePiece ret = RequireDaisPiece(library, "P_MOD_Stairs_01_E_angle_concave_4");
            ForgePiece retCap = RequireDaisPiece(library, "P_MOD_Floor_01_O_angle_tiny");

            float padX = side > 0 ? edgeX : edgeX - 2f;
            float lobeX = side > 0 ? edgeX + 2f : edgeX - 2f;
            float lobeYaw = side > 0 ? 180f : 270f;
            var up = Vector3.up * topUnits;
            design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                pad.path, $"{name}_pad", new Vector3(padX, 0f, mf) + up, 180f));
            design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                disc.path, $"{name}_disc", new Vector3(lobeX, 0f, mf) + up, lobeYaw));
            design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                lobe.path, $"{name}_lobe", new Vector3(lobeX, 0f, mf) + up, lobeYaw));
            design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                ret.path, $"{name}_return", new Vector3(edgeX, 0f, mf - 2f) + up, lobeYaw));
            design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                retCap.path, $"{name}_return_cap", new Vector3(edgeX, 0f, mf - 2f) + up, lobeYaw));
        }

        // Bay: the gold platform 1 plan parametrically (3-cell mass on the
        // wall, 1-cell center bay, flank entry strips along the wall,
        // shoulders hugging both bay sides). Decision 45: the bay NOSE is
        // part of the closed band — front strip + the approved _4 corner
        // notches — never a bare lip (the gold scene dressed its lip with
        // furniture we no longer place). The wings design is WITHDRAWN
        // until a silhouette passes review through the band invariants.
        private static void TryAddGoldGrammarBayDesign(
            List<DaisDesign> designs,
            List<string> failures,
            ForgePieceLibrary library,
            string style)
        {
            const int massWidth = 3;
            var design = new DaisDesign
            {
                name = $"dais_backed_{style}_bay_r1",
                sizeCellsX = massWidth + 2,
                sizeCellsZ = 2,
                backed = true,
                cells = new HashSet<Vector2Int>(),
            };
            try
            {
                ForgePiece floor = RequireDaisPiece(library, "P_MOD_Floor_01_O_straight_med");
                ForgePiece strip = RequireDaisPiece(library, "P_MOD_Stairs_01_E_straight_4");
                ForgePiece notch = RequireDaisPiece(library, style == "angle" ? "P_MOD_Stairs_01_E_angle_convex_4" : "P_MOD_Stairs_01_E_convex_4");
                float top = LevelHeight;

                for (int cx = 1; cx <= massWidth; cx++)
                {
                    design.cells.Add(new Vector2Int(cx, 1));
                }

                design.cells.Add(new Vector2Int(2, 0));
                foreach (Vector2Int cell in design.cells.OrderBy(c => c.y).ThenBy(c => c.x))
                {
                    design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                        floor.path, $"dais_floor_{cell.x}_{cell.y}", DaisFullCellPivot(cell.x, cell.y, 0f) + Vector3.up * top, 0f));
                }

                // Flank entry strips along the wall, climbing onto the mass
                // ends (the gold access pattern).
                EmitDaisStrip(design, strip, 1, 1, -1, 0, faceTop: top, climbOutward: false);
                EmitDaisStrip(design, strip, massWidth, 1, 1, 0, faceTop: top, climbOutward: false);

                EmitGoldShoulder(design, library, style, edgeX: 2 * CellSize, side: -1, mf: CellSize, topUnits: top, name: "dais_shoulder_w");
                EmitGoldShoulder(design, library, style, edgeX: 3 * CellSize, side: 1, mf: CellSize, topUnits: top, name: "dais_shoulder_e");

                // Nose band: strip across the bay front, _4 notches turning
                // the band around the nose corners into the shoulder returns.
                EmitDaisStrip(design, strip, 2, 0, 0, -1, faceTop: top, climbOutward: false);
                foreach ((int sx, float yaw, Vector3 pivot) in new[]
                         {
                             (-1, 270f, DaisFullCellPivot(1, -1, 270f)),
                             (1, 180f, DaisFullCellPivot(3, -1, 180f))
                         })
                {
                    design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                        notch.path, $"dais_nose_corner_{(sx < 0 ? "w" : "e")}", pivot + Vector3.up * top, yaw));
                }

                designs.Add(design);
            }
            catch (InvalidOperationException error)
            {
                failures.Add($"{design.name}: {error.Message}");
            }
        }

        // ===== Gold-scene backed dais reproductions (decision 44, gallery
        // round 2). Source: demoscene_dungeon_level_1_dungeon.unity, the
        // user's two hand-built backed platforms. Coordinates are VERBATIM
        // scene world transforms (parsed via /tmp/scene_dig); do not "fix"
        // them — they are the calibration truth for contour corner kits.
        //
        // Platform 1 "backed bay" (wall at scene z=-48, top y=-2, ground
        // y=-3): 3-cell platform, full-cell center bay, half-cell tiny-pad
        // shoulders, E_convex_5+convex_tiny lobes flanking the shoulders,
        // E_angle_concave_4+angle_tiny returns where the shoulders step
        // back (ANGLE family even beside round lobes), flank entry strips
        // along the wall guarded by sloped stair railings + ground posts,
        // WallTrim_L/R_4 where the strips die into the wall, an LVL convex
        // FILL half chunk at the bay's east foot. Gallery round 3 (user):
        // the four large_2 columns (platform + ground) removed.
        private static readonly (string name, float x, float y, float z, float yaw)[] GoldBackedBayPieces =
        {
            ("P_MOD_Stairs_01_E_straight_4", -36f, -2f, -48f, 0f),
            ("P_MOD_Floor_01_O_straight_med", -28f, -2f, -48f, 90f),
            ("P_MOD_Floor_01_O_straight_med", -28f, -2f, -48f, 0f),
            ("P_MOD_Stairs_01_WallTrim_L_4", -36f, -2f, -47.6f, 0f),
            ("P_MOD_Stairs_01_WallTrim_R_4", -24f, -2f, -47.59f, 180f),
            ("P_MOD_Floor_01_O_straight_med", -36f, -2f, -44f, 180f),
            ("P_MOD_Railing_01_column", -36f, -2f, -44f, 0f),
            ("P_MOD_Stairs_01_Railing_4", -36f, -2f, -44f, 0f),
            ("P_MOD_Floor_01_O_convex_tiny", -34f, -2f, -44f, 0f),
            ("P_MOD_Stairs_01_E_convex_5", -34f, -2f, -44f, 0f),
            ("P_MOD_Floor_01_O_straight_tiny", -32f, -2f, -44f, 0f),
            ("P_MOD_Floor_01_O_straight_med", -28f, -2f, -44f, 0f),
            ("P_MOD_Floor_01_O_convex_tiny", -26f, -2f, -44f, 90f),
            ("P_MOD_Floor_01_O_straight_tiny", -26f, -2f, -44f, 0f),
            ("P_MOD_Stairs_01_E_convex_5", -26f, -2f, -44f, 90f),
            ("P_MOD_Railing_01_column", -24f, -2f, -44f, 180f),
            ("P_MOD_Stairs_01_E_straight_4", -24f, -2f, -44f, 180f),
            ("P_MOD_Stairs_01_Railing_4", -24f, -2f, -44f, 180f),
            ("P_MOD_Floor_01_O_angle_tiny", -32f, -2f, -42f, 0f),
            ("P_MOD_Stairs_01_E_angle_concave_4", -32f, -2f, -42f, 0f),
            ("P_MOD_Floor_01_O_angle_tiny", -28f, -2f, -42f, 90f),
            ("P_MOD_Stairs_01_E_angle_concave_4", -28f, -2f, -42f, 90f),
            ("P_MOD_Railing_01_column", -38f, -3f, -44f, 0f),
            ("P_MOD_Railing_01_column", -22f, -3f, -44f, 180f),
            // Band completion (gallery round 5, not scene pieces): the bay
            // lip was dressed by ground columns + an LVL convex FILL chunk
            // in the scene; once the columns went, it read as a chopped
            // nose. Same closure as the synthesized bay — front strip +
            // round _4 notches into the returns. The LVL chunk (the scene's
            // own east-foot corner dressing) is dropped: it collides with
            // the notch that replaces its role.
            ("P_MOD_Stairs_01_E_straight_4", -32f, -2f, -40f, 90f),
            ("P_MOD_Stairs_01_E_convex_4", -28f, -2f, -40f, 90f),
            ("P_MOD_Stairs_01_E_convex_4", -32f, -2f, -40f, 0f),
        };

        // Platform 2 "backed scallop" (wall at scene z=-32, ledge top y=5,
        // floor y=4): a 1-cell-deep ledge against the wall — two
        // side-by-side E_concave_5 + O_concave_med scallops with half-floor
        // pads between, E_convex_4 wall-corner notches at both ends (west
        // is scene-authored; east mirrors it), and a NOSE protruding 8u
        // from the ledge front: a one-cell-wide strip-flanked neck tipped
        // by the half-disc fan (which overhangs the neck 2u each side).
        // The fan is the scene's LVL_01_O_stairs_convex_FILL_5_2_half
        // chunk DECOMPOSED into its prefab children (two E_convex_5
        // quarters + the convex_med_2_half cap co-located at the fan
        // pivot) so the raster and band invariants can see it. Shape per
        // user reference `_claude_step_example_gold` + round-8 direction:
        // nose extended one cell, ends notched symmetric.
        private static readonly (string name, float x, float y, float z, float yaw)[] GoldBackedScallopPieces =
        {
            // Ledge (reference verbatim + mirrored east notch).
            ("P_MOD_Stairs_01_E_convex_4", -8f, 5f, -32f, 0f),
            ("P_MOD_Floor_01_O_concave_med", -4f, 5f, -32f, 0f),
            ("P_MOD_Floor_01_O_straight_small", -4f, 5f, -32f, 90f),
            ("P_MOD_Stairs_01_E_concave_5", -4f, 5f, -32f, 0f),
            ("P_MOD_Floor_01_O_straight_small", -2f, 5f, -32f, 90f),
            ("P_MOD_Floor_01_O_concave_med", 0f, 5f, -32f, 90f),
            ("P_MOD_Stairs_01_E_concave_5", 0f, 5f, -32f, 90f),
            ("P_MOD_Stairs_01_E_convex_4", 4f, 5f, -32f, 90f),
            // Nose neck (one cell deep, ONE CELL WIDE — matches the pad
            // bay; gallery round 9: the 8u version was too wide and its
            // corner notches unneeded — the fan arcs and flank strips
            // terminate each other).
            ("P_MOD_Floor_01_O_straight_med", 0f, 5f, -28f, 0f),
            ("P_MOD_Stairs_01_E_straight_4", -4f, 5f, -28f, 0f),
            ("P_MOD_Stairs_01_E_straight_4", 0f, 5f, -24f, 180f),
            // Fan tip (the half-disc chunk, decomposed, pivot at x=-2).
            ("P_MOD_Stairs_01_E_convex_5", -2f, 5f, -24f, 0f),
            ("P_MOD_Stairs_01_E_convex_5", -2f, 5f, -24f, 90f),
            ("P_MOD_Floor_01_O_convex_med_2_half", 0f, 5f, -24f, 0f),
        };


        private static void TryAddGoldDaisDesign(
            List<DaisDesign> designs,
            List<string> failures,
            ForgePieceLibrary library,
            string designName,
            (string name, float x, float y, float z, float yaw)[] scenePieces,
            float originX,
            float originY,
            float originZ,
            int sizeCellsX,
            int sizeCellsZ)
        {
            var design = new DaisDesign
            {
                name = designName,
                sizeCellsX = sizeCellsX,
                sizeCellsZ = sizeCellsZ,
                backed = true,
                gold = true,
                cells = new HashSet<Vector2Int>(),
            };
            try
            {
                int index = 0;
                foreach ((string name, float x, float y, float z, float yaw) in scenePieces)
                {
                    string path = RequireDaisPiece(library, name).path;
                    design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                        path,
                        $"gold_{index++}_{name}",
                        new Vector3(-x + originX, y + originY, -z + originZ),
                        Mathf.Repeat(yaw + 180f, 360f)));
                }

                designs.Add(design);
            }
            catch (InvalidOperationException error)
            {
                failures.Add($"{design.name}: {error.Message}");
            }
        }

        // Walks a rect tier's boundary and emits the contour dressing. For a
        // raised tier the mass is INSIDE the rect (convex corners, strips
        // climb inward from the surrounding ring); for a sunken tier the mass
        // is OUTSIDE (concave corners, strips descend inward from the rim).
        // backedNorth (decision 44): the +z side is flush against a room
        // wall — no strips and no corner sweeps on that side; the wall
        // terminates the rim.
        private static void EmitDaisTier(
            DaisDesign design,
            ForgePieceLibrary library,
            string style,
            RectInt rect,
            float topUnits,
            int riseLevels,
            bool sunken,
            bool backedNorth = false)
        {
            // Corner scale matches the strip protrusion (gallery round 3): 1u
            // rims joint with the single quarter-cell _4 notch piece (the
            // twice-approved construction); 2u rims use the full-cell _3
            // sweep, whose strips also run a full cell deep. Raised corners
            // take NO floor cap (gallery round 4: the sweeps fill their whole
            // footprint with steps — caps floated). Sunken keeps the full-cell
            // _5/_3 ON the pit corner cell with the MED cap (approved).
            bool quarterCorner = !sunken && riseLevels == 1;
            string suffix = quarterCorner ? "4" : riseLevels == 1 ? "5" : "3";
            string family = sunken ? "concave" : "convex";
            string cornerName = style == "angle"
                ? $"P_MOD_Stairs_01_E_angle_{family}_{suffix}"
                : $"P_MOD_Stairs_01_E_{family}_{suffix}";
            string capName = style == "angle" ? "P_MOD_Floor_01_O_angle_med" : $"P_MOD_Floor_01_O_{family}_med";
            ForgePiece corner = RequireDaisPiece(library, cornerName);
            ForgePiece cap = RequireDaisPiece(library, capName);
            ForgePiece strip = RequireDaisPiece(library, riseLevels == 1 ? "P_MOD_Stairs_01_E_straight_4" : "P_MOD_Stairs_01_E_straight_3");
            ForgePiece floor = RequireDaisPiece(library, "P_MOD_Floor_01_O_straight_med");

            for (int cz = rect.yMin; cz < rect.yMax; cz++)
            {
                for (int cx = rect.xMin; cx < rect.xMax; cx++)
                {
                    bool westEdge = cx == rect.xMin;
                    bool eastEdge = cx == rect.xMax - 1;
                    bool southEdge = cz == rect.yMin;
                    bool northEdge = cz == rect.yMax - 1;
                    int outwardX = westEdge ? -1 : eastEdge ? 1 : 0;
                    int outwardZ = southEdge ? -1 : northEdge ? 1 : 0;
                    bool isCorner = outwardX != 0 && outwardZ != 0;
                    if (sunken)
                    {
                        if (isCorner)
                        {
                            // Concave piece ON the pit corner cell (the lower
                            // side of the edge), med cap at surrounding level.
                            float yaw = Mathf.Repeat(
                                (outwardX < 0
                                    ? (outwardZ < 0 ? 0f : 90f)
                                    : (outwardZ < 0 ? 270f : 180f)) + DaisConcaveCornerYawOffset,
                                360f);
                            Vector3 pivot = DaisFullCellPivot(cx, cz, yaw);
                            design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                                corner.path, $"dais_corner_{cx}_{cz}", pivot, yaw));
                            design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                                cap.path, $"dais_cap_{cx}_{cz}", pivot, yaw));
                            continue;
                        }

                        if (outwardX != 0 || outwardZ != 0)
                        {
                            EmitDaisStrip(design, strip, cx, cz, outwardX, outwardZ, faceTop: 0f, climbOutward: true);
                        }

                        continue;
                    }

                    // RAISED: every dais cell keeps its full plain floor; every
                    // rim FACE takes a strip and every rim face PAIR takes a
                    // corner sweep in the diagonal ring cell. Faces enumerate
                    // independently — a 1-cell-wide tier is its own west AND
                    // east edge (gallery round 4: the per-axis outward ternary
                    // dropped half the faces and corners of 1x1 top tiers).
                    design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                        floor.path, $"dais_floor_{cx}_{cz}", DaisFullCellPivot(cx, cz, 0f) + Vector3.up * topUnits, 0f));
                    if (westEdge)
                    {
                        EmitDaisStrip(design, strip, cx, cz, -1, 0, faceTop: topUnits, climbOutward: false);
                    }

                    if (eastEdge)
                    {
                        EmitDaisStrip(design, strip, cx, cz, 1, 0, faceTop: topUnits, climbOutward: false);
                    }

                    if (southEdge)
                    {
                        EmitDaisStrip(design, strip, cx, cz, 0, -1, faceTop: topUnits, climbOutward: false);
                    }

                    if (northEdge && !backedNorth)
                    {
                        EmitDaisStrip(design, strip, cx, cz, 0, 1, faceTop: topUnits, climbOutward: false);
                    }

                    foreach (int sx in new[] { -1, 1 })
                    {
                        foreach (int sz in new[] { -1, 1 })
                        {
                            if (backedNorth && sz > 0)
                            {
                                continue;
                            }

                            if ((sx < 0 ? !westEdge : !eastEdge) || (sz < 0 ? !southEdge : !northEdge))
                            {
                                continue;
                            }

                            // Notch quadrant map; pivot lands on the dais corner
                            // vertex (DaisFullCellPivot of the diagonal cell
                            // equals the vertex for these yaws, and the quarter
                            // piece's 2x2 footprint fills the diagonal's nearest
                            // quadrant).
                            float yaw = sx < 0
                                ? (sz < 0 ? 270f : 0f)
                                : (sz < 0 ? 180f : 90f);
                            Vector3 pivot = DaisFullCellPivot(cx + sx, cz + sz, yaw);
                            design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                                corner.path, $"dais_corner_{cx}_{cz}_{sx}_{sz}", pivot + Vector3.up * topUnits, yaw));
                        }
                    }
                }
            }

            // Sunken pits floor every cell at the pit bottom (the corner cells
            // too — the concave pieces stand on it).
            if (sunken)
            {
                for (int cz = rect.yMin; cz < rect.yMax; cz++)
                {
                    for (int cx = rect.xMin; cx < rect.xMax; cx++)
                    {
                        design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                            floor.path,
                            $"dais_pit_floor_{cx}_{cz}",
                            DaisFullCellPivot(cx, cz, 0f) + Vector3.up * (-riseLevels * LevelHeight),
                            0f));
                    }
                }
            }
        }

        private static void EmitDaisStrip(
            DaisDesign design,
            ForgePiece strip,
            int cx,
            int cz,
            int faceX,
            int faceZ,
            float faceTop,
            bool climbOutward)
        {
            var outward = new Vector2(faceX, faceZ);
            Vector2 climb = climbOutward ? outward : -outward;
            float stripYaw = YawForLocalPlusX(climb);
            var faceCenter = new Vector3(
                (cx + 0.5f) * CellSize + faceX * CellSize * 0.5f,
                faceTop,
                (cz + 0.5f) * CellSize + faceZ * CellSize * 0.5f);
            Vector3 position = faceCenter - RotateYaw(strip.exitSocketLocal, stripYaw);
            design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                strip.path, $"dais_strip_{cx}_{cz}_{faceX}_{faceZ}", position, stripYaw));
        }

        // Pivot position that makes a (-x,+z)-footprint full-cell piece cover
        // cell (cx,cz) at the given cardinal yaw.
        private static Vector3 DaisFullCellPivot(int cx, int cz, float yaw)
        {
            float minX = cx * CellSize;
            float minZ = cz * CellSize;
            int quarter = Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 90f) & 3;
            switch (quarter)
            {
                case 0: return new Vector3(minX + CellSize, 0f, minZ);
                case 1: return new Vector3(minX, 0f, minZ);
                case 2: return new Vector3(minX, 0f, minZ + CellSize);
                default: return new Vector3(minX + CellSize, 0f, minZ + CellSize);
            }
        }

        private static float YawForLocalPlusX(Vector2 worldDirection)
        {
            if (worldDirection.x > 0.5f) return 0f;
            if (worldDirection.x < -0.5f) return 180f;
            return worldDirection.y > 0.5f ? 270f : 90f;
        }

        private static ForgePiece RequireDaisPiece(ForgePieceLibrary library, string name)
        {
            ForgePiece piece = library.pieces.FirstOrDefault(p => p.name == name);
            if (piece == null)
            {
                throw new InvalidOperationException($"measured piece '{name}' missing from the library (re-run metrology)");
            }

            return piece;
        }

        [MenuItem("Tools/Dungeon Lab/Synthesis Review: Build Dais Design Gallery")]
        public static void BuildDaisDesignGallery()
        {
            const string rootName = "DungeonLab_DaisDesignGallery";
            GameObject prior = GameObject.Find(rootName);
            if (prior != null)
            {
                UnityEngine.Object.DestroyImmediate(prior);
            }

            var root = new GameObject(rootName);
            List<DaisDesign> designs = SynthesizeDaisDesigns(out string failureSummary);
            float offsetZ = 0f;
            float offsetX = 0f;
            (bool sunken, bool backed, bool gold) currentRowKey = (false, false, false);
            int rowDepthCells = 1;
            foreach (DaisDesign design in designs)
            {
                if ((design.sunken, design.backed, design.gold) != currentRowKey)
                {
                    currentRowKey = (design.sunken, design.backed, design.gold);
                    offsetZ += (rowDepthCells + 3) * CellSize;
                    offsetX = 0f;
                    rowDepthCells = 1;
                }

                var slot = new GameObject($"design_{design.name}");
                slot.transform.SetParent(root.transform, false);
                slot.transform.localPosition = new Vector3(offsetX, 0f, offsetZ);
                foreach (ElevationEdgeModel.SynthesizedPiecePlacement piece in design.pieces)
                {
                    Instantiate(slot.transform, piece.sourcePrefab, piece.pieceName, piece.localPosition, piece.localYawDegrees, piece.localPitchDegrees);
                }

                // Context: a one-cell ring of plain floors at the surrounding
                // walk level so rims and pits read against a ground plane
                // (gallery only — never part of the design's piece plan).
                // Backed designs trade the back ring row for a straight room
                // wall whose visible face points at the dais.
                ForgePieceLibrary library = ForgePieceLibrary.Load();
                ForgePiece contextFloor = library.pieces.FirstOrDefault(p => p.name == "P_MOD_Floor_01_O_straight_med");
                if (contextFloor != null)
                {
                    for (int cz = -1; cz <= design.sizeCellsZ; cz++)
                    {
                        for (int cx = -1; cx <= design.sizeCellsX; cx++)
                        {
                            bool inside = cx >= 0 && cx < design.sizeCellsX && cz >= 0 && cz < design.sizeCellsZ;
                            bool massCell = design.cells != null
                                ? design.cells.Contains(new Vector2Int(cx, cz))
                                : inside;
                            if (massCell || (design.backed && cz == design.sizeCellsZ))
                            {
                                continue;
                            }

                            Instantiate(
                                slot.transform,
                                contextFloor.path,
                                $"context_floor_{cx}_{cz}",
                                DaisFullCellPivot(cx, cz, 0f),
                                0f);
                        }
                    }
                }

                if (design.backed)
                {
                    // Emitted from the back row's NORTH faces so the one-sided
                    // wall plane faces the dais (gallery round 1: emitting
                    // from the far side left the wall facing backwards).
                    var contextWall = new DaisDesign();
                    for (int cx = -1; cx <= design.sizeCellsX; cx++)
                    {
                        EmitTierWallStack(
                            contextWall,
                            library,
                            "straight",
                            new Vector2Int(cx, design.sizeCellsZ - 1),
                            0,
                            1,
                            top: 8f,
                            $"context_wall_{cx}");
                    }

                    foreach (ElevationEdgeModel.SynthesizedPiecePlacement piece in contextWall.pieces)
                    {
                        Instantiate(slot.transform, piece.sourcePrefab, piece.pieceName, piece.localPosition, piece.localYawDegrees, piece.localPitchDegrees);
                    }
                }

                offsetX += (design.sizeCellsX + 3) * CellSize;
                rowDepthCells = Mathf.Max(rowDepthCells, design.sizeCellsZ);
            }

            Debug.Log(
                $"Dungeon Lab Dais Forge: design gallery rebuilt under '{rootName}' ({designs.Count} designs; raised, sunken, then backed rows per style)." +
                (string.IsNullOrEmpty(failureSummary) ? string.Empty : $" Failures: {failureSummary}"));
        }

        // ===== Tier corner forge (step 9, decision 36) =====
        //
        // Cliff-scale rounded tier corners: a convex tier corner swaps its two
        // straight wall faces for ONE quarter shell stack (the grid-snapped
        // O-family round walls: 4x4 plan, exact 2/4/6u courses), capped with
        // the matching round wall trim, the round floor corner above, and the
        // convex/angle railing arc. Concave inside corners take the concave
        // shell in the notch cell. Gallery-first like the dais: the knobs
        // below are the calibration constants for the first review round —
        // shell curve orientation and one-sided wall facing are not measurable
        // (no side-plane data on shells; walls are face-culled one-sided).
        // Gallery rounds 1-2 (2026-06-12): the convex family flips 180 from
        // the original guess (90 -> 270); the concave family was right at 90
        // all along — round 1's "every corner backwards" was the eight convex
        // designs, and the uniform flip broke the two concave ones.
        private const float TierCornerConvexShellYawOffset = 270f;
        private const float TierCornerConcaveShellYawOffset = 90f;
        private const float TierCornerStraightWallYawOffset = 0f;

        internal static List<DaisDesign> SynthesizeTierCornerDesigns(out string failureSummary)
        {
            var failures = new List<string>();
            var designs = new List<DaisDesign>();
            ForgePieceLibrary library = ForgePieceLibrary.Load();
            foreach (string style in new[] { "angle", "round" })
            {
                foreach (int drop in new[] { 2, 3, 4, 6 })
                {
                    TryAddTierCornerDesign(designs, failures, library, style, drop, concave: false);
                }

                TryAddTierCornerDesign(designs, failures, library, style, drop: 4, concave: true);
            }

            failureSummary = failures.Count == 0 ? string.Empty : string.Join("; ", failures);
            return designs;
        }

        private static void TryAddTierCornerDesign(
            List<DaisDesign> designs,
            List<string> failures,
            ForgePieceLibrary library,
            string style,
            int drop,
            bool concave)
        {
            var design = new DaisDesign
            {
                name = $"tiercorner_{(concave ? "concave" : "convex")}_{style}_d{drop}",
                sizeCellsX = 2,
                sizeCellsZ = 2,
                sunken = concave,
            };
            try
            {
                EmitTierCorner(design, library, style, drop, concave);
                designs.Add(design);
            }
            catch (InvalidOperationException error)
            {
                failures.Add($"{design.name}: {error.Message}");
            }
        }

        private static void EmitTierCorner(DaisDesign design, ForgePieceLibrary library, string style, int drop, bool concave)
        {
            float top = drop;
            string shellFamily = concave ? "concave" : style == "angle" ? "angle" : "convex";
            string trimName = style == "angle"
                ? "P_MOD_WallTrim_01_O_angle"
                : $"P_MOD_WallTrim_01_O_{(concave ? "concave" : "convex")}";
            ForgePiece floor = RequireDaisPiece(library, "P_MOD_Floor_01_O_straight_med");

            // Mass cells: full 2x2 for convex (corner under test at cell (1,1)
            // toward (+x,+z)); an L missing (1,1) for concave (inside corner at
            // the notch).
            var massCells = new List<Vector2Int> { new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(0, 1) };
            if (!concave)
            {
                massCells.Add(new Vector2Int(1, 1));
            }

            foreach (Vector2Int cell in massCells)
            {
                bool isRoundedCorner = !concave && cell == new Vector2Int(1, 1);
                if (isRoundedCorner)
                {
                    // The corner cell's floor gets the rounded variant so the
                    // slab edge follows the shell's sweep. One corner kit, one
                    // yaw: the cap rotates with the shell offset (round 1
                    // flipped the whole kit together; split the knobs only if
                    // a later round shows the families disagree).
                    string capName = style == "angle" ? "P_MOD_Floor_01_O_angle_med" : "P_MOD_Floor_01_O_convex_med";
                    ForgePiece cap = RequireDaisPiece(library, capName);
                    float capYaw = Mathf.Repeat(180f + TierCornerConvexShellYawOffset, 360f);
                    design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                        cap.path, "corner_floor", DaisFullCellPivot(cell.x, cell.y, capYaw) + Vector3.up * top, capYaw));
                    continue;
                }

                design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                    floor.path, $"mass_floor_{cell.x}_{cell.y}", DaisFullCellPivot(cell.x, cell.y, 0f) + Vector3.up * top, 0f));
            }

            // Straight wall faces around the mass perimeter, except the faces
            // the shell replaces.
            foreach (Vector2Int cell in massCells)
            {
                foreach ((int dx, int dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                {
                    var neighbor = new Vector2Int(cell.x + dx, cell.y + dz);
                    if (massCells.Contains(neighbor))
                    {
                        continue;
                    }

                    bool shellFace = concave
                        ? neighbor == new Vector2Int(1, 1)
                        : cell == new Vector2Int(1, 1) && (dx > 0 || dz > 0);
                    if (shellFace)
                    {
                        continue;
                    }

                    EmitTierWallStack(design, library, "straight", cell, dx, dz, top, $"wall_{cell.x}_{cell.y}_{dx}_{dz}");
                }
            }

            // The shell stack: convex on the corner cell itself, concave in
            // the notch cell; both pivot by the full-cell quadrant map with
            // the calibration yaw offset.
            Vector2Int shellCell = new Vector2Int(1, 1);
            float baseYaw = concave ? 0f : 180f;
            float shellYaw = Mathf.Repeat(baseYaw + (concave ? TierCornerConcaveShellYawOffset : TierCornerConvexShellYawOffset), 360f);
            EmitTierShellStack(design, library, shellFamily, shellCell, shellYaw, top);

            // Round trim curb at the shell top and, for convex corners, the
            // railing arc above it (no concave railing exists in the pack).
            ForgePiece trim = RequireDaisPiece(library, trimName);
            Vector3 shellPivot = DaisFullCellPivot(shellCell.x, shellCell.y, shellYaw);
            design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                trim.path, "corner_trim", shellPivot + Vector3.up * top, shellYaw));
            if (!concave)
            {
                string railName = style == "angle" ? "P_MOD_Railing_01_angle" : "P_MOD_Railing_01_convex";
                ForgePiece rail = RequireDaisPiece(library, railName);
                design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                    rail.path, "corner_railing", shellPivot + Vector3.up * top, shellYaw));
            }
        }

        // Top-anchored change-making wall stack on one cell face (gallery
        // context; the edge model owns the real drop-face placement). Straight
        // O walls are zero-thickness planes running local -x at z=0.
        private static void EmitTierWallStack(
            DaisDesign design,
            ForgePieceLibrary library,
            string family,
            Vector2Int cell,
            int dx,
            int dz,
            float top,
            string name)
        {
            float minX = cell.x * CellSize;
            float minZ = cell.y * CellSize;
            float yaw;
            Vector3 pivot;
            if (dx > 0)
            {
                yaw = 90f;
                pivot = new Vector3(minX + CellSize, 0f, minZ);
            }
            else if (dx < 0)
            {
                yaw = 270f;
                pivot = new Vector3(minX, 0f, minZ + CellSize);
            }
            else if (dz > 0)
            {
                yaw = 180f;
                pivot = new Vector3(minX, 0f, minZ + CellSize);
            }
            else
            {
                yaw = 0f;
                pivot = new Vector3(minX + CellSize, 0f, minZ);
            }

            yaw = Mathf.Repeat(yaw + TierCornerStraightWallYawOffset, 360f);
            EmitWallCourses(design, library, family, pivot, yaw, top, name);
        }

        private static void EmitTierShellStack(
            DaisDesign design,
            ForgePieceLibrary library,
            string family,
            Vector2Int cell,
            float yaw,
            float top)
        {
            EmitWallCourses(design, library, family, DaisFullCellPivot(cell.x, cell.y, yaw), yaw, top, "shell");
        }

        private static void EmitWallCourses(
            DaisDesign design,
            ForgePieceLibrary library,
            string family,
            Vector3 pivot,
            float yaw,
            float top,
            string name)
        {
            ForgePiece small = RequireDaisPiece(library, $"P_MOD_Wall_01_O_{family}_small");
            ForgePiece med = RequireDaisPiece(library, $"P_MOD_Wall_01_O_{family}_med");
            ForgePiece large = RequireDaisPiece(library, $"P_MOD_Wall_01_O_{family}_large");
            var courses = new[] { large, med, small };
            float remaining = top;
            float courseTop = top;
            int index = 0;
            while (remaining > 0.01f && index <= 12)
            {
                ForgePiece course = small;
                foreach (ForgePiece candidate in courses)
                {
                    if (candidate.sizeUnits.y <= remaining + 1.01f)
                    {
                        course = candidate;
                        break;
                    }
                }

                design.pieces.Add(new ElevationEdgeModel.SynthesizedPiecePlacement(
                    course.path, $"{name}_c{index}", pivot + Vector3.up * (courseTop - course.sizeUnits.y), yaw));
                courseTop -= course.sizeUnits.y;
                remaining -= course.sizeUnits.y;
                index++;
            }
        }

        [MenuItem("Tools/Dungeon Lab/Synthesis Review: Build Tier Corner Gallery")]
        public static void BuildTierCornerGallery()
        {
            const string rootName = "DungeonLab_TierCornerGallery";
            GameObject prior = GameObject.Find(rootName);
            if (prior != null)
            {
                UnityEngine.Object.DestroyImmediate(prior);
            }

            var root = new GameObject(rootName);
            List<DaisDesign> designs = SynthesizeTierCornerDesigns(out string failureSummary);
            ForgePieceLibrary library = ForgePieceLibrary.Load();
            ForgePiece contextFloor = library.pieces.FirstOrDefault(p => p.name == "P_MOD_Floor_01_O_straight_med");
            float offsetX = 0f;
            float offsetZ = 0f;
            bool currentRowConcave = false;
            foreach (DaisDesign design in designs)
            {
                if (design.sunken != currentRowConcave)
                {
                    currentRowConcave = design.sunken;
                    offsetZ += 6 * CellSize;
                    offsetX = 0f;
                }

                var slot = new GameObject($"design_{design.name}");
                slot.transform.SetParent(root.transform, false);
                slot.transform.localPosition = new Vector3(offsetX, 0f, offsetZ);
                foreach (ElevationEdgeModel.SynthesizedPiecePlacement piece in design.pieces)
                {
                    Instantiate(slot.transform, piece.sourcePrefab, piece.pieceName, piece.localPosition, piece.localYawDegrees, piece.localPitchDegrees);
                }

                if (contextFloor != null)
                {
                    for (int cz = -1; cz <= 2; cz++)
                    {
                        for (int cx = -1; cx <= 2; cx++)
                        {
                            bool insideMass = cx >= 0 && cx < 2 && cz >= 0 && cz < 2 &&
                                !(design.sunken && cx == 1 && cz == 1);
                            if (!insideMass)
                            {
                                Instantiate(
                                    slot.transform,
                                    contextFloor.path,
                                    $"context_floor_{cx}_{cz}",
                                    DaisFullCellPivot(cx, cz, 0f),
                                    0f);
                            }
                        }
                    }
                }

                offsetX += 6 * CellSize;
            }

            Debug.Log(
                $"Dungeon Lab Tier Corner Forge: gallery rebuilt under '{rootName}' ({designs.Count} designs; convex row then concave)." +
                (string.IsNullOrEmpty(failureSummary) ? string.Empty : $" Failures: {failureSummary}"));
        }

        [MenuItem("Tools/Dungeon Lab/Synthesis Review: Mark Selected Reviewed")]
        public static void MarkSelectedSynthesisReviewed()
        {
            GameObject selected = Selection.activeGameObject;
            Transform cursor = selected != null ? selected.transform : null;
            while (cursor != null &&
                (cursor.parent == null || !string.Equals(cursor.parent.name, SynthesisReviewRootName, StringComparison.Ordinal)))
            {
                cursor = cursor.parent;
            }

            if (cursor == null || !File.Exists(SynthesisLogPath))
            {
                string error = cursor == null
                    ? $"Select a staircase slot under '{SynthesisReviewRootName}' first."
                    : $"Missing synthesis log at '{SynthesisLogPath}'.";
                EditorUtility.DisplayDialog("Synthesis Review", error, "OK");
                Debug.LogError($"Dungeon Lab Synthesis Review: {error}");
                return;
            }

            JObject root = JObject.Parse(File.ReadAllText(SynthesisLogPath));
            JObject match = ((root["entries"] as JArray) ?? new JArray())
                .OfType<JObject>()
                .FirstOrDefault(e => string.Equals(SynthesisReviewSlotName(e), cursor.name, StringComparison.Ordinal));
            if (match == null)
            {
                string error = $"No synthesis log entry matched slot '{cursor.name}'.";
                EditorUtility.DisplayDialog("Synthesis Review", error, "OK");
                Debug.LogError($"Dungeon Lab Synthesis Review: {error}");
                return;
            }

            match["reviewStatus"] = "reviewed";
            File.WriteAllText(SynthesisLogPath, root.ToString(Unity.Plastic.Newtonsoft.Json.Formatting.Indented));
            AssetDatabase.ImportAsset(SynthesisLogPath);
            Debug.Log($"Dungeon Lab Synthesis Review: marked '{cursor.name}' reviewed.");
        }

        private static string SynthesisReviewSlotName(JObject entry)
        {
            return $"review_seed{entry.Value<int?>("seed") ?? 0}_{entry.Value<string>("gapId")}_{entry.Value<string>("name")}";
        }

        // ---------------------------------------------------------------------
        // Measured piece library access (dimensions only ever come from here).
        // ---------------------------------------------------------------------

        private sealed class ForgePiece
        {
            public string name;
            public string path;
            public string category;
            public Vector3 boundsMin;
            public Vector3 boundsMax;
            public Vector3 sizeUnits;
            public Vector3 boundsCenter => (boundsMin + boundsMax) * 0.5f;
            public bool widthAxisIsX;

            // Flight-only fields.
            public int riseLevels;
            public float riseUnits;
            public float runUnits;
            public float lateralWidthUnits;
            public float walkSurfaceTopY;
            public Vector3 exitSocketLocal;

            // Curved-flight fields: measured socket directions and turn sign.
            public Vector3 entrySocketLocal;
            public Vector2Int entrySocketDirection;
            public Vector2Int exitSocketDirection;
            public int turnSign;

            // Per-boundary-plane flat side-face areas (bottomCap family). A
            // quarter-round has flat faces on exactly the two adjacent planes
            // meeting at its inner corner; the arc opens at the opposite corner.
            // A straight block is solid on all four planes and stays
            // rotation-free.
            public float sideAreaXMinus, sideAreaXPlus, sideAreaZMinus, sideAreaZPlus;

            public Vector2Int OuterOpenQuadrant()
            {
                float max = Mathf.Max(sideAreaXMinus, sideAreaXPlus, sideAreaZMinus, sideAreaZPlus);
                if (max <= 0.01f)
                {
                    return Vector2Int.zero;
                }

                float solidThreshold = max * 0.4f;
                bool xMinusSolid = sideAreaXMinus >= solidThreshold;
                bool xPlusSolid = sideAreaXPlus >= solidThreshold;
                bool zMinusSolid = sideAreaZMinus >= solidThreshold;
                bool zPlusSolid = sideAreaZPlus >= solidThreshold;
                int solidCount = (xMinusSolid ? 1 : 0) + (xPlusSolid ? 1 : 0) + (zMinusSolid ? 1 : 0) + (zPlusSolid ? 1 : 0);
                if (solidCount != 2 || xMinusSolid == xPlusSolid || zMinusSolid == zPlusSolid)
                {
                    return Vector2Int.zero;
                }

                // The two solid planes meet at the inner corner; the arc opens
                // diagonally opposite.
                return new Vector2Int(xMinusSolid ? 1 : -1, zMinusSolid ? 1 : -1);
            }
        }

        private sealed class ForgePieceLibrary
        {
            public readonly List<ForgePiece> pieces = new List<ForgePiece>();
            public ForgePiece railingColumn;
            public ForgePiece fullRailing;   // P-length straight flat railing (4u)
            public ForgePiece halfRailing;   // half-length flat railing (2u)

            public static ForgePieceLibrary Load()
            {
                if (!File.Exists(StepPieceLibraryPath))
                {
                    throw new InvalidOperationException(
                        $"The forge needs the measured step piece library at '{StepPieceLibraryPath}'. Run Tools > Dungeon Lab > Measure Step Piece Library.");
                }

                var library = new ForgePieceLibrary();
                JObject root = JObject.Parse(File.ReadAllText(StepPieceLibraryPath));
                if (root["pieces"] is JArray pieceArray)
                {
                    foreach (JToken token in pieceArray)
                    {
                        if (!string.Equals(token.Value<string>("confidence"), "high", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var piece = new ForgePiece
                        {
                            name = token.Value<string>("name") ?? string.Empty,
                            path = token.Value<string>("path") ?? string.Empty,
                            category = token.Value<string>("category") ?? string.Empty,
                            boundsMin = ParseVector(token["boundsMin"]),
                            boundsMax = ParseVector(token["boundsMax"]),
                            sizeUnits = ParseVector(token["sizeUnits"]),
                            widthAxisIsX = !string.Equals(token.Value<string>("widthAxis"), "z", StringComparison.Ordinal),
                            riseLevels = token.Value<int?>("riseLevels") ?? 0,
                            riseUnits = token.Value<float?>("riseUnits") ?? 0f,
                            runUnits = token.Value<float?>("runUnits") ?? 0f,
                            lateralWidthUnits = token.Value<float?>("lateralWidthUnits") ?? 0f,
                            walkSurfaceTopY = token.Value<float?>("walkSurfaceTopY") ?? 0f,
                        };

                        piece.turnSign = token.Value<int?>("turnSign") ?? 0;
                        if (token["sidePlaneAreas"] is JObject sideAreas)
                        {
                            piece.sideAreaXMinus = sideAreas.Value<float?>("xMinus") ?? 0f;
                            piece.sideAreaXPlus = sideAreas.Value<float?>("xPlus") ?? 0f;
                            piece.sideAreaZMinus = sideAreas.Value<float?>("zMinus") ?? 0f;
                            piece.sideAreaZPlus = sideAreas.Value<float?>("zPlus") ?? 0f;
                        }
                        if (token["sockets"] is JArray sockets)
                        {
                            foreach (JToken socket in sockets)
                            {
                                if (string.Equals(socket.Value<string>("role"), "exit", StringComparison.Ordinal))
                                {
                                    piece.exitSocketLocal = ParseVector(socket["local"]);
                                    piece.exitSocketDirection = ParseAxisDirection(socket.Value<string>("direction"));
                                }
                                else if (string.Equals(socket.Value<string>("role"), "entry", StringComparison.Ordinal))
                                {
                                    piece.entrySocketLocal = ParseVector(socket["local"]);
                                    piece.entrySocketDirection = ParseAxisDirection(socket.Value<string>("direction"));
                                }
                            }
                        }

                        library.pieces.Add(piece);
                    }
                }

                library.pieces.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                library.railingColumn = library.pieces.FirstOrDefault(p => p.category == "railingColumn");
                List<ForgePiece> flatRailings = library.pieces
                    .Where(p => p.category == "railing" && p.sizeUnits.y < 2.5f && Mathf.Min(p.sizeUnits.x, p.sizeUnits.z) < 1.5f)
                    .ToList();
                library.fullRailing = flatRailings.FirstOrDefault(p => Mathf.Abs(Mathf.Max(p.sizeUnits.x, p.sizeUnits.z) - CellSize) <= 0.3f);
                library.halfRailing = flatRailings.FirstOrDefault(p => Mathf.Abs(Mathf.Max(p.sizeUnits.x, p.sizeUnits.z) - HalfCell) <= 0.3f);
                return library;
            }

            public string DescribeMissingCategories()
            {
                var missing = new List<string>();
                if (!pieces.Any(p => p.category == "stairFlight" && Mathf.Abs(p.lateralWidthUnits - CellSize) <= 0.3f))
                {
                    missing.Add("full-width stairFlight pieces");
                }

                if (FindFloor(CellSize, CellSize) == null)
                {
                    missing.Add("a full-cell floor (4u x 4u, category \"floor\")");
                }

                if (FindFloor(HalfCell, CellSize) == null)
                {
                    missing.Add("a half floor (2u x 4u, category \"floor\")");
                }

                if (!pieces.Any(p => p.category == "stairSideCover"))
                {
                    missing.Add("the stairSideCover family");
                }

                if (!pieces.Any(p => p.category == "stairBotCap"))
                {
                    missing.Add("the stairBotCap family");
                }

                return string.Join(", ", missing);
            }

            public List<int> FlightRises()
            {
                return pieces
                    .Where(p => p.category == "stairFlight" && p.riseLevels > 0 && Mathf.Abs(p.lateralWidthUnits - CellSize) <= 0.3f)
                    .Select(p => p.riseLevels)
                    .Distinct()
                    .OrderBy(r => r)
                    .ToList();
            }

            public ForgePiece FindFlight(int riseLevels)
            {
                return pieces.FirstOrDefault(p =>
                    p.category == "stairFlight" &&
                    p.riseLevels == riseLevels &&
                    Mathf.Abs(p.lateralWidthUnits - CellSize) <= 0.3f);
            }

            // The piece that replaces TWO of the given flight in a row: exactly
            // twice the level rise at the same measured slope (within 10%), e.g.
            // E_straight_3 (2.02u over 4.12u) for two E_straight_4 strips (1.02u
            // over 2.09u). The steep families have no same-angle double, so this
            // is null for them and runs stay piecewise.
            // Quarter-turn flight of the staircase's exact steepness and the
            // requested chirality (+1 right, -1 left), with measured sockets.
            public ForgePiece FindCurvedFlight(int riseLevels, int turnSign)
            {
                return pieces.FirstOrDefault(p =>
                    p.category == "stairCurvedFlight" &&
                    p.riseLevels == riseLevels &&
                    p.turnSign == turnSign &&
                    p.entrySocketDirection != Vector2Int.zero &&
                    p.exitSocketDirection != Vector2Int.zero);
            }

            // Curved dressing co-locates with its flight (the hand-authored
            // curved stairs place flight + arc railing + curved wall at one
            // transform). Chirality rides the name family token (selection only);
            // plan size is validated from measurement.
            public ForgePiece FindCurvedDressing(ForgePiece curve, string category)
            {
                string token = ChiralityToken(curve.name);
                if (token == null)
                {
                    return null;
                }

                return pieces
                    .Where(p => p.category == category &&
                        p.name.Contains(token) &&
                        Mathf.Abs(p.sizeUnits.x - curve.sizeUnits.x) <= 0.6f &&
                        Mathf.Abs(p.sizeUnits.z - curve.sizeUnits.z) <= 0.6f)
                    .OrderBy(p => p.name, StringComparer.Ordinal)
                    .FirstOrDefault();
            }

            public ForgePiece FindCurvedRailing(ForgePiece curve)
            {
                string token = ChiralityToken(curve.name);
                if (token == null)
                {
                    return null;
                }

                return pieces
                    .Where(p => p.category == "stairRailing" &&
                        p.name.Contains(token) &&
                        Mathf.Abs(p.sizeUnits.x - curve.sizeUnits.x) <= 0.6f &&
                        Mathf.Abs(p.sizeUnits.z - curve.sizeUnits.z) <= 0.6f)
                    .OrderBy(p => p.name, StringComparer.Ordinal)
                    .FirstOrDefault();
            }

            private static string ChiralityToken(string name)
            {
                if (name.Contains("_NW"))
                {
                    return "_NW";
                }

                return name.Contains("_SW") ? "_SW" : null;
            }

            public ForgePiece FindSameAngleDoubledFlight(ForgePiece unit)
            {
                if (unit.runUnits <= 0f)
                {
                    return null;
                }

                float unitSlope = unit.riseUnits / unit.runUnits;
                return pieces.FirstOrDefault(p =>
                    p.category == "stairFlight" &&
                    p.riseLevels == unit.riseLevels * 2 &&
                    Mathf.Abs(p.lateralWidthUnits - unit.lateralWidthUnits) <= 0.3f &&
                    p.runUnits > 0f &&
                    Mathf.Abs(p.riseUnits / p.runUnits - unitSlope) <= unitSlope * 0.1f);
            }

            public ForgePiece FindStairRailing(ForgePiece flight)
            {
                // Pairing is measured: the railing whose length matches the flight's
                // grid run, closest in height to the flight's rise plus the pack's
                // ~1.5u guard profile.
                float gridRun = HalfCellsPerFlight(flight.riseLevels) * HalfCell;
                return pieces
                    .Where(p => p.category == "stairRailing" && Mathf.Abs(Mathf.Max(p.sizeUnits.x, p.sizeUnits.z) - gridRun) <= 0.3f)
                    .OrderBy(p => Mathf.Abs(p.sizeUnits.y - (flight.riseUnits + 1.5f)))
                    .ThenBy(p => p.name, StringComparer.Ordinal)
                    .FirstOrDefault();
            }

            public ForgePiece FindBySizeMatch(string category, float sizeX, float toleranceX, float sizeZ, float toleranceZ, float sizeY, float toleranceY)
            {
                // Height participates in the match: the cover/cap families share
                // one run and width per family and differ ONLY in elevation
                // (WallCover_1..4 measure 4.0x4/3/2/1u x 4.0), so a run+width
                // match alone pairs every flight with the same piece.
                return pieces
                    .Where(p => p.category == category &&
                        Mathf.Abs(p.sizeUnits.x - sizeX) <= toleranceX &&
                        Mathf.Abs(p.sizeUnits.z - sizeZ) <= toleranceZ &&
                        Mathf.Abs(p.sizeUnits.y - sizeY) <= toleranceY)
                    .OrderBy(p => Mathf.Abs(p.sizeUnits.y - sizeY))
                    .ThenBy(p => Mathf.Abs(p.sizeUnits.x - sizeX))
                    .ThenBy(p => p.name, StringComparer.Ordinal)
                    .FirstOrDefault();
            }

            public ForgePiece FindFloor(float runSize, float wideSize)
            {
                // Floors are slabs; orientation is free, so match the size pair in
                // either order. The _hole variants exist for trapdoors — selection
                // by name family keeps them out (selection, not measurement).
                return pieces
                    .Where(p => p.category == "floor" &&
                        !p.name.EndsWith("_hole", StringComparison.Ordinal) &&
                        p.sizeUnits.y <= 1.0f &&
                        SizePairMatches(p, runSize, wideSize))
                    .OrderBy(p => p.name, StringComparer.Ordinal)
                    .FirstOrDefault();
            }

            // Quarter-round masonry denominations for under-curve fill, sorted by
            // height like the straight bases.
            public List<ForgePiece> ConvexBases()
            {
                return pieces
                    .Where(p => p.category == "bottomCap" &&
                        p.name.Contains("convex") &&
                        !p.name.Contains("hole") &&
                        Mathf.Abs(p.sizeUnits.x - CellSize) <= 0.3f &&
                        Mathf.Abs(p.sizeUnits.z - CellSize) <= 0.3f)
                    .OrderBy(p => p.sizeUnits.y)
                    .ThenBy(p => p.name, StringComparer.Ordinal)
                    .ToList();
            }

            public List<ForgePiece> StraightBases(float runSize, float wideSize)
            {
                // Solid masonry denominations for under-fill, sorted by height. The
                // 2u-plan variants serve half-cell fills (paired up laterally).
                bool narrow = runSize <= HalfCell + 0.3f;
                float plan = narrow ? HalfCell : CellSize;
                return pieces
                    .Where(p => p.category == "bottomCap" &&
                        !p.name.Contains("hole") &&
                        Mathf.Abs(p.sizeUnits.x - plan) <= 0.3f &&
                        Mathf.Abs(p.sizeUnits.z - plan) <= 0.3f &&
                        p.name.Contains("straight"))
                    .OrderBy(p => p.sizeUnits.y)
                    .ThenBy(p => p.name, StringComparer.Ordinal)
                    .ToList();
            }

            private static bool SizePairMatches(ForgePiece piece, float a, float b)
            {
                return (Mathf.Abs(piece.sizeUnits.x - a) <= 0.3f && Mathf.Abs(piece.sizeUnits.z - b) <= 0.3f) ||
                    (Mathf.Abs(piece.sizeUnits.x - b) <= 0.3f && Mathf.Abs(piece.sizeUnits.z - a) <= 0.3f);
            }

            private static Vector3 ParseVector(JToken token)
            {
                return token == null
                    ? Vector3.zero
                    : new Vector3(token.Value<float?>("x") ?? 0f, token.Value<float?>("y") ?? 0f, token.Value<float?>("z") ?? 0f);
            }

            private static Vector2Int ParseAxisDirection(string axis)
            {
                switch (axis)
                {
                    case "x+": return Vector2Int.right;
                    case "x-": return Vector2Int.left;
                    case "z+": return Vector2Int.up;
                    case "z-": return Vector2Int.down;
                    default: return Vector2Int.zero;
                }
            }
        }

        private enum ForgeSideStyle { Walled, Bridge }

        private readonly struct ForgeRequest
        {
            public readonly int rise;
            public readonly int lanes;
            public readonly ForgeSideStyle style;
            public readonly bool forceTurn;
            // Aerial deck (step 8): a rise-0 flat span. Railings place at the
            // contract base (the whole deck is elevated at placement), with end
            // posts per railing piece. The 4-arg ctor stays as-is: the smoke
            // harness constructs requests through it by reflection.
            public readonly bool deck;
            // Decision 32 follow-up: per-cell railing suppression where the deck
            // runs even with adjacent floor (bit i = contract cell x == i; plus =
            // local +z side, minus = local -z side).
            public readonly ulong deckRailPlusMask;
            public readonly ulong deckRailMinusMask;

            public ForgeRequest(int rise, int lanes, ForgeSideStyle style, bool forceTurn = false)
                : this(rise, lanes, style, forceTurn, deck: false)
            {
            }

            public ForgeRequest(int rise, int lanes, ForgeSideStyle style, bool forceTurn, bool deck)
                : this(rise, lanes, style, forceTurn, deck, 0, 0)
            {
            }

            public ForgeRequest(int rise, int lanes, ForgeSideStyle style, bool forceTurn, bool deck, ulong deckRailPlusMask, ulong deckRailMinusMask)
            {
                this.rise = rise;
                this.lanes = lanes;
                this.style = style;
                this.forceTurn = forceTurn;
                this.deck = deck;
                this.deckRailPlusMask = deckRailPlusMask;
                this.deckRailMinusMask = deckRailMinusMask;
            }

            public string Identity()
            {
                return $"r{rise}_l{lanes}_{(style == ForgeSideStyle.Bridge ? "bridge" : "walled")}{(forceTurn ? "_turn" : string.Empty)}{(deck ? "_deck" : string.Empty)}";
            }
        }
    }
}

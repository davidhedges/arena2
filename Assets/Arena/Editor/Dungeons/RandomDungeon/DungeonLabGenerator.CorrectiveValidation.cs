using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // Focused optional-crossing evidence only. Production generation continues
    // to use AddAerialBridges; this fixture supplies deterministic eligible
    // geometry to that existing method and inspects its normal renderer/export.
    internal sealed partial class DungeonLabGenerator
    {
        private const string CorrectiveStackedCrossingReportVersion =
            "corrective-stacked-crossing-v1";

        private sealed class CorrectiveBatchEvidence
        {
            public readonly int runOrdinal;
            public readonly List<double> planningMilliseconds = new List<double>();
            public double measuredLoopSeconds;

            public CorrectiveBatchEvidence(int runOrdinal)
            {
                this.runOrdinal = runOrdinal;
            }
        }

        private sealed class CorrectiveStackedFixture
        {
            public Dictionary<Vector2Int, int> levels;
            public List<ElevationEdgeModel.TransitionEdge> transitions;
            public Dictionary<Vector2Int, int> spanDeckLevels;
            public Vector2Int stackedCell;
            public Vector2Int lowerStart;
            public Vector2Int lowerEnd;
            public ElevationEdgeModel.TransitionEdge bridge;
            public GameObject root;
            public ElevationEdgeModel.BuildReport buildReport;
            public bool lowerRouteTraversable;
            public bool upperBridgeTraversable;
            public bool positiveHeadroomPassed;
            public bool negativeHeadroomRejected;
            public bool lowerClearanceOpen;
            public List<string> lowerSurfaceColliderNames = new List<string>();
            public List<string> upperSurfaceColliderNames = new List<string>();
        }

        [MenuItem("Tools/Dungeon Lab/Corrective/Validate Two 200-Seed Runs")]
        public static void ValidateCorrectiveTwoHundredSeedRuns()
        {
            string baselinePath = Path.Combine(
                BatchReportDirectory,
                $"dungeon_plan_{Phase0BaselineFirstSeed}_{Phase0BaselineFirstSeed + Phase0BaselineSeedCount - 1}.json");
            if (!File.Exists(baselinePath))
            {
                throw new FileNotFoundException(
                    "The established v8 200-seed report is required as the pre-corrective structural baseline.",
                    baselinePath);
            }

            JObject baseline = JObject.Parse(File.ReadAllText(baselinePath));
            if (!string.Equals(
                    baseline.Value<string>("summaryVersion"),
                    "dungeon-plan-v8",
                    StringComparison.Ordinal) ||
                baseline.Value<int?>("seedCount") != Phase0BaselineSeedCount)
            {
                throw new InvalidOperationException(
                    "The pre-corrective report is not the established v8 200-seed baseline.");
            }

            var environmentEvidence = new Phase7BatchEvidence(0, Phase7WarmupSeedCount);
            JObject measurementEnvironment = BuildPhase7MeasurementEnvironment(environmentEvidence);
            if (measurementEnvironment.Value<bool?>("passed") != true)
            {
                throw new InvalidOperationException(
                    "Corrective performance validation requires the locked batch measurement environment.");
            }
            WarmPhase7MeasurementProcess();

            string run1Path = RunBatchValidation(
                Phase0BaselineFirstSeed,
                Phase0BaselineSeedCount,
                correctiveRunOrdinal: 1);
            string run2Path = RunBatchValidation(
                Phase0BaselineFirstSeed,
                Phase0BaselineSeedCount,
                correctiveRunOrdinal: 2);
            JObject run1 = JObject.Parse(File.ReadAllText(run1Path));
            JObject run2 = JObject.Parse(File.ReadAllText(run2Path));
            JObject comparison = BuildCorrectiveBatchComparison(baseline, run1, run2);
            comparison["measurementEnvironment"] = measurementEnvironment;
            string comparisonPath = Path.Combine(
                BatchReportDirectory,
                "corrective_connections_200_seed_comparison.json");
            File.WriteAllText(comparisonPath, comparison.ToString(Formatting.Indented));
            if (comparison.Value<bool?>("passed") != true)
            {
                throw new InvalidOperationException(
                    $"Corrective 200-seed validation failed. Inspect '{comparisonPath}'.");
            }

            Debug.Log($"Dungeon Lab corrective two-run 200-seed validation passed ({comparisonPath}).");
        }

        private static void AppendCorrectiveBatchEvidence(
            JObject report,
            JArray seedReports,
            IReadOnlyDictionary<string, int> selectedPatternCounts,
            JObject attemptDistribution,
            int successCount,
            int hardValidCount,
            CorrectiveBatchEvidence evidence)
        {
            int externalValidSeeds = 0;
            int desiredCountRealizedSeeds = 0;
            int uniqueDirectionSeeds = 0;
            int namedPromontories = 0;
            int processionalPromontories = 0;
            int atriumPromontories = 0;
            int twinWingPromontories = 0;
            var externalCounts = new SortedDictionary<int, int>();
            foreach (JToken seedReport in seedReports)
            {
                if (seedReport.Value<bool?>("accepted") != true)
                    continue;
                int seed = seedReport.Value<int>("seed");
                JArray external = seedReport["externalConnectors"] as JArray ?? new JArray();
                externalCounts.TryGetValue(external.Count, out int externalCount);
                externalCounts[external.Count] = externalCount + 1;
                if (seedReport["validation"]?["externalConnectors"]?.Value<bool?>("passed") == true)
                    externalValidSeeds++;
                if (external.Count == ExternalConnectorDesiredCount(seed))
                    desiredCountRealizedSeeds++;
                if (external.Select(token => token.Value<int>("directionId")).Distinct().Count() == external.Count)
                    uniqueDirectionSeeds++;

                JArray named = seedReport["namedPromontories"] as JArray ?? new JArray();
                namedPromontories += named.Count;
                string pattern = seedReport["routeIntent"]?.Value<string>("patternId") ?? string.Empty;
                if (string.Equals(pattern, Phase1PatternId, StringComparison.Ordinal))
                    processionalPromontories += named.Count;
                else if (string.Equals(pattern, AtriumRingPatternId, StringComparison.Ordinal))
                    atriumPromontories += named.Count;
                else if (string.Equals(pattern, TwinWingPatternId, StringComparison.Ordinal))
                    twinWingPromontories += named.Count;
            }

            JObject planning = BuildDoubleDistribution(evidence.planningMilliseconds);
            bool exactTopologies =
                selectedPatternCounts.TryGetValue(Phase1PatternId, out int processional) && processional == 100 &&
                selectedPatternCounts.TryGetValue(AtriumRingPatternId, out int atrium) && atrium == 50 &&
                selectedPatternCounts.TryGetValue(TwinWingPatternId, out int twinWing) && twinWing == 50;
            bool reliabilityPassed = successCount == Phase0BaselineSeedCount &&
                hardValidCount == Phase0BaselineSeedCount &&
                attemptDistribution.Value<int>("p95") <= 1 &&
                attemptDistribution.Value<int>("max") <= Phase1LayoutAttemptLimit;
            bool connectorsPassed = externalValidSeeds == successCount &&
                desiredCountRealizedSeeds == successCount &&
                uniqueDirectionSeeds == successCount &&
                externalCounts.ContainsKey(1) &&
                externalCounts.ContainsKey(2) &&
                externalCounts.ContainsKey(3) &&
                externalCounts.ContainsKey(4);
            bool scenicPassed = namedPromontories == 114 &&
                processionalPromontories == 22 &&
                atriumPromontories == 50 &&
                twinWingPromontories == 42;
            bool performancePassed = evidence.planningMilliseconds.Count == Phase0BaselineSeedCount &&
                planning.Value<double>("meanMs") <= 125d &&
                planning.Value<double>("p95Ms") <= 200d &&
                planning.Value<double>("maxMs") <= 750d &&
                evidence.measuredLoopSeconds <= 25d;
            report["correctiveValidation"] = new JObject
            {
                ["passed"] = exactTopologies && reliabilityPassed && connectorsPassed && scenicPassed && performancePassed,
                ["runOrdinal"] = evidence.runOrdinal,
                ["exactTopologySplit"] = exactTopologies,
                ["reliabilityPassed"] = reliabilityPassed,
                ["connectorsPassed"] = connectorsPassed,
                ["scenicBaselinePassed"] = scenicPassed,
                ["performancePassed"] = performancePassed,
                ["externalValidSeeds"] = externalValidSeeds,
                ["desiredCountRealizedSeeds"] = desiredCountRealizedSeeds,
                ["uniqueDirectionSeeds"] = uniqueDirectionSeeds,
                ["externalCountDistribution"] = new JObject(externalCounts.Select(entry =>
                    new JProperty(entry.Key.ToString(), entry.Value))),
                ["namedPromontories"] = namedPromontories,
                ["processionalPromontories"] = processionalPromontories,
                ["atriumPromontories"] = atriumPromontories,
                ["twinWingPromontories"] = twinWingPromontories,
                ["planningMilliseconds"] = planning,
                ["measuredLoopSeconds"] = evidence.measuredLoopSeconds,
                ["budgets"] = new JObject
                {
                    ["meanMilliseconds"] = 125,
                    ["p95Milliseconds"] = 200,
                    ["maxMilliseconds"] = 750,
                    ["loopSeconds"] = 25
                }
            };
        }

        private static JObject BuildCorrectiveBatchComparison(
            JObject baseline,
            JObject run1,
            JObject run2)
        {
            var failureCodes = new HashSet<string>(StringComparer.Ordinal);
            JArray baselineSeeds = baseline["seeds"] as JArray ?? new JArray();
            JArray firstSeeds = run1["seeds"] as JArray ?? new JArray();
            JArray secondSeeds = run2["seeds"] as JArray ?? new JArray();
            if (baselineSeeds.Count != Phase0BaselineSeedCount ||
                firstSeeds.Count != Phase0BaselineSeedCount ||
                secondSeeds.Count != Phase0BaselineSeedCount)
            {
                failureCodes.Add("SEED_COUNT_MISMATCH");
            }

            int compared = Math.Min(baselineSeeds.Count, Math.Min(firstSeeds.Count, secondSeeds.Count));
            int preservedStructureSeeds = 0;
            int deterministicSeeds = 0;
            for (int index = 0; index < compared; index++)
            {
                JToken before = baselineSeeds[index];
                JToken first = firstSeeds[index];
                JToken second = secondSeeds[index];
                bool sameSeed = before.Value<int?>("seed") == first.Value<int?>("seed") &&
                    first.Value<int?>("seed") == second.Value<int?>("seed");
                bool preserved = sameSeed &&
                    string.Equals(before["hashes"]?.Value<string>("layout"), first["hashes"]?.Value<string>("layout"), StringComparison.Ordinal) &&
                    string.Equals(before["hashes"]?.Value<string>("routeIntent"), first["hashes"]?.Value<string>("routeIntent"), StringComparison.Ordinal) &&
                    string.Equals(before["hashes"]?.Value<string>("recipeResolutions"), first["hashes"]?.Value<string>("recipeResolutions"), StringComparison.Ordinal) &&
                    string.Equals(before["hashes"]?.Value<string>("recipeCatalog"), first["hashes"]?.Value<string>("recipeCatalog"), StringComparison.Ordinal) &&
                    string.Equals(before["hashes"]?.Value<string>("tieredLevelPlan"), first["hashes"]?.Value<string>("preCorrectiveTieredLevelPlan"), StringComparison.Ordinal) &&
                    JToken.DeepEquals(before["namedPromontories"], first["namedPromontories"]) &&
                    JToken.DeepEquals(before["routeResolution"], first["routeResolution"]);
                if (preserved)
                    preservedStructureSeeds++;
                else
                    failureCodes.Add($"PRECHANGE_STRUCTURE_MISMATCH:{first.Value<int?>("seed")}");

                bool deterministic = JToken.DeepEquals(first, second);
                if (deterministic)
                    deterministicSeeds++;
                else
                    failureCodes.Add($"RUN_DETERMINISM_MISMATCH:{first.Value<int?>("seed")}");
            }

            bool aggregateDeterministic = string.Equals(
                run1.Value<string>("resultHash"),
                run2.Value<string>("resultHash"),
                StringComparison.Ordinal);
            if (!aggregateDeterministic)
                failureCodes.Add("AGGREGATE_DETERMINISM_MISMATCH");
            if (run1["correctiveValidation"]?.Value<bool?>("passed") != true)
                failureCodes.Add("RUN1_BUDGET_FAILED");
            if (run2["correctiveValidation"]?.Value<bool?>("passed") != true)
                failureCodes.Add("RUN2_BUDGET_FAILED");

            bool passed = compared == Phase0BaselineSeedCount &&
                preservedStructureSeeds == Phase0BaselineSeedCount &&
                deterministicSeeds == Phase0BaselineSeedCount &&
                aggregateDeterministic &&
                failureCodes.Count == 0;
            return new JObject
            {
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["reportVersion"] = "corrective-connections-comparison-v1",
                ["passed"] = passed,
                ["baselineSummaryVersion"] = baseline.Value<string>("summaryVersion"),
                ["currentSummaryVersion"] = run1.Value<string>("summaryVersion"),
                ["comparedSeeds"] = compared,
                ["preservedStructureSeeds"] = preservedStructureSeeds,
                ["deterministicSeeds"] = deterministicSeeds,
                ["aggregateDeterministic"] = aggregateDeterministic,
                ["run1ResultHash"] = run1.Value<string>("resultHash"),
                ["run2ResultHash"] = run2.Value<string>("resultHash"),
                ["run1Validation"] = run1["correctiveValidation"]?.DeepClone(),
                ["run2Validation"] = run2["correctiveValidation"]?.DeepClone(),
                ["failureCodes"] = new JArray(failureCodes.OrderBy(code => code, StringComparer.Ordinal))
            };
        }

        [MenuItem("Tools/Dungeon Lab/Corrective/Validate Nine Production Collision Floors")]
        public static void ValidateCorrectiveNineProductionCollisionFloors()
        {
            string comparisonPath = Path.Combine(
                BatchReportDirectory,
                "corrective_connections_200_seed_comparison.json");
            string runPath = Path.Combine(
                BatchReportDirectory,
                $"dungeon_plan_{Phase0BaselineFirstSeed}_{Phase0BaselineFirstSeed + Phase0BaselineSeedCount - 1}_corrective_run1.json");
            if (!File.Exists(comparisonPath) || !File.Exists(runPath) ||
                JObject.Parse(File.ReadAllText(comparisonPath)).Value<bool?>("passed") != true)
            {
                throw new InvalidOperationException(
                    "The passing corrective two-run comparison is required before nine-floor collision validation.");
            }

            JObject run = JObject.Parse(File.ReadAllText(runPath));
            List<Phase7ReviewSelection> selections =
                BuildCorrectiveNineFloorSelection(run);
            JObject report = RunCorrectiveNineFloorCollisionValidation(selections, run);
            string reportPath = Path.Combine(
                BatchReportDirectory,
                "corrective_nine_floor_collision_export.json");
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            if (report.Value<bool?>("passed") != true)
            {
                throw new InvalidOperationException(
                    $"Corrective nine-floor collision validation failed. Inspect '{reportPath}'.");
            }

            Debug.Log($"Dungeon Lab corrective nine-floor collision validation passed ({reportPath}).");
        }

        private static List<Phase7ReviewSelection> BuildCorrectiveNineFloorSelection(JObject report)
        {
            List<JToken> candidates = (report["seeds"] as JArray ?? new JArray())
                .Where(seed => seed.Value<bool?>("accepted") == true &&
                    seed["validation"]?.Value<bool?>("passed") == true)
                .ToList();
            var selected = new List<Phase7ReviewSelection>();
            var selectedSeeds = new HashSet<int>();

            void Add(string slot, Func<JToken, bool> predicate)
            {
                JToken seedReport = candidates
                    .Where(candidate => !selectedSeeds.Contains(candidate.Value<int>("seed")) && predicate(candidate))
                    .OrderBy(candidate => candidate["hashes"]?.Value<string>("canonical"), StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.Value<int>("seed"))
                    .FirstOrDefault();
                if (seedReport == null)
                    throw new InvalidOperationException($"No deterministic candidate remained for nine-floor slot '{slot}'.");
                int seed = seedReport.Value<int>("seed");
                selectedSeeds.Add(seed);
                selected.Add(new Phase7ReviewSelection
                {
                    seed = seed,
                    source = "corrective-200-seed-run1",
                    patternId = seedReport["routeIntent"]?.Value<string>("patternId") ?? string.Empty,
                    selectionSlot = slot,
                    expectedCanonicalHash = seedReport["hashes"]?.Value<string>("canonical") ?? string.Empty,
                    transitionCount = seedReport["tieredLevelPlan"]?.Value<int?>("transitionCount") ?? 0,
                    visibilityCount = seedReport["tieredLevelPlan"]?.Value<int?>("visibleDistantRoomProxyCount") ?? 0,
                    hasNamedPromontory = (seedReport["namedPromontories"] as JArray)?.Count > 0
                });
            }

            for (int count = 1; count <= 4; count++)
            {
                int requiredCount = count;
                Add($"external-count-{count}", candidate =>
                    (candidate["externalConnectors"] as JArray)?.Count == requiredCount);
            }
            Add("topology-processional", candidate =>
                string.Equals(candidate["routeIntent"]?.Value<string>("patternId"), Phase1PatternId, StringComparison.Ordinal));
            Add("topology-atrium", candidate =>
                string.Equals(candidate["routeIntent"]?.Value<string>("patternId"), AtriumRingPatternId, StringComparison.Ordinal));
            Add("topology-twin-wing", candidate =>
                string.Equals(candidate["routeIntent"]?.Value<string>("patternId"), TwinWingPatternId, StringComparison.Ordinal));
            Add("scenic-coexistence", candidate =>
                (candidate["namedPromontories"] as JArray)?.Count > 0 &&
                (candidate["externalConnectors"] as JArray)?.Count > 0);
            Add("stable-hash-fill", _ => true);
            return selected;
        }

        private static JObject RunCorrectiveNineFloorCollisionValidation(
            IReadOnlyList<Phase7ReviewSelection> selections,
            JObject runReport)
        {
            if (selections.Count != 9 || selections.Select(selection => selection.seed).Distinct().Count() != 9)
                throw new InvalidOperationException("Corrective collision selection must contain exactly nine unique floors.");

            string unique = Guid.NewGuid().ToString("N");
            string sceneAssetPath =
                $"Assets/Arena/Content/Scenes/OpenWorld/CorrectiveNineFloor_{unique}.unity";
            string dataKey = $"corrective_nine_floor_{unique}";
            string serverMovementPath = Phase7ServerCollisionPath(dataKey, query: false);
            string serverQueryPath = Phase7ServerCollisionPath(dataKey, query: true);
            string bundledMovementPath = Phase7BundledCollisionPath(dataKey, query: false);
            string bundledQueryPath = Phase7BundledCollisionPath(dataKey, query: true);
            var cleanupFailures = new List<string>();
            var preparationFailures = new List<string>();
            var trackedSnapshots = new Dictionary<string, Phase7TrackedArtifactSnapshot>(StringComparer.Ordinal);
            var records = new JArray();
            var elapsedMilliseconds = new List<double>();
            int exportWarmupSeedCount = 0;

            try
            {
                CapturePhase7TrackedArtifact(Phase7SynthesizedStairLogPath, trackedSnapshots);
                RequireAbsentPhase7TemporaryArtifacts(
                    sceneAssetPath,
                    serverMovementPath,
                    serverQueryPath,
                    bundledMovementPath,
                    bundledQueryPath);

                try
                {
                    exportWarmupSeedCount = WarmPhase7CollisionExportProcess(
                        selections,
                        sceneAssetPath,
                        dataKey,
                        trackedSnapshots);
                }
                catch (Exception exception)
                {
                    preparationFailures.Add(
                        $"EXPORT_WARMUP_FAILED:{exception.GetType().Name}:{exception.Message}");
                }

                if (preparationFailures.Count == 0)
                {
                    foreach (Phase7ReviewSelection selection in selections)
                    {
                        JObject record = ValidatePhase7CollisionExportSeed(
                            selection,
                            sceneAssetPath,
                            dataKey,
                            serverMovementPath,
                            serverQueryPath,
                            bundledMovementPath,
                            bundledQueryPath,
                            trackedSnapshots,
                            out double elapsedMs);
                        JToken seedReport = (runReport["seeds"] as JArray ?? new JArray())
                            .First(seed => seed.Value<int>("seed") == selection.seed);
                        bool terminalsClear = ValidateCorrectiveTerminalSources(seedReport, out string terminalMessage);
                        record["terminalSourceClearancePassed"] = terminalsClear;
                        record["terminalSourceClearanceMessage"] = terminalMessage;
                        record["passed"] = record.Value<bool?>("passed") == true && terminalsClear;
                        records.Add(record);
                        elapsedMilliseconds.Add(elapsedMs);
                    }
                }
            }
            finally
            {
                CleanupPhase7CollisionArtifacts(
                    sceneAssetPath,
                    serverMovementPath,
                    serverQueryPath,
                    bundledMovementPath,
                    bundledQueryPath,
                    cleanupFailures);
                RestorePhase7TrackedArtifacts(trackedSnapshots, cleanupFailures);
            }

            JObject performance = BuildDoubleDistribution(elapsedMilliseconds);
            bool performancePassed = elapsedMilliseconds.Count == 9 &&
                performance.Value<double>("p95Ms") <= 2500d &&
                performance.Value<double>("maxMs") <= 5000d;
            bool everyFloorPassed = records.Count == 9 &&
                records.All(record => record.Value<bool?>("passed") == true);
            bool passed = preparationFailures.Count == 0 &&
                everyFloorPassed &&
                performancePassed &&
                cleanupFailures.Count == 0;
            return new JObject
            {
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["reportVersion"] = "corrective-nine-floor-collision-v1",
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["passed"] = passed,
                ["floorCount"] = records.Count,
                ["requiredFloorCount"] = 9,
                ["everyFloorPassed"] = everyFloorPassed,
                ["performancePassed"] = performancePassed,
                ["exportWarmupPassed"] = preparationFailures.Count == 0,
                ["exportWarmupSeedCount"] = exportWarmupSeedCount,
                ["selection"] = BuildPhase7SelectionToken(selections),
                ["performance"] = new JObject
                {
                    ["milliseconds"] = performance,
                    ["maximumP95Milliseconds"] = 2500,
                    ["maximumFloorMilliseconds"] = 5000
                },
                ["preparationFailures"] = new JArray(preparationFailures),
                ["cleanupFailures"] = new JArray(cleanupFailures),
                ["records"] = records
            };
        }

        private static bool ValidateCorrectiveTerminalSources(
            JToken seedReport,
            out string message)
        {
            GameObject root = UnityEngine.SceneManagement.SceneManager.GetActiveScene()
                .GetRootGameObjects()
                .FirstOrDefault(candidate => string.Equals(candidate.name, "Generated Dungeon", StringComparison.Ordinal));
            if (root == null)
            {
                message = "generated root missing";
                return false;
            }

            Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: false);
            JArray connectors = seedReport["externalConnectors"] as JArray ?? new JArray();
            foreach (JToken connector in connectors)
            {
                int x = connector["terminalCell"]?.Value<int>("x") ?? int.MinValue;
                int y = connector["terminalCell"]?.Value<int>("y") ?? int.MinValue;
                int level = connector.Value<int>("level");
                int direction = connector.Value<int>("directionId");
                string floorName = $"floor_{x}_{y}_level_{level}";
                Transform floor = transforms.FirstOrDefault(transform =>
                    string.Equals(transform.name, floorName, StringComparison.Ordinal));
                if (floor == null || floor.GetComponentsInChildren<Collider>(includeInactive: false)
                        .All(collider => collider == null || !collider.enabled || collider.isTrigger))
                {
                    message = $"terminal floor collider missing for {connector.Value<string>("id")}";
                    return false;
                }

                string forbiddenCoverPrefix = $"pier_cover_{x}_{y}_{direction}_";
                if (transforms.Any(transform => transform.name.StartsWith(
                        forbiddenCoverPrefix,
                        StringComparison.Ordinal)))
                {
                    message = $"terminal edge fascia survived for {connector.Value<string>("id")}";
                    return false;
                }
            }

            message = $"{connectors.Count} terminal floors present with no terminal-edge fascia; exact source/export parity covers all enabled colliders";
            return connectors.Count >= 1 && connectors.Count <= 4;
        }

        private static string BuildStackedCrossingSnapshot()
        {
            CorrectiveStackedFixture fixture = null;
            try
            {
                fixture = BuildCorrectiveStackedFixture();
                return string.Join("\n", new[]
                {
                    $"fixture.transitionCount={fixture.transitions.Count}",
                    $"fixture.placementClass={fixture.bridge.placementClass}",
                    $"fixture.stackedCoordinateCount={fixture.bridge.footprintCells.Count(cell => fixture.levels.ContainsKey(cell))}",
                    $"fixture.lowerTraversable={fixture.lowerRouteTraversable}",
                    $"fixture.upperTraversable={fixture.upperBridgeTraversable}",
                    $"fixture.positiveHeadroom={fixture.positiveHeadroomPassed}",
                    $"fixture.negativeHeadroomRejected={fixture.negativeHeadroomRejected}",
                    $"fixture.rendererRejected={fixture.buildReport.rejected}",
                    $"fixture.lowerClearanceOpen={fixture.lowerClearanceOpen}",
                    $"fixture.lowerSurfaceColliders={fixture.lowerSurfaceColliderNames.Count}",
                    $"fixture.upperSurfaceColliders={fixture.upperSurfaceColliderNames.Count}"
                });
            }
            finally
            {
                if (fixture?.root != null)
                    DestroyImmediate(fixture.root);
            }
        }

        [MenuItem("Tools/Dungeon Lab/Corrective/Validate Stacked Crossing Export")]
        public static void ValidateCorrectiveStackedCrossingExport()
        {
            JObject report = RunCorrectiveStackedCrossingExport();
            Directory.CreateDirectory(BatchReportDirectory);
            string path = Path.Combine(
                BatchReportDirectory,
                "corrective_stacked_crossing_export.json");
            File.WriteAllText(path, report.ToString(Formatting.Indented));
            if (report.Value<bool?>("passed") != true)
            {
                throw new InvalidOperationException(
                    $"Corrective stacked-crossing export failed. Inspect '{path}'.");
            }

            Debug.Log($"Dungeon Lab corrective stacked-crossing export passed ({path}).");
        }

        private static JObject RunCorrectiveStackedCrossingExport()
        {
            string unique = Guid.NewGuid().ToString("N");
            string dataKey = $"corrective_stacked_crossing_{unique}";
            string sceneAssetPath =
                $"Assets/Arena/Content/Scenes/OpenWorld/CorrectiveStackedCrossing_{unique}.unity";
            string serverMovementPath = Phase7ServerCollisionPath(dataKey, query: false);
            string serverQueryPath = Phase7ServerCollisionPath(dataKey, query: true);
            string bundledMovementPath = Phase7BundledCollisionPath(dataKey, query: false);
            string bundledQueryPath = Phase7BundledCollisionPath(dataKey, query: true);
            var cleanupFailures = new List<string>();
            var failureCodes = new HashSet<string>(StringComparer.Ordinal);
            var trackedSnapshots = new Dictionary<string, Phase7TrackedArtifactSnapshot>(StringComparer.Ordinal);
            JObject movementSummary = new JObject();
            JObject querySummary = new JObject();
            bool lowerSemanticExported = false;
            bool upperSemanticExported = false;
            CorrectiveStackedFixture fixture = null;

            try
            {
                RequireAbsentPhase7TemporaryArtifacts(
                    sceneAssetPath,
                    serverMovementPath,
                    serverQueryPath,
                    bundledMovementPath,
                    bundledQueryPath);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                fixture = BuildCorrectiveStackedFixture();
                RandomDungeonSceneBuilder.PrepareGeneratedDungeonCollisionForValidation(
                    fixture.root,
                    modelPath => CapturePhase7TrackedArtifact(
                        $"{modelPath}.meta",
                        trackedSnapshots));

                List<Collider> colliders = fixture.root
                    .GetComponentsInChildren<Collider>(includeInactive: false)
                    .Where(collider => collider != null && collider.enabled && !collider.isTrigger)
                    .ToList();
                List<string> sourceNames = colliders
                    .Where(collider => collider is BoxCollider || collider is MeshCollider)
                    .Select(collider => Phase7CollisionHierarchyPath(collider.transform))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                if (sourceNames.Count == 0)
                    failureCodes.Add("EMPTY_COLLISION_SOURCE");
                if (colliders.Any(collider => !(collider is BoxCollider) && !(collider is MeshCollider)))
                    failureCodes.Add("UNSUPPORTED_COLLIDER_SOURCE");

                GameplayCollisionExporter.ExportActiveSceneSharedCollisionData(dataKey);
                movementSummary = ValidatePhase7ExportPayload(
                    serverMovementPath,
                    bundledMovementPath,
                    sourceNames,
                    "MOVEMENT",
                    failureCodes,
                    out _,
                    out _);
                querySummary = ValidatePhase7ExportPayload(
                    serverQueryPath,
                    bundledQueryPath,
                    sourceNames,
                    "QUERY",
                    failureCodes,
                    out _,
                    out _);
                lowerSemanticExported = ExportContainsEveryName(
                    serverMovementPath,
                    fixture.lowerSurfaceColliderNames) &&
                    ExportContainsEveryName(serverQueryPath, fixture.lowerSurfaceColliderNames);
                upperSemanticExported = ExportContainsEveryName(
                    serverMovementPath,
                    fixture.upperSurfaceColliderNames) &&
                    ExportContainsEveryName(serverQueryPath, fixture.upperSurfaceColliderNames);
                if (!lowerSemanticExported)
                    failureCodes.Add("LOWER_SURFACE_EXPORT_MISSING");
                if (!upperSemanticExported)
                    failureCodes.Add("UPPER_SURFACE_EXPORT_MISSING");
                if (!fixture.lowerClearanceOpen)
                    failureCodes.Add("LOWER_CLEARANCE_OBSTRUCTED");
            }
            catch (Exception exception)
            {
                failureCodes.Add($"STACKED_EXPORT_EXCEPTION:{exception.GetType().Name}:{exception.Message}");
            }
            finally
            {
                CleanupPhase7CollisionArtifacts(
                    sceneAssetPath,
                    serverMovementPath,
                    serverQueryPath,
                    bundledMovementPath,
                    bundledQueryPath,
                    cleanupFailures);
                RestorePhase7TrackedArtifacts(trackedSnapshots, cleanupFailures);
            }

            bool fixturePassed = fixture != null &&
                fixture.transitions.Count == 1 &&
                fixture.lowerRouteTraversable &&
                fixture.upperBridgeTraversable &&
                fixture.positiveHeadroomPassed &&
                fixture.negativeHeadroomRejected &&
                fixture.buildReport.rejected == 0 &&
                fixture.lowerClearanceOpen &&
                fixture.lowerSurfaceColliderNames.Count > 0 &&
                fixture.upperSurfaceColliderNames.Count > 0;
            bool passed = fixturePassed &&
                lowerSemanticExported &&
                upperSemanticExported &&
                failureCodes.Count == 0 &&
                cleanupFailures.Count == 0;
            return new JObject
            {
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["reportVersion"] = CorrectiveStackedCrossingReportVersion,
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["passed"] = passed,
                ["fixturePassed"] = fixturePassed,
                ["lowerSemanticExported"] = lowerSemanticExported,
                ["upperSemanticExported"] = upperSemanticExported,
                ["movementExport"] = movementSummary,
                ["queryExport"] = querySummary,
                ["failureCodes"] = new JArray(failureCodes.OrderBy(code => code, StringComparer.Ordinal)),
                ["cleanupFailures"] = new JArray(cleanupFailures)
            };
        }

        private static CorrectiveStackedFixture BuildCorrectiveStackedFixture()
        {
            RoomFootprint west = RoomFootprint.FromRect(new RectInt(-4, -1, 2, 3));
            RoomFootprint east = RoomFootprint.FromRect(new RectInt(3, -1, 2, 3));
            var floorCells = new HashSet<Vector2Int>();
            floorCells.UnionWith(west.cells);
            floorCells.UnionWith(east.cells);
            var levels = new Dictionary<Vector2Int, int>();
            foreach (Vector2Int cell in floorCells)
                levels[cell] = 4;

            Vector2Int lowerStart = new Vector2Int(0, -4);
            Vector2Int lowerEnd = new Vector2Int(0, 4);
            for (int y = lowerStart.y; y <= lowerEnd.y; y++)
            {
                var cell = new Vector2Int(0, y);
                floorCells.Add(cell);
                levels[cell] = 0;
            }

            var layout = new DungeonLayout(
                floorCells,
                new List<RoomFootprint> { west, east },
                new List<RoomConnection>());
            var transitions = new List<ElevationEdgeModel.TransitionEdge>();
            var spanDeckLevels = new Dictionary<Vector2Int, int>();
            AddAerialBridges(
                layout,
                levels,
                new System.Random(7),
                transitions,
                new HashSet<string>(),
                new StairPlacementLedger(),
                spanDeckLevels,
                new List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)>(),
                new HashSet<Vector2Int>());
            ElevationEdgeModel.TransitionEdge bridge = transitions.Single(transition =>
                string.Equals(
                    transition.placementClass,
                    ExternalSpanStairPlacementClass,
                    StringComparison.Ordinal));
            List<Vector2Int> stackedCells = bridge.footprintCells
                .Where(cell => levels.TryGetValue(cell, out int lowerLevel) &&
                    spanDeckLevels.TryGetValue(cell, out int deckLevel) &&
                    deckLevel - lowerLevel >= MinHeadroomLevels)
                .ToList();
            if (stackedCells.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Focused fixture expected one exact stacked coordinate; found {stackedCells.Count}.");
            }

            bool positiveHeadroom = TryValidateSpanHeadroom(
                levels,
                spanDeckLevels,
                out _);
            var negativeLevels = new Dictionary<Vector2Int, int>(levels)
            {
                [stackedCells[0]] = spanDeckLevels[stackedCells[0]] - 2
            };
            bool negativeRejected = !TryValidateSpanHeadroom(
                negativeLevels,
                spanDeckLevels,
                out _);
            bool lowerTraversable = EqualLevelPathExists(
                levels,
                lowerStart,
                lowerEnd,
                0);
            var upperLevels = new Dictionary<Vector2Int, int>();
            foreach (Vector2Int cell in west.cells)
                upperLevels[cell] = 4;
            foreach (Vector2Int cell in east.cells)
                upperLevels[cell] = 4;
            bool upperGraphBuilt = TryBuildFloorStairPortGraph(
                upperLevels,
                transitions,
                out FloorStairPortGraph upperGraph,
                out _);
            bool upperTraversable = upperGraphBuilt && upperGraph.IsGloballyConnected(out _);

            GameObject root = ElevationEdgeModel.BuildLevelField(
                Vector3.zero,
                levels,
                transitions,
                null,
                null,
                null,
                null,
                "Corrective Stacked Crossing Fixture",
                out ElevationEdgeModel.BuildReport buildReport,
                out _);
            Physics.SyncTransforms();
            CollectStackedSurfaceEvidence(
                root,
                stackedCells[0],
                spanDeckLevels[stackedCells[0]],
                buildReport.levelHeight,
                out bool lowerClearanceOpen,
                out List<string> lowerSurfaceColliders,
                out List<string> upperSurfaceColliders);

            return new CorrectiveStackedFixture
            {
                levels = levels,
                transitions = transitions,
                spanDeckLevels = spanDeckLevels,
                stackedCell = stackedCells[0],
                lowerStart = lowerStart,
                lowerEnd = lowerEnd,
                bridge = bridge,
                root = root,
                buildReport = buildReport,
                lowerRouteTraversable = lowerTraversable,
                upperBridgeTraversable = upperTraversable,
                positiveHeadroomPassed = positiveHeadroom,
                negativeHeadroomRejected = negativeRejected,
                lowerClearanceOpen = lowerClearanceOpen,
                lowerSurfaceColliderNames = lowerSurfaceColliders,
                upperSurfaceColliderNames = upperSurfaceColliders
            };
        }

        private static void CollectStackedSurfaceEvidence(
            GameObject root,
            Vector2Int stackedCell,
            int deckLevel,
            float levelHeight,
            out bool lowerClearanceOpen,
            out List<string> lowerSurfaceColliderNames,
            out List<string> upperSurfaceColliderNames)
        {
            Vector3 center = new Vector3(
                (stackedCell.x + 0.5f) * 4f,
                0f,
                (stackedCell.y + 0.5f) * 4f);
            Collider[] colliders = root
                .GetComponentsInChildren<Collider>(includeInactive: false)
                .Where(collider => collider != null && collider.enabled && !collider.isTrigger)
                .ToArray();
            var clearance = new Bounds(
                center + Vector3.up * 1.5f * levelHeight,
                new Vector3(1f, 2.6f * levelHeight, 1f));
            lowerClearanceOpen = !colliders.Any(collider =>
                collider.bounds.Intersects(clearance));

            bool CoversHorizontalPoint(Collider collider)
            {
                Bounds bounds = collider.bounds;
                return center.x >= bounds.min.x && center.x <= bounds.max.x &&
                    center.z >= bounds.min.z && center.z <= bounds.max.z;
            }

            lowerSurfaceColliderNames = colliders
                .Where(collider => CoversHorizontalPoint(collider) &&
                    collider.bounds.min.y <= 0.1f && collider.bounds.max.y >= -0.1f)
                .Select(collider => Phase7CollisionHierarchyPath(collider.transform))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            float deckY = deckLevel * levelHeight;
            upperSurfaceColliderNames = colliders
                .Where(collider => CoversHorizontalPoint(collider) &&
                    collider.bounds.min.y <= deckY + 0.1f &&
                    collider.bounds.max.y >= deckY - 0.1f &&
                    Phase7CollisionHierarchyPath(collider.transform)
                        .Contains("Transition Stairs"))
                .Select(collider => Phase7CollisionHierarchyPath(collider.transform))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }

        private static bool EqualLevelPathExists(
            IReadOnlyDictionary<Vector2Int, int> levels,
            Vector2Int start,
            Vector2Int end,
            int level)
        {
            var visited = new HashSet<Vector2Int> { start };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                if (cell == end)
                    return true;
                foreach (Vector2Int neighbor in CardinalNeighbors(cell))
                {
                    if (levels.TryGetValue(neighbor, out int neighborLevel) &&
                        neighborLevel == level &&
                        visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            return false;
        }

        private static bool ExportContainsEveryName(
            string path,
            IReadOnlyCollection<string> requiredNames)
        {
            if (requiredNames == null || requiredNames.Count == 0 || !File.Exists(path))
                return false;
            JObject payload = JObject.Parse(File.ReadAllText(path));
            var names = new HashSet<string>(
                (payload["boxes"] as JArray ?? new JArray())
                    .Concat(payload["mesh_instances"] as JArray ?? new JArray())
                    .Select(token => token.Value<string>("name") ?? string.Empty),
                StringComparer.Ordinal);
            return requiredNames.All(names.Contains);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Plastic.Newtonsoft.Json;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // Phase 7 collision evidence drives the existing production scene-builder
    // and exporter through unique temporary destinations. It does not add a
    // second renderer, collision representation, or canonical plan projection.
    internal sealed partial class DungeonLabGenerator
    {
        private const int Phase7ReviewSentinelCount = 6;
        private const int Phase7ReviewSeedCountPerPattern = 8;
        private const int Phase7CollisionReviewSeedCount = 30;
        private const double Phase7MaximumRenderedExportP95Milliseconds = 2500d;
        private const double Phase7MaximumRenderedExportSeedMilliseconds = 5000d;
        private const string Phase7CollisionReportVersion = "phase7-collision-export-v1";
        private const string Phase7CollisionTempScenePrefix = "DungeonLabPhase7CollisionValidation";
        private const string Phase7CollisionTempDataPrefix = "phase7_collision_validation";
        private const string Phase7SynthesizedStairLogPath =
            "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/synthesized_stair_log.json";

        private static readonly string[] Phase7ReviewPatternOrder =
        {
            Phase1PatternId,
            AtriumRingPatternId,
            TwinWingPatternId
        };

        private static readonly string[] Phase7CollisionPerformanceStageNames =
        {
            "planAndRender",
            "resolveGeneratedRoot",
            "centerDungeonSpawn",
            "normalizeCollisionMeshImporters",
            "markDungeonCollision",
            "activateAndRegisterScene",
            "exportSharedCollision"
        };

        private static readonly string[] Phase7CollisionNonBudgetStageNames =
        {
            "newScene",
            "sceneMetadataCameraAndLighting",
            "saveSceneBeforeExport",
            "saveSceneAndAssetsAfterExport"
        };

        private sealed class Phase7ReviewCandidate
        {
            internal int seed;
            internal string patternId;
            internal int transitionCount;
            internal int visibilityCount;
            internal bool hasNamedPromontory;
            internal string canonicalHash;
        }

        private sealed class Phase7ReviewSelection
        {
            internal int seed;
            internal string source;
            internal string patternId;
            internal string selectionSlot;
            internal string expectedCanonicalHash;
            internal int transitionCount;
            internal int visibilityCount;
            internal bool hasNamedPromontory;
        }

        private sealed class Phase7TrackedArtifactSnapshot
        {
            internal string path;
            internal bool existed;
            internal byte[] bytes;
        }

        [MenuItem("Tools/Dungeon Lab/Phase 7/Validate Curated Collision Export Parity")]
        public static void BatchValidatePhase7CollisionExportParity()
        {
            var environmentEvidence = new Phase7BatchEvidence(0, Phase7WarmupSeedCount);
            JObject measurementEnvironment = BuildPhase7MeasurementEnvironment(environmentEvidence);
            if (measurementEnvironment.Value<bool?>("passed") != true)
            {
                throw new InvalidOperationException(
                    "Phase 7 collision-export validation requires the locked measurement environment and explicit preflight confirmation.");
            }

            JObject sweepReport = LoadPhase7AcceptedSweepReport();
            JObject sentinelManifest = LoadPhase7SentinelManifest();
            List<Phase7ReviewSelection> selections =
                BuildPhase7CollisionReviewSelection(sweepReport, sentinelManifest);
            if (selections.Count != Phase7CollisionReviewSeedCount ||
                selections.Select(selection => selection.seed).Distinct().Count() != Phase7CollisionReviewSeedCount)
            {
                throw new InvalidOperationException(
                    $"Phase 7 collision selector produced {selections.Count} records instead of " +
                    $"{Phase7CollisionReviewSeedCount} unique seeds.");
            }

            WarmPhase7MeasurementProcess();
            JObject report = RunPhase7CollisionExportValidation(selections, measurementEnvironment);
            string reportPath = Phase7CollisionReportPath();
            Directory.CreateDirectory(BatchReportDirectory);
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            if (report.Value<bool?>("passed") != true)
            {
                throw new InvalidOperationException(
                    $"Phase 7 collision-export validation failed. Inspect '{reportPath}'.");
            }

            Debug.Log($"Dungeon Lab Phase 7: curated collision-export parity passed ({reportPath}).");
        }

        private static JObject LoadPhase7AcceptedSweepReport()
        {
            string path = Path.Combine(
                BatchReportDirectory,
                $"dungeon_plan_{Phase7FirstSeed}_{Phase7LastSeed}_phase7_run1.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "The passing Phase 7 first-sweep report is required to select the locked review corpus.",
                    path);
            }

            string comparisonPath = Phase7ComparisonPath();
            if (!File.Exists(comparisonPath))
            {
                throw new FileNotFoundException(
                    "The passing Phase 7 sweep comparison is required before collision validation.",
                    comparisonPath);
            }

            JObject comparison = JObject.Parse(File.ReadAllText(comparisonPath));
            if (comparison.Value<bool?>("passed") != true)
            {
                throw new InvalidOperationException(
                    "The Phase 7 automated sweep comparison is not passing.");
            }

            JObject report = JObject.Parse(File.ReadAllText(path));
            if (report.Value<int?>("firstSeed") != Phase7FirstSeed ||
                report.Value<int?>("lastSeed") != Phase7LastSeed ||
                report.Value<int?>("seedCount") != Phase7SeedCount ||
                report["phase7BudgetResult"]?.Value<bool?>("passed") != true ||
                !string.Equals(
                    report.Value<string>("resultHash"),
                    comparison["firstSweep"]?.Value<string>("resultHash"),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Phase 7 first-sweep report does not match the passing locked comparison.");
            }

            return report;
        }

        private static JObject LoadPhase7SentinelManifest()
        {
            string path = Path.Combine(BatchReportDirectory, "visual_sentinels", "manifest.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "The six established visual-sentinel records are required for the locked review corpus.",
                    path);
            }

            JObject manifest = JObject.Parse(File.ReadAllText(path));
            if ((manifest["sentinels"] as JArray)?.Count != Phase7ReviewSentinelCount)
            {
                throw new InvalidOperationException(
                    $"The visual-sentinel manifest must contain exactly {Phase7ReviewSentinelCount} records.");
            }

            return manifest;
        }

        private static List<Phase7ReviewSelection> BuildPhase7CollisionReviewSelection(
            JObject sweepReport,
            JObject sentinelManifest)
        {
            var selections = new List<Phase7ReviewSelection>(Phase7CollisionReviewSeedCount);
            JArray sentinelRecords = sentinelManifest?["sentinels"] as JArray ?? new JArray();
            foreach ((int seed, string category, string annotation) sentinel in Phase0VisualSentinels)
            {
                JObject record = sentinelRecords
                    .OfType<JObject>()
                    .SingleOrDefault(candidate => candidate.Value<int?>("seed") == sentinel.seed);
                if (record == null ||
                    !string.Equals(record.Value<string>("category"), sentinel.category, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(record.Value<string>("canonicalHash")))
                {
                    throw new InvalidOperationException(
                        $"Historical sentinel {sentinel.seed} ({sentinel.category}) is missing or inconsistent.");
                }

                selections.Add(new Phase7ReviewSelection
                {
                    seed = sentinel.seed,
                    source = "historical-sentinel",
                    patternId = SelectedRoutePatternId(sentinel.seed),
                    selectionSlot = sentinel.category,
                    expectedCanonicalHash = record.Value<string>("canonicalHash"),
                    transitionCount = -1,
                    visibilityCount = -1,
                    hasNamedPromontory = false
                });
            }

            JArray seedReports = sweepReport?["seeds"] as JArray ?? new JArray();
            foreach (string patternId in Phase7ReviewPatternOrder)
            {
                List<Phase7ReviewCandidate> candidates = seedReports
                    .OfType<JObject>()
                    .Where(report =>
                        report.Value<bool?>("accepted") == true &&
                        report["validation"]?.Value<bool?>("passed") == true &&
                        string.Equals(
                            report["routeIntent"]?.Value<string>("patternId"),
                            patternId,
                            StringComparison.Ordinal))
                    .Select(report => new Phase7ReviewCandidate
                    {
                        seed = report.Value<int>("seed"),
                        patternId = patternId,
                        transitionCount = report["tieredLevelPlan"]?.Value<int?>("transitionCount") ?? -1,
                        visibilityCount = report["tieredLevelPlan"]?.Value<int?>("visibleDistantRoomProxyCount") ?? -1,
                        hasNamedPromontory = (report["namedPromontories"] as JArray)?.Count > 0,
                        canonicalHash = report["hashes"]?.Value<string>("canonical") ?? string.Empty
                    })
                    .OrderBy(candidate => candidate.seed)
                    .ToList();
                if (candidates.Count == 0 ||
                    candidates.Any(candidate =>
                        candidate.transitionCount < 0 ||
                        candidate.visibilityCount < 0 ||
                        string.IsNullOrWhiteSpace(candidate.canonicalHash)))
                {
                    throw new InvalidOperationException(
                        $"Phase 7 review candidates for '{patternId}' are missing locked metrics or hashes.");
                }

                AddPhase7PatternSelections(selections, candidates, patternId);
            }

            int phase7SelectionCount = selections.Count(selection =>
                string.Equals(selection.source, "phase7-sweep", StringComparison.Ordinal));
            if (selections.Count != Phase7CollisionReviewSeedCount ||
                phase7SelectionCount != Phase7ReviewSeedCountPerPattern * Phase7ReviewPatternOrder.Length ||
                Phase7ReviewPatternOrder.Any(pattern =>
                    selections.Count(selection =>
                        string.Equals(selection.source, "phase7-sweep", StringComparison.Ordinal) &&
                        string.Equals(selection.patternId, pattern, StringComparison.Ordinal)) !=
                    Phase7ReviewSeedCountPerPattern))
            {
                throw new InvalidOperationException(
                    "Phase 7 collision review selection did not preserve the locked 6 + 8/8/8 composition.");
            }

            return selections;
        }

        private static void AddPhase7PatternSelections(
            List<Phase7ReviewSelection> selections,
            List<Phase7ReviewCandidate> candidates,
            string patternId)
        {
            var selectedSeeds = new HashSet<int>();
            var transitions = candidates.Select(candidate => candidate.transitionCount).OrderBy(value => value).ToList();
            var visibility = candidates.Select(candidate => candidate.visibilityCount).OrderBy(value => value).ToList();
            int medianTransitions = NearestRank(transitions, 0.50d);
            int medianVisibility = NearestRank(visibility, 0.50d);

            AddPhase7Selection(
                selections,
                selectedSeeds,
                "median-pair",
                candidates
                    .OrderBy(candidate =>
                        Math.Abs(candidate.transitionCount - medianTransitions) +
                        Math.Abs(candidate.visibilityCount - medianVisibility))
                    .ThenBy(candidate => candidate.seed));
            AddPhase7Selection(
                selections,
                selectedSeeds,
                "minimum-transitions",
                candidates.OrderBy(candidate => candidate.transitionCount).ThenBy(candidate => candidate.seed));
            AddPhase7Selection(
                selections,
                selectedSeeds,
                "maximum-transitions",
                candidates.OrderByDescending(candidate => candidate.transitionCount).ThenBy(candidate => candidate.seed));
            AddPhase7Selection(
                selections,
                selectedSeeds,
                "minimum-distant-visibility",
                candidates.OrderBy(candidate => candidate.visibilityCount).ThenBy(candidate => candidate.seed));
            AddPhase7Selection(
                selections,
                selectedSeeds,
                "maximum-distant-visibility",
                candidates.OrderByDescending(candidate => candidate.visibilityCount).ThenBy(candidate => candidate.seed));
            AddPhase7Selection(
                selections,
                selectedSeeds,
                "named-promontory-present",
                candidates.Where(candidate => candidate.hasNamedPromontory).OrderBy(candidate => candidate.seed));

            List<Phase7ReviewCandidate> withoutPromontory = candidates
                .Where(candidate => !candidate.hasNamedPromontory)
                .OrderBy(candidate => candidate.seed)
                .ToList();
            if (withoutPromontory.Count > 0)
            {
                AddPhase7Selection(
                    selections,
                    selectedSeeds,
                    "named-promontory-absent",
                    withoutPromontory);
            }
            else
            {
                AddPhase7Selection(
                    selections,
                    selectedSeeds,
                    "named-promontory-absent-hash-replacement",
                    Phase7HashRankedCandidates(candidates));
            }

            AddPhase7Selection(
                selections,
                selectedSeeds,
                "stable-hash-ranked",
                Phase7HashRankedCandidates(candidates));

            if (selectedSeeds.Count != Phase7ReviewSeedCountPerPattern)
            {
                throw new InvalidOperationException(
                    $"Phase 7 selector chose {selectedSeeds.Count} unique '{patternId}' seeds instead of " +
                    $"{Phase7ReviewSeedCountPerPattern}.");
            }
        }

        private static IOrderedEnumerable<Phase7ReviewCandidate> Phase7HashRankedCandidates(
            IEnumerable<Phase7ReviewCandidate> candidates)
        {
            return candidates
                .OrderBy(candidate => candidate.canonicalHash, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.seed);
        }

        private static void AddPhase7Selection(
            List<Phase7ReviewSelection> selections,
            HashSet<int> selectedSeeds,
            string slot,
            IEnumerable<Phase7ReviewCandidate> orderedCandidates)
        {
            Phase7ReviewCandidate selected = orderedCandidates.FirstOrDefault(candidate =>
                !selectedSeeds.Contains(candidate.seed));
            if (selected == null)
            {
                throw new InvalidOperationException(
                    $"Phase 7 review slot '{slot}' has no remaining eligible candidate.");
            }

            selectedSeeds.Add(selected.seed);
            selections.Add(new Phase7ReviewSelection
            {
                seed = selected.seed,
                source = "phase7-sweep",
                patternId = selected.patternId,
                selectionSlot = slot,
                expectedCanonicalHash = selected.canonicalHash,
                transitionCount = selected.transitionCount,
                visibilityCount = selected.visibilityCount,
                hasNamedPromontory = selected.hasNamedPromontory
            });
        }

        private static JObject RunPhase7CollisionExportValidation(
            IReadOnlyList<Phase7ReviewSelection> selections,
            JObject measurementEnvironment)
        {
            string unique = Guid.NewGuid().ToString("N");
            string sceneAssetPath =
                $"Assets/Arena/Content/Scenes/OpenWorld/{Phase7CollisionTempScenePrefix}_{unique}.unity";
            string dataKey = $"{Phase7CollisionTempDataPrefix}_{unique}";
            string serverMovementPath = Phase7ServerCollisionPath(dataKey, query: false);
            string serverQueryPath = Phase7ServerCollisionPath(dataKey, query: true);
            string bundledMovementPath = Phase7BundledCollisionPath(dataKey, query: false);
            string bundledQueryPath = Phase7BundledCollisionPath(dataKey, query: true);
            var records = new JArray();
            var elapsedMilliseconds = new List<double>(selections.Count);
            var cleanupFailures = new List<string>();
            var preparationFailures = new List<string>();
            var trackedSnapshots = new Dictionary<string, Phase7TrackedArtifactSnapshot>(StringComparer.Ordinal);
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
            JObject fullProductionRebuildPerformance = BuildDoubleDistribution(records
                .Select(record => record.Value<double?>("fullProductionRebuildMilliseconds") ?? 0d)
                .ToList());
            bool performancePassed =
                elapsedMilliseconds.Count == Phase7CollisionReviewSeedCount &&
                performance.Value<double>("p95Ms") <= Phase7MaximumRenderedExportP95Milliseconds &&
                performance.Value<double>("maxMs") <= Phase7MaximumRenderedExportSeedMilliseconds;
            bool everySeedPassed =
                records.Count == Phase7CollisionReviewSeedCount &&
                records.All(record => record.Value<bool?>("passed") == true);
            bool cleanupPassed = cleanupFailures.Count == 0;
            bool passed =
                measurementEnvironment?.Value<bool?>("passed") == true &&
                preparationFailures.Count == 0 &&
                everySeedPassed &&
                performancePassed &&
                cleanupPassed;

            return new JObject
            {
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["reportVersion"] = Phase7CollisionReportVersion,
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["passed"] = passed,
                ["seedCount"] = records.Count,
                ["requiredSeedCount"] = Phase7CollisionReviewSeedCount,
                ["everySeedPassed"] = everySeedPassed,
                ["performancePassed"] = performancePassed,
                ["cleanupPassed"] = cleanupPassed,
                ["exportWarmupPassed"] = preparationFailures.Count == 0,
                ["exportWarmupSeedCount"] = exportWarmupSeedCount,
                ["trackedArtifactSnapshotCount"] = trackedSnapshots.Count,
                ["measurementEnvironment"] = measurementEnvironment?.DeepClone(),
                ["selection"] = BuildPhase7SelectionToken(selections),
                ["performance"] = new JObject
                {
                    ["timingBoundary"] = "production plan/render through actual shared collision export",
                    ["clock"] = "System.Diagnostics.Stopwatch monotonic timestamp",
                    ["includedStages"] = new JArray(Phase7CollisionPerformanceStageNames),
                    ["excludedNonBudgetStages"] = new JArray(Phase7CollisionNonBudgetStageNames),
                    ["perSeedMilliseconds"] = performance,
                    ["maximumP95Milliseconds"] = Phase7MaximumRenderedExportP95Milliseconds,
                    ["maximumSeedMilliseconds"] = Phase7MaximumRenderedExportSeedMilliseconds,
                    ["stageMilliseconds"] = BuildPhase7CollisionStageDistributions(records),
                    ["fullProductionRebuildDiagnostic"] = new JObject
                    {
                        ["acceptanceMetric"] = false,
                        ["includesTemporarySceneSetupCameraLightingAndSceneAssetSaves"] = true,
                        ["perSeedMilliseconds"] = fullProductionRebuildPerformance
                    }
                },
                ["parityContract"] = new JObject
                {
                    ["productionCore"] = "RandomDungeonSceneBuilder.RebuildWithSeed",
                    ["exporter"] = "GameplayCollisionExporter.ExportActiveSceneSharedCollisionData",
                    ["sourceRule"] = "every enabled non-trigger rendered dungeon collider is supported and appears exactly once in movement and query payloads",
                    ["copyRule"] = "server and bundled client JSON are byte-identical independently for movement and query collision",
                    ["artifactRule"] = "unique temporary scene and collision outputs are absent after validation"
                },
                ["preparationFailureCodes"] = new JArray(preparationFailures),
                ["cleanupFailureCodes"] = new JArray(cleanupFailures),
                ["records"] = records
            };
        }

        private static JObject BuildPhase7CollisionStageDistributions(JArray records)
        {
            var stageNames = new SortedSet<string>(StringComparer.Ordinal);
            foreach (JToken record in records)
            {
                if (record["stageMilliseconds"] is not JObject stages)
                    continue;
                foreach (JProperty property in stages.Properties())
                    stageNames.Add(property.Name);
            }

            var result = new JObject();
            foreach (string stageName in stageNames)
            {
                var values = new List<double>();
                foreach (JToken record in records)
                {
                    double? value = record["stageMilliseconds"]?.Value<double?>(stageName);
                    if (value.HasValue)
                        values.Add(value.Value);
                }

                result[stageName] = BuildDoubleDistribution(values);
            }

            return result;
        }

        private static double SumPhase7CollisionPerformanceStages(
            IReadOnlyDictionary<string, double> stageMilliseconds)
        {
            double total = 0d;
            foreach (string stage in Phase7CollisionPerformanceStageNames)
            {
                if (stageMilliseconds.TryGetValue(stage, out double value))
                    total += value;
            }

            return total;
        }

        private static int WarmPhase7CollisionExportProcess(
            IReadOnlyList<Phase7ReviewSelection> selections,
            string sceneAssetPath,
            string dataKey,
            IDictionary<string, Phase7TrackedArtifactSnapshot> trackedSnapshots)
        {
            int completed = 0;
            foreach (Phase7ReviewSelection selection in selections)
            {
                var errorLogs = new List<string>();
                void CaptureLog(string condition, string stackTrace, LogType type)
                {
                    if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                        errorLogs.Add(condition);
                }

                Application.logMessageReceived += CaptureLog;
                try
                {
                    RandomDungeonSceneBuilder.RebuildWithSeedForValidation(
                        selection.seed,
                        sceneAssetPath,
                        dataKey,
                        modelPath => CapturePhase7TrackedArtifact($"{modelPath}.meta", trackedSnapshots));
                }
                finally
                {
                    Application.logMessageReceived -= CaptureLog;
                }

                if (errorLogs.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Collision export warm-up seed {selection.seed} logged {errorLogs.Count} error(s): " +
                        string.Join(" | ", errorLogs.Distinct(StringComparer.Ordinal).Take(3)));
                }

                completed++;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return completed;
        }

        private static JObject ValidatePhase7CollisionExportSeed(
            Phase7ReviewSelection selection,
            string sceneAssetPath,
            string dataKey,
            string serverMovementPath,
            string serverQueryPath,
            string bundledMovementPath,
            string bundledQueryPath,
            IDictionary<string, Phase7TrackedArtifactSnapshot> trackedSnapshots,
            out double elapsedMs)
        {
            elapsedMs = 0d;
            double fullProductionRebuildMilliseconds = 0d;
            var failureCodes = new HashSet<string>(StringComparer.Ordinal);
            var errorLogs = new List<string>();
            var warningLogs = new List<string>();
            string currentCanonicalHash = string.Empty;
            int sourceColliderCount = 0;
            int sourceBoxCount = 0;
            int sourceMeshCount = 0;
            int unsupportedColliderCount = 0;
            bool sceneSaved = false;
            bool metadataSeedMatches = false;
            JObject movementSummary = null;
            JObject querySummary = null;
            string movementHash = string.Empty;
            string queryHash = string.Empty;
            bool movementCopiesMatch = false;
            bool queryCopiesMatch = false;
            var measuredImporterMutations = new HashSet<string>(StringComparer.Ordinal);
            var stageMilliseconds = new Dictionary<string, double>(StringComparer.Ordinal);

            try
            {
                JObject planReport = BuildPhase0SeedReport(selection.seed);
                currentCanonicalHash = planReport["hashes"]?.Value<string>("canonical") ?? string.Empty;
                if (planReport.Value<bool?>("accepted") != true)
                    failureCodes.Add("PLAN_NOT_ACCEPTED");
                if (planReport["validation"]?.Value<bool?>("passed") != true)
                    failureCodes.Add("PLAN_NOT_HARD_VALID");
                if (!string.Equals(
                        currentCanonicalHash,
                        selection.expectedCanonicalHash,
                        StringComparison.Ordinal))
                {
                    failureCodes.Add("CANONICAL_HASH_MISMATCH");
                }

                void CaptureLog(string condition, string stackTrace, LogType type)
                {
                    if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                        errorLogs.Add(condition);
                    else if (type == LogType.Warning)
                        warningLogs.Add(condition);
                }

                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                Application.logMessageReceived += CaptureLog;
                try
                {
                    RandomDungeonSceneBuilder.RebuildWithSeedForValidation(
                        selection.seed,
                        sceneAssetPath,
                        dataKey,
                        modelPath =>
                        {
                            measuredImporterMutations.Add(modelPath);
                            CapturePhase7TrackedArtifact($"{modelPath}.meta", trackedSnapshots);
                        },
                        (stage, milliseconds) =>
                        {
                            stageMilliseconds.TryGetValue(stage, out double existing);
                            stageMilliseconds[stage] = existing + milliseconds;
                        });
                }
                finally
                {
                    Application.logMessageReceived -= CaptureLog;
                    fullProductionRebuildMilliseconds = ElapsedMilliseconds(
                        start,
                        System.Diagnostics.Stopwatch.GetTimestamp());
                }

                if (errorLogs.Count > 0)
                    failureCodes.Add("EXPORT_LOG_ERROR");
                if (measuredImporterMutations.Count > 0)
                    failureCodes.Add("MEASURED_IMPORT_OCCURRED");
                sceneSaved = File.Exists(sceneAssetPath);
                if (!sceneSaved)
                    failureCodes.Add("SCENE_NOT_SAVED");

                Scene activeScene = SceneManager.GetActiveScene();
                GameObject dungeonRoot = activeScene
                    .GetRootGameObjects()
                    .FirstOrDefault(root => string.Equals(root.name, "Generated Dungeon", StringComparison.Ordinal));
                if (dungeonRoot == null)
                {
                    failureCodes.Add("SCENE_ROOT_MISSING");
                }
                else
                {
                    List<Collider> colliders = dungeonRoot
                        .GetComponentsInChildren<Collider>(includeInactive: false)
                        .Where(collider => collider != null && collider.enabled && !collider.isTrigger)
                        .ToList();
                    sourceColliderCount = colliders.Count;
                    sourceBoxCount = colliders.Count(collider => collider is BoxCollider);
                    sourceMeshCount = colliders.Count(collider => collider is MeshCollider);
                    unsupportedColliderCount = sourceColliderCount - sourceBoxCount - sourceMeshCount;
                    if (sourceColliderCount == 0)
                        failureCodes.Add("EMPTY_COLLISION_SOURCE");
                    if (unsupportedColliderCount != 0)
                        failureCodes.Add("UNSUPPORTED_COLLIDER_SOURCE");

                    List<string> sourceNames = colliders
                        .Where(collider => collider is BoxCollider || collider is MeshCollider)
                        .Select(collider => Phase7CollisionHierarchyPath(collider.transform))
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToList();
                    movementSummary = ValidatePhase7ExportPayload(
                        serverMovementPath,
                        bundledMovementPath,
                        sourceNames,
                        "MOVEMENT",
                        failureCodes,
                        out movementHash,
                        out movementCopiesMatch);
                    querySummary = ValidatePhase7ExportPayload(
                        serverQueryPath,
                        bundledQueryPath,
                        sourceNames,
                        "QUERY",
                        failureCodes,
                        out queryHash,
                        out queryCopiesMatch);
                }

                metadataSeedMatches = activeScene
                    .GetRootGameObjects()
                    .Any(root => string.Equals(
                        root.name,
                        $"Random Dungeon Seed {selection.seed}",
                        StringComparison.Ordinal));
                if (!metadataSeedMatches)
                    failureCodes.Add("SCENE_METADATA_SEED_MISMATCH");
            }
            catch (Exception exception)
            {
                failureCodes.Add("REBUILD_OR_EXPORT_EXCEPTION");
                errorLogs.Add($"{exception.GetType().Name}: {exception.Message}");
            }

            elapsedMs = SumPhase7CollisionPerformanceStages(stageMilliseconds);

            return new JObject
            {
                ["seed"] = selection.seed,
                ["source"] = selection.source,
                ["patternId"] = selection.patternId,
                ["selectionSlot"] = selection.selectionSlot,
                ["passed"] = failureCodes.Count == 0,
                ["failureCodes"] = new JArray(failureCodes.OrderBy(code => code, StringComparer.Ordinal)),
                ["expectedCanonicalHash"] = selection.expectedCanonicalHash,
                ["currentCanonicalHash"] = currentCanonicalHash,
                ["elapsedMilliseconds"] = elapsedMs,
                ["fullProductionRebuildMilliseconds"] = fullProductionRebuildMilliseconds,
                ["stageMilliseconds"] = new JObject(stageMilliseconds
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new JProperty(entry.Key, entry.Value))),
                ["measuredImporterMutations"] = new JArray(
                    measuredImporterMutations.OrderBy(path => path, StringComparer.Ordinal)),
                ["sceneSaved"] = sceneSaved,
                ["metadataSeedMatches"] = metadataSeedMatches,
                ["sourceCollision"] = new JObject
                {
                    ["colliders"] = sourceColliderCount,
                    ["boxes"] = sourceBoxCount,
                    ["meshes"] = sourceMeshCount,
                    ["unsupported"] = unsupportedColliderCount
                },
                ["movementExport"] = movementSummary,
                ["queryExport"] = querySummary,
                ["movementServerBundledCopiesMatch"] = movementCopiesMatch,
                ["queryServerBundledCopiesMatch"] = queryCopiesMatch,
                ["movementPayloadHash"] = movementHash,
                ["queryPayloadHash"] = queryHash,
                ["errorLogs"] = new JArray(errorLogs.Distinct(StringComparer.Ordinal)),
                ["warningLogs"] = new JArray(warningLogs.Distinct(StringComparer.Ordinal))
            };
        }

        private static JObject ValidatePhase7ExportPayload(
            string serverPath,
            string bundledPath,
            IReadOnlyList<string> expectedSourceNames,
            string codePrefix,
            ISet<string> failureCodes,
            out string payloadHash,
            out bool copiesMatch)
        {
            payloadHash = string.Empty;
            copiesMatch = false;
            if (!File.Exists(serverPath) || !File.Exists(bundledPath))
            {
                failureCodes.Add($"{codePrefix}_EXPORT_MISSING");
                return new JObject();
            }

            string serverJson = File.ReadAllText(serverPath);
            string bundledJson = File.ReadAllText(bundledPath);
            payloadHash = ComputeSha256(serverJson);
            copiesMatch = string.Equals(serverJson, bundledJson, StringComparison.Ordinal);
            if (!copiesMatch)
                failureCodes.Add($"{codePrefix}_SERVER_BUNDLED_MISMATCH");

            JObject payload;
            try
            {
                payload = JObject.Parse(serverJson);
            }
            catch (Exception)
            {
                failureCodes.Add($"{codePrefix}_EXPORT_INVALID_JSON");
                return new JObject();
            }

            JArray boxes = payload["boxes"] as JArray ?? new JArray();
            JArray geometries = payload["mesh_geometries"] as JArray ?? new JArray();
            JArray instances = payload["mesh_instances"] as JArray ?? new JArray();
            int version = payload.Value<int?>("version") ?? 0;
            if (version != 1)
                failureCodes.Add($"{codePrefix}_EXPORT_VERSION_MISMATCH");
            bool sourceNamesMatch = Phase7ExportSourceNamesMatch(
                expectedSourceNames,
                boxes,
                instances);
            if (!sourceNamesMatch)
                failureCodes.Add($"{codePrefix}_SOURCE_PARITY_MISMATCH");
            if (boxes.Count + instances.Count == 0)
                failureCodes.Add($"{codePrefix}_EXPORT_EMPTY");

            return new JObject
            {
                ["version"] = version,
                ["boxes"] = boxes.Count,
                ["meshGeometries"] = geometries.Count,
                ["meshInstances"] = instances.Count,
                ["sourceNamesMatch"] = sourceNamesMatch,
                ["payloadHash"] = payloadHash
            };
        }

        private static bool Phase7ExportSourceNamesMatch(
            IReadOnlyList<string> expectedSourceNames,
            IEnumerable<JToken> boxes,
            IEnumerable<JToken> instances)
        {
            List<string> exportedNames = boxes
                .Concat(instances)
                .Select(token => token.Value<string>("name") ?? string.Empty)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            return expectedSourceNames.SequenceEqual(exportedNames);
        }

        private static JArray BuildPhase7SelectionToken(
            IEnumerable<Phase7ReviewSelection> selections)
        {
            return new JArray(selections.Select(selection => new JObject
            {
                ["seed"] = selection.seed,
                ["source"] = selection.source,
                ["patternId"] = selection.patternId,
                ["selectionSlot"] = selection.selectionSlot,
                ["expectedCanonicalHash"] = selection.expectedCanonicalHash,
                ["transitionCount"] = selection.transitionCount >= 0
                    ? JToken.FromObject(selection.transitionCount)
                    : JValue.CreateNull(),
                ["visibleDistantRoomProxyCount"] = selection.visibilityCount >= 0
                    ? JToken.FromObject(selection.visibilityCount)
                    : JValue.CreateNull(),
                ["hasNamedPromontory"] = selection.hasNamedPromontory
            }));
        }

        private static void RequireAbsentPhase7TemporaryArtifacts(params string[] paths)
        {
            string existing = paths.FirstOrDefault(path =>
                File.Exists(path) ||
                (!Path.IsPathRooted(path) && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null));
            if (!string.IsNullOrEmpty(existing))
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite pre-existing Phase 7 validation artifact '{existing}'.");
            }
        }

        private static void CapturePhase7TrackedArtifact(
            string path,
            IDictionary<string, Phase7TrackedArtifactSnapshot> snapshots)
        {
            string normalized = path.Replace('\\', '/');
            if (snapshots.ContainsKey(normalized))
                return;

            bool existed = File.Exists(normalized);
            snapshots.Add(normalized, new Phase7TrackedArtifactSnapshot
            {
                path = normalized,
                existed = existed,
                bytes = existed ? File.ReadAllBytes(normalized) : Array.Empty<byte>()
            });
        }

        private static void RestorePhase7TrackedArtifacts(
            IReadOnlyDictionary<string, Phase7TrackedArtifactSnapshot> snapshots,
            ICollection<string> cleanupFailures)
        {
            foreach (Phase7TrackedArtifactSnapshot snapshot in snapshots.Values
                         .OrderBy(value => value.path, StringComparer.Ordinal))
            {
                try
                {
                    if (snapshot.existed)
                    {
                        File.WriteAllBytes(snapshot.path, snapshot.bytes);
                    }
                    else if (File.Exists(snapshot.path))
                    {
                        File.Delete(snapshot.path);
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(
                        $"TRACKED_ARTIFACT_RESTORE_EXCEPTION:{snapshot.path}:{exception.GetType().Name}");
                }
            }

            foreach (Phase7TrackedArtifactSnapshot snapshot in snapshots.Values)
            {
                bool matches = snapshot.existed
                    ? File.Exists(snapshot.path) && File.ReadAllBytes(snapshot.path).SequenceEqual(snapshot.bytes)
                    : !File.Exists(snapshot.path);
                if (!matches)
                    cleanupFailures.Add($"TRACKED_ARTIFACT_RESTORE_MISMATCH:{snapshot.path}");
            }
        }

        private static void CleanupPhase7CollisionArtifacts(
            string sceneAssetPath,
            string serverMovementPath,
            string serverQueryPath,
            string bundledMovementPath,
            string bundledQueryPath,
            ICollection<string> cleanupFailures)
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add($"TEMP_SCENE_CLOSE:{exception.GetType().Name}");
            }

            DeletePhase7Asset(sceneAssetPath, cleanupFailures);
            DeletePhase7Asset(bundledMovementPath, cleanupFailures);
            DeletePhase7Asset(bundledQueryPath, cleanupFailures);
            DeletePhase7File(serverMovementPath, cleanupFailures);
            DeletePhase7File(serverQueryPath, cleanupFailures);
            AssetDatabase.Refresh();

            foreach (string path in new[]
                     {
                         sceneAssetPath,
                         serverMovementPath,
                         serverQueryPath,
                         bundledMovementPath,
                         bundledQueryPath
                     })
            {
                if (File.Exists(path))
                    cleanupFailures.Add($"TEMP_ARTIFACT_REMAINS:{path.Replace('\\', '/')}");
            }
        }

        private static void DeletePhase7Asset(string assetPath, ICollection<string> cleanupFailures)
        {
            try
            {
                if (File.Exists(assetPath) && !AssetDatabase.DeleteAsset(assetPath))
                    cleanupFailures.Add($"TEMP_ASSET_DELETE_FAILED:{assetPath}");
            }
            catch (Exception exception)
            {
                cleanupFailures.Add($"TEMP_ASSET_DELETE_EXCEPTION:{assetPath}:{exception.GetType().Name}");
            }
        }

        private static void DeletePhase7File(string path, ICollection<string> cleanupFailures)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add($"TEMP_FILE_DELETE_EXCEPTION:{path}:{exception.GetType().Name}");
            }
        }

        private static string Phase7CollisionHierarchyPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = $"{transform.name}/{path}";
            }

            return path;
        }

        private static string Phase7ServerCollisionPath(string dataKey, bool query)
        {
            return Path.Combine(
                "server",
                "src",
                "world_data",
                $"{dataKey}{(query ? ".query_collision" : ".collision")}.shared.json");
        }

        private static string Phase7BundledCollisionPath(string dataKey, bool query)
        {
            return Path.Combine(
                "Assets",
                "Arena",
                "Resources",
                "SharedData",
                "Worlds",
                $"{dataKey}{(query ? ".query_collision" : ".collision")}.shared.json");
        }

        private static string Phase7CollisionReportPath()
        {
            return Path.Combine(BatchReportDirectory, "phase7_collision_export_parity.json");
        }

        // Reflection entry point for focused selector and safety tests. It uses
        // only synthetic evidence and rejects production destinations before
        // any scene generation can begin.
        private static string BuildPhase7CollisionSupportSnapshot()
        {
            JObject sweep = BuildSyntheticPhase7CollisionSweep();
            JObject sentinels = BuildSyntheticPhase7SentinelManifest();
            List<Phase7ReviewSelection> selection =
                BuildPhase7CollisionReviewSelection(sweep, sentinels);
            bool sceneGuard = Phase7ValidationGuardRejects(
                RandomDungeonSceneBuilder.ScenePath,
                $"{Phase7CollisionTempDataPrefix}_guard");
            bool dataGuard = Phase7ValidationGuardRejects(
                $"Assets/Arena/Content/Scenes/OpenWorld/{Phase7CollisionTempScenePrefix}_guard.unity",
                RandomDungeonSceneBuilder.DataKey);
            var expectedNames = new List<string> { "Generated Dungeon/Floor", "Generated Dungeon/Wall" };
            var matchingBoxes = new JArray(new JObject { ["name"] = "Generated Dungeon/Wall" });
            var matchingMeshes = new JArray(new JObject { ["name"] = "Generated Dungeon/Floor" });
            bool exactNamesMatch = Phase7ExportSourceNamesMatch(
                expectedNames,
                matchingBoxes,
                matchingMeshes);
            bool missingNameDetected = !Phase7ExportSourceNamesMatch(
                expectedNames,
                matchingBoxes,
                new JArray());
            return string.Join("\n", new[]
            {
                $"selection.count={selection.Count}",
                $"selection.unique={selection.Select(item => item.seed).Distinct().Count()}",
                $"selection.sentinels={selection.Count(item => item.source == "historical-sentinel")}",
                $"selection.processional={selection.Count(item => item.source == "phase7-sweep" && item.patternId == Phase1PatternId)}",
                $"selection.atrium={selection.Count(item => item.source == "phase7-sweep" && item.patternId == AtriumRingPatternId)}",
                $"selection.twinWing={selection.Count(item => item.source == "phase7-sweep" && item.patternId == TwinWingPatternId)}",
                $"selection.slots={selection.Count(item => item.source == "phase7-sweep" && !string.IsNullOrEmpty(item.selectionSlot))}",
                $"guard.productionScene={sceneGuard}",
                $"guard.productionData={dataGuard}",
                $"parity.exactNames={exactNamesMatch}",
                $"parity.missingDetected={missingNameDetected}",
                $"performance.includedStageCount={Phase7CollisionPerformanceStageNames.Length}",
                $"performance.excludedStageCount={Phase7CollisionNonBudgetStageNames.Length}",
                $"performance.includesPlanAndRender={Phase7CollisionPerformanceStageNames.Contains("planAndRender")}",
                $"performance.includesExport={Phase7CollisionPerformanceStageNames.Contains("exportSharedCollision")}",
                $"performance.excludesSceneSaves={Phase7CollisionNonBudgetStageNames.Contains("saveSceneBeforeExport") && Phase7CollisionNonBudgetStageNames.Contains("saveSceneAndAssetsAfterExport")}",
                $"performance.p95Ms={Phase7MaximumRenderedExportP95Milliseconds}",
                $"performance.maxMs={Phase7MaximumRenderedExportSeedMilliseconds}",
                $"report.version={Phase7CollisionReportVersion}"
            });
        }

        private static bool Phase7ValidationGuardRejects(string scenePath, string dataKey)
        {
            try
            {
                RandomDungeonSceneBuilder.RebuildWithSeedForValidation(0, scenePath, dataKey);
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private static JObject BuildSyntheticPhase7CollisionSweep()
        {
            var seeds = new JArray();
            int seed = Phase7FirstSeed;
            foreach (string pattern in Phase7ReviewPatternOrder)
            {
                for (int i = 0; i < 12; i++)
                {
                    seeds.Add(new JObject
                    {
                        ["seed"] = seed++,
                        ["accepted"] = true,
                        ["validation"] = new JObject { ["passed"] = true },
                        ["routeIntent"] = new JObject { ["patternId"] = pattern },
                        ["tieredLevelPlan"] = new JObject
                        {
                            ["transitionCount"] = 10 + i,
                            ["visibleDistantRoomProxyCount"] = i % 6
                        },
                        ["namedPromontories"] = i % 2 == 0
                            ? new JArray(new JObject { ["id"] = "promontory" })
                            : new JArray(),
                        ["hashes"] = new JObject
                        {
                            ["canonical"] = ComputeSha256($"{pattern}:{i}")
                        }
                    });
                }
            }

            return new JObject { ["seeds"] = seeds };
        }

        private static JObject BuildSyntheticPhase7SentinelManifest()
        {
            return new JObject
            {
                ["sentinels"] = new JArray(Phase0VisualSentinels.Select(sentinel => new JObject
                {
                    ["seed"] = sentinel.seed,
                    ["category"] = sentinel.category,
                    ["canonicalHash"] = ComputeSha256($"sentinel:{sentinel.seed}")
                }))
            };
        }
    }
}

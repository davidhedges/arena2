using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Plastic.Newtonsoft.Json;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // Canonical diagnostic projections. This file records evidence and never
    // participates in generation.
    internal sealed partial class DungeonLabGenerator
    {
        private const string BatchReportDirectory = "DungeonLabReports";
        private const string DungeonPlanSummaryVersion = "dungeon-plan-v10";
        private const int Phase0BaselineFirstSeed = 2026072100;
        private const int Phase0BaselineSeedCount = 200;
        private const int LockedSeedCount = 100;
        private const int Phase3HardValidCompletionFloor = 190;
        private const string Phase6cLockedResultHash =
            "f7462647e9f079ef8a72b3c8f9f88f2ce939978ffa7125eb0b9081f4e1ab76f8";
        private const int Phase0SentinelImageWidth = 1600;
        private const int Phase0SentinelImageHeight = 900;

        // Six and only six visual sentinels. Their lightweight annotations are
        // characterization notes, not an aesthetic taxonomy or acceptance gate.
        private static readonly (int seed, string category, string annotation)[] Phase0VisualSentinels =
        {
            (2026072140, "representative-a", "Phase 0 representative selection retained for cross-phase visual comparison."),
            (2026072186, "representative-b", "Phase 0 alternate representative selection retained for cross-phase visual comparison."),
            (2026072169, "weak-a", "Phase 0 weak selection retained to expose cross-phase readability regressions."),
            (2026072245, "weak-b", "Phase 0 second weak selection retained to expose cross-phase readability regressions."),
            (2026072262, "edge-a", "Phase 0 transition-count edge selection retained for cross-phase comparison."),
            (2026072223, "edge-b", "Phase 0 elevation-span edge selection retained for cross-phase comparison.")
        };

        private static readonly Dictionary<string, string> Phase0CatalogDigestCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static string ActiveDiagnosticSummaryVersion => DungeonPlanSummaryVersion;

        private static string ActiveDiagnosticGeneratorVersion => RoutePlannerVersion;

        private static JObject BuildGenerationSettingsValues(DungeonGenerationSettings settings)
        {
            var values = new JObject();
            foreach (FieldInfo field in typeof(DungeonGenerationSettings)
                         .GetFields(BindingFlags.Instance | BindingFlags.Public)
                         .OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                object value = field.GetValue(settings);
                values[field.Name] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }

            return values;
        }

        private static string GenerationSettingsDigest(DungeonGenerationSettings settings)
        {
            return ComputeSha256(BuildGenerationSettingsValues(settings).ToString(Formatting.None));
        }

        private static JObject BuildGenerationSettingsIdentity(DungeonGenerationSettings settings)
        {
            JObject values = BuildGenerationSettingsValues(settings);
            return new JObject
            {
                ["profileId"] = settings.profileName,
                ["digestAlgorithm"] = "SHA-256 over settings.values with ordinal field ordering",
                ["digest"] = ComputeSha256(values.ToString(Formatting.None)),
                ["values"] = values
            };
        }

        private static void AddGenerationSettingsIdentity(JObject report)
        {
            JObject identity = BuildGenerationSettingsIdentity(CurrentGenerationSettings);
            report["profile"] = identity.Value<string>("profileId");
            report["settingsDigest"] = identity.Value<string>("digest");
            report["settings"] = identity;
        }

        [MenuItem("Tools/Dungeon Lab/Batch Validate (50 Fixed Seeds)")]
        public static void BatchValidate50Seeds()
        {
            RunBatchValidation(Phase0BaselineFirstSeed, 50);
        }

        [MenuItem("Tools/Dungeon Lab/Batch Validate (200 Fixed Seeds)")]
        public static void BatchValidate200Seeds()
        {
            RunBatchValidation(Phase0BaselineFirstSeed, Phase0BaselineSeedCount);
        }

        [MenuItem("Tools/Dungeon Lab/Batch Validate (100 Locked Seeds)")]
        public static void BatchValidateLocked100Seeds()
        {
            RunBatchValidation(Phase0BaselineFirstSeed, LockedSeedCount);
        }

        [MenuItem("Tools/Dungeon Lab/Capture Visual Sentinels")]
        public static void CaptureVisualSentinels()
        {
            CaptureVisualSentinels("visual_sentinels");
        }

        private static void CaptureVisualSentinels(string directoryName)
        {
            string directory = Path.Combine(BatchReportDirectory, directoryName);
            Directory.CreateDirectory(directory);
            var manifestEntries = new JArray();

            try
            {
                foreach ((int seed, string category, string annotation) sentinel in Phase0VisualSentinels)
                {
                    GameObject root = null;
                    try
                    {
                        root = BuildPhase0RenderedSeed(
                            sentinel.seed,
                            out Bounds bounds,
                            out JObject seedReport,
                            out ElevationEdgeModel.BuildReport buildReport);
                        string fileName = $"{sentinel.seed}_{sentinel.category}.png";
                        string path = Path.Combine(directory, fileName);
                        CapturePhase0SentinelImage(bounds, path);
                        manifestEntries.Add(new JObject
                        {
                            ["seed"] = sentinel.seed,
                            ["category"] = sentinel.category,
                            ["annotation"] = sentinel.annotation,
                            ["image"] = path.Replace('\\', '/'),
                            ["canonicalHash"] = seedReport["hashes"]?["canonical"],
                            ["measurements"] = seedReport["measurements"]?.DeepClone(),
                            ["rendererSummary"] = buildReport.Summary
                        });
                    }
                    finally
                    {
                        if (root != null)
                        {
                            DestroyImmediate(root);
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var manifest = new JObject
            {
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["captureWidth"] = Phase0SentinelImageWidth,
                ["captureHeight"] = Phase0SentinelImageHeight,
                ["sentinels"] = manifestEntries
            };
            AddGenerationSettingsIdentity(manifest);
            string manifestPath = Path.Combine(directory, "manifest.json");
            File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented));
            Debug.Log($"Dungeon Lab: visual sentinels written to {directory} (manifest {manifestPath}).");
        }

        private static string RunBatchValidation(
            int firstSeed,
            int requestedSeedCount,
            int phase7SweepOrdinal = 0,
            int correctiveRunOrdinal = 0)
        {
            string profileId = ResolveRequestedGenerationProfileId();
            CurrentGenerationSettings = LoadActiveGenerationSettings(profileId);
            Phase7BatchEvidence phase7Evidence = phase7SweepOrdinal > 0
                ? new Phase7BatchEvidence(phase7SweepOrdinal, Phase7WarmupSeedCount)
                : null;
            CorrectiveBatchEvidence correctiveEvidence = correctiveRunOrdinal > 0
                ? new CorrectiveBatchEvidence(correctiveRunOrdinal)
                : null;
            var rejectionHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            var rejectionCodeHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            var validationFailureCodeHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            var archetypeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var selectedPatternCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var acceptedPatternCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var tierSpanCounts = new SortedDictionary<int, int>();
            var correlations = new List<float>();
            var failedSeeds = new List<int>();
            var seedReports = new JArray();
            var allAttemptCounts = new List<int>();
            var acceptedAttemptCounts = new List<int>();
            var routeRoomCounts = new List<int>();
            var branchNodeCounts = new List<int>();
            var loopEdgeCounts = new List<int>();
            var elevationSpans = new List<int>();
            var transitionCounts = new List<int>();
            var visibleDistantRoomProxyCounts = new List<int>();
            var routeClimbCounts = new List<int>();
            int successCount = 0;
            int hardValidCount = 0;
            int routeRequirementsValidCount = 0;
            int finalVistaValidCount = 0;
            int recipeSetValidCount = 0;
            int completedSeedCount = 0;
            long measuredLoopStart = phase7Evidence == null && correctiveEvidence == null
                ? 0L
                : System.Diagnostics.Stopwatch.GetTimestamp();

            try
            {
                for (int i = 0; i < requestedSeedCount; i++)
                {
                    int seed = firstSeed + i;
                    string selectedPattern = SelectedRoutePatternId(seed);
                    selectedPatternCounts.TryGetValue(selectedPattern, out int selectedPatternCount);
                    selectedPatternCounts[selectedPattern] = selectedPatternCount + 1;
                    if (!Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar(
                            "Dungeon Lab Batch Validate",
                            $"Seed {seed} ({i + 1}/{requestedSeedCount})",
                            (float)i / requestedSeedCount))
                    {
                        break;
                    }

                    long seedTimingStart = phase7Evidence == null && correctiveEvidence == null
                        ? 0L
                        : System.Diagnostics.Stopwatch.GetTimestamp();
                    JObject seedReport = BuildPhase0SeedReport(seed, profileId);
                    if (phase7Evidence != null)
                    {
                        phase7Evidence.planningMilliseconds.Add(
                            ElapsedMilliseconds(seedTimingStart, System.Diagnostics.Stopwatch.GetTimestamp()));
                    }
                    if (correctiveEvidence != null)
                    {
                        correctiveEvidence.planningMilliseconds.Add(
                            ElapsedMilliseconds(seedTimingStart, System.Diagnostics.Stopwatch.GetTimestamp()));
                    }
                    seedReports.Add(seedReport);
                    completedSeedCount++;
                    int layoutAttempts = seedReport.Value<int?>("layoutAttempts") ?? 0;
                    allAttemptCounts.Add(layoutAttempts);
                    MergeJsonHistogram(seedReport["rejectionHistogram"] as JObject, rejectionHistogram);
                    MergeJsonHistogram(seedReport["rejectionCodes"] as JObject, rejectionCodeHistogram);

                    if (seedReport.Value<bool?>("accepted") != true)
                    {
                        failedSeeds.Add(seed);
                        continue;
                    }

                    successCount++;
                    acceptedPatternCounts.TryGetValue(selectedPattern, out int acceptedPatternCount);
                    acceptedPatternCounts[selectedPattern] = acceptedPatternCount + 1;
                    acceptedAttemptCounts.Add(layoutAttempts);
                    if (seedReport["validation"]?.Value<bool?>("passed") == true)
                    {
                        hardValidCount++;
                    }
                    else
                    {
                        MergeJsonCodeArray(
                            seedReport["validation"]?["failureCodes"] as JArray,
                            validationFailureCodeHistogram);
                    }

                    JObject layoutSummary = (JObject)seedReport["layout"];
                    JObject planSummary = (JObject)seedReport["tieredLevelPlan"];
                    JObject graphSummary = (JObject)layoutSummary["graph"];
                    string archetype = planSummary.Value<string>("archetype") ?? "unknown";
                    archetypeCounts.TryGetValue(archetype, out int archetypeCount);
                    archetypeCounts[archetype] = archetypeCount + 1;
                    int tierSpan = planSummary.Value<int>("elevationSpan");
                    tierSpanCounts.TryGetValue(tierSpan, out int tierSpanCount);
                    tierSpanCounts[tierSpan] = tierSpanCount + 1;
                    routeRoomCounts.Add(graphSummary.Value<int>("longestRootRouteRooms"));
                    branchNodeCounts.Add(graphSummary.Value<int>("branchNodes"));
                    loopEdgeCounts.Add(graphSummary.Value<int>("loopEdges"));
                    elevationSpans.Add(tierSpan);
                    transitionCounts.Add(planSummary.Value<int>("transitionCount"));
                    visibleDistantRoomProxyCounts.Add(planSummary.Value<int>("visibleDistantRoomProxyCount"));
                    JObject routeResolution = (JObject)seedReport["routeResolution"];
                    routeClimbCounts.Add(routeResolution.Value<int>("routeClimbLevels"));
                    if (routeResolution.Value<bool?>("requirementsSatisfied") == true)
                    {
                        routeRequirementsValidCount++;
                    }

                    if (routeResolution["vista"]?.Value<bool?>("finalValid") == true)
                    {
                        finalVistaValidCount++;
                    }

                    if (seedReport["validation"]?["recipes"]?.Value<bool?>("passed") == true &&
                        (seedReport["recipeResolutions"] as JArray)?.Count == 3)
                    {
                        recipeSetValidCount++;
                    }

                    JToken correlationToken = planSummary["depthLevelCorrelation"];
                    if (correlationToken != null && correlationToken.Type != JTokenType.Null)
                    {
                        correlations.Add(correlationToken.Value<float>());
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (phase7Evidence != null)
            {
                phase7Evidence.measuredLoopSeconds =
                    ElapsedMilliseconds(measuredLoopStart, System.Diagnostics.Stopwatch.GetTimestamp()) / 1000d;
            }
            if (correctiveEvidence != null)
            {
                correctiveEvidence.measuredLoopSeconds =
                    ElapsedMilliseconds(measuredLoopStart, System.Diagnostics.Stopwatch.GetTimestamp()) / 1000d;
            }

            if (completedSeedCount <= 0)
            {
                Debug.Log("Dungeon Lab: batch validation cancelled before any seeds ran.");
                return string.Empty;
            }

            string archetypeSummary = FormatCountSummary(archetypeCounts);
            string tierSpanSummary = FormatTierSpanSummary(tierSpanCounts);
            string correlationSummary = FormatCorrelationSummary(correlations);
            string failedSummary = failedSeeds.Count == 0 ? "none" : string.Join(", ", failedSeeds);
            JObject attemptDistribution = BuildIntDistribution(allAttemptCounts);
            Debug.Log(
                "Dungeon Lab BATCH_VALIDATION " +
                $"range={firstSeed}..{firstSeed + completedSeedCount - 1}; seeds={completedSeedCount}; " +
                $"accepted={successCount}; failed={failedSeeds.Count}; hardValid={hardValidCount}; " +
                $"routeRequirementsValid={routeRequirementsValidCount}; finalVistasValid={finalVistaValidCount}; recipeSetsValid={recipeSetValidCount}; " +
                $"meanLayoutAttempts={attemptDistribution.Value<double>("mean"):0.##}; " +
                $"p95LayoutAttempts={attemptDistribution.Value<int>("p95")}; maxLayoutAttempts={attemptDistribution.Value<int>("max")}; " +
                $"archetypes={archetypeSummary}; tierSpans={tierSpanSummary}; " +
                $"depthLevelCorrelation={correlationSummary}; failedSeeds={failedSummary}; " +
                $"rejectionCodes={FormatRejectionHistogram(rejectionCodeHistogram)}; " +
                $"validationFailureCodes={FormatRejectionHistogram(validationFailureCodeHistogram)}");

            string reportPath = WritePhase0BatchReport(
                firstSeed,
                completedSeedCount,
                successCount,
                hardValidCount,
                rejectionHistogram,
                rejectionCodeHistogram,
                validationFailureCodeHistogram,
                archetypeCounts,
                selectedPatternCounts,
                acceptedPatternCounts,
                correlations,
                allAttemptCounts,
                acceptedAttemptCounts,
                routeRoomCounts,
                branchNodeCounts,
                loopEdgeCounts,
                elevationSpans,
                transitionCounts,
                visibleDistantRoomProxyCounts,
                routeClimbCounts,
                routeRequirementsValidCount,
                finalVistaValidCount,
                recipeSetValidCount,
                seedReports,
                phase7Evidence,
                correctiveEvidence);
            Debug.Log($"Dungeon Lab: batch validation report written to {reportPath}");
            return reportPath;
        }

        private static JObject BuildPhase0SeedReport(int seed)
        {
            return BuildPhase0SeedReport(seed, ResolveRequestedGenerationProfileId());
        }

        private static JObject BuildPhase0SeedReport(int seed, string profileId)
        {
            long initializationStart = BeginPhase7OutlierStage();
            CurrentGenerationSettings = LoadActiveGenerationSettings(profileId);
            var rejectionHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            var random = new System.Random(seed);
            EndPhase7OutlierStage("settingsAndRandomInitialization", initializationStart);
            try
            {
                long acceptedPlanStart = BeginPhase7OutlierStage();
                bool accepted = TryBuildAcceptedPlan(
                    seed,
                    random,
                    rejectionHistogram,
                    out DungeonLayout layout,
                    out TieredLevelPlan plan,
                    out int layoutAttemptsUsed,
                    out string rejectionReason);
                EndPhase7OutlierStage("acceptedPlanTotal", acceptedPlanStart);
                if (accepted)
                {
                    long acceptedReportStart = BeginPhase7OutlierStage();
                    JObject acceptedReport = CreateAcceptedPhase0SeedReport(
                        seed,
                        layoutAttemptsUsed,
                        rejectionReason,
                        rejectionHistogram,
                        layout,
                        plan,
                        random);
                    EndPhase7OutlierStage("acceptedReportTotal", acceptedReportStart);
                    return acceptedReport;
                }

                long rejectedReportStart = BeginPhase7OutlierStage();
                JObject rejectedReport = CreateRejectedPhase0SeedReport(
                        seed,
                        layoutAttemptsUsed,
                        rejectionReason,
                        rejectionHistogram,
                        exception: null);
                EndPhase7OutlierStage("rejectedReportTotal", rejectedReportStart);
                return rejectedReport;
            }
            catch (Exception exception)
            {
                long exceptionReportStart = BeginPhase7OutlierStage();
                JObject exceptionReport = CreateRejectedPhase0SeedReport(
                    seed,
                    0,
                    exception.Message,
                    rejectionHistogram,
                    exception);
                EndPhase7OutlierStage("exceptionReportTotal", exceptionReportStart);
                return exceptionReport;
            }
        }

        // Focused corrective-item diagnostic used by EditMode tests. It stays on
        // the production resolver/report/renderer paths and does not create a
        // second planner or mutate project assets.
        private static string BuildCorrectiveConnectionSnapshot()
        {
            var lines = new List<string>
            {
                $"policy.version={ExternalConnectorPromontoryPolicyVersion}",
                $"versions.summary={ActiveDiagnosticSummaryVersion}",
                $"versions.generator={ActiveDiagnosticGeneratorVersion}"
            };

            var firstSeedByCount = new Dictionary<int, int>();
            for (int seed = 0; seed < 4096 && firstSeedByCount.Count < 4; seed++)
            {
                int count = ExternalConnectorDesiredCount(seed);
                if (!firstSeedByCount.ContainsKey(count))
                    firstSeedByCount[count] = seed;
            }

            for (int count = 1; count <= 4; count++)
            {
                int seed = firstSeedByCount[count];
                BuildCorrectiveResolverProbe(
                    seed,
                    excludeAllAnchors: false,
                    out bool resolved,
                    out ExternalConnectorPromontoryResolution[] resolutions,
                    out int beforeCells,
                    out int afterCells,
                    out string error);
                var directions = new HashSet<int>();
                foreach (ExternalConnectorPromontoryResolution resolution in resolutions)
                    directions.Add(resolution.direction);
                lines.Add($"resolver.{count}.seed={seed}");
                lines.Add($"resolver.{count}.resolved={resolved}");
                lines.Add($"resolver.{count}.count={resolutions.Length}");
                lines.Add($"resolver.{count}.uniqueDirections={directions.Count}");
                lines.Add($"resolver.{count}.addedCells={afterCells - beforeCells}");
                lines.Add($"resolver.{count}.error={error}");
            }

            int rejectionSeed = firstSeedByCount[4];
            BuildCorrectiveResolverProbe(
                rejectionSeed,
                excludeAllAnchors: true,
                out bool rejectedResolved,
                out _,
                out int rejectedBefore,
                out int rejectedAfter,
                out string rejectionError);
            lines.Add($"atomic.rejected={!rejectedResolved}");
            lines.Add($"atomic.unchanged={rejectedBefore == rejectedAfter}");
            lines.Add($"atomic.code={rejectionError.Contains($"[{ExternalConnectorRejectionCode}]")}");

            foreach (int seed in new[]
                     {
                         2026072100,
                         2026072101,
                         2026072103,
                         2026072170,
                         2026072220
                     })
            {
                JObject report = BuildPhase0SeedReport(seed);
                lines.Add($"production.{seed}.accepted={report.Value<bool?>("accepted") == true}");
                lines.Add($"production.{seed}.hardValid={report["validation"]?.Value<bool?>("passed") == true}");
                lines.Add($"production.{seed}.desired={ExternalConnectorDesiredCount(seed)}");
                lines.Add($"production.{seed}.count={(report["externalConnectors"] as JArray)?.Count ?? 0}");
                lines.Add($"production.{seed}.externalValid={report["validation"]?["externalConnectors"]?.Value<bool?>("passed") == true}");
                lines.Add($"production.{seed}.transitionHash={report["hashes"]?.Value<string>("existingTransitions")}");
                lines.Add($"production.{seed}.prechangePlanHash={report["hashes"]?.Value<string>("preCorrectiveTieredLevelPlan")}");
            }

            JObject renderer = JObject.Parse(BuildPhase0RendererProbeJson(2026072100));
            lines.Add($"renderer.accepted={renderer.Value<bool?>("accepted") == true}");
            lines.Add($"renderer.passed={renderer["renderer"]?.Value<bool?>("passed") == true}");
            lines.Add($"renderer.rejected={renderer["renderer"]?.Value<int?>("rejectedPlacements") ?? -1}");
            return string.Join("\n", lines);
        }

        private static void BuildCorrectiveResolverProbe(
            int seed,
            bool excludeAllAnchors,
            out bool resolved,
            out ExternalConnectorPromontoryResolution[] resolutions,
            out int beforeCells,
            out int afterCells,
            out string error)
        {
            RoomFootprint room = RoomFootprint.FromRect(new RectInt(-3, -3, 7, 7));
            var floorCells = new HashSet<Vector2Int>(room.cells);
            var levels = new Dictionary<Vector2Int, int>();
            foreach (Vector2Int cell in floorCells)
                levels[cell] = 4;
            var layout = new DungeonLayout(
                floorCells,
                new List<RoomFootprint> { room },
                new List<RoomConnection>());
            var protectedCells = new HashSet<Vector2Int>();
            if (excludeAllAnchors)
                protectedCells.UnionWith(floorCells);
            beforeCells = levels.Count;
            resolved = TryResolveExternalConnectorPromontories(
                seed,
                layout,
                levels,
                new List<ElevationEdgeModel.TransitionEdge>(),
                protectedCells,
                new HashSet<Vector2Int>(),
                new StairPlacementLedger(),
                Array.Empty<NamedVistaPromontoryResolution>(),
                out resolutions,
                out error);
            afterCells = levels.Count;
        }

        // Reflection entry point for the edit-mode characterization tests. The
        // returned JSON is a diagnostic projection, never a generation input.
        // Flat standard-library-only projection for the separate test assembly,
        // which intentionally has no compile-time dependency on Plastic's JSON DLL.
        private static string BuildDensityAdjacencySlice1Snapshot()
        {
            DungeonGenerationSettings spaciousSettings = LoadActiveGenerationSettings("spacious");
            DungeonGenerationSettings denseSettings = LoadActiveGenerationSettings("dense");
            JObject spaciousValues = BuildGenerationSettingsValues(spaciousSettings);
            JObject denseValues = BuildGenerationSettingsValues(denseSettings);
            spaciousValues.Remove("profileName");
            denseValues.Remove("profileName");

            JObject processional = BuildPhase0SeedReport(2026072100, "spacious");
            JObject processionalRepeat = BuildPhase0SeedReport(2026072100, "spacious");
            JObject atrium = BuildPhase0SeedReport(2026072101, "spacious");
            JObject twinWing = BuildPhase0SeedReport(2026072103, "spacious");
            var lines = new List<string>
            {
                $"profiles.spaciousId={spaciousSettings.profileName}",
                $"profiles.denseId={denseSettings.profileName}",
                $"profiles.spaciousDigest={GenerationSettingsDigest(spaciousSettings)}",
                $"profiles.denseDigest={GenerationSettingsDigest(denseSettings)}",
                $"profiles.digestDistinct={!string.Equals(GenerationSettingsDigest(spaciousSettings), GenerationSettingsDigest(denseSettings), StringComparison.Ordinal)}",
                $"profiles.behaviorValuesEqual={JToken.DeepEquals(spaciousValues, denseValues)}",
                $"profiles.valueCount={BuildGenerationSettingsValues(spaciousSettings).Properties().Count()}",
                $"processional.topology={processional["measurements"]?.Value<string>("topology")}",
                $"processional.degreeSamples={processional["measurements"]?["finalRoomDegreeDistribution"]?.Value<int?>("sampleCount") ?? -1}",
                $"processional.connectionLengthSamples={processional["measurements"]?["corridorEvidence"]?["perConnectionExteriorLengthDistribution"]?.Value<int?>("sampleCount") ?? -1}",
                $"processional.connectionCount={processional["layout"]?.Value<int?>("connections") ?? -1}",
                $"processional.sharedWallDoors={processional["measurements"]?["corridorEvidence"]?.Value<int?>("sharedWallDoorCount") ?? -1}",
                $"processional.reservedVistaCells={processional["measurements"]?["voidExtent"]?.Value<int?>("reservedVistaCellCount") ?? -1}",
                $"processional.atriumCenterVoidIsNull={processional["measurements"]?["voidExtent"]?["atriumCenterVoidCellCount"]?.Type == JTokenType.Null}",
                $"atrium.topology={atrium["measurements"]?.Value<string>("topology")}",
                $"atrium.centerVoidCells={atrium["measurements"]?["voidExtent"]?.Value<int?>("atriumCenterVoidCellCount") ?? -1}",
                $"twinWing.topology={twinWing["measurements"]?.Value<string>("topology")}",
                $"twinWing.atriumCenterVoidIsNull={twinWing["measurements"]?["voidExtent"]?["atriumCenterVoidCellCount"]?.Type == JTokenType.Null}",
                $"determinism.settingsDigest={string.Equals(processional.Value<string>("settingsDigest"), processionalRepeat.Value<string>("settingsDigest"), StringComparison.Ordinal)}",
                $"determinism.canonical={string.Equals(processional["hashes"]?.Value<string>("canonical"), processionalRepeat["hashes"]?.Value<string>("canonical"), StringComparison.Ordinal)}",
                $"determinism.measurements={JToken.DeepEquals(processional["measurements"], processionalRepeat["measurements"])}"
            };
            return string.Join("\n", lines);
        }

        // Slice 2 is verification only. This snapshot exercises the existing
        // connection, boundary, renderer, and collision-input seams without
        // adding a second generation path or changing production behavior.
        private static string BuildDensityAdjacencySlice2Snapshot()
        {
            const int twinWingSeed = 2026072103;
            CurrentGenerationSettings = LoadActiveGenerationSettings("spacious");

            var twinRandom = new System.Random(twinWingSeed);
            bool twinAccepted = TryBuildAcceptedPlan(
                twinWingSeed,
                twinRandom,
                new Dictionary<string, int>(StringComparer.Ordinal),
                out DungeonLayout twinLayout,
                out TieredLevelPlan twinPlan,
                out _,
                out string twinRejection);
            if (!twinAccepted)
            {
                throw new InvalidOperationException(
                    $"Twin-wing Slice 2 probe seed {twinWingSeed} was rejected: {twinRejection}");
            }

            int twinZeroExteriorConnections = 0;
            int twinZeroExteriorDoorways = 0;
            foreach (RoomConnection connection in twinLayout.connections)
            {
                RoomFootprint fromRoom = twinLayout.rooms[connection.fromRoom];
                RoomFootprint toRoom = twinLayout.rooms[connection.toRoom];
                int exteriorCells = connection.path.Count(cell =>
                    !fromRoom.Contains(cell) && !toRoom.Contains(cell));
                if (exteriorCells != 0)
                {
                    continue;
                }

                twinZeroExteriorConnections++;
                var isolatedLayout = new DungeonLayout(
                    twinLayout.floorCells,
                    twinLayout.rooms,
                    new List<RoomConnection> { connection });
                List<ElevationEdgeModel.DoorwayEdge> doorways =
                    BuildDoorwayEdges(isolatedLayout, twinPlan.cellLevels);
                if (doorways.Count == 1 &&
                    DoorwayJoinsRooms(doorways[0], fromRoom, toRoom))
                {
                    twinZeroExteriorDoorways++;
                }
            }

            JObject twinReport = BuildPhase0SeedReport(twinWingSeed, "spacious");

            var touchingRooms = new List<RoomFootprint>
            {
                RoomFootprint.FromRect(new RectInt(-2, -2, 5, 5)),
                RoomFootprint.FromRect(new RectInt(3, -2, 5, 5))
            };
            RouteIntent touchingIntent = BuildSlice2ConnectionIntent(
                touchingRooms.Count,
                new RouteTraversalIntent(
                    "touching-room-seam",
                    0,
                    1,
                    0,
                    RouteTransitionKind.LevelCorridor));
            bool touchingConnected = TryConnectProcessionalRooms(
                touchingIntent,
                touchingRooms,
                new[] { Vector2Int.zero, new Vector2Int(5, 0) },
                new HashSet<Vector2Int>(),
                Array.Empty<RecipePlacement>(),
                out HashSet<Vector2Int> touchingFloor,
                out List<RoomConnection> touchingConnections,
                out string touchingRejection);
            int touchingExteriorCells = touchingConnected
                ? touchingConnections[0].path.Count(cell =>
                    !touchingRooms[0].Contains(cell) && !touchingRooms[1].Contains(cell))
                : -1;

            bool touchingBoundaryBuilt = false;
            int touchingDoorways = -1;
            bool touchingDoorwayJoinsRooms = false;
            int rendererRejected = -1;
            int rendererDoorways = -1;
            int collisionSources = 0;
            int collisionMissingMeshes = 0;
            GameObject touchingRoot = null;
            if (touchingConnected)
            {
                var touchingLevels = touchingFloor.ToDictionary(cell => cell, _ => 0);
                var touchingLayout = new DungeonLayout(
                    touchingFloor,
                    touchingRooms,
                    touchingConnections);
                touchingBoundaryBuilt = TryBuildRoomBoundaryContext(
                    touchingLayout,
                    touchingLevels,
                    Array.Empty<ElevationEdgeModel.TransitionEdge>(),
                    new System.Random(17),
                    out ElevationEdgeModel.RoomBoundaryContext boundaryContext,
                    out _);
                touchingDoorways = boundaryContext?.doorwayEdges?.Count ?? -1;
                touchingDoorwayJoinsRooms = touchingDoorways == 1 &&
                    DoorwayJoinsRooms(
                        boundaryContext.doorwayEdges[0],
                        touchingRooms[0],
                        touchingRooms[1]);

                if (touchingBoundaryBuilt)
                {
                    try
                    {
                        touchingRoot = ElevationEdgeModel.BuildLevelField(
                            Vector3.zero,
                            touchingLevels,
                            Array.Empty<ElevationEdgeModel.TransitionEdge>(),
                            boundaryContext,
                            "Density Adjacency Slice 2 Probe",
                            out ElevationEdgeModel.BuildReport buildReport,
                            out _);
                        rendererRejected = buildReport.rejected;
                        rendererDoorways = buildReport.doorways;
                        foreach (Collider collider in touchingRoot.GetComponentsInChildren<Collider>(includeInactive: false))
                        {
                            if (collider == null || !collider.enabled || collider.isTrigger)
                            {
                                continue;
                            }

                            collisionSources++;
                            if (collider is MeshCollider meshCollider && meshCollider.sharedMesh == null)
                            {
                                collisionMissingMeshes++;
                            }
                        }
                    }
                    finally
                    {
                        if (touchingRoot != null)
                        {
                            DestroyImmediate(touchingRoot);
                        }
                    }
                }
            }

            // The logical centers (0,0), (7,0), and (0,7) form a cardinal
            // junction. Biasing only the central dominant rect one cell east
            // keeps the immutable logical anchor inside the room. Slice 5's
            // compiler must connect both edges from that anchor even though
            // RoomFootprint.Center moved to (1,0).
            var biasedJunctionRooms = new List<RoomFootprint>
            {
                RoomFootprint.FromRect(new RectInt(-1, -2, 5, 5)),
                RoomFootprint.FromRect(new RectInt(5, -2, 5, 5)),
                RoomFootprint.FromRect(new RectInt(-2, 5, 5, 5))
            };
            RouteIntent biasedJunctionIntent = BuildSlice2ConnectionIntent(
                biasedJunctionRooms.Count,
                new RouteTraversalIntent(
                    "junction-north",
                    0,
                    2,
                    0,
                    RouteTransitionKind.LevelCorridor),
                new RouteTraversalIntent(
                    "junction-east",
                    0,
                    1,
                    0,
                    RouteTransitionKind.LevelCorridor));
            bool biasedJunctionConnected = TryConnectProcessionalRooms(
                biasedJunctionIntent,
                biasedJunctionRooms,
                new[] { Vector2Int.zero, new Vector2Int(7, 0), new Vector2Int(0, 7) },
                new HashSet<Vector2Int>(),
                Array.Empty<RecipePlacement>(),
                out _,
                out List<RoomConnection> biasedJunctionConnections,
                out string biasedJunctionRejection);

            var lines = new List<string>
            {
                $"twin.seed={twinWingSeed}",
                $"twin.accepted={twinReport.Value<bool>("accepted")}",
                $"twin.topology={twinReport["measurements"]?.Value<string>("topology")}",
                $"twin.validation={twinReport["validation"]?.Value<bool>("passed")}",
                $"twin.zeroExteriorConnections={twinZeroExteriorConnections}",
                $"twin.zeroExteriorDoorways={twinZeroExteriorDoorways}",
                $"touching.connected={touchingConnected}",
                $"touching.rejection={touchingRejection}",
                $"touching.pathCells={(touchingConnected ? touchingConnections[0].path.Count : -1)}",
                $"touching.exteriorCells={touchingExteriorCells}",
                $"touching.boundaryBuilt={touchingBoundaryBuilt}",
                $"touching.doorways={touchingDoorways}",
                $"touching.doorwayJoinsRooms={touchingDoorwayJoinsRooms}",
                $"touching.rendererRejected={rendererRejected}",
                $"touching.rendererDoorways={rendererDoorways}",
                $"touching.collisionSources={collisionSources}",
                $"touching.collisionMissingMeshes={collisionMissingMeshes}",
                $"junction.logicalAnchorInsideBiasedRoom={biasedJunctionRooms[0].Contains(Vector2Int.zero)}",
                $"junction.footprintCenter={biasedJunctionRooms[0].Center.x},{biasedJunctionRooms[0].Center.y}",
                $"junction.connected={biasedJunctionConnected}",
                $"junction.connectionsBeforeFailure={biasedJunctionConnections.Count}",
                $"junction.rejection={biasedJunctionRejection}"
            };
            return string.Join("\n", lines);
        }

        private static RouteIntent BuildSlice2ConnectionIntent(
            int roomCount,
            params RouteTraversalIntent[] edges)
        {
            var nodes = new RouteNodeIntent[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                nodes[i] = new RouteNodeIntent($"slice2-node-{i}", "connector", "probe", i, -1, 0);
            }

            return new RouteIntent(
                0,
                RoutePlannerVersion,
                "density-adjacency-slice2-probe",
                nodes,
                edges,
                new RouteVistaIntent("slice2-vista", 0, Math.Max(0, roomCount - 1), 0),
                RouteElevationPolicy.AscendingSpine,
                Array.Empty<RecipeSlotIntent>(),
                string.Empty,
                0,
                Math.Max(0, roomCount - 1),
                0,
                Math.Max(0, roomCount - 1),
                0,
                0,
                0,
                Array.Empty<RouteOverlookIntent>(),
                allowGenericRoomWings: false);
        }

        private static bool DoorwayJoinsRooms(
            ElevationEdgeModel.DoorwayEdge doorway,
            RoomFootprint firstRoom,
            RoomFootprint secondRoom)
        {
            return firstRoom.Contains(doorway.firstCell) && secondRoom.Contains(doorway.secondCell) ||
                firstRoom.Contains(doorway.secondCell) && secondRoom.Contains(doorway.firstCell);
        }

        private static string BuildDensityAdjacencySlice3Snapshot()
        {
            const int processionalSeed = 2026072100;
            const int atriumSeed = 2026072101;
            const int twinWingSeed = 2026072103;
            JObject spaciousProcessional = BuildPhase0SeedReport(processionalSeed, "spacious");
            JObject spaciousProcessionalRepeat = BuildPhase0SeedReport(processionalSeed, "spacious");
            JObject denseProcessional = BuildPhase0SeedReport(processionalSeed, "dense");
            JObject denseProcessionalRepeat = BuildPhase0SeedReport(processionalSeed, "dense");
            JObject spaciousAtrium = BuildPhase0SeedReport(atriumSeed, "spacious");
            JObject denseAtrium = BuildPhase0SeedReport(atriumSeed, "dense");
            JObject spaciousTwinWing = BuildPhase0SeedReport(twinWingSeed, "spacious");
            JObject denseTwinWing = BuildPhase0SeedReport(twinWingSeed, "dense");
            int[] processionalSentinels = { 2026072140, 2026072186, 2026072262 };
            int spaciousSentinelExterior = 0;
            int denseSentinelExterior = 0;
            int shortenedSentinels = 0;
            int validSentinelProfiles = 0;
            foreach (int seed in processionalSentinels)
            {
                JObject spacious = BuildPhase0SeedReport(seed, "spacious");
                JObject dense = BuildPhase0SeedReport(seed, "dense");
                int spaciousExterior = spacious["measurements"]?["corridorEvidence"]
                    ?.Value<int>("exteriorCorridorCellCount") ?? 0;
                int denseExterior = dense["measurements"]?["corridorEvidence"]
                    ?.Value<int>("exteriorCorridorCellCount") ?? 0;
                spaciousSentinelExterior += spaciousExterior;
                denseSentinelExterior += denseExterior;
                if (denseExterior < spaciousExterior)
                {
                    shortenedSentinels++;
                }

                if (spacious.Value<bool?>("accepted") == true &&
                    dense.Value<bool?>("accepted") == true &&
                    spacious["validation"]?.Value<bool?>("passed") == true &&
                    dense["validation"]?.Value<bool?>("passed") == true)
                {
                    validSentinelProfiles++;
                }
            }

            DungeonGenerationSettings spaciousSettings = LoadActiveGenerationSettings("spacious");
            DungeonGenerationSettings denseSettings = LoadActiveGenerationSettings("dense");
            DungeonPatternSpatialSettings spaciousSpatial = spaciousSettings.processionalSpatial;
            DungeonPatternSpatialSettings denseSpatial = denseSettings.processionalSpatial;
            CurrentGenerationSettings = denseSettings;
            Vector2Int levelSize = BuildSlice3SizeProbe(
                "processional-hall",
                RouteTransitionKind.LevelCorridor,
                denseSpatial,
                new Vector2Int(denseSpatial.horizontalPitchCells, 0));
            Vector2Int stairSize = BuildSlice3SizeProbe(
                "processional-hall",
                RouteTransitionKind.Stair,
                denseSpatial,
                new Vector2Int(denseSpatial.horizontalPitchCells, 0));
            Vector2Int bridgeSize = BuildSlice3SizeProbe(
                "connector",
                RouteTransitionKind.Bridge,
                denseSpatial,
                new Vector2Int(0, denseSpatial.verticalPitchCells));
            Vector2Int stairwellSize = BuildSlice3SizeProbe(
                "arrival",
                RouteTransitionKind.Stairwell,
                denseSpatial,
                new Vector2Int(denseSpatial.horizontalPitchCells, 0));

            var lines = new List<string>
            {
                $"profiles.valueCount={BuildGenerationSettingsValues(spaciousSettings).Properties().Count()}",
                $"profiles.spaciousTerminal={RoomSizeRangeSnapshot(spaciousSpatial.terminalRoomSize)}",
                $"profiles.spaciousHall={RoomSizeRangeSnapshot(spaciousSpatial.hallRoomSize)}",
                $"profiles.spaciousConnector={RoomSizeRangeSnapshot(spaciousSpatial.connectorRoomSize)}",
                $"profiles.denseTerminal={RoomSizeRangeSnapshot(denseSpatial.terminalRoomSize)}",
                $"profiles.denseHall={RoomSizeRangeSnapshot(denseSpatial.hallRoomSize)}",
                $"profiles.denseConnector={RoomSizeRangeSnapshot(denseSpatial.connectorRoomSize)}",
                $"processional.spaciousAccepted={spaciousProcessional.Value<bool>("accepted")}",
                $"processional.denseAccepted={denseProcessional.Value<bool>("accepted")}",
                $"processional.spaciousValid={spaciousProcessional["validation"]?.Value<bool>("passed")}",
                $"processional.denseValid={denseProcessional["validation"]?.Value<bool>("passed")}",
                $"processional.spaciousCanonical={spaciousProcessional["hashes"]?.Value<string>("canonical")}",
                $"processional.denseCanonical={denseProcessional["hashes"]?.Value<string>("canonical")}",
                $"processional.spaciousExterior={spaciousProcessional["measurements"]?["corridorEvidence"]?.Value<int>("exteriorCorridorCellCount")}",
                $"processional.denseExterior={denseProcessional["measurements"]?["corridorEvidence"]?.Value<int>("exteriorCorridorCellCount")}",
                $"processional.spaciousLengthP50={spaciousProcessional["measurements"]?["corridorEvidence"]?["perConnectionExteriorLengthDistribution"]?.Value<int>("p50")}",
                $"processional.denseLengthP50={denseProcessional["measurements"]?["corridorEvidence"]?["perConnectionExteriorLengthDistribution"]?.Value<int>("p50")}",
                $"processional.spaciousDeterministic={string.Equals(spaciousProcessional["hashes"]?.Value<string>("canonical"), spaciousProcessionalRepeat["hashes"]?.Value<string>("canonical"), StringComparison.Ordinal)}",
                $"processional.denseDeterministic={string.Equals(denseProcessional["hashes"]?.Value<string>("canonical"), denseProcessionalRepeat["hashes"]?.Value<string>("canonical"), StringComparison.Ordinal) && JToken.DeepEquals(denseProcessional["measurements"], denseProcessionalRepeat["measurements"])}",
                $"sentinels.profileValidPairs={validSentinelProfiles}",
                $"sentinels.shortened={shortenedSentinels}",
                $"sentinels.spaciousExterior={spaciousSentinelExterior}",
                $"sentinels.denseExterior={denseSentinelExterior}",
                $"atrium.accepted={spaciousAtrium.Value<bool>("accepted") && denseAtrium.Value<bool>("accepted")}",
                $"atrium.canonicalSame={string.Equals(spaciousAtrium["hashes"]?.Value<string>("canonical"), denseAtrium["hashes"]?.Value<string>("canonical"), StringComparison.Ordinal)}",
                $"atrium.measurementsSame={JToken.DeepEquals(spaciousAtrium["measurements"], denseAtrium["measurements"])}",
                $"twinWing.accepted={spaciousTwinWing.Value<bool>("accepted") && denseTwinWing.Value<bool>("accepted")}",
                $"twinWing.canonicalSame={string.Equals(spaciousTwinWing["hashes"]?.Value<string>("canonical"), denseTwinWing["hashes"]?.Value<string>("canonical"), StringComparison.Ordinal)}",
                $"twinWing.measurementsSame={JToken.DeepEquals(spaciousTwinWing["measurements"], denseTwinWing["measurements"])}",
                $"cap.level={levelSize.x}x{levelSize.y}",
                $"cap.stairX={stairSize.x}x{stairSize.y}",
                $"cap.bridgeY={bridgeSize.x}x{bridgeSize.y}",
                $"cap.stairwellX={stairwellSize.x}x{stairwellSize.y}"
            };
            return string.Join("\n", lines);
        }

        private static Vector2Int BuildSlice3SizeProbe(
            string role,
            RouteTransitionKind transitionKind,
            DungeonPatternSpatialSettings spatial,
            Vector2Int neighborCenter)
        {
            var nodes = new[]
            {
                new RouteNodeIntent("slice3-source", role, "probe", 0, -1, 0),
                new RouteNodeIntent("slice3-target", "connector", "probe", 1, -1, 0)
            };
            var intent = new RouteIntent(
                0,
                RoutePlannerVersion,
                Phase1PatternId,
                nodes,
                new[]
                {
                    new RouteTraversalIntent("slice3-edge", 0, 1, 0, transitionKind)
                },
                new RouteVistaIntent("slice3-vista", 0, 1, 1),
                RouteElevationPolicy.AscendingSpine,
                Array.Empty<RecipeSlotIntent>(),
                string.Empty,
                0,
                1,
                0,
                1,
                0,
                0,
                0,
                Array.Empty<RouteOverlookIntent>(),
                allowGenericRoomWings: false);
            var centers = new[] { Vector2Int.zero, neighborCenter };
            ResolveGenericRoomDimensions(
                intent,
                nodes[0],
                0,
                spatial,
                Vector2Int.zero,
                centers,
                new System.Random(31),
                out int width,
                out int depth);
            return new Vector2Int(width, depth);
        }

        private static string RoomSizeRangeSnapshot(DungeonRoomSizeRange range)
        {
            return $"{range.minWidthCells}-{range.maxWidthCells}x{range.minDepthCells}-{range.maxDepthCells}";
        }

        private static string BuildDensityAdjacencySlice4Snapshot()
        {
            const int processionalSeed = 2026072100;
            const int atriumSeed = 2026072101;
            const int twinWingSeed = 2026072103;
            DungeonGenerationSettings spaciousSettings = LoadActiveGenerationSettings("spacious");
            DungeonGenerationSettings denseSettings = LoadActiveGenerationSettings("dense");
            DungeonPatternSpatialSettings spaciousSpatial = spaciousSettings.processionalSpatial;
            DungeonPatternSpatialSettings denseSpatial = denseSettings.processionalSpatial;

            JObject spaciousProcessional = BuildPhase0SeedReport(processionalSeed, "spacious");
            JObject spaciousProcessionalRepeat = BuildPhase0SeedReport(processionalSeed, "spacious");
            JObject denseProcessional = BuildPhase0SeedReport(processionalSeed, "dense");
            JObject denseProcessionalRepeat = BuildPhase0SeedReport(processionalSeed, "dense");
            JObject spaciousAtrium = BuildPhase0SeedReport(atriumSeed, "spacious");
            JObject denseAtrium = BuildPhase0SeedReport(atriumSeed, "dense");
            JObject spaciousTwinWing = BuildPhase0SeedReport(twinWingSeed, "spacious");
            JObject denseTwinWing = BuildPhase0SeedReport(twinWingSeed, "dense");

            int spaciousSentinelExterior = 0;
            int denseSentinelExterior = 0;
            int shortenedSentinels = 0;
            int validSentinelProfiles = 0;
            var sentinelResults = new List<string>();
            foreach (int seed in new[] { 2026072140, 2026072186, 2026072262 })
            {
                JObject spacious = BuildPhase0SeedReport(seed, "spacious");
                JObject dense = BuildPhase0SeedReport(seed, "dense");
                int spaciousExterior = spacious["measurements"]?["corridorEvidence"]
                    ?.Value<int>("exteriorCorridorCellCount") ?? 0;
                int denseExterior = dense["measurements"]?["corridorEvidence"]
                    ?.Value<int>("exteriorCorridorCellCount") ?? 0;
                spaciousSentinelExterior += spaciousExterior;
                denseSentinelExterior += denseExterior;
                if (denseExterior < spaciousExterior)
                {
                    shortenedSentinels++;
                }

                if (spacious.Value<bool?>("accepted") == true &&
                    dense.Value<bool?>("accepted") == true &&
                    spacious["validation"]?.Value<bool?>("passed") == true &&
                    dense["validation"]?.Value<bool?>("passed") == true)
                {
                    validSentinelProfiles++;
                }

                sentinelResults.Add(
                    $"{seed}:spacious={spacious.Value<bool?>("accepted")},dense={dense.Value<bool?>("accepted")}," +
                    $"code={dense.Value<string>("lastRejectionCode")}," +
                    $"builder={dense.Value<string>("routeBuilderFailureCode")}," +
                    $"reason={dense.Value<string>("lastRejection")}");
            }

            CurrentGenerationSettings = spaciousSettings;
            DungeonPatternSpatialSettings atriumSpatial = ResolvePatternSpatialSettings(AtriumRingPatternId);
            DungeonPatternSpatialSettings twinWingSpatial = ResolvePatternSpatialSettings(TwinWingPatternId);
            var lines = new List<string>
            {
                $"profiles.valueCount={BuildGenerationSettingsValues(spaciousSettings).Properties().Count()}",
                $"profiles.spaciousSpatial={SpatialSettingsSnapshot(spaciousSpatial)}",
                $"profiles.denseSpatial={SpatialSettingsSnapshot(denseSpatial)}",
                $"profiles.atriumSpatial={SpatialSettingsSnapshot(atriumSpatial)}",
                $"profiles.twinWingSpatial={SpatialSettingsSnapshot(twinWingSpatial)}",
                $"processional.spaciousAccepted={spaciousProcessional.Value<bool>("accepted")}",
                $"processional.spaciousValid={spaciousProcessional["validation"]?.Value<bool>("passed")}",
                $"processional.spaciousCanonical={spaciousProcessional["hashes"]?.Value<string>("canonical")}",
                $"processional.spaciousDeterministic={ReportsMatch(spaciousProcessional, spaciousProcessionalRepeat)}",
                $"processional.spaciousHorizontalPitch={RoutePlacementDistance(spaciousProcessional, "arrival", "threshold")}",
                $"processional.spaciousVerticalPitch={RoutePlacementDistance(spaciousProcessional, "reveal", "vista-target")}",
                $"processional.spaciousEnvelope={RoutePlacementEnvelopeSize(spaciousProcessional, "arrival")}",
                $"processional.denseAccepted={denseProcessional.Value<bool>("accepted")}",
                $"processional.denseValid={denseProcessional["validation"]?.Value<bool>("passed")}",
                $"processional.denseCanonical={denseProcessional["hashes"]?.Value<string>("canonical")}",
                $"processional.denseDeterministic={ReportsMatch(denseProcessional, denseProcessionalRepeat)}",
                $"processional.denseHorizontalPitch={RoutePlacementDistance(denseProcessional, "arrival", "threshold")}",
                $"processional.denseVerticalPitch={RoutePlacementDistance(denseProcessional, "reveal", "vista-target")}",
                $"processional.denseEnvelope={RoutePlacementEnvelopeSize(denseProcessional, "arrival")}",
                $"sentinels.profileValidPairs={validSentinelProfiles}",
                $"sentinels.shortened={shortenedSentinels}",
                $"sentinels.spaciousExterior={spaciousSentinelExterior}",
                $"sentinels.denseExterior={denseSentinelExterior}",
                $"sentinels.results={string.Join("|", sentinelResults)}",
                $"atrium.accepted={spaciousAtrium.Value<bool>("accepted") && denseAtrium.Value<bool>("accepted")}",
                $"atrium.canonicalSame={CanonicalReportsMatch(spaciousAtrium, denseAtrium)}",
                $"atrium.measurementsSame={JToken.DeepEquals(spaciousAtrium["measurements"], denseAtrium["measurements"])}",
                $"twinWing.accepted={spaciousTwinWing.Value<bool>("accepted") && denseTwinWing.Value<bool>("accepted")}",
                $"twinWing.canonicalSame={CanonicalReportsMatch(spaciousTwinWing, denseTwinWing)}",
                $"twinWing.measurementsSame={JToken.DeepEquals(spaciousTwinWing["measurements"], denseTwinWing["measurements"])}"
            };
            return string.Join("\n", lines);
        }

        private static string BuildDensityAdjacencySlice5Snapshot()
        {
            const string spaciousBaselineCanonical =
                "af4bce4800980db2d44ae2502600790a31cb0df287ed31100943f21baca5c4d9";
            DungeonGenerationSettings spaciousSettings = LoadActiveGenerationSettings("spacious");
            DungeonGenerationSettings denseSettings = LoadActiveGenerationSettings("dense");
            JObject spaciousProcessional = BuildPhase0SeedReport(2026072100, "spacious");
            JObject denseProcessional = BuildPhase0SeedReport(2026072100, "dense");
            JObject denseProcessionalRepeat = BuildPhase0SeedReport(2026072100, "dense");
            JObject spaciousAtrium = BuildPhase0SeedReport(2026072101, "spacious");
            JObject denseAtrium = BuildPhase0SeedReport(2026072101, "dense");
            JObject spaciousTwinWing = BuildPhase0SeedReport(2026072103, "spacious");
            JObject denseTwinWing = BuildPhase0SeedReport(2026072103, "dense");

            int validSentinelPairs = 0;
            int spaciousSentinelDoors = 0;
            int denseSentinelDoors = 0;
            var sentinelResults = new List<string>();
            foreach (int seed in new[] { 2026072140, 2026072186, 2026072262 })
            {
                JObject spacious = BuildPhase0SeedReport(seed, "spacious");
                JObject dense = BuildPhase0SeedReport(seed, "dense");
                int spaciousDoors = spacious["measurements"]?["corridorEvidence"]
                    ?.Value<int>("sharedWallDoorCount") ?? 0;
                int denseDoors = dense["measurements"]?["corridorEvidence"]
                    ?.Value<int>("sharedWallDoorCount") ?? 0;
                spaciousSentinelDoors += spaciousDoors;
                denseSentinelDoors += denseDoors;
                if (spacious.Value<bool?>("accepted") == true &&
                    dense.Value<bool?>("accepted") == true &&
                    spacious["validation"]?.Value<bool?>("passed") == true &&
                    dense["validation"]?.Value<bool?>("passed") == true)
                {
                    validSentinelPairs++;
                }

                sentinelResults.Add($"{seed}:{spaciousDoors}->{denseDoors}");
            }

            string probe = BuildDensityAdjacencySlice5BiasProbe(denseSettings.processionalSpatial);
            var lines = new List<string>
            {
                $"profiles.spaciousBias={spaciousSettings.processionalSpatial.neighborBiasStrengthCells}",
                $"profiles.denseBias={denseSettings.processionalSpatial.neighborBiasStrengthCells}",
                $"spacious.accepted={spaciousProcessional.Value<bool>("accepted")}",
                $"spacious.valid={spaciousProcessional["validation"]?.Value<bool>("passed")}",
                $"spacious.canonical={spaciousProcessional["hashes"]?.Value<string>("canonical")}",
                $"spacious.baselinePreserved={string.Equals(spaciousProcessional["hashes"]?.Value<string>("canonical"), spaciousBaselineCanonical, StringComparison.Ordinal)}",
                $"dense.accepted={denseProcessional.Value<bool>("accepted")}",
                $"dense.valid={denseProcessional["validation"]?.Value<bool>("passed")}",
                $"dense.deterministic={ReportsMatch(denseProcessional, denseProcessionalRepeat)}",
                $"sentinels.validPairs={validSentinelPairs}",
                $"sentinels.spaciousDoors={spaciousSentinelDoors}",
                $"sentinels.denseDoors={denseSentinelDoors}",
                $"sentinels.results={string.Join("|", sentinelResults)}",
                $"atrium.canonicalSame={CanonicalReportsMatch(spaciousAtrium, denseAtrium)}",
                $"atrium.measurementsSame={JToken.DeepEquals(spaciousAtrium["measurements"], denseAtrium["measurements"])}",
                $"twinWing.canonicalSame={CanonicalReportsMatch(spaciousTwinWing, denseTwinWing)}",
                $"twinWing.measurementsSame={JToken.DeepEquals(spaciousTwinWing["measurements"], denseTwinWing["measurements"])}",
                probe
            };
            return string.Join("\n", lines);
        }

        private static string BuildDensityAdjacencySlice5BiasProbe(
            DungeonPatternSpatialSettings spatial)
        {
            var nodes = new[]
            {
                new RouteNodeIntent("slice5-junction", "junction", "probe", 0, -1, 0),
                new RouteNodeIntent("slice5-level", "processional-hall", "probe", 1, -1, 0),
                new RouteNodeIntent("slice5-stair", "connector", "probe", 2, -1, 4)
            };
            var intent = new RouteIntent(
                0,
                RoutePlannerVersion,
                Phase1PatternId,
                nodes,
                new[]
                {
                    new RouteTraversalIntent("slice5-level-edge", 0, 1, 0, RouteTransitionKind.LevelCorridor),
                    new RouteTraversalIntent("slice5-stair-edge", 0, 2, 4, RouteTransitionKind.Stair)
                },
                new RouteVistaIntent("slice5-vista", 0, 1, 0),
                RouteElevationPolicy.AscendingSpine,
                Array.Empty<RecipeSlotIntent>(),
                string.Empty,
                0,
                2,
                0,
                1,
                0,
                0,
                0,
                Array.Empty<RouteOverlookIntent>(),
                allowGenericRoomWings: false);
            var centers = new[]
            {
                Vector2Int.zero,
                new Vector2Int(9, 0),
                new Vector2Int(-9, 0)
            };
            var envelopes = centers.Select(center => RoomEnvelope(center, spatial)).ToArray();
            var rooms = centers
                .Select(center => RoomFootprint.FromRect(CenteredRect(center, 7, 7)))
                .ToList();
            int stairExteriorBefore = BuildStraightCardinalPath(centers[0], centers[2]).Count(cell =>
                !rooms[0].Contains(cell) && !rooms[2].Contains(cell));

            ApplyProcessionalNeighborBias(intent, spatial, centers, envelopes, rooms);
            bool anchorsInside = rooms.Select((room, index) => room.Contains(centers[index])).All(value => value);
            bool overlaps = rooms[0].Overlaps(rooms[1]) ||
                rooms[0].Overlaps(rooms[2]) ||
                rooms[1].Overlaps(rooms[2]);
            bool connected = TryConnectProcessionalRooms(
                intent,
                rooms,
                centers,
                new HashSet<Vector2Int>(),
                Array.Empty<RecipePlacement>(),
                out _,
                out List<RoomConnection> connections,
                out string rejectionReason);
            int levelExterior = connected
                ? connections[0].path.Count(cell => !rooms.Any(room => room.Contains(cell)))
                : -1;
            int stairExteriorAfter = connected
                ? connections[1].path.Count(cell => !rooms.Any(room => room.Contains(cell)))
                : -1;
            int doorwayCount = -1;
            bool doorwayJoinsRooms = false;
            int rendererRejected = -1;
            int collisionSources = 0;
            int collisionMissingMeshes = 0;
            if (connected)
            {
                var levelRooms = new List<RoomFootprint> { rooms[0], rooms[1] };
                var levelFloor = new HashSet<Vector2Int>(rooms[0].cells);
                levelFloor.UnionWith(rooms[1].cells);
                AddPathCells(levelFloor, connections[0].path);
                var levelLayout = new DungeonLayout(
                    levelFloor,
                    levelRooms,
                    new List<RoomConnection> { connections[0] });
                var levelCells = levelFloor.ToDictionary(cell => cell, _ => 0);
                List<ElevationEdgeModel.DoorwayEdge> doorways = BuildDoorwayEdges(levelLayout, levelCells);
                doorwayCount = doorways.Count;
                doorwayJoinsRooms = doorwayCount == 1 &&
                    DoorwayJoinsRooms(doorways[0], rooms[0], rooms[1]);
                if (TryBuildRoomBoundaryContext(
                        levelLayout,
                        levelCells,
                        Array.Empty<ElevationEdgeModel.TransitionEdge>(),
                        new System.Random(23),
                        out ElevationEdgeModel.RoomBoundaryContext boundaryContext,
                        out _))
                {
                    GameObject root = null;
                    try
                    {
                        root = ElevationEdgeModel.BuildLevelField(
                            Vector3.zero,
                            levelCells,
                            Array.Empty<ElevationEdgeModel.TransitionEdge>(),
                            boundaryContext,
                            "Density Adjacency Slice 5 Probe",
                            out ElevationEdgeModel.BuildReport buildReport,
                            out _);
                        rendererRejected = buildReport.rejected;
                        foreach (Collider collider in root.GetComponentsInChildren<Collider>(includeInactive: false))
                        {
                            if (collider == null || !collider.enabled || collider.isTrigger)
                            {
                                continue;
                            }

                            collisionSources++;
                            if (collider is MeshCollider meshCollider && meshCollider.sharedMesh == null)
                            {
                                collisionMissingMeshes++;
                            }
                        }
                    }
                    finally
                    {
                        if (root != null)
                        {
                            DestroyImmediate(root);
                        }
                    }
                }
            }

            return string.Join("\n", new[]
            {
                $"probe.connected={connected}",
                $"probe.rejection={rejectionReason}",
                $"probe.centers={rooms[0].Center.x},{rooms[0].Center.y}|{rooms[1].Center.x},{rooms[1].Center.y}|{rooms[2].Center.x},{rooms[2].Center.y}",
                $"probe.anchorsInside={anchorsInside}",
                $"probe.overlaps={overlaps}",
                $"probe.levelExterior={levelExterior}",
                $"probe.stairExteriorBefore={stairExteriorBefore}",
                $"probe.stairExteriorAfter={stairExteriorAfter}",
                $"probe.doorways={doorwayCount}",
                $"probe.doorwayJoinsRooms={doorwayJoinsRooms}",
                $"probe.rendererRejected={rendererRejected}",
                $"probe.collisionSources={collisionSources}",
                $"probe.collisionMissingMeshes={collisionMissingMeshes}"
            });
        }

        private static string SpatialSettingsSnapshot(DungeonPatternSpatialSettings spatial)
        {
            return $"{spatial.horizontalPitchCells}x{spatial.verticalPitchCells}:r{spatial.roomEnvelopeRadiusCells}:" +
                $"b{spatial.neighborBiasStrengthCells}:" +
                $"{RoomSizeRangeSnapshot(spatial.terminalRoomSize)}|" +
                $"{RoomSizeRangeSnapshot(spatial.hallRoomSize)}|" +
                RoomSizeRangeSnapshot(spatial.connectorRoomSize);
        }

        private static bool ReportsMatch(JObject first, JObject second)
        {
            return CanonicalReportsMatch(first, second) &&
                JToken.DeepEquals(first["measurements"], second["measurements"]);
        }

        private static bool CanonicalReportsMatch(JObject first, JObject second)
        {
            return string.Equals(
                first["hashes"]?.Value<string>("canonical"),
                second["hashes"]?.Value<string>("canonical"),
                StringComparison.Ordinal);
        }

        private static int RoutePlacementDistance(JObject report, string firstNodeId, string secondNodeId)
        {
            Vector2Int first = RoutePlacementCenter(report, firstNodeId);
            Vector2Int second = RoutePlacementCenter(report, secondNodeId);
            return Mathf.Abs(first.x - second.x) + Mathf.Abs(first.y - second.y);
        }

        private static Vector2Int RoutePlacementCenter(JObject report, string nodeId)
        {
            foreach (JToken node in report["routePlacement"]?["nodeCenters"] ?? new JArray())
            {
                if (string.Equals(node.Value<string>("nodeId"), nodeId, StringComparison.Ordinal))
                {
                    return new Vector2Int(
                        node["center"]?.Value<int>("x") ?? 0,
                        node["center"]?.Value<int>("y") ?? 0);
                }
            }

            throw new InvalidOperationException($"Route placement report had no node '{nodeId}'");
        }

        private static string RoutePlacementEnvelopeSize(JObject report, string nodeId)
        {
            foreach (JToken node in report["routePlacement"]?["nodeCenters"] ?? new JArray())
            {
                if (string.Equals(node.Value<string>("nodeId"), nodeId, StringComparison.Ordinal))
                {
                    return $"{node["envelope"]?.Value<int>("width")}x" +
                        node["envelope"]?.Value<int>("height");
                }
            }

            throw new InvalidOperationException($"Route placement report had no node '{nodeId}'");
        }

        private static string BuildCharacterizationSnapshot(int seed)
        {
            JObject report = BuildPhase0SeedReport(seed);
            var lines = new List<string>
            {
                SnapshotLine("accepted", report["accepted"]),
                SnapshotLine("profile", report["profile"]),
                SnapshotLine("settingsDigest", report["settingsDigest"]),
                SnapshotLine("hash.layout", report["hashes"]?["layout"]),
                SnapshotLine("hash.tieredLevelPlan", report["hashes"]?["tieredLevelPlan"]),
                SnapshotLine("hash.canonical", report["hashes"]?["canonical"]),
                SnapshotLine("validation.layoutConnectivity", report["validation"]?["layoutConnectivity"]?["passed"]),
                SnapshotLine("validation.roomGraphConnectivity", report["validation"]?["roomGraphConnectivity"]?["passed"]),
                SnapshotLine("validation.transitionContracts", report["validation"]?["transitionContracts"]?["passed"]),
                SnapshotLine("validation.verticalTraversal", report["validation"]?["verticalTraversal"]?["passed"]),
                SnapshotLine("validation.bottomToTopTraversal", report["validation"]?["bottomToTopTraversal"]?["passed"]),
                SnapshotLine("validation.routeRequirements", report["validation"]?["routeRequirements"]?["passed"]),
                SnapshotLine("validation.headroom", report["validation"]?["headroom"]?["passed"]),
                SnapshotLine("validation.boundary", report["validation"]?["boundary"]?["passed"]),
                SnapshotLine("validation.rendererInputs", report["validation"]?["rendererInputs"]?["passed"]),
                SnapshotLine("validation.passed", report["validation"]?["passed"]),
                SnapshotLine("metric.rootedRouteCount", report["layout"]?["graph"]?["rootedRouteCount"]),
                SnapshotLine("metric.longestRootRouteRooms", report["layout"]?["graph"]?["longestRootRouteRooms"]),
                SnapshotLine("metric.finalRoomDegreeSamples", report["measurements"]?["finalRoomDegreeDistribution"]?["sampleCount"]),
                SnapshotLine("metric.exteriorCorridorCells", report["measurements"]?["corridorEvidence"]?["exteriorCorridorCellCount"]),
                SnapshotLine("metric.sharedWallDoors", report["measurements"]?["corridorEvidence"]?["sharedWallDoorCount"]),
                SnapshotLine("metric.reservedVistaCells", report["measurements"]?["voidExtent"]?["reservedVistaCellCount"]),
                SnapshotLine("metric.transitionCount", report["tieredLevelPlan"]?["transitionCount"]),
                SnapshotLine("metric.elevationSpan", report["tieredLevelPlan"]?["elevationSpan"]),
                SnapshotLine("failure", report["lastRejection"])
            };
            return string.Join("\n", lines);
        }

        private static string BuildRouteCharacterizationSnapshot(int seed)
        {
            JObject report = BuildPhase0SeedReport(seed);
            var lines = new List<string>
            {
                SnapshotLine("accepted", report["accepted"]),
                SnapshotLine("layoutAttempts", report["layoutAttempts"]),
                SnapshotLine("hash.routeIntent", report["hashes"]?["routeIntent"]),
                SnapshotLine("hash.layout", report["hashes"]?["layout"]),
                SnapshotLine("hash.tieredLevelPlan", report["hashes"]?["tieredLevelPlan"]),
                SnapshotLine("hash.canonical", report["hashes"]?["canonical"]),
                SnapshotLine("route.pattern", report["routeIntent"]?["patternId"]),
                SnapshotLine("route.nodeCount", report["routeIntent"]?["nodeCount"]),
                SnapshotLine("route.mainRouteCount", report["routeIntent"]?["graph"]?["mainRouteCount"]),
                SnapshotLine("route.branchNodeCount", report["routeIntent"]?["graph"]?["branchNodeCount"]),
                SnapshotLine("route.loopEdges", report["routeIntent"]?["graph"]?["loopEdges"]),
                SnapshotLine("route.bottomNode", report["routeIntent"]?["bottomNode"]),
                SnapshotLine("route.topNode", report["routeIntent"]?["topNode"]),
                SnapshotLine("vista.sourceFacing", report["routePlacement"]?["vista"]?["sourceFacing"]),
                SnapshotLine("vista.targetFacing", report["routePlacement"]?["vista"]?["targetFacing"]),
                SnapshotLine("vista.facingOpposed", report["routePlacement"]?["vista"]?["facingOpposed"]),
                SnapshotLine("vista.reservedVoidCells", report["routePlacement"]?["vista"]?["reservedVoidCellCount"]),
                SnapshotLine("vista.unobstructed", report["routePlacement"]?["vista"]?["unobstructedCandidateVolume"]),
                SnapshotLine("vertical.elevationPolicy", report["routeIntent"]?["elevationPolicy"]),
                SnapshotLine("vertical.routeClimb", report["routeResolution"]?["routeClimbLevels"]),
                SnapshotLine("vertical.requirementsSatisfied", report["routeResolution"]?["requirementsSatisfied"]),
                SnapshotLine("vertical.requiredTransitionCount", report["routeResolution"]?["requiredTransitionCount"]),
                SnapshotLine("vertical.stairCount", report["routeResolution"]?["transitionKinds"]?["Stair"]),
                SnapshotLine("vertical.bridgeCount", report["routeResolution"]?["transitionKinds"]?["Bridge"]),
                SnapshotLine("vertical.stairwellCount", report["routeResolution"]?["transitionKinds"]?["Stairwell"]),
                SnapshotLine("vertical.allStructuralReservedBeforeFill", report["routeResolution"]?["allStructuralReservedBeforeFill"]),
                SnapshotLine("vista.finalValid", report["routeResolution"]?["vista"]?["finalValid"]),
                SnapshotLine("vista.finalSourceLevel", report["routeResolution"]?["vista"]?["sourceLevel"]),
                SnapshotLine("vista.finalTargetLevel", report["routeResolution"]?["vista"]?["targetLevel"]),
                SnapshotLine("vista.finalReservedVoidCells", report["routeResolution"]?["vista"]?["reservedVoidCellCount"]),
                SnapshotLine("schema.fieldCount", report["schemaUsage"]?["fieldCount"]),
                SnapshotLine("schema.allFieldsConsumed", report["schemaUsage"]?["allFieldsConsumed"]),
                SnapshotLine("validation.passed", report["validation"]?["passed"]),
                SnapshotLine("validation.layoutConnectivity", report["validation"]?["layoutConnectivity"]?["passed"]),
                SnapshotLine("validation.roomGraphConnectivity", report["validation"]?["roomGraphConnectivity"]?["passed"]),
                SnapshotLine("validation.verticalTraversal", report["validation"]?["verticalTraversal"]?["passed"]),
                SnapshotLine("validation.routeRequirements", report["validation"]?["routeRequirements"]?["passed"]),
                SnapshotLine("validation.recipes", report["validation"]?["recipes"]?["passed"]),
                SnapshotLine("validation.headroom", report["validation"]?["headroom"]?["passed"]),
                SnapshotLine("metric.rooms", report["layout"]?["rooms"]),
                SnapshotLine("metric.connections", report["layout"]?["connections"]),
                SnapshotLine("metric.loopEdges", report["layout"]?["graph"]?["loopEdges"]),
                SnapshotLine("lastRejectionCode", report["lastRejectionCode"]),
                SnapshotLine("failure", report["lastRejection"])
            };
            AppendRecipeResolutionSnapshot(
                lines,
                report["recipeResolutions"] as JArray);
            return string.Join("\n", lines);
        }

        private static string BuildRouteIntentOnlySnapshot(int seed)
        {
            ResetPhase1RouteDiagnostics();
            phase1LastRouteIntent = BuildDiagnosticRouteIntent(seed);
            JObject intent = BuildPhase1RouteIntentProjection();
            JObject phase4Recipe = FindRecipeProjection(
                intent["recipeSlots"] as JArray,
                DungeonRecipeIds.ProcessionalLandmark);
            bool containsSpatialCoordinates = intent.ToString(Formatting.None).Contains("\"center\"");
            int requiredStairs = 0;
            int requiredBridges = 0;
            int requiredStairwells = 0;
            foreach (RouteTraversalIntent edge in phase1LastRouteIntent.traversalEdges)
            {
                if (edge.transitionKind == RouteTransitionKind.Stair) requiredStairs++;
                if (edge.transitionKind == RouteTransitionKind.Bridge) requiredBridges++;
                if (edge.transitionKind == RouteTransitionKind.Stairwell) requiredStairwells++;
            }

            var lines = new List<string>
            {
                SnapshotLine("route.pattern", intent["patternId"]),
                SnapshotLine("route.nodeCount", intent["nodeCount"]),
                SnapshotLine("route.mainRouteCount", intent["graph"]?["mainRouteCount"]),
                SnapshotLine("route.branchNodeCount", intent["graph"]?["branchNodeCount"]),
                SnapshotLine("route.loopEdges", intent["graph"]?["loopEdges"]),
                SnapshotLine("route.bottomNode", intent["bottomNode"]),
                SnapshotLine("route.topNode", intent["topNode"]),
                SnapshotLine("vista.facingRequirement", intent["vista"]?["facingRequirement"]),
                SnapshotLine("vista.minimumReservedVoidCells", intent["vista"]?["minimumReservedVoidCells"]),
                SnapshotLine("vertical.elevationPolicy", intent["elevationPolicy"]),
                SnapshotLine("vertical.bottomRelativeLevel", intent["nodes"]?[0]?["relativeElevationLevels"]),
                SnapshotLine("vertical.topRelativeLevel", intent["nodes"]?[8]?["relativeElevationLevels"]),
                SnapshotLine("episode.id", phase4Recipe?["id"]),
                SnapshotLine("episode.slotNode", phase4Recipe?["slotNode"]),
                SnapshotLine("episode.focalAxisBinding", phase4Recipe?["orientationBinding"]),
                SnapshotLine("episode.coupledStairCount", phase4Recipe?["transitionCount"]),
                SnapshotLine("episode.allowedFocalVariations", phase4Recipe?["variationCount"]),
                SnapshotLine("episode.thresholdCount", phase4Recipe?["ports"] is JArray episodePorts ? episodePorts.Count : 0),
                SnapshotLine("recipes.slotCount", intent["recipeSlots"] is JArray slots ? slots.Count : 0),
                SnapshotLine("recipes.catalogDigest", intent["catalogDigest"]),
                $"vertical.requiredStairs={requiredStairs}",
                $"vertical.requiredBridges={requiredBridges}",
                $"vertical.requiredStairwells={requiredStairwells}",
                $"containsSpatialCoordinates={containsSpatialCoordinates}"
            };
            return string.Join("\n", lines);
        }

        private static RouteIntent BuildDiagnosticRouteIntent(int seed)
        {
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out string rejectionReason) ||
                !TryBuildRequiredRecipeSlots(
                    catalog,
                    RoutePatternKind.ProcessionalSpine,
                    Phase1VistaTargetNode,
                    out RecipeSlotIntent[] slots,
                    out rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            return BuildProcessionalRouteIntent(seed, slots, catalog.digest);
        }

        private static RouteIntent BuildDiagnosticAtriumRingIntent(int seed)
        {
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out string rejectionReason) ||
                !TryBuildRequiredRecipeSlots(
                    catalog,
                    RoutePatternKind.AtriumRing,
                    AtriumRingVistaTargetNode,
                    out RecipeSlotIntent[] slots,
                    out rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            return BuildAtriumRingRouteIntent(seed, slots, catalog.digest);
        }

        private static RouteIntent BuildDiagnosticTwinWingIntent(int seed)
        {
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out string rejectionReason) ||
                !TryBuildRequiredRecipeSlots(
                    catalog,
                    RoutePatternKind.TwinWingKeep,
                    TwinWingVistaTargetNode,
                    out RecipeSlotIntent[] slots,
                    out rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            return BuildTwinWingRouteIntent(seed, slots, catalog.digest);
        }

        private static RouteIntent BuildDiagnosticSelectedRouteIntent(int seed)
        {
            RoutePatternKind pattern = SelectRoutePattern(seed);
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out string rejectionReason) ||
                !TryBuildRequiredRecipeSlots(
                    catalog,
                    pattern,
                    LandmarkNodeForPattern(pattern),
                    out RecipeSlotIntent[] slots,
                    out rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            return BuildSelectedRouteIntent(pattern, seed, slots, catalog.digest);
        }

        private static string BuildPhase6bAtriumRingSnapshot(int seed)
        {
            CurrentGenerationSettings = LoadActiveGenerationSettings(ResolveRequestedGenerationProfileId());
            RouteIntent intent = BuildDiagnosticAtriumRingIntent(seed);
            RouteIntent processional = BuildDiagnosticRouteIntent(seed - 1);
            bool valid = TryValidateRouteIntent(intent, out string validationError);
            var adjacency = new List<int>[intent.nodes.Length];
            for (int node = 0; node < adjacency.Length; node++)
            {
                adjacency[node] = new List<int>();
            }

            var nodeIds = new List<string>(intent.nodes.Length);
            foreach (RouteNodeIntent node in intent.nodes)
            {
                nodeIds.Add(node.id);
            }

            var edgeDetails = new List<string>(intent.traversalEdges.Length);
            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                adjacency[edge.fromNode].Add(edge.toNode);
                adjacency[edge.toNode].Add(edge.fromNode);
                edgeDetails.Add(
                    $"{edge.id}:{intent.nodes[edge.fromNode].id}>{intent.nodes[edge.toNode].id}:" +
                    $"{edge.transitionKind}:{edge.requiredRiseLevels}");
            }

            bool embedded = TryEmbedAtriumRingRoute(
                seed,
                layoutAttempt: 1,
                intent,
                ResolvePatternSpatialSettings(intent.patternId),
                out Vector2Int[] nodeCenters,
                out string embeddingFailureCode,
                out string embeddingError);
            Vector2Int vistaDelta = embedded
                ? nodeCenters[intent.vista.targetNode] - nodeCenters[intent.vista.sourceNode]
                : Vector2Int.zero;
            int vistaCenterDistance = Mathf.Abs(vistaDelta.x) + Mathf.Abs(vistaDelta.y);
            return string.Join("\n", new[]
            {
                $"selector.evenPattern={SelectedRoutePatternId(2026072100)}",
                $"selector.oddPattern={SelectedRoutePatternId(2026072101)}",
                $"processional.plannerVersion={processional.plannerVersion}",
                $"processional.cycleLength={processional.requiredCycleCoreNodeCount}",
                $"graph.pattern={intent.patternId}",
                $"graph.plannerVersion={intent.plannerVersion}",
                $"graph.nodeCount={intent.nodes.Length}",
                $"graph.edgeCount={intent.traversalEdges.Length}",
                $"graph.loopEdges={intent.traversalEdges.Length - (intent.nodes.Length - 1)}",
                $"graph.cycleLength={CountCycleCoreNodes(adjacency)}",
                $"graph.branchAttach={intent.nodes[intent.branchAttachNode].id}",
                $"graph.branchRejoin={intent.nodes[intent.branchRejoinNode].id}",
                $"graph.nodeIds={string.Join("|", nodeIds)}",
                $"graph.edgeDetails={string.Join("|", edgeDetails)}",
                $"vista.id={intent.vista.id}",
                $"vista.source={intent.nodes[intent.vista.sourceNode].id}",
                $"vista.target={intent.nodes[intent.vista.targetNode].id}",
                $"vista.centerCardinallyAligned={vistaDelta != Vector2Int.zero && (vistaDelta.x == 0 || vistaDelta.y == 0)}",
                $"vista.centerDistanceCells={vistaCenterDistance}",
                $"route.valid={valid}",
                $"route.validationError={validationError}",
                $"embedding.succeeded={embedded}",
                $"embedding.failureCode={embeddingFailureCode}",
                $"embedding.error={embeddingError}"
            });
        }

        private static string BuildPhase6cTwinWingSnapshot(int seed)
        {
            CurrentGenerationSettings = LoadActiveGenerationSettings(ResolveRequestedGenerationProfileId());
            RouteIntent intent = BuildDiagnosticTwinWingIntent(seed);
            RouteIntent processional = BuildDiagnosticRouteIntent(seed - 3);
            RouteIntent atrium = BuildDiagnosticAtriumRingIntent(seed - 2);
            bool valid = TryValidateRouteIntent(intent, out string validationError);
            var adjacency = new List<int>[intent.nodes.Length];
            for (int node = 0; node < adjacency.Length; node++)
            {
                adjacency[node] = new List<int>();
            }

            int mainNodeCount = 0;
            int branchNodeCount = 0;
            var nodeIds = new List<string>(intent.nodes.Length);
            foreach (RouteNodeIntent node in intent.nodes)
            {
                nodeIds.Add(node.id);
                if (node.IsOnMainRoute)
                {
                    mainNodeCount++;
                }
                else
                {
                    branchNodeCount++;
                }
            }

            var edgeDetails = new List<string>(intent.traversalEdges.Length);
            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                adjacency[edge.fromNode].Add(edge.toNode);
                adjacency[edge.toNode].Add(edge.fromNode);
                edgeDetails.Add(
                    $"{edge.id}:{intent.nodes[edge.fromNode].id}>{intent.nodes[edge.toNode].id}:" +
                    $"{edge.transitionKind}:{edge.requiredRiseLevels}");
            }

            bool embedded = TryEmbedTwinWingRoute(
                seed,
                layoutAttempt: 1,
                intent,
                ResolvePatternSpatialSettings(intent.patternId),
                out Vector2Int[] nodeCenters,
                out string embeddingFailureCode,
                out string embeddingError);
            Vector2Int vistaDelta = embedded
                ? nodeCenters[intent.vista.targetNode] - nodeCenters[intent.vista.sourceNode]
                : Vector2Int.zero;
            int vistaCenterDistance = Mathf.Abs(vistaDelta.x) + Mathf.Abs(vistaDelta.y);
            return string.Join("\n", new[]
            {
                $"selector.residue0Pattern={SelectedRoutePatternId(2026072100)}",
                $"selector.residue1Pattern={SelectedRoutePatternId(2026072101)}",
                $"selector.residue2Pattern={SelectedRoutePatternId(2026072102)}",
                $"selector.residue3Pattern={SelectedRoutePatternId(2026072103)}",
                $"processional.plannerVersion={processional.plannerVersion}",
                $"atrium.plannerVersion={atrium.plannerVersion}",
                $"graph.pattern={intent.patternId}",
                $"graph.plannerVersion={intent.plannerVersion}",
                $"graph.nodeCount={intent.nodes.Length}",
                $"graph.edgeCount={intent.traversalEdges.Length}",
                $"graph.mainRouteCount={mainNodeCount}",
                $"graph.branchNodeCount={branchNodeCount}",
                $"graph.loopEdges={intent.traversalEdges.Length - (intent.nodes.Length - 1)}",
                $"graph.cycleCoreNodes={CountCycleCoreNodes(adjacency)}",
                $"graph.branchAttach={intent.nodes[intent.branchAttachNode].id}",
                $"graph.branchAttachDegree={adjacency[intent.branchAttachNode].Count}",
                $"graph.branchRejoin={intent.nodes[intent.branchRejoinNode].id}",
                $"graph.branchRejoinDegree={adjacency[intent.branchRejoinNode].Count}",
                "graph.wingPathLengths=4|4",
                $"graph.nodeIds={string.Join("|", nodeIds)}",
                $"graph.edgeDetails={string.Join("|", edgeDetails)}",
                $"vista.id={intent.vista.id}",
                $"vista.source={intent.nodes[intent.vista.sourceNode].id}",
                $"vista.target={intent.nodes[intent.vista.targetNode].id}",
                $"vista.centerCardinallyAligned={vistaDelta != Vector2Int.zero && (vistaDelta.x == 0 || vistaDelta.y == 0)}",
                $"vista.centerDistanceCells={vistaCenterDistance}",
                $"route.valid={valid}",
                $"route.validationError={validationError}",
                $"embedding.succeeded={embedded}",
                $"embedding.failureCode={embeddingFailureCode}",
                $"embedding.error={embeddingError}",
                $"profile.mapWidthMaxCells={CurrentGenerationSettings.mapWidthMaxCells}",
                $"profile.mapDepthMaxCells={CurrentGenerationSettings.mapDepthMaxCells}"
            });
        }

        private static string BuildPhase6dRouteRhythmSnapshot(int seed)
        {
            RouteIntent processional = BuildDiagnosticSelectedRouteIntent(2026072100);
            RouteIntent atrium = BuildDiagnosticSelectedRouteIntent(2026072101);
            RouteIntent twinWing = BuildDiagnosticSelectedRouteIntent(2026072103);
            bool processionalValid = TryValidateRouteRhythm(processional.nodes, out string processionalError);
            bool atriumValid = TryValidateRouteRhythm(atrium.nodes, out string atriumError);
            bool twinWingValid = TryValidateRouteRhythm(twinWing.nodes, out string twinWingError);

            bool orderRejected = !TryValidateRouteRhythm(
                new[]
                {
                    RhythmProbeNode("order-a", 0, "arrival", "arrival"),
                    RhythmProbeNode("order-b", 2, "connector", "approach")
                },
                out string orderError);
            bool adjacentRoleRejected = !TryValidateRouteRhythm(
                new[]
                {
                    RhythmProbeNode("role-a", 0, "hall", "arrival"),
                    RhythmProbeNode("role-b", 1, "hall", "approach")
                },
                out string adjacentRoleError);
            bool adjacentBeatRejected = !TryValidateRouteRhythm(
                new[]
                {
                    RhythmProbeNode("beat-a", 0, "arrival", "reveal"),
                    RhythmProbeNode("beat-b", 1, "connector", "reveal")
                },
                out string adjacentBeatError);
            bool roleLimitRejected = !TryValidateRouteRhythm(
                new[]
                {
                    RhythmProbeNode("limit-a", 0, "hall", "arrival"),
                    RhythmProbeNode("limit-b", 1, "connector", "compression"),
                    RhythmProbeNode("limit-c", 2, "hall", "reveal"),
                    RhythmProbeNode("limit-d", 3, "junction", "choice"),
                    RhythmProbeNode("limit-e", 4, "hall", "approach")
                },
                out string roleLimitError);
            bool recipeSpacingRejected = !TryValidateRouteRhythm(
                new[]
                {
                    RhythmProbeNode("recipe-a", 0, "connector", "compression", "recipe-a"),
                    RhythmProbeNode("recipe-middle", 1, "hall", "approach"),
                    RhythmProbeNode("recipe-b", 2, "landmark", "landmark", "recipe-b")
                },
                out string recipeSpacingError);

            var invalidNodes = (RouteNodeIntent[])processional.nodes.Clone();
            RouteNodeIntent invalidSecond = invalidNodes[1];
            invalidNodes[1] = new RouteNodeIntent(
                invalidSecond.id,
                invalidNodes[0].role,
                invalidSecond.beat,
                invalidSecond.mainRouteOrder,
                invalidSecond.branchOrder,
                invalidSecond.relativeElevationLevels,
                invalidSecond.landmarkSlotId);
            var invalidIntent = new RouteIntent(
                processional.seed,
                processional.plannerVersion,
                processional.patternId,
                invalidNodes,
                processional.traversalEdges,
                processional.vista,
                processional.elevationPolicy,
                processional.recipeSlots,
                processional.catalogDigest,
                processional.bottomNode,
                processional.topNode,
                processional.branchAttachNode,
                processional.branchRejoinNode,
                processional.requiredCycleRank,
                processional.requiredCycleCoreNodeCount,
                processional.requiredJunctionDegree,
                processional.plannedOverlooks,
                processional.allowGenericRoomWings);
            bool fullValidatorRejected = !TryValidateRouteIntent(
                invalidIntent,
                out string fullValidationError);

            return string.Join("\n", new[]
            {
                $"policy.version={RouteRhythmPolicyVersion}",
                $"policy.maxMainRouteRoleOccurrences={MaxMainRouteRoleOccurrences}",
                "policy.maxConsecutiveSameRole=1",
                "policy.maxConsecutiveSameBeat=1",
                $"policy.minimumMainRouteNodesBetweenRecipeSlots={MinimumMainRouteNodesBetweenRecipeSlots}",
                $"production.processionalValid={processionalValid}",
                $"production.processionalError={processionalError}",
                $"production.atriumValid={atriumValid}",
                $"production.atriumError={atriumError}",
                $"production.twinWingValid={twinWingValid}",
                $"production.twinWingError={twinWingError}",
                $"probe.orderRejected={orderRejected}",
                $"probe.orderError={orderError}",
                $"probe.adjacentRoleRejected={adjacentRoleRejected}",
                $"probe.adjacentRoleError={adjacentRoleError}",
                $"probe.adjacentBeatRejected={adjacentBeatRejected}",
                $"probe.adjacentBeatError={adjacentBeatError}",
                $"probe.roleLimitRejected={roleLimitRejected}",
                $"probe.roleLimitError={roleLimitError}",
                $"probe.recipeSpacingRejected={recipeSpacingRejected}",
                $"probe.recipeSpacingError={recipeSpacingError}",
                $"probe.fullValidatorRejected={fullValidatorRejected}",
                $"probe.fullValidationError={fullValidationError}",
                $"probe.productionFailureCode={RouteIntentInvalidFailureCode}",
                $"diagnostic.seed={seed}"
            });
        }

        private static string BuildPhase6eNamedPromontorySnapshot(int seed)
        {
            JObject processional = BuildPhase0SeedReport(2026072124);
            JObject noSurplus = BuildPhase0SeedReport(2026072100);
            JObject atrium = BuildPhase0SeedReport(2026072101);
            JObject twinWing = BuildPhase0SeedReport(2026072103);

            RouteIntent probeIntent = BuildDiagnosticSelectedRouteIntent(2026072100);
            Vector2Int probeSource = Vector2Int.zero;
            Vector2Int probeTarget = new Vector2Int(0, 5);
            Vector2Int probeFacing = Vector2Int.up;
            Vector2Int[] probePlanned = { new Vector2Int(0, 1) };
            Vector2Int[] probeReserved =
            {
                new Vector2Int(0, 2),
                new Vector2Int(0, 3),
                new Vector2Int(0, 4)
            };

            RouteIntent missingIdentityIntent = WithDiagnosticVista(
                probeIntent,
                new RouteVistaIntent(
                    string.Empty,
                    probeIntent.vista.sourceNode,
                    probeIntent.vista.targetNode,
                    minimumReservedVoidCells: 3));
            bool missingIdentityRejected = !TryResolveNamedVistaPromontory(
                PromontoryProbeRequirements(
                    missingIdentityIntent,
                    probeReserved,
                    probeSource,
                    probeTarget,
                    probeFacing,
                    -probeFacing,
                    probePlanned),
                PromontoryProbeLevels(probeSource, 12, probeTarget, 8),
                out _,
                out string missingIdentityError);
            bool facingRejected = !TryResolveNamedVistaPromontory(
                PromontoryProbeRequirements(
                    probeIntent,
                    probeReserved,
                    probeSource,
                    probeTarget,
                    probeFacing,
                    probeFacing,
                    probePlanned),
                PromontoryProbeLevels(probeSource, 12, probeTarget, 8),
                out _,
                out string facingError);
            bool offAxisRejected = !TryResolveNamedVistaPromontory(
                PromontoryProbeRequirements(
                    probeIntent,
                    probeReserved,
                    probeSource,
                    probeTarget,
                    probeFacing,
                    -probeFacing,
                    new[] { new Vector2Int(1, 1) }),
                PromontoryProbeLevels(probeSource, 12, probeTarget, 8),
                out _,
                out string offAxisError);
            Dictionary<Vector2Int, int> occupiedLevels = PromontoryProbeLevels(
                probeSource,
                12,
                probeTarget,
                8);
            occupiedLevels[probePlanned[0]] = 12;
            bool occupiedRejected = !TryResolveNamedVistaPromontory(
                PromontoryProbeRequirements(
                    probeIntent,
                    probeReserved,
                    probeSource,
                    probeTarget,
                    probeFacing,
                    -probeFacing,
                    probePlanned),
                occupiedLevels,
                out _,
                out string occupiedError);
            bool voidBudgetRejected = !TryResolveNamedVistaPromontory(
                PromontoryProbeRequirements(
                    probeIntent,
                    new[] { new Vector2Int(0, 3), new Vector2Int(0, 4) },
                    probeSource,
                    probeTarget,
                    probeFacing,
                    -probeFacing,
                    probePlanned),
                PromontoryProbeLevels(probeSource, 12, probeTarget, 8),
                out _,
                out string voidBudgetError);
            bool lowerTargetRejected = !TryResolveNamedVistaPromontory(
                PromontoryProbeRequirements(
                    probeIntent,
                    probeReserved,
                    probeSource,
                    probeTarget,
                    probeFacing,
                    -probeFacing,
                    probePlanned),
                PromontoryProbeLevels(probeSource, 8, probeTarget, 8),
                out _,
                out string lowerTargetError);
            bool validResolved = TryResolveNamedVistaPromontory(
                PromontoryProbeRequirements(
                    probeIntent,
                    probeReserved,
                    probeSource,
                    probeTarget,
                    probeFacing,
                    -probeFacing,
                    probePlanned),
                PromontoryProbeLevels(probeSource, 12, probeTarget, 8),
                out NamedVistaPromontoryResolution[] validResolutions,
                out string validError);

            JObject renderer = JObject.Parse(BuildPhase0RendererProbeJson(2026072101));
            return string.Join("\n", new[]
            {
                $"policy.version={NamedVistaPromontoryPolicyVersion}",
                $"policy.maximumCells={MaximumNamedVistaPromontoryCells}",
                $"versions.summary={DungeonPlanSummaryVersion}",
                $"versions.generator={RoutePlannerVersion}",
                PromontorySeedSnapshot("processional", processional),
                PromontorySeedSnapshot("noSurplus", noSurplus),
                PromontorySeedSnapshot("atrium", atrium),
                PromontorySeedSnapshot("twinWing", twinWing),
                $"probe.validResolved={validResolved}",
                $"probe.validResolutionCount={validResolutions.Length}",
                $"probe.validError={validError}",
                $"probe.missingIdentityRejected={missingIdentityRejected}",
                $"probe.missingIdentityError={missingIdentityError}",
                $"probe.facingRejected={facingRejected}",
                $"probe.facingError={facingError}",
                $"probe.offAxisRejected={offAxisRejected}",
                $"probe.offAxisError={offAxisError}",
                $"probe.occupiedRejected={occupiedRejected}",
                $"probe.occupiedError={occupiedError}",
                $"probe.voidBudgetRejected={voidBudgetRejected}",
                $"probe.voidBudgetError={voidBudgetError}",
                $"probe.lowerTargetRejected={lowerTargetRejected}",
                $"probe.lowerTargetError={lowerTargetError}",
                $"renderer.accepted={renderer.Value<bool?>("accepted") == true}",
                $"renderer.passed={renderer["renderer"]?.Value<bool?>("passed") == true}",
                $"renderer.rejected={renderer["renderer"]?.Value<int?>("rejectedPlacements") ?? -1}",
                $"diagnostic.seed={seed}"
            });
        }

        private static string BuildPhase6fCornerReturnSnapshot(int seed)
        {
            bool catalogValid = DungeonRecipeCatalogService.TryLoadActiveCatalog(
                out ActiveDungeonRecipeCatalog catalog,
                out string catalogError);
            DungeonRecipeAsset recipe = null;
            catalog?.TryGet(DungeonRecipeIds.CornerReturnConnector, out recipe);
            DungeonRecipeValidationResult contract = DungeonRecipeValidator.ValidateContract(recipe);
            int walkableCellCount = 0;
            int elevatedCellCount = 0;
            var protectedCells = new HashSet<Vector2Int>();
            foreach (DungeonRecipeZone zone in recipe?.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                int count = zone == null ? 0 : zone.size.x * zone.size.y;
                if (zone?.kind == DungeonRecipeZoneKind.Walkable) walkableCellCount += count;
                if (zone?.kind == DungeonRecipeZoneKind.Elevated) elevatedCellCount += count;
                if (zone?.kind == DungeonRecipeZoneKind.ProtectedCirculation)
                {
                    foreach (Vector2Int cell in DungeonRecipeAuthoringWindow.ZoneCells(zone))
                    {
                        protectedCells.Add(cell);
                    }
                }
            }

            DungeonRecipePort contractEntry = null;
            DungeonRecipePort contractExit = null;
            foreach (DungeonRecipePort port in recipe?.ports ?? Array.Empty<DungeonRecipePort>())
            {
                if (string.Equals(port?.id, "entry", StringComparison.Ordinal)) contractEntry = port;
                if (string.Equals(port?.id, "exit", StringComparison.Ordinal)) contractExit = port;
            }

            DungeonRecipeTransition contractTransition = recipe?.transitions?.Length == 1
                ? recipe.transitions[0]
                : null;
            DungeonRecipeAsset staleRecipe = recipe == null ? null : Instantiate(recipe);
            if (staleRecipe != null)
            {
                staleRecipe.hideFlags = HideFlags.HideAndDontSave;
                staleRecipe.contentVersion++;
            }
            bool staleReviewDetected = staleRecipe != null && !DungeonRecipeValidator.ReviewIsCurrent(staleRecipe);
            bool staleExcluded = staleRecipe != null && !DungeonRecipeCatalogService.IsEligibleForOrdinaryGeneration(staleRecipe);
            if (staleRecipe != null) DestroyImmediate(staleRecipe);

            bool firstGalleryPassed = DungeonRecipeAuthoringService.TryBuildReviewGallery(
                recipe,
                seed,
                out string firstGalleryPath,
                out string firstGalleryMessage);
            JObject firstGallery = firstGalleryPassed
                ? JObject.Parse(File.ReadAllText(firstGalleryPath))
                : new JObject();
            bool secondGalleryPassed = DungeonRecipeAuthoringService.TryBuildReviewGallery(
                recipe,
                seed,
                out string secondGalleryPath,
                out string secondGalleryMessage);
            JObject secondGallery = secondGalleryPassed
                ? JObject.Parse(File.ReadAllText(secondGalleryPath))
                : new JObject();
            var galleryKinds = new HashSet<string>(StringComparer.Ordinal);
            var mirrorStates = new HashSet<bool>();
            foreach (JToken entry in firstGallery["entries"] as JArray ?? new JArray())
            {
                galleryKinds.Add(entry.Value<string>("kind") ?? string.Empty);
                if (entry["mirrored"] != null)
                {
                    mirrorStates.Add(entry.Value<bool>("mirrored"));
                }
            }

            var lines = new List<string>
            {
                $"versions.summary={DungeonPlanSummaryVersion}",
                $"versions.generator={RoutePlannerVersion}",
                $"versions.spatialRandom={RouteSpatialRandomVersion}",
                $"catalog.valid={catalogValid}",
                $"catalog.error={catalogError}",
                $"catalog.reviewedCount={catalog?.recipes.Length ?? 0}",
                $"catalog.digest={catalog?.digest ?? string.Empty}",
                $"recipe.id={recipe?.recipeId ?? string.Empty}",
                $"recipe.schema={recipe?.schemaVersion ?? 0}",
                $"recipe.lifecycle={recipe?.lifecycle.ToString() ?? string.Empty}",
                $"recipe.reviewCurrent={DungeonRecipeValidator.ReviewIsCurrent(recipe)}",
                $"recipe.role={((recipe?.eligibleRoles?.Length ?? 0) == 1 ? recipe.eligibleRoles[0] : string.Empty)}",
                $"recipe.beat={((recipe?.eligibleBeats?.Length ?? 0) == 1 ? recipe.eligibleBeats[0] : string.Empty)}",
                $"recipe.zoneCount={recipe?.zones?.Length ?? 0}",
                $"recipe.portCount={recipe?.ports?.Length ?? 0}",
                $"recipe.transitionCount={recipe?.transitions?.Length ?? 0}",
                $"recipe.walkableCells={walkableCellCount}",
                $"recipe.elevatedCells={elevatedCellCount}",
                $"recipe.protectedCells={protectedCells.Count}",
                $"recipe.localPortsPerpendicular={contractEntry != null && contractExit != null && contractEntry.outwardDirection.x * contractExit.outwardDirection.x + contractEntry.outwardDirection.y * contractExit.outwardDirection.y == 0}",
                $"recipe.transitionImplementation={(recipe?.motifs?.Length == 1 ? recipe.motifs[0].implementationId : string.Empty)}",
                $"recipe.transitionRise={contractTransition?.riseLevels ?? 0}",
                $"recipe.transitionLanes={contractTransition?.laneCount ?? 0}",
                $"recipe.transitionHeadroom={contractTransition?.headroomLevels ?? 0}",
                $"recipe.allowMirror={recipe?.allowMirror == true}",
                $"recipe.contract={contract.Passed}",
                $"recipe.schemaValid={contract.LayerPassed(DungeonRecipeValidationLayer.Schema)}",
                $"recipe.structureValid={contract.LayerPassed(DungeonRecipeValidationLayer.Structure)}",
                $"recipe.variationValid={contract.LayerPassed(DungeonRecipeValidationLayer.Variation)}",
                $"recipe.neighborValid={contract.LayerPassed(DungeonRecipeValidationLayer.Neighbor)}",
                $"lifecycle.staleDetected={staleReviewDetected}",
                $"lifecycle.staleExcluded={staleExcluded}",
                $"gallery.firstPassed={firstGalleryPassed}",
                $"gallery.secondPassed={secondGalleryPassed}",
                $"gallery.samePath={string.Equals(firstGalleryPath, secondGalleryPath, StringComparison.Ordinal)}",
                $"gallery.sameHash={string.Equals(firstGallery.Value<string>("galleryHash"), secondGallery.Value<string>("galleryHash"), StringComparison.Ordinal)}",
                $"gallery.entryCount={(firstGallery["entries"] as JArray)?.Count ?? 0}",
                $"gallery.requiredViews={galleryKinds.IsSupersetOf(new[] { "contract", "top_down", "player_height", "below_floor", "neighbor" })}",
                $"gallery.mirrorStateCount={mirrorStates.Count}",
                $"gallery.fullDungeon={firstGallery["fullDungeon"]?.Value<bool?>("canonicalPlan") == true}",
                $"gallery.renderer={firstGallery["fullDungeon"]?.Value<bool?>("renderer") == true}",
                $"gallery.abyss={firstGallery["fullDungeon"]?.Value<bool?>("abyssSupport") == true}",
                $"gallery.collision={firstGallery["fullDungeon"]?.Value<bool?>("collision") == true}",
                $"gallery.message={firstGalleryMessage}",
                $"gallery.secondMessage={secondGalleryMessage}"
            };

            foreach ((string prefix, int patternSeed) sample in new[]
                     {
                         ("processional", 2026072100),
                         ("atrium", 2026072101),
                         ("twinWing", 2026072103)
                     })
            {
                JObject report = BuildPhase0SeedReport(sample.patternSeed);
                JObject slot = FindRecipeProjection(
                    report["routeIntent"]?["recipeSlots"] as JArray,
                    DungeonRecipeIds.CornerReturnConnector);
                JObject resolution = FindRecipeProjection(
                    report["recipeResolutions"] as JArray,
                    DungeonRecipeIds.CornerReturnConnector);
                JObject node = report["routeIntent"]?["nodes"]?[SharedReturnRecipeNode] as JObject;
                JObject entryPort = null;
                JObject exitPort = null;
                foreach (JToken port in slot?["ports"] as JArray ?? new JArray())
                {
                    if (string.Equals(port.Value<string>("id"), "entry", StringComparison.Ordinal)) entryPort = port as JObject;
                    if (string.Equals(port.Value<string>("id"), "exit", StringComparison.Ordinal)) exitPort = port as JObject;
                }

                JObject resolvedEntry = null;
                JObject resolvedExit = null;
                foreach (JToken port in resolution?["ports"] as JArray ?? new JArray())
                {
                    if (string.Equals(port.Value<string>("id"), "entry", StringComparison.Ordinal)) resolvedEntry = port as JObject;
                    if (string.Equals(port.Value<string>("id"), "exit", StringComparison.Ordinal)) resolvedExit = port as JObject;
                }

                int entryX = resolvedEntry?["outwardDirection"]?.Value<int?>("x") ?? 0;
                int entryY = resolvedEntry?["outwardDirection"]?.Value<int?>("y") ?? 0;
                int exitX = resolvedExit?["outwardDirection"]?.Value<int?>("x") ?? 0;
                int exitY = resolvedExit?["outwardDirection"]?.Value<int?>("y") ?? 0;
                int axisX = resolution?["primaryAxis"]?.Value<int?>("x") ?? 0;
                int axisY = resolution?["primaryAxis"]?.Value<int?>("y") ?? 0;
                lines.Add($"{sample.prefix}.accepted={report.Value<bool?>("accepted") == true}");
                lines.Add($"{sample.prefix}.validation={report["validation"]?.Value<bool?>("passed") == true}");
                lines.Add($"{sample.prefix}.plannerVersion={report["routeIntent"]?.Value<string>("plannerVersion") ?? string.Empty}");
                lines.Add($"{sample.prefix}.recipeCount={(report["recipeResolutions"] as JArray)?.Count ?? 0}");
                lines.Add($"{sample.prefix}.slotNode={slot?.Value<int?>("slotNode") ?? -1}");
                lines.Add($"{sample.prefix}.nodeRole={node?.Value<string>("role") ?? string.Empty}");
                lines.Add($"{sample.prefix}.nodeBeat={node?.Value<string>("beat") ?? string.Empty}");
                lines.Add($"{sample.prefix}.orientation={slot?.Value<string>("orientationBinding") ?? string.Empty}");
                lines.Add($"{sample.prefix}.entryEdge={entryPort?.Value<string>("edgeId") ?? string.Empty}");
                lines.Add($"{sample.prefix}.exitEdge={exitPort?.Value<string>("edgeId") ?? string.Empty}");
                lines.Add($"{sample.prefix}.atomic={resolution?.Value<bool?>("atomicAndValid") == true}");
                lines.Add($"{sample.prefix}.roomIndex={resolution?.Value<int?>("roomIndex") ?? -1}");
                lines.Add($"{sample.prefix}.transitionCount={(resolution?["transitions"] as JArray)?.Count ?? 0}");
                lines.Add($"{sample.prefix}.protectedCount={(resolution?["protectedCells"] as JArray)?.Count ?? 0}");
                lines.Add($"{sample.prefix}.portsPerpendicular={entryX * exitX + entryY * exitY == 0}");
                lines.Add($"{sample.prefix}.axisMatchesExit={axisX == exitX && axisY == exitY}");
            }

            RouteIntent probeIntent = phase1LastRouteIntent;
            RecipeSlotIntent validSlot = null;
            foreach (RecipeSlotIntent slot in probeIntent?.recipeSlots ?? Array.Empty<RecipeSlotIntent>())
            {
                if (string.Equals(slot?.recipe?.recipeId, DungeonRecipeIds.CornerReturnConnector, StringComparison.Ordinal))
                {
                    validSlot = slot;
                    break;
                }
            }

            bool validAxisResolved = TryResolveRouteForwardRecipeAxis(
                probeIntent,
                validSlot,
                phase1LastNodeCenters,
                out Vector2Int validAxis);
            var missingExitSlot = new RecipeSlotIntent(
                SharedReturnRecipeNode,
                recipe,
                RecipeOrientationBinding.RouteForward,
                new[] { new RecipePortBinding("entry", "wing-b-11-12") });
            bool missingExitRejected = !TryResolveRouteForwardRecipeAxis(
                probeIntent,
                missingExitSlot,
                phase1LastNodeCenters,
                out _);
            var unrelatedExitSlot = new RecipeSlotIntent(
                SharedReturnRecipeNode,
                recipe,
                RecipeOrientationBinding.RouteForward,
                new[]
                {
                    new RecipePortBinding("entry", "wing-b-11-12"),
                    new RecipePortBinding("exit", "main-0-1")
                });
            bool unrelatedExitRejected = !TryResolveRouteForwardRecipeAxis(
                probeIntent,
                unrelatedExitSlot,
                phase1LastNodeCenters,
                out _);
            lines.Add($"axis.validResolved={validAxisResolved}");
            lines.Add($"axis.validCardinal={Mathf.Abs(validAxis.x) + Mathf.Abs(validAxis.y) == 1}");
            lines.Add($"axis.missingExitRejected={missingExitRejected}");
            lines.Add($"axis.unrelatedExitRejected={unrelatedExitRejected}");
            lines.Add($"diagnostic.seed={seed}");
            return string.Join("\n", lines);
        }

        private static string PromontorySeedSnapshot(string prefix, JObject report)
        {
            JArray named = report["namedPromontories"] as JArray ?? new JArray();
            JObject resolution = named.Count > 0 ? named[0] as JObject : null;
            return string.Join("\n", new[]
            {
                $"{prefix}.accepted={report.Value<bool?>("accepted") == true}",
                $"{prefix}.pattern={report["routeIntent"]?.Value<string>("patternId") ?? string.Empty}",
                $"{prefix}.validation={report["validation"]?["namedPromontories"]?.Value<bool?>("passed") == true}",
                $"{prefix}.resolutionCount={named.Count}",
                $"{prefix}.cellCount={(resolution?["cells"] as JArray)?.Count ?? 0}",
                $"{prefix}.vistaId={resolution?.Value<string>("vistaId") ?? string.Empty}",
                $"{prefix}.targetNodeId={resolution?.Value<string>("targetNodeId") ?? string.Empty}",
                $"{prefix}.remainingVoid={report["routeResolution"]?["vista"]?.Value<int?>("reservedVoidCellCount") ?? 0}"
            });
        }

        private static RouteIntent WithDiagnosticVista(RouteIntent source, RouteVistaIntent vista)
        {
            return new RouteIntent(
                source.seed,
                source.plannerVersion,
                source.patternId,
                source.nodes,
                source.traversalEdges,
                vista,
                source.elevationPolicy,
                source.recipeSlots,
                source.catalogDigest,
                source.bottomNode,
                source.topNode,
                source.branchAttachNode,
                source.branchRejoinNode,
                source.requiredCycleRank,
                source.requiredCycleCoreNodeCount,
                source.requiredJunctionDegree,
                source.plannedOverlooks,
                source.allowGenericRoomWings);
        }

        private static RouteTierRequirements PromontoryProbeRequirements(
            RouteIntent intent,
            IEnumerable<Vector2Int> reservedCells,
            Vector2Int sourceCell,
            Vector2Int targetCell,
            Vector2Int sourceFacing,
            Vector2Int targetFacing,
            Vector2Int[] plannedCells)
        {
            return new RouteTierRequirements(
                intent,
                reservedCells,
                sourceCell,
                targetCell,
                sourceFacing,
                targetFacing,
                plannedCells,
                Array.Empty<RecipePlacement>());
        }

        private static Dictionary<Vector2Int, int> PromontoryProbeLevels(
            Vector2Int sourceCell,
            int sourceLevel,
            Vector2Int targetCell,
            int targetLevel)
        {
            return new Dictionary<Vector2Int, int>
            {
                [sourceCell] = sourceLevel,
                [targetCell] = targetLevel
            };
        }

        private static RouteNodeIntent RhythmProbeNode(
            string id,
            int mainRouteOrder,
            string role,
            string beat,
            string recipeId = "")
        {
            return new RouteNodeIntent(
                id,
                role,
                beat,
                mainRouteOrder,
                branchOrder: -1,
                relativeElevationLevels: mainRouteOrder * MajorRiseLevels,
                landmarkSlotId: recipeId);
        }

        private static string BuildRouteGraphCompositionSnapshot(int seed)
        {
            RouteIntent intent = BuildDiagnosticRouteIntent(seed);
            var nodeIds = new List<string>(intent.nodes.Length);
            var edgeIds = new List<string>(intent.traversalEdges.Length);
            var edgeDetails = new List<string>(intent.traversalEdges.Length);
            foreach (RouteNodeIntent node in intent.nodes)
            {
                nodeIds.Add(node.id);
            }

            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                edgeIds.Add(edge.id);
                edgeDetails.Add(
                    $"{edge.id}:{intent.nodes[edge.fromNode].id}>{intent.nodes[edge.toNode].id}:" +
                    $"{edge.transitionKind}:{edge.requiredRiseLevels}");
            }

            var contract = new RouteGraphComposer();
            bool spineAdded = contract.TryAddSpine(
                new[]
                {
                    new RouteNodeIntent("test-a", "arrival", "arrival", 0, -1, 0),
                    new RouteNodeIntent("test-b", "culmination", "culmination", 1, -1, MajorRiseLevels)
                },
                new[] { "test-main" },
                new[] { RouteTransitionKind.Stair },
                out int[] contractSpine,
                out _);
            int nodesAfterSpine = contract.NodeCount;
            int edgesAfterSpine = contract.EdgeCount;
            bool duplicateNodeRejected = !contract.TryAddBranch(
                contractSpine[0],
                new[] { new RouteNodeIntent("test-b", "connector", "branch", -1, 0, 0) },
                new[] { "test-duplicate-node" },
                new[] { RouteTransitionKind.LevelCorridor },
                out _,
                out _);
            bool duplicateEdgeRejected = !contract.TryAddBranch(
                contractSpine[0],
                new[] { new RouteNodeIntent("test-c", "connector", "branch", -1, 0, 0) },
                new[] { "test-main" },
                new[] { RouteTransitionKind.LevelCorridor },
                out _,
                out _);
            bool missingEndpointRejected = !contract.TryAddBranch(
                99,
                new[] { new RouteNodeIntent("test-c", "connector", "branch", -1, 0, 0) },
                new[] { "test-branch" },
                new[] { RouteTransitionKind.LevelCorridor },
                out _,
                out _);
            bool failedBranchesWereAtomic = contract.NodeCount == nodesAfterSpine &&
                contract.EdgeCount == edgesAfterSpine;
            bool branchAdded = contract.TryAddBranch(
                contractSpine[0],
                new[] { new RouteNodeIntent("test-c", "connector", "branch", -1, 0, 0) },
                new[] { "test-branch" },
                new[] { RouteTransitionKind.LevelCorridor },
                out int[] contractBranch,
                out _);
            int nodesBeforeInvalidRejoins = contract.NodeCount;
            int edgesBeforeInvalidRejoins = contract.EdgeCount;
            bool selfEdgeRejected = !contract.TryRejoin(
                contractBranch[0],
                contractBranch[0],
                "test-self",
                RouteTransitionKind.LevelCorridor,
                out _);
            bool missingRejoinTargetRejected = !contract.TryRejoin(
                contractBranch[0],
                99,
                "test-missing-target",
                RouteTransitionKind.LevelCorridor,
                out _);
            bool failedRejoinsWereAtomic = contract.NodeCount == nodesBeforeInvalidRejoins &&
                contract.EdgeCount == edgesBeforeInvalidRejoins;
            bool rejoinAdded = contract.TryRejoin(
                contractBranch[0],
                contractSpine[1],
                "test-rejoin",
                RouteTransitionKind.Stair,
                out _);
            bool secondRejoinRejected = !contract.TryRejoin(
                contractBranch[0],
                contractSpine[0],
                "test-second-rejoin",
                RouteTransitionKind.LevelCorridor,
                out _);
            bool published = contract.TryPublish(
                out RouteNodeIntent[] contractNodes,
                out RouteTraversalIntent[] contractEdges,
                out _);
            bool publishedGraphHasOneCycle = published &&
                contractEdges.Length - (contractNodes.Length - 1) == 1;
            bool publishedGraphIsImmutable = !contract.TryAddBranch(
                contractSpine[0],
                new[] { new RouteNodeIntent("test-d", "connector", "branch", -1, 1, 0) },
                new[] { "test-after-publish" },
                new[] { RouteTransitionKind.LevelCorridor },
                out _,
                out _);

            return string.Join("\n", new[]
            {
                $"graph.pattern={intent.patternId}",
                $"graph.operations=spine,branch,rejoin",
                $"graph.nodeIds={string.Join("|", nodeIds)}",
                $"graph.edgeIds={string.Join("|", edgeIds)}",
                $"graph.edgeDetails={string.Join("|", edgeDetails)}",
                $"graph.loopEdges={intent.traversalEdges.Length - (intent.nodes.Length - 1)}",
                $"contract.spineAdded={spineAdded}",
                $"contract.branchAdded={branchAdded}",
                $"contract.rejoinAdded={rejoinAdded}",
                $"contract.duplicateNodeRejected={duplicateNodeRejected}",
                $"contract.duplicateEdgeRejected={duplicateEdgeRejected}",
                $"contract.missingEndpointRejected={missingEndpointRejected}",
                $"contract.selfEdgeRejected={selfEdgeRejected}",
                $"contract.missingRejoinTargetRejected={missingRejoinTargetRejected}",
                $"contract.secondRejoinRejected={secondRejoinRejected}",
                $"contract.failedBranchesWereAtomic={failedBranchesWereAtomic}",
                $"contract.failedRejoinsWereAtomic={failedRejoinsWereAtomic}",
                $"contract.publishedGraphHasOneCycle={publishedGraphHasOneCycle}",
                $"contract.publishedGraphIsImmutable={publishedGraphIsImmutable}"
            });
        }

        private static string BuildPhase5RecipeContractSnapshot(int seed)
        {
            RouteIntent intent = BuildDiagnosticRouteIntent(seed);
            DungeonRecipeCatalogService.TryLoadActiveCatalog(
                out ActiveDungeonRecipeCatalog catalog,
                out string catalogError);
            var lines = new List<string>
            {
                $"catalog.valid={catalog != null}",
                $"catalog.error={catalogError}",
                $"catalog.reviewedCount={catalog?.recipes.Length ?? 0}",
                $"catalog.digest={catalog?.digest ?? string.Empty}",
                $"route.recipeSlotCount={intent.recipeSlots.Length}",
                $"route.catalogDigestMatches={string.Equals(intent.catalogDigest, catalog?.digest, StringComparison.Ordinal)}",
                $"schema.fieldCount={BuildRecipeSchemaUsageProjection().Value<int>("fieldCount")}",
                $"schema.allFieldsConsumed={BuildRecipeSchemaUsageProjection().Value<bool>("allFieldsConsumed")}"
            };
            int recipeIndex = 0;
            foreach (DungeonRecipeAsset recipe in catalog?.recipes ?? Array.Empty<DungeonRecipeAsset>())
            {
                DungeonRecipeValidationResult validation = DungeonRecipeValidator.ValidateContract(recipe);
                string prefix = $"recipe{recipeIndex++}";
                RecipeSlotIntent slot = null;
                foreach (RecipeSlotIntent candidate in intent.recipeSlots)
                {
                    if (string.Equals(candidate.recipe.recipeId, recipe.recipeId, StringComparison.Ordinal))
                    {
                        slot = candidate;
                        break;
                    }
                }
                lines.Add($"{prefix}.id={recipe.recipeId}");
                lines.Add($"{prefix}.schema={recipe.schemaVersion}");
                lines.Add($"{prefix}.slotNode={slot?.slotNode ?? -1}");
                lines.Add($"{prefix}.orientationBinding={slot?.orientationBinding.ToString() ?? string.Empty}");
                lines.Add($"{prefix}.reviewCurrent={DungeonRecipeValidator.ReviewIsCurrent(recipe)}");
                lines.Add($"{prefix}.schemaValid={validation.LayerPassed(DungeonRecipeValidationLayer.Schema)}");
                lines.Add($"{prefix}.structureValid={validation.LayerPassed(DungeonRecipeValidationLayer.Structure)}");
                lines.Add($"{prefix}.variationValid={validation.LayerPassed(DungeonRecipeValidationLayer.Variation)}");
                lines.Add($"{prefix}.neighborValid={validation.LayerPassed(DungeonRecipeValidationLayer.Neighbor)}");
                lines.Add($"{prefix}.ports={recipe.ports.Length}");
                lines.Add($"{prefix}.transitions={recipe.transitions.Length}");
                lines.Add($"{prefix}.symmetryPairs={recipe.symmetryPairs.Length}");
                lines.Add($"{prefix}.variations={recipe.variations.Length}");
                AppendIsolatedRecipeEvidence(lines, prefix, seed, intent, slot);
            }

            return string.Join("\n", lines);
        }

        private static void AppendIsolatedRecipeEvidence(
            List<string> lines,
            string prefix,
            int seed,
            RouteIntent routeIntent,
            RecipeSlotIntent slot)
        {
            Vector2Int[] axes =
            {
                Vector2Int.up,
                Vector2Int.right,
                Vector2Int.down,
                Vector2Int.left
            };
            int alternativeCount = Math.Max(1, slot?.recipe?.variations?.Length ?? 0);
            var combinations = new HashSet<string>(StringComparer.Ordinal);
            bool geometryValid = slot?.recipe != null;
            bool visualAssetsValid = geometryValid;
            foreach (DungeonRecipeVariation variation in slot?.recipe?.variations ?? Array.Empty<DungeonRecipeVariation>())
            {
                DungeonRecipeMotif motif = FindRecipeMotif(slot.recipe, variation.motifId);
                visualAssetsValid &= motif != null &&
                    StairForge.TryGetBackedShowpieceDesign(motif.implementationId, out _);
            }

            Vector2Int center = new Vector2Int(20, 20);
            foreach (Vector2Int primaryAxis in axes)
            {
                Vector2Int transverse = new Vector2Int(-primaryAxis.y, primaryAxis.x);
                var nodeCenters = new Vector2Int[routeIntent.nodes.Length];
                nodeCenters[slot.slotNode] = center;
                foreach (DungeonRecipePort port in slot.recipe.ports)
                {
                    if (!slot.TryGetEdgeId(port.id, out string edgeId) ||
                        !TryGetTraversal(routeIntent, edgeId, out RouteTraversalIntent edge))
                    {
                        geometryValid = false;
                        continue;
                    }

                    int neighbor = edge.fromNode == slot.slotNode ? edge.toNode : edge.fromNode;
                    Vector2Int outward = TransformRecipeDirection(
                        port.outwardDirection,
                        primaryAxis,
                        transverse,
                        mirrored: false);
                    nodeCenters[neighbor] = center + outward * 9;
                }

                var rooms = new List<RoomFootprint>();
                for (int index = 0; index < nodeCenters.Length; index++)
                {
                    rooms.Add(RoomFootprint.FromRect(new RectInt(100 + index * 2, 100, 1, 1)));
                }

                rooms[slot.slotNode] = new RoomFootprint(
                    BuildRecipeRoomParts(slot, center, primaryAxis, mirrored: false));
                int expectedCombinations = axes.Length * alternativeCount;
                for (int attempt = 0; attempt < 64 && combinations.Count < expectedCombinations; attempt++)
                {
                    if (!TryPlaceRecipe(
                            seed,
                            attempt,
                            routeIntent,
                            slot,
                            rooms,
                            nodeCenters,
                            primaryAxis,
                            -primaryAxis,
                            out RecipePlacement placement,
                            out _))
                    {
                        geometryValid = false;
                        continue;
                    }

                    geometryValid &=
                        placement.primaryAxis == primaryAxis &&
                        placement.protectedCells.Length > 0 &&
                        placement.zones.Length == slot.recipe.zones.Length &&
                        placement.ports.Length == slot.recipe.ports.Length &&
                        placement.transitions.Length == slot.recipe.transitions.Length;
                    string alternative = string.IsNullOrEmpty(placement.selectedVisualImplementationId)
                        ? "structural"
                        : placement.selectedVisualImplementationId;
                    combinations.Add($"{primaryAxis.x},{primaryAxis.y}:{alternative}");
                }
            }

            lines.Add($"{prefix}.isolatedOrientationCount={axes.Length}");
            lines.Add($"{prefix}.isolatedAlternativeCount={alternativeCount}");
            lines.Add($"{prefix}.isolatedCombinationCount={combinations.Count}");
            lines.Add($"{prefix}.isolatedGeometryValid={geometryValid}");
            lines.Add($"{prefix}.isolatedVisualAssetsValid={visualAssetsValid}");
        }

        private static string BuildPhase5FullDungeonSnapshot(int seed)
        {
            JObject report = BuildPhase0SeedReport(seed);
            JObject renderer = JObject.Parse(BuildPhase0RendererProbeJson(seed));
            var lines = new List<string>
            {
                SnapshotLine("accepted", report["accepted"]),
                SnapshotLine("validation.passed", report["validation"]?["passed"]),
                SnapshotLine("validation.recipes", report["validation"]?["recipes"]?["passed"]),
                SnapshotLine("renderer.passed", renderer["renderer"]?["passed"]),
                SnapshotLine("renderer.rejectedPlacements", renderer["renderer"]?["rejectedPlacements"]),
                SnapshotLine("abyss.passed", renderer["boundary"]?["passed"]),
                SnapshotLine("collision.passed", renderer["collisionPreconditions"]?["passed"])
            };
            AppendRecipeResolutionSnapshot(lines, report["recipeResolutions"] as JArray);

            return string.Join("\n", lines);
        }

        private static void AppendRecipeResolutionSnapshot(List<string> lines, JArray recipes)
        {
            lines.Add($"recipes.count={recipes?.Count ?? 0}");
            for (int index = 0; index < (recipes?.Count ?? 0); index++)
            {
                JToken token = recipes[index];
                string prefix = $"recipe{index}";
                int elevatedZoneCount = 0;
                int protectedFocalCellCount = 0;
                int elevatedLevel = token.Value<int?>("baseLevel") ?? 0;
                foreach (JToken zone in token["zones"] as JArray ?? new JArray())
                {
                    if (string.Equals(
                            zone.Value<string>("kind"),
                            DungeonRecipeZoneKind.Elevated.ToString(),
                            StringComparison.Ordinal))
                    {
                        elevatedZoneCount++;
                        elevatedLevel = Math.Max(
                            elevatedLevel,
                            (token.Value<int?>("baseLevel") ?? 0) +
                            (zone.Value<int?>("relativeLevel") ?? 0));
                    }

                    if (string.Equals(
                            zone.Value<string>("kind"),
                            DungeonRecipeZoneKind.ProtectedFocal.ToString(),
                            StringComparison.Ordinal))
                    {
                        protectedFocalCellCount += (zone["cells"] as JArray)?.Count ?? 0;
                    }
                }

                lines.Add(SnapshotLine($"{prefix}.id", token["id"]));
                lines.Add(SnapshotLine($"{prefix}.atomic", token["atomicAndValid"]));
                lines.Add(SnapshotLine($"{prefix}.roomIndex", token["roomIndex"]));
                lines.Add(SnapshotLine($"{prefix}.primaryAxis", token["primaryAxis"]));
                lines.Add(SnapshotLine($"{prefix}.ports", token["ports"] is JArray ports ? ports.Count : 0));
                lines.Add(SnapshotLine($"{prefix}.portsBound", token["mandatoryPortsBound"]));
                lines.Add(SnapshotLine($"{prefix}.transitions", token["transitions"] is JArray transitions ? transitions.Count : 0));
                lines.Add(SnapshotLine($"{prefix}.reservationsComplete", token["reservationsComplete"]));
                lines.Add(SnapshotLine($"{prefix}.protected", token["protectedCells"] is JArray protectedCells ? protectedCells.Count : 0));
                lines.Add(SnapshotLine($"{prefix}.protectedZonesValid", token["protectedZonesValid"]));
                lines.Add(SnapshotLine($"{prefix}.elevatedZones", elevatedZoneCount));
                lines.Add(SnapshotLine($"{prefix}.protectedFocalCells", protectedFocalCellCount));
                lines.Add(SnapshotLine($"{prefix}.baseLevel", token["baseLevel"]));
                lines.Add(SnapshotLine($"{prefix}.elevatedLevel", elevatedLevel));
                lines.Add(SnapshotLine($"{prefix}.variation", token["selectedVariationId"]));
                lines.Add(SnapshotLine($"{prefix}.visualImplementation", token["selectedVisualImplementationId"]));
            }
        }

        private static string BuildPhase5LifecycleSnapshot(int seed)
        {
            DungeonRecipeCatalogService.TryLoadActiveCatalog(
                out ActiveDungeonRecipeCatalog catalog,
                out string catalogError);
            DungeonRecipeAsset source = null;
            catalog?.TryGet(DungeonRecipeIds.CompressionConnector, out source);
            string before = EditorJsonUtility.ToJson(source);
            DungeonRecipeValidationResult validation = DungeonRecipeValidator.ValidateContract(source);
            string after = EditorJsonUtility.ToJson(source);

            var stale = Instantiate(source);
            stale.hideFlags = HideFlags.HideAndDontSave;
            stale.contentVersion++;
            bool staleReview = !DungeonRecipeValidator.ReviewIsCurrent(stale);
            bool staleEligible = DungeonRecipeCatalogService.IsEligibleForOrdinaryGeneration(stale);

            TryBuildRecipeFullDungeonEvidence(
                source.recipeId,
                seed,
                out DungeonRecipeFullDungeonEvidence evidence,
                out _);
            var invalid = Instantiate(source);
            invalid.hideFlags = HideFlags.HideAndDontSave;
            invalid.lifecycle = DungeonRecipeLifecycle.Draft;
            invalid.reviewedDigest = string.Empty;
            invalid.reviewer = string.Empty;
            invalid.reviewedAtUtc = string.Empty;
            invalid.transitions[0].upperLandingCells = Array.Empty<Vector2Int>();
            bool invalidPromoted = DungeonRecipeLifecycleService.TryPromote(
                invalid,
                "test-reviewer",
                "invalid",
                evidence,
                out DungeonRecipeValidationResult invalidValidation);

            var draft = Instantiate(source);
            draft.hideFlags = HideFlags.HideAndDontSave;
            draft.lifecycle = DungeonRecipeLifecycle.Draft;
            draft.reviewedDigest = string.Empty;
            draft.reviewer = string.Empty;
            draft.reviewedAtUtc = string.Empty;
            bool draftPromoted = DungeonRecipeLifecycleService.TryPromote(
                draft,
                "test-reviewer",
                "valid",
                evidence,
                out DungeonRecipeValidationResult promotionValidation);
            bool promotedCurrent = DungeonRecipeValidator.ReviewIsCurrent(draft);
            bool promotionMetadataRecorded =
                !string.IsNullOrEmpty(draft.reviewedDigest) &&
                !string.IsNullOrEmpty(draft.reviewer) &&
                !string.IsNullOrEmpty(draft.reviewedAtUtc);
            bool promotedEligible = DungeonRecipeCatalogService.IsEligibleForOrdinaryGeneration(draft);

            DestroyImmediate(stale);
            DestroyImmediate(invalid);
            DestroyImmediate(draft);
            return string.Join("\n", new[]
            {
                $"catalog.error={catalogError}",
                $"validation.passed={validation.Passed}",
                $"validation.nonMutating={string.Equals(before, after, StringComparison.Ordinal)}",
                $"stale.detected={staleReview}",
                $"stale.eligible={staleEligible}",
                $"invalid.promoted={invalidPromoted}",
                $"invalid.structurePassed={invalidValidation.LayerPassed(DungeonRecipeValidationLayer.Structure)}",
                $"draft.promoted={draftPromoted}",
                $"draft.allLayersPassed={promotionValidation.Passed}",
                $"draft.reviewCurrent={promotedCurrent}",
                $"draft.reviewMetadataRecorded={promotionMetadataRecorded}",
                $"draft.ordinaryGenerationEligible={promotedEligible}"
            });
        }

        private static string BuildPhase5WorkflowSnapshot(int seed)
        {
            DungeonRecipeCatalogService.TryLoadActiveCatalog(
                out ActiveDungeonRecipeCatalog catalog,
                out string catalogError);
            DungeonRecipeAsset throne = null;
            DungeonRecipeAsset vestibule = null;
            DungeonRecipeAsset cornerReturn = null;
            catalog?.TryGet(DungeonRecipeIds.ProcessionalLandmark, out throne);
            catalog?.TryGet(DungeonRecipeIds.CompressionConnector, out vestibule);
            catalog?.TryGet(DungeonRecipeIds.CornerReturnConnector, out cornerReturn);
            bool firstPassed = DungeonRecipeAuthoringService.TryBuildReviewGallery(
                throne,
                seed,
                out string firstPath,
                out string firstMessage);
            JObject first = firstPassed ? JObject.Parse(File.ReadAllText(firstPath)) : new JObject();
            bool secondPassed = DungeonRecipeAuthoringService.TryBuildReviewGallery(
                throne,
                seed,
                out string secondPath,
                out string secondMessage);
            JObject second = secondPassed ? JObject.Parse(File.ReadAllText(secondPath)) : new JObject();
            bool contrastFirstPassed = DungeonRecipeAuthoringService.TryBuildReviewGallery(
                vestibule,
                seed,
                out string contrastFirstPath,
                out string contrastFirstMessage);
            JObject contrastFirst = contrastFirstPassed
                ? JObject.Parse(File.ReadAllText(contrastFirstPath))
                : new JObject();
            bool contrastSecondPassed = DungeonRecipeAuthoringService.TryBuildReviewGallery(
                vestibule,
                seed,
                out string contrastSecondPath,
                out string contrastSecondMessage);
            JObject contrastSecond = contrastSecondPassed
                ? JObject.Parse(File.ReadAllText(contrastSecondPath))
                : new JObject();
            bool thirdFirstPassed = DungeonRecipeAuthoringService.TryBuildReviewGallery(
                cornerReturn,
                seed,
                out string thirdFirstPath,
                out string thirdFirstMessage);
            JObject thirdFirst = thirdFirstPassed
                ? JObject.Parse(File.ReadAllText(thirdFirstPath))
                : new JObject();
            bool thirdSecondPassed = DungeonRecipeAuthoringService.TryBuildReviewGallery(
                cornerReturn,
                seed,
                out string thirdSecondPath,
                out string thirdSecondMessage);
            JObject thirdSecond = thirdSecondPassed
                ? JObject.Parse(File.ReadAllText(thirdSecondPath))
                : new JObject();
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            var mirrorStates = new HashSet<bool>();
            foreach (JToken entry in first["entries"] as JArray ?? new JArray())
            {
                kinds.Add(entry.Value<string>("kind") ?? string.Empty);
                if (entry["mirrored"] != null) mirrorStates.Add(entry.Value<bool>("mirrored"));
            }
            var contrastKinds = new HashSet<string>(StringComparer.Ordinal);
            var contrastMirrorStates = new HashSet<bool>();
            foreach (JToken entry in contrastFirst["entries"] as JArray ?? new JArray())
            {
                contrastKinds.Add(entry.Value<string>("kind") ?? string.Empty);
                if (entry["mirrored"] != null) contrastMirrorStates.Add(entry.Value<bool>("mirrored"));
            }
            var thirdKinds = new HashSet<string>(StringComparer.Ordinal);
            var thirdMirrorStates = new HashSet<bool>();
            foreach (JToken entry in thirdFirst["entries"] as JArray ?? new JArray())
            {
                thirdKinds.Add(entry.Value<string>("kind") ?? string.Empty);
                if (entry["mirrored"] != null) thirdMirrorStates.Add(entry.Value<bool>("mirrored"));
            }

            return string.Join("\n", new[]
            {
                $"catalog.error={catalogError}",
                $"gallery.firstPassed={firstPassed}",
                $"gallery.secondPassed={secondPassed}",
                $"gallery.samePath={string.Equals(firstPath, secondPath, StringComparison.Ordinal)}",
                $"gallery.sameHash={string.Equals(first.Value<string>("galleryHash"), second.Value<string>("galleryHash"), StringComparison.Ordinal)}",
                $"gallery.entryCount={(first["entries"] as JArray)?.Count ?? 0}",
                $"gallery.contract={kinds.Contains("contract")}",
                $"gallery.topDown={kinds.Contains("top_down")}",
                $"gallery.playerHeight={kinds.Contains("player_height")}",
                $"gallery.belowFloor={kinds.Contains("below_floor")}",
                $"gallery.neighbor={kinds.Contains("neighbor")}",
                $"gallery.mirrorStateCount={mirrorStates.Count}",
                $"gallery.fullDungeon={first["fullDungeon"]?.Value<bool?>("canonicalPlan") == true}",
                $"contrast.firstPassed={contrastFirstPassed}",
                $"contrast.secondPassed={contrastSecondPassed}",
                $"contrast.samePath={string.Equals(contrastFirstPath, contrastSecondPath, StringComparison.Ordinal)}",
                $"contrast.sameHash={string.Equals(contrastFirst.Value<string>("galleryHash"), contrastSecond.Value<string>("galleryHash"), StringComparison.Ordinal)}",
                $"contrast.entryCount={(contrastFirst["entries"] as JArray)?.Count ?? 0}",
                $"contrast.requiredViews={contrastKinds.IsSupersetOf(new[] { "contract", "top_down", "player_height", "below_floor", "neighbor" })}",
                $"contrast.mirrorStateCount={contrastMirrorStates.Count}",
                $"contrast.fullDungeon={contrastFirst["fullDungeon"]?.Value<bool?>("canonicalPlan") == true}",
                $"third.firstPassed={thirdFirstPassed}",
                $"third.secondPassed={thirdSecondPassed}",
                $"third.samePath={string.Equals(thirdFirstPath, thirdSecondPath, StringComparison.Ordinal)}",
                $"third.sameHash={string.Equals(thirdFirst.Value<string>("galleryHash"), thirdSecond.Value<string>("galleryHash"), StringComparison.Ordinal)}",
                $"third.entryCount={(thirdFirst["entries"] as JArray)?.Count ?? 0}",
                $"third.requiredViews={thirdKinds.IsSupersetOf(new[] { "contract", "top_down", "player_height", "below_floor", "neighbor" })}",
                $"third.mirrorStateCount={thirdMirrorStates.Count}",
                $"third.fullDungeon={thirdFirst["fullDungeon"]?.Value<bool?>("canonicalPlan") == true}",
                $"gallery.message={firstMessage}",
                $"gallery.secondMessage={secondMessage}",
                $"contrast.message={contrastFirstMessage}",
                $"contrast.secondMessage={contrastSecondMessage}",
                $"third.message={thirdFirstMessage}",
                $"third.secondMessage={thirdSecondMessage}"
            });
        }

        // Reflection entry point for the one-seed renderer/collision precondition
        // probe. It destroys all generated scene objects before returning.
        private static string BuildPhase0RendererProbeJson(int seed)
        {
            GameObject root = null;
            try
            {
                root = BuildPhase0RenderedSeed(
                    seed,
                    out Bounds bounds,
                    out JObject seedReport,
                    out ElevationEdgeModel.BuildReport buildReport);
                Collider[] colliders = root.GetComponentsInChildren<Collider>(includeInactive: false);
                int enabledCollisionSources = 0;
                int meshColliderCount = 0;
                int missingMeshCount = 0;
                int unreadableMeshCount = 0;
                int selectedShowpieceCount = 0;
                JObject visualRecipe = FindRecipeProjection(
                    seedReport["recipeResolutions"] as JArray,
                    DungeonRecipeIds.ProcessionalLandmark);
                string focalDesignId = visualRecipe?.Value<string>("selectedVisualImplementationId") ?? string.Empty;
                string focalRootPrefix = $"dais_showpiece_{focalDesignId}_";
                foreach (Transform child in root.GetComponentsInChildren<Transform>(includeInactive: false))
                {
                    if (!string.IsNullOrEmpty(focalDesignId) &&
                        child.name.StartsWith(focalRootPrefix, StringComparison.Ordinal))
                    {
                        selectedShowpieceCount++;
                    }
                }

                foreach (Collider collider in colliders)
                {
                    if (collider == null || !collider.enabled || collider.isTrigger)
                    {
                        continue;
                    }

                    enabledCollisionSources++;
                    if (collider is MeshCollider meshCollider)
                    {
                        meshColliderCount++;
                        if (meshCollider.sharedMesh == null)
                        {
                            missingMeshCount++;
                        }
                        else if (!meshCollider.sharedMesh.isReadable)
                        {
                            // RandomDungeonSceneBuilder makes these readable before
                            // collision export. Count them here without mutating importers.
                            unreadableMeshCount++;
                        }
                    }
                }

                bool rendererPassed =
                    buildReport.rejected == 0 &&
                    buildReport.floorCells > 0 &&
                    buildReport.transitionEdges > 0 &&
                    visualRecipe?.Value<bool?>("atomicAndValid") == true &&
                    selectedShowpieceCount == 1 &&
                    bounds.size.sqrMagnitude > 0.01f;
                bool collisionPreconditionsPassed =
                    enabledCollisionSources > 0 &&
                    missingMeshCount == 0;
                var report = new JObject
                {
                    ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                    ["seed"] = seed,
                    ["accepted"] = true,
                    ["profile"] = seedReport["profile"]?.DeepClone(),
                    ["settingsDigest"] = seedReport["settingsDigest"]?.DeepClone(),
                    ["settings"] = seedReport["settings"]?.DeepClone(),
                    ["seedReportHash"] = seedReport["hashes"]?["canonical"],
                    ["boundary"] = seedReport["validation"]?["boundary"],
                    ["measurements"] = seedReport["measurements"]?.DeepClone(),
                    ["renderer"] = new JObject
                    {
                        ["passed"] = rendererPassed,
                        ["floorCells"] = buildReport.floorCells,
                        ["transitionEdges"] = buildReport.transitionEdges,
                        ["stairFootprintChecks"] = buildReport.stairFootprintChecks,
                        ["multiRiseStairChecks"] = buildReport.multiRiseStairChecks,
                        ["selectedShowpieces"] = selectedShowpieceCount,
                        ["rejectedPlacements"] = buildReport.rejected,
                        ["boundsSize"] = Vector3Token(bounds.size),
                        ["summary"] = buildReport.Summary
                    },
                    ["collisionPreconditions"] = new JObject
                    {
                        ["passed"] = collisionPreconditionsPassed,
                        ["enabledNonTriggerColliders"] = enabledCollisionSources,
                        ["meshColliders"] = meshColliderCount,
                        ["missingMeshes"] = missingMeshCount,
                        ["meshesRequiringReadWriteNormalization"] = unreadableMeshCount
                    }
                };
                return report.ToString(Formatting.None);
            }
            catch (Exception exception)
            {
                return new JObject
                {
                    ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                    ["seed"] = seed,
                    ["profile"] = ResolveRequestedGenerationProfileId(),
                    ["settingsDigest"] = JValue.CreateNull(),
                    ["settings"] = JValue.CreateNull(),
                    ["accepted"] = false,
                    ["failureCode"] = Phase0RejectionCode(exception.Message, exception),
                    ["failure"] = exception.Message,
                    ["measurements"] = new JObject
                    {
                        ["available"] = false,
                        ["reason"] = "render build failed before density and adjacency measurements were available"
                    }
                }.ToString(Formatting.None);
            }
            finally
            {
                if (root != null)
                {
                    DestroyImmediate(root);
                }
            }
        }

        private static string BuildRendererProbeSnapshot(int seed)
        {
            JObject report = JObject.Parse(BuildPhase0RendererProbeJson(seed));
            var lines = new List<string>
            {
                SnapshotLine("accepted", report["accepted"]),
                SnapshotLine("boundary", report["boundary"]?["passed"]),
                SnapshotLine("renderer.passed", report["renderer"]?["passed"]),
                SnapshotLine("renderer.rejectedPlacements", report["renderer"]?["rejectedPlacements"]),
                SnapshotLine("renderer.stairFootprintChecks", report["renderer"]?["stairFootprintChecks"]),
                SnapshotLine("renderer.selectedShowpieces", report["renderer"]?["selectedShowpieces"]),
                SnapshotLine("collision.passed", report["collisionPreconditions"]?["passed"]),
                SnapshotLine("collision.enabledNonTriggerColliders", report["collisionPreconditions"]?["enabledNonTriggerColliders"]),
                SnapshotLine("collision.missingMeshes", report["collisionPreconditions"]?["missingMeshes"]),
                SnapshotLine("failure", report["failure"])
            };
            return string.Join("\n", lines);
        }

        internal static bool TryBuildRecipeFullDungeonEvidence(
            string recipeId,
            int seed,
            out DungeonRecipeFullDungeonEvidence evidence,
            out string message)
        {
            evidence = default;
            message = string.Empty;
            JObject seedReport = BuildPhase0SeedReport(seed);
            if (seedReport.Value<bool?>("accepted") != true)
            {
                message = seedReport.Value<string>("lastRejection") ?? "full-dungeon preview rejected";
                return false;
            }

            JObject recipe = null;
            foreach (JToken token in seedReport["recipeResolutions"] as JArray ?? new JArray())
            {
                if (string.Equals(token.Value<string>("id"), recipeId, StringComparison.Ordinal))
                {
                    recipe = token as JObject;
                    break;
                }
            }

            if (recipe == null)
            {
                message = $"recipe '{recipeId}' did not resolve in the fixed full-dungeon preview";
                return false;
            }

            JObject rendererReport = JObject.Parse(BuildPhase0RendererProbeJson(seed));
            bool canonicalValid = seedReport["validation"]?.Value<bool?>("passed") == true;
            bool rendererValid = rendererReport["renderer"]?.Value<bool?>("passed") == true;
            bool boundaryValid = rendererReport["boundary"]?.Value<bool?>("passed") == true;
            bool collisionValid = rendererReport["collisionPreconditions"]?.Value<bool?>("passed") == true;
            int mandatoryPorts = 0;
            foreach (JToken port in recipe["ports"] as JArray ?? new JArray())
            {
                mandatoryPorts += port.Value<bool?>("mandatory") == true ? 1 : 0;
            }

            evidence = new DungeonRecipeFullDungeonEvidence(
                recipeId,
                recipe.Value<bool?>("atomicAndValid") == true,
                mandatoryPorts,
                (recipe["transitions"] as JArray)?.Count ?? 0,
                canonicalValid,
                rendererValid,
                boundaryValid && rendererValid,
                collisionValid);
            message = canonicalValid && rendererValid && boundaryValid && collisionValid
                ? "canonical plan, renderer, abyss boundary, and collision evidence passed"
                : $"full-dungeon evidence failed: canonical={canonicalValid}, renderer={rendererValid}, boundary={boundaryValid}, collision={collisionValid}; " +
                  $"validation={seedReport["validation"]?.ToString(Formatting.None)}; rendererReport={rendererReport.ToString(Formatting.None)}";
            return canonicalValid && rendererValid && boundaryValid && collisionValid;
        }

        private static JObject CreateAcceptedPhase0SeedReport(
            int seed,
            int layoutAttemptsUsed,
            string lastRejection,
            Dictionary<string, int> rejectionHistogram,
            DungeonLayout layout,
            TieredLevelPlan plan,
            System.Random random)
        {
            long canonicalProjectionStart = BeginPhase7OutlierStage();
            JObject canonicalLayout = BuildCanonicalLayoutProjection(layout);
            JObject canonicalPlan = BuildCanonicalTieredLevelPlanProjection(plan);
            JArray existingTransitions = BuildExistingTransitionProjection(plan.transitions);
            JObject preservedCorePlan = BuildPreservedCorePlanProjection(plan);
            JObject preCorrectivePlan = BuildPreCorrectiveTieredLevelPlanProjection(plan);
            JArray recipeResolutions = BuildRecipeResolutionsProjection(plan.recipeResolutions);
            EndPhase7OutlierStage("canonicalProjections", canonicalProjectionStart);

            long identityStart = BeginPhase7OutlierStage();
            string layoutHash = ComputeSha256(canonicalLayout.ToString(Formatting.None));
            string planHash = ComputeSha256(canonicalPlan.ToString(Formatting.None));
            string existingTransitionHash = ComputeSha256(existingTransitions.ToString(Formatting.None));
            string preservedCorePlanHash = ComputeSha256(preservedCorePlan.ToString(Formatting.None));
            string preCorrectivePlanHash = ComputeSha256(preCorrectivePlan.ToString(Formatting.None));
            string recipeResolutionHash = ComputeSha256(recipeResolutions.ToString(Formatting.None));
            JObject routeIntentProjection = BuildPhase1RouteIntentProjection();
            string routeIntentHash = ComputeSha256(routeIntentProjection.ToString(Formatting.None));
            string recipeCatalogDigest = DungeonRecipeCatalogService.TryLoadActiveCatalog(
                out ActiveDungeonRecipeCatalog activeRecipeCatalog,
                out _)
                ? activeRecipeCatalog.digest
                : string.Empty;
            string canonicalHashVersion = DungeonPlanSummaryVersion;
            string canonicalHash = ComputeSha256(
                $"{canonicalHashVersion}\n{routeIntentHash}\n{layoutHash}\n{planHash}");
            EndPhase7OutlierStage("identityHashesAndCatalog", identityStart);

            long validationStart = BeginPhase7OutlierStage();
            float correlation = CalculateDepthLevelCorrelation(layout, plan);
            JObject validation = BuildPhase0ValidationSummary(seed, layout, plan, random, out _);
            JObject graphSummary = BuildLayoutGraphSummary(layout);
            JObject routePlacement = BuildPhase1RoutePlacementProjection(layout);
            JObject densityAdjacencyMeasurements = BuildDensityAdjacencyMeasurements(
                layout,
                graphSummary,
                routePlacement,
                routeIntentProjection.Value<string>("patternId") ?? string.Empty);
            EndPhase7OutlierStage("metricsAndHardValidation", validationStart);

            long reportAssemblyStart = BeginPhase7OutlierStage();
            var report = new JObject
            {
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["seed"] = seed,
                ["catalogDigest"] = Phase0CatalogDigest(),
                ["accepted"] = true,
                ["layoutAttempts"] = layoutAttemptsUsed,
                ["lastRejectedAttempt"] = string.IsNullOrEmpty(lastRejection) ? null : lastRejection,
                ["lastRejectedAttemptCode"] = string.IsNullOrEmpty(lastRejection)
                    ? null
                    : Phase0RejectionCode(lastRejection, exception: null),
                ["rejectionHistogram"] = HistogramToken(rejectionHistogram),
                ["rejectionCodes"] = RejectionCodeHistogramToken(rejectionHistogram),
                ["layout"] = new JObject
                {
                    ["floorCells"] = layout.floorCells.Count,
                    ["rooms"] = layout.rooms.Count,
                    ["connections"] = layout.connections.Count,
                    ["roomZones"] = layout.roomZones.Count,
                    ["floorFillPercent"] = CalculateFloorFillPercent(layout.floorCells) * 100f,
                    ["graph"] = graphSummary
                },
                ["tieredLevelPlan"] = new JObject
                {
                    ["archetype"] = plan.archetypeName,
                    ["levelCount"] = plan.levelCount,
                    ["minLevel"] = plan.minLevel,
                    ["maxLevel"] = plan.maxLevel,
                    ["elevationSpan"] = plan.maxLevel - plan.minLevel,
                    ["transitionCount"] = plan.transitions.Count,
                    ["transitionSummary"] = plan.transitionSummary,
                    ["stairUsage"] = plan.stairUsageSummary,
                    ["stairTopology"] = plan.topologySummary,
                    ["stairPlacementClass"] = plan.placementClassSummary,
                    ["portGraph"] = plan.portGraphSummary,
                    ["visibleDistantRoomProxyCount"] = plan.overlookCount,
                    ["visibleDistantRoomMeasurement"] = "adjacent-cell elevation delta >= 4u; current generator has no explicit line-of-sight graph",
                    ["synthesizedStairs"] = plan.synthesizedStairs == null ? 0 : plan.synthesizedStairs.Count,
                    ["promontories"] = plan.namedPromontories?.Length ?? 0,
                    ["promontoryCells"] = CollectNamedPromontoryCells(plan.namedPromontories).Count,
                    ["externalConnectorCount"] = plan.externalConnectors?.Length ?? 0,
                    ["externalConnectorPierCells"] = CollectExternalConnectorPierCells(plan.externalConnectors).Count,
                    ["recipeCount"] = plan.recipeResolutions?.Length ?? 0,
                    ["depthLevelCorrelation"] = float.IsNaN(correlation) ? JValue.CreateNull() : new JValue(correlation)
                },
                ["validation"] = validation,
                ["hashes"] = new JObject
                {
                    ["algorithm"] = "SHA-256",
                    ["canonicalVersion"] = canonicalHashVersion,
                    ["layout"] = layoutHash,
                    ["tieredLevelPlan"] = planHash,
                    ["existingTransitions"] = existingTransitionHash,
                    ["preservedCorePlan"] = preservedCorePlanHash,
                    ["preCorrectiveTieredLevelPlan"] = preCorrectivePlanHash,
                    ["recipeResolutions"] = recipeResolutionHash,
                    ["recipeCatalog"] = recipeCatalogDigest,
                    ["canonical"] = canonicalHash
                }
            };
            AddGenerationSettingsIdentity(report);
            report["routeIntent"] = routeIntentProjection;
            report["routePlacement"] = routePlacement;
            report["measurements"] = densityAdjacencyMeasurements;
            report["routeResolution"] = BuildRouteRequirementResolutionProjection(plan.routeRequirementResolution);
            report["recipeResolutions"] = recipeResolutions;
            report["namedPromontories"] = BuildNamedPromontoryProjection(plan.namedPromontories);
            report["externalConnectors"] = BuildExternalConnectorProjection(plan.externalConnectors);
            report["existingTransitions"] = existingTransitions;
            report["schemaUsage"] = BuildRecipeSchemaUsageProjection();
            ((JObject)report["hashes"])["routeIntent"] = routeIntentHash;
            EndPhase7OutlierStage("reportAssemblyAndDiagnosticProjections", reportAssemblyStart);

            return report;
        }

        private static JObject CreateRejectedPhase0SeedReport(
            int seed,
            int layoutAttemptsUsed,
            string rejectionReason,
            Dictionary<string, int> rejectionHistogram,
            Exception exception)
        {
            var report = new JObject
            {
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["seed"] = seed,
                ["catalogDigest"] = Phase0CatalogDigest(),
                ["accepted"] = false,
                ["layoutAttempts"] = layoutAttemptsUsed,
                ["lastRejection"] = rejectionReason ?? string.Empty,
                ["lastRejectionCode"] = Phase0RejectionCode(rejectionReason, exception),
                ["exceptionType"] = exception?.GetType().FullName,
                ["rejectionHistogram"] = HistogramToken(rejectionHistogram),
                ["rejectionCodes"] = RejectionCodeHistogramToken(rejectionHistogram)
            };
            AddGenerationSettingsIdentity(report);
            report["measurements"] = new JObject
            {
                ["available"] = false,
                ["reason"] = "layout was rejected before density and adjacency measurements were available"
            };
            if (phase1LastRouteIntent != null)
            {
                report["routeIntent"] = BuildPhase1RouteIntentProjection();
                report["routeBuilderFailureCode"] = phase1LastFailureCode;
            }

            return report;
        }

        private static JObject BuildPhase1RouteIntentProjection()
        {
            if (phase1LastRouteIntent == null)
            {
                return new JObject();
            }

            RouteIntent intent = phase1LastRouteIntent;
            var nodes = new JArray();
            var mainRoute = new JArray();
            var branch = new JArray();
            foreach (RouteNodeIntent node in intent.nodes)
            {
                nodes.Add(new JObject
                {
                    ["id"] = node.id,
                    ["role"] = node.role,
                    ["beat"] = node.beat,
                    ["mainRouteOrder"] = node.mainRouteOrder,
                    ["branchOrder"] = node.branchOrder,
                    ["relativeElevationLevels"] = node.relativeElevationLevels,
                    ["landmarkSlotId"] = node.landmarkSlotId
                });
                if (node.IsOnMainRoute)
                {
                    mainRoute.Add(node.id);
                }
                else
                {
                    branch.Add(node.id);
                }
            }

            var traversalEdges = new JArray();
            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                traversalEdges.Add(new JObject
                {
                    ["id"] = edge.id,
                    ["fromNode"] = intent.nodes[edge.fromNode].id,
                    ["toNode"] = intent.nodes[edge.toNode].id,
                    ["connectionType"] = edge.transitionKind.ToString(),
                    ["requiredRiseLevels"] = edge.requiredRiseLevels,
                    ["laneCount"] = edge.laneCount
                });
            }

            int loopEdges = intent.traversalEdges.Length - (intent.nodes.Length - 1);
            var recipeSlots = new JArray();
            foreach (RecipeSlotIntent slot in intent.recipeSlots)
            {
                recipeSlots.Add(BuildRecipeSlotIntentProjection(slot));
            }

            return new JObject
            {
                ["seed"] = intent.seed,
                ["plannerVersion"] = intent.plannerVersion,
                ["patternId"] = intent.patternId,
                ["catalogDigest"] = intent.catalogDigest,
                ["elevationPolicy"] = intent.elevationPolicy.ToString(),
                ["nodeCount"] = intent.nodes.Length,
                ["nodes"] = nodes,
                ["bottomNode"] = intent.nodes[intent.bottomNode].id,
                ["topNode"] = intent.nodes[intent.topNode].id,
                ["graph"] = new JObject
                {
                    ["mainRoute"] = mainRoute,
                    ["mainRouteCount"] = mainRoute.Count,
                    ["branch"] = branch,
                    ["branchNodeCount"] = branch.Count,
                    ["traversalEdges"] = traversalEdges,
                    ["traversalEdgeCount"] = traversalEdges.Count,
                    ["loopEdges"] = loopEdges,
                    ["branchAttachNode"] = intent.nodes[intent.branchAttachNode].id,
                    ["branchRejoinNode"] = intent.nodes[intent.branchRejoinNode].id
                },
                ["vista"] = new JObject
                {
                    ["id"] = intent.vista.id,
                    ["sourceNode"] = intent.nodes[intent.vista.sourceNode].id,
                    ["targetNode"] = intent.nodes[intent.vista.targetNode].id,
                    ["facingRequirement"] = "mutual-facing",
                    ["minimumReservedVoidCells"] = intent.vista.minimumReservedVoidCells,
                    ["candidateSightVolumeRequired"] = true
                },
                ["recipeSlots"] = recipeSlots
            };
        }

        private static JObject BuildRecipeSlotIntentProjection(RecipeSlotIntent slot)
        {
            if (slot?.recipe == null)
            {
                return new JObject();
            }

            var ports = new JArray();
            foreach (DungeonRecipePort port in slot.recipe.ports)
            {
                slot.TryGetEdgeId(port.id, out string edgeId);
                ports.Add(new JObject
                {
                    ["id"] = port.id,
                    ["edgeId"] = edgeId,
                    ["type"] = port.type.ToString(),
                    ["mandatory"] = port.mandatory,
                    ["cell"] = CellToken(port.cell),
                    ["outwardDirection"] = CellToken(port.outwardDirection),
                    ["relativeLevel"] = port.relativeLevel
                });
            }

            return new JObject
            {
                ["id"] = slot.recipe.recipeId,
                ["slotNode"] = slot.slotNode,
                ["kind"] = slot.recipe.kind.ToString(),
                ["schemaVersion"] = slot.recipe.schemaVersion,
                ["contentVersion"] = slot.recipe.contentVersion,
                ["contentDigest"] = DungeonRecipeValidator.ComputeContentDigest(slot.recipe),
                ["orientationBinding"] = slot.orientationBinding.ToString(),
                ["ports"] = ports,
                ["zoneCount"] = slot.recipe.zones.Length,
                ["transitionCount"] = slot.recipe.transitions.Length,
                ["symmetryPairCount"] = slot.recipe.symmetryPairs.Length,
                ["variationCount"] = slot.recipe.variations.Length
            };
        }

        private static JObject BuildPhase1RoutePlacementProjection(DungeonLayout layout)
        {
            var centers = new JArray();
            if (phase1LastRouteIntent != null &&
                phase1LastNodeCenters.Length == phase1LastRouteIntent.nodes.Length)
            {
                DungeonPatternSpatialSettings spatial =
                    ResolvePatternSpatialSettings(phase1LastRouteIntent.patternId);
                for (int node = 0; node < phase1LastNodeCenters.Length; node++)
                {
                    centers.Add(new JObject
                    {
                        ["nodeId"] = phase1LastRouteIntent.nodes[node].id,
                        ["center"] = CellToken(phase1LastNodeCenters[node]),
                        ["envelope"] = RectToken(RoomEnvelope(phase1LastNodeCenters[node], spatial))
                    });
                }
            }

            var approaches = new JArray();
            if (phase1LastRouteIntent != null &&
                phase1LastNodeCenters.Length == phase1LastRouteIntent.nodes.Length)
            {
                foreach (RouteTraversalIntent edge in phase1LastRouteIntent.traversalEdges)
                {
                    Vector2Int delta = phase1LastNodeCenters[edge.toNode] - phase1LastNodeCenters[edge.fromNode];
                    var direction = new Vector2Int(Math.Sign(delta.x), Math.Sign(delta.y));
                    approaches.Add(new JObject
                    {
                        ["edgeId"] = edge.id,
                        ["fromApproach"] = CellToken(direction),
                        ["toApproach"] = CellToken(-direction)
                    });
                }
            }

            bool vistaUnobstructedAtLayoutHandoff = phase1LastVistaCells.Length >=
                (phase1LastRouteIntent?.vista.minimumReservedVoidCells ?? int.MaxValue);
            bool reservedVoidPreservedAfterTierLooping = vistaUnobstructedAtLayoutHandoff;
            foreach (Vector2Int cell in phase1LastVistaCells)
            {
                if (layout.floorCells.Contains(cell))
                {
                    reservedVoidPreservedAfterTierLooping = false;
                    break;
                }
            }

            return new JObject
            {
                ["layoutAttempt"] = phase1LastLayoutAttempt,
                ["mainEmbeddingAttempts"] = phase1LastMainEmbeddingAttempts,
                ["branchSearchExpansions"] = phase1LastBranchSearchExpansions,
                ["roomInflationAttempts"] = phase1LastRoomInflationAttempts,
                ["nodeCenters"] = centers,
                ["pinnedApproaches"] = approaches,
                ["vista"] = new JObject
                {
                    ["sourceFacing"] = CellToken(phase1LastVistaSourceFacing),
                    ["targetFacing"] = CellToken(phase1LastVistaTargetFacing),
                    ["facingOpposed"] = phase1LastVistaSourceFacing == -phase1LastVistaTargetFacing &&
                        phase1LastVistaSourceFacing != Vector2Int.zero,
                    ["reservedVoidCellCount"] = phase1LastVistaCells.Length,
                    ["reservedVoidCells"] = CellsToken(phase1LastVistaCells, sort: false),
                    ["unobstructedCandidateVolume"] = vistaUnobstructedAtLayoutHandoff,
                    ["measurementStage"] = "DungeonLayout handoff before route-constrained tier planning and loop additions",
                    ["reservedVoidPreservedAfterTierLooping"] = reservedVoidPreservedAfterTierLooping
                }
            };
        }

        private static JObject BuildRouteRequirementResolutionProjection(
            RouteRequirementResolution resolution)
        {
            var transitions = new JArray();
            var transitionKinds = new JObject
            {
                [RouteTransitionKind.LevelCorridor.ToString()] = 0,
                [RouteTransitionKind.Stair.ToString()] = 0,
                [RouteTransitionKind.Bridge.ToString()] = 0,
                [RouteTransitionKind.Stairwell.ToString()] = 0
            };
            bool allStructuralReservedBeforeFill = true;
            foreach (RouteTransitionResolution transition in
                     resolution.transitions ?? Array.Empty<RouteTransitionResolution>())
            {
                string kind = transition.transitionKind.ToString();
                transitionKinds[kind] = (transitionKinds.Value<int?>(kind) ?? 0) + 1;
                bool reservedBeforeFill = transition.transitionKind == RouteTransitionKind.LevelCorridor ||
                    transition.lowerLandingCells.Length > 0 &&
                    transition.upperLandingCells.Length > 0 &&
                    transition.footprintCells.Length > 0;
                allStructuralReservedBeforeFill &= reservedBeforeFill;
                transitions.Add(new JObject
                {
                    ["edgeId"] = transition.edgeId,
                    ["fromRoom"] = transition.fromRoom,
                    ["toRoom"] = transition.toRoom,
                    ["transitionKind"] = kind,
                    ["requiredRiseLevels"] = transition.requiredRiseLevels,
                    ["resolvedRiseLevels"] = transition.resolvedRiseLevels,
                    ["placementClass"] = transition.placementClass,
                    ["transitionFirstCell"] = CellToken(transition.transitionFirstCell),
                    ["transitionSecondCell"] = CellToken(transition.transitionSecondCell),
                    ["lowerLandingCells"] = CellsToken(transition.lowerLandingCells, sort: false),
                    ["upperLandingCells"] = CellsToken(transition.upperLandingCells, sort: false),
                    ["footprintCells"] = CellsToken(transition.footprintCells, sort: false),
                    ["reservedBeforeFill"] = reservedBeforeFill
                });
            }

            return new JObject
            {
                ["requirementsSatisfied"] = resolution.transitions != null &&
                    resolution.transitions.Length > 0 &&
                    resolution.finalVistaValid,
                ["requiredTransitionCount"] = transitions.Count,
                ["transitionKinds"] = transitionKinds,
                ["allStructuralReservedBeforeFill"] = allStructuralReservedBeforeFill,
                ["bottomLevel"] = resolution.bottomLevel,
                ["topLevel"] = resolution.topLevel,
                ["routeClimbLevels"] = resolution.RouteClimbLevels,
                ["transitions"] = transitions,
                ["vista"] = new JObject
                {
                    ["sourceCell"] = CellToken(resolution.vistaSourceCell),
                    ["targetCell"] = CellToken(resolution.vistaTargetCell),
                    ["sourceLevel"] = resolution.vistaSourceLevel,
                    ["targetLevel"] = resolution.vistaTargetLevel,
                    ["levelDelta"] = resolution.vistaSourceLevel - resolution.vistaTargetLevel,
                    ["sourceFacing"] = CellToken(resolution.vistaSourceFacing),
                    ["targetFacing"] = CellToken(resolution.vistaTargetFacing),
                    ["reservedVoidCells"] = CellsToken(resolution.reservedVistaCells, sort: false),
                    ["reservedVoidCellCount"] = resolution.reservedVistaCells?.Length ?? 0,
                    ["finalValid"] = resolution.finalVistaValid,
                    ["measurementStage"] = "final TieredLevelPlan before boundary construction and rendering"
                }
            };
        }

        private static JArray BuildRecipeResolutionsProjection(IEnumerable<RecipeResolution> resolutions)
        {
            var result = new JArray();
            foreach (RecipeResolution resolution in resolutions ?? Array.Empty<RecipeResolution>())
            {
                result.Add(BuildRecipeResolutionProjection(resolution));
            }

            return result;
        }

        private static JObject BuildRecipeResolutionProjection(RecipeResolution resolution)
        {
            var zones = new JArray();
            foreach (RecipeZonePlacement zone in resolution.zones ?? Array.Empty<RecipeZonePlacement>())
            {
                zones.Add(new JObject
                {
                    ["id"] = zone.id,
                    ["kind"] = zone.kind.ToString(),
                    ["relativeLevel"] = zone.relativeLevel,
                    ["cells"] = CellsToken(zone.cells, sort: false)
                });
            }

            var ports = new JArray();
            foreach (RecipePortPlacement port in resolution.ports ?? Array.Empty<RecipePortPlacement>())
            {
                ports.Add(new JObject
                {
                    ["id"] = port.id,
                    ["edgeId"] = port.edgeId,
                    ["type"] = port.type.ToString(),
                    ["mandatory"] = port.mandatory,
                    ["neighborRoomIndex"] = port.neighborRoomIndex,
                    ["cell"] = CellToken(port.cell),
                    ["outwardDirection"] = CellToken(port.outwardDirection),
                    ["expectedRelativeLevel"] = port.expectedRelativeLevel
                });
            }

            var recipeTransitions = new JArray();
            bool reservationsComplete = true;
            foreach (RecipeTransitionPlacement transition in
                     resolution.transitions ?? Array.Empty<RecipeTransitionPlacement>())
            {
                reservationsComplete &= transition.lowerLandingCells.Length > 0 &&
                    transition.upperLandingCells.Length > 0 &&
                    transition.footprintCells.Length > 0;
                recipeTransitions.Add(new JObject
                {
                    ["id"] = transition.id,
                    ["atomicGroupId"] = transition.atomicGroupId,
                    ["lowerTransitionCell"] = CellToken(transition.lowerTransitionCell),
                    ["upperTransitionCell"] = CellToken(transition.upperTransitionCell),
                    ["lowerLandingCells"] = CellsToken(transition.lowerLandingCells, sort: false),
                    ["upperLandingCells"] = CellsToken(transition.upperLandingCells, sort: false),
                    ["footprintCells"] = CellsToken(transition.footprintCells, sort: false),
                    ["climbDirection"] = CellToken(transition.climbDirection)
                });
            }

            return new JObject
            {
                ["id"] = resolution.id,
                ["kind"] = resolution.kind.ToString(),
                ["contentDigest"] = resolution.contentDigest,
                ["roomIndex"] = resolution.roomIndex,
                ["primaryAxis"] = CellToken(resolution.primaryAxis),
                ["mirrored"] = resolution.mirrored,
                ["protectedCells"] = CellsToken(resolution.protectedCells, sort: false),
                ["zones"] = zones,
                ["ports"] = ports,
                ["transitions"] = recipeTransitions,
                ["selectedVariationId"] = resolution.selectedVariationId,
                ["selectedVisualImplementationId"] = resolution.selectedVisualImplementationId,
                ["showpieceOriginCell"] = CellToken(resolution.showpieceOriginCell),
                ["showpieceYawDegrees"] = resolution.showpieceYawDegrees,
                ["baseLevel"] = resolution.baseLevel,
                ["atomicAndValid"] = resolution.atomicAndValid,
                ["mandatoryPortsBound"] = resolution.atomicAndValid && ports.Count > 0,
                ["reservationsComplete"] = reservationsComplete,
                ["protectedZonesValid"] = resolution.atomicAndValid && resolution.protectedCells.Length > 0
            };
        }

        private static RecipeResolution FindRecipeResolution(
            IEnumerable<RecipeResolution> resolutions,
            string recipeId)
        {
            foreach (RecipeResolution resolution in resolutions ?? Array.Empty<RecipeResolution>())
            {
                if (string.Equals(resolution.id, recipeId, StringComparison.Ordinal))
                {
                    return resolution;
                }
            }

            return default;
        }

        private static JObject FindRecipeProjection(JArray recipes, string recipeId)
        {
            foreach (JToken token in recipes ?? new JArray())
            {
                if (string.Equals(token.Value<string>("id"), recipeId, StringComparison.Ordinal))
                {
                    return token as JObject;
                }
            }

            return null;
        }

        private static JObject BuildRecipeSchemaUsageProjection()
        {
            var fields = new JArray();
            void Add(string field, string producer, string consumer)
            {
                fields.Add(new JObject
                {
                    ["field"] = field,
                    ["producer"] = producer,
                    ["consumer"] = consumer
                });
            }

            Add("asset.recipeId/schemaVersion/contentVersion", "reviewed recipe assets", "stable streams, digest, catalog, diagnostics");
            Add("routeSlot.node/recipeId", "BuildProcessionalRouteIntent", "eligibility, room inflation, tier handoff");
            Add("routeSlot.orientationBinding", "BuildProcessionalRouteIntent", "route/vista-bound primary axis");
            Add("zones.walkable", "reviewed recipe assets", "atomic room footprint");
            Add("zones.protected", "reviewed recipe assets", "late-feature and dressing protection");
            Add("zones.elevated", "reviewed recipe assets", "canonical cell levels");
            Add("zones.relativeLevel", "reviewed recipe assets", "level and transition validation");
            Add("transitions.atomicGroup", "reviewed recipe assets", "atomic transition validation");
            Add("variations/motifs", "reviewed recipe assets", "stable StairForge-backed visual selection");
            Add("ports.id/type/mandatory", "reviewed recipe assets", "route edge binding and neighbor validation");
            Add("ports.cell/outward/level", "TryPlaceRecipe", "exact corridor endpoint and tier validation");
            Add("placement.primaryAxis/mirror", "TryPlaceRecipe", "orientation, variations, symmetry validation");
            Add("placement.protectedCells", "TryPlaceRecipe", "generic feature exclusions and final validation");
            Add("placement.zoneCells", "TryPlaceRecipe", "canonical levels and structural validation");
            Add("transition.cells/landings/footprint/climb", "TryPlaceRecipe", "StairPlacementLedger, TransitionEdge, headroom, port graph");
            Add("selected variation/visual", "TryPlaceRecipe", "DaisShowpiece and renderer");
            Add("reviewDigest/lifecycle", "review action", "stale-review detection and active catalog admission");
            return new JObject
            {
                ["probeId"] = "dungeon-recipe-v1",
                ["fieldCount"] = fields.Count,
                ["allFieldsConsumed"] = true,
                ["fields"] = fields
            };
        }

        private static JObject BuildPhase0ValidationSummary(
            int seed,
            DungeonLayout layout,
            TieredLevelPlan plan,
            System.Random random,
            out ElevationEdgeModel.RoomBoundaryContext boundaryContext)
        {
            long connectivityStart = BeginPhase7OutlierStage();
            bool layoutConnected = IsConnected(layout.floorCells);
            bool roomGraphConnected = TryValidateRoomGraphConnectivity(layout, out string roomGraphMessage);
            EndPhase7OutlierStage("hardValidation.connectivity", connectivityStart);

            long transitionStart = BeginPhase7OutlierStage();
            bool transitionContractsValid = TryValidateTransitionLevelDeltas(
                plan.cellLevels,
                plan.transitions,
                out string transitionMessage);
            bool portGraphBuilt = TryBuildFloorStairPortGraph(
                plan.cellLevels,
                plan.transitions,
                out FloorStairPortGraph portGraph,
                out string portGraphBuildMessage);
            bool portGraphConnected = false;
            string portGraphConnectedMessage = portGraphBuildMessage;
            if (portGraphBuilt)
            {
                portGraphConnected = portGraph.IsGloballyConnected(out portGraphConnectedMessage);
            }
            EndPhase7OutlierStage("hardValidation.transitionsAndPortGraph", transitionStart);

            long contractStart = BeginPhase7OutlierStage();
            bool bottomToTop = portGraphConnected && plan.minLevel < plan.maxLevel;
            bool routeRequirementsValid = TryValidateAcceptedRouteRequirements(
                plan,
                out string routeRequirementsMessage);
            bool recipesValid = TryValidateAcceptedRecipes(
                plan,
                out string recipesMessage);
            bool namedPromontoriesValid = TryValidateAcceptedNamedPromontories(
                plan,
                out string namedPromontoryMessage);
            bool externalConnectorsValid = TryValidateAcceptedExternalConnectors(
                seed,
                plan,
                out string externalConnectorMessage);
            bool headroomValid = TryValidateAcceptedPlanHeadroom(plan, out string headroomMessage);
            EndPhase7OutlierStage("hardValidation.routeRecipesPromontoriesHeadroom", contractStart);

            long boundaryStart = BeginPhase7OutlierStage();
            bool boundaryValid = TryBuildRoomBoundaryContext(
                layout,
                plan.cellLevels,
                plan.transitions,
                random,
                out boundaryContext,
                out string boundaryMessage);
            EndPhase7OutlierStage("hardValidation.boundary", boundaryStart);

            long rendererInputsStart = BeginPhase7OutlierStage();
            bool rendererInputsValid = TryValidatePhase0RendererInputs(plan, out string rendererInputMessage);
            EndPhase7OutlierStage("hardValidation.rendererInputs", rendererInputsStart);
            bool passed =
                layoutConnected &&
                roomGraphConnected &&
                transitionContractsValid &&
                portGraphConnected &&
                bottomToTop &&
                routeRequirementsValid &&
                recipesValid &&
                namedPromontoriesValid &&
                externalConnectorsValid &&
                headroomValid &&
                boundaryValid &&
                rendererInputsValid;
            var failureCodes = new JArray();
            AddFailureCode(failureCodes, layoutConnected, "FLOOR_CONNECTIVITY");
            AddFailureCode(failureCodes, roomGraphConnected, "ROOM_GRAPH_CONNECTIVITY");
            AddFailureCode(failureCodes, transitionContractsValid, "TRANSITION_CONTRACT");
            AddFailureCode(failureCodes, portGraphConnected, "VERTICAL_TRAVERSAL");
            AddFailureCode(failureCodes, bottomToTop, "BOTTOM_TO_TOP_TRAVERSAL");
            AddFailureCode(failureCodes, routeRequirementsValid, "ROUTE_REQUIREMENTS");
            AddFailureCode(failureCodes, recipesValid, "RECIPES");
            AddFailureCode(failureCodes, namedPromontoriesValid, "NAMED_PROMONTORY");
            AddFailureCode(failureCodes, externalConnectorsValid, "EXTERNAL_CONNECTOR_PROMONTORY");
            AddFailureCode(failureCodes, headroomValid, "POST_PLAN_HEADROOM_CLEARANCE");
            AddFailureCode(failureCodes, boundaryValid, "BOUNDARY_CONTEXT");
            AddFailureCode(failureCodes, rendererInputsValid, "RENDERER_INPUT");

            return new JObject
            {
                ["passed"] = passed,
                ["failureCodes"] = failureCodes,
                ["layoutConnectivity"] = CheckToken(layoutConnected, layoutConnected ? "floor mask connected" : "floor mask disconnected"),
                ["roomGraphConnectivity"] = CheckToken(roomGraphConnected, roomGraphMessage),
                ["transitionContracts"] = CheckToken(transitionContractsValid, transitionMessage),
                ["verticalTraversal"] = CheckToken(portGraphConnected, portGraphConnectedMessage),
                ["bottomToTopTraversal"] = CheckToken(
                    bottomToTop,
                    bottomToTop
                        ? $"connected traversal spans levels {plan.minLevel}..{plan.maxLevel}"
                        : $"traversal did not span distinct bottom/top levels ({plan.minLevel}..{plan.maxLevel})"),
                ["routeRequirements"] = CheckToken(routeRequirementsValid, routeRequirementsMessage),
                ["recipes"] = CheckToken(recipesValid, recipesMessage),
                ["namedPromontories"] = CheckToken(namedPromontoriesValid, namedPromontoryMessage),
                ["externalConnectors"] = CheckToken(externalConnectorsValid, externalConnectorMessage),
                ["headroom"] = CheckToken(headroomValid, headroomMessage),
                ["boundary"] = CheckToken(boundaryValid, boundaryMessage),
                ["rendererInputs"] = CheckToken(rendererInputsValid, rendererInputMessage)
            };
        }

        private static bool TryValidateAcceptedRouteRequirements(
            TieredLevelPlan plan,
            out string message)
        {
            RouteRequirementResolution resolution = plan.routeRequirementResolution;
            int stairCount = 0;
            int bridgeCount = 0;
            int stairwellCount = 0;
            foreach (RouteTransitionResolution transition in
                     resolution.transitions ?? Array.Empty<RouteTransitionResolution>())
            {
                switch (transition.transitionKind)
                {
                    case RouteTransitionKind.Stair:
                        stairCount++;
                        break;
                    case RouteTransitionKind.Bridge:
                        bridgeCount++;
                        break;
                    case RouteTransitionKind.Stairwell:
                        stairwellCount++;
                        break;
                }

                if (transition.transitionKind != RouteTransitionKind.LevelCorridor &&
                    (transition.lowerLandingCells.Length == 0 ||
                     transition.upperLandingCells.Length == 0 ||
                     transition.footprintCells.Length == 0))
                {
                    message = $"route edge '{transition.edgeId}' lacked pre-fill landing/footprint evidence";
                    return false;
                }
            }

            int expectedTransitionCount = phase1LastRouteIntent?.traversalEdges?.Length ?? 0;
            bool passed = resolution.transitions != null &&
                resolution.transitions.Length == expectedTransitionCount &&
                resolution.bottomLevel == 0 &&
                resolution.topLevel == MaxGeneratedLevel &&
                resolution.RouteClimbLevels == MaxGeneratedLevel &&
                stairCount > 0 &&
                bridgeCount > 0 &&
                stairwellCount > 0 &&
                resolution.finalVistaValid &&
                resolution.reservedVistaCells != null &&
                resolution.reservedVistaCells.Length >= 3 &&
                resolution.vistaSourceLevel - resolution.vistaTargetLevel >= MajorRiseLevels;
            message = passed
                ? $"route requirements resolved {resolution.transitions.Length} edges, 0u..{resolution.topLevel}u, with stair:{stairCount}, bridge:{bridgeCount}, stairwell:{stairwellCount}, final vista valid"
                : $"route requirements incomplete: edges={resolution.transitions?.Length ?? 0}, climb={resolution.RouteClimbLevels}u, stair={stairCount}, bridge={bridgeCount}, stairwell={stairwellCount}, vista={resolution.finalVistaValid}";
            return passed;
        }

        private static bool TryValidateAcceptedRecipes(
            TieredLevelPlan plan,
            out string message)
        {
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out message) ||
                plan.recipeResolutions == null ||
                plan.recipeResolutions.Length != 3)
            {
                message = string.IsNullOrEmpty(message)
                    ? $"expected three recipe resolutions; found {plan.recipeResolutions?.Length ?? 0}"
                    : message;
                return false;
            }

            foreach (string requiredId in new[]
                     {
                         DungeonRecipeIds.ProcessionalLandmark,
                         DungeonRecipeIds.CompressionConnector,
                         DungeonRecipeIds.CornerReturnConnector
                     })
            {
                RecipeResolution resolution = FindRecipeResolution(plan.recipeResolutions, requiredId);
                if (!catalog.TryGet(requiredId, out DungeonRecipeAsset recipe) ||
                    !resolution.atomicAndValid ||
                    resolution.primaryAxis == Vector2Int.zero ||
                    resolution.ports == null || resolution.ports.Length != recipe.ports.Length ||
                    resolution.transitions == null || resolution.transitions.Length != recipe.transitions.Length ||
                    resolution.protectedCells == null || resolution.protectedCells.Length == 0 ||
                    !string.Equals(
                        resolution.contentDigest,
                        DungeonRecipeValidator.ComputeContentDigest(recipe),
                        StringComparison.Ordinal))
                {
                    message = $"recipe '{requiredId}' was absent, stale, partial, or invalid";
                    return false;
                }

                foreach (RecipeTransitionPlacement transition in resolution.transitions)
                {
                    if (transition.lowerLandingCells.Length == 0 ||
                        transition.upperLandingCells.Length == 0 ||
                        transition.footprintCells.Length == 0)
                    {
                        message = $"recipe transition '{transition.id}' lacked landing/footprint evidence";
                        return false;
                    }
                }
            }

            message = $"three reviewed recipes resolved atomically with catalog {catalog.digest}";
            return true;
        }

        private static bool TryValidateAcceptedNamedPromontories(
            TieredLevelPlan plan,
            out string message)
        {
            RouteIntent intent = phase1LastRouteIntent;
            RouteRequirementResolution route = plan.routeRequirementResolution;
            NamedVistaPromontoryResolution[] resolutions =
                plan.namedPromontories ?? Array.Empty<NamedVistaPromontoryResolution>();
            if (intent == null || string.IsNullOrEmpty(intent.vista.id))
            {
                message = "accepted plan had no named vista for promontory validation";
                return false;
            }

            Vector2Int facing = route.vistaSourceFacing;
            int distance = Mathf.Abs(route.vistaTargetCell.x - route.vistaSourceCell.x) +
                Mathf.Abs(route.vistaTargetCell.y - route.vistaSourceCell.y);
            int expectedLength = Mathf.Min(
                MaximumNamedVistaPromontoryCells,
                Mathf.Max(0, distance - 1 - intent.vista.minimumReservedVoidCells));
            if (expectedLength == 0)
            {
                bool absentAndClear = resolutions.Length == 0 &&
                    route.reservedVistaCells.Length >= intent.vista.minimumReservedVoidCells;
                message = absentAndClear
                    ? "vista had no surplus cell; no named promontory was emitted"
                    : "vista without surplus cells emitted a named promontory or lost its void reservation";
                return absentAndClear;
            }

            if (resolutions.Length != 1)
            {
                message = $"expected one named promontory; found {resolutions.Length}";
                return false;
            }

            NamedVistaPromontoryResolution resolution = resolutions[0];
            string targetNodeId = intent.nodes[intent.vista.targetNode].id;
            if (!string.Equals(resolution.vistaId, intent.vista.id, StringComparison.Ordinal) ||
                !string.Equals(resolution.targetNodeId, targetNodeId, StringComparison.Ordinal) ||
                resolution.sourceCell != route.vistaSourceCell ||
                resolution.targetCell != route.vistaTargetCell ||
                resolution.facing != facing ||
                resolution.cells == null ||
                resolution.cells.Length != expectedLength ||
                route.reservedVistaCells.Length < intent.vista.minimumReservedVoidCells ||
                route.vistaSourceLevel - route.vistaTargetLevel < MajorRiseLevels)
            {
                message = "named promontory identity, geometry, or vista clearance did not match its route target";
                return false;
            }

            for (int index = 0; index < resolution.cells.Length; index++)
            {
                Vector2Int expectedCell = route.vistaSourceCell + facing * (index + 1);
                if (resolution.cells[index] != expectedCell ||
                    !plan.cellLevels.TryGetValue(expectedCell, out int level) ||
                    level != resolution.level ||
                    level != route.vistaSourceLevel)
                {
                    message = $"named promontory cell {index} was off-axis, non-contiguous, or at the wrong level";
                    return false;
                }
            }

            message = $"named promontory '{resolution.vistaId}' targets '{resolution.targetNodeId}' with {resolution.cells.Length} cell(s) and preserves {route.reservedVistaCells.Length} void cell(s)";
            return true;
        }

        private static bool TryValidateAcceptedExternalConnectors(
            int seed,
            TieredLevelPlan plan,
            out string message)
        {
            ExternalConnectorPromontoryResolution[] resolutions =
                plan.externalConnectors ?? Array.Empty<ExternalConnectorPromontoryResolution>();
            int desiredCount = ExternalConnectorDesiredCount(seed);
            if (resolutions.Length != desiredCount || desiredCount < 1 || desiredCount > 4)
            {
                message = $"desired {desiredCount} external connectors; resolved {resolutions.Length}";
                return false;
            }

            var directions = new HashSet<int>();
            var occupied = new HashSet<Vector2Int>();
            foreach (ExternalConnectorPromontoryResolution resolution in resolutions)
            {
                Vector2Int outward = CardinalVector(resolution.direction);
                if (!directions.Add(resolution.direction) ||
                    outward == Vector2Int.zero ||
                    !string.Equals(
                        resolution.id,
                        ExternalConnectorId(resolution.direction),
                        StringComparison.Ordinal) ||
                    resolution.occupiedCells == null ||
                    resolution.occupiedCells.Length != ExternalConnectorAppendageCells + 1 ||
                    resolution.occupiedCells[0] != resolution.anchorCell ||
                    resolution.occupiedCells[1] != resolution.anchorCell + outward ||
                    resolution.occupiedCells[2] != resolution.anchorCell + outward * 2 ||
                    resolution.terminalCell != resolution.occupiedCells[2] ||
                    plan.cellLevels.ContainsKey(resolution.terminalCell + outward))
                {
                    message = $"external connector '{resolution.id}' had invalid identity, direction, geometry, or terminal throat";
                    return false;
                }

                foreach (Vector2Int cell in resolution.occupiedCells)
                {
                    if (!occupied.Add(cell) ||
                        !plan.cellLevels.TryGetValue(cell, out int level) ||
                        level != resolution.level)
                    {
                        message = $"external connector '{resolution.id}' overlapped another connector or changed level";
                        return false;
                    }
                }
            }

            List<ElevationEdgeModel.OpenFloorEdge> openEdges =
                BuildExternalConnectorOpenEdges(resolutions);
            bool passed = directions.Count == desiredCount &&
                occupied.Count == desiredCount * (ExternalConnectorAppendageCells + 1) &&
                openEdges.Count == desiredCount * 2;
            message = passed
                ? $"resolved exact deterministic count {desiredCount} with unique directions, clear terminal throats, and {openEdges.Count} renderer openings"
                : "external connector set was incomplete";
            return passed;
        }

        private static bool TryValidateRoomGraphConnectivity(DungeonLayout layout, out string message)
        {
            if (layout.rooms == null || layout.rooms.Count == 0)
            {
                message = "room graph had no rooms";
                return false;
            }

            List<int>[] adjacency = BuildRoomAdjacency(layout.rooms.Count, layout.connections);
            var visited = new HashSet<int> { 0 };
            var queue = new Queue<int>();
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int neighbor in adjacency[current])
                {
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            message = $"room graph reached {visited.Count}/{layout.rooms.Count} rooms";
            return visited.Count == layout.rooms.Count;
        }

        private static bool TryValidateAcceptedPlanHeadroom(TieredLevelPlan plan, out string rejectionReason)
        {
            var spanDeckLevels = new Dictionary<Vector2Int, int>();
            foreach (ElevationEdgeModel.TransitionEdge transition in plan.transitions)
            {
                if (!string.Equals(
                        transition.placementClass,
                        ExternalSpanStairPlacementClass,
                        StringComparison.Ordinal) ||
                    transition.footprintCells == null ||
                    transition.footprintCells.Length == 0 ||
                    transition.lowerLandingCells == null ||
                    transition.lowerLandingCells.Length == 0 ||
                    transition.upperLandingCells == null ||
                    transition.upperLandingCells.Length == 0)
                {
                    continue;
                }

                Vector2Int lowerLanding = transition.lowerLandingCells[0];
                Vector2Int upperLanding = transition.upperLandingCells[0];
                if (!plan.cellLevels.TryGetValue(lowerLanding, out int lowerLevel) ||
                    !plan.cellLevels.TryGetValue(upperLanding, out int upperLevel))
                {
                    rejectionReason = $"external span {lowerLanding}->{upperLanding} referenced a missing landing";
                    return false;
                }

                int spanLength = Mathf.Abs(upperLanding.x - lowerLanding.x) +
                    Mathf.Abs(upperLanding.y - lowerLanding.y);
                foreach (Vector2Int deckCell in transition.footprintCells)
                {
                    int deckDistance = Mathf.Abs(deckCell.x - lowerLanding.x) +
                        Mathf.Abs(deckCell.y - lowerLanding.y);
                    int deckLevel = Mathf.FloorToInt(Mathf.Lerp(
                        Mathf.Min(lowerLevel, upperLevel),
                        Mathf.Max(lowerLevel, upperLevel),
                        spanLength > 0 ? (float)deckDistance / spanLength : 0f));
                    if (!spanDeckLevels.TryGetValue(deckCell, out int existing) || deckLevel < existing)
                    {
                        spanDeckLevels[deckCell] = deckLevel;
                    }
                }
            }

            bool passed = TryValidateSpanHeadroom(plan.cellLevels, spanDeckLevels, out rejectionReason);
            if (passed)
            {
                rejectionReason = $"headroom gate passed for {spanDeckLevels.Count} external-span deck cells";
            }

            return passed;
        }

        private static bool TryValidatePhase0RendererInputs(TieredLevelPlan plan, out string message)
        {
            if (plan.cellLevels == null || plan.cellLevels.Count == 0)
            {
                message = "renderer input had no leveled floor cells";
                return false;
            }

            var prefabPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (ElevationEdgeModel.TransitionEdge transition in plan.transitions)
            {
                if (transition.synthesizedSetPiece != null)
                {
                    foreach (ElevationEdgeModel.SynthesizedPiecePlacement piece in transition.synthesizedSetPiece.pieces)
                    {
                        if (!string.IsNullOrWhiteSpace(piece.sourcePrefab))
                        {
                            prefabPaths.Add(piece.sourcePrefab);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(transition.stairPrefabPath))
                {
                    prefabPaths.Add(transition.stairPrefabPath);
                }
            }

            if (plan.daisShowpieces != null)
            {
                foreach (DaisShowpiece showpiece in plan.daisShowpieces)
                {
                    foreach (ElevationEdgeModel.SynthesizedPiecePlacement piece in showpiece.pieces ?? Array.Empty<ElevationEdgeModel.SynthesizedPiecePlacement>())
                    {
                        if (!string.IsNullOrWhiteSpace(piece.sourcePrefab))
                        {
                            prefabPaths.Add(piece.sourcePrefab);
                        }
                    }
                }
            }

            foreach (string prefabPath in prefabPaths)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                {
                    message = $"renderer input prefab was missing at '{prefabPath}'";
                    return false;
                }
            }

            message = $"renderer inputs resolved {plan.cellLevels.Count} leveled cells and {prefabPaths.Count} transition/set-piece prefabs";
            return true;
        }

        private static GameObject BuildPhase0RenderedSeed(
            int seed,
            out Bounds bounds,
            out JObject seedReport,
            out ElevationEdgeModel.BuildReport buildReport)
        {
            return BuildPhase0RenderedSeed(
                seed,
                out bounds,
                out seedReport,
                out buildReport,
                out _,
                out _);
        }

        // The additional outputs expose the existing canonical renderer inputs
        // only to diagnostic capture code. They do not add a second plan or
        // participate in generation.
        private static GameObject BuildPhase0RenderedSeed(
            int seed,
            out Bounds bounds,
            out JObject seedReport,
            out ElevationEdgeModel.BuildReport buildReport,
            out Vector3 levelFieldOrigin,
            out TieredLevelPlan renderedPlan)
        {
            CurrentGenerationSettings = LoadActiveGenerationSettings(ResolveRequestedGenerationProfileId());
            var rejectionHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            var random = new System.Random(seed);
            if (!TryBuildAcceptedPlan(
                    seed,
                    random,
                    rejectionHistogram,
                    out DungeonLayout layout,
                    out TieredLevelPlan plan,
                    out int layoutAttemptsUsed,
                    out string rejectionReason))
            {
                throw new InvalidOperationException(
                    $"Sentinel seed {seed} failed after {layoutAttemptsUsed} attempts: " +
                    $"{Phase0RejectionCode(rejectionReason, exception: null)}: {rejectionReason}");
            }

            seedReport = CreateAcceptedPhase0SeedReport(
                seed,
                layoutAttemptsUsed,
                rejectionReason,
                rejectionHistogram,
                layout,
                plan,
                random);
            if (seedReport["validation"]?.Value<bool?>("passed") != true)
            {
                throw new InvalidOperationException(
                    $"Sentinel seed {seed} failed pre-render validation: " +
                    seedReport["validation"]?.ToString(Formatting.None));
            }

            // Rebuild the same deterministic boundary context because the report
            // consumed the shared RNG while characterizing it.
            random = new System.Random(seed);
            if (!TryBuildAcceptedPlan(
                    seed,
                    random,
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    out layout,
                    out plan,
                    out _,
                    out _))
            {
                throw new InvalidOperationException($"Seed {seed} did not reproduce its accepted plan.");
            }

            if (!TryBuildRoomBoundaryContext(
                    layout,
                    plan.cellLevels,
                    plan.transitions,
                    random,
                    out ElevationEdgeModel.RoomBoundaryContext boundaryContext,
                    out string boundaryError))
            {
                throw new InvalidOperationException($"Seed {seed} could not reproduce boundary context: {boundaryError}");
            }

            levelFieldOrigin = CalculateCenteredLevelFieldOrigin(layout.floorCells, Vector3.zero);
            renderedPlan = plan;
            GameObject root = ElevationEdgeModel.BuildLevelField(
                levelFieldOrigin,
                plan.cellLevels,
                plan.transitions,
                null,
                BuildExternalConnectorOpenEdges(plan.externalConnectors),
                boundaryContext,
                CollectRenderedPromontoryCells(
                    plan.namedPromontories,
                    plan.externalConnectors),
                "DungeonLab Renderer Probe",
                out buildReport,
                out bounds);
            if (plan.daisShowpieces != null && plan.daisShowpieces.Count > 0)
            {
                PlaceDaisShowpieces(root.transform, plan.daisShowpieces, levelFieldOrigin, buildReport.levelHeight, ref bounds);
            }

            return root;
        }

        private static JObject BuildLayoutGraphSummary(DungeonLayout layout)
        {
            if (layout.rooms == null || layout.rooms.Count == 0)
            {
                return new JObject
                {
                    ["rootedRouteCount"] = 0,
                    ["longestRootRouteRooms"] = 0,
                    ["branchNodes"] = 0,
                    ["leafNodes"] = 0,
                    ["loopEdges"] = 0,
                    ["finalRoomDegreeDistribution"] = BuildIntDistribution(new List<int>())
                };
            }

            List<int>[] adjacency = BuildRoomAdjacency(layout.rooms.Count, layout.connections);
            var finalRoomDegrees = new List<int>(adjacency.Length);
            var depths = new int[layout.rooms.Count];
            for (int i = 0; i < depths.Length; i++)
            {
                depths[i] = -1;
            }

            int branchNodes = 0;
            int leafNodes = 0;
            for (int i = 0; i < adjacency.Length; i++)
            {
                finalRoomDegrees.Add(adjacency[i].Count);
                if (adjacency[i].Count >= 3)
                {
                    branchNodes++;
                }

                if (i != 0 && adjacency[i].Count == 1)
                {
                    leafNodes++;
                }
            }

            int maxDepth = 0;
            var queue = new Queue<int>();
            depths[0] = 0;
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                maxDepth = Mathf.Max(maxDepth, depths[current]);
                foreach (int neighbor in adjacency[current])
                {
                    if (depths[neighbor] >= 0)
                    {
                        continue;
                    }

                    depths[neighbor] = depths[current] + 1;
                    queue.Enqueue(neighbor);
                }
            }

            return new JObject
            {
                ["rootedRouteCount"] = 1,
                ["longestRootRouteRooms"] = maxDepth + 1,
                ["branchNodes"] = branchNodes,
                ["leafNodes"] = leafNodes,
                ["loopEdges"] = CountLoopEdges(layout),
                ["finalRoomDegreeDistribution"] = BuildIntDistribution(finalRoomDegrees),
                ["measurement"] = "current room graph rooted at room 0; no semantic route catalog exists"
            };
        }

        private static JObject BuildDensityAdjacencyMeasurements(
            DungeonLayout layout,
            JObject graphSummary,
            JObject routePlacement,
            string patternId)
        {
            var roomCells = new HashSet<Vector2Int>();
            foreach (RoomFootprint room in layout.rooms)
            {
                roomCells.UnionWith(room.cells);
            }

            int exteriorCorridorCellCount = 0;
            foreach (Vector2Int cell in layout.floorCells)
            {
                if (!roomCells.Contains(cell))
                {
                    exteriorCorridorCellCount++;
                }
            }

            var perConnectionExteriorLengths = new List<int>(layout.connections.Count);
            int sharedWallDoorCount = 0;
            foreach (RoomConnection connection in layout.connections)
            {
                int exteriorLength = 0;
                foreach (Vector2Int cell in connection.path)
                {
                    if (!roomCells.Contains(cell))
                    {
                        exteriorLength++;
                    }
                }

                perConnectionExteriorLengths.Add(exteriorLength);
                if (exteriorLength == 0)
                {
                    sharedWallDoorCount++;
                }
            }

            JObject vista = routePlacement["vista"] as JObject ?? new JObject();
            bool isAtrium = string.Equals(patternId, AtriumRingPatternId, StringComparison.Ordinal);
            return new JObject
            {
                ["available"] = true,
                ["topology"] = patternId,
                ["finalRoomDegreeDistribution"] =
                    graphSummary["finalRoomDegreeDistribution"]?.DeepClone() ?? BuildIntDistribution(new List<int>()),
                ["corridorEvidence"] = new JObject
                {
                    ["exteriorCorridorCellCount"] = exteriorCorridorCellCount,
                    ["perConnectionExteriorLengthDistribution"] = BuildIntDistribution(perConnectionExteriorLengths),
                    ["sharedWallDoorCount"] = sharedWallDoorCount,
                    ["measurement"] = "exterior cells are floor/path cells outside every room footprint; a zero-length connection is a shared-wall-door candidate"
                },
                ["voidExtent"] = new JObject
                {
                    ["reservedVistaCellCount"] = vista.Value<int?>("reservedVoidCellCount") ?? 0,
                    ["reservedVistaPreservedAfterTierLooping"] =
                        vista.Value<bool?>("reservedVoidPreservedAfterTierLooping") ?? false,
                    ["atriumCenterVoidCellCount"] = isAtrium
                        ? new JValue(CountLargestEnclosedVoidCells(layout.floorCells))
                        : JValue.CreateNull(),
                    ["atriumMeasurement"] = "largest enclosed non-floor component in the projected atrium floor mask; null for other topologies"
                }
            };
        }

        private static int CountLargestEnclosedVoidCells(HashSet<Vector2Int> floorCells)
        {
            if (floorCells == null || floorCells.Count == 0)
            {
                return 0;
            }

            RectInt floorBounds = GetCellRect(floorCells);
            int minX = floorBounds.xMin - 1;
            int maxX = floorBounds.xMax;
            int minY = floorBounds.yMin - 1;
            int maxY = floorBounds.yMax;
            var directions = new[]
            {
                new Vector2Int(1, 0),
                new Vector2Int(-1, 0),
                new Vector2Int(0, 1),
                new Vector2Int(0, -1)
            };

            var visited = new HashSet<Vector2Int>();
            var queue = new Queue<Vector2Int>();
            var exteriorStart = new Vector2Int(minX, minY);
            visited.Add(exteriorStart);
            queue.Enqueue(exteriorStart);
            while (queue.Count > 0)
            {
                Vector2Int current = queue.Dequeue();
                foreach (Vector2Int direction in directions)
                {
                    Vector2Int neighbor = current + direction;
                    if (neighbor.x < minX || neighbor.x > maxX ||
                        neighbor.y < minY || neighbor.y > maxY ||
                        floorCells.Contains(neighbor) || !visited.Add(neighbor))
                    {
                        continue;
                    }

                    queue.Enqueue(neighbor);
                }
            }

            int largestComponent = 0;
            for (int y = floorBounds.yMin; y < floorBounds.yMax; y++)
            {
                for (int x = floorBounds.xMin; x < floorBounds.xMax; x++)
                {
                    var start = new Vector2Int(x, y);
                    if (floorCells.Contains(start) || !visited.Add(start))
                    {
                        continue;
                    }

                    int componentSize = 0;
                    queue.Enqueue(start);
                    while (queue.Count > 0)
                    {
                        Vector2Int current = queue.Dequeue();
                        componentSize++;
                        foreach (Vector2Int direction in directions)
                        {
                            Vector2Int neighbor = current + direction;
                            if (!floorBounds.Contains(neighbor) ||
                                floorCells.Contains(neighbor) || !visited.Add(neighbor))
                            {
                                continue;
                            }

                            queue.Enqueue(neighbor);
                        }
                    }

                    largestComponent = Mathf.Max(largestComponent, componentSize);
                }
            }

            return largestComponent;
        }

        private static JObject BuildCanonicalLayoutProjection(DungeonLayout layout)
        {
            var floorCells = new List<Vector2Int>(layout.floorCells);
            floorCells.Sort(CompareCells);
            var rooms = new JArray();
            for (int roomIndex = 0; roomIndex < layout.rooms.Count; roomIndex++)
            {
                RoomFootprint room = layout.rooms[roomIndex];
                var parts = new JArray();
                foreach (RectInt part in room.parts)
                {
                    parts.Add(RectToken(part));
                }

                var cells = new JArray();
                foreach (Vector2Int cell in room.CellsRowMajor())
                {
                    cells.Add(CellToken(cell));
                }

                rooms.Add(new JObject
                {
                    ["index"] = roomIndex,
                    ["parts"] = parts,
                    ["cells"] = cells
                });
            }

            var connections = new JArray();
            for (int index = 0; index < layout.connections.Count; index++)
            {
                RoomConnection connection = layout.connections[index];
                connections.Add(new JObject
                {
                    ["index"] = index,
                    ["fromRoom"] = connection.fromRoom,
                    ["toRoom"] = connection.toRoom,
                    ["path"] = CellsToken(connection.path, sort: false)
                });
            }

            var zones = new JArray();
            foreach (RoomZonePlan zone in layout.roomZones)
            {
                var seamPairs = new JArray();
                foreach ((Vector2Int lowerCell, Vector2Int raisedCell) pair in zone.SeamCellPairs())
                {
                    seamPairs.Add(new JObject
                    {
                        ["lower"] = CellToken(pair.lowerCell),
                        ["raised"] = CellToken(pair.raisedCell)
                    });
                }

                zones.Add(new JObject
                {
                    ["roomIndex"] = zone.roomIndex,
                    ["lowerCells"] = CellsToken(zone.lowerCells, sort: true),
                    ["raisedCells"] = CellsToken(zone.raisedCells, sort: true),
                    ["seamPairs"] = seamPairs
                });
            }

            return new JObject
            {
                ["floorCells"] = CellsToken(floorCells, sort: false),
                ["rooms"] = rooms,
                ["connections"] = connections,
                ["roomZones"] = zones,
                ["connectorCandidateCount"] = layout.connectorCandidateCount
            };
        }

        private static JObject BuildCanonicalTieredLevelPlanProjection(TieredLevelPlan plan)
        {
            var levelCells = new List<Vector2Int>(plan.cellLevels.Keys);
            levelCells.Sort(CompareCells);
            var levels = new JArray();
            foreach (Vector2Int cell in levelCells)
            {
                levels.Add(new JObject
                {
                    ["cell"] = CellToken(cell),
                    ["level"] = plan.cellLevels[cell]
                });
            }

            JArray transitions = BuildExistingTransitionProjection(plan.transitions);

            var synthesizedStairs = new JArray();
            if (plan.synthesizedStairs != null)
            {
                foreach ((string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece) item in plan.synthesizedStairs)
                {
                    synthesizedStairs.Add(new JObject
                    {
                        ["gapId"] = item.gapId,
                        ["setPiece"] = SynthesizedSetPieceToken(item.setPiece)
                    });
                }
            }

            var showpieces = new JArray();
            if (plan.daisShowpieces != null)
            {
                foreach (DaisShowpiece showpiece in plan.daisShowpieces)
                {
                    var pieces = new JArray();
                    foreach (ElevationEdgeModel.SynthesizedPiecePlacement piece in showpiece.pieces ?? Array.Empty<ElevationEdgeModel.SynthesizedPiecePlacement>())
                    {
                        pieces.Add(SynthesizedPieceToken(piece));
                    }

                    showpieces.Add(new JObject
                    {
                        ["designName"] = showpiece.designName,
                        ["originCell"] = CellToken(showpiece.originCell),
                        ["yawDegrees"] = showpiece.yawDegrees,
                        ["roomLevel"] = showpiece.roomLevel,
                        ["pieces"] = pieces
                    });
                }
            }

            return new JObject
            {
                ["cellLevels"] = levels,
                ["transitions"] = transitions,
                ["levelCount"] = plan.levelCount,
                ["minLevel"] = plan.minLevel,
                ["maxLevel"] = plan.maxLevel,
                ["roomsPerTierSummary"] = plan.roomsPerTierSummary,
                ["overlookCount"] = plan.overlookCount,
                ["transitionSummary"] = plan.transitionSummary,
                ["connectorCandidateCount"] = plan.connectorCandidateCount,
                ["stairUsageSummary"] = plan.stairUsageSummary,
                ["topologySummary"] = plan.topologySummary,
                ["placementClassSummary"] = plan.placementClassSummary,
                ["stairCandidateSummary"] = plan.stairCandidateSummary,
                ["portGraphSummary"] = plan.portGraphSummary,
                ["archetypeName"] = plan.archetypeName,
                ["synthesizedStairs"] = synthesizedStairs,
                ["synthesizedStairSummary"] = plan.synthesizedStairSummary,
                ["daisShowpieces"] = showpieces,
                ["promontoryCells"] = CellsToken(CollectNamedPromontoryCells(plan.namedPromontories), sort: true),
                ["namedPromontories"] = BuildNamedPromontoryProjection(plan.namedPromontories),
                ["externalConnectorPierCells"] = CellsToken(CollectExternalConnectorPierCells(plan.externalConnectors), sort: true),
                ["externalConnectors"] = BuildExternalConnectorProjection(plan.externalConnectors),
                ["recipeResolutions"] = BuildRecipeResolutionsProjection(plan.recipeResolutions),
                ["routeRequirements"] = BuildRouteRequirementResolutionProjection(plan.routeRequirementResolution)
            };
        }

        private static JArray BuildExistingTransitionProjection(
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> source)
        {
            var transitions = new JArray();
            for (int index = 0; index < (source?.Count ?? 0); index++)
            {
                ElevationEdgeModel.TransitionEdge transition = source[index];
                transitions.Add(new JObject
                {
                    ["index"] = index,
                    ["firstCell"] = CellToken(transition.firstCell),
                    ["secondCell"] = CellToken(transition.secondCell),
                    ["stairPrefabPath"] = transition.stairPrefabPath,
                    ["placementClass"] = transition.placementClass,
                    ["hasLandings"] = transition.hasLandings,
                    ["lowerLandingCells"] = CellsToken(transition.lowerLandingCells, sort: false),
                    ["upperLandingCells"] = CellsToken(transition.upperLandingCells, sort: false),
                    ["footprintCells"] = CellsToken(transition.footprintCells, sort: false),
                    ["hasPortDirections"] = transition.hasPortDirections,
                    ["lowerPortDirection"] = transition.lowerPortDirection,
                    ["upperPortDirection"] = transition.upperPortDirection,
                    ["synthesizedSetPiece"] = SynthesizedSetPieceToken(transition.synthesizedSetPiece)
                });
            }

            return transitions;
        }

        private static JObject BuildPreservedCorePlanProjection(TieredLevelPlan plan)
        {
            var externalPierCells = new HashSet<Vector2Int>(
                CollectExternalConnectorPierCells(plan.externalConnectors));
            var cells = new List<Vector2Int>(plan.cellLevels.Keys);
            cells.Sort(CompareCells);
            var levels = new JArray();
            foreach (Vector2Int cell in cells)
            {
                if (externalPierCells.Contains(cell))
                    continue;

                levels.Add(new JObject
                {
                    ["cell"] = CellToken(cell),
                    ["level"] = plan.cellLevels[cell]
                });
            }

            return new JObject
            {
                ["cellLevelsBeforeExternalConnectors"] = levels,
                ["transitions"] = BuildExistingTransitionProjection(plan.transitions),
                ["synthesizedStairs"] = new JArray((plan.synthesizedStairs ??
                    new List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)>())
                    .ConvertAll(item => new JObject
                    {
                        ["gapId"] = item.gapId,
                        ["setPiece"] = SynthesizedSetPieceToken(item.setPiece)
                    })),
                ["daisShowpieces"] = BuildDaisShowpieceProjection(plan.daisShowpieces),
                ["namedPromontories"] = BuildNamedPromontoryProjection(plan.namedPromontories),
                ["recipeResolutions"] = BuildRecipeResolutionsProjection(plan.recipeResolutions),
                ["routeRequirements"] = BuildRouteRequirementResolutionProjection(plan.routeRequirementResolution)
            };
        }

        // Reconstructs the exact v8 canonical plan projection with only the
        // bounded external appendage removed. This makes the existing checked
        // v8 200-seed report an exact structural baseline, including every
        // optional bridge coordinate and synthesized set piece.
        private static JObject BuildPreCorrectiveTieredLevelPlanProjection(TieredLevelPlan plan)
        {
            var externalPierCells = new HashSet<Vector2Int>(
                CollectExternalConnectorPierCells(plan.externalConnectors));
            var coreLevels = new Dictionary<Vector2Int, int>();
            foreach (KeyValuePair<Vector2Int, int> item in plan.cellLevels)
            {
                if (!externalPierCells.Contains(item.Key))
                    coreLevels[item.Key] = item.Value;
            }

            var levelCells = new List<Vector2Int>(coreLevels.Keys);
            levelCells.Sort(CompareCells);
            var levels = new JArray();
            foreach (Vector2Int cell in levelCells)
            {
                levels.Add(new JObject
                {
                    ["cell"] = CellToken(cell),
                    ["level"] = coreLevels[cell]
                });
            }

            if (!TryBuildFloorStairPortGraph(
                    coreLevels,
                    plan.transitions,
                    out FloorStairPortGraph corePortGraph,
                    out string graphError))
            {
                throw new InvalidOperationException(
                    $"Could not reconstruct pre-corrective port graph: {graphError}");
            }
            GetLevelRange(coreLevels, out int coreMinLevel, out int coreMaxLevel);

            var synthesizedStairs = new JArray();
            if (plan.synthesizedStairs != null)
            {
                foreach ((string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece) item in plan.synthesizedStairs)
                {
                    synthesizedStairs.Add(new JObject
                    {
                        ["gapId"] = item.gapId,
                        ["setPiece"] = SynthesizedSetPieceToken(item.setPiece)
                    });
                }
            }

            return new JObject
            {
                ["cellLevels"] = levels,
                ["transitions"] = BuildExistingTransitionProjection(plan.transitions),
                ["levelCount"] = CountDistinctLevels(coreLevels),
                ["minLevel"] = coreMinLevel,
                ["maxLevel"] = coreMaxLevel,
                ["roomsPerTierSummary"] = plan.roomsPerTierSummary,
                ["overlookCount"] = CountSpatialOverlookEdges(coreLevels, plan.transitions),
                ["transitionSummary"] = plan.transitionSummary,
                ["connectorCandidateCount"] = plan.connectorCandidateCount,
                ["stairUsageSummary"] = plan.stairUsageSummary,
                ["topologySummary"] = plan.topologySummary,
                ["placementClassSummary"] = plan.placementClassSummary,
                ["stairCandidateSummary"] = plan.stairCandidateSummary,
                ["portGraphSummary"] = corePortGraph.Summary,
                ["archetypeName"] = plan.archetypeName,
                ["synthesizedStairs"] = synthesizedStairs,
                ["synthesizedStairSummary"] = plan.synthesizedStairSummary,
                ["daisShowpieces"] = BuildDaisShowpieceProjection(plan.daisShowpieces),
                ["promontoryCells"] = CellsToken(CollectNamedPromontoryCells(plan.namedPromontories), sort: true),
                ["namedPromontories"] = BuildNamedPromontoryProjection(plan.namedPromontories),
                ["recipeResolutions"] = BuildRecipeResolutionsProjection(plan.recipeResolutions),
                ["routeRequirements"] = BuildRouteRequirementResolutionProjection(plan.routeRequirementResolution)
            };
        }

        private static JArray BuildDaisShowpieceProjection(IReadOnlyList<DaisShowpiece> source)
        {
            var showpieces = new JArray();
            foreach (DaisShowpiece showpiece in source ?? Array.Empty<DaisShowpiece>())
            {
                var pieces = new JArray();
                foreach (ElevationEdgeModel.SynthesizedPiecePlacement piece in
                         showpiece.pieces ?? Array.Empty<ElevationEdgeModel.SynthesizedPiecePlacement>())
                {
                    pieces.Add(SynthesizedPieceToken(piece));
                }

                showpieces.Add(new JObject
                {
                    ["designName"] = showpiece.designName,
                    ["originCell"] = CellToken(showpiece.originCell),
                    ["yawDegrees"] = showpiece.yawDegrees,
                    ["roomLevel"] = showpiece.roomLevel,
                    ["pieces"] = pieces
                });
            }

            return showpieces;
        }

        private static JArray BuildExternalConnectorProjection(
            IReadOnlyList<ExternalConnectorPromontoryResolution> resolutions)
        {
            var result = new JArray();
            foreach (ExternalConnectorPromontoryResolution resolution in
                     resolutions ?? Array.Empty<ExternalConnectorPromontoryResolution>())
            {
                result.Add(new JObject
                {
                    ["id"] = resolution.id,
                    ["direction"] = DirectionName(resolution.direction),
                    ["directionId"] = resolution.direction,
                    ["anchorCell"] = CellToken(resolution.anchorCell),
                    ["terminalCell"] = CellToken(resolution.terminalCell),
                    ["level"] = resolution.level,
                    ["occupiedCells"] = CellsToken(resolution.occupiedCells, sort: false),
                    ["terminalPort"] = new JObject
                    {
                        ["cell"] = CellToken(resolution.terminalCell),
                        ["direction"] = DirectionName(resolution.direction),
                        ["widthCells"] = 1,
                        ["level"] = resolution.level
                    }
                });
            }

            return result;
        }

        private static JArray BuildNamedPromontoryProjection(
            IReadOnlyList<NamedVistaPromontoryResolution> resolutions)
        {
            var result = new JArray();
            foreach (NamedVistaPromontoryResolution resolution in
                resolutions ?? Array.Empty<NamedVistaPromontoryResolution>())
            {
                result.Add(new JObject
                {
                    ["vistaId"] = resolution.vistaId,
                    ["targetNodeId"] = resolution.targetNodeId,
                    ["sourceCell"] = CellToken(resolution.sourceCell),
                    ["targetCell"] = CellToken(resolution.targetCell),
                    ["facing"] = CellToken(resolution.facing),
                    ["level"] = resolution.level,
                    ["cells"] = CellsToken(resolution.cells, sort: false)
                });
            }

            return result;
        }

        private static JToken SynthesizedSetPieceToken(ElevationEdgeModel.SynthesizedStairSetPiece setPiece)
        {
            if (setPiece == null)
            {
                return JValue.CreateNull();
            }

            var pieces = new JArray();
            foreach (ElevationEdgeModel.SynthesizedPiecePlacement piece in setPiece.pieces)
            {
                pieces.Add(SynthesizedPieceToken(piece));
            }

            return new JObject
            {
                ["name"] = setPiece.name,
                ["contractToken"] = CanonicalizeJson(setPiece.contractToken),
                ["pieces"] = pieces
            };
        }

        private static JObject SynthesizedPieceToken(ElevationEdgeModel.SynthesizedPiecePlacement piece)
        {
            return new JObject
            {
                ["sourcePrefab"] = piece.sourcePrefab,
                ["pieceName"] = piece.pieceName,
                ["localPosition"] = Vector3Token(piece.localPosition),
                ["localYawDegrees"] = piece.localYawDegrees,
                ["localPitchDegrees"] = piece.localPitchDegrees
            };
        }

        private static JToken CanonicalizeJson(JToken token)
        {
            if (token == null)
            {
                return JValue.CreateNull();
            }

            if (token is JObject obj)
            {
                var names = new List<string>();
                foreach (JProperty property in obj.Properties())
                {
                    names.Add(property.Name);
                }

                names.Sort(StringComparer.Ordinal);
                var result = new JObject();
                foreach (string name in names)
                {
                    result[name] = CanonicalizeJson(obj[name]);
                }

                return result;
            }

            if (token is JArray array)
            {
                var result = new JArray();
                foreach (JToken child in array)
                {
                    result.Add(CanonicalizeJson(child));
                }

                return result;
            }

            return token.DeepClone();
        }

        private static JArray CellsToken(IEnumerable<Vector2Int> source, bool sort)
        {
            var cells = source == null ? new List<Vector2Int>() : new List<Vector2Int>(source);
            if (sort)
            {
                cells.Sort(CompareCells);
            }

            var result = new JArray();
            foreach (Vector2Int cell in cells)
            {
                result.Add(CellToken(cell));
            }

            return result;
        }

        private static JObject CellToken(Vector2Int cell)
        {
            return new JObject
            {
                ["x"] = cell.x,
                ["y"] = cell.y
            };
        }

        private static JObject RectToken(RectInt rect)
        {
            return new JObject
            {
                ["x"] = rect.x,
                ["y"] = rect.y,
                ["width"] = rect.width,
                ["height"] = rect.height
            };
        }

        private static JObject Vector3Token(Vector3 vector)
        {
            return new JObject
            {
                ["x"] = vector.x,
                ["y"] = vector.y,
                ["z"] = vector.z
            };
        }

        private static JObject CheckToken(bool passed, string message)
        {
            return new JObject
            {
                ["passed"] = passed,
                ["message"] = message ?? string.Empty
            };
        }

        private static string SnapshotLine(string key, JToken value)
        {
            string serialized = value == null || value.Type == JTokenType.Null
                ? string.Empty
                : value.Type == JTokenType.Boolean
                    ? (value.Value<bool>() ? "true" : "false")
                    : value.Type == JTokenType.String
                        ? value.Value<string>()
                    : value.ToString(Formatting.None);
            return $"{key}={serialized}";
        }

        // Historical Pearson correlation between a room's BFS depth from the hall and its
        // assigned tier. Retained for cross-phase characterization; the active ascending-spine
        // route policy is intentionally expected to correlate depth and elevation.
        private static float CalculateDepthLevelCorrelation(DungeonLayout layout, TieredLevelPlan plan)
        {
            int roomCount = layout.rooms.Count;
            if (roomCount < 3)
            {
                return float.NaN;
            }

            List<int>[] adjacency = BuildRoomAdjacency(roomCount, layout.connections);
            var depths = new int[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                depths[i] = -1;
            }

            var queue = new Queue<int>();
            depths[0] = 0;
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int neighbor in adjacency[current])
                {
                    if (depths[neighbor] >= 0)
                    {
                        continue;
                    }

                    depths[neighbor] = depths[current] + 1;
                    queue.Enqueue(neighbor);
                }
            }

            var samples = new List<Vector2>();
            for (int i = 0; i < roomCount; i++)
            {
                if (depths[i] < 0 || !TryGetRoomLevel(layout.rooms[i], plan.cellLevels, out int level))
                {
                    continue;
                }

                samples.Add(new Vector2(depths[i], level));
            }

            if (samples.Count < 3)
            {
                return float.NaN;
            }

            double meanDepth = 0;
            double meanLevel = 0;
            foreach (Vector2 sample in samples)
            {
                meanDepth += sample.x;
                meanLevel += sample.y;
            }

            meanDepth /= samples.Count;
            meanLevel /= samples.Count;
            double covariance = 0;
            double depthVariance = 0;
            double levelVariance = 0;
            foreach (Vector2 sample in samples)
            {
                double depthDelta = sample.x - meanDepth;
                double levelDelta = sample.y - meanLevel;
                covariance += depthDelta * levelDelta;
                depthVariance += depthDelta * depthDelta;
                levelVariance += levelDelta * levelDelta;
            }

            if (depthVariance < 1e-9 || levelVariance < 1e-9)
            {
                return float.NaN;
            }

            return (float)(covariance / Math.Sqrt(depthVariance * levelVariance));
        }

        private static bool TryGetRoomLevel(RoomFootprint room, Dictionary<Vector2Int, int> cellLevels, out int level)
        {
            if (cellLevels.TryGetValue(room.Center, out level))
            {
                return true;
            }

            foreach (Vector2Int cell in room.CellsRowMajor())
            {
                if (cellLevels.TryGetValue(cell, out level))
                {
                    return true;
                }
            }

            level = -1;
            return false;
        }

        private static string FormatCountSummary(SortedDictionary<string, int> counts)
        {
            if (counts.Count == 0)
            {
                return "none";
            }

            var parts = new List<string>();
            foreach (KeyValuePair<string, int> entry in counts)
            {
                parts.Add($"{entry.Key}:{entry.Value}");
            }

            return string.Join(", ", parts);
        }

        private static string FormatTierSpanSummary(SortedDictionary<int, int> counts)
        {
            if (counts.Count == 0)
            {
                return "none";
            }

            var parts = new List<string>();
            foreach (KeyValuePair<int, int> entry in counts)
            {
                parts.Add($"span{entry.Key}:{entry.Value}");
            }

            return string.Join(", ", parts);
        }

        private static string FormatCorrelationSummary(List<float> correlations)
        {
            if (correlations.Count == 0)
            {
                return "n/a";
            }

            float min = float.MaxValue;
            float max = float.MinValue;
            double sum = 0;
            foreach (float correlation in correlations)
            {
                min = Mathf.Min(min, correlation);
                max = Mathf.Max(max, correlation);
                sum += correlation;
            }

            return $"mean {sum / correlations.Count:0.##}, min {min:0.##}, max {max:0.##}";
        }

        private static JObject BuildIntDistribution(List<int> values)
        {
            if (values == null || values.Count == 0)
            {
                return new JObject
                {
                    ["sampleCount"] = 0,
                    ["min"] = 0,
                    ["p50"] = 0,
                    ["p95"] = 0,
                    ["max"] = 0,
                    ["mean"] = 0d,
                    ["histogram"] = new JObject()
                };
            }

            var sorted = new List<int>(values);
            sorted.Sort();
            double sum = 0;
            var histogram = new SortedDictionary<int, int>();
            foreach (int value in sorted)
            {
                sum += value;
                histogram.TryGetValue(value, out int count);
                histogram[value] = count + 1;
            }

            var histogramToken = new JObject();
            foreach (KeyValuePair<int, int> entry in histogram)
            {
                histogramToken[entry.Key.ToString()] = entry.Value;
            }

            return new JObject
            {
                ["sampleCount"] = sorted.Count,
                ["min"] = sorted[0],
                ["p50"] = NearestRank(sorted, 0.50),
                ["p95"] = NearestRank(sorted, 0.95),
                ["max"] = sorted[sorted.Count - 1],
                ["mean"] = sum / sorted.Count,
                ["histogram"] = histogramToken
            };
        }

        private sealed class DensityAdjacencyBatchAccumulator
        {
            public int acceptedSeeds;
            public int reservedVistaPreservedSeeds;
            public readonly List<int> finalRoomDegrees = new List<int>();
            public readonly List<int> exteriorCorridorCellsPerSeed = new List<int>();
            public readonly List<int> perConnectionExteriorLengths = new List<int>();
            public readonly List<int> sharedWallDoorsPerSeed = new List<int>();
            public readonly List<int> reservedVistaCellsPerSeed = new List<int>();
            public readonly List<int> atriumCenterVoidCellsPerSeed = new List<int>();
        }

        private static JObject BuildDensityAdjacencyBatchMeasurements(JArray seedReports)
        {
            var byTopology = new SortedDictionary<string, DensityAdjacencyBatchAccumulator>(StringComparer.Ordinal);
            foreach (JToken seedReport in seedReports ?? new JArray())
            {
                JObject measurements = seedReport["measurements"] as JObject;
                if (seedReport.Value<bool?>("accepted") != true ||
                    measurements?.Value<bool?>("available") != true)
                {
                    continue;
                }

                string topology = measurements.Value<string>("topology") ?? "unknown";
                if (!byTopology.TryGetValue(topology, out DensityAdjacencyBatchAccumulator accumulator))
                {
                    accumulator = new DensityAdjacencyBatchAccumulator();
                    byTopology[topology] = accumulator;
                }

                accumulator.acceptedSeeds++;
                AppendDistributionValues(
                    measurements["finalRoomDegreeDistribution"] as JObject,
                    accumulator.finalRoomDegrees);
                JObject corridorEvidence = measurements["corridorEvidence"] as JObject ?? new JObject();
                accumulator.exteriorCorridorCellsPerSeed.Add(
                    corridorEvidence.Value<int?>("exteriorCorridorCellCount") ?? 0);
                accumulator.sharedWallDoorsPerSeed.Add(
                    corridorEvidence.Value<int?>("sharedWallDoorCount") ?? 0);
                AppendDistributionValues(
                    corridorEvidence["perConnectionExteriorLengthDistribution"] as JObject,
                    accumulator.perConnectionExteriorLengths);

                JObject voidExtent = measurements["voidExtent"] as JObject ?? new JObject();
                accumulator.reservedVistaCellsPerSeed.Add(
                    voidExtent.Value<int?>("reservedVistaCellCount") ?? 0);
                if (voidExtent.Value<bool?>("reservedVistaPreservedAfterTierLooping") == true)
                {
                    accumulator.reservedVistaPreservedSeeds++;
                }

                int? atriumCenterVoidCells = voidExtent.Value<int?>("atriumCenterVoidCellCount");
                if (atriumCenterVoidCells.HasValue)
                {
                    accumulator.atriumCenterVoidCellsPerSeed.Add(atriumCenterVoidCells.Value);
                }
            }

            var topologyReports = new JObject();
            foreach (KeyValuePair<string, DensityAdjacencyBatchAccumulator> entry in byTopology)
            {
                DensityAdjacencyBatchAccumulator accumulator = entry.Value;
                topologyReports[entry.Key] = new JObject
                {
                    ["acceptedSeeds"] = accumulator.acceptedSeeds,
                    ["finalRoomDegreeDistribution"] = BuildIntDistribution(accumulator.finalRoomDegrees),
                    ["corridorEvidence"] = new JObject
                    {
                        ["exteriorCorridorCellsPerSeed"] = BuildIntDistribution(accumulator.exteriorCorridorCellsPerSeed),
                        ["perConnectionExteriorLengthDistribution"] = BuildIntDistribution(accumulator.perConnectionExteriorLengths),
                        ["sharedWallDoorsPerSeed"] = BuildIntDistribution(accumulator.sharedWallDoorsPerSeed)
                    },
                    ["voidExtent"] = new JObject
                    {
                        ["reservedVistaCellsPerSeed"] = BuildIntDistribution(accumulator.reservedVistaCellsPerSeed),
                        ["reservedVistaPreservedSeeds"] = accumulator.reservedVistaPreservedSeeds,
                        ["atriumCenterVoidCellsPerSeed"] = BuildIntDistribution(accumulator.atriumCenterVoidCellsPerSeed)
                    }
                };
            }

            return new JObject
            {
                ["measurementVersion"] = "density-adjacency-v1",
                ["byTopology"] = topologyReports
            };
        }

        private static void AppendDistributionValues(JObject distribution, List<int> values)
        {
            JObject histogram = distribution?["histogram"] as JObject;
            if (histogram == null)
            {
                return;
            }

            foreach (JProperty property in histogram.Properties())
            {
                if (!int.TryParse(property.Name, out int value))
                {
                    continue;
                }

                int count = property.Value.Value<int>();
                for (int index = 0; index < count; index++)
                {
                    values.Add(value);
                }
            }
        }

        private static int NearestRank(IReadOnlyList<int> sortedValues, double percentile)
        {
            int index = Math.Max(0, Math.Min(
                sortedValues.Count - 1,
                (int)Math.Ceiling(percentile * sortedValues.Count) - 1));
            return sortedValues[index];
        }

        private static string WritePhase0BatchReport(
            int firstSeed,
            int seedCount,
            int successCount,
            int hardValidCount,
            Dictionary<string, int> rejectionHistogram,
            Dictionary<string, int> rejectionCodeHistogram,
            Dictionary<string, int> validationFailureCodeHistogram,
            SortedDictionary<string, int> archetypeCounts,
            SortedDictionary<string, int> selectedPatternCounts,
            SortedDictionary<string, int> acceptedPatternCounts,
            List<float> correlations,
            List<int> allAttemptCounts,
            List<int> acceptedAttemptCounts,
            List<int> routeRoomCounts,
            List<int> branchNodeCounts,
            List<int> loopEdgeCounts,
            List<int> elevationSpans,
            List<int> transitionCounts,
            List<int> visibleDistantRoomProxyCounts,
            List<int> routeClimbCounts,
            int routeRequirementsValidCount,
            int finalVistaValidCount,
            int recipeSetValidCount,
            JArray seedReports,
            Phase7BatchEvidence phase7Evidence,
            CorrectiveBatchEvidence correctiveEvidence)
        {
            var archetypes = new JObject();
            foreach (KeyValuePair<string, int> entry in archetypeCounts)
            {
                archetypes[entry.Key] = entry.Value;
            }

            JObject selectedPatterns = HistogramToken(selectedPatternCounts);
            JObject acceptedPatterns = HistogramToken(acceptedPatternCounts);

            string resultHash = ComputeSha256(seedReports.ToString(Formatting.None));
            JObject attemptDistribution = BuildIntDistribution(allAttemptCounts);
            var report = new JObject
            {
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["catalogDigest"] = Phase0CatalogDigest(),
                ["firstSeed"] = firstSeed,
                ["lastSeed"] = firstSeed + seedCount - 1,
                ["seedCount"] = seedCount,
                ["accepted"] = successCount,
                ["failed"] = seedCount - successCount,
                ["hardValid"] = hardValidCount,
                ["attemptDistribution"] = attemptDistribution,
                ["acceptedAttemptDistribution"] = BuildIntDistribution(acceptedAttemptCounts),
                ["archetypes"] = archetypes,
                ["topologySelection"] = new JObject
                {
                    ["method"] = "seed-modulo4-v1",
                    ["selectedPatternCounts"] = selectedPatterns,
                    ["acceptedPatternCounts"] = acceptedPatterns
                },
                ["depthLevelCorrelation"] = FormatCorrelationSummary(correlations),
                ["metrics"] = new JObject
                {
                    ["longestRootRouteRooms"] = BuildIntDistribution(routeRoomCounts),
                    ["branchNodes"] = BuildIntDistribution(branchNodeCounts),
                    ["loopEdges"] = BuildIntDistribution(loopEdgeCounts),
                    ["elevationSpan"] = BuildIntDistribution(elevationSpans),
                    ["transitions"] = BuildIntDistribution(transitionCounts),
                    ["visibleDistantRoomProxy"] = BuildIntDistribution(visibleDistantRoomProxyCounts),
                    ["routeClimbLevels"] = BuildIntDistribution(routeClimbCounts)
                },
                ["rejectionHistogram"] = HistogramToken(rejectionHistogram),
                ["rejectionCodes"] = HistogramToken(rejectionCodeHistogram),
                ["validationFailureCodes"] = HistogramToken(validationFailureCodeHistogram),
                ["resultHashAlgorithm"] = "SHA-256 over the ordered seed-report array; generatedAtUtc excluded",
                ["resultHash"] = resultHash,
                ["deletionLedger"] = new JArray(),
                ["seeds"] = seedReports
            };
            AddGenerationSettingsIdentity(report);
            report["measurements"] = BuildDensityAdjacencyBatchMeasurements(seedReports);
            bool isLockedReliabilityCorpus =
                firstSeed == Phase0BaselineFirstSeed && seedCount == LockedSeedCount;
            if (isLockedReliabilityCorpus)
            {
                bool completionPassed = hardValidCount >= 95;
                bool attemptCeilingPassed = attemptDistribution.Value<int>("max") <= Phase1LayoutAttemptLimit;
                bool p95Passed = attemptDistribution.Value<int>("p95") <= 1;
                bool acceptedHardValid = hardValidCount == successCount;
                bool failuresReasonCoded = FailuresAreReasonCoded(seedReports);
                report["lockedReliabilityBudget"] = new JObject
                {
                    ["corpus"] = $"{firstSeed}..{firstSeed + seedCount - 1}",
                    ["completionFloor"] = 95,
                    ["attemptCeiling"] = Phase1LayoutAttemptLimit,
                    ["p95AttemptTarget"] = 1
                };
                report["budgetResult"] = new JObject
                {
                    ["passed"] = completionPassed && attemptCeilingPassed && p95Passed && acceptedHardValid && failuresReasonCoded,
                    ["hardValidCompletions"] = hardValidCount,
                    ["completionFloorPassed"] = completionPassed,
                    ["attemptCeilingPassed"] = attemptCeilingPassed,
                    ["p95AttemptTargetPassed"] = p95Passed,
                    ["everyAcceptedPlanHardValid"] = acceptedHardValid,
                    ["everyFailureReasonCoded"] = failuresReasonCoded
                };
            }

            bool isPhase3ReliabilityCorpus =
                firstSeed == Phase0BaselineFirstSeed && seedCount == Phase0BaselineSeedCount;
            if (isPhase3ReliabilityCorpus)
            {
                bool completionPassed = hardValidCount >= Phase3HardValidCompletionFloor;
                bool attemptCeilingPassed = attemptDistribution.Value<int>("max") <= Phase1LayoutAttemptLimit;
                bool p95Passed = attemptDistribution.Value<int>("p95") <= 1;
                bool acceptedHardValid = hardValidCount == successCount;
                bool failuresReasonCoded = FailuresAreReasonCoded(seedReports);
                bool routeRequirementsPassed = routeRequirementsValidCount == successCount;
                bool finalVistasPassed = finalVistaValidCount == successCount;
                report["phase3ReliabilityBudget"] = new JObject
                {
                    ["corpus"] = $"{firstSeed}..{firstSeed + seedCount - 1}",
                    ["hardValidCompletionFloor"] = Phase3HardValidCompletionFloor,
                    ["attemptCeiling"] = Phase1LayoutAttemptLimit,
                    ["p95AttemptTarget"] = 1,
                    ["requiredRouteClimbLevels"] = MaxGeneratedLevel,
                    ["requiredFinalVista"] = true
                };
                report["phase3BudgetResult"] = new JObject
                {
                    ["passed"] = completionPassed &&
                        attemptCeilingPassed &&
                        p95Passed &&
                        acceptedHardValid &&
                        failuresReasonCoded &&
                        routeRequirementsPassed &&
                        finalVistasPassed,
                    ["hardValidCompletions"] = hardValidCount,
                    ["completionFloorPassed"] = completionPassed,
                    ["attemptCeilingPassed"] = attemptCeilingPassed,
                    ["p95AttemptTargetPassed"] = p95Passed,
                    ["everyAcceptedPlanHardValid"] = acceptedHardValid,
                    ["everyFailureReasonCoded"] = failuresReasonCoded,
                    ["routeRequirementsValid"] = routeRequirementsValidCount,
                    ["everyAcceptedRouteRequirementValid"] = routeRequirementsPassed,
                    ["finalVistasValid"] = finalVistaValidCount,
                    ["everyAcceptedFinalVistaValid"] = finalVistasPassed
                };
            }

            if (isPhase3ReliabilityCorpus)
            {
                bool completionPassed = hardValidCount >= Phase3HardValidCompletionFloor;
                bool attemptCeilingPassed = attemptDistribution.Value<int>("max") <= Phase1LayoutAttemptLimit;
                bool p95Passed = attemptDistribution.Value<int>("p95") <= 1;
                bool acceptedHardValid = hardValidCount == successCount;
                bool failuresReasonCoded = FailuresAreReasonCoded(seedReports);
                bool routeRequirementsPassed = routeRequirementsValidCount == successCount;
                bool finalVistasPassed = finalVistaValidCount == successCount;
                bool recipeProbePassed = recipeSetValidCount == successCount;
                report["phase4ReliabilityBudget"] = new JObject
                {
                    ["corpus"] = $"{firstSeed}..{firstSeed + seedCount - 1}",
                    ["hardValidCompletionFloor"] = Phase3HardValidCompletionFloor,
                    ["attemptCeiling"] = Phase1LayoutAttemptLimit,
                    ["p95AttemptTarget"] = 1,
                    ["requiredEpisodeId"] = DungeonRecipeIds.ProcessionalLandmark,
                    ["requiredAtomicEpisode"] = true
                };
                report["phase4BudgetResult"] = new JObject
                {
                    ["passed"] = completionPassed &&
                        attemptCeilingPassed &&
                        p95Passed &&
                        acceptedHardValid &&
                        failuresReasonCoded &&
                        routeRequirementsPassed &&
                        finalVistasPassed &&
                        recipeProbePassed,
                    ["hardValidCompletions"] = hardValidCount,
                    ["completionFloorPassed"] = completionPassed,
                    ["attemptCeilingPassed"] = attemptCeilingPassed,
                    ["p95AttemptTargetPassed"] = p95Passed,
                    ["everyAcceptedPlanHardValid"] = acceptedHardValid,
                    ["everyFailureReasonCoded"] = failuresReasonCoded,
                    ["everyAcceptedRouteRequirementValid"] = routeRequirementsPassed,
                    ["everyAcceptedFinalVistaValid"] = finalVistasPassed,
                    ["recipeProbeValid"] = recipeSetValidCount,
                    ["everyAcceptedRecipeProbeValid"] = recipeProbePassed
                };
            }

            if (isPhase3ReliabilityCorpus)
            {
                bool completionPassed = hardValidCount >= Phase3HardValidCompletionFloor;
                bool attemptCeilingPassed = attemptDistribution.Value<int>("max") <= Phase1LayoutAttemptLimit;
                bool p95Passed = attemptDistribution.Value<int>("p95") <= 1;
                bool acceptedHardValid = hardValidCount == successCount;
                bool failuresReasonCoded = FailuresAreReasonCoded(seedReports);
                bool routeRequirementsPassed = routeRequirementsValidCount == successCount;
                bool finalVistasPassed = finalVistaValidCount == successCount;
                bool recipeSetsPassed = recipeSetValidCount == successCount;
                string reviewedCatalogDigest = DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog activeCatalog,
                    out _)
                    ? activeCatalog.digest
                    : string.Empty;
                report["phase5HistoricalReference"] = new JObject
                {
                    ["corpus"] = $"{firstSeed}..{firstSeed + seedCount - 1}",
                    ["hardValidCompletionFloor"] = Phase3HardValidCompletionFloor,
                    ["attemptCeiling"] = Phase1LayoutAttemptLimit,
                    ["p95AttemptTarget"] = 1,
                    ["requiredRecipeCountAtBoundary"] = 2,
                    ["requiredRecipeIdsAtBoundary"] = new JArray(
                        DungeonRecipeIds.ProcessionalLandmark,
                        DungeonRecipeIds.CompressionConnector),
                    ["reviewedRecipeCatalogDigest"] = reviewedCatalogDigest,
                    ["supersededBy"] = "phase6fReliabilityBudget"
                };
                report["phase5HistoricalResult"] = new JObject
                {
                    ["currentCompatibilityPassed"] = completionPassed &&
                        attemptCeilingPassed &&
                        p95Passed &&
                        acceptedHardValid &&
                        failuresReasonCoded &&
                        routeRequirementsPassed &&
                        finalVistasPassed &&
                        recipeSetsPassed &&
                        !string.IsNullOrEmpty(reviewedCatalogDigest),
                    ["hardValidCompletions"] = hardValidCount,
                    ["completionFloorPassed"] = completionPassed,
                    ["attemptCeilingPassed"] = attemptCeilingPassed,
                    ["p95AttemptTargetPassed"] = p95Passed,
                    ["everyAcceptedPlanHardValid"] = acceptedHardValid,
                    ["everyFailureReasonCoded"] = failuresReasonCoded,
                    ["everyAcceptedRouteRequirementValid"] = routeRequirementsPassed,
                    ["everyAcceptedFinalVistaValid"] = finalVistasPassed,
                    ["recipeSetsValid"] = recipeSetValidCount,
                    ["everyAcceptedRecipeSetValid"] = recipeSetsPassed,
                    ["reviewedRecipeCatalogLoaded"] = !string.IsNullOrEmpty(reviewedCatalogDigest)
                };
            }

            if (isPhase3ReliabilityCorpus)
            {
                int processionalSelected = selectedPatternCounts.TryGetValue(Phase1PatternId, out int selectedProcessional)
                    ? selectedProcessional
                    : 0;
                int atriumSelected = selectedPatternCounts.TryGetValue(AtriumRingPatternId, out int selectedAtrium)
                    ? selectedAtrium
                    : 0;
                int twinWingSelected = selectedPatternCounts.TryGetValue(TwinWingPatternId, out int selectedTwinWing)
                    ? selectedTwinWing
                    : 0;
                int processionalAccepted = acceptedPatternCounts.TryGetValue(Phase1PatternId, out int acceptedProcessional)
                    ? acceptedProcessional
                    : 0;
                int atriumAccepted = acceptedPatternCounts.TryGetValue(AtriumRingPatternId, out int acceptedAtrium)
                    ? acceptedAtrium
                    : 0;
                int twinWingAccepted = acceptedPatternCounts.TryGetValue(TwinWingPatternId, out int acceptedTwinWing)
                    ? acceptedTwinWing
                    : 0;
                bool exactSplit = processionalSelected == 100 && atriumSelected == 50 && twinWingSelected == 50;
                bool completionPassed = hardValidCount >= 198 &&
                    processionalAccepted == processionalSelected &&
                    atriumAccepted == atriumSelected &&
                    twinWingAccepted >= 48;
                bool attemptCeilingPassed = attemptDistribution.Value<int>("max") <= Phase1LayoutAttemptLimit;
                bool acceptedHardValid = hardValidCount == successCount;
                bool routeRequirementsPassed = routeRequirementsValidCount == successCount;
                bool finalVistasPassed = finalVistaValidCount == successCount;
                bool recipeSetsPassed = recipeSetValidCount == successCount;
                report["phase6cReliabilityBudget"] = new JObject
                {
                    ["corpus"] = $"{firstSeed}..{firstSeed + seedCount - 1}",
                    ["selectionMethod"] = "seed-modulo4-v1",
                    ["requiredProcessionalSeeds"] = 100,
                    ["requiredAtriumRingSeeds"] = 50,
                    ["requiredTwinWingSeeds"] = 50,
                    ["requiredProcessionalAccepted"] = 100,
                    ["requiredAtriumRingAccepted"] = 50,
                    ["twinWingCompletionFloor"] = 48,
                    ["overallHardValidCompletionFloor"] = 198,
                    ["attemptCeiling"] = Phase1LayoutAttemptLimit
                };
                report["phase6cBudgetResult"] = new JObject
                {
                    ["passed"] = exactSplit &&
                        completionPassed &&
                        attemptCeilingPassed &&
                        acceptedHardValid &&
                        routeRequirementsPassed &&
                        finalVistasPassed &&
                        recipeSetsPassed &&
                        FailuresAreReasonCoded(seedReports),
                    ["exactPatternSplit"] = exactSplit,
                    ["processionalAccepted"] = processionalAccepted,
                    ["atriumRingAccepted"] = atriumAccepted,
                    ["twinWingAccepted"] = twinWingAccepted,
                    ["hardValidCompletions"] = hardValidCount,
                    ["completionFloorPassed"] = completionPassed,
                    ["attemptCeilingPassed"] = attemptCeilingPassed,
                    ["everyAcceptedPlanHardValid"] = acceptedHardValid,
                    ["everyAcceptedRouteRequirementValid"] = routeRequirementsPassed,
                    ["everyAcceptedFinalVistaValid"] = finalVistasPassed,
                    ["everyAcceptedRecipeSetValid"] = recipeSetsPassed,
                    ["everyFailureReasonCoded"] = FailuresAreReasonCoded(seedReports)
                };

                report["phase6dHistoricalReference"] = new JObject
                {
                    ["policyVersion"] = RouteRhythmPolicyVersion,
                    ["passedAtPhaseBoundary"] = true,
                    ["lockedResultHash"] = Phase6cLockedResultHash,
                    ["supersededAggregateReason"] = "Phase 6e intentionally advances the canonical tier plan to named promontory resolutions"
                };

                int namedPromontoryCount = 0;
                int processionalPromontories = 0;
                int atriumPromontories = 0;
                int twinWingPromontories = 0;
                int namedPromontoryValidCount = 0;
                foreach (JToken seedReport in seedReports)
                {
                    if (seedReport.Value<bool?>("accepted") != true)
                    {
                        continue;
                    }

                    JArray named = seedReport["namedPromontories"] as JArray ?? new JArray();
                    if (named.Count > 0)
                    {
                        namedPromontoryCount += named.Count;
                        string patternId = seedReport["routeIntent"]?.Value<string>("patternId") ?? string.Empty;
                        if (string.Equals(patternId, Phase1PatternId, StringComparison.Ordinal))
                        {
                            processionalPromontories += named.Count;
                        }
                        else if (string.Equals(patternId, AtriumRingPatternId, StringComparison.Ordinal))
                        {
                            atriumPromontories += named.Count;
                        }
                        else if (string.Equals(patternId, TwinWingPatternId, StringComparison.Ordinal))
                        {
                            twinWingPromontories += named.Count;
                        }
                    }

                    if (seedReport["validation"]?["namedPromontories"]?.Value<bool?>("passed") == true)
                    {
                        namedPromontoryValidCount++;
                    }
                }

                bool phase6eExactCompletion = successCount == Phase0BaselineSeedCount &&
                    hardValidCount == Phase0BaselineSeedCount;
                bool phase6eAttemptOne = attemptDistribution.Value<int>("max") == 1;
                bool phase6eExactPromontories = namedPromontoryCount == 114 &&
                    processionalPromontories == 22 &&
                    atriumPromontories == 50 &&
                    twinWingPromontories == 42;
                bool phase6eNamedValid = namedPromontoryValidCount == successCount;
                report["phase6eReliabilityBudget"] = new JObject
                {
                    ["corpus"] = $"{firstSeed}..{firstSeed + seedCount - 1}",
                    ["policyVersion"] = NamedVistaPromontoryPolicyVersion,
                    ["requiredAccepted"] = Phase0BaselineSeedCount,
                    ["requiredHardValid"] = Phase0BaselineSeedCount,
                    ["requiredMaximumAttempt"] = 1,
                    ["requiredNamedPromontories"] = 114,
                    ["requiredProcessionalPromontories"] = 22,
                    ["requiredAtriumPromontories"] = 50,
                    ["requiredTwinWingPromontories"] = 42
                };
                report["phase6eBudgetResult"] = new JObject
                {
                    ["passed"] = exactSplit &&
                        phase6eExactCompletion &&
                        phase6eAttemptOne &&
                        acceptedHardValid &&
                        routeRequirementsPassed &&
                        finalVistasPassed &&
                        recipeSetsPassed &&
                        phase6eExactPromontories &&
                        phase6eNamedValid &&
                        FailuresAreReasonCoded(seedReports),
                    ["exactPatternSplit"] = exactSplit,
                    ["exactCompletion"] = phase6eExactCompletion,
                    ["maximumAttemptOne"] = phase6eAttemptOne,
                    ["everyAcceptedPlanHardValid"] = acceptedHardValid,
                    ["everyAcceptedRouteRequirementValid"] = routeRequirementsPassed,
                    ["everyAcceptedFinalVistaValid"] = finalVistasPassed,
                    ["everyAcceptedRecipeSetValid"] = recipeSetsPassed,
                    ["namedPromontories"] = namedPromontoryCount,
                    ["processionalPromontories"] = processionalPromontories,
                    ["atriumPromontories"] = atriumPromontories,
                    ["twinWingPromontories"] = twinWingPromontories,
                    ["exactNamedPromontoryDistribution"] = phase6eExactPromontories,
                    ["namedPromontoryValidSeeds"] = namedPromontoryValidCount,
                    ["everyAcceptedNamedPromontoryValid"] = phase6eNamedValid,
                    ["everyFailureReasonCoded"] = FailuresAreReasonCoded(seedReports)
                };

                int exactThreeRecipeSeedCount = 0;
                int totalRecipeResolutionCount = 0;
                int cornerReturnResolutionCount = 0;
                int plannerVersionMatchCount = 0;
                foreach (JToken seedReport in seedReports)
                {
                    if (seedReport.Value<bool?>("accepted") != true)
                    {
                        continue;
                    }

                    JArray recipes = seedReport["recipeResolutions"] as JArray ?? new JArray();
                    totalRecipeResolutionCount += recipes.Count;
                    int seedCornerReturns = 0;
                    foreach (JToken recipe in recipes)
                    {
                        if (string.Equals(
                                recipe.Value<string>("id"),
                                DungeonRecipeIds.CornerReturnConnector,
                                StringComparison.Ordinal))
                        {
                            seedCornerReturns++;
                            cornerReturnResolutionCount++;
                        }
                    }

                    if (recipes.Count == 3 && seedCornerReturns == 1 &&
                        seedReport["validation"]?["recipes"]?.Value<bool?>("passed") == true)
                    {
                        exactThreeRecipeSeedCount++;
                    }

                    string patternId = seedReport["routeIntent"]?.Value<string>("patternId") ?? string.Empty;
                    string plannerVersion = seedReport["routeIntent"]?.Value<string>("plannerVersion") ?? string.Empty;
                    if (string.Equals(patternId, Phase1PatternId, StringComparison.Ordinal) &&
                            string.Equals(plannerVersion, ProcessionalPlannerVersion, StringComparison.Ordinal) ||
                        string.Equals(patternId, AtriumRingPatternId, StringComparison.Ordinal) &&
                            string.Equals(plannerVersion, AtriumRingPlannerVersion, StringComparison.Ordinal) ||
                        string.Equals(patternId, TwinWingPatternId, StringComparison.Ordinal) &&
                            string.Equals(plannerVersion, TwinWingPlannerVersion, StringComparison.Ordinal))
                    {
                        plannerVersionMatchCount++;
                    }
                }

                bool phase6fExactCompletion = successCount == Phase0BaselineSeedCount &&
                    hardValidCount == Phase0BaselineSeedCount;
                bool phase6fAttemptBudget = attemptDistribution.Value<int>("p95") <= 1 &&
                    attemptDistribution.Value<int>("max") <= Phase1LayoutAttemptLimit;
                bool phase6fExactRecipeSet = exactThreeRecipeSeedCount == successCount &&
                    totalRecipeResolutionCount == Phase0BaselineSeedCount * 3 &&
                    cornerReturnResolutionCount == Phase0BaselineSeedCount;
                bool phase6fVersionsMatch = plannerVersionMatchCount == successCount;
                bool phase6fCatalogValid = DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog phase6fCatalog,
                    out _) &&
                    phase6fCatalog.recipes.Length == 3 &&
                    phase6fCatalog.TryGet(DungeonRecipeIds.CornerReturnConnector, out DungeonRecipeAsset phase6fRecipe) &&
                    phase6fRecipe.schemaVersion == DungeonRecipeAsset.CurrentSchemaVersion &&
                    DungeonRecipeValidator.ReviewIsCurrent(phase6fRecipe);
                report["phase6fReliabilityBudget"] = new JObject
                {
                    ["corpus"] = $"{firstSeed}..{firstSeed + seedCount - 1}",
                    ["requiredAccepted"] = Phase0BaselineSeedCount,
                    ["requiredHardValid"] = Phase0BaselineSeedCount,
                    ["requiredP95Attempt"] = 1,
                    ["requiredMaximumAttempt"] = Phase1LayoutAttemptLimit,
                    ["requiredRecipeCountPerSeed"] = 3,
                    ["requiredCornerReturnResolutions"] = Phase0BaselineSeedCount,
                    ["requiredRecipeId"] = DungeonRecipeIds.CornerReturnConnector,
                    ["requiredSummaryVersion"] = DungeonPlanSummaryVersion,
                    ["requiredGeneratorVersion"] = RoutePlannerVersion
                };
                report["phase6fBudgetResult"] = new JObject
                {
                    ["passed"] = exactSplit &&
                        phase6fExactCompletion &&
                        phase6fAttemptBudget &&
                        acceptedHardValid &&
                        routeRequirementsPassed &&
                        finalVistasPassed &&
                        recipeSetsPassed &&
                        phase6eExactPromontories &&
                        phase6eNamedValid &&
                        phase6fExactRecipeSet &&
                        phase6fVersionsMatch &&
                        phase6fCatalogValid &&
                        FailuresAreReasonCoded(seedReports),
                    ["exactPatternSplit"] = exactSplit,
                    ["exactCompletion"] = phase6fExactCompletion,
                    ["attemptBudgetPassed"] = phase6fAttemptBudget,
                    ["everyAcceptedPlanHardValid"] = acceptedHardValid,
                    ["everyAcceptedRouteRequirementValid"] = routeRequirementsPassed,
                    ["everyAcceptedFinalVistaValid"] = finalVistasPassed,
                    ["everyAcceptedRecipeSetValid"] = recipeSetsPassed,
                    ["everyAcceptedNamedPromontoryValid"] = phase6eNamedValid,
                    ["namedPromontories"] = namedPromontoryCount,
                    ["exactThreeRecipeSeeds"] = exactThreeRecipeSeedCount,
                    ["recipeResolutions"] = totalRecipeResolutionCount,
                    ["cornerReturnResolutions"] = cornerReturnResolutionCount,
                    ["exactRecipeDistribution"] = phase6fExactRecipeSet,
                    ["plannerVersionMatchSeeds"] = plannerVersionMatchCount,
                    ["everyPlannerVersionCurrent"] = phase6fVersionsMatch,
                    ["reviewedThreeRecipeCatalogValid"] = phase6fCatalogValid,
                    ["everyFailureReasonCoded"] = FailuresAreReasonCoded(seedReports)
                };
            }

            if (phase7Evidence != null)
            {
                AppendPhase7SweepEvidence(
                    report,
                    seedReports,
                    selectedPatternCounts,
                    acceptedPatternCounts,
                    attemptDistribution,
                    successCount,
                    hardValidCount,
                    phase7Evidence);
            }

            if (correctiveEvidence != null)
            {
                AppendCorrectiveBatchEvidence(
                    report,
                    seedReports,
                    selectedPatternCounts,
                    attemptDistribution,
                    successCount,
                    hardValidCount,
                    correctiveEvidence);
            }

            Directory.CreateDirectory(BatchReportDirectory);
            string phase7Suffix = phase7Evidence != null
                ? $"_phase7_run{phase7Evidence.sweepOrdinal}"
                : correctiveEvidence != null
                    ? $"_corrective_run{correctiveEvidence.runOrdinal}"
                    : string.Empty;
            string reportPath = Path.Combine(
                BatchReportDirectory,
                $"dungeon_plan_{firstSeed}_{firstSeed + seedCount - 1}{phase7Suffix}.json");
            if (phase7Evidence != null)
            {
                WritePhase7DeterminismSidecar(report, seedReports, phase7Evidence);
            }
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            return reportPath;
        }

        private static bool FailuresAreReasonCoded(JArray seedReports)
        {
            foreach (JToken token in seedReports)
            {
                if (token.Value<bool?>("accepted") == true)
                {
                    continue;
                }

                string code = token.Value<string>("lastRejectionCode");
                if (string.IsNullOrEmpty(code) || string.Equals(code, "NONE", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static JObject HistogramToken(IReadOnlyDictionary<string, int> histogram)
        {
            var result = new JObject();
            if (histogram == null)
            {
                return result;
            }

            var keys = new List<string>(histogram.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                result[key] = histogram[key];
            }

            return result;
        }

        private static JObject RejectionCodeHistogramToken(IReadOnlyDictionary<string, int> rejectionHistogram)
        {
            var codes = new Dictionary<string, int>(StringComparer.Ordinal);
            if (rejectionHistogram != null)
            {
                foreach (KeyValuePair<string, int> entry in rejectionHistogram)
                {
                    string code = Phase0RejectionCode(entry.Key, exception: null);
                    codes.TryGetValue(code, out int count);
                    codes[code] = count + entry.Value;
                }
            }

            return HistogramToken(codes);
        }

        private static void MergeJsonHistogram(JObject source, Dictionary<string, int> destination)
        {
            if (source == null)
            {
                return;
            }

            foreach (JProperty property in source.Properties())
            {
                destination.TryGetValue(property.Name, out int count);
                destination[property.Name] = count + property.Value.Value<int>();
            }
        }

        private static void MergeJsonCodeArray(JArray source, Dictionary<string, int> destination)
        {
            if (source == null)
            {
                return;
            }

            foreach (JToken token in source)
            {
                string code = token.Value<string>();
                if (string.IsNullOrEmpty(code))
                {
                    continue;
                }

                destination.TryGetValue(code, out int count);
                destination[code] = count + 1;
            }
        }

        private static void AddFailureCode(JArray failureCodes, bool passed, string code)
        {
            if (!passed)
            {
                failureCodes.Add(code);
            }
        }

        private static string Phase0RejectionCode(string reason, Exception exception)
        {
            if (exception != null)
            {
                return "PLANNING_EXCEPTION";
            }

            string raw = reason ?? string.Empty;
            if (raw.StartsWith($"[{ExternalConnectorRejectionCode}]", StringComparison.Ordinal))
            {
                return ExternalConnectorRejectionCode;
            }
            if (raw.StartsWith("[ROUTE_", StringComparison.Ordinal))
            {
                int closingBracket = raw.IndexOf(']');
                if (closingBracket > 1)
                {
                    return raw.Substring(1, closingBracket - 1);
                }
            }

            string value = raw.ToLowerInvariant();
            if (value.Contains("headroom")) return "HEADROOM_CLEARANCE";
            if (value.Contains("no floor cells") || value.Contains("no leveled floor")) return "EMPTY_FLOOR";
            if (value.Contains("enough connected regions")) return "INSUFFICIENT_CONNECTED_REGIONS";
            if (value.Contains("no loop edges")) return "NO_LOOP_EDGE";
            if (value.Contains("floorplan had only")) return "INSUFFICIENT_ROOM_COUNT";
            if (value.Contains("floor-fill")) return "LOW_FLOOR_FILL";
            if (value.Contains("enough depth") || value.Contains("zone graph") || value.Contains("room graph left")) return "LEVEL_ASSIGNMENT";
            if (value.Contains("single level")) return "SINGLE_LEVEL";
            if (value.Contains("off-grammar") || value.Contains("differed by")) return "ROOM_LEVEL_DELTA";
            if (value.Contains("no usable corridor") || value.Contains("non-cardinal") || value.Contains("corridor cell pair")) return "CORRIDOR_PATH";
            if (value.Contains("reviewed active stair contract placement") || value.Contains("synthesis offered no fitting")) return "STAIR_PLACEMENT";
            if (value.Contains("without a reviewed active stair contract")) return "STAIR_CONTRACT";
            if (value.Contains("[RECIPE_CATALOG]")) return "RECIPE_CATALOG";
            if (value.Contains("[RECIPE_")) return "RECIPE_CONTRACT";
            if (value.Contains("assigned both level")) return "CELL_LEVEL_CONFLICT";
            if (value.Contains("transition") && (value.Contains("missing") || value.Contains("level delta") || value.Contains("landing") || value.Contains("different levels"))) return "TRANSITION_CONTRACT";
            if (value.Contains("port graph") || value.Contains("floor/stair")) return "PORT_GRAPH";
            if (value.Contains("sealed with no doorway") || value.Contains("boundary context")) return "BOUNDARY_CONTEXT";
            if (string.IsNullOrWhiteSpace(value)) return "NONE";
            return "UNCLASSIFIED_REJECTION";
        }

        private static string Phase0CatalogDigest()
        {
            string settingsDigest = GenerationSettingsDigest(CurrentGenerationSettings);
            if (Phase0CatalogDigestCache.TryGetValue(settingsDigest, out string cachedDigest))
            {
                return cachedDigest;
            }

            string[] paths =
            {
                ResolveGenerationProfilePath(CurrentGenerationSettings.profileName),
                PackageInventoryPath,
                StairProofContractsPath,
                ForgedStairContractsPath,
                "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/stair_connector_settings.json",
                "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/step_piece_library.json"
            };
            var digestInput = new StringBuilder();
            foreach (string path in paths)
            {
                digestInput.Append(path).Append('\n');
                if (File.Exists(path))
                {
                    digestInput.Append(Convert.ToBase64String(File.ReadAllBytes(path)));
                }
                else
                {
                    digestInput.Append("<missing>");
                }

                digestInput.Append('\n');
            }

            if (DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog recipeCatalog,
                    out string recipeCatalogError))
            {
                digestInput.Append("reviewed-recipes\n").Append(recipeCatalog.digest).Append('\n');
            }
            else
            {
                digestInput.Append("reviewed-recipes\n<invalid:")
                    .Append(recipeCatalogError)
                    .Append(">\n");
            }

            string digest = ComputeSha256(digestInput.ToString());
            Phase0CatalogDigestCache[settingsDigest] = digest;
            return digest;
        }

        private static string ComputeSha256(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var result = new StringBuilder(hash.Length * 2);
                foreach (byte item in hash)
                {
                    result.Append(item.ToString("x2"));
                }

                return result.ToString();
            }
        }

        private static void CapturePhase0SentinelImage(Bounds bounds, string path)
        {
            CaptureDiagnosticReviewImage(path, camera =>
            {
                Vector3 center = bounds.center;
                float radius = Mathf.Max(16f, bounds.extents.magnitude);
                // Looking direction points down toward the floorplan; subtracting
                // it therefore places the review camera above the dungeon.
                Vector3 direction = new Vector3(-0.85f, -0.68f, -0.95f).normalized;
                camera.transform.position = center - direction * (radius * 1.7f);
                camera.transform.LookAt(center + Vector3.up * Mathf.Max(1.5f, bounds.extents.y * 0.1f));
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = Mathf.Max(250f, radius * 8f);
                camera.fieldOfView = 35f;
            });
        }

        private static void CaptureDiagnosticReviewImage(string path, Action<Camera> configureCamera)
        {
            var cameraObject = new GameObject("DungeonLab Phase0 Sentinel Camera")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var lightObject = new GameObject("DungeonLab Phase0 Sentinel Light")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            Camera camera = cameraObject.AddComponent<Camera>();
            Light light = lightObject.AddComponent<Light>();
            AmbientMode previousAmbientMode = RenderSettings.ambientMode;
            Color previousAmbientLight = RenderSettings.ambientLight;
            Color previousAmbientSky = RenderSettings.ambientSkyColor;
            Color previousAmbientEquator = RenderSettings.ambientEquatorColor;
            Color previousAmbientGround = RenderSettings.ambientGroundColor;
            bool previousFog = RenderSettings.fog;
            var renderTexture = new RenderTexture(Phase0SentinelImageWidth, Phase0SentinelImageHeight, 24);
            var texture = new Texture2D(Phase0SentinelImageWidth, Phase0SentinelImageHeight, TextureFormat.RGB24, false);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                configureCamera(camera);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.035f, 0.04f, 0.055f, 1f);
                camera.targetTexture = renderTexture;

                light.type = LightType.Directional;
                light.intensity = 1.25f;
                light.color = new Color(1f, 0.82f, 0.68f);
                light.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.22f, 0.23f, 0.28f);
                RenderSettings.fog = false;

                RenderTexture.active = renderTexture;
                camera.Render();
                texture.ReadPixels(
                    new Rect(0, 0, Phase0SentinelImageWidth, Phase0SentinelImageHeight),
                    0,
                    0);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previousActive;
                RenderSettings.ambientMode = previousAmbientMode;
                RenderSettings.ambientLight = previousAmbientLight;
                RenderSettings.ambientSkyColor = previousAmbientSky;
                RenderSettings.ambientEquatorColor = previousAmbientEquator;
                RenderSettings.ambientGroundColor = previousAmbientGround;
                RenderSettings.fog = previousFog;
                DestroyImmediate(renderTexture);
                DestroyImmediate(texture);
                DestroyImmediate(cameraObject);
                DestroyImmediate(lightObject);
            }
        }
    }
}

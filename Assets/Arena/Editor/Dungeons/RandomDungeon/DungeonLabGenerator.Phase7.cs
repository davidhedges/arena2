using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // Phase 7 evidence remains diagnostic-only. Timing and report comparison
    // wrap the canonical generator without participating in any random stream,
    // placement decision, accepted plan, renderer input, or collision output.
    internal sealed partial class DungeonLabGenerator
    {
        private const int Phase7FirstSeed = 2026072300;
        private const int Phase7SeedCount = 2000;
        private const int Phase7LastSeed = Phase7FirstSeed + Phase7SeedCount - 1;
        private const int Phase7WarmupSeedCount = 40;
        private const int Phase7RequiredAccepted = 1990;
        private const int Phase7RequiredProcessionalSelected = 1000;
        private const int Phase7RequiredAtriumSelected = 500;
        private const int Phase7RequiredTwinWingSelected = 500;
        private const int Phase7RequiredProcessionalAccepted = 990;
        private const int Phase7RequiredAtriumAccepted = 495;
        private const int Phase7RequiredTwinWingAccepted = 495;
        private const int Phase7RequiredP95Attempt = 1;
        private const double Phase7MaximumMeanPlanningMilliseconds = 125d;
        private const double Phase7MaximumP95PlanningMilliseconds = 200d;
        private const double Phase7MaximumSeedPlanningMilliseconds = 750d;
        private const double Phase7MaximumMeasuredLoopSeconds = 250d;
        private const string Phase7EnvironmentConfirmationVariable =
            "DUNGEON_PHASE7_ENVIRONMENT_CONFIRMED";
        private const int Phase7OutlierDiagnosticTopCount = 20;
        private static readonly string[] Phase7OutlierStageNames =
        {
            "settingsAndRandomInitialization",
            "acceptedPlanTotal",
            "routeLayout",
            "tieredLevelPlan",
            "tiered.loadReviewedStairs",
            "tierAttempt.total",
            "tierAttempt.zoneAndLevels",
            "tierAttempt.loopConnectionsAndDensity",
            "tierAttempt.connectedDeltaValidation",
            "tierAttempt.cellLevelField",
            "tierAttempt.postFieldValidation",
            "tierAttempt.planAssembly",
            "cellField.reviewedStairSearch",
            "cellField.activeSynthesis",
            "cellField.stairwellSynthesis",
            "acceptedReportTotal",
            "canonicalProjections",
            "identityHashesAndCatalog",
            "metricsAndHardValidation",
            "hardValidation.connectivity",
            "hardValidation.transitionsAndPortGraph",
            "hardValidation.routeRecipesPromontoriesHeadroom",
            "hardValidation.boundary",
            "hardValidation.rendererInputs",
            "reportAssemblyAndDiagnosticProjections",
            "rejectedReportTotal",
            "exceptionReportTotal"
        };

        private static Phase7OutlierSeedTiming phase7ActiveOutlierTiming;

        private sealed class Phase7BatchEvidence
        {
            internal readonly int sweepOrdinal;
            internal readonly int warmupSeedCount;
            internal readonly List<double> planningMilliseconds = new List<double>();
            internal double measuredLoopSeconds;

            internal Phase7BatchEvidence(int sweepOrdinal, int warmupSeedCount)
            {
                this.sweepOrdinal = sweepOrdinal;
                this.warmupSeedCount = warmupSeedCount;
            }
        }

        private sealed class Phase7OutlierSeedTiming
        {
            internal readonly int seed;
            internal readonly Dictionary<string, double> stageMilliseconds =
                new Dictionary<string, double>(StringComparer.Ordinal);
            internal double totalMilliseconds;
            internal double processCpuMilliseconds;
            internal long managedMemoryBeforeBytes;
            internal long managedMemoryAfterBytes;
            internal int generation0Collections;
            internal int generation1Collections;
            internal int generation2Collections;

            internal Phase7OutlierSeedTiming(int seed)
            {
                this.seed = seed;
            }

            internal void AddStage(string stage, double milliseconds)
            {
                stageMilliseconds.TryGetValue(stage, out double existing);
                stageMilliseconds[stage] = existing + milliseconds;
            }

            internal double StageMilliseconds(string stage)
            {
                return stageMilliseconds.TryGetValue(stage, out double value) ? value : 0d;
            }
        }

        [MenuItem("Tools/Dungeon Lab/Phase 7/Run First 2000-Seed Sweep")]
        public static void BatchValidatePhase7FirstSweep()
        {
            RunPhase7Sweep(1);
        }

        [MenuItem("Tools/Dungeon Lab/Phase 7/Run Second 2000-Seed Sweep And Compare")]
        public static void BatchValidatePhase7SecondSweep()
        {
            string firstSidecarPath = Phase7DeterminismSidecarPath(1);
            if (!File.Exists(firstSidecarPath))
            {
                throw new InvalidOperationException(
                    $"Phase 7 first-sweep determinism evidence was missing at '{firstSidecarPath}'. " +
                    "Run BatchValidatePhase7FirstSweep before the second sweep.");
            }

            RunPhase7Sweep(2);
            JObject comparison = WritePhase7SweepComparison();
            bool passed = comparison.Value<bool?>("passed") == true;
            string comparisonPath = Phase7ComparisonPath();
            if (!passed)
            {
                throw new InvalidOperationException(
                    $"Phase 7 sweep comparison failed. Inspect '{comparisonPath}'.");
            }

            Debug.Log($"Dungeon Lab Phase 7: both sweep budgets and deterministic comparison passed ({comparisonPath}).");
        }

        [MenuItem("Tools/Dungeon Lab/Phase 7/Diagnose Planning-Time Outliers")]
        public static void DiagnosePhase7PlanningOutliers()
        {
            if (phase7ActiveOutlierTiming != null)
            {
                throw new InvalidOperationException("A Phase 7 outlier diagnostic is already active.");
            }

            var environmentEvidence = new Phase7BatchEvidence(0, Phase7WarmupSeedCount);
            JObject measurementEnvironment = BuildPhase7MeasurementEnvironment(environmentEvidence);
            if (measurementEnvironment.Value<bool?>("passed") != true)
            {
                throw new InvalidOperationException(
                    "Phase 7 outlier diagnosis requires the locked measurement environment and explicit preflight confirmation.");
            }

            WarmPhase7MeasurementProcess();
            var retainedSeedReports = new JArray();
            var timings = new List<Phase7OutlierSeedTiming>(Phase7SeedCount);
            long diagnosticLoopStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
            {
                for (int i = 0; i < Phase7SeedCount; i++)
                {
                    int seed = Phase7FirstSeed + i;
                    var timing = new Phase7OutlierSeedTiming(seed)
                    {
                        managedMemoryBeforeBytes = GC.GetTotalMemory(forceFullCollection: false)
                    };
                    int generation0Before = GC.CollectionCount(0);
                    int generation1Before = GC.CollectionCount(1);
                    int generation2Before = GC.CollectionCount(2);
                    double processCpuBefore = process.TotalProcessorTime.TotalMilliseconds;
                    long totalStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    JObject seedReport;
                    phase7ActiveOutlierTiming = timing;
                    try
                    {
                        seedReport = BuildPhase0SeedReport(seed);
                    }
                    finally
                    {
                        timing.totalMilliseconds = ElapsedMilliseconds(
                            totalStart,
                            System.Diagnostics.Stopwatch.GetTimestamp());
                        phase7ActiveOutlierTiming = null;
                    }

                    timing.generation0Collections = GC.CollectionCount(0) - generation0Before;
                    timing.generation1Collections = GC.CollectionCount(1) - generation1Before;
                    timing.generation2Collections = GC.CollectionCount(2) - generation2Before;
                    timing.processCpuMilliseconds =
                        process.TotalProcessorTime.TotalMilliseconds - processCpuBefore;
                    timing.managedMemoryAfterBytes = GC.GetTotalMemory(forceFullCollection: false);
                    retainedSeedReports.Add(seedReport);
                    timings.Add(timing);
                }
            }

            double diagnosticLoopSeconds = ElapsedMilliseconds(
                diagnosticLoopStart,
                System.Diagnostics.Stopwatch.GetTimestamp()) / 1000d;
            JObject report = BuildPhase7OutlierDiagnosticReport(
                retainedSeedReports,
                timings,
                measurementEnvironment,
                diagnosticLoopSeconds);
            string reportPath = Phase7OutlierDiagnosticPath();
            Directory.CreateDirectory(BatchReportDirectory);
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Debug.Log(
                $"Dungeon Lab Phase 7: planning-time outlier diagnostic complete; " +
                $"outliers={report.Value<int>("outlierCount")}; report={reportPath}.");
        }

        private static long BeginPhase7OutlierStage()
        {
            return phase7ActiveOutlierTiming == null
                ? 0L
                : System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static void EndPhase7OutlierStage(string stage, long startTimestamp)
        {
            if (phase7ActiveOutlierTiming == null || startTimestamp == 0L)
            {
                return;
            }

            phase7ActiveOutlierTiming.AddStage(
                stage,
                ElapsedMilliseconds(startTimestamp, System.Diagnostics.Stopwatch.GetTimestamp()));
        }

        private static JObject BuildPhase7OutlierDiagnosticReport(
            JArray retainedSeedReports,
            IReadOnlyList<Phase7OutlierSeedTiming> timings,
            JObject measurementEnvironment,
            double diagnosticLoopSeconds)
        {
            var totalMilliseconds = new List<double>(timings.Count);
            var stageDistributions = new JObject();
            var records = new JArray();
            var outlierRecords = new JArray();
            int acceptedCount = 0;
            int hardValidCount = 0;
            int seedsWithAnyGcCollection = 0;
            int outliersWithAnyGcCollection = 0;
            for (int i = 0; i < timings.Count; i++)
            {
                Phase7OutlierSeedTiming timing = timings[i];
                JObject seedReport = retainedSeedReports[i] as JObject;
                JObject record = BuildPhase7OutlierTimingToken(timing, seedReport);
                records.Add(record);
                totalMilliseconds.Add(timing.totalMilliseconds);
                bool collected = timing.generation0Collections > 0 ||
                    timing.generation1Collections > 0 ||
                    timing.generation2Collections > 0;
                if (collected)
                {
                    seedsWithAnyGcCollection++;
                }

                if (timing.totalMilliseconds > Phase7MaximumSeedPlanningMilliseconds)
                {
                    outlierRecords.Add(record.DeepClone());
                    if (collected)
                    {
                        outliersWithAnyGcCollection++;
                    }
                }

                if (seedReport?.Value<bool?>("accepted") == true)
                {
                    acceptedCount++;
                    if (seedReport["validation"]?.Value<bool?>("passed") == true)
                    {
                        hardValidCount++;
                    }
                }
            }

            foreach (string stage in Phase7OutlierStageNames)
            {
                var values = new List<double>(timings.Count);
                foreach (Phase7OutlierSeedTiming timing in timings)
                {
                    values.Add(timing.StageMilliseconds(stage));
                }

                stageDistributions[stage] = BuildDoubleDistribution(values);
            }

            var ranked = new List<Phase7OutlierSeedTiming>(timings);
            ranked.Sort((left, right) =>
            {
                int byDuration = right.totalMilliseconds.CompareTo(left.totalMilliseconds);
                return byDuration != 0 ? byDuration : left.seed.CompareTo(right.seed);
            });
            var slowestRecords = new JArray();
            for (int i = 0; i < Math.Min(Phase7OutlierDiagnosticTopCount, ranked.Count); i++)
            {
                Phase7OutlierSeedTiming timing = ranked[i];
                int reportIndex = timing.seed - Phase7FirstSeed;
                slowestRecords.Add(BuildPhase7OutlierTimingToken(
                    timing,
                    retainedSeedReports[reportIndex] as JObject));
            }

            string resultHash = ComputeSha256(retainedSeedReports.ToString(Formatting.None));
            JObject determinismSignature = BuildPhase7DeterminismSignature(retainedSeedReports);
            string referencePath = Phase7DeterminismSidecarPath(1);
            JObject reference = File.Exists(referencePath)
                ? JObject.Parse(File.ReadAllText(referencePath))
                : null;
            bool resultHashMatches = reference != null && string.Equals(
                resultHash,
                reference.Value<string>("resultHash"),
                StringComparison.Ordinal);
            bool determinismDigestMatches = reference != null && string.Equals(
                determinismSignature.Value<string>("digest"),
                reference.Value<string>("digest"),
                StringComparison.Ordinal);
            bool functionalEvidenceMatches =
                acceptedCount == Phase7SeedCount &&
                hardValidCount == acceptedCount &&
                resultHashMatches &&
                determinismDigestMatches;

            return new JObject
            {
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["diagnosticOnly"] = true,
                ["acceptanceSweep"] = false,
                ["firstSeed"] = Phase7FirstSeed,
                ["lastSeed"] = Phase7LastSeed,
                ["seedCount"] = Phase7SeedCount,
                ["timingBoundary"] = "BuildPhase0SeedReport(seed)",
                ["instrumentation"] = "nested monotonic stage timings; retained seed reports reproduce acceptance-loop allocation pressure",
                ["lockedMaximumSeedPlanningMilliseconds"] = Phase7MaximumSeedPlanningMilliseconds,
                ["diagnosticLoopSeconds"] = diagnosticLoopSeconds,
                ["measurementEnvironment"] = measurementEnvironment.DeepClone(),
                ["totalMilliseconds"] = BuildDoubleDistribution(totalMilliseconds),
                ["stageMilliseconds"] = stageDistributions,
                ["outlierCount"] = outlierRecords.Count,
                ["outliers"] = outlierRecords,
                ["slowestRecords"] = slowestRecords,
                ["garbageCollection"] = new JObject
                {
                    ["counter"] = "System.GC.CollectionCount delta across each timed seed",
                    ["seedsWithAnyCollection"] = seedsWithAnyGcCollection,
                    ["outliersWithAnyCollection"] = outliersWithAnyGcCollection
                },
                ["functionalEvidence"] = new JObject
                {
                    ["accepted"] = acceptedCount,
                    ["hardValid"] = hardValidCount,
                    ["resultHash"] = resultHash,
                    ["determinismDigest"] = determinismSignature.Value<string>("digest"),
                    ["referenceSidecar"] = referencePath.Replace('\\', '/'),
                    ["referenceFound"] = reference != null,
                    ["resultHashMatchesReference"] = resultHashMatches,
                    ["determinismDigestMatchesReference"] = determinismDigestMatches,
                    ["passed"] = functionalEvidenceMatches
                },
                ["records"] = records
            };
        }

        private static JObject BuildPhase7OutlierTimingToken(
            Phase7OutlierSeedTiming timing,
            JObject seedReport)
        {
            var stages = new JObject();
            foreach (string stage in Phase7OutlierStageNames)
            {
                stages[stage] = timing.StageMilliseconds(stage);
            }

            double topLevelAccounted =
                timing.StageMilliseconds("settingsAndRandomInitialization") +
                timing.StageMilliseconds("acceptedPlanTotal") +
                timing.StageMilliseconds("acceptedReportTotal") +
                timing.StageMilliseconds("rejectedReportTotal") +
                timing.StageMilliseconds("exceptionReportTotal");
            return new JObject
            {
                ["seed"] = timing.seed,
                ["patternId"] = SelectedRoutePatternId(timing.seed),
                ["accepted"] = seedReport?.Value<bool?>("accepted") == true,
                ["hardValid"] = seedReport?["validation"]?.Value<bool?>("passed") == true,
                ["layoutAttempts"] = seedReport?.Value<int?>("layoutAttempts") ?? 0,
                ["canonicalHash"] = seedReport?["hashes"]?.Value<string>("canonical") ?? string.Empty,
                ["totalMs"] = timing.totalMilliseconds,
                ["processCpuMs"] = timing.processCpuMilliseconds,
                ["wallMinusProcessCpuMs"] = timing.totalMilliseconds - timing.processCpuMilliseconds,
                ["topLevelUnattributedMs"] = timing.totalMilliseconds - topLevelAccounted,
                ["managedMemoryBeforeBytes"] = timing.managedMemoryBeforeBytes,
                ["managedMemoryAfterBytes"] = timing.managedMemoryAfterBytes,
                ["managedMemoryDeltaBytes"] = timing.managedMemoryAfterBytes - timing.managedMemoryBeforeBytes,
                ["generation0Collections"] = timing.generation0Collections,
                ["generation1Collections"] = timing.generation1Collections,
                ["generation2Collections"] = timing.generation2Collections,
                ["anyGcCollection"] = timing.generation0Collections > 0 ||
                    timing.generation1Collections > 0 ||
                    timing.generation2Collections > 0,
                ["stages"] = stages
            };
        }

        private static void RunPhase7Sweep(int sweepOrdinal)
        {
            if (sweepOrdinal != 1 && sweepOrdinal != 2)
            {
                throw new ArgumentOutOfRangeException(nameof(sweepOrdinal));
            }

            WarmPhase7MeasurementProcess();
            string reportPath = RunBatchValidation(
                Phase7FirstSeed,
                Phase7SeedCount,
                sweepOrdinal);
            Debug.Log($"Dungeon Lab Phase 7: sweep {sweepOrdinal} complete ({reportPath}).");
        }

        private static void WarmPhase7MeasurementProcess()
        {
            for (int i = 0; i < Phase7WarmupSeedCount; i++)
            {
                int seed = Phase0BaselineFirstSeed + i;
                JObject report = BuildPhase0SeedReport(seed);
                if (report.Value<bool?>("accepted") != true ||
                    report["validation"]?.Value<bool?>("passed") != true)
                {
                    throw new InvalidOperationException(
                        $"Phase 7 warm-up seed {seed} did not reproduce its accepted hard-valid baseline.");
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static double ElapsedMilliseconds(long startTimestamp, long endTimestamp)
        {
            return (endTimestamp - startTimestamp) * 1000d /
                System.Diagnostics.Stopwatch.Frequency;
        }

        private static JObject BuildDoubleDistribution(IReadOnlyList<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return new JObject
                {
                    ["sampleCount"] = 0,
                    ["minMs"] = 0d,
                    ["p50Ms"] = 0d,
                    ["p95Ms"] = 0d,
                    ["maxMs"] = 0d,
                    ["meanMs"] = 0d,
                    ["totalMs"] = 0d
                };
            }

            var sorted = new List<double>(values);
            sorted.Sort();
            double total = 0d;
            foreach (double value in sorted)
            {
                total += value;
            }

            return new JObject
            {
                ["sampleCount"] = sorted.Count,
                ["minMs"] = sorted[0],
                ["p50Ms"] = NearestRank(sorted, 0.50d),
                ["p95Ms"] = NearestRank(sorted, 0.95d),
                ["maxMs"] = sorted[sorted.Count - 1],
                ["meanMs"] = total / sorted.Count,
                ["totalMs"] = total
            };
        }

        private static double NearestRank(IReadOnlyList<double> sortedValues, double percentile)
        {
            int index = Math.Max(
                0,
                Math.Min(
                    sortedValues.Count - 1,
                    (int)Math.Ceiling(percentile * sortedValues.Count) - 1));
            return sortedValues[index];
        }

        private static void AppendPhase7SweepEvidence(
            JObject report,
            JArray seedReports,
            IReadOnlyDictionary<string, int> selectedPatternCounts,
            IReadOnlyDictionary<string, int> acceptedPatternCounts,
            JObject attemptDistribution,
            int successCount,
            int hardValidCount,
            Phase7BatchEvidence evidence)
        {
            JObject planningDistribution = BuildDoubleDistribution(evidence.planningMilliseconds);
            JObject patternAttempts = BuildPhase7PatternAttemptDistributions(seedReports);
            JObject measurementEnvironment = BuildPhase7MeasurementEnvironment(evidence);
            JObject determinismSignature = BuildPhase7DeterminismSignature(seedReports);

            int processionalSelected = CountFor(selectedPatternCounts, Phase1PatternId);
            int atriumSelected = CountFor(selectedPatternCounts, AtriumRingPatternId);
            int twinWingSelected = CountFor(selectedPatternCounts, TwinWingPatternId);
            int processionalAccepted = CountFor(acceptedPatternCounts, Phase1PatternId);
            int atriumAccepted = CountFor(acceptedPatternCounts, AtriumRingPatternId);
            int twinWingAccepted = CountFor(acceptedPatternCounts, TwinWingPatternId);

            bool exactCorpus = report.Value<int>("firstSeed") == Phase7FirstSeed &&
                report.Value<int>("lastSeed") == Phase7LastSeed &&
                report.Value<int>("seedCount") == Phase7SeedCount;
            bool exactPatternSplit =
                processionalSelected == Phase7RequiredProcessionalSelected &&
                atriumSelected == Phase7RequiredAtriumSelected &&
                twinWingSelected == Phase7RequiredTwinWingSelected;
            bool overallReliabilityPassed = successCount >= Phase7RequiredAccepted;
            bool patternReliabilityPassed =
                processionalAccepted >= Phase7RequiredProcessionalAccepted &&
                atriumAccepted >= Phase7RequiredAtriumAccepted &&
                twinWingAccepted >= Phase7RequiredTwinWingAccepted;
            bool everyAcceptedPlanHardValid = hardValidCount == successCount;
            bool failuresReasonCoded = FailuresAreReasonCoded(seedReports);
            bool attemptBudgetPassed =
                attemptDistribution.Value<int>("p95") <= Phase7RequiredP95Attempt &&
                attemptDistribution.Value<int>("max") <= Phase1LayoutAttemptLimit &&
                Phase7PatternAttemptsPass(patternAttempts);
            bool performanceBudgetPassed =
                planningDistribution.Value<double>("meanMs") <= Phase7MaximumMeanPlanningMilliseconds &&
                planningDistribution.Value<double>("p95Ms") <= Phase7MaximumP95PlanningMilliseconds &&
                planningDistribution.Value<double>("maxMs") <= Phase7MaximumSeedPlanningMilliseconds &&
                evidence.measuredLoopSeconds <= Phase7MaximumMeasuredLoopSeconds;
            bool environmentPassed = measurementEnvironment.Value<bool?>("passed") == true;
            bool passed = exactCorpus &&
                exactPatternSplit &&
                overallReliabilityPassed &&
                patternReliabilityPassed &&
                everyAcceptedPlanHardValid &&
                failuresReasonCoded &&
                attemptBudgetPassed &&
                performanceBudgetPassed &&
                environmentPassed;

            report["phase7SweepOrdinal"] = evidence.sweepOrdinal;
            report["phase7PlanningPerformance"] = new JObject
            {
                ["timingBoundary"] = "BuildPhase0SeedReport(seed)",
                ["clock"] = "System.Diagnostics.Stopwatch monotonic timestamp",
                ["perSeedMilliseconds"] = planningDistribution,
                ["measuredLoopSeconds"] = evidence.measuredLoopSeconds
            };
            report["phase7MeasurementEnvironment"] = measurementEnvironment;
            report["phase7PatternAttemptDistributions"] = patternAttempts;
            report["phase7DeterminismDigest"] = determinismSignature.Value<string>("digest");
            report["phase7ReliabilityBudget"] = BuildPhase7BudgetToken();
            report["phase7BudgetResult"] = new JObject
            {
                ["passed"] = passed,
                ["exactCorpus"] = exactCorpus,
                ["exactPatternSplit"] = exactPatternSplit,
                ["accepted"] = successCount,
                ["requiredAccepted"] = Phase7RequiredAccepted,
                ["overallReliabilityPassed"] = overallReliabilityPassed,
                ["processionalAccepted"] = processionalAccepted,
                ["atriumRingAccepted"] = atriumAccepted,
                ["twinWingAccepted"] = twinWingAccepted,
                ["patternReliabilityPassed"] = patternReliabilityPassed,
                ["hardValid"] = hardValidCount,
                ["everyAcceptedPlanHardValid"] = everyAcceptedPlanHardValid,
                ["everyFailureReasonCoded"] = failuresReasonCoded,
                ["attemptBudgetPassed"] = attemptBudgetPassed,
                ["performanceBudgetPassed"] = performanceBudgetPassed,
                ["measurementEnvironmentPassed"] = environmentPassed,
                ["determinismComparisonPending"] = evidence.sweepOrdinal == 1
            };
        }

        private static JObject BuildPhase7BudgetToken()
        {
            return new JObject
            {
                ["corpus"] = $"{Phase7FirstSeed}..{Phase7LastSeed}",
                ["seedCount"] = Phase7SeedCount,
                ["requiredAccepted"] = Phase7RequiredAccepted,
                ["requiredProcessionalSelected"] = Phase7RequiredProcessionalSelected,
                ["requiredAtriumRingSelected"] = Phase7RequiredAtriumSelected,
                ["requiredTwinWingSelected"] = Phase7RequiredTwinWingSelected,
                ["requiredProcessionalAccepted"] = Phase7RequiredProcessionalAccepted,
                ["requiredAtriumRingAccepted"] = Phase7RequiredAtriumAccepted,
                ["requiredTwinWingAccepted"] = Phase7RequiredTwinWingAccepted,
                ["requiredP95Attempt"] = Phase7RequiredP95Attempt,
                ["requiredMaximumAttempt"] = Phase1LayoutAttemptLimit,
                ["maximumMeanPlanningMilliseconds"] = Phase7MaximumMeanPlanningMilliseconds,
                ["maximumP95PlanningMilliseconds"] = Phase7MaximumP95PlanningMilliseconds,
                ["maximumSeedPlanningMilliseconds"] = Phase7MaximumSeedPlanningMilliseconds,
                ["maximumMeasuredLoopSeconds"] = Phase7MaximumMeasuredLoopSeconds,
                ["warmupSeedCount"] = Phase7WarmupSeedCount,
                ["requiredIndependentSweeps"] = 2
            };
        }

        private static JObject BuildPhase7PatternAttemptDistributions(JArray seedReports)
        {
            var byPattern = new Dictionary<string, List<int>>(StringComparer.Ordinal)
            {
                [Phase1PatternId] = new List<int>(),
                [AtriumRingPatternId] = new List<int>(),
                [TwinWingPatternId] = new List<int>()
            };
            foreach (JToken seedReport in seedReports)
            {
                int seed = seedReport.Value<int>("seed");
                string patternId = SelectedRoutePatternId(seed);
                byPattern[patternId].Add(seedReport.Value<int?>("layoutAttempts") ?? 0);
            }

            return new JObject
            {
                [Phase1PatternId] = BuildIntDistribution(byPattern[Phase1PatternId]),
                [AtriumRingPatternId] = BuildIntDistribution(byPattern[AtriumRingPatternId]),
                [TwinWingPatternId] = BuildIntDistribution(byPattern[TwinWingPatternId])
            };
        }

        private static bool Phase7PatternAttemptsPass(JObject patternAttempts)
        {
            foreach (string patternId in new[] { Phase1PatternId, AtriumRingPatternId, TwinWingPatternId })
            {
                JObject distribution = patternAttempts[patternId] as JObject;
                if (distribution == null ||
                    distribution.Value<int>("p95") > Phase7RequiredP95Attempt ||
                    distribution.Value<int>("max") > Phase1LayoutAttemptLimit)
                {
                    return false;
                }
            }

            return true;
        }

        private static int CountFor(IReadOnlyDictionary<string, int> counts, string key)
        {
            return counts != null && counts.TryGetValue(key, out int value) ? value : 0;
        }

        private static JObject BuildPhase7MeasurementEnvironment(Phase7BatchEvidence evidence)
        {
            string operatingSystem = SystemInfo.operatingSystem ?? string.Empty;
            string processorType = SystemInfo.processorType ?? string.Empty;
            string graphicsDeviceName = SystemInfo.graphicsDeviceName ?? string.Empty;
            bool operatorPreflightConfirmed = string.Equals(
                Environment.GetEnvironmentVariable(Phase7EnvironmentConfirmationVariable),
                "1",
                StringComparison.Ordinal);
            bool machineMatches =
                string.Equals(Application.unityVersion, "6000.4.0f1", StringComparison.Ordinal) &&
                Application.isBatchMode &&
                operatingSystem.IndexOf("14.3", StringComparison.OrdinalIgnoreCase) >= 0 &&
                processorType.IndexOf("Apple M1 Pro", StringComparison.OrdinalIgnoreCase) >= 0 &&
                SystemInfo.processorCount == 8 &&
                SystemInfo.systemMemorySize >= 16000 &&
                SystemInfo.systemMemorySize < 17000 &&
                graphicsDeviceName.IndexOf("Apple M1 Pro", StringComparison.OrdinalIgnoreCase) >= 0;
            return new JObject
            {
                ["passed"] = machineMatches && operatorPreflightConfirmed,
                ["machineMatches"] = machineMatches,
                ["operatorPreflightConfirmed"] = operatorPreflightConfirmed,
                ["confirmationVariable"] = Phase7EnvironmentConfirmationVariable,
                ["unityVersion"] = Application.unityVersion,
                ["batchMode"] = Application.isBatchMode,
                ["operatingSystem"] = operatingSystem,
                ["processorType"] = processorType,
                ["processorCount"] = SystemInfo.processorCount,
                ["graphicsDeviceName"] = graphicsDeviceName,
                ["systemMemoryMb"] = SystemInfo.systemMemorySize,
                ["warmupFirstSeed"] = Phase0BaselineFirstSeed,
                ["warmupSeedCount"] = evidence.warmupSeedCount
            };
        }

        private static JObject BuildPhase7DeterminismSignature(JArray seedReports)
        {
            var records = new JArray();
            foreach (JToken seedReport in seedReports)
            {
                int seed = seedReport.Value<int>("seed");
                bool accepted = seedReport.Value<bool?>("accepted") == true;
                JObject hashes = seedReport["hashes"] as JObject;
                records.Add(new JObject
                {
                    ["seed"] = seed,
                    ["patternId"] = SelectedRoutePatternId(seed),
                    ["accepted"] = accepted,
                    ["layoutAttempts"] = seedReport.Value<int?>("layoutAttempts") ?? 0,
                    ["rejectionCode"] = accepted
                        ? string.Empty
                        : seedReport.Value<string>("lastRejectionCode") ?? string.Empty,
                    ["routeIntentHash"] = hashes?.Value<string>("routeIntent") ?? string.Empty,
                    ["layoutHash"] = hashes?.Value<string>("layout") ?? string.Empty,
                    ["tieredLevelPlanHash"] = hashes?.Value<string>("tieredLevelPlan") ?? string.Empty,
                    ["recipeResolutionsHash"] = hashes?.Value<string>("recipeResolutions") ?? string.Empty,
                    ["recipeCatalogHash"] = hashes?.Value<string>("recipeCatalog") ?? string.Empty,
                    ["canonicalHash"] = hashes?.Value<string>("canonical") ?? string.Empty
                });
            }

            return new JObject
            {
                ["algorithm"] = "SHA-256 over ordered compact determinism records",
                ["recordCount"] = records.Count,
                ["digest"] = ComputeSha256(records.ToString(Formatting.None)),
                ["records"] = records
            };
        }

        private static void WritePhase7DeterminismSidecar(
            JObject report,
            JArray seedReports,
            Phase7BatchEvidence evidence)
        {
            JObject signature = BuildPhase7DeterminismSignature(seedReports);
            signature["summaryVersion"] = ActiveDiagnosticSummaryVersion;
            signature["generatorVersion"] = ActiveDiagnosticGeneratorVersion;
            signature["sweepOrdinal"] = evidence.sweepOrdinal;
            signature["firstSeed"] = Phase7FirstSeed;
            signature["lastSeed"] = Phase7LastSeed;
            signature["resultHash"] = report.Value<string>("resultHash");
            signature["individualBudgetPassed"] = report["phase7BudgetResult"]?.Value<bool?>("passed") == true;
            signature["planningPerformance"] = report["phase7PlanningPerformance"]?.DeepClone();
            signature["measurementEnvironment"] = report["phase7MeasurementEnvironment"]?.DeepClone();
            string sidecarPath = Phase7DeterminismSidecarPath(evidence.sweepOrdinal);
            Directory.CreateDirectory(BatchReportDirectory);
            File.WriteAllText(sidecarPath, signature.ToString(Formatting.Indented));
            report["phase7DeterminismSidecar"] = sidecarPath.Replace('\\', '/');
        }

        private static JObject WritePhase7SweepComparison()
        {
            string firstPath = Phase7DeterminismSidecarPath(1);
            string secondPath = Phase7DeterminismSidecarPath(2);
            JObject first = JObject.Parse(File.ReadAllText(firstPath));
            JObject second = JObject.Parse(File.ReadAllText(secondPath));
            JArray firstRecords = first["records"] as JArray ?? new JArray();
            JArray secondRecords = second["records"] as JArray ?? new JArray();
            bool recordsIdentical = JToken.DeepEquals(firstRecords, secondRecords);
            int firstMismatchIndex = -1;
            if (!recordsIdentical)
            {
                int count = Math.Min(firstRecords.Count, secondRecords.Count);
                for (int i = 0; i < count; i++)
                {
                    if (!JToken.DeepEquals(firstRecords[i], secondRecords[i]))
                    {
                        firstMismatchIndex = i;
                        break;
                    }
                }

                if (firstMismatchIndex < 0 && firstRecords.Count != secondRecords.Count)
                {
                    firstMismatchIndex = count;
                }
            }

            bool digestsIdentical = string.Equals(
                first.Value<string>("digest"),
                second.Value<string>("digest"),
                StringComparison.Ordinal);
            bool resultHashesIdentical = string.Equals(
                first.Value<string>("resultHash"),
                second.Value<string>("resultHash"),
                StringComparison.Ordinal);
            bool individualBudgetsPassed =
                first.Value<bool?>("individualBudgetPassed") == true &&
                second.Value<bool?>("individualBudgetPassed") == true;
            bool passed = individualBudgetsPassed &&
                recordsIdentical &&
                digestsIdentical &&
                resultHashesIdentical;
            var comparison = new JObject
            {
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["firstSeed"] = Phase7FirstSeed,
                ["lastSeed"] = Phase7LastSeed,
                ["seedCount"] = Phase7SeedCount,
                ["passed"] = passed,
                ["individualBudgetsPassed"] = individualBudgetsPassed,
                ["perSeedRecordsIdentical"] = recordsIdentical,
                ["determinismDigestsIdentical"] = digestsIdentical,
                ["resultHashesIdentical"] = resultHashesIdentical,
                ["firstMismatchIndex"] = firstMismatchIndex,
                ["firstMismatchSeed"] = firstMismatchIndex >= 0 && firstMismatchIndex < firstRecords.Count
                    ? firstRecords[firstMismatchIndex]?.Value<int?>("seed")
                    : null,
                ["firstSweep"] = SweepComparisonToken(firstPath, first),
                ["secondSweep"] = SweepComparisonToken(secondPath, second),
                ["lockedBudget"] = BuildPhase7BudgetToken()
            };
            Directory.CreateDirectory(BatchReportDirectory);
            File.WriteAllText(Phase7ComparisonPath(), comparison.ToString(Formatting.Indented));
            return comparison;
        }

        private static JObject SweepComparisonToken(string path, JObject sidecar)
        {
            return new JObject
            {
                ["path"] = path.Replace('\\', '/'),
                ["individualBudgetPassed"] = sidecar.Value<bool?>("individualBudgetPassed") == true,
                ["digest"] = sidecar.Value<string>("digest"),
                ["resultHash"] = sidecar.Value<string>("resultHash"),
                ["planningPerformance"] = sidecar["planningPerformance"]?.DeepClone(),
                ["measurementEnvironment"] = sidecar["measurementEnvironment"]?.DeepClone()
            };
        }

        private static string Phase7DeterminismSidecarPath(int sweepOrdinal)
        {
            return Path.Combine(
                BatchReportDirectory,
                $"phase7_determinism_{Phase7FirstSeed}_{Phase7LastSeed}_run{sweepOrdinal}.json");
        }

        private static string Phase7ComparisonPath()
        {
            return Path.Combine(
                BatchReportDirectory,
                $"phase7_sweep_comparison_{Phase7FirstSeed}_{Phase7LastSeed}.json");
        }

        private static string Phase7OutlierDiagnosticPath()
        {
            return Path.Combine(
                BatchReportDirectory,
                $"phase7_planning_outlier_diagnostic_{Phase7FirstSeed}_{Phase7LastSeed}.json");
        }

        // Reflection entry point for the focused Phase 7 support fixture. It
        // exercises only constants and pure diagnostic projection helpers.
        private static string BuildPhase7SweepSupportSnapshot()
        {
            JObject firstRecord = SyntheticPhase7SeedRecord(Phase7FirstSeed, 1);
            JObject secondRecord = (JObject)firstRecord.DeepClone();
            JArray firstRecords = new JArray(firstRecord);
            JArray secondRecords = new JArray(secondRecord);
            string firstDigest = BuildPhase7DeterminismSignature(firstRecords).Value<string>("digest");
            string secondDigest = BuildPhase7DeterminismSignature(secondRecords).Value<string>("digest");
            secondRecord["layoutAttempts"] = 2;
            string changedDigest = BuildPhase7DeterminismSignature(secondRecords).Value<string>("digest");
            JObject distribution = BuildDoubleDistribution(new[]
            {
                10d, 20d, 30d, 40d, 50d, 60d, 70d, 80d, 90d, 100d,
                110d, 120d, 130d, 140d, 150d, 160d, 170d, 180d, 190d, 200d
            });
            return string.Join("\n", new[]
            {
                $"range.first={Phase7FirstSeed}",
                $"range.last={Phase7LastSeed}",
                $"range.count={Phase7SeedCount}",
                $"reliability.overall={Phase7RequiredAccepted}",
                $"reliability.processional={Phase7RequiredProcessionalAccepted}",
                $"reliability.atrium={Phase7RequiredAtriumAccepted}",
                $"reliability.twinWing={Phase7RequiredTwinWingAccepted}",
                $"attempt.p95={Phase7RequiredP95Attempt}",
                $"attempt.max={Phase1LayoutAttemptLimit}",
                $"performance.meanMs={Phase7MaximumMeanPlanningMilliseconds}",
                $"performance.p95Ms={Phase7MaximumP95PlanningMilliseconds}",
                $"performance.maxMs={Phase7MaximumSeedPlanningMilliseconds}",
                $"performance.loopSeconds={Phase7MaximumMeasuredLoopSeconds}",
                $"warmup.count={Phase7WarmupSeedCount}",
                $"distribution.p50={distribution.Value<double>("p50Ms")}",
                $"distribution.p95={distribution.Value<double>("p95Ms")}",
                $"distribution.max={distribution.Value<double>("maxMs")}",
                $"determinism.identical={string.Equals(firstDigest, secondDigest, StringComparison.Ordinal)}",
                $"determinism.changeDetected={!string.Equals(firstDigest, changedDigest, StringComparison.Ordinal)}",
                $"paths.distinct={!string.Equals(Phase7DeterminismSidecarPath(1), Phase7DeterminismSidecarPath(2), StringComparison.Ordinal)}",
                $"outlierDiagnostic.acceptanceSweep=False",
                $"outlierDiagnostic.stageCount={Phase7OutlierStageNames.Length}",
                $"outlierDiagnostic.maximumMs={Phase7MaximumSeedPlanningMilliseconds}",
                $"versions.summary={DungeonPlanSummaryVersion}",
                $"versions.generator={RoutePlannerVersion}"
            });
        }

        private static JObject SyntheticPhase7SeedRecord(int seed, int layoutAttempts)
        {
            return new JObject
            {
                ["seed"] = seed,
                ["accepted"] = true,
                ["layoutAttempts"] = layoutAttempts,
                ["hashes"] = new JObject
                {
                    ["routeIntent"] = "route",
                    ["layout"] = "layout",
                    ["tieredLevelPlan"] = "tier",
                    ["recipeResolutions"] = "recipes",
                    ["recipeCatalog"] = "catalog",
                    ["canonical"] = "canonical"
                }
            };
        }

        // Reflection entry point for the focused output-preserving cache contract.
        private static string BuildPhase7TierRetryOptimizationSnapshot()
        {
            List<StairForge.SynthesizedStaircaseDesign> activeDesigns =
                StairForge.EnumerateSynthesisDesigns(MajorRiseLevels, out _);
            PreparedSynthesizedStairCatalog activeFirst =
                PrepareSynthesizedStairCatalog(activeDesigns, "synthesized");
            PreparedSynthesizedStairCatalog activeSecond =
                PrepareSynthesizedStairCatalog(activeDesigns, "synthesized");
            List<StairForge.SynthesizedStaircaseDesign> stairwellDesigns =
                StairForge.EnumerateStairwellSynthesisDesigns(MajorRiseLevels, out _);
            PreparedSynthesizedStairCatalog stairwellFirst =
                PrepareSynthesizedStairCatalog(stairwellDesigns, "stairwell");
            PreparedSynthesizedStairCatalog stairwellSecond =
                PrepareSynthesizedStairCatalog(stairwellDesigns, "stairwell");
            return string.Join("\n", new[]
            {
                $"active.designs={activeDesigns.Count}",
                $"active.options={activeFirst.options.Count}",
                $"active.reused={ReferenceEquals(activeFirst, activeSecond)}",
                $"stairwell.designs={stairwellDesigns.Count}",
                $"stairwell.options={stairwellFirst.options.Count}",
                $"stairwell.reused={ReferenceEquals(stairwellFirst, stairwellSecond)}"
            });
        }

        private static string BuildPhase7TierRetryPreservationSnapshot()
        {
            const int outlierSeed = 2026072486;
            JObject report = BuildPhase0SeedReport(outlierSeed);
            return string.Join("\n", new[]
            {
                $"seed={outlierSeed}",
                $"accepted={report.Value<bool?>("accepted") == true}",
                $"hardValid={report["validation"]?.Value<bool?>("passed") == true}",
                $"layoutAttempts={report.Value<int?>("layoutAttempts") ?? 0}",
                $"stairPlacementRejections={report["rejectionCodes"]?.Value<int?>("STAIR_PLACEMENT") ?? 0}",
                $"canonicalHash={report["hashes"]?.Value<string>("canonical") ?? string.Empty}"
            });
        }
    }
}

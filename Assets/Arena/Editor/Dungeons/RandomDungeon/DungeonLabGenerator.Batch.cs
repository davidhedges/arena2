using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // Headless batch validation: builds tiered plans (no scene geometry) for many seeds and
    // reports success rate, archetype mix, tier spreads, and the depth/level correlation that
    // measures how "same" the elevation grammar is across dungeons.
    internal sealed partial class DungeonLabGenerator
    {
        private const string BatchReportDirectory = "DungeonLabReports";

        [MenuItem("Tools/Dungeon Lab/Batch Validate (50 Seeds)")]
        public static void BatchValidate50Seeds()
        {
            RunBatchValidation(50);
        }

        [MenuItem("Tools/Dungeon Lab/Batch Validate (200 Seeds)")]
        public static void BatchValidate200Seeds()
        {
            RunBatchValidation(200);
        }

        private static void RunBatchValidation(int seedCount)
        {
            CurrentGenerationSettings = LoadActiveGenerationSettings();
            int baseSeed = CreateRandomSeed();
            var rejectionHistogram = new Dictionary<string, int>();
            var archetypeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var tierSpanCounts = new SortedDictionary<int, int>();
            var correlations = new List<float>();
            var failedSeeds = new List<int>();
            var seedReports = new JArray();
            int successCount = 0;
            int totalLayoutAttempts = 0;

            try
            {
                for (int i = 0; i < seedCount; i++)
                {
                    int seed = baseSeed + i;
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Dungeon Lab Batch Validate",
                            $"Seed {seed} ({i + 1}/{seedCount})",
                            (float)i / seedCount))
                    {
                        seedCount = i;
                        break;
                    }

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
                        failedSeeds.Add(seed);
                        totalLayoutAttempts += layoutAttemptsUsed;
                        seedReports.Add(new JObject
                        {
                            ["seed"] = seed,
                            ["accepted"] = false,
                            ["lastRejection"] = rejectionReason
                        });
                        continue;
                    }

                    successCount++;
                    totalLayoutAttempts += layoutAttemptsUsed;
                    archetypeCounts.TryGetValue(plan.archetypeName, out int archetypeCount);
                    archetypeCounts[plan.archetypeName] = archetypeCount + 1;
                    int tierSpan = plan.maxLevel - plan.minLevel;
                    tierSpanCounts.TryGetValue(tierSpan, out int spanCount);
                    tierSpanCounts[tierSpan] = spanCount + 1;
                    float correlation = CalculateDepthLevelCorrelation(layout, plan);
                    if (!float.IsNaN(correlation))
                    {
                        correlations.Add(correlation);
                    }

                    seedReports.Add(new JObject
                    {
                        ["seed"] = seed,
                        ["accepted"] = true,
                        ["archetype"] = plan.archetypeName,
                        ["layoutAttempts"] = layoutAttemptsUsed,
                        ["rooms"] = layout.rooms.Count,
                        ["floorCells"] = layout.floorCells.Count,
                        ["floorFillPercent"] = CalculateFloorFillPercent(layout.floorCells) * 100f,
                        ["loopEdges"] = CountLoopEdges(layout),
                        ["minLevel"] = plan.minLevel,
                        ["maxLevel"] = plan.maxLevel,
                        ["levelCount"] = plan.levelCount,
                        ["roomsPerTier"] = plan.roomsPerTierSummary,
                        ["overlooks"] = plan.overlookCount,
                        ["depthLevelCorrelation"] = correlation,
                        ["synthesizedStairs"] = plan.synthesizedStairs == null ? 0 : plan.synthesizedStairs.Count,
                        ["synthesizedStairUsage"] = plan.synthesizedStairSummary
                    });
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (seedCount <= 0)
            {
                Debug.Log("Dungeon Lab: batch validation cancelled before any seeds ran.");
                return;
            }

            string archetypeSummary = FormatCountSummary(archetypeCounts);
            string tierSpanSummary = FormatTierSpanSummary(tierSpanCounts);
            string correlationSummary = FormatCorrelationSummary(correlations);
            string failedSummary = failedSeeds.Count == 0
                ? "none"
                : string.Join(", ", failedSeeds);
            Debug.Log(
                "Dungeon Lab BATCH_VALIDATION " +
                $"seeds={seedCount}; accepted={successCount}; failed={failedSeeds.Count}; " +
                $"meanLayoutAttempts={(float)totalLayoutAttempts / seedCount:0.##}; " +
                $"archetypes={archetypeSummary}; tierSpans={tierSpanSummary}; " +
                $"depthLevelCorrelation={correlationSummary}; " +
                $"failedSeeds={failedSummary}; " +
                $"rejections={FormatRejectionHistogram(rejectionHistogram)}");

            string reportPath = WriteBatchReport(
                baseSeed,
                seedCount,
                successCount,
                rejectionHistogram,
                archetypeCounts,
                correlations,
                seedReports);
            Debug.Log($"Dungeon Lab: batch validation report written to {reportPath}");
        }

        // Pearson correlation between a room's BFS depth from the hall and its assigned tier.
        // The pre-archetype generator scored ~+1 on every seed; a healthy archetype mix should
        // spread this across negative and positive values.
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

        private static string WriteBatchReport(
            int baseSeed,
            int seedCount,
            int successCount,
            Dictionary<string, int> rejectionHistogram,
            SortedDictionary<string, int> archetypeCounts,
            List<float> correlations,
            JArray seedReports)
        {
            var rejections = new JObject();
            foreach (KeyValuePair<string, int> entry in rejectionHistogram)
            {
                rejections[entry.Key] = entry.Value;
            }

            var archetypes = new JObject();
            foreach (KeyValuePair<string, int> entry in archetypeCounts)
            {
                archetypes[entry.Key] = entry.Value;
            }

            var report = new JObject
            {
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["baseSeed"] = baseSeed,
                ["seedCount"] = seedCount,
                ["accepted"] = successCount,
                ["failed"] = seedCount - successCount,
                ["archetypes"] = archetypes,
                ["depthLevelCorrelation"] = FormatCorrelationSummary(correlations),
                ["rejectionHistogram"] = rejections,
                ["seeds"] = seedReports
            };

            Directory.CreateDirectory(BatchReportDirectory);
            string reportPath = Path.Combine(
                BatchReportDirectory,
                $"batch_validation_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(reportPath, report.ToString());
            return reportPath;
        }
    }
}

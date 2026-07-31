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
        private const string DungeonPlanSummaryVersion = "dungeon-plan-v11";
        private const string ThroneRecipeFixtureId = "episode_throne_twin_stairs_01";
        private const string VestibuleRecipeFixtureId = "connector_flexible_vestibule_01";
        private const string CornerReturnRecipeFixtureId = "connector_corner_return_01";
        private const string ExampleRecipeFixtureId = "connector_example_01";
        private const string UnknownDisabledPreviewFixtureId =
            "preview_disabled_connector_slice_c_01";
        private const int BaselineFirstSeed = 2026072100;
        private const int BaselineSeedCount = 200;
        private const int LockedSeedCount = 100;
        private const int SentinelImageWidth = 1600;
        private const int SentinelImageHeight = 900;
        // The three-quarter view reads elevation; it cannot read void, because a
        // near tier hides the hole behind it. The density work is judged on
        // holes, so every sentinel also gets a square orthographic plan view.
        private const int SentinelTopDownImageSize = 1200;

        // Six and only six visual sentinels. Their lightweight annotations are
        // characterization notes, not an aesthetic taxonomy or acceptance gate.
        private static readonly (int seed, string category, string annotation)[] VisualSentinels =
        {
            (2026072140, "representative-a", "Representative selection retained for cross-run visual comparison."),
            (2026072186, "representative-b", "Alternate representative selection retained for cross-run visual comparison."),
            (2026072169, "weak-a", "Phase 0 weak selection retained to expose cross-phase readability regressions."),
            (2026072245, "weak-b", "Phase 0 second weak selection retained to expose cross-phase readability regressions."),
            (2026072262, "edge-a", "Phase 0 transition-count edge selection retained for cross-phase comparison."),
            (2026072223, "edge-b", "Phase 0 elevation-span edge selection retained for cross-phase comparison.")
        };

        private static readonly Dictionary<string, string> ActiveContentDigestCache =
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
                ["densityLevel"] = settings.densityLevel,
                ["digestAlgorithm"] = "SHA-256 over settings.values with ordinal field ordering",
                ["digest"] = ComputeSha256(values.ToString(Formatting.None)),
                ["values"] = values
            };
        }

        private static void AddGenerationSettingsIdentity(JObject report)
        {
            JObject identity = BuildGenerationSettingsIdentity(CurrentGenerationSettings);
            report["profile"] = identity.Value<string>("profileId");
            report["density"] = identity.Value<int>("densityLevel");
            report["settingsDigest"] = identity.Value<string>("digest");
            report["settings"] = identity;
        }

        [MenuItem("Tools/Dungeon Lab/Batch Validate (50 Fixed Seeds)")]
        public static void BatchValidate50Seeds()
        {
            RunBatchValidation(BaselineFirstSeed, 50);
        }

        [MenuItem("Tools/Dungeon Lab/Batch Validate (200 Fixed Seeds)")]
        public static void BatchValidate200Seeds()
        {
            RunBatchValidation(BaselineFirstSeed, BaselineSeedCount);
        }

        [MenuItem("Tools/Dungeon Lab/Batch Validate (100 Locked Seeds)")]
        public static void BatchValidateLocked100Seeds()
        {
            RunBatchValidation(BaselineFirstSeed, LockedSeedCount);
        }

        [MenuItem("Tools/Dungeon Lab/Capture Visual Sentinels")]
        public static void CaptureVisualSentinels()
        {
            CaptureVisualSentinels("visual_sentinels");
        }

        /// <summary>
        /// Renders a seed range and reports which seeds the RENDERER refuses.
        /// </summary>
        /// <remarks>
        /// Batch Validate answers a question about the PLAN — it never builds a
        /// GameObject, so a plan that is hard-valid and a plan that renders are
        /// two different claims and only the first was ever measured over the
        /// corpus. Phase 4 of the density work found the gap the hard way: at
        /// densities 4 and 5 a sentinel seed threw `STAIR_BOUNDARY_CONFLICT`
        /// from a tier corner overlapping a stairwell footprint while every one
        /// of the 200 plans passed validation. This is slow — a full scene build
        /// per seed — so it is a separate sweep rather than part of the batch.
        /// </remarks>
        [MenuItem("Tools/Dungeon Lab/Render Sweep (50 Fixed Seeds)")]
        public static void RenderSweep50Seeds()
        {
            RunRenderSweep(BaselineFirstSeed, 50);
        }

        [MenuItem("Tools/Dungeon Lab/Render Sweep (200 Fixed Seeds)")]
        public static void RenderSweep200Seeds()
        {
            RunRenderSweep(BaselineFirstSeed, BaselineSeedCount);
        }

        /// <summary>
        /// What one density level costs: scene objects, colliders, traps, bytes.
        /// </summary>
        /// <remarks>
        /// Design §9 residual risk 3 says the packed end buys ~1.5x floor cells
        /// and a larger increase in WALL segments — every packed seam is a
        /// partition or retaining wall where it used to be a cliff face — and
        /// that the cost gets measured in phase 6 rather than assumed. This is
        /// that measurement.
        /// <para>
        /// It counts colliders rather than exported bytes. The collision payload
        /// is one entry per collision source, so the collider count IS the size
        /// to within a constant; running the exporter here would measure nothing
        /// anyway, because the authoring components it walks are added by
        /// <c>RandomDungeonSceneBuilder</c> after the render pass, not by the
        /// renderer.
        /// </para>
        /// </remarks>
        [MenuItem("Tools/Dungeon Lab/Measure Density Cost (12 Seeds)")]
        public static void MeasureDensityCost()
        {
            RunDensityCostProbe(BaselineFirstSeed, 12);
        }

        private static string RunDensityCostProbe(int firstSeed, int seedCount)
        {
            Directory.CreateDirectory(BatchReportDirectory);
            int densityLevel = ResolveRequestedDensityLevel();
            var seeds = new JArray();
            try
            {
                for (int index = 0; index < seedCount; index++)
                {
                    int seed = firstSeed + index;
                    GameObject root = null;
                    try
                    {
                        root = BuildRenderedSeed(
                            seed,
                            out _,
                            out JObject seedReport,
                            out ElevationEdgeModel.BuildReport buildReport);
                        seeds.Add(BuildDensityCostEntry(seed, root, seedReport, buildReport));
                    }
                    catch (Exception failure)
                    {
                        seeds.Add(new JObject
                        {
                            ["seed"] = seed,
                            ["failure"] = failure.Message
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

            var report = new JObject
            {
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["densityLevel"] = densityLevel,
                ["firstSeed"] = firstSeed,
                ["seedCount"] = seedCount,
                ["seeds"] = seeds,
                ["measurement"] =
                    "per seed: the rendered root's object/collider/renderer counts and the renderer's own " +
                    "wall and feature tallies. Collider count stands in for collision payload size — the " +
                    "payload is one entry per collision source."
            };
            AddGenerationSettingsIdentity(report);
            string path = Path.Combine(
                BatchReportDirectory,
                $"density_cost_d{densityLevel}.json");
            File.WriteAllText(path, report.ToString(Formatting.Indented));
            Debug.Log($"Dungeon Lab DENSITY_COST density={densityLevel}; seeds={seedCount}; report={path}");
            return path;
        }

        // Enough of the path to find the prefab, without the seed-specific
        // coordinates every generated name carries.
        private static string DescribeHierarchyPath(Transform transform)
        {
            var parts = new List<string>();
            for (Transform current = transform; current != null; current = current.parent)
            {
                parts.Insert(0, current.name);
                if (parts.Count >= 3)
                {
                    break;
                }
            }

            return string.Join("/", parts);
        }

        private static JObject BuildDensityCostEntry(
            int seed,
            GameObject root,
            JObject seedReport,
            ElevationEdgeModel.BuildReport buildReport)
        {
            int gameObjects = root.GetComponentsInChildren<Transform>(includeInactive: true).Length;
            int renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true).Length;
            Collider[] colliders = root.GetComponentsInChildren<Collider>(includeInactive: true);
            int meshColliders = 0;
            int meshCollidersMissingMesh = 0;
            var missingMeshOwners = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Collider collider in colliders)
            {
                if (!(collider is MeshCollider meshCollider))
                {
                    continue;
                }

                meshColliders++;
                // A mesh collider with no mesh is a hole in the exported server
                // collision, not a cosmetic problem. Seven EditMode tests have
                // been red on `main` for a while asserting this is zero
                // (`collision.missingMeshes`), so the count — and enough of a
                // name to find the prefab — belongs somewhere a fresh build
                // reports it rather than only a test fixture.
                if (meshCollider.sharedMesh == null)
                {
                    meshCollidersMissingMesh++;
                    if (missingMeshOwners.Count < 12)
                    {
                        missingMeshOwners.Add(DescribeHierarchyPath(meshCollider.transform));
                    }
                }
            }

            JObject density = seedReport["measurements"]?["density"] as JObject;
            return new JObject
            {
                ["seed"] = seed,
                ["floorCells"] = buildReport.floorCells,
                ["latticeEnvelopeFillPercent"] = density?.Value<float?>("latticeEnvelopeFillPercent") ?? 0f,
                ["gameObjects"] = gameObjects,
                ["renderers"] = renderers,
                ["colliders"] = colliders.Length,
                ["meshColliders"] = meshColliders,
                ["meshCollidersMissingMesh"] = meshCollidersMissingMesh,
                ["meshCollidersMissingMeshOwners"] = new JArray(missingMeshOwners),
                ["partitionWalls"] = buildReport.partitionWalls,
                ["cliffEdges"] = buildReport.cliffEdges,
                ["retainingEdges"] = buildReport.retainingEdges,
                ["railings"] = buildReport.railings,
                ["doorways"] = buildReport.doorways,
                ["gateways"] = buildReport.gateways,
                ["traps"] = buildReport.traps
            };
        }


        private static string RunRenderSweep(int firstSeed, int seedCount)
        {
            Directory.CreateDirectory(BatchReportDirectory);
            var failures = new JArray();
            var codes = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int rendered = 0;
            try
            {
                for (int index = 0; index < seedCount; index++)
                {
                    int seed = firstSeed + index;
                    if (!Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar(
                            "Dungeon Lab Render Sweep",
                            $"Seed {seed} ({index + 1}/{seedCount})",
                            (float)index / seedCount))
                    {
                        break;
                    }

                    GameObject root = null;
                    try
                    {
                        root = BuildRenderedSeed(seed, out _, out _, out _);
                        rendered++;
                    }
                    catch (Exception failure)
                    {
                        string code = NormalizedRejectionCode(failure.Message, failure);
                        codes.TryGetValue(code, out int count);
                        codes[code] = count + 1;
                        failures.Add(new JObject
                        {
                            ["seed"] = seed,
                            ["code"] = code,
                            ["message"] = failure.Message
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

            var report = new JObject
            {
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["firstSeed"] = firstSeed,
                ["seedCount"] = seedCount,
                ["rendered"] = rendered,
                ["failureCodes"] = HistogramToken(codes),
                ["failures"] = failures
            };
            AddGenerationSettingsIdentity(report);
            string path = Path.Combine(
                BatchReportDirectory,
                $"render_sweep_{firstSeed}_{firstSeed + seedCount - 1}.json");
            File.WriteAllText(path, report.ToString(Formatting.Indented));
            Debug.Log(
                $"Dungeon Lab RENDER_SWEEP range={firstSeed}..{firstSeed + seedCount - 1}; " +
                $"rendered={rendered}/{seedCount}; failureCodes={FormatCountSummary(codes)}; report={path}");
            return path;
        }

        private static void CaptureVisualSentinels(string directoryName)
        {
            string directory = Path.Combine(BatchReportDirectory, directoryName);
            Directory.CreateDirectory(directory);
            var manifestEntries = new JArray();
            var failures = new List<string>();

            try
            {
                foreach ((int seed, string category, string annotation) sentinel in VisualSentinels)
                {
                    GameObject root = null;
                    try
                    {
                        root = BuildRenderedSeed(
                            sentinel.seed,
                            out Bounds bounds,
                            out JObject seedReport,
                            out ElevationEdgeModel.BuildReport buildReport);
                        string fileName = $"{sentinel.seed}_{sentinel.category}.png";
                        string path = Path.Combine(directory, fileName);
                        CaptureSentinelImage(bounds, path);
                        string topDownFileName = $"{sentinel.seed}_{sentinel.category}_topdown.png";
                        string topDownPath = Path.Combine(directory, topDownFileName);
                        CaptureTopDownSentinelImage(bounds, topDownPath);
                        manifestEntries.Add(new JObject
                        {
                            ["seed"] = sentinel.seed,
                            ["category"] = sentinel.category,
                            ["annotation"] = sentinel.annotation,
                            ["image"] = path.Replace('\\', '/'),
                            ["topDownImage"] = topDownPath.Replace('\\', '/'),
                            ["canonicalHash"] = seedReport["hashes"]?["canonical"],
                            ["measurements"] = seedReport["measurements"]?.DeepClone(),
                            ["floorplan"] = seedReport["floorplan"]?.DeepClone(),
                            ["rendererSummary"] = buildReport.Summary
                        });
                    }
                    catch (Exception failure)
                    {
                        // One sentinel that will not render used to destroy the
                        // whole capture, so the five that DID render were never
                        // written and the comparison the sentinels exist for was
                        // unavailable exactly when something had gone wrong. The
                        // failure is recorded per sentinel and rethrown below,
                        // after every other image is on disk — louder, not
                        // quieter.
                        failures.Add($"{sentinel.seed} ({sentinel.category}): {failure.Message}");
                        manifestEntries.Add(new JObject
                        {
                            ["seed"] = sentinel.seed,
                            ["category"] = sentinel.category,
                            ["annotation"] = sentinel.annotation,
                            ["renderFailure"] = failure.Message
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
                ["captureWidth"] = SentinelImageWidth,
                ["captureHeight"] = SentinelImageHeight,
                ["topDownCaptureSize"] = SentinelTopDownImageSize,
                ["renderFailures"] = new JArray(failures),
                ["sentinels"] = manifestEntries
            };
            AddGenerationSettingsIdentity(manifest);
            string manifestPath = Path.Combine(directory, "manifest.json");
            File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented));
            Debug.Log($"Dungeon Lab: visual sentinels written to {directory} (manifest {manifestPath}).");
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{failures.Count} of {VisualSentinels.Length} sentinels did not render: " +
                    string.Join("; ", failures));
            }
        }

        private static string RunBatchValidation(
            int firstSeed,
            int requestedSeedCount)
        {
            int densityLevel = ResolveRequestedDensityLevel();
            CurrentGenerationSettings = LoadActiveGenerationSettings(densityLevel);
            BeginPlanIdentityRecording();
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
            var tierAttemptCounts = new List<int>();
            var routeRoomCounts = new List<int>();
            var branchNodeCounts = new List<int>();
            var loopEdgeCounts = new List<int>();
            var elevationSpans = new List<int>();
            var transitionCounts = new List<int>();
            var visibleDistantRoomProxyCounts = new List<int>();
            var routeClimbCounts = new List<int>();
            var latticeEnvelopeFillPercents = new List<int>();
            var maxVoidComponentCells = new List<int>();
            var channelVoidCells = new List<int>();
            var vacantVoidCells = new List<int>();
            int successCount = 0;
            int hardValidCount = 0;
            int routeRequirementsValidCount = 0;
            int finalVistaValidCount = 0;
            int recipeSetValidCount = 0;
            int completedSeedCount = 0;

            try
            {
                for (int i = 0; i < requestedSeedCount; i++)
                {
                    int seed = firstSeed + i;
                    string selectedPattern = SelectRouteTopologyId(seed);
                    selectedPatternCounts.TryGetValue(selectedPattern, out int selectedPatternCount);
                    selectedPatternCounts[selectedPattern] = selectedPatternCount + 1;
                    if (!Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar(
                            "Dungeon Lab Batch Validate",
                            $"Seed {seed} ({i + 1}/{requestedSeedCount})",
                            (float)i / requestedSeedCount))
                    {
                        break;
                    }

                    JObject seedReport = BuildSeedReport(seed, densityLevel);
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
                    tierAttemptCounts.Add(seedReport.Value<int?>("tierAttempts") ?? 0);
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

                    if (seedReport["measurements"]?["density"] is JObject density &&
                        density.Value<bool?>("available") == true)
                    {
                        latticeEnvelopeFillPercents.Add(
                            Mathf.RoundToInt(density.Value<float>("latticeEnvelopeFillPercent")));
                        maxVoidComponentCells.Add(
                            density["voidComponents"]?.Value<int?>("maxComponentCells") ?? 0);
                        channelVoidCells.Add(
                            density["voidDecomposition"]?.Value<int?>("channelVoidCells") ?? 0);
                        vacantVoidCells.Add(
                            density["voidDecomposition"]?.Value<int?>("vacantVoidCells") ?? 0);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (completedSeedCount <= 0)
            {
                recordingPlanIdentityDiagnostics = false;
                Debug.Log("Dungeon Lab: batch validation cancelled before any seeds ran.");
                return string.Empty;
            }

            string planIdentityPath = WritePlanIdentityReport(firstSeed, completedSeedCount);
            Debug.Log(
                "Dungeon Lab PLAN_IDENTITY " +
                $"version={PlanIdentityReportVersion}; acceptedSeedsExamined={planIdentitySeedsExamined}; " +
                $"planShadowDisagreementSeeds={planIdentityShadowDisagreementSeeds}; " +
                $"connectionIdentityViolationSeeds={planIdentityConnectionViolationSeeds}; " +
                $"report={planIdentityPath}");

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

            JObject fillDistribution = BuildIntDistribution(latticeEnvelopeFillPercents);
            JObject voidDistribution = BuildIntDistribution(maxVoidComponentCells);
            Debug.Log(
                "Dungeon Lab BATCH_DENSITY " +
                $"measurement={DensityMeasurementVersion}; seeds={latticeEnvelopeFillPercents.Count}; " +
                $"latticeEnvelopeFillPercent min={fillDistribution.Value<int>("min")} " +
                $"p50={fillDistribution.Value<int>("p50")} max={fillDistribution.Value<int>("max")}; " +
                $"maxVoidComponentCells min={voidDistribution.Value<int>("min")} " +
                $"p50={voidDistribution.Value<int>("p50")} max={voidDistribution.Value<int>("max")}; " +
                $"channelVoidCells p50={BuildIntDistribution(channelVoidCells).Value<int>("p50")}; " +
                $"vacantVoidCells p50={BuildIntDistribution(vacantVoidCells).Value<int>("p50")}");

            string reportPath = WriteBatchReport(
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
                tierAttemptCounts);
            Debug.Log($"Dungeon Lab: batch validation report written to {reportPath}");
            return reportPath;
        }

        // ------------------------------------------------------------------
        // Plan identity diagnostics — OUT OF BAND, on purpose.
        //
        // `resultHash` is SHA-256 over the ordered seed-report array
        // (WriteBatchReport), so a diagnostic added to a seed report moves the
        // hash with no geometry change at all. A1's whole claim is that the hash
        // holds, so these findings get their own file and the hashed array never
        // learns they exist. That is the only arrangement in which "the check has
        // teeth" and "nothing moved" are both provable in one run.
        // ------------------------------------------------------------------
        private const string PlanIdentityReportVersion = "plan-identity-v1";
        private static bool recordingPlanIdentityDiagnostics;
        private static readonly JArray PlanIdentityFindings = new JArray();
        private static int planIdentitySeedsExamined;
        private static int planIdentityShadowDisagreementSeeds;
        private static int planIdentityConnectionViolationSeeds;

        private static void BeginPlanIdentityRecording()
        {
            PlanIdentityFindings.Clear();
            planIdentitySeedsExamined = 0;
            planIdentityShadowDisagreementSeeds = 0;
            planIdentityConnectionViolationSeeds = 0;
            recordingPlanIdentityDiagnostics = true;
        }

        // Only the first few cells are named. The point is to identify the
        // producer, not to serialize a second copy of the plan.
        private const int PlanIdentitySampleCells = 8;

        private static void RecordPlanIdentityDiagnostics(
            int seed,
            DungeonLayout layout,
            TieredLevelPlan plan)
        {
            if (!recordingPlanIdentityDiagnostics)
            {
                return;
            }

            planIdentitySeedsExamined++;
            PlanShadowDisagreement disagreement = DetectPlanShadowDisagreement(layout, plan);
            List<string> connectionViolations =
                lastConnectionIdentityViolations ?? new List<string>();
            if (disagreement.Agrees && connectionViolations.Count == 0)
            {
                return;
            }

            var finding = new JObject { ["seed"] = seed };
            if (!disagreement.Agrees)
            {
                planIdentityShadowDisagreementSeeds++;
                finding["code"] = "PLAN_SHADOW_DISAGREEMENT";
                finding["planShadowCells"] = layout.floorCells?.Count ?? 0;
                // CELLS carrying a surface, to stay comparable with
                // planShadowCells above. `SurfaceField.Count` counts SURFACES,
                // which is the same number only while the field is single-layer.
                finding["surfaceCells"] = plan.surfaces?.FlooredPlanCells().Count ?? 0;
                finding["surfacedCellsOutsideShadow"] = new JObject
                {
                    ["count"] = disagreement.surfacedCellsOutsideShadow.Length,
                    ["sample"] = SampleCellsToken(disagreement.surfacedCellsOutsideShadow)
                };
                finding["shadowCellsWithoutSurface"] = new JObject
                {
                    ["count"] = disagreement.shadowCellsWithoutSurface.Length,
                    ["sample"] = SampleCellsToken(disagreement.shadowCellsWithoutSurface),
                    ["note"] = "informational: a shadow cell with no surface is legitimate " +
                        "(the gap under an external span deck), and the shadow is the domain " +
                        "the level field floods within. Not part of the agreement gate."
                };
            }

            if (connectionViolations.Count > 0)
            {
                planIdentityConnectionViolationSeeds++;
                finding["connectionIdentityViolations"] = new JArray(connectionViolations);
            }

            PlanIdentityFindings.Add(finding);
        }

        private static JArray SampleCellsToken(IReadOnlyList<Vector2Int> cells)
        {
            var token = new JArray();
            for (int index = 0; index < cells.Count && index < PlanIdentitySampleCells; index++)
            {
                token.Add(CellToken(cells[index]));
            }

            return token;
        }

        private static string WritePlanIdentityReport(int firstSeed, int seedCount)
        {
            recordingPlanIdentityDiagnostics = false;
            var report = new JObject
            {
                ["reportVersion"] = PlanIdentityReportVersion,
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["purpose"] =
                    "Phase A1 detects the plan-shadow disagreement and the connection-identity " +
                    "invariant and reports them HERE rather than in a seed report, because " +
                    "resultHash is SHA-256 over the seed-report array and A1 gates on that hash " +
                    "holding. Repairing the shadow is A2's job and moves the hash once.",
                ["firstSeed"] = firstSeed,
                ["seedCount"] = seedCount,
                ["acceptedSeedsExamined"] = planIdentitySeedsExamined,
                ["planShadowDisagreementSeeds"] = planIdentityShadowDisagreementSeeds,
                ["connectionIdentityViolationSeeds"] = planIdentityConnectionViolationSeeds,
                ["findings"] = PlanIdentityFindings
            };

            Directory.CreateDirectory(BatchReportDirectory);
            string path = Path.Combine(
                BatchReportDirectory,
                $"plan_identity_{firstSeed}_{firstSeed + seedCount - 1}.json");
            File.WriteAllText(path, report.ToString(Formatting.Indented));
            return path;
        }

        private static JObject BuildSeedReport(int seed)
        {
            return BuildSeedReport(seed, ResolveRequestedDensityLevel());
        }

        private static JObject BuildSeedReport(int seed, int densityLevel)
        {
            CurrentGenerationSettings = LoadActiveGenerationSettings(densityLevel);
            var rejectionHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                bool accepted = TryBuildAcceptedPlan(
                    seed,
                    rejectionHistogram,
                    out DungeonLayout layout,
                    out TieredLevelPlan plan,
                    out _,
                    out DungeonPlanValidation validation,
                    out int layoutAttemptsUsed,
                    out string rejectionReason);
                if (accepted)
                {
                    RecordPlanIdentityDiagnostics(seed, layout, plan);
                    JObject acceptedReport = CreateAcceptedSeedReport(
                        seed,
                        layoutAttemptsUsed,
                        rejectionReason,
                        rejectionHistogram,
                        layout,
                        plan,
                        validation);
                    return acceptedReport;
                }

                JObject rejectedReport = CreateRejectedSeedReport(
                        seed,
                        layoutAttemptsUsed,
                        rejectionReason,
                        rejectionHistogram,
                        exception: null);
                return rejectedReport;
            }
            catch (Exception exception)
            {
                JObject exceptionReport = CreateRejectedSeedReport(
                    seed,
                    0,
                    exception.Message,
                    rejectionHistogram,
                    exception);
                return exceptionReport;
            }
        }

        // Focused corrective-item diagnostic used by EditMode tests. It stays on
        // the production resolver/report/renderer paths and does not create a
        // second planner or mutate project assets.
        private static string BuildExternalConnectorSnapshot()
        {
            var lines = new List<string>
            {
                $"policy.version={ExternalConnectorPromontoryPolicyVersion}",
                $"policy.rejectsConcavity={!IsOnExternalConnectorOuterFace(new RectInt(-3, -3, 7, 7), Vector2Int.zero, Direction.North)}",
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
                bool allLongAndStraight = true;
                bool allOuter = true;
                var probeExtent = new RectInt(-3, -3, 7, 7);
                foreach (ExternalConnectorPromontoryResolution resolution in resolutions)
                {
                    directions.Add(resolution.direction);
                    allOuter &=
                        IsOnExternalConnectorOuterFace(
                            probeExtent,
                            resolution.anchorCell,
                            resolution.direction);
                    Vector2Int outward = CardinalVector(resolution.direction);
                    allLongAndStraight &= outward != Vector2Int.zero &&
                        resolution.occupiedCells.Length == ExternalConnectorAppendageCells + 1;
                    for (int index = 0; index < resolution.occupiedCells.Length; index++)
                    {
                        allLongAndStraight &=
                            resolution.occupiedCells[index] ==
                            resolution.anchorCell + outward * index;
                    }

                    allLongAndStraight &=
                        resolution.terminalCell ==
                        resolution.anchorCell + outward * ExternalConnectorAppendageCells;
                }
                lines.Add($"resolver.{count}.seed={seed}");
                lines.Add($"resolver.{count}.resolved={resolved}");
                lines.Add($"resolver.{count}.count={resolutions.Length}");
                lines.Add($"resolver.{count}.uniqueDirections={directions.Count}");
                lines.Add($"resolver.{count}.addedCells={afterCells - beforeCells}");
                lines.Add($"resolver.{count}.allLongAndStraight={allLongAndStraight}");
                lines.Add($"resolver.{count}.allOuter={allOuter}");
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
                JObject report = BuildSeedReport(seed);
                lines.Add($"production.{seed}.accepted={report.Value<bool?>("accepted") == true}");
                lines.Add($"production.{seed}.hardValid={report["validation"]?.Value<bool?>("passed") == true}");
                lines.Add($"production.{seed}.desired={ExternalConnectorDesiredCount(seed)}");
                lines.Add($"production.{seed}.count={(report["externalConnectors"] as JArray)?.Count ?? 0}");
                lines.Add($"production.{seed}.externalValid={report["validation"]?["externalConnectors"]?.Value<bool?>("passed") == true}");
                lines.Add($"production.{seed}.transitionHash={report["hashes"]?.Value<string>("existingTransitions")}");
                lines.Add($"production.{seed}.prechangePlanHash={report["hashes"]?.Value<string>("preCorrectiveTieredLevelPlan")}");
            }

            JObject renderer = JObject.Parse(BuildRendererProbeJson(2026072100));
            lines.Add($"renderer.accepted={renderer.Value<bool?>("accepted") == true}");
            lines.Add($"renderer.passed={renderer["renderer"]?.Value<bool?>("passed") == true}");
            lines.Add($"renderer.promontoryDeckCells={renderer["renderer"]?.Value<int?>("promontoryDeckCells") ?? -1}");
            lines.Add($"renderer.expectedPromontoryDeckCells={renderer["renderer"]?.Value<int?>("expectedPromontoryDeckCells") ?? -1}");
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
            var surfaces = new SurfaceField(levels);
            var layout = new DungeonLayout(
                floorCells,
                new List<RoomFootprint> { room },
                new List<RoomConnection>());
            var protectedCells = new HashSet<Vector2Int>();
            if (excludeAllAnchors)
                protectedCells.UnionWith(floorCells);
            beforeCells = surfaces.Count;
            resolved = TryResolveExternalConnectorPromontories(
                seed,
                layout,
                surfaces,
                new List<ElevationEdgeModel.TransitionEdge>(),
                protectedCells,
                new HashSet<Vector2Int>(),
                new PrismLedger(),
                Array.Empty<NamedVistaPromontoryResolution>(),
                out resolutions,
                out error);
            afterCells = surfaces.Count;
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
            JObject report = BuildSeedReport(seed);
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
            JObject report = BuildSeedReport(seed);
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
                SnapshotLine(
                    "recipe.approachTransitionConflicts",
                    CountReservedRecipeApproachTransitionConflicts(
                        report["recipeResolutions"] as JArray,
                        report["existingTransitions"] as JArray)),
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

        private static int CountReservedRecipeApproachTransitionConflicts(
            JArray recipeResolutions,
            JArray existingTransitions)
        {
            var requiredApproachCells = new HashSet<Vector2Int>();
            foreach (JToken recipe in recipeResolutions ?? new JArray())
            {
                foreach (JToken port in recipe["ports"] as JArray ?? new JArray())
                {
                    Vector2Int portCell = ProjectedCell(port["cell"]);
                    Vector2Int outward = ProjectedCell(port["outwardDirection"]);
                    bool transitionAbutsWallEnd = false;
                    foreach (JToken transition in recipe["transitions"] as JArray ?? new JArray())
                    {
                        foreach (JToken footprint in
                                 transition["footprintCells"] as JArray ?? new JArray())
                        {
                            Vector2Int offset = ProjectedCell(footprint) - portCell;
                            transitionAbutsWallEnd |=
                                Mathf.Abs(offset.x) + Mathf.Abs(offset.y) == 1 &&
                                offset.x * outward.x + offset.y * outward.y == 0;
                        }
                    }

                    if (!transitionAbutsWallEnd)
                    {
                        continue;
                    }

                    foreach (JToken approach in port["approachCells"] as JArray ?? new JArray())
                    {
                        requiredApproachCells.Add(ProjectedCell(approach));
                    }
                }
            }

            int conflicts = 0;
            foreach (JToken transition in existingTransitions ?? new JArray())
            {
                var occupied = new HashSet<Vector2Int>
                {
                    ProjectedCell(transition["firstCell"]),
                    ProjectedCell(transition["secondCell"])
                };
                foreach (JToken footprint in
                         transition["footprintCells"] as JArray ?? new JArray())
                {
                    occupied.Add(ProjectedCell(footprint));
                }

                foreach (Vector2Int cell in occupied)
                {
                    if (requiredApproachCells.Contains(cell))
                    {
                        conflicts++;
                    }
                }
            }

            return conflicts;
        }

        private static Vector2Int ProjectedCell(JToken token)
        {
            return new Vector2Int(
                token?.Value<int?>("x") ?? int.MinValue,
                token?.Value<int?>("y") ?? int.MinValue);
        }

        private static JObject FindRouteNodeProjection(JObject routeIntent, string nodeId)
        {
            foreach (JToken node in routeIntent?["nodes"] as JArray ?? new JArray())
            {
                if (string.Equals(node.Value<string>("id"), nodeId, StringComparison.Ordinal))
                {
                    return node as JObject;
                }
            }

            return null;
        }

        private static string BuildRouteIntentOnlySnapshot(int seed)
        {
            ResetRouteDiagnostics();
            lastRouteIntent = BuildDiagnosticRouteIntent(seed);
            JObject intent = BuildRouteIntentProjection();
            JObject phase4Recipe = FindRecipeProjection(
                intent["recipeSlots"] as JArray,
                ThroneRecipeFixtureId);
            bool containsSpatialCoordinates = intent.ToString(Formatting.None).Contains("\"center\"");
            int requiredStairs = 0;
            int requiredBridges = 0;
            int requiredStairwells = 0;
            foreach (RouteTraversalIntent edge in lastRouteIntent.traversalEdges)
            {
                if (edge.transitionKind == RouteTransitionKind.Stair) requiredStairs++;
                if (edge.transitionKind == RouteTransitionKind.Bridge) requiredBridges++;
                if (edge.transitionKind == RouteTransitionKind.Stairwell) requiredStairwells++;
            }

            var lines = new List<string>
            {
                $"selector.firstSeed={FirstSeedSelectingTopology(ProcessionalPatternId, BaselineFirstSeed, 2000)}",
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
                // The anchors are declared, so read them by id. Indices 0 and 8
                // only ever happened to be the anchors of a 13-node ascending
                // graph.
                SnapshotLine(
                    "vertical.bottomRelativeLevel",
                    FindRouteNodeProjection(intent, intent.Value<string>("bottomNode"))?["relativeElevationLevels"]),
                SnapshotLine(
                    "vertical.topRelativeLevel",
                    FindRouteNodeProjection(intent, intent.Value<string>("topNode"))?["relativeElevationLevels"]),
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

        // Every node of degree >= 3, with its degree. A general graph has no
        // single attach/rejoin pair — twin-wing already forks twice off one hub
        // — so the honest report is the whole junction set.
        private static string RouteJunctionSummary(RouteIntent intent)
        {
            var junctions = new List<string>(intent.junctionNodes.Length);
            foreach (int node in intent.junctionNodes)
            {
                junctions.Add($"{intent.nodes[node].id}:{intent.adjacency[node].Count}");
            }

            return string.Join("|", junctions);
        }

        // Diagnostics that want a specific topology have to ask for one: with a
        // weighted draw, no seed is guaranteed to select any particular graph.
        private static int FirstSeedSelectingTopology(string topologyId, int firstSeed, int seedLimit)
        {
            for (int index = 0; index < seedLimit; index++)
            {
                if (string.Equals(SelectRouteTopologyId(firstSeed + index), topologyId, StringComparison.Ordinal))
                {
                    return firstSeed + index;
                }
            }

            throw new InvalidOperationException(
                $"[ROUTE_TOPOLOGY] no seed in {firstSeed}..{firstSeed + seedLimit - 1} selects '{topologyId}'; " +
                "check its weight");
        }

        /// <summary>
        /// The first seed on a topology whose route actually binds one recipe.
        /// </summary>
        /// <remarks>
        /// Slot selection draws from every compatible candidate, so "the first
        /// seed on this topology" stopped being "a seed that binds the corner
        /// return" the moment a second connector/return recipe was enabled
        /// (`connector_generic_room_01`, 2026-07-24). The fixture wants a seed
        /// where the recipe under test IS selected — asserting that one
        /// particular candidate always wins the draw would be asserting the pool
        /// away.
        /// </remarks>
        private static int FirstSeedBindingRecipe(
            string topologyId,
            string recipeId,
            int firstSeed,
            int seedLimit)
        {
            for (int index = 0; index < seedLimit; index++)
            {
                int seed = firstSeed + index;
                if (!string.Equals(SelectRouteTopologyId(seed), topologyId, StringComparison.Ordinal))
                {
                    continue;
                }

                JObject report = BuildSeedReport(seed);
                if (report.Value<bool?>("accepted") == true &&
                    FindRecipeProjection(
                        report["routeIntent"]?["recipeSlots"] as JArray,
                        recipeId) != null)
                {
                    return seed;
                }
            }

            throw new InvalidOperationException(
                $"[RECIPE_POOL] no seed in {firstSeed}..{firstSeed + seedLimit - 1} put '{recipeId}' " +
                $"on '{topologyId}'; check the recipe's eligibility and the candidate pool");
        }

        private static string TopologyWeightSummary()
        {
            var weights = new List<string>();
            foreach (DungeonRouteTopology topology in AllRouteTopologiesByFileOrder())
            {
                weights.Add($"{topology.id}:{topology.weight}");
            }

            return string.Join("|", weights);
        }

        // A weighted draw has no residue table to read off, so the selector is
        // characterised by the distribution it actually produces over a window.
        private static string TopologySelectionSummary(int firstSeed, int seedCount)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (DungeonRouteTopology topology in AllRouteTopologiesByFileOrder())
            {
                counts[topology.id] = 0;
            }

            for (int index = 0; index < seedCount; index++)
            {
                string selected = SelectRouteTopologyId(firstSeed + index);
                counts.TryGetValue(selected, out int seen);
                counts[selected] = seen + 1;
            }

            var summary = new List<string>(counts.Count);
            foreach (KeyValuePair<string, int> entry in counts)
            {
                summary.Add($"{entry.Key}:{entry.Value}");
            }

            return string.Join("|", summary);
        }

        private static RouteIntent BuildDiagnosticRouteIntent(int seed)
        {
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out string rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            return ResolveDiagnosticRouteIntent(
                BuildTopologyRouteIntent(
                    RequireRouteTopology(ProcessionalPatternId),
                    seed,
                    Array.Empty<RecipeSlotIntent>(),
                    string.Empty),
                catalog);
        }

        private static RouteIntent BuildDiagnosticAtriumRingIntent(int seed)
        {
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out string rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            return ResolveDiagnosticRouteIntent(
                BuildTopologyRouteIntent(
                    RequireRouteTopology(AtriumRingPatternId),
                    seed,
                    Array.Empty<RecipeSlotIntent>(),
                    string.Empty),
                catalog);
        }

        private static RouteIntent BuildDiagnosticTwinWingIntent(int seed)
        {
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out string rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            return ResolveDiagnosticRouteIntent(
                BuildTopologyRouteIntent(
                    RequireRouteTopology(TwinWingPatternId),
                    seed,
                    Array.Empty<RecipeSlotIntent>(),
                    string.Empty),
                catalog);
        }

        private static RouteIntent BuildDiagnosticSelectedRouteIntent(int seed)
        {
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out string rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            return ResolveDiagnosticRouteIntent(
                BuildSelectedRouteIntent(
                    seed,
                    Array.Empty<RecipeSlotIntent>(),
                    string.Empty),
                catalog);
        }

        private static RouteIntent ResolveDiagnosticRouteIntent(
            RouteIntent intent,
            ActiveDungeonRecipeCatalog catalog)
        {
            if (!TryResolveRequiredRecipeSlots(
                    catalog,
                    intent,
                    out RecipeSlotIntent[] slots,
                    out string rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            intent.ResolveRecipeSlots(slots, catalog.digest);
            return intent;
        }

        private static string BuildAtriumRingSnapshot(int seed)
        {
            CurrentGenerationSettings = LoadActiveGenerationSettings(ResolveRequestedDensityLevel());
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

            bool embedded = TryEmbedRoute(
                seed,
                layoutAttempt: 1,
                intent,
                ResolveTopologySpatialSettings(intent.topology),
                out Vector2Int[] nodeCenters,
                out Vector2Int[] _,
                out string embeddingFailureCode,
                out string embeddingError);
            Vector2Int vistaDelta = embedded
                ? nodeCenters[intent.vista.targetNode] - nodeCenters[intent.vista.sourceNode]
                : Vector2Int.zero;
            int vistaCenterDistance = Mathf.Abs(vistaDelta.x) + Mathf.Abs(vistaDelta.y);
            return string.Join("\n", new[]
            {
                $"selector.weights={TopologyWeightSummary()}",
                $"selector.distribution={TopologySelectionSummary(BaselineFirstSeed, 200)}",
                $"selector.firstSeed={FirstSeedSelectingTopology(AtriumRingPatternId, BaselineFirstSeed, 2000)}",
                $"processional.plannerVersion={processional.plannerVersion}",
                $"processional.cycleLength={processional.cycleCoreNodeCount}",
                $"graph.pattern={intent.patternId}",
                $"graph.plannerVersion={intent.plannerVersion}",
                $"graph.nodeCount={intent.nodes.Length}",
                $"graph.edgeCount={intent.traversalEdges.Length}",
                $"graph.loopEdges={intent.traversalEdges.Length - (intent.nodes.Length - 1)}",
                $"graph.cycleLength={CountCycleCoreNodes(adjacency)}",
                $"graph.junctions={RouteJunctionSummary(intent)}",
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

        private static string BuildTwinWingSnapshot(int seed)
        {
            CurrentGenerationSettings = LoadActiveGenerationSettings(ResolveRequestedDensityLevel());
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

            bool embedded = TryEmbedRoute(
                seed,
                layoutAttempt: 1,
                intent,
                ResolveTopologySpatialSettings(intent.topology),
                out Vector2Int[] nodeCenters,
                out Vector2Int[] _,
                out string embeddingFailureCode,
                out string embeddingError);
            Vector2Int vistaDelta = embedded
                ? nodeCenters[intent.vista.targetNode] - nodeCenters[intent.vista.sourceNode]
                : Vector2Int.zero;
            int vistaCenterDistance = Mathf.Abs(vistaDelta.x) + Mathf.Abs(vistaDelta.y);
            return string.Join("\n", new[]
            {
                $"selector.weights={TopologyWeightSummary()}",
                $"selector.distribution={TopologySelectionSummary(BaselineFirstSeed, 200)}",
                $"selector.firstSeed={FirstSeedSelectingTopology(TwinWingPatternId, BaselineFirstSeed, 2000)}",
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
                $"graph.junctions={RouteJunctionSummary(intent)}",
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

        private static string BuildRouteRhythmSnapshot(int seed)
        {
            RouteIntent processional = BuildDiagnosticSelectedRouteIntent(
                FirstSeedSelectingTopology(ProcessionalPatternId, BaselineFirstSeed, 2000));
            RouteIntent atrium = BuildDiagnosticSelectedRouteIntent(
                FirstSeedSelectingTopology(AtriumRingPatternId, BaselineFirstSeed, 2000));
            RouteIntent twinWing = BuildDiagnosticSelectedRouteIntent(
                FirstSeedSelectingTopology(TwinWingPatternId, BaselineFirstSeed, 2000));
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
                invalidSecond.recipeSlotId);
            var invalidIntent = new RouteIntent(
                processional.seed,
                processional.plannerVersion,
                processional.topology,
                invalidNodes,
                processional.traversalEdges,
                processional.vista,
                processional.elevationPolicy,
                processional.recipeSlots,
                processional.catalogDigest,
                processional.bottomNode,
                processional.topNode,
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

        private static string BuildNamedPromontorySnapshot(int seed)
        {
            JObject processional = BuildSeedReport(
                FirstSeedSelectingTopology(ProcessionalPatternId, 2026072124, 2000));
            JObject noSurplus = BuildSeedReport(BaselineFirstSeed);
            JObject atrium = BuildSeedReport(
                FirstSeedSelectingTopology(AtriumRingPatternId, BaselineFirstSeed, 2000));
            JObject twinWing = BuildSeedReport(
                FirstSeedSelectingTopology(TwinWingPatternId, BaselineFirstSeed, 2000));

            RouteIntent probeIntent = BuildDiagnosticSelectedRouteIntent(BaselineFirstSeed);
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
            SurfaceField occupiedLevels = PromontoryProbeLevels(
                probeSource,
                12,
                probeTarget,
                8);
            occupiedLevels.AddFloorLevel(probePlanned[0], 12);
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

            JObject renderer = JObject.Parse(BuildRendererProbeJson(2026072101));
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

        private static string BuildCornerReturnRecipeSnapshot(int seed)
        {
            bool catalogValid = DungeonRecipeCatalogService.TryLoadActiveCatalog(
                out ActiveDungeonRecipeCatalog catalog,
                out string catalogError);
            DungeonRecipeAsset recipe = null;
            catalog?.TryGet(CornerReturnRecipeFixtureId, out recipe);
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
            bool firstGalleryPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                recipe,
                seed,
                out string firstGalleryPath,
                out string firstGalleryMessage);
            JObject firstGallery = firstGalleryPassed
                ? JObject.Parse(File.ReadAllText(firstGalleryPath))
                : new JObject();
            bool secondGalleryPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
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
                $"catalog.activeCount={catalog?.recipes.Length ?? 0}",
                $"catalog.digest={catalog?.digest ?? string.Empty}",
                $"recipe.id={recipe?.recipeId ?? string.Empty}",
                $"recipe.schema={recipe?.schemaVersion ?? 0}",
                $"recipe.disabledForGeneration={recipe?.disabledForGeneration == true}",
                $"recipe.currentValid={contract.Passed}",
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

            // Selection is a weighted draw now, so a seed no longer names a
            // topology. Find the first seed that actually selects each one.
            foreach ((string prefix, string topologyId) sample in new[]
                     {
                         ("processional", ProcessionalPatternId),
                         ("atrium", AtriumRingPatternId),
                         ("twinWing", TwinWingPatternId)
                     })
            {
                JObject report = BuildSeedReport(
                    FirstSeedBindingRecipe(
                        sample.topologyId,
                        CornerReturnRecipeFixtureId,
                        2026072100,
                        2000));
                JObject slot = FindRecipeProjection(
                    report["routeIntent"]?["recipeSlots"] as JArray,
                    CornerReturnRecipeFixtureId);
                JObject resolution = FindRecipeProjection(
                    report["recipeResolutions"] as JArray,
                    CornerReturnRecipeFixtureId);
                JObject node = report["routeIntent"]?["nodes"]?[slot?.Value<int?>("slotNode") ?? 0] as JObject;
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

            RouteIntent probeIntent = lastRouteIntent;
            RecipeSlotIntent validSlot = null;
            foreach (RecipeSlotIntent slot in probeIntent?.recipeSlots ?? Array.Empty<RecipeSlotIntent>())
            {
                if (string.Equals(slot?.recipe?.recipeId, CornerReturnRecipeFixtureId, StringComparison.Ordinal))
                {
                    validSlot = slot;
                    break;
                }
            }

            // The return slot's node and its edge ids come from the topology
            // file, not from a pinned node index and a pinned edge name.
            RouteTopologySlot probeReturnSlot = null;
            foreach (RouteTopologySlot slot in probeIntent?.topology.slots ?? Array.Empty<RouteTopologySlot>())
            {
                if (string.Equals(slot.slotId, ReturnRecipeSlotId, StringComparison.Ordinal))
                {
                    probeReturnSlot = slot;
                    break;
                }
            }

            int returnSlotNode = probeReturnSlot?.node ?? 0;
            string returnEntryEdgeId = probeReturnSlot?.entryEdgeId ?? string.Empty;
            string unrelatedEdgeId = string.Empty;
            foreach (RouteTopologyEdge edge in probeIntent?.topology.edges ?? Array.Empty<RouteTopologyEdge>())
            {
                if (edge.fromNode != returnSlotNode && edge.toNode != returnSlotNode)
                {
                    unrelatedEdgeId = edge.id;
                    break;
                }
            }

            bool validAxisResolved = TryResolveRouteForwardRecipeAxis(
                probeIntent,
                validSlot,
                lastNodeCenters,
                out Vector2Int validAxis);
            var missingExitSlot = new RecipeSlotIntent(
                ReturnRecipeSlotId,
                returnSlotNode,
                recipe,
                RecipeOrientationBinding.RouteForward,
                new[] { new RecipePortBinding("entry", returnEntryEdgeId) });
            bool missingExitRejected = !TryResolveRouteForwardRecipeAxis(
                probeIntent,
                missingExitSlot,
                lastNodeCenters,
                out _);
            var unrelatedExitSlot = new RecipeSlotIntent(
                ReturnRecipeSlotId,
                returnSlotNode,
                recipe,
                RecipeOrientationBinding.RouteForward,
                new[]
                {
                    new RecipePortBinding("entry", returnEntryEdgeId),
                    new RecipePortBinding("exit", unrelatedEdgeId)
                });
            bool unrelatedExitRejected = !TryResolveRouteForwardRecipeAxis(
                probeIntent,
                unrelatedExitSlot,
                lastNodeCenters,
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
                source.topology,
                source.nodes,
                source.traversalEdges,
                vista,
                source.elevationPolicy,
                source.recipeSlots,
                source.catalogDigest,
                source.bottomNode,
                source.topNode,
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
                // A promontory probe never reaches the fill gate, so the envelope
                // it would measure against is not part of what this fixture tests.
                new RectInt(0, 0, 1, 1),
                reservedCells,
                sourceCell,
                targetCell,
                sourceFacing,
                targetFacing,
                plannedCells,
                Array.Empty<RecipePlacement>());
        }

        private static SurfaceField PromontoryProbeLevels(
            Vector2Int sourceCell,
            int sourceLevel,
            Vector2Int targetCell,
            int targetLevel)
        {
            return new SurfaceField(new Dictionary<Vector2Int, int>
            {
                [sourceCell] = sourceLevel,
                [targetCell] = targetLevel
            });
        }

        private static RouteNodeIntent RhythmProbeNode(
            string id,
            int mainRouteOrder,
            string role,
            string beat,
            string recipeSlotId = "")
        {
            return new RouteNodeIntent(
                id,
                role,
                beat,
                mainRouteOrder,
                branchOrder: -1,
                relativeElevationLevels: mainRouteOrder * MajorRiseLevels,
                recipeSlotId: recipeSlotId);
        }

        // The graph is authored data now, so the contract this exercises is the
        // topology loader's: what it rejects, and which facts it derives rather
        // than reads.
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

            // Rise is derived from the BOUND elevations (design §8.1), and this
            // re-derives them from the NODES' own layer tables rather than from
            // the levels the edge recorded — otherwise the check would compare
            // the builder against itself. For an unbound edge both ends resolve
            // to their node's own level and this is the identical assertion it
            // has always been.
            bool riseIsDerived = true;
            foreach (RouteTraversalIntent edge in intent.traversalEdges)
            {
                riseIsDerived &=
                    intent.nodes[edge.fromNode].TryGetAbsoluteLevel(edge.fromLayerId, out int fromLevel) &&
                    intent.nodes[edge.toNode].TryGetAbsoluteLevel(edge.toLayerId, out int toLevel) &&
                    edge.requiredRiseLevels == toLevel - fromLevel;
            }

            var junctionIds = new List<string>(intent.junctionNodes.Length);
            foreach (int junction in intent.junctionNodes)
            {
                junctionIds.Add($"{intent.nodes[junction].id}:{intent.adjacency[junction].Count}");
            }

            bool probeLoaded = TryParseRouteTopologyProbe(RouteTopologyProbeJson, out DungeonRouteTopology probe);
            var probeNodeIds = new List<string>(probe?.nodes.Length ?? 0);
            var probeEdgeIds = new List<string>(probe?.edges.Length ?? 0);
            foreach (RouteTopologyNode node in probe?.nodes ?? Array.Empty<RouteTopologyNode>())
            {
                probeNodeIds.Add(node.id);
            }

            foreach (RouteTopologyEdge edge in probe?.edges ?? Array.Empty<RouteTopologyEdge>())
            {
                probeEdgeIds.Add(edge.id);
            }

            // Reformatting the file cannot renumber the graph: the node index
            // order is derived from the declared main/branch orders.
            bool nodeOrderIsDerived =
                TryMutateRouteTopologyProbe(
                    "\"A\": [\"probe-a\", \"arrival\", \"arrival\", 0, { \"main\": 0 }],\n    " +
                    "\"B\": [\"probe-b\", \"connector\", \"compression\", 4, { \"main\": 1 }],\n    " +
                    "\"C\": [\"probe-c\", \"culmination\", \"culmination\", 8, { \"main\": 2 }]",
                    "\"C\": [\"probe-c\", \"culmination\", \"culmination\", 8, { \"main\": 2 }],\n    " +
                    "\"B\": [\"probe-b\", \"connector\", \"compression\", 4, { \"main\": 1 }],\n    " +
                    "\"A\": [\"probe-a\", \"arrival\", \"arrival\", 0, { \"main\": 0 }]",
                    out string permutedJson) &&
                TryParseRouteTopologyProbe(permutedJson, out DungeonRouteTopology permuted) &&
                permuted.nodes.Length == 3 &&
                string.Equals(permuted.nodes[0].id, "probe-a", StringComparison.Ordinal) &&
                string.Equals(permuted.nodes[1].id, "probe-b", StringComparison.Ordinal) &&
                string.Equals(permuted.nodes[2].id, "probe-c", StringComparison.Ordinal);

            return string.Join("\n", new[]
            {
                $"graph.pattern={intent.patternId}",
                $"graph.source={intent.topology.sourcePath}",
                $"graph.nodeIds={string.Join("|", nodeIds)}",
                $"graph.edgeIds={string.Join("|", edgeIds)}",
                $"graph.edgeDetails={string.Join("|", edgeDetails)}",
                $"graph.loopEdges={intent.traversalEdges.Length - (intent.nodes.Length - 1)}",
                $"derived.riseFromLevels={riseIsDerived}",
                $"derived.cycleRank={intent.cycleRank}",
                $"derived.cycleCoreNodeCount={intent.cycleCoreNodeCount}",
                $"derived.junctions={string.Join("|", junctionIds)}",
                $"derived.weight={intent.topology.weight}",
                $"selector.weights={TopologyWeightSummary()}",
                $"selector.firstSeed={FirstSeedSelectingTopology(ProcessionalPatternId, BaselineFirstSeed, 2000)}",
                $"contract.probeLoaded={probeLoaded}",
                $"contract.probeNodeIds={string.Join("|", probeNodeIds)}",
                $"contract.probeEdgeIds={string.Join("|", probeEdgeIds)}",
                $"contract.nodeOrderIsDerived={nodeOrderIsDerived}",
                $"contract.duplicateNodeIdRejected={RouteTopologyProbeRejects("\"probe-c\", \"culmination\"", "\"probe-a\", \"culmination\"")}",
                $"contract.pinnedEdgeIdRejected={RouteTopologyProbeRejects("[\"A\", \"B\", \"Stair\"]", "[\"A\", \"B\", \"Stair\", \"main-0-1\"]")}",
                $"contract.legacyBlockRejected={RouteTopologyProbeRejects("\"plannerVersion\": \"probe-v1\",", "\"plannerVersion\": \"probe-v1\",\n  \"legacy\": { \"orientationStreamId\": \"route\" },")}",
                $"contract.spatialSettingsTokenRejected={RouteTopologyProbeRejects("\"spatial\": {", "\"spatial\": { \"settings\": \"baseline\",")}",
                $"contract.invertedLaneGapRejected={RouteTopologyProbeRejects("\"columnGapDeltaCells\": 0", "\"columnGapDeltaCells\": { \"minDelta\": 3, \"maxDelta\": 0 }")}",
                $"contract.absoluteLaneGapRejected={RouteTopologyProbeRejects("\"columnGapDeltaCells\": 0", "\"columnGapCells\": 9")}",
                $"contract.absoluteRoomSizesRejected={RouteTopologyProbeRejects("\"rowGapDeltaCells\": 0", "\"rowGapDeltaCells\": 0, \"roomSizes\": { \"hall\": [5, 5, 5, 5] }")}",
                $"contract.unknownRoomSizeClassRejected={RouteTopologyProbeRejects("\"rowGapDeltaCells\": 0", "\"rowGapDeltaCells\": 0, \"roomSizeDeltaCells\": { \"gallery\": [-4, -4, -4, -4] }")}",
                $"contract.negativeWeightRejected={RouteTopologyProbeRejects("\"plannerVersion\": \"probe-v1\",", "\"plannerVersion\": \"probe-v1\",\n  \"weight\": -1,")}",
                $"contract.unknownEndpointRejected={RouteTopologyProbeRejects("[\"A\", \"B\", \"Stair\"]", "[\"A\", \"Z\", \"Stair\"]")}",
                $"contract.selfEdgeRejected={RouteTopologyProbeRejects("[\"A\", \"B\", \"Stair\"]", "[\"A\", \"A\", \"Stair\"]")}",
                $"contract.parallelEdgeRejected={RouteTopologyProbeRejects("[\"B\", \"C\", \"Stair\"]", "[\"B\", \"A\", \"Stair\"]")}",
                $"contract.unknownTransitionKindRejected={RouteTopologyProbeRejects("[\"A\", \"B\", \"Stair\"]", "[\"A\", \"B\", \"Escalator\"]")}",
                $"contract.mapCellWithoutNodeRejected={RouteTopologyProbeRejects("\"A  B  C\"", "\"A  B  C  D\"")}",
                $"contract.nodeWithoutMapCellRejected={RouteTopologyProbeRejects("{ \"main\": 2 }]\n  }", "{ \"main\": 2 }],\n    \"D\": [\"probe-d\", \"connector\", \"return\", 8, { \"branch\": 0 }]\n  }")}",
                $"contract.slotOnUnknownEdgeRejected={RouteTopologyProbeRejects("\"exit\": \"B-C\"", "\"exit\": \"nope\"")}",
                $"contract.repeatedMainOrderRejected={RouteTopologyProbeRejects("8, { \"main\": 2 }]", "8, { \"main\": 1 }]")}",
                $"contract.unknownAnchorRejected={RouteTopologyProbeRejects("\"top\": \"C\"", "\"top\": \"Z\"")}",
                // Three gaps for three lanes: a per-lane array needs lanes - 1.
                $"contract.laneGapCountRejected={RouteTopologyProbeRejects("\"columnGapDeltaCells\": 0", "\"columnGapDeltaCells\": [0, 0, 0]")}",
                $"contract.idMustMatchFileNameRejected={RouteTopologyProbeRejects("\"id\": \"probe\"", "\"id\": \"not-probe\"")}"
            });
        }

        // A deliberately tiny valid topology. Each contract probe below mutates
        // exactly one thing in it and expects the loader to refuse the result.
        private const string RouteTopologyProbeJson = @"{
  ""id"": ""probe"",
  ""displayName"": ""Loader Probe"",
  ""plannerVersion"": ""probe-v1"",
  ""map"": [""A  B  C""],
  ""spatial"": { ""columnGapDeltaCells"": 0, ""rowGapDeltaCells"": 0 },
  ""nodes"": {
    ""A"": [""probe-a"", ""arrival"", ""arrival"", 0, { ""main"": 0 }],
    ""B"": [""probe-b"", ""connector"", ""compression"", 4, { ""main"": 1 }],
    ""C"": [""probe-c"", ""culmination"", ""culmination"", 8, { ""main"": 2 }]
  },
  ""edges"": [[""A"", ""B"", ""Stair""], [""B"", ""C"", ""Stair""]],
  ""slots"": [{ ""id"": ""probe-slot"", ""at"": ""B"", ""entry"": ""A-B"", ""exit"": ""B-C"" }],
  ""vista"": { ""id"": ""probe-vista"", ""from"": ""C"", ""to"": ""A"", ""minVoidCells"": 3 },
  ""anchors"": { ""bottom"": ""A"", ""top"": ""C"" }
}";

        private static bool TryParseRouteTopologyProbe(string json, out DungeonRouteTopology topology)
        {
            return TryParseRouteTopology(json, "<probe>/probe.json", out topology, out _);
        }

        // A probe that silently failed to apply would pass vacuously, so every
        // mutation reports whether it changed anything.
        private static bool TryMutateRouteTopologyProbe(
            string find,
            string replaceWith,
            out string mutated)
        {
            mutated = RouteTopologyProbeJson.Replace(find, replaceWith);
            return !string.Equals(mutated, RouteTopologyProbeJson, StringComparison.Ordinal);
        }

        private static bool RouteTopologyProbeRejects(string find, string replaceWith)
        {
            return TryMutateRouteTopologyProbe(find, replaceWith, out string mutated) &&
                !TryParseRouteTopologyProbe(mutated, out _);
        }


        private static string BuildRecipeContractSnapshot(int seed)
        {
            RouteIntent intent = BuildDiagnosticRouteIntent(seed);
            DungeonRecipeCatalogService.TryLoadActiveCatalog(
                out ActiveDungeonRecipeCatalog catalog,
                out string catalogError);
            var lines = new List<string>
            {
                $"catalog.valid={catalog != null}",
                $"catalog.error={catalogError}",
                $"catalog.activeCount={catalog?.recipes.Length ?? 0}",
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
                RouteIntent recipeIntent = intent;
                foreach (RecipeSlotIntent candidate in intent.recipeSlots)
                {
                    if (string.Equals(candidate.recipe.recipeId, recipe.recipeId, StringComparison.Ordinal))
                    {
                        slot = candidate;
                        break;
                    }
                }

                if (slot == null)
                {
                    using (IDisposable previewScope =
                           DungeonRecipeCatalogService.BeginAuthoringPreview(
                               recipe,
                               out string previewError))
                    {
                        if (previewScope == null)
                        {
                            lines.Add($"{prefix}.previewError={previewError}");
                        }
                        else
                        {
                            recipeIntent = BuildDiagnosticRouteIntent(seed);
                            foreach (RecipeSlotIntent candidate in recipeIntent.recipeSlots)
                            {
                                if (string.Equals(
                                        candidate.recipe.recipeId,
                                        recipe.recipeId,
                                        StringComparison.Ordinal))
                                {
                                    slot = candidate;
                                    break;
                                }
                            }
                        }
                    }
                }

                lines.Add($"{prefix}.id={recipe.recipeId}");
                lines.Add($"{prefix}.schema={recipe.schemaVersion}");
                lines.Add($"{prefix}.slotNode={slot?.slotNode ?? -1}");
                lines.Add($"{prefix}.orientationBinding={slot?.orientationBinding.ToString() ?? string.Empty}");
                lines.Add($"{prefix}.disabledForGeneration={recipe.disabledForGeneration}");
                lines.Add($"{prefix}.currentValid={validation.Passed}");
                lines.Add($"{prefix}.schemaValid={validation.LayerPassed(DungeonRecipeValidationLayer.Schema)}");
                lines.Add($"{prefix}.structureValid={validation.LayerPassed(DungeonRecipeValidationLayer.Structure)}");
                lines.Add($"{prefix}.variationValid={validation.LayerPassed(DungeonRecipeValidationLayer.Variation)}");
                lines.Add($"{prefix}.neighborValid={validation.LayerPassed(DungeonRecipeValidationLayer.Neighbor)}");
                lines.Add($"{prefix}.ports={recipe.ports.Length}");
                lines.Add($"{prefix}.transitions={recipe.transitions.Length}");
                lines.Add($"{prefix}.symmetryPairs={recipe.symmetryPairs.Length}");
                lines.Add($"{prefix}.variations={recipe.variations.Length}");
                AppendIsolatedRecipeEvidence(lines, prefix, seed, recipeIntent, slot);
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
                            // This probe sweeps the recipe's own alternatives via
                            // the layout-attempt index; the route-shape attempt is
                            // not what it varies.
                            shapeAttempt: 0,
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

        private static string BuildRecipeFullDungeonSnapshot(int seed)
        {
            JObject report = BuildSeedReport(seed);
            JObject renderer = JObject.Parse(BuildRendererProbeJson(seed));
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
                int approachCellCount = 0;
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

                foreach (JToken port in token["ports"] as JArray ?? new JArray())
                {
                    approachCellCount += (port["approachCells"] as JArray)?.Count ?? 0;
                }

                lines.Add(SnapshotLine($"{prefix}.id", token["id"]));
                lines.Add(SnapshotLine($"{prefix}.atomic", token["atomicAndValid"]));
                lines.Add(SnapshotLine($"{prefix}.roomIndex", token["roomIndex"]));
                lines.Add(SnapshotLine($"{prefix}.primaryAxis", token["primaryAxis"]));
                lines.Add(SnapshotLine($"{prefix}.ports", token["ports"] is JArray ports ? ports.Count : 0));
                lines.Add(SnapshotLine($"{prefix}.portsBound", token["mandatoryPortsBound"]));
                lines.Add(SnapshotLine($"{prefix}.approachCells", approachCellCount));
                lines.Add(SnapshotLine($"{prefix}.transitions", token["transitions"] is JArray transitions ? transitions.Count : 0));
                lines.Add(SnapshotLine($"{prefix}.reservationsComplete", token["reservationsComplete"]));
                lines.Add(SnapshotLine(
                    $"{prefix}.showpieceRequiredFloorCells",
                    token["showpieceRequiredFloorCells"] is JArray requiredFloor ? requiredFloor.Count : 0));
                lines.Add(SnapshotLine(
                    $"{prefix}.showpieceWallMarginCells",
                    token["showpieceWallMarginCells"] is JArray wallMargins ? wallMargins.Count : 0));
                lines.Add(SnapshotLine(
                    $"{prefix}.showpieceBackdropVoidCells",
                    token["showpieceBackdropVoidCells"] is JArray backdropVoid ? backdropVoid.Count : 0));
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

        private static string BuildRecipeAvailabilitySnapshot(int seed)
        {
            DungeonRecipeCatalogService.TryLoadActiveCatalog(
                out ActiveDungeonRecipeCatalog catalog,
                out string catalogError);
            DungeonRecipeAsset source = null;
            catalog?.TryGet(VestibuleRecipeFixtureId, out source);
            string before = EditorJsonUtility.ToJson(source);
            DungeonRecipeValidationResult validation = DungeonRecipeValidator.ValidateContract(source);
            string after = EditorJsonUtility.ToJson(source);

            string sourceDigest = DungeonRecipeValidator.ComputeContentDigest(source);
            var edited = Instantiate(source);
            edited.hideFlags = HideFlags.HideAndDontSave;
            edited.contentVersion++;
            bool editedDigestChanged = !string.Equals(
                sourceDigest,
                DungeonRecipeValidator.ComputeContentDigest(edited),
                StringComparison.Ordinal);

            var disabled = Instantiate(source);
            disabled.hideFlags = HideFlags.HideAndDontSave;
            disabled.disabledForGeneration = true;
            bool disabledCatalogValid = DungeonRecipeCatalogService.TryBuildActiveCatalog(
                new[] { disabled },
                out ActiveDungeonRecipeCatalog disabledCatalog,
                out string disabledReason);

            var invalid = Instantiate(source);
            invalid.hideFlags = HideFlags.HideAndDontSave;
            invalid.disabledForGeneration = false;
            invalid.transitions[0].upperLandingCells = Array.Empty<Vector2Int>();
            DungeonRecipeValidationResult invalidValidation =
                DungeonRecipeValidator.ValidateContract(invalid);
            bool invalidCatalogValid = DungeonRecipeCatalogService.TryBuildActiveCatalog(
                new[] { invalid },
                out _,
                out string invalidReason);

            var fresh = ScriptableObject.CreateInstance<DungeonRecipeAsset>();
            fresh.hideFlags = HideFlags.HideAndDontSave;
            bool freshDisabled = fresh.disabledForGeneration;

            DestroyImmediate(edited);
            DestroyImmediate(disabled);
            DestroyImmediate(invalid);
            DestroyImmediate(fresh);
            return string.Join("\n", new[]
            {
                $"catalog.error={catalogError}",
                $"validation.passed={validation.Passed}",
                $"validation.nonMutating={string.Equals(before, after, StringComparison.Ordinal)}",
                $"source.enabled={source != null && !source.disabledForGeneration}",
                $"source.digestLength={sourceDigest.Length}",
                $"edited.digestChanged={editedDigestChanged}",
                $"disabled.catalogValid={disabledCatalogValid}",
                $"disabled.catalogReason={disabledReason}",
                $"disabled.activeCount={disabledCatalog?.recipes.Length ?? -1}",
                $"invalid.catalogValid={invalidCatalogValid}",
                $"invalid.catalogReason={invalidReason}",
                $"invalid.structurePassed={invalidValidation.LayerPassed(DungeonRecipeValidationLayer.Structure)}",
                $"fresh.disabledForGeneration={freshDisabled}"
            });
        }

        private static string BuildRecipePoolSelectionSnapshot(int seed)
        {
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out string rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            RouteIntent intent = BuildDiagnosticSelectedRouteIntent(seed);
            lastRouteIntent = intent;
            JObject firstProjection = BuildRouteIntentProjection();
            JObject secondProjection = BuildRouteIntentProjection();
            var lines = new List<string>
            {
                $"catalog.activeCount={catalog.recipes.Length}",
                $"catalog.digest={catalog.digest}",
                $"route.recipeSlotCount={intent.recipeSlots.Length}",
                $"report.repeatable={string.Equals(firstProjection.ToString(Formatting.None), secondProjection.ToString(Formatting.None), StringComparison.Ordinal)}",
                $"report.hash={ComputeSha256(firstProjection.ToString(Formatting.None))}"
            };

            foreach (RecipeSlotIntent slot in intent.recipeSlots)
            {
                RouteNodeIntent node = intent.nodes[slot.slotNode];
                var rejected = new List<string>(slot.rejectedCandidates.Length);
                foreach (RecipeCandidateRejection candidate in slot.rejectedCandidates)
                {
                    rejected.Add($"{candidate.recipeId}:{candidate.reasonCode}");
                }

                string prefix = $"slot.{slot.slotId}";
                lines.Add($"{prefix}.node={node.id}");
                lines.Add($"{prefix}.role={node.role}");
                lines.Add($"{prefix}.beat={node.beat}");
                lines.Add($"{prefix}.catalogDigestMatches={string.Equals(slot.catalogDigest, catalog.digest, StringComparison.Ordinal)}");
                lines.Add($"{prefix}.candidates={string.Join(",", slot.compatibleCandidateIds)}");
                lines.Add($"{prefix}.rejected={string.Join(",", rejected)}");
                lines.Add($"{prefix}.selected={slot.recipe.recipeId}");
                lines.Add($"{prefix}.stream={slot.selectionStreamIdentity}");
            }

            DungeonRecipeAsset landmarkOnly = null;
            catalog.TryGet(ThroneRecipeFixtureId, out landmarkOnly);
            var incompatibleCatalog = new ActiveDungeonRecipeCatalog(
                new[] { landmarkOnly },
                "diagnostic-incompatible-catalog");
            RouteIntent unresolved = BuildSelectedRouteIntent(
                seed,
                Array.Empty<RecipeSlotIntent>(),
                string.Empty);
            bool noCandidateRejected = !TryResolveRequiredRecipeSlots(
                incompatibleCatalog,
                unresolved,
                out _,
                out string noCandidateReason);
            lines.Add($"noCandidate.rejected={noCandidateRejected}");
            lines.Add($"noCandidate.reason={noCandidateReason}");
            return string.Join("\n", lines);
        }

        private static string BuildRecipePoolProofSnapshot(int seed)
        {
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog catalog,
                    out string rejectionReason) ||
                !catalog.TryGet(ExampleRecipeFixtureId, out DungeonRecipeAsset exampleRecipe))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            DungeonRecipeValidationResult contract =
                DungeonRecipeValidator.ValidateContract(exampleRecipe);
            bool firstGalleryPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                exampleRecipe,
                seed,
                out string firstGalleryPath,
                out string firstGalleryMessage);
            JObject firstGallery = firstGalleryPassed
                ? JObject.Parse(File.ReadAllText(firstGalleryPath))
                : new JObject();
            bool secondGalleryPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                exampleRecipe,
                seed,
                out string secondGalleryPath,
                out string secondGalleryMessage);
            JObject secondGallery = secondGalleryPassed
                ? JObject.Parse(File.ReadAllText(secondGalleryPath))
                : new JObject();
            var galleryKinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken entry in firstGallery["entries"] as JArray ?? new JArray())
            {
                galleryKinds.Add(entry.Value<string>("kind") ?? string.Empty);
            }

            const int corpusFirstSeed = 2026072100;
            const int corpusSeedCount = 50;
            var firstSelections = new HashSet<string>(StringComparer.Ordinal);
            var secondSelections = new HashSet<string>(StringComparer.Ordinal);
            var firstRows = new List<string>(corpusSeedCount);
            var secondRows = new List<string>(corpusSeedCount);
            int firstAccepted = 0;
            int secondAccepted = 0;
            bool nonTargetSelectionsPreserved = true;
            string firstCandidates = string.Empty;
            for (int offset = 0; offset < corpusSeedCount; offset++)
            {
                int corpusSeed = corpusFirstSeed + offset;
                JObject first = BuildSeedReport(corpusSeed);
                JObject second = BuildSeedReport(corpusSeed);
                firstAccepted += first.Value<bool?>("accepted") == true ? 1 : 0;
                secondAccepted += second.Value<bool?>("accepted") == true ? 1 : 0;
                JObject firstCompression = FindRecipeSlotProjection(
                    first["routeIntent"]?["recipeSlots"] as JArray,
                    CompressionRecipeSlotId);
                JObject secondCompression = FindRecipeSlotProjection(
                    second["routeIntent"]?["recipeSlots"] as JArray,
                    CompressionRecipeSlotId);
                JObject firstLandmark = FindRecipeSlotProjection(
                    first["routeIntent"]?["recipeSlots"] as JArray,
                    LandmarkRecipeSlotId);
                JObject secondLandmark = FindRecipeSlotProjection(
                    second["routeIntent"]?["recipeSlots"] as JArray,
                    LandmarkRecipeSlotId);
                JObject firstReturn = FindRecipeSlotProjection(
                    first["routeIntent"]?["recipeSlots"] as JArray,
                    ReturnRecipeSlotId);
                JObject secondReturn = FindRecipeSlotProjection(
                    second["routeIntent"]?["recipeSlots"] as JArray,
                    ReturnRecipeSlotId);
                string firstSelection = firstCompression?.Value<string>("id") ?? string.Empty;
                string secondSelection = secondCompression?.Value<string>("id") ?? string.Empty;
                firstSelections.Add(firstSelection);
                secondSelections.Add(secondSelection);
                if (offset == 0)
                {
                    var candidates = new List<string>();
                    foreach (JToken candidate in
                             firstCompression?["compatibleCandidateIds"] as JArray ??
                             new JArray())
                    {
                        candidates.Add(candidate.Value<string>() ?? string.Empty);
                    }

                    firstCandidates = string.Join(",", candidates);
                }

                nonTargetSelectionsPreserved &=
                    string.Equals(
                        firstLandmark?.Value<string>("id"),
                        ThroneRecipeFixtureId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        secondLandmark?.Value<string>("id"),
                        ThroneRecipeFixtureId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        firstReturn?.Value<string>("id"),
                        CornerReturnRecipeFixtureId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        secondReturn?.Value<string>("id"),
                        CornerReturnRecipeFixtureId,
                        StringComparison.Ordinal);
                firstRows.Add(RecipePoolSeedRow(corpusSeed, firstSelection, first));
                secondRows.Add(RecipePoolSeedRow(corpusSeed, secondSelection, second));
            }

            bool withoutExampleResolved = TryResolveSliceDDisabledScenario(
                catalog,
                new[] { ExampleRecipeFixtureId },
                seed,
                out int withoutExampleActiveCount,
                out string withoutExampleCompression,
                out string withoutExampleLandmark,
                out string withoutExampleReturn,
                out string withoutExampleReason);
            bool withoutVestibuleResolved = TryResolveSliceDDisabledScenario(
                catalog,
                new[] { VestibuleRecipeFixtureId },
                seed,
                out int withoutVestibuleActiveCount,
                out string withoutVestibuleCompression,
                out string withoutVestibuleLandmark,
                out string withoutVestibuleReturn,
                out string withoutVestibuleReason);
            bool withoutBothResolved = TryResolveSliceDDisabledScenario(
                catalog,
                new[] { ExampleRecipeFixtureId, VestibuleRecipeFixtureId },
                seed,
                out int withoutBothActiveCount,
                out string withoutBothCompression,
                out _,
                out _,
                out string withoutBothReason);

            string firstDigest = ComputeSha256(string.Join("\n", firstRows));
            string secondDigest = ComputeSha256(string.Join("\n", secondRows));
            JObject previewContext = firstGallery["previewContext"] as JObject;
            return string.Join("\n", new[]
            {
                $"catalog.activeCount={catalog.recipes.Length}",
                $"catalog.digest={catalog.digest}",
                $"recipe.id={exampleRecipe.recipeId}",
                $"recipe.kind={exampleRecipe.kind}",
                $"recipe.disabledForGeneration={exampleRecipe.disabledForGeneration}",
                $"recipe.contract={contract.Passed}",
                $"recipe.schema={contract.LayerPassed(DungeonRecipeValidationLayer.Schema)}",
                $"recipe.structure={contract.LayerPassed(DungeonRecipeValidationLayer.Structure)}",
                $"recipe.variation={contract.LayerPassed(DungeonRecipeValidationLayer.Variation)}",
                $"recipe.neighbor={contract.LayerPassed(DungeonRecipeValidationLayer.Neighbor)}",
                $"recipe.transitionImplementation={(exampleRecipe.motifs.Length == 1 ? exampleRecipe.motifs[0].implementationId : string.Empty)}",
                $"gallery.firstPassed={firstGalleryPassed}",
                $"gallery.secondPassed={secondGalleryPassed}",
                $"gallery.samePath={string.Equals(firstGalleryPath, secondGalleryPath, StringComparison.Ordinal)}",
                $"gallery.sameHash={string.Equals(firstGallery.Value<string>("galleryHash"), secondGallery.Value<string>("galleryHash"), StringComparison.Ordinal)}",
                $"gallery.isolated={galleryKinds.IsSupersetOf(new[] { "contract", "top_down", "player_height", "below_floor" })}",
                $"gallery.neighbor={galleryKinds.Contains("neighbor")}",
                $"gallery.canonical={firstGallery["fullDungeon"]?.Value<bool?>("canonicalPlan") == true}",
                $"gallery.renderer={firstGallery["fullDungeon"]?.Value<bool?>("renderer") == true}",
                $"gallery.abyss={firstGallery["fullDungeon"]?.Value<bool?>("abyssSupport") == true}",
                $"gallery.collision={firstGallery["fullDungeon"]?.Value<bool?>("collision") == true}",
                $"gallery.message={firstGalleryMessage}",
                $"gallery.secondMessage={secondGalleryMessage}",
                $"context.forced={previewContext?.Value<bool?>("forced") == true}",
                $"context.recipeId={previewContext?.Value<string>("forcedRecipeId") ?? string.Empty}",
                $"context.slotId={previewContext?.Value<string>("recipeSlotId") ?? string.Empty}",
                $"corpus.firstSeed={corpusFirstSeed}",
                $"corpus.seedCount={corpusSeedCount}",
                $"corpus.firstAccepted={firstAccepted}",
                $"corpus.secondAccepted={secondAccepted}",
                $"corpus.firstSelections={string.Join(",", firstSelections.OrderBy(value => value, StringComparer.Ordinal))}",
                $"corpus.secondSelections={string.Join(",", secondSelections.OrderBy(value => value, StringComparer.Ordinal))}",
                $"corpus.candidates={firstCandidates}",
                $"corpus.firstDigest={firstDigest}",
                $"corpus.secondDigest={secondDigest}",
                $"corpus.repeatable={string.Equals(firstDigest, secondDigest, StringComparison.Ordinal)}",
                $"corpus.nonTargetSelectionsPreserved={nonTargetSelectionsPreserved}",
                $"withoutExample.resolved={withoutExampleResolved}",
                $"withoutExample.activeCount={withoutExampleActiveCount}",
                $"withoutExample.compression={withoutExampleCompression}",
                $"withoutExample.landmark={withoutExampleLandmark}",
                $"withoutExample.return={withoutExampleReturn}",
                $"withoutExample.reason={withoutExampleReason}",
                $"withoutVestibule.resolved={withoutVestibuleResolved}",
                $"withoutVestibule.activeCount={withoutVestibuleActiveCount}",
                $"withoutVestibule.compression={withoutVestibuleCompression}",
                $"withoutVestibule.landmark={withoutVestibuleLandmark}",
                $"withoutVestibule.return={withoutVestibuleReturn}",
                $"withoutVestibule.reason={withoutVestibuleReason}",
                $"withoutBoth.resolved={withoutBothResolved}",
                $"withoutBoth.activeCount={withoutBothActiveCount}",
                $"withoutBoth.compression={withoutBothCompression}",
                $"withoutBoth.reason={withoutBothReason}"
            });
        }

        private static JObject FindRecipeSlotProjection(JArray slots, string slotId)
        {
            foreach (JToken slot in slots ?? new JArray())
            {
                if (string.Equals(
                        slot.Value<string>("recipeSlotId"),
                        slotId,
                        StringComparison.Ordinal))
                {
                    return slot as JObject;
                }
            }

            return null;
        }

        private static string RecipePoolSeedRow(int seed, string selection, JObject report)
        {
            return string.Join("|", new[]
            {
                seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                selection ?? string.Empty,
                report["hashes"]?.Value<string>("routeIntent") ?? string.Empty,
                report["hashes"]?.Value<string>("recipeResolutions") ?? string.Empty,
                report["hashes"]?.Value<string>("canonical") ?? string.Empty
            });
        }

        private static bool TryResolveSliceDDisabledScenario(
            ActiveDungeonRecipeCatalog sourceCatalog,
            IReadOnlyCollection<string> disabledRecipeIds,
            int seed,
            out int activeCount,
            out string compressionRecipeId,
            out string landmarkRecipeId,
            out string returnRecipeId,
            out string rejectionReason)
        {
            activeCount = 0;
            compressionRecipeId = string.Empty;
            landmarkRecipeId = string.Empty;
            returnRecipeId = string.Empty;
            rejectionReason = string.Empty;
            var clones = new List<DungeonRecipeAsset>(sourceCatalog.recipes.Length);
            try
            {
                foreach (DungeonRecipeAsset source in sourceCatalog.recipes)
                {
                    DungeonRecipeAsset clone = Instantiate(source);
                    clone.hideFlags = HideFlags.HideAndDontSave;
                    clone.disabledForGeneration =
                        disabledRecipeIds.Contains(source.recipeId);
                    clones.Add(clone);
                }

                if (!DungeonRecipeCatalogService.TryBuildActiveCatalog(
                        clones,
                        out ActiveDungeonRecipeCatalog scenarioCatalog,
                        out rejectionReason))
                {
                    return false;
                }

                activeCount = scenarioCatalog.recipes.Length;
                RouteIntent unresolved = BuildSelectedRouteIntent(
                    seed,
                    Array.Empty<RecipeSlotIntent>(),
                    string.Empty);
                if (!TryResolveRequiredRecipeSlots(
                        scenarioCatalog,
                        unresolved,
                        out RecipeSlotIntent[] slots,
                        out rejectionReason))
                {
                    return false;
                }

                foreach (RecipeSlotIntent slot in slots)
                {
                    if (string.Equals(slot.slotId, CompressionRecipeSlotId, StringComparison.Ordinal))
                        compressionRecipeId = slot.recipe.recipeId;
                    if (string.Equals(slot.slotId, LandmarkRecipeSlotId, StringComparison.Ordinal))
                        landmarkRecipeId = slot.recipe.recipeId;
                    if (string.Equals(slot.slotId, ReturnRecipeSlotId, StringComparison.Ordinal))
                        returnRecipeId = slot.recipe.recipeId;
                }

                return true;
            }
            finally
            {
                foreach (DungeonRecipeAsset clone in clones)
                {
                    DestroyImmediate(clone);
                }
            }
        }

        private static string BuildRecipeAuthoringPreviewIsolationSnapshot(int seed)
        {
            if (!DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog beforeCatalog,
                    out string rejectionReason) ||
                !beforeCatalog.TryGet(
                    VestibuleRecipeFixtureId,
                    out DungeonRecipeAsset source))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            JObject beforeReport = BuildSeedReport(seed);
            DungeonRecipeAsset previewRecipe = Instantiate(source);
            DungeonRecipeAsset incompatibleRecipe = Instantiate(source);
            previewRecipe.hideFlags = HideFlags.HideAndDontSave;
            incompatibleRecipe.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                previewRecipe.recipeId = UnknownDisabledPreviewFixtureId;
                previewRecipe.displayName = "Slice C Unknown Disabled Preview";
                previewRecipe.disabledForGeneration = true;
                bool firstPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                    previewRecipe,
                    seed,
                    out string firstPath,
                    out string firstMessage);
                JObject firstManifest = firstPassed
                    ? JObject.Parse(File.ReadAllText(firstPath))
                    : new JObject();
                bool secondPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                    previewRecipe,
                    seed,
                    out string secondPath,
                    out string secondMessage);
                JObject secondManifest = secondPassed
                    ? JObject.Parse(File.ReadAllText(secondPath))
                    : new JObject();

                incompatibleRecipe.recipeId = "preview_incompatible_connector_slice_c_01";
                incompatibleRecipe.displayName = "Slice C Incompatible Disabled Preview";
                incompatibleRecipe.disabledForGeneration = true;
                incompatibleRecipe.eligibleRoles = new[] { "preview-only-role" };
                incompatibleRecipe.eligibleBeats = new[] { "preview-only-beat" };
                bool incompatiblePassed =
                    DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                        incompatibleRecipe,
                        seed,
                        out _,
                        out string incompatibleMessage);

                bool afterCatalogValid = DungeonRecipeCatalogService.TryLoadActiveCatalog(
                    out ActiveDungeonRecipeCatalog afterCatalog,
                    out string afterCatalogError);
                JObject afterReport = BuildSeedReport(seed);
                JObject context = firstManifest["previewContext"] as JObject;
                var previewKinds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JToken entry in firstManifest["entries"] as JArray ?? new JArray())
                {
                    previewKinds.Add(entry.Value<string>("kind") ?? string.Empty);
                }

                bool previewCatalogMember =
                    afterCatalog != null &&
                    afterCatalog.TryGet(previewRecipe.recipeId, out _);
                return string.Join("\n", new[]
                {
                    $"preview.recipeId={previewRecipe.recipeId}",
                    $"preview.disabledForGeneration={previewRecipe.disabledForGeneration}",
                    $"preview.catalogMember={previewCatalogMember}",
                    $"preview.firstPassed={firstPassed}",
                    $"preview.secondPassed={secondPassed}",
                    $"preview.samePath={string.Equals(firstPath, secondPath, StringComparison.Ordinal)}",
                    $"preview.sameHash={string.Equals(firstManifest.Value<string>("galleryHash"), secondManifest.Value<string>("galleryHash"), StringComparison.Ordinal)}",
                    $"preview.isolatedEvidence={previewKinds.IsSupersetOf(new[] { "contract", "top_down", "player_height", "below_floor" })}",
                    $"preview.neighborEvidence={previewKinds.Contains("neighbor")}",
                    $"preview.firstMessage={firstMessage}",
                    $"preview.secondMessage={secondMessage}",
                    $"context.forced={context?.Value<bool?>("forced") == true}",
                    $"context.recipeId={context?.Value<string>("forcedRecipeId") ?? string.Empty}",
                    $"context.topologyId={context?.Value<string>("topologyId") ?? string.Empty}",
                    $"context.recipeSlotId={context?.Value<string>("recipeSlotId") ?? string.Empty}",
                    $"context.routeNodeId={context?.Value<string>("routeNodeId") ?? string.Empty}",
                    $"fullDungeon.canonical={firstManifest["fullDungeon"]?.Value<bool?>("canonicalPlan") == true}",
                    $"fullDungeon.renderer={firstManifest["fullDungeon"]?.Value<bool?>("renderer") == true}",
                    $"fullDungeon.abyss={firstManifest["fullDungeon"]?.Value<bool?>("abyssSupport") == true}",
                    $"fullDungeon.collision={firstManifest["fullDungeon"]?.Value<bool?>("collision") == true}",
                    $"incompatible.passed={incompatiblePassed}",
                    $"incompatible.message={incompatibleMessage}",
                    $"ordinary.catalogValid={afterCatalogValid}",
                    $"ordinary.catalogError={afterCatalogError}",
                    $"ordinary.activeCount={afterCatalog?.recipes.Length ?? 0}",
                    $"ordinary.catalogDigestPreserved={string.Equals(beforeCatalog.digest, afterCatalog?.digest, StringComparison.Ordinal)}",
                    $"ordinary.previewAbsentBefore={FindRecipeProjection(beforeReport["recipeResolutions"] as JArray, previewRecipe.recipeId) == null}",
                    $"ordinary.previewAbsentAfter={FindRecipeProjection(afterReport["recipeResolutions"] as JArray, previewRecipe.recipeId) == null}",
                    $"ordinary.routeHashPreserved={string.Equals(beforeReport["hashes"]?.Value<string>("routeIntent"), afterReport["hashes"]?.Value<string>("routeIntent"), StringComparison.Ordinal)}",
                    $"ordinary.canonicalHashPreserved={string.Equals(beforeReport["hashes"]?.Value<string>("canonical"), afterReport["hashes"]?.Value<string>("canonical"), StringComparison.Ordinal)}"
                });
            }
            finally
            {
                DestroyImmediate(previewRecipe);
                DestroyImmediate(incompatibleRecipe);
            }
        }

        private static string BuildRecipeWorkflowSnapshot(int seed)
        {
            DungeonRecipeCatalogService.TryLoadActiveCatalog(
                out ActiveDungeonRecipeCatalog catalog,
                out string catalogError);
            DungeonRecipeAsset throne = null;
            DungeonRecipeAsset vestibule = null;
            DungeonRecipeAsset cornerReturn = null;
            catalog?.TryGet(ThroneRecipeFixtureId, out throne);
            catalog?.TryGet(VestibuleRecipeFixtureId, out vestibule);
            catalog?.TryGet(CornerReturnRecipeFixtureId, out cornerReturn);
            bool firstPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                throne,
                seed,
                out string firstPath,
                out string firstMessage);
            JObject first = firstPassed ? JObject.Parse(File.ReadAllText(firstPath)) : new JObject();
            bool secondPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                throne,
                seed,
                out string secondPath,
                out string secondMessage);
            JObject second = secondPassed ? JObject.Parse(File.ReadAllText(secondPath)) : new JObject();
            bool contrastFirstPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                vestibule,
                seed,
                out string contrastFirstPath,
                out string contrastFirstMessage);
            JObject contrastFirst = contrastFirstPassed
                ? JObject.Parse(File.ReadAllText(contrastFirstPath))
                : new JObject();
            bool contrastSecondPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                vestibule,
                seed,
                out string contrastSecondPath,
                out string contrastSecondMessage);
            JObject contrastSecond = contrastSecondPassed
                ? JObject.Parse(File.ReadAllText(contrastSecondPath))
                : new JObject();
            bool thirdFirstPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                cornerReturn,
                seed,
                out string thirdFirstPath,
                out string thirdFirstMessage);
            JObject thirdFirst = thirdFirstPassed
                ? JObject.Parse(File.ReadAllText(thirdFirstPath))
                : new JObject();
            bool thirdSecondPassed = DungeonRecipeAuthoringService.TryBuildPreviewGallery(
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
        private static string BuildRendererProbeJson(int seed)
        {
            GameObject root = null;
            try
            {
                root = BuildRenderedSeed(
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
                    ThroneRecipeFixtureId);
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

                int expectedPromontoryDeckCells =
                    seedReport["tieredLevelPlan"]?.Value<int?>("promontoryCells") ?? 0;
                expectedPromontoryDeckCells +=
                    seedReport["tieredLevelPlan"]?.Value<int?>("externalConnectorPierCells") ?? 0;
                bool rendererPassed =
                    buildReport.rejected == 0 &&
                    buildReport.floorCells > 0 &&
                    buildReport.transitionEdges > 0 &&
                    buildReport.promontoryDeckCells == expectedPromontoryDeckCells &&
                    visualRecipe?.Value<bool?>("atomicAndValid") == true &&
                    selectedShowpieceCount == 1 &&
                    bounds.size.sqrMagnitude > 0.01f;
                // `missingMeshCount` used to gate this and it measured the wrong
                // thing: a MeshCollider with no mesh is INERT — the exporter
                // filters on `sharedMesh != null` before it builds anything, so
                // such a component never reached the payload. The ~20 in every
                // dungeon are all on the third-party `P_MOD_Gateway_Door_01_*`
                // prefab, and door blocking is manifest-driven anyway
                // (`WorldDoorCollisionRuntime.closed_blocker`), so the count was
                // a property of an FBX import failing a dungeon probe. Still
                // reported below, just not a gate.
                bool collisionPreconditionsPassed = enabledCollisionSources > 0;
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
                        ["promontoryDeckCells"] = buildReport.promontoryDeckCells,
                        ["expectedPromontoryDeckCells"] = expectedPromontoryDeckCells,
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
                    ["density"] = ResolveRequestedDensityLevel(),
                    ["settingsDigest"] = JValue.CreateNull(),
                    ["settings"] = JValue.CreateNull(),
                    ["accepted"] = false,
                    ["failureCode"] = NormalizedRejectionCode(exception.Message, exception),
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
            JObject report = JObject.Parse(BuildRendererProbeJson(seed));
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
            JObject seedReport = BuildSeedReport(seed);
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

            JObject previewContext = null;
            foreach (JToken token in
                     seedReport["routeIntent"]?["recipeSlots"] as JArray ?? new JArray())
            {
                JObject candidateContext = token["authoringPreview"] as JObject;
                if (candidateContext?.Value<bool?>("forced") == true &&
                    string.Equals(
                        candidateContext.Value<string>("recipeId"),
                        recipeId,
                        StringComparison.Ordinal))
                {
                    previewContext = candidateContext;
                    break;
                }
            }

            JObject rendererReport = JObject.Parse(BuildRendererProbeJson(seed));
            bool canonicalValid = seedReport["validation"]?.Value<bool?>("passed") == true;
            bool rendererValid = rendererReport["renderer"]?.Value<bool?>("passed") == true;
            bool boundaryValid = rendererReport["boundary"]?.Value<bool?>("passed") == true;
            bool collisionValid = rendererReport["collisionPreconditions"]?.Value<bool?>("passed") == true;
            bool previewContextValid =
                previewContext?.Value<bool?>("forced") == true &&
                !string.IsNullOrEmpty(previewContext.Value<string>("topologyId")) &&
                !string.IsNullOrEmpty(previewContext.Value<string>("recipeSlotId")) &&
                !string.IsNullOrEmpty(previewContext.Value<string>("routeNodeId"));
            bool fullDungeonValid =
                canonicalValid &&
                rendererValid &&
                boundaryValid &&
                collisionValid &&
                previewContextValid;
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
                collisionValid,
                previewContextValid,
                previewContext?.Value<string>("topologyId"),
                previewContext?.Value<string>("recipeSlotId"),
                previewContext?.Value<string>("routeNodeId"));
            message = fullDungeonValid
                ? $"canonical plan, renderer, abyss boundary, and collision evidence passed in preview context " +
                  $"{previewContext?.Value<string>("topologyId")}/" +
                  $"{previewContext?.Value<string>("recipeSlotId")}/" +
                  $"{previewContext?.Value<string>("routeNodeId")}"
                : $"full-dungeon evidence failed: canonical={canonicalValid}, renderer={rendererValid}, boundary={boundaryValid}, collision={collisionValid}, previewContext={previewContextValid}; " +
                  $"validation={seedReport["validation"]?.ToString(Formatting.None)}; rendererReport={rendererReport.ToString(Formatting.None)}";
            return fullDungeonValid;
        }

        private static JObject CreateAcceptedSeedReport(
            int seed,
            int layoutAttemptsUsed,
            string lastRejection,
            Dictionary<string, int> rejectionHistogram,
            DungeonLayout layout,
            TieredLevelPlan plan,
            DungeonPlanValidation validation)
        {
            JObject canonicalLayout = BuildCanonicalLayoutProjection(layout);
            JObject canonicalPlan = BuildCanonicalTieredLevelPlanProjection(plan);
            JArray existingTransitions = BuildExistingTransitionProjection(plan.transitions, !plan.surfaces.IsSingleLayer);
            JObject preservedCorePlan = BuildPreservedCorePlanProjection(plan);
            JObject preCorrectivePlan = BuildPreCorrectiveTieredLevelPlanProjection(plan);
            JArray recipeResolutions = BuildRecipeResolutionsProjection(plan.recipeResolutions);

            string layoutHash = ComputeSha256(canonicalLayout.ToString(Formatting.None));
            string planHash = ComputeSha256(canonicalPlan.ToString(Formatting.None));
            string existingTransitionHash = ComputeSha256(existingTransitions.ToString(Formatting.None));
            string preservedCorePlanHash = ComputeSha256(preservedCorePlan.ToString(Formatting.None));
            string preCorrectivePlanHash = ComputeSha256(preCorrectivePlan.ToString(Formatting.None));
            string recipeResolutionHash = ComputeSha256(recipeResolutions.ToString(Formatting.None));
            JObject routeIntentProjection = BuildRouteIntentProjection();
            string routeIntentHash = ComputeSha256(routeIntentProjection.ToString(Formatting.None));
            string recipeCatalogDigest = DungeonRecipeCatalogService.TryLoadActiveCatalog(
                out ActiveDungeonRecipeCatalog activeRecipeCatalog,
                out _)
                ? activeRecipeCatalog.digest
                : string.Empty;
            string canonicalHashVersion = DungeonPlanSummaryVersion;
            string canonicalHash = ComputeSha256(
                $"{canonicalHashVersion}\n{routeIntentHash}\n{layoutHash}\n{planHash}");

            float correlation = CalculateDepthLevelCorrelation(layout, plan);
            JObject validationToken = BuildValidationSummaryToken(validation);
            JObject graphSummary = BuildLayoutGraphSummary(layout);
            JObject routePlacement = BuildRoutePlacementProjection(layout);
            JObject densityAdjacencyMeasurements = BuildDensityAdjacencyMeasurements(
                layout,
                plan,
                graphSummary,
                routePlacement,
                routeIntentProjection.Value<string>("patternId") ?? string.Empty);

            var report = new JObject
            {
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["seed"] = seed,
                ["catalogDigest"] = ActiveContentDigest(),
                ["accepted"] = true,
                ["layoutAttempts"] = layoutAttemptsUsed,
                // Tier attempts the accepted plan needed. Size TierPlacementAttempts
                // from this distribution rather than from the historical 32.
                ["tierAttempts"] = lastTierPlacementAttempts,
                ["lastRejectedAttempt"] = string.IsNullOrEmpty(lastRejection) ? null : lastRejection,
                ["lastRejectedAttemptCode"] = string.IsNullOrEmpty(lastRejection)
                    ? null
                    : NormalizedRejectionCode(lastRejection, exception: null),
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
                    // The span-deck inventory, reported because the claim it
                    // settles was previously an assumption: "spans fly over
                    // authored void, so dropping their columns from the port
                    // graph is harmless today". `deckSurfacesOverFloor` is the
                    // number that could have made that false, and it is now
                    // measured per seed instead of reasoned about.
                    ["deckSurfaces"] = CountDeckSurfaces(plan.surfaces, out int decksOverFloor),
                    ["deckSurfacesOverFloor"] = decksOverFloor,
                    // The transition body is a BAND, not a column to the sky.
                    // This counts the surfaces the old whole-column rule deleted
                    // and the band rule keeps — 0 means single-layer generation
                    // never stacks over a stair body, so the narrowing is latent
                    // in production and matters only to multi-layer rooms.
                    ["surfacesOverTransitionBodies"] =
                        CountSurfacesOverTransitionBodies(plan.surfaces, plan.transitions),
                    ["depthLevelCorrelation"] = float.IsNaN(correlation) ? JValue.CreateNull() : new JValue(correlation)
                },
                ["validation"] = validationToken,
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
            report["floorplan"] = BuildFloorplanProjection(layout, plan);
            report["recipeResolutions"] = recipeResolutions;
            report["namedPromontories"] = BuildNamedPromontoryProjection(plan.namedPromontories);
            report["externalConnectors"] = BuildExternalConnectorProjection(plan.externalConnectors);
            report["existingTransitions"] = existingTransitions;
            report["schemaUsage"] = BuildRecipeSchemaUsageProjection();
            ((JObject)report["hashes"])["routeIntent"] = routeIntentHash;

            return report;
        }

        private static JObject CreateRejectedSeedReport(
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
                ["catalogDigest"] = ActiveContentDigest(),
                ["accepted"] = false,
                ["layoutAttempts"] = layoutAttemptsUsed,
                ["lastRejection"] = rejectionReason ?? string.Empty,
                ["lastRejectionCode"] = NormalizedRejectionCode(rejectionReason, exception),
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
            if (lastRouteIntent != null)
            {
                report["routeIntent"] = BuildRouteIntentProjection();
                report["routeBuilderFailureCode"] = lastRouteFailureCode;
            }

            return report;
        }

        private static JObject BuildRouteIntentProjection()
        {
            if (lastRouteIntent == null)
            {
                return new JObject();
            }

            RouteIntent intent = lastRouteIntent;
            var nodes = new JArray();
            var mainRoute = new JArray();
            var branch = new JArray();
            foreach (RouteNodeIntent node in intent.nodes)
            {
                var nodeToken = new JObject
                {
                    ["id"] = node.id,
                    ["role"] = node.role,
                    ["beat"] = node.beat,
                    ["mainRouteOrder"] = node.mainRouteOrder,
                    ["branchOrder"] = node.branchOrder,
                    ["relativeElevationLevels"] = node.relativeElevationLevels,
                    ["recipeSlotId"] = node.recipeSlotId
                };
                // Appended only by a node that declares storeys. This projection
                // is hashed into `routeIntentHash` and from there into every
                // seed's `hashes.canonical`, so an unconditional row would move
                // all 200 seeds for a schema addition that changed no geometry —
                // the same trap, and the same fix, as C2a's recipe layer fields.
                if (node.DeclaresLayers)
                {
                    var layers = new JArray();
                    foreach (RouteTopologyLayer layer in node.layers)
                    {
                        layers.Add(new JObject
                        {
                            ["layerId"] = layer.layerId,
                            ["relativeLevel"] = layer.relativeLevel,
                            ["absoluteLevel"] = node.relativeElevationLevels + layer.relativeLevel
                        });
                    }

                    nodeToken["layers"] = layers;
                }

                nodes.Add(nodeToken);
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
                var edgeToken = new JObject
                {
                    ["id"] = edge.id,
                    ["fromNode"] = intent.nodes[edge.fromNode].id,
                    ["toNode"] = intent.nodes[edge.toNode].id,
                    ["connectionType"] = edge.transitionKind.ToString(),
                    ["requiredRiseLevels"] = edge.requiredRiseLevels,
                    ["laneCount"] = edge.laneCount
                };
                // Conditional for the same hash reason as the node layers above.
                // The absolute levels are reported beside the ids because the ids
                // are room-local and the levels are what every rule compares.
                if (edge.IsLayerBound)
                {
                    edgeToken["fromLayer"] = edge.fromLayerId;
                    edgeToken["toLayer"] = edge.toLayerId;
                    edgeToken["fromAbsoluteLevel"] = edge.fromAbsoluteLevel;
                    edgeToken["toAbsoluteLevel"] = edge.toAbsoluteLevel;
                }

                traversalEdges.Add(edgeToken);
            }

            int loopEdges = intent.traversalEdges.Length - (intent.nodes.Length - 1);
            var junctionNodes = new JArray();
            foreach (int junction in intent.junctionNodes)
            {
                junctionNodes.Add(new JObject
                {
                    ["nodeId"] = intent.nodes[junction].id,
                    ["degree"] = intent.adjacency[junction].Count
                });
            }

            var recipeSlots = new JArray();
            foreach (RecipeSlotIntent slot in intent.recipeSlots)
            {
                recipeSlots.Add(BuildRecipeSlotIntentProjection(intent, slot));
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
                    ["junctionNodes"] = junctionNodes
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

        private static JObject BuildRecipeSlotIntentProjection(
            RouteIntent intent,
            RecipeSlotIntent slot)
        {
            if (intent == null ||
                slot?.recipe == null ||
                slot.slotNode < 0 ||
                slot.slotNode >= intent.nodes.Length)
            {
                return new JObject();
            }

            RouteNodeIntent node = intent.nodes[slot.slotNode];
            var ports = new JArray();
            foreach (DungeonRecipePort port in slot.recipe.ports)
            {
                slot.TryGetEdgeId(port.id, out string edgeId);
                var portProjection = new JObject
                {
                    ["id"] = port.id,
                    ["edgeId"] = edgeId,
                    ["type"] = port.type.ToString(),
                    ["mandatory"] = port.mandatory,
                    ["cell"] = CellToken(port.cell),
                    ["outwardDirection"] = CellToken(port.outwardDirection),
                    ["relativeLevel"] = port.relativeLevel
                };
                if (slot.recipe.UsesIncidentCardinalSockets)
                {
                    portProjection["routeBoundSocket"] = true;
                }

                ports.Add(portProjection);
            }

            var compatibleCandidates = new JArray();
            foreach (string recipeId in slot.compatibleCandidateIds)
            {
                compatibleCandidates.Add(recipeId);
            }

            var rejectedCandidates = new JArray();
            foreach (RecipeCandidateRejection rejection in slot.rejectedCandidates)
            {
                rejectedCandidates.Add(new JObject
                {
                    ["id"] = rejection.recipeId,
                    ["reasonCode"] = rejection.reasonCode
                });
            }

            var projection = new JObject
            {
                ["id"] = slot.recipe.recipeId,
                ["recipeSlotId"] = slot.slotId,
                ["slotNode"] = slot.slotNode,
                ["routeNodeId"] = node.id,
                ["role"] = node.role,
                ["beat"] = node.beat,
                ["catalogDigest"] = slot.catalogDigest,
                ["compatibleCandidateIds"] = compatibleCandidates,
                ["rejectedCandidates"] = rejectedCandidates,
                ["selectedRecipeId"] = slot.recipe.recipeId,
                ["selectionStreamIdentity"] = slot.selectionStreamIdentity,
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
            if (slot.recipe.UsesIncidentCardinalSockets)
            {
                projection["portBindingMode"] = slot.recipe.portBindingMode.ToString();
                projection["minimumActiveSockets"] = slot.recipe.minimumActiveSockets;
                projection["maximumActiveSockets"] = slot.recipe.maximumActiveSockets;
            }

            if (slot.forcedForAuthoringPreview)
            {
                projection["authoringPreview"] = new JObject
                {
                    ["forced"] = true,
                    ["recipeId"] = slot.recipe.recipeId,
                    ["topologyId"] = intent.patternId,
                    ["recipeSlotId"] = slot.slotId,
                    ["routeNodeId"] = node.id
                };
            }

            return projection;
        }

        private static JObject BuildRoutePlacementProjection(DungeonLayout layout)
        {
            var centers = new JArray();
            if (lastRouteIntent != null &&
                lastNodeCenters.Length == lastRouteIntent.nodes.Length)
            {
                DungeonPatternSpatialSettings spatial =
                    ResolveTopologySpatialSettings(lastRouteIntent.topology);
                for (int node = 0; node < lastNodeCenters.Length; node++)
                {
                    centers.Add(new JObject
                    {
                        ["nodeId"] = lastRouteIntent.nodes[node].id,
                        ["center"] = CellToken(lastNodeCenters[node]),
                        ["envelope"] = RectToken(RoomEnvelope(lastNodeCenters[node], spatial))
                    });
                }
            }

            var approaches = new JArray();
            if (lastRouteIntent != null &&
                lastNodeCenters.Length == lastRouteIntent.nodes.Length)
            {
                foreach (RouteTraversalIntent edge in lastRouteIntent.traversalEdges)
                {
                    Vector2Int delta = lastNodeCenters[edge.toNode] - lastNodeCenters[edge.fromNode];
                    var direction = new Vector2Int(Math.Sign(delta.x), Math.Sign(delta.y));
                    approaches.Add(new JObject
                    {
                        ["edgeId"] = edge.id,
                        ["fromApproach"] = CellToken(direction),
                        ["toApproach"] = CellToken(-direction)
                    });
                }
            }

            bool vistaUnobstructedAtLayoutHandoff = lastVistaCells.Length >=
                (lastRouteIntent?.vista.minimumReservedVoidCells ?? int.MaxValue);
            bool reservedVoidPreservedAfterTierLooping = vistaUnobstructedAtLayoutHandoff;
            foreach (Vector2Int cell in lastVistaCells)
            {
                if (layout.floorCells.Contains(cell))
                {
                    reservedVoidPreservedAfterTierLooping = false;
                    break;
                }
            }

            return new JObject
            {
                ["layoutAttempt"] = lastLayoutAttempt,
                ["mainEmbeddingAttempts"] = lastMainEmbeddingAttempts,
                ["corridorLadderRungs"] = HistogramToken(lastCorridorRungCounts),
                ["latticeSlackSpentCells"] = lastLatticeSlackSpentCells,
                ["latticeSlackAvailableCells"] = lastLatticeSlackAvailableCells,
                ["roomInflationAttempts"] = lastRoomInflationAttempts,
                ["annex"] = new JObject
                {
                    ["vacantLatticeCells"] = lastVacantLatticeCellCount,
                    ["annexedRects"] = lastAnnexedRectCount,
                    ["annexedFloorCells"] = lastAnnexedFloorCells,
                    ["moppedRects"] = lastMoppedRectCount,
                    ["moppedFloorCells"] = lastMoppedFloorCells,
                    ["boundaryChambers"] = lastChamberCount
                },
                // §3.2 wants this ceiling sized from the observed maximum rather
                // than guessed, so the observation has to be in the report.
                ["routeShapeAttempts"] = lastRouteShapeAttempts,
                ["nodeCenters"] = centers,
                ["pinnedApproaches"] = approaches,
                ["vista"] = new JObject
                {
                    ["sourceFacing"] = CellToken(lastVistaSourceFacing),
                    ["targetFacing"] = CellToken(lastVistaTargetFacing),
                    ["facingOpposed"] = lastVistaSourceFacing == -lastVistaTargetFacing &&
                        lastVistaSourceFacing != Vector2Int.zero,
                    ["reservedVoidCellCount"] = lastVistaCells.Length,
                    ["reservedVoidCells"] = CellsToken(lastVistaCells, sort: false),
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
                var zoneToken = new JObject
                {
                    ["id"] = zone.id,
                    ["kind"] = zone.kind.ToString(),
                    ["relativeLevel"] = zone.relativeLevel,
                    ["cells"] = CellsToken(zone.cells, sort: false)
                };
                // Only a zone off the base storey carries its layer into the
                // report, so a single-layer resolution projects exactly the
                // shape it always did.
                if (!zone.isBaseLayer)
                {
                    zoneToken["layerRelativeLevel"] = zone.layerRelativeLevel;
                }

                zones.Add(zoneToken);
            }

            var ports = new JArray();
            bool reservationsComplete = true;
            foreach (RecipePortPlacement port in resolution.ports ?? Array.Empty<RecipePortPlacement>())
            {
                Vector2Int[] approachCells = BuildRecipePortApproachReservationCells(port);
                reservationsComplete &= approachCells.Length ==
                    port.widthCells * port.approachDepthCells;
                ports.Add(new JObject
                {
                    ["id"] = port.id,
                    ["edgeId"] = port.edgeId,
                    ["type"] = port.type.ToString(),
                    ["mandatory"] = port.mandatory,
                    ["neighborRoomIndex"] = port.neighborRoomIndex,
                    ["cell"] = CellToken(port.cell),
                    ["outwardDirection"] = CellToken(port.outwardDirection),
                    ["expectedRelativeLevel"] = port.expectedRelativeLevel,
                    ["widthCells"] = port.widthCells,
                    ["approachDepthCells"] = port.approachDepthCells,
                    ["headroomLevels"] = port.headroomLevels,
                    ["approachCells"] = CellsToken(approachCells, sort: false)
                });
            }

            var recipeTransitions = new JArray();
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

            // Conditional, like the layer and endpoint-level rows before it: an
            // unconditional `openings: []` on every resolution would move every
            // seed's `hashes.recipeResolutions` and thence `canonical`, for a
            // schema addition no existing recipe uses.
            JArray recipeOpenings = null;
            foreach (RecipeOpeningPlacement opening in
                     resolution.openings ?? Array.Empty<RecipeOpeningPlacement>())
            {
                recipeOpenings ??= new JArray();
                recipeOpenings.Add(new JObject
                {
                    ["id"] = opening.id,
                    ["cell"] = CellToken(opening.cell),
                    ["direction"] = opening.direction,
                    ["layerRelativeLevel"] = opening.layerRelativeLevel
                });
            }

            if (!string.IsNullOrEmpty(resolution.selectedVisualImplementationId))
            {
                reservationsComplete &=
                    (resolution.showpieceReservation.requiredFloorCells?.Length ?? 0) > 0 &&
                    (resolution.showpieceReservation.wallMarginCells?.Length ?? 0) > 0 &&
                    (resolution.showpieceReservation.backdropVoidCells?.Length ?? 0) > 0;
            }

            var projection = new JObject
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
                ["showpieceRequiredFloorCells"] = CellsToken(
                    resolution.showpieceReservation.requiredFloorCells,
                    sort: false),
                ["showpieceWallMarginCells"] = CellsToken(
                    resolution.showpieceReservation.wallMarginCells,
                    sort: false),
                ["showpieceBackdropVoidCells"] = CellsToken(
                    resolution.showpieceReservation.backdropVoidCells,
                    sort: false),
                ["baseLevel"] = resolution.baseLevel,
                ["atomicAndValid"] = resolution.atomicAndValid,
                ["mandatoryPortsBound"] = resolution.atomicAndValid && ports.Count > 0,
                ["reservationsComplete"] = reservationsComplete,
                ["protectedZonesValid"] = resolution.atomicAndValid && resolution.protectedCells.Length > 0
            };
            if (recipeOpenings != null)
            {
                projection["openings"] = recipeOpenings;
            }

            return projection;
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

            Add("asset.recipeId/schemaVersion/contentVersion", "recipe assets", "stable streams, digest, catalog, diagnostics");
            Add("routeSlot.slotId/node/selectedRecipe", "route topology and catalog selector", "eligibility, room inflation, tier handoff");
            Add("routeSlot.orientationBinding", "catalog selector", "route/vista-bound primary axis");
            Add("zones.walkable", "recipe assets", "atomic room footprint");
            Add("zones.protected", "recipe assets", "late-feature and dressing protection");
            Add("zones.elevated", "recipe assets", "canonical cell levels");
            Add("zones.relativeLevel", "recipe assets", "level and transition validation");
            Add("layers.layerId/relativeLevel/isBase", "recipe assets", "per-storey level derivation and RECIPE_LAYER_CONNECTIVITY");
            Add("zones/ports/transitions.layerId", "recipe assets", "which storey a zone, entrance or stair belongs to; empty is the base");
            Add("openings.cell/outward/layerId", "recipe assets", "bare rims on a stacked storey — the aperture you walk off");
            Add("transitions.atomicGroup", "recipe assets", "atomic transition validation");
            Add("variations/motifs", "recipe assets", "stable StairForge-backed visual selection");
            Add("ports.id/type/mandatory", "recipe assets", "route edge binding and neighbor validation");
            Add("ports.bindingMode/activeSocketRange", "recipe assets and TryPlaceRecipe", "exact named binding or incident-edge cardinal socket activation");
            Add("ports.cell/outward/level", "TryPlaceRecipe", "exact corridor endpoint and tier validation");
            Add("ports.width/approach/headroom", "recipe assets", "planning-time approach reservation and clearance validation");
            Add("placement.primaryAxis/mirror", "TryPlaceRecipe", "orientation, variations, symmetry validation");
            Add("placement.protectedCells", "TryPlaceRecipe", "generic feature exclusions and final validation");
            Add("placement.zoneCells", "TryPlaceRecipe", "canonical levels and structural validation");
            Add("transition.cells/landings/footprint/climb", "TryPlaceRecipe", "PrismLedger, TransitionEdge, headroom, port graph");
            Add("selected variation/visual", "TryPlaceRecipe", "StairForge footprint contract, DaisShowpiece, and renderer");
            Add("disabledForGeneration", "recipe asset", "active catalog admission");
            return new JObject
            {
                ["probeId"] = "dungeon-recipe-v1",
                ["fieldCount"] = fields.Count,
                ["allFieldsConsumed"] = true,
                ["fields"] = fields
            };
        }

        // Thin projection of the shared gate result. The checks themselves live
        // in DungeonLabGenerator.Validation.cs and run inside the accepted tier
        // attempt, so this cannot drift from what generation actually enforced.
        private static JObject BuildValidationSummaryToken(DungeonPlanValidation validation)
        {
            if (validation == null)
            {
                return new JObject
                {
                    ["passed"] = false,
                    ["failureCodes"] = new JArray("VALIDATION_MISSING")
                };
            }

            var token = new JObject
            {
                ["passed"] = validation.passed,
                ["failureCodes"] = new JArray(validation.FailureCodes())
            };
            foreach (DungeonPlanCheck check in validation.checks)
            {
                token[check.id] = CheckToken(check.passed, check.message);
            }

            return token;
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

            int expectedTransitionCount = lastRouteIntent?.traversalEdges?.Length ?? 0;
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
                lastRouteIntent?.recipeSlots == null ||
                plan.recipeResolutions == null ||
                plan.recipeResolutions.Length != lastRouteIntent.recipeSlots.Length)
            {
                message = string.IsNullOrEmpty(message)
                    ? $"expected {lastRouteIntent?.recipeSlots?.Length ?? 0} selected recipe resolutions; found {plan.recipeResolutions?.Length ?? 0}"
                    : message;
                return false;
            }

            foreach (RecipeSlotIntent slot in lastRouteIntent.recipeSlots)
            {
                DungeonRecipeAsset recipe = slot.recipe;
                string requiredId = recipe.recipeId;
                RecipeResolution resolution = FindRecipeResolution(plan.recipeResolutions, requiredId);
                bool portCountValid = resolution.ports != null &&
                    (recipe.UsesIncidentCardinalSockets
                        ? resolution.ports.Length >= recipe.minimumActiveSockets &&
                          resolution.ports.Length <= recipe.maximumActiveSockets
                        : resolution.ports.Length == recipe.ports.Length);
                if (!catalog.TryGet(requiredId, out DungeonRecipeAsset catalogRecipe) ||
                    !ReferenceEquals(recipe, catalogRecipe) ||
                    !resolution.atomicAndValid ||
                    resolution.primaryAxis == Vector2Int.zero ||
                    !portCountValid ||
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

            message = $"three selected recipes resolved atomically with catalog {catalog.digest}";
            return true;
        }

        private static bool TryValidateAcceptedNamedPromontories(
            TieredLevelPlan plan,
            out string message)
        {
            RouteIntent intent = lastRouteIntent;
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
                    !plan.surfaces.TryGetFloorLevel(expectedCell, out int level) ||
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
            // The contract is "1-4 external promontories, at most one per
            // cardinal". The seed's drawn count is the PREFERENCE the resolver
            // starts from and may walk down when the grown core stops offering
            // that many anchors, so this validates the contract rather than the
            // preference — asserting the exact number here is what turned a
            // smaller dungeon mouth into a failed seed.
            int desiredCount = ExternalConnectorDesiredCount(seed);
            if (resolutions.Length < 1 ||
                resolutions.Length > desiredCount ||
                desiredCount < 1 ||
                desiredCount > 4)
            {
                message = $"preferred up to {desiredCount} external connectors; resolved {resolutions.Length}";
                return false;
            }

            var directions = new HashSet<int>();
            var occupied = new HashSet<Vector2Int>();
            RectInt finalExtent = GetCellRect(plan.surfaces.FlooredPlanCells());
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
                    resolution.terminalCell !=
                        resolution.anchorCell + outward * ExternalConnectorAppendageCells ||
                    !IsOnExternalConnectorOuterFace(
                        finalExtent,
                        resolution.terminalCell,
                        resolution.direction) ||
                    plan.surfaces.HasFloor(resolution.terminalCell + outward))
                {
                    message = $"external connector '{resolution.id}' had invalid identity, direction, geometry, or terminal throat";
                    return false;
                }

                for (int index = 0; index < resolution.occupiedCells.Length; index++)
                {
                    Vector2Int cell = resolution.occupiedCells[index];
                    if (!occupied.Add(cell) ||
                        cell != resolution.anchorCell + outward * index ||
                        !plan.surfaces.TryGetFloorLevel(cell, out int level) ||
                        level != resolution.level)
                    {
                        message = $"external connector '{resolution.id}' bent, overlapped another connector, or changed level";
                        return false;
                    }
                }
            }

            List<ElevationEdgeModel.OpenFloorEdge> openEdges =
                BuildExternalConnectorOpenEdges(resolutions);
            // Against the RESOLVED count, not the preferred one: the resolver may
            // have walked down when the grown core stopped offering anchors, and
            // what this has to prove is that every connector it did resolve is a
            // complete, unique, straight run.
            int resolvedCount = resolutions.Length;
            bool passed = directions.Count == resolvedCount &&
                occupied.Count == resolvedCount * (ExternalConnectorAppendageCells + 1) &&
                openEdges.Count == resolvedCount * 2;
            message = passed
                ? $"resolved {resolvedCount} of a preferred {desiredCount} as {ExternalConnectorAppendageCells}-cell straight runs with unique directions, clear terminal throats, and {openEdges.Count} renderer openings"
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

        // Phase B of the layered-topology design (§6 payoff 3, §13, review M4).
        //
        // This used to rebuild the deck heights from the accepted transition
        // list with its OWN copy of the floored-linear span formula — a second
        // implementation of the planner's arithmetic, written because the
        // planning gate ran before three passes that could still move the level
        // field (review H3). Two copies of a formula guarding two different
        // moments is exactly how a rule drifts from itself.
        //
        // The planning gate now runs after the last mutation it must see, and
        // the plan carries the ledger, so acceptance re-runs the identical rule
        // over the identical reservations. Nothing is re-derived and there is
        // one formula left in the generator.
        private static bool TryValidateAcceptedPlanHeadroom(TieredLevelPlan plan, out string rejectionReason)
        {
            bool passed = plan.prisms.TryValidateSurfaceHeadroom(plan.surfaces, out rejectionReason);
            if (passed)
            {
                rejectionReason =
                    $"headroom gate passed for {plan.prisms.HeadroomBearingCellCount} external-span deck cells";
            }

            return passed;
        }

        /// <summary>
        /// How many span-deck surfaces a plan carries, and how many of those
        /// stand over a column that has a floor.
        /// </summary>
        /// <remarks>
        /// The second figure is the interesting one. A deck over a true gap is
        /// scenery; a deck over a floor is a walkway crossing playable geometry,
        /// which is the case the port graph used to delete. Reported per seed so
        /// the corpus answers it rather than an argument.
        /// </remarks>
        private static int CountDeckSurfaces(SurfaceField surfaces, out int overFloor)
        {
            overFloor = 0;
            if (surfaces == null)
            {
                return 0;
            }

            int decks = 0;
            foreach (ElevationEdgeModel.StackedSurface surface in surfaces.StackedSurfaces())
            {
                if (surface.kind != SurfaceKind.Deck)
                {
                    continue;
                }

                decks++;
                if (surfaces.HasFloor(surface.cell))
                {
                    overFloor++;
                }
            }

            return decks;
        }

        private static bool TryValidateRendererInputs(TieredLevelPlan plan, out string message)
        {
            if (plan.surfaces == null || plan.surfaces.Count == 0)
            {
                message = "renderer input had no leveled floor cells";
                return false;
            }

            var renderedPromontoryCells = new HashSet<Vector2Int>(
                CollectRenderedPromontoryCells(
                    plan.namedPromontories,
                    plan.externalConnectors));
            foreach (ExternalConnectorPromontoryResolution resolution in
                     plan.externalConnectors ?? Array.Empty<ExternalConnectorPromontoryResolution>())
            {
                for (int index = 1; index < resolution.occupiedCells.Length; index++)
                {
                    if (!renderedPromontoryCells.Contains(resolution.occupiedCells[index]))
                    {
                        message =
                            $"renderer input omitted external promontory '{resolution.id}' cell " +
                            $"{resolution.occupiedCells[index]}";
                        return false;
                    }
                }
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

            message = $"renderer inputs resolved {plan.surfaces.FlooredCellCount} leveled cells and {prefabPaths.Count} transition/set-piece prefabs";
            return true;
        }

        private static GameObject BuildRenderedSeed(
            int seed,
            out Bounds bounds,
            out JObject seedReport,
            out ElevationEdgeModel.BuildReport buildReport)
        {
            return BuildRenderedSeed(
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
        private static GameObject BuildRenderedSeed(
            int seed,
            out Bounds bounds,
            out JObject seedReport,
            out ElevationEdgeModel.BuildReport buildReport,
            out Vector3 levelFieldOrigin,
            out TieredLevelPlan renderedPlan)
        {
            CurrentGenerationSettings = LoadActiveGenerationSettings(ResolveRequestedDensityLevel());
            var rejectionHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!TryBuildAcceptedPlan(
                    seed,
                    rejectionHistogram,
                    out DungeonLayout layout,
                    out TieredLevelPlan plan,
                    out ElevationEdgeModel.RoomBoundaryContext boundaryContext,
                    out DungeonPlanValidation validation,
                    out int layoutAttemptsUsed,
                    out string rejectionReason))
            {
                throw new InvalidOperationException(
                    $"Sentinel seed {seed} failed after {layoutAttemptsUsed} attempts: " +
                    $"{NormalizedRejectionCode(rejectionReason, exception: null)}: {rejectionReason}");
            }

            // No second generation pass and no boundary-context rebuild: the
            // accepted plan now carries the context that was validated with it,
            // so the report cannot consume shared RNG the render pass then needs.
            seedReport = CreateAcceptedSeedReport(
                seed,
                layoutAttemptsUsed,
                rejectionReason,
                rejectionHistogram,
                layout,
                plan,
                validation);

            levelFieldOrigin = CalculateCenteredLevelFieldOrigin(layout.floorCells, Vector3.zero);
            renderedPlan = plan;
            GameObject root = ElevationEdgeModel.BuildLevelField(
                levelFieldOrigin,
                plan.surfaces.ColumnFloors(),
                plan.surfaces.StackedSurfaces(),
                plan.transitions,
                null,
                BuildPlannedOpenEdges(plan),
                boundaryContext,
                CollectRenderedPromontoryCells(
                    plan.namedPromontories,
                    plan.externalConnectors),
                LoadActiveTrapPlacementSettings(seed),
                "DungeonLab Renderer Probe",
                out buildReport,
                out bounds);
            RequireRenderedPromontoryDecks(plan, buildReport);
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
            TieredLevelPlan plan,
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
                },
                // The density metric proper. floorFillPercent above stays for
                // continuity with the corpus already measured; this is what the
                // density dial is steered and accepted on.
                ["density"] = BuildVoidDensityMeasurements(layout, plan)
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
            var levelCells = new List<Vector2Int>(plan.surfaces.FlooredCells());
            levelCells.Sort(CompareCells);
            var levels = new JArray();
            foreach (Vector2Int cell in levelCells)
            {
                plan.surfaces.TryGetFloorLevel(cell, out int cellLevel);
                levels.Add(new JObject
                {
                    ["cell"] = CellToken(cell),
                    ["level"] = cellLevel
                });
            }

            // The stacked half, and it is CONDITIONAL for the same reason C2a's
            // recipe layer fields are: this projection is hashed into `planHash`
            // and thence into `canonicalHash`, so an unconditional row moves
            // every seed for a schema addition that changed no geometry. A
            // single-layer plan is fully described by the rows above — the
            // column floor IS the only surface — so omitting an empty array
            // loses nothing and keeps 200 seeds byte-identical.
            JArray stackedSurfaces = BuildStackedSurfaceProjection(plan.surfaces);

            JArray transitions = BuildExistingTransitionProjection(plan.transitions, !plan.surfaces.IsSingleLayer);

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

            var projection = new JObject
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
            if (stackedSurfaces != null)
            {
                projection["stackedSurfaces"] = stackedSurfaces;
            }

            return projection;
        }

        /// <summary>
        /// The surfaces standing ABOVE their column floors, or null when there
        /// are none.
        /// </summary>
        /// <remarks>
        /// Null rather than an empty array on purpose. This feeds `planHash` and
        /// therefore `canonicalHash`, and C2a's lesson was that an unconditional
        /// append moves every seed's hash for a schema addition that changed no
        /// geometry — so the row is emitted only by a plan that actually stacks.
        /// A single-layer plan is completely described without it.
        /// </remarks>
        private static JArray BuildStackedSurfaceProjection(SurfaceField surfaces)
        {
            if (surfaces == null || surfaces.IsSingleLayer)
            {
                return null;
            }

            var stacked = new JArray();
            foreach (ElevationEdgeModel.StackedSurface surface in surfaces.StackedSurfaces())
            {
                stacked.Add(new JObject
                {
                    ["cell"] = CellToken(surface.cell),
                    ["level"] = surface.level,
                    ["kind"] = surface.kind.ToString()
                });
            }

            return stacked;
        }

        /// <summary>
        /// The accepted transition list, with endpoint levels only when the plan
        /// stacks.
        /// </summary>
        /// <remarks>
        /// CONDITIONAL for the same reason C2a's recipe layer fields are: this
        /// projection is hashed into `planHash` and thence into `canonicalHash`,
        /// so unconditional rows would move every seed's hash for a schema
        /// addition that changed no geometry. While every column carries one
        /// surface a transition's levels are recoverable from its cells and the
        /// `cellLevels` rows beside it, so omitting them loses nothing; a
        /// cross-layer edge is the case that genuinely needs stating, and it can
        /// only exist on a stacked plan.
        /// </remarks>
        private static JArray BuildExistingTransitionProjection(
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> source,
            bool includeLevels)
        {
            var transitions = new JArray();
            for (int index = 0; index < (source?.Count ?? 0); index++)
            {
                ElevationEdgeModel.TransitionEdge transition = source[index];
                var token = new JObject
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
                };
                if (includeLevels)
                {
                    token["firstLevel"] = transition.firstLevel;
                    token["secondLevel"] = transition.secondLevel;
                }

                transitions.Add(token);
            }

            return transitions;
        }

        private static JObject BuildPreservedCorePlanProjection(TieredLevelPlan plan)
        {
            var externalPierCells = new HashSet<Vector2Int>(
                CollectExternalConnectorPierCells(plan.externalConnectors));
            var cells = new List<Vector2Int>(plan.surfaces.FlooredCells());
            cells.Sort(CompareCells);
            var levels = new JArray();
            foreach (Vector2Int cell in cells)
            {
                if (externalPierCells.Contains(cell))
                    continue;

                plan.surfaces.TryGetFloorLevel(cell, out int cellLevel);
                levels.Add(new JObject
                {
                    ["cell"] = CellToken(cell),
                    ["level"] = cellLevel
                });
            }

            return new JObject
            {
                ["cellLevelsBeforeExternalConnectors"] = levels,
                ["transitions"] = BuildExistingTransitionProjection(plan.transitions, !plan.surfaces.IsSingleLayer),
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
            // FlooredCells(), not FlooredPlanCells(): backing-store order, because
            // this dictionary's own enumeration order is observable downstream —
            // it is handed to the port-graph rebuild, whose node insertion order
            // reaches a diagnostic string. Same reason the shadow reconcile does
            // not sort.
            var coreLevels = new Dictionary<Vector2Int, int>();
            foreach (Vector2Int cell in plan.surfaces.FlooredCells())
            {
                if (!externalPierCells.Contains(cell) &&
                    plan.surfaces.TryGetFloorLevel(cell, out int cellLevel))
                {
                    coreLevels[cell] = cellLevel;
                }
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

            // A synthetic single-layer view by construction — the accepted plan
            // minus the external appendage — so wrapping it is what these metrics
            // saw before, not a stacked question in disguise.
            var coreSurfaces = new SurfaceField(coreLevels);
            if (!TryBuildFloorStairPortGraph(
                    coreSurfaces,
                    plan.transitions,
                    out FloorStairPortGraph corePortGraph,
                    out string graphError))
            {
                throw new InvalidOperationException(
                    $"Could not reconstruct pre-corrective port graph: {graphError}");
            }

            GetLevelRange(coreSurfaces, out int coreMinLevel, out int coreMaxLevel);

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
                ["transitions"] = BuildExistingTransitionProjection(plan.transitions, !plan.surfaces.IsSingleLayer),
                ["levelCount"] = CountDistinctLevels(coreSurfaces),
                ["minLevel"] = coreMinLevel,
                ["maxLevel"] = coreMaxLevel,
                ["roomsPerTierSummary"] = plan.roomsPerTierSummary,
                ["overlookCount"] = CountSpatialOverlookEdges(coreSurfaces, plan.transitions),
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
                if (depths[i] < 0 || !TryGetRoomLevel(layout.rooms[i], plan.surfaces, out int level))
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

        // A depth-vs-level scatter sample: "roughly how high does this room
        // sit". The column floor is the room's own storey; a gallery inside it
        // would be a second sample, which this stat does not model.
        private static bool TryGetRoomLevel(RoomFootprint room, SurfaceField surfaces, out int level)
        {
            if (surfaces.TryGetFloorLevel(room.Center, out level))
            {
                return true;
            }

            foreach (Vector2Int cell in room.CellsRowMajor())
            {
                if (surfaces.TryGetFloorLevel(cell, out level))
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
            var voidByTopology = new SortedDictionary<string, VoidDensityAccumulator>(StringComparer.Ordinal);
            var voidCorpus = new VoidDensityAccumulator();
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

                if (!voidByTopology.TryGetValue(topology, out VoidDensityAccumulator voidAccumulator))
                {
                    voidAccumulator = new VoidDensityAccumulator();
                    voidByTopology[topology] = voidAccumulator;
                }

                var density = measurements["density"] as JObject;
                AccumulateVoidDensity(density, voidAccumulator);
                AccumulateVoidDensity(density, voidCorpus);

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
                    },
                    ["density"] = voidByTopology.TryGetValue(
                        entry.Key,
                        out VoidDensityAccumulator topologyVoid)
                        ? BuildVoidDensitySummary(topologyVoid)
                        : BuildVoidDensitySummary(new VoidDensityAccumulator())
                };
            }

            return new JObject
            {
                ["measurementVersion"] = "density-adjacency-v1",
                ["density"] = BuildVoidDensitySummary(voidCorpus),
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

        private static string WriteBatchReport(
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
            List<int> tierAttemptCounts)
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
                ["catalogDigest"] = ActiveContentDigest(),
                ["firstSeed"] = firstSeed,
                ["lastSeed"] = firstSeed + seedCount - 1,
                ["seedCount"] = seedCount,
                ["accepted"] = successCount,
                ["failed"] = seedCount - successCount,
                ["hardValid"] = hardValidCount,
                ["attemptDistribution"] = attemptDistribution,
                ["acceptedAttemptDistribution"] = BuildIntDistribution(acceptedAttemptCounts),
                ["tierAttemptDistribution"] = BuildIntDistribution(tierAttemptCounts),
                ["archetypes"] = archetypes,
                ["topologySelection"] = new JObject
                {
                    ["method"] = "weighted-registry-draw-v1",
                    ["weights"] = TopologyWeightSummary(),
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

            Directory.CreateDirectory(BatchReportDirectory);
            string reportPath = Path.Combine(
                BatchReportDirectory,
                $"dungeon_plan_{firstSeed}_{firstSeed + seedCount - 1}.json");
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
                    string code = NormalizedRejectionCode(entry.Key, exception: null);
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

        private static string NormalizedRejectionCode(string reason, Exception exception)
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

        private static string ActiveContentDigest()
        {
            string settingsDigest = GenerationSettingsDigest(CurrentGenerationSettings);
            if (ActiveContentDigestCache.TryGetValue(settingsDigest, out string cachedDigest))
            {
                return cachedDigest;
            }

            string[] paths =
            {
                GenerationProfilePath,
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
            ActiveContentDigestCache[settingsDigest] = digest;
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

        // Straight down, orthographic, square: void reads as void from here and
        // from nowhere else. Deliberately the SAME lighting and clear colour as
        // the three-quarter capture, so the pair can be compared side by side.
        private static void CaptureTopDownSentinelImage(Bounds bounds, string path)
        {
            CaptureDiagnosticReviewImage(
                path,
                SentinelTopDownImageSize,
                SentinelTopDownImageSize,
                camera =>
                {
                    float halfExtent = Mathf.Max(
                        8f,
                        Mathf.Max(bounds.extents.x, bounds.extents.z));
                    float height = bounds.extents.y + halfExtent * 4f + 32f;
                    camera.orthographic = true;
                    // A little margin so the outermost promontory is not clipped
                    // against the frame, which is exactly where they live.
                    camera.orthographicSize = halfExtent * 1.08f + 2f;
                    camera.transform.position = bounds.center + Vector3.up * height;
                    camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    camera.nearClipPlane = 0.1f;
                    camera.farClipPlane = height + bounds.size.magnitude + 64f;
                });
        }

        private static void CaptureSentinelImage(Bounds bounds, string path)
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
            CaptureDiagnosticReviewImage(
                path,
                SentinelImageWidth,
                SentinelImageHeight,
                configureCamera);
        }

        private static void CaptureDiagnosticReviewImage(
            string path,
            int width,
            int height,
            Action<Camera> configureCamera)
        {
            var cameraObject = new GameObject("DungeonLab Sentinel Camera")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var lightObject = new GameObject("DungeonLab Sentinel Light")
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
            var renderTexture = new RenderTexture(width, height, 24);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
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
                    new Rect(0, 0, width, height),
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

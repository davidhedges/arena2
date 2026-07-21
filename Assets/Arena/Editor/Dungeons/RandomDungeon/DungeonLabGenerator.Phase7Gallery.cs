using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // Phase 7 gallery support renders only the already-locked review corpus.
    // It writes blinded diagnostic evidence and never participates in planning,
    // canonical hashing, production rendering, collision export, or scoring.
    internal sealed partial class DungeonLabGenerator
    {
        private const string Phase7GalleryVersion = "phase7-curated-gallery-v1";
        private const string Phase7GalleryDirectoryName = "phase7_curated_gallery";
        private const int Phase7GalleryViewCountPerFloor = 3;
        private const int Phase7GalleryCaptureLayer = 31;
        private const float Phase7GalleryPlayerEyeHeight = 1.65f;
        private const float Phase7GalleryPlayerFieldOfView = 70f;
        private const float Phase7GalleryCellSize = 4f;

        private static readonly string[] Phase7GalleryViewNames =
        {
            "overview",
            "arrival_threshold",
            "landmark_vista_approach"
        };

        private static readonly string[] Phase7GalleryFloorCriteria =
        {
            "route_readability",
            "entrance_threshold_clarity",
            "vertical_circulation_legibility",
            "focal_hierarchy",
            "overlooks_and_distant_views",
            "landmark_room_and_approach_relationship",
            "intentional_without_feeling_preassembled"
        };

        private sealed class Phase7GalleryItem
        {
            internal Phase7ReviewSelection selection;
            internal string reviewId;
            internal string orderKey;
            internal string anonymousTopologyGroup;
        }

        [MenuItem("Tools/Dungeon Lab/Phase 7/Generate Blinded Curated Gallery")]
        public static void GeneratePhase7CuratedGallery()
        {
            JObject sweepReport = LoadPhase7AcceptedSweepReport();
            JObject sentinelManifest = LoadPhase7SentinelManifest();
            List<Phase7ReviewSelection> selections =
                BuildPhase7CollisionReviewSelection(sweepReport, sentinelManifest);
            RequirePassingPhase7CollisionSelection(selections);
            List<Phase7GalleryItem> items = BuildPhase7GalleryItems(selections);

            string finalDirectory = Path.Combine(BatchReportDirectory, Phase7GalleryDirectoryName);
            if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite existing Phase 7 gallery evidence at '{finalDirectory}'.");
            }

            string stagingDirectory = Path.Combine(
                BatchReportDirectory,
                $".{Phase7GalleryDirectoryName}_staging_{Guid.NewGuid():N}");
            string reviewDirectory = Path.Combine(stagingDirectory, "review_packet");
            string imageDirectory = Path.Combine(reviewDirectory, "images");
            var trackedSnapshots = new Dictionary<string, Phase7TrackedArtifactSnapshot>(StringComparer.Ordinal);
            var cleanupFailures = new List<string>();
            bool trackedArtifactsRestored = false;
            GameObject root = null;

            try
            {
                CapturePhase7TrackedArtifact(Phase7SynthesizedStairLogPath, trackedSnapshots);
                Directory.CreateDirectory(imageDirectory);

                var reviewFloors = new JArray();
                var internalFloors = new JArray();
                for (int index = 0; index < items.Count; index++)
                {
                    Phase7GalleryItem item = items[index];
                    EditorUtility.DisplayProgressBar(
                        "Dungeon Lab Phase 7 curated gallery",
                        $"Rendering {item.reviewId} ({index + 1}/{items.Count})",
                        (float)index / items.Count);

                    try
                    {
                        root = BuildPhase0RenderedSeed(
                            item.selection.seed,
                            out Bounds bounds,
                            out JObject seedReport,
                            out ElevationEdgeModel.BuildReport buildReport,
                            out Vector3 levelFieldOrigin,
                            out TieredLevelPlan plan);
                        RequirePhase7GalleryRenderedSeed(item, seedReport, buildReport);
                        SetPhase7GalleryLayer(root.transform, Phase7GalleryCaptureLayer);

                        string overviewPath = Phase7GalleryImagePath(
                            imageDirectory,
                            item.reviewId,
                            Phase7GalleryViewNames[0]);
                        string arrivalPath = Phase7GalleryImagePath(
                            imageDirectory,
                            item.reviewId,
                            Phase7GalleryViewNames[1]);
                        string vistaPath = Phase7GalleryImagePath(
                            imageDirectory,
                            item.reviewId,
                            Phase7GalleryViewNames[2]);

                        CapturePhase7GalleryOverview(bounds, overviewPath);
                        ResolvePhase7ArrivalCamera(
                            plan,
                            levelFieldOrigin,
                            buildReport.levelHeight,
                            out Vector3 arrivalPosition,
                            out Vector3 arrivalTarget);
                        CapturePhase7GalleryPlayerView(arrivalPosition, arrivalTarget, bounds, arrivalPath);
                        ResolvePhase7VistaCamera(
                            plan,
                            levelFieldOrigin,
                            buildReport.levelHeight,
                            out Vector3 vistaPosition,
                            out Vector3 vistaTarget);
                        CapturePhase7GalleryPlayerView(vistaPosition, vistaTarget, bounds, vistaPath);

                        reviewFloors.Add(BuildPhase7GalleryReviewFloor(
                            item,
                            reviewDirectory,
                            overviewPath,
                            arrivalPath,
                            vistaPath));
                        internalFloors.Add(BuildPhase7GalleryInternalFloor(item, seedReport));
                    }
                    finally
                    {
                        if (root != null)
                        {
                            DestroyImmediate(root);
                            root = null;
                        }
                    }
                }

                WritePhase7GalleryReviewMaterials(reviewDirectory, items, reviewFloors);
                WritePhase7GalleryInternalManifest(stagingDirectory, items, internalFloors);

                RestorePhase7TrackedArtifacts(trackedSnapshots, cleanupFailures);
                trackedArtifactsRestored = true;
                if (cleanupFailures.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Phase 7 gallery generation could not restore tracked artifacts: " +
                        string.Join("; ", cleanupFailures));
                }

                Directory.Move(stagingDirectory, finalDirectory);
                Debug.Log(
                    $"Dungeon Lab Phase 7: generated {items.Count} blinded floors and " +
                    $"{items.Count * Phase7GalleryViewCountPerFloor} scored images at '{finalDirectory}'.");
            }
            finally
            {
                if (root != null)
                    DestroyImmediate(root);
                EditorUtility.ClearProgressBar();

                if (!trackedArtifactsRestored)
                {
                    RestorePhase7TrackedArtifacts(trackedSnapshots, cleanupFailures);
                }

                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
        }

        private static void RequirePassingPhase7CollisionSelection(
            IReadOnlyList<Phase7ReviewSelection> selections)
        {
            string path = Phase7CollisionReportPath();
            if (!File.Exists(path))
                throw new FileNotFoundException("Passing collision evidence is required before gallery generation.", path);

            JObject report = JObject.Parse(File.ReadAllText(path));
            JArray expectedSelection = BuildPhase7SelectionToken(selections);
            if (report.Value<bool?>("passed") != true ||
                !string.Equals(
                    report.Value<string>("reportVersion"),
                    Phase7CollisionReportVersion,
                    StringComparison.Ordinal) ||
                report.Value<int?>("seedCount") != Phase7CollisionReviewSeedCount ||
                !JToken.DeepEquals(report["selection"], expectedSelection))
            {
                throw new InvalidOperationException(
                    "Phase 7 collision evidence is not passing for the exact locked gallery corpus.");
            }
        }

        private static List<Phase7GalleryItem> BuildPhase7GalleryItems(
            IReadOnlyList<Phase7ReviewSelection> selections)
        {
            if (selections == null ||
                selections.Count != Phase7CollisionReviewSeedCount ||
                selections.Select(selection => selection.seed).Distinct().Count() != Phase7CollisionReviewSeedCount)
            {
                throw new InvalidOperationException("The Phase 7 gallery requires exactly 30 unique locked selections.");
            }

            Dictionary<string, string> anonymousGroups = Phase7ReviewPatternOrder
                .OrderBy(pattern => ComputeSha256($"{Phase7GalleryVersion}:group:{pattern}"), StringComparer.Ordinal)
                .Select((pattern, index) => new { pattern, group = $"Group {Convert.ToChar('A' + index)}" })
                .ToDictionary(item => item.pattern, item => item.group, StringComparer.Ordinal);

            List<Phase7GalleryItem> ordered = selections
                .Select(selection => new Phase7GalleryItem
                {
                    selection = selection,
                    // Canonical hashes are uniformly distributed and already
                    // locked by the accepted corpus, so sorting by them supplies
                    // a fixed randomized order without a new random stream.
                    orderKey = selection.expectedCanonicalHash,
                    anonymousTopologyGroup = string.Equals(
                        selection.source,
                        "phase7-sweep",
                        StringComparison.Ordinal)
                        ? anonymousGroups[selection.patternId]
                        : string.Empty
                })
                .OrderBy(item => item.orderKey, StringComparer.Ordinal)
                .ThenBy(item => item.selection.seed)
                .ToList();
            for (int index = 0; index < ordered.Count; index++)
                ordered[index].reviewId = $"F{index + 1:00}";

            return ordered;
        }

        private static void RequirePhase7GalleryRenderedSeed(
            Phase7GalleryItem item,
            JObject seedReport,
            ElevationEdgeModel.BuildReport buildReport)
        {
            string actualHash = seedReport?["hashes"]?.Value<string>("canonical") ?? string.Empty;
            if (!string.Equals(actualHash, item.selection.expectedCanonicalHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Gallery floor {item.reviewId} did not reproduce its locked canonical hash.");
            }

            if (seedReport?["validation"]?.Value<bool?>("passed") != true || buildReport.rejected != 0)
            {
                throw new InvalidOperationException(
                    $"Gallery floor {item.reviewId} was not hard-valid with a rejection-free renderer report.");
            }
        }

        private static void SetPhase7GalleryLayer(Transform transform, int layer)
        {
            transform.gameObject.layer = layer;
            for (int child = 0; child < transform.childCount; child++)
                SetPhase7GalleryLayer(transform.GetChild(child), layer);
        }

        private static string Phase7GalleryImagePath(string directory, string reviewId, string viewName)
        {
            return Path.Combine(directory, $"{reviewId}_{viewName}.png");
        }

        private static void CapturePhase7GalleryOverview(Bounds bounds, string path)
        {
            CaptureDiagnosticReviewImage(path, camera =>
            {
                Vector3 center = bounds.center;
                float radius = Mathf.Max(16f, bounds.extents.magnitude);
                Vector3 direction = new Vector3(-0.85f, -0.68f, -0.95f).normalized;
                camera.transform.position = center - direction * (radius * 1.7f);
                camera.transform.LookAt(center + Vector3.up * Mathf.Max(1.5f, bounds.extents.y * 0.1f));
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = Mathf.Max(250f, radius * 8f);
                camera.fieldOfView = 35f;
                camera.cullingMask = 1 << Phase7GalleryCaptureLayer;
            });
        }

        private static void CapturePhase7GalleryPlayerView(
            Vector3 position,
            Vector3 target,
            Bounds bounds,
            string path)
        {
            CaptureDiagnosticReviewImage(path, camera =>
            {
                if ((target - position).sqrMagnitude < 0.01f)
                    throw new InvalidOperationException("Phase 7 player-height capture had no view direction.");

                camera.transform.position = position;
                camera.transform.rotation = Quaternion.LookRotation((target - position).normalized, Vector3.up);
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = Mathf.Max(250f, bounds.size.magnitude * 4f);
                camera.fieldOfView = Phase7GalleryPlayerFieldOfView;
                camera.cullingMask = 1 << Phase7GalleryCaptureLayer;
            });
        }

        private static void ResolvePhase7ArrivalCamera(
            TieredLevelPlan plan,
            Vector3 origin,
            float levelHeight,
            out Vector3 position,
            out Vector3 target)
        {
            RouteIntent intent = phase1LastRouteIntent ??
                throw new InvalidOperationException("Gallery capture had no route intent.");
            int firstEdgeIndex = Array.FindIndex(intent.traversalEdges, edge =>
                edge.fromNode == intent.bottomNode || edge.toNode == intent.bottomNode);
            if (firstEdgeIndex < 0)
                throw new InvalidOperationException("Gallery capture could not resolve the arrival threshold edge.");

            RouteTraversalIntent firstEdge = intent.traversalEdges[firstEdgeIndex];
            int nextNode = firstEdge.fromNode == intent.bottomNode
                ? firstEdge.toNode
                : firstEdge.toNode == intent.bottomNode
                    ? firstEdge.fromNode
                    : -1;
            if (nextNode < 0)
                throw new InvalidOperationException("Gallery capture could not resolve the arrival threshold edge.");

            ResolvePhase7RecipeApproachCamera(
                plan,
                firstEdge.id,
                nextNode,
                origin,
                levelHeight,
                out position,
                out target);
        }

        private static void ResolvePhase7VistaCamera(
            TieredLevelPlan plan,
            Vector3 origin,
            float levelHeight,
            out Vector3 position,
            out Vector3 target)
        {
            RouteRequirementResolution vista = plan.routeRequirementResolution;
            if (!vista.finalVistaValid)
                throw new InvalidOperationException("Gallery capture requires the accepted plan's final valid vista.");

            if (plan.namedPromontories.Length > 0)
            {
                position = Phase7GalleryPlayerPosition(
                    vista.vistaSourceCell,
                    vista.vistaSourceLevel,
                    origin,
                    levelHeight);
                target = Phase7GalleryPlayerPosition(
                    vista.vistaTargetCell,
                    vista.vistaTargetLevel,
                    origin,
                    levelHeight);
                return;
            }

            RouteIntent intent = phase1LastRouteIntent ??
                throw new InvalidOperationException("Gallery capture had no route intent.");
            int landmarkNode = intent.vista.targetNode;
            int landmarkOrder = intent.nodes[landmarkNode].mainRouteOrder;
            int predecessorNode = Array.FindIndex(intent.nodes, node =>
                node.mainRouteOrder == landmarkOrder - 1);
            int approachEdgeIndex = Array.FindIndex(intent.traversalEdges, edge =>
                edge.fromNode == predecessorNode && edge.toNode == landmarkNode ||
                edge.fromNode == landmarkNode && edge.toNode == predecessorNode);
            if (predecessorNode < 0 || approachEdgeIndex < 0)
                throw new InvalidOperationException("Gallery capture could not resolve the landmark approach edge.");

            ResolvePhase7RecipeApproachCamera(
                plan,
                intent.traversalEdges[approachEdgeIndex].id,
                landmarkNode,
                origin,
                levelHeight,
                out position,
                out target);
        }

        private static void ResolvePhase7RecipeApproachCamera(
            TieredLevelPlan plan,
            string edgeId,
            int recipeRoomIndex,
            Vector3 origin,
            float levelHeight,
            out Vector3 position,
            out Vector3 target)
        {
            int recipeIndex = Array.FindIndex(plan.recipeResolutions, recipe =>
                recipe.roomIndex == recipeRoomIndex);
            if (recipeIndex < 0)
                throw new InvalidOperationException($"Gallery capture route edge '{edgeId}' had no resolved recipe room.");

            RecipeResolution recipe = plan.recipeResolutions[recipeIndex];
            int portIndex = Array.FindIndex(recipe.ports, port =>
                string.Equals(port.edgeId, edgeId, StringComparison.Ordinal));
            if (portIndex < 0)
                throw new InvalidOperationException($"Gallery capture route edge '{edgeId}' had no resolved recipe port.");

            RecipePortPlacement port = recipe.ports[portIndex];
            if (port.outwardDirection == Vector2Int.zero)
                throw new InvalidOperationException($"Gallery capture route edge '{edgeId}' had no port direction.");

            Vector2Int cameraCell = port.cell + port.outwardDirection;
            if (!plan.cellLevels.ContainsKey(cameraCell))
                throw new InvalidOperationException($"Gallery capture route edge '{edgeId}' had no exterior approach cell.");

            Vector2Int fartherCell = cameraCell + port.outwardDirection;
            if (plan.cellLevels.TryGetValue(fartherCell, out int fartherLevel) &&
                plan.cellLevels[cameraCell] == fartherLevel)
            {
                cameraCell = fartherCell;
            }

            Vector2Int interiorTargetCell = port.cell - port.outwardDirection;
            if (!plan.cellLevels.ContainsKey(interiorTargetCell))
                interiorTargetCell = port.cell;

            position = Phase7GalleryPlayerPosition(plan, cameraCell, origin, levelHeight);
            target = Phase7GalleryPlayerPosition(plan, interiorTargetCell, origin, levelHeight);
        }

        private static Vector3 Phase7GalleryPlayerPosition(
            TieredLevelPlan plan,
            Vector2Int cell,
            Vector3 origin,
            float levelHeight)
        {
            if (!plan.cellLevels.TryGetValue(cell, out int level))
                throw new InvalidOperationException($"Gallery capture cell {cell} had no canonical floor level.");
            return Phase7GalleryPlayerPosition(cell, level, origin, levelHeight);
        }

        private static Vector3 Phase7GalleryPlayerPosition(
            Vector2Int cell,
            int level,
            Vector3 origin,
            float levelHeight)
        {
            return origin + new Vector3(
                (cell.x + 0.5f) * Phase7GalleryCellSize,
                level * levelHeight + Phase7GalleryPlayerEyeHeight,
                (cell.y + 0.5f) * Phase7GalleryCellSize);
        }

        private static JObject BuildPhase7GalleryReviewFloor(
            Phase7GalleryItem item,
            string reviewDirectory,
            params string[] imagePaths)
        {
            var views = new JObject();
            for (int index = 0; index < Phase7GalleryViewNames.Length; index++)
            {
                string relativePath = Path.GetRelativePath(reviewDirectory, imagePaths[index]).Replace('\\', '/');
                views[Phase7GalleryViewNames[index]] = new JObject
                {
                    ["image"] = relativePath,
                    ["sha256"] = Phase7GalleryFileSha256(imagePaths[index])
                };
            }

            return new JObject
            {
                ["reviewOrder"] = int.Parse(item.reviewId.Substring(1)),
                ["floorId"] = item.reviewId,
                ["views"] = views
            };
        }

        private static JObject BuildPhase7GalleryInternalFloor(
            Phase7GalleryItem item,
            JObject seedReport)
        {
            return new JObject
            {
                ["floorId"] = item.reviewId,
                ["seed"] = item.selection.seed,
                ["source"] = item.selection.source,
                ["patternId"] = item.selection.patternId,
                ["selectionSlot"] = item.selection.selectionSlot,
                ["canonicalHash"] = item.selection.expectedCanonicalHash,
                ["fixedRandomOrderKey"] = item.orderKey,
                ["anonymousTopologyGroup"] = string.IsNullOrEmpty(item.anonymousTopologyGroup)
                    ? JValue.CreateNull()
                    : JToken.FromObject(item.anonymousTopologyGroup),
                ["layoutAttempts"] = seedReport.Value<int?>("layoutAttempts"),
                ["transitionCount"] = item.selection.transitionCount >= 0
                    ? JToken.FromObject(item.selection.transitionCount)
                    : JValue.CreateNull(),
                ["visibleDistantRoomProxyCount"] = item.selection.visibilityCount >= 0
                    ? JToken.FromObject(item.selection.visibilityCount)
                    : JValue.CreateNull(),
                ["hasNamedPromontory"] = item.selection.hasNamedPromontory
            };
        }

        private static void WritePhase7GalleryReviewMaterials(
            string reviewDirectory,
            IReadOnlyList<Phase7GalleryItem> items,
            JArray floors)
        {
            var manifest = new JObject
            {
                ["galleryVersion"] = Phase7GalleryVersion,
                ["blinded"] = true,
                ["floorCount"] = items.Count,
                ["viewsPerFloor"] = Phase7GalleryViewCountPerFloor,
                ["capture"] = new JObject
                {
                    ["width"] = Phase0SentinelImageWidth,
                    ["height"] = Phase0SentinelImageHeight,
                    ["overview"] = "existing fixed Phase 0 sentinel framing",
                    ["playerHeightMeters"] = Phase7GalleryPlayerEyeHeight,
                    ["playerFieldOfViewDegrees"] = Phase7GalleryPlayerFieldOfView,
                    ["arrivalDirection"] = "first declared route edge through its resolved threshold recipe port",
                    ["landmarkVistaDirection"] = "named-promontory vista source-to-target when rendered; otherwise the resolved landmark entry port"
                },
                ["criteria"] = new JArray(Phase7GalleryFloorCriteria),
                ["scale"] = Phase7GalleryScaleToken(),
                ["floors"] = floors
            };
            File.WriteAllText(
                Path.Combine(reviewDirectory, "manifest.json"),
                manifest.ToString(Formatting.Indented));
            File.WriteAllText(Path.Combine(reviewDirectory, "README.md"), BuildPhase7GalleryReviewerInstructions());

            string floorSheet = BuildPhase7GalleryFloorScoreSheet(items);
            string repetitionSheet = BuildPhase7GalleryRepetitionScoreSheet(items);
            foreach (string reviewer in new[] { "reviewer_1", "reviewer_2" })
            {
                File.WriteAllText(Path.Combine(reviewDirectory, $"STEP_1_{reviewer}_floor_scores.csv"), floorSheet);
                File.WriteAllText(Path.Combine(reviewDirectory, $"STEP_2_{reviewer}_repetition_scores.csv"), repetitionSheet);
            }
        }

        private static void WritePhase7GalleryInternalManifest(
            string stagingDirectory,
            IReadOnlyList<Phase7GalleryItem> items,
            JArray floors)
        {
            var groups = new JArray(items
                .Where(item => !string.IsNullOrEmpty(item.anonymousTopologyGroup))
                .GroupBy(item => item.anonymousTopologyGroup, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new JObject
                {
                    ["anonymousGroup"] = group.Key,
                    ["patternId"] = group.Select(item => item.selection.patternId).Distinct().Single(),
                    ["floorIds"] = new JArray(group.Select(item => item.reviewId))
                }));
            var manifest = new JObject
            {
                ["galleryVersion"] = Phase7GalleryVersion,
                ["warning"] = "INTERNAL ONLY: do not give this identity/metric mapping to reviewers before scoring is complete.",
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["orderRule"] = "ascending locked canonical SHA-256, then seed only as an impossible-hash-tie fallback",
                ["selectionCount"] = items.Count,
                ["anonymousTopologyGroups"] = groups,
                ["floors"] = floors
            };
            File.WriteAllText(
                Path.Combine(stagingDirectory, "INTERNAL_identity_manifest.json"),
                manifest.ToString(Formatting.Indented));
        }

        private static JObject Phase7GalleryScaleToken()
        {
            return new JObject
            {
                ["1"] = "unusable or contradictory",
                ["2"] = "major revision required",
                ["3"] = "acceptable for ordinary use",
                ["4"] = "strong",
                ["5"] = "exemplary"
            };
        }

        private static string BuildPhase7GalleryFloorScoreSheet(IReadOnlyList<Phase7GalleryItem> items)
        {
            var result = new StringBuilder();
            result.Append("review_order,floor_id,")
                .Append(string.Join(",", Phase7GalleryFloorCriteria))
                .Append(",notes\n");
            for (int index = 0; index < items.Count; index++)
            {
                result.Append(index + 1).Append(',').Append(items[index].reviewId)
                    .Append(new string(',', Phase7GalleryFloorCriteria.Length + 1))
                    .Append('\n');
            }
            return result.ToString();
        }

        private static string BuildPhase7GalleryRepetitionScoreSheet(IReadOnlyList<Phase7GalleryItem> items)
        {
            var result = new StringBuilder("scope,floor_ids,repetition_score,notes\n");
            foreach (IGrouping<string, Phase7GalleryItem> group in items
                         .Where(item => !string.IsNullOrEmpty(item.anonymousTopologyGroup))
                         .GroupBy(item => item.anonymousTopologyGroup, StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                result.Append(group.Key.Replace(' ', '_').ToLowerInvariant())
                    .Append(",\"")
                    .Append(string.Join(" ", group.Select(item => item.reviewId)))
                    .Append("\",,\n");
            }
            result.Append("overall,\"")
                .Append(string.Join(" ", items.Select(item => item.reviewId)))
                .Append("\",,\n");
            return result.ToString();
        }

        private static string BuildPhase7GalleryReviewerInstructions()
        {
            return string.Join("\n", new[]
            {
                "# Phase 7 blinded curated review",
                "",
                "This packet contains 30 floors in one fixed randomized order. Each floor has three unannotated images: overview, player-height arrival/threshold, and player-height landmark/vista approach.",
                "",
                "## Who reviews",
                "",
                "Two people familiar with third-person traversal review independently. At least one must not have implemented the dungeon generator. Do not discuss scores until both reviewers finish STEP 1 and STEP 2.",
                "",
                "## Complete steps",
                "",
                "1. Use only this `review_packet` directory. Do not open `INTERNAL_identity_manifest.json`; it contains the hidden seed, topology, historical-control, selection, and metric mapping.",
                "2. Choose one unused reviewer number. Open its `STEP_1_reviewer_N_floor_scores.csv` in a spreadsheet.",
                "3. Review F01 through F30 in order. For each floor, inspect all three matching images in `images/`, then enter one integer from 1 through 5 for every criterion. Add a short note for any score of 1 or 2.",
                "4. After all 30 floor rows are complete, open the matching `STEP_2_reviewer_N_repetition_scores.csv`. Score repetition for each anonymous eight-floor topology group and then for the complete 30-floor gallery. The group labels deliberately do not reveal topology names.",
                "5. Save both CSV files under new names that identify the reviewer. Keep the original empty templates unchanged, and send the two completed copies to the person compiling the Phase 7 decision.",
                "6. Only after both reviewers have submitted may they compare scores. Any permitted joint re-review must be documented without changing the original submitted files.",
                "",
                "## Scale",
                "",
                "- 1: unusable or contradictory",
                "- 2: major revision required",
                "- 3: acceptable for ordinary use",
                "- 4: strong",
                "- 5: exemplary",
                "",
                "## Floor criteria",
                "",
                "1. Route readability",
                "2. Entrance and threshold clarity",
                "3. Vertical circulation legibility",
                "4. Focal hierarchy",
                "5. Usefulness of overlooks and distant views",
                "6. Relationship between landmark rooms and their approaches",
                "7. Whether the floor feels intentional without feeling preassembled",
                "",
                "## Locked passing rule (do not reinterpret after scoring)",
                "",
                "No score of 1 is permitted. Both reviewers must score every floor at least 3 for route readability, entrance/threshold clarity, and vertical-circulation legibility. Any 2 on another criterion requires documented joint re-review; an unresolved 2 fails. Every floor's combined average must be at least 3.25, at least 24 of 30 floors must average at least 3.5, and every floor criterion must average at least 3.5 across the gallery. Repetition must receive at least 3 from each reviewer for every anonymous topology group and overall, with a combined overall mean of at least 3.5.",
                "",
                "The packet contains no route overlays. If a route overlay is later supplied, it is diagnostic-only and must not change these scored observations.",
                ""
            });
        }

        private static string Phase7GalleryFileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(File.ReadAllBytes(path));
                var result = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                    result.Append(value.ToString("x2"));
                return result.ToString();
            }
        }

        // Reflection entry point for focused gallery-boundary tests. It creates
        // no scene object, image, score, or report artifact.
        private static string BuildPhase7CuratedGallerySupportSnapshot()
        {
            List<Phase7ReviewSelection> selections = BuildPhase7CollisionReviewSelection(
                BuildSyntheticPhase7CollisionSweep(),
                BuildSyntheticPhase7SentinelManifest());
            List<Phase7GalleryItem> items = BuildPhase7GalleryItems(selections);
            bool reviewNamesBlinded = items.All(item =>
                !item.reviewId.Contains(item.selection.seed.ToString(), StringComparison.Ordinal) &&
                !Phase7GalleryViewNames.Any(view =>
                    $"{item.reviewId}_{view}.png".Contains(item.selection.patternId, StringComparison.Ordinal)));
            return string.Join("\n", new[]
            {
                $"gallery.version={Phase7GalleryVersion}",
                $"gallery.floorCount={items.Count}",
                $"gallery.uniqueIds={items.Select(item => item.reviewId).Distinct().Count()}",
                $"gallery.viewsPerFloor={Phase7GalleryViewNames.Length}",
                $"gallery.imageCount={items.Count * Phase7GalleryViewNames.Length}",
                $"gallery.width={Phase0SentinelImageWidth}",
                $"gallery.height={Phase0SentinelImageHeight}",
                $"gallery.playerEyeHeight={Phase7GalleryPlayerEyeHeight}",
                $"gallery.playerFov={Phase7GalleryPlayerFieldOfView}",
                $"gallery.criteria={Phase7GalleryFloorCriteria.Length}",
                $"gallery.orderFixed={items.Select(item => item.orderKey).SequenceEqual(items.Select(item => item.orderKey).OrderBy(value => value, StringComparer.Ordinal))}",
                $"gallery.reviewNamesBlinded={reviewNamesBlinded}",
                $"gallery.historicalControls={items.Count(item => item.selection.source == "historical-sentinel")}",
                $"gallery.topologyGroups={items.Where(item => !string.IsNullOrEmpty(item.anonymousTopologyGroup)).Select(item => item.anonymousTopologyGroup).Distinct().Count()}",
                $"gallery.groupSizes={string.Join(",", items.Where(item => !string.IsNullOrEmpty(item.anonymousTopologyGroup)).GroupBy(item => item.anonymousTopologyGroup).OrderBy(group => group.Key).Select(group => group.Count()))}",
                $"gallery.emptyFloorSheet={BuildPhase7GalleryFloorScoreSheet(items).Split('\n').Skip(1).Where(line => !string.IsNullOrEmpty(line)).All(line => line.EndsWith(new string(',', Phase7GalleryFloorCriteria.Length + 1), StringComparison.Ordinal))}",
                $"gallery.repetitionScopes={BuildPhase7GalleryRepetitionScoreSheet(items).Split('\n').Skip(1).Count(line => !string.IsNullOrEmpty(line))}"
            });
        }
    }
}

#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    internal static class GeneratedCollisionOptimizerEvaluator
    {
        private const string VariantRoot = "Assets/Arena/Content/Prefabs/OpenWorld/EnvironmentVariants";
        private const string GeneratedCollisionRoot = "Assets/Arena/Content/Prefabs/OpenWorld/GeneratedCollision";
        private const string SettingsPath = "Assets/Arena/Content/Settings/OpenWorld/generated_collision_optimizer_settings.json";
        private const string ReportPath = "Assets/Arena/Content/Settings/OpenWorld/generated_collision_optimizer_evaluation_report.json";
        private const string GameplayCollisionLayer = "GameplayCollision";
        private const string GameplayQueryCollisionLayer = "GameplayQueryCollision";
        private const string MovementCollisionChildName = "ArenaGameplayCollision";
        private const string QueryCollisionChildName = "ArenaGameplayQueryCollision";
        private const float DegenerateTriangleAreaSquaredEpsilon = 0.000000000001f;

        [MenuItem("Arena/OpenWorld/Collision Optimization/1 Evaluate Selected Variant Assets", false, 500)]
        private static void EvaluateSelectedVariantAssets()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            OptimizerSettings settings = LoadSettings();
            List<string> assetPaths = ResolveSelectedVariantAssetPaths();
            if (assetPaths.Count == 0)
            {
                Debug.LogError(
                    "[GeneratedCollisionOptimizerEvaluator] Select one or more Arena environment variant prefab assets, " +
                    $"Project folders under {VariantRoot}, or scene instances of those variants.");
                return;
            }

            var warnings = new List<string>();
            var evaluations = new List<AssetEvaluation>();
            foreach (string assetPath in assetPaths)
            {
                GameObject root = PrefabUtility.LoadPrefabContents(assetPath);
                try
                {
                    evaluations.Add(EvaluatePrefabAsset(root, assetPath, settings, warnings));
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            var report = new EvaluationReport
            {
                generated_at_utc = DateTime.UtcNow.ToString("O"),
                settings_path = File.Exists(ProjectAbsolutePath(SettingsPath)) ? SettingsPath : "<defaults>",
                report_path = ReportPath,
                asset_count = evaluations.Count,
                summary = BuildReportSummary(evaluations),
                assets = evaluations.ToArray(),
                warnings = warnings.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            };

            string json = JsonUtility.ToJson(report, true);
            string absoluteReportPath = ProjectAbsolutePath(ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absoluteReportPath)!);
            File.WriteAllText(absoluteReportPath, json);
            AssetDatabase.Refresh();

            int needsGeneratedMovement = evaluations.Count(e => e.recommendation == "generate_movement_hulls");
            int readyForReplacementReview = evaluations.Count(e => e.recommendation == "review_generated_movement_replacement");
            Debug.Log(
                $"[GeneratedCollisionOptimizerEvaluator] Evaluated {evaluations.Count} variant asset(s). " +
                $"Needs generated movement hulls: {needsGeneratedMovement}; ready for replacement review: {readyForReplacementReview}. " +
                $"Report: {ReportPath}");
        }

        private static AssetEvaluation EvaluatePrefabAsset(
            GameObject root,
            string assetPath,
            OptimizerSettings settings,
            List<string> warnings)
        {
            int gameplayLayer = LayerMask.NameToLayer(GameplayCollisionLayer);
            int queryLayer = LayerMask.NameToLayer(GameplayQueryCollisionLayer);
            if (gameplayLayer < 0)
                warnings.Add($"Layer '{GameplayCollisionLayer}' is missing; movement classification will rely on object names only.");
            if (queryLayer < 0)
                warnings.Add($"Layer '{GameplayQueryCollisionLayer}' is missing; query classification will rely on object names only.");

            var sourceVisualMeshes = EvaluateSourceVisualMeshes(root, assetPath);
            var authorColliders = EvaluateAuthorSourceColliders(root, assetPath, gameplayLayer, queryLayer);
            var movementBoxes = EvaluateMovementBoxes(root, assetPath, gameplayLayer);
            var queryBoxes = EvaluateQueryBoxes(root, assetPath, queryLayer);
            var rawQueryMeshes = EvaluateQueryMeshes(root, assetPath, queryLayer, settings, simplified: false);
            var simplifiedQueryMeshes = EvaluateQueryMeshes(root, assetPath, queryLayer, settings, simplified: true);
            var generatedHulls = EvaluateGeneratedMovementHulls(root, assetPath, gameplayLayer, settings);
            var capsules = EvaluateUnsupportedCapsules(root, assetPath);
            CandidateEvaluation solidGeometryBaseline = BuildSolidGeometryBaseline(rawQueryMeshes, simplifiedQueryMeshes, authorColliders);

            ApplySourceFit(sourceVisualMeshes, solidGeometryBaseline, authorColliders, movementBoxes, queryBoxes, rawQueryMeshes, simplifiedQueryMeshes, generatedHulls, capsules);
            ApplySolidGeometryFit(solidGeometryBaseline, movementBoxes, generatedHulls);

            string[] blockers = BuildBlockers(sourceVisualMeshes, solidGeometryBaseline, movementBoxes, queryBoxes, generatedHulls, rawQueryMeshes, simplifiedQueryMeshes, settings);
            string recommendation = BuildRecommendation(sourceVisualMeshes, movementBoxes, queryBoxes, generatedHulls, solidGeometryBaseline, rawQueryMeshes, simplifiedQueryMeshes, capsules, blockers, settings);

            return new AssetEvaluation
            {
                asset_path = assetPath,
                asset_guid = AssetDatabase.AssetPathToGUID(assetPath),
                prefab_name = root.name,
                recommendation = recommendation,
                blockers = blockers,
                source_visual_meshes = sourceVisualMeshes,
                solid_geometry_baseline = solidGeometryBaseline,
                author_source_colliders = authorColliders,
                current_movement_boxes = movementBoxes,
                current_query_boxes = queryBoxes,
                raw_query_meshes = rawQueryMeshes,
                simplified_query_meshes = simplifiedQueryMeshes,
                generated_compound_hulls = generatedHulls,
                unsupported_capsules = capsules,
            };
        }

        private static ReportSummary BuildReportSummary(IReadOnlyCollection<AssetEvaluation> evaluations)
        {
            var summary = new ReportSummary
            {
                asset_count = evaluations.Count,
                current_movement_box_count = evaluations.Sum(evaluation => evaluation.current_movement_boxes.box_count),
                current_query_box_count = evaluations.Sum(evaluation => evaluation.current_query_boxes.box_count),
                generated_movement_hull_count = evaluations.Sum(evaluation => evaluation.generated_compound_hulls.hull_count),
                raw_query_mesh_count = evaluations.Sum(evaluation => evaluation.raw_query_meshes.mesh_count),
                raw_query_triangle_count = evaluations.Sum(evaluation => evaluation.raw_query_meshes.triangle_count),
                assets_with_solid_geometry_baseline = evaluations.Count(evaluation => HasGeometry(evaluation.solid_geometry_baseline)),
                assets_requiring_generated_movement = evaluations.Count(evaluation => evaluation.recommendation == "generate_movement_hulls"),
                assets_ready_for_generated_replacement_review = evaluations.Count(evaluation => evaluation.recommendation == "review_generated_movement_replacement"),
                assets_recommended_collision_removal = evaluations.Count(evaluation => evaluation.recommendation == "remove_tiny_collision"),
                generated_hull_solid_fit_blocker_assets = evaluations.Count(evaluation => evaluation.blockers.Any(blocker => blocker.StartsWith("generated_hull_solid_fit_ratio_exceeds_", StringComparison.Ordinal))),
                generated_movement_box_replacement_debt_assets = evaluations.Count(evaluation => evaluation.blockers.Contains("generated_movement_box_requires_replacement", StringComparer.Ordinal)),
                missing_movement_collision_assets = evaluations.Count(evaluation => evaluation.blockers.Contains("missing_movement_collision_requires_generation", StringComparer.Ordinal)),
                movement_box_solid_fit_blocker_assets = evaluations.Count(evaluation => evaluation.blockers.Any(blocker => blocker.StartsWith("movement_box_solid_fit_ratio_exceeds_", StringComparison.Ordinal))),
            };

            summary.max_movement_box_solid_fit_ratio = evaluations
                .Select(evaluation => evaluation.current_movement_boxes.solid_geometry_aabb_volume_ratio)
                .DefaultIfEmpty(0f)
                .Max();
            summary.top_movement_box_solid_fit = evaluations
                .Where(evaluation => evaluation.current_movement_boxes.solid_geometry_aabb_volume_ratio > 0f)
                .OrderByDescending(evaluation => evaluation.current_movement_boxes.solid_geometry_aabb_volume_ratio)
                .ThenBy(evaluation => evaluation.asset_path, StringComparer.Ordinal)
                .Take(25)
                .Select(evaluation => new AssetFitSummary
                {
                    asset_path = evaluation.asset_path,
                    recommendation = evaluation.recommendation,
                    movement_box_solid_fit_ratio = evaluation.current_movement_boxes.solid_geometry_aabb_volume_ratio,
                    movement_box_count = evaluation.current_movement_boxes.box_count,
                    solid_baseline_triangle_count = evaluation.solid_geometry_baseline.triangle_count,
                    blockers = evaluation.blockers,
                })
                .ToArray();

            return summary;
        }

        private static CandidateEvaluation EvaluateSourceVisualMeshes(GameObject root, string assetPath)
        {
            var accumulator = new CandidateAccumulator("source_visual_meshes", "fidelity_baseline", root.transform, assetPath);
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(includeInactive: false)
                         .OrderBy(filter => HierarchyPath(filter.transform), StringComparer.Ordinal))
            {
                Renderer? renderer = filter.GetComponent<Renderer>();
                if (filter.sharedMesh == null || renderer == null || !ShouldUseRenderer(renderer))
                    continue;

                accumulator.AddMesh(filter.sharedMesh, filter.transform.localToWorldMatrix, isHull: false, generatedAssetPath: AssetDatabase.GetAssetPath(filter.sharedMesh));
            }

            CandidateEvaluation evaluation = accumulator.ToEvaluation();
            evaluation.notes = "Raw visual mesh bounds and counts are used only as a fit baseline; this is not an export candidate.";
            return evaluation;
        }

        private static CandidateEvaluation EvaluateAuthorSourceColliders(
            GameObject root,
            string assetPath,
            int gameplayLayer,
            int queryLayer)
        {
            var accumulator = new CandidateAccumulator("author_source_colliders", "source_authoring_data", root.transform, assetPath);
            foreach (Collider collider in OrderedColliders(root))
            {
                if (collider.gameObject.layer == gameplayLayer ||
                    collider.gameObject.layer == queryLayer ||
                    IsGeneratedCollisionObject(collider.gameObject))
                {
                    continue;
                }

                accumulator.AddCollider(collider);
            }

            CandidateEvaluation evaluation = accumulator.ToEvaluation();
            evaluation.notes = "Default-layer authoring colliders are preserved source data until an explicit export/generation path consumes them.";
            return evaluation;
        }

        private static CandidateEvaluation EvaluateMovementBoxes(GameObject root, string assetPath, int gameplayLayer)
        {
            var accumulator = new CandidateAccumulator("current_movement_boxes", "movement", root.transform, assetPath);
            foreach (BoxCollider collider in OrderedColliders(root).OfType<BoxCollider>())
            {
                if (!IsMovementCollisionObject(collider.gameObject, gameplayLayer))
                    continue;

                accumulator.AddCollider(collider);
            }

            CandidateEvaluation evaluation = accumulator.ToEvaluation();
            evaluation.notes = "Existing GameplayCollision boxes are temporary movement blockers when their fit ratio is high.";
            return evaluation;
        }

        private static CandidateEvaluation EvaluateQueryBoxes(GameObject root, string assetPath, int queryLayer)
        {
            var accumulator = new CandidateAccumulator("current_query_boxes", "projectile_los_query", root.transform, assetPath);
            foreach (BoxCollider collider in OrderedColliders(root).OfType<BoxCollider>())
            {
                if (!IsQueryCollisionObject(collider.gameObject, queryLayer))
                    continue;

                accumulator.AddCollider(collider);
            }

            CandidateEvaluation evaluation = accumulator.ToEvaluation();
            evaluation.notes = "Existing GameplayQueryCollision boxes are exported query blockers; tiny decorative props should not keep these unless intentionally blocking projectiles/LOS.";
            return evaluation;
        }

        private static CandidateEvaluation EvaluateQueryMeshes(
            GameObject root,
            string assetPath,
            int queryLayer,
            OptimizerSettings settings,
            bool simplified)
        {
            var accumulator = new CandidateAccumulator(
                simplified ? "simplified_query_meshes" : "raw_query_meshes",
                "projectile_los_query",
                root.transform,
                assetPath);
            foreach (MeshCollider collider in OrderedColliders(root).OfType<MeshCollider>())
            {
                if (!IsQueryCollisionObject(collider.gameObject, queryLayer) || collider.sharedMesh == null)
                    continue;

                bool colliderIsSimplified = IsSimplifiedOrGeneratedMesh(collider.sharedMesh, settings);
                if (colliderIsSimplified != simplified)
                    continue;

                accumulator.AddMeshCollider(collider, isHull: false);
            }

            CandidateEvaluation evaluation = accumulator.ToEvaluation();
            evaluation.notes = simplified
                ? "Explicit simplified/generated query meshes are only preferred when raw query meshes exceed budget or runtime counters prove too expensive."
                : "Raw query meshes are the projectile/LOS fidelity baseline; they are not acceptable as final player movement collision.";
            return evaluation;
        }

        private static CandidateEvaluation EvaluateGeneratedMovementHulls(
            GameObject root,
            string assetPath,
            int gameplayLayer,
            OptimizerSettings settings)
        {
            var accumulator = new CandidateAccumulator("generated_compound_hulls", "movement", root.transform, assetPath);
            foreach (MeshCollider collider in OrderedColliders(root).OfType<MeshCollider>())
            {
                if (collider.sharedMesh == null || !collider.convex)
                    continue;
                if (!IsMovementCollisionObject(collider.gameObject, gameplayLayer) &&
                    !IsGeneratedMovementHullObject(collider.gameObject, collider.sharedMesh, settings))
                {
                    continue;
                }

                accumulator.AddMeshCollider(collider, isHull: true);
            }

            CandidateEvaluation evaluation = accumulator.ToEvaluation();
            evaluation.tool_name = InferGeneratedToolName(evaluation.mesh_asset_paths, settings);
            evaluation.tool_version = settings.generatedToolVersion;
            evaluation.parameter_profile = settings.generatedParameterProfile;
            evaluation.notes = "Generated convex hull candidates are measured for movement replacement review; this evaluator does not swap them into export.";
            return evaluation;
        }

        private static CandidateEvaluation EvaluateUnsupportedCapsules(GameObject root, string assetPath)
        {
            var accumulator = new CandidateAccumulator("unsupported_capsules", "source_authoring_data", root.transform, assetPath);
            foreach (CapsuleCollider collider in OrderedColliders(root).OfType<CapsuleCollider>())
                accumulator.AddCollider(collider);

            CandidateEvaluation evaluation = accumulator.ToEvaluation();
            evaluation.notes = "Capsules remain useful authoring data but are not exported by the current server movement collision format.";
            return evaluation;
        }

        private static void ApplySourceFit(
            CandidateEvaluation source,
            params CandidateEvaluation[] candidates)
        {
            Bounds sourceBounds = BoundsFromEvaluation(source);
            float sourceVolume = BoundsVolume(sourceBounds);
            ApplySourceFitToCandidate(sourceBounds, sourceVolume, source);
            foreach (CandidateEvaluation candidate in candidates)
                ApplySourceFitToCandidate(sourceBounds, sourceVolume, candidate);
        }

        private static void ApplySourceFitToCandidate(
            Bounds sourceBounds,
            float sourceVolume,
            CandidateEvaluation candidate)
        {
            Bounds candidateBounds = BoundsFromEvaluation(candidate);
            float candidateVolume = BoundsVolume(candidateBounds);
            candidate.source_aabb_volume_ratio = sourceVolume > 0f ? candidateVolume / sourceVolume : 0f;
            candidate.source_aabb_coverage = sourceVolume > 0f ? BoundsVolume(Intersect(sourceBounds, candidateBounds)) / sourceVolume : 0f;
        }

        private static CandidateEvaluation BuildSolidGeometryBaseline(
            CandidateEvaluation rawQueryMeshes,
            CandidateEvaluation simplifiedQueryMeshes,
            CandidateEvaluation authorColliders)
        {
            CandidateEvaluation baseline = new()
            {
                label = "solid_geometry_baseline",
                purpose = "movement_fit_reference",
                notes = "Query meshes are preferred as the solid-geometry fit reference; author colliders are used only when no query mesh exists.",
            };

            CandidateEvaluation[] preferred = rawQueryMeshes.mesh_count + simplifiedQueryMeshes.mesh_count > 0
                ? new[] { rawQueryMeshes, simplifiedQueryMeshes }
                : new[] { authorColliders };

            Bounds? bounds = null;
            foreach (CandidateEvaluation candidate in preferred)
            {
                if (!HasGeometry(candidate))
                    continue;

                Bounds candidateBounds = BoundsFromEvaluation(candidate);
                bounds = bounds.HasValue ? Encapsulate(bounds.Value, candidateBounds) : candidateBounds;
                baseline.collider_count += candidate.collider_count;
                baseline.box_count += candidate.box_count;
                baseline.capsule_count += candidate.capsule_count;
                baseline.mesh_count += candidate.mesh_count;
                baseline.unreadable_mesh_count += candidate.unreadable_mesh_count;
                baseline.hull_count += candidate.hull_count;
                baseline.vertex_count += candidate.vertex_count;
                baseline.triangle_count += candidate.triangle_count;
                baseline.degenerate_triangle_count += candidate.degenerate_triangle_count;
                baseline.max_triangles_per_mesh = Mathf.Max(baseline.max_triangles_per_mesh, candidate.max_triangles_per_mesh);
                baseline.max_vertices_per_hull = Mathf.Max(baseline.max_vertices_per_hull, candidate.max_vertices_per_hull);
                baseline.estimated_export_bytes += candidate.estimated_export_bytes;
            }

            Bounds outputBounds = bounds ?? new Bounds(Vector3.zero, Vector3.zero);
            baseline.bounds_center = new[] { outputBounds.center.x, outputBounds.center.y, outputBounds.center.z };
            baseline.bounds_size = new[] { outputBounds.size.x, outputBounds.size.y, outputBounds.size.z };
            baseline.content_hash = BuildCombinedHash(preferred.Where(HasGeometry).Select(candidate => candidate.content_hash));
            baseline.mesh_asset_paths = preferred.SelectMany(candidate => candidate.mesh_asset_paths).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            baseline.mesh_asset_guids = preferred.SelectMany(candidate => candidate.mesh_asset_guids).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return baseline;
        }

        private static void ApplySolidGeometryFit(
            CandidateEvaluation solidGeometryBaseline,
            params CandidateEvaluation[] candidates)
        {
            Bounds solidBounds = BoundsFromEvaluation(solidGeometryBaseline);
            float solidVolume = BoundsVolume(solidBounds);
            foreach (CandidateEvaluation candidate in candidates)
            {
                Bounds candidateBounds = BoundsFromEvaluation(candidate);
                float candidateVolume = BoundsVolume(candidateBounds);
                candidate.solid_geometry_aabb_volume_ratio = solidVolume > 0f ? candidateVolume / solidVolume : 0f;
                candidate.solid_geometry_aabb_coverage = solidVolume > 0f ? BoundsVolume(Intersect(solidBounds, candidateBounds)) / solidVolume : 0f;
            }
        }

        private static string[] BuildBlockers(
            CandidateEvaluation sourceVisualMeshes,
            CandidateEvaluation solidGeometryBaseline,
            CandidateEvaluation movementBoxes,
            CandidateEvaluation queryBoxes,
            CandidateEvaluation generatedHulls,
            CandidateEvaluation rawQueryMeshes,
            CandidateEvaluation simplifiedQueryMeshes,
            OptimizerSettings settings)
        {
            var blockers = new List<string>();
            bool shouldRemoveTinyCollision = IsTinyNoQueryCollisionRemovalCandidate(sourceVisualMeshes, rawQueryMeshes, simplifiedQueryMeshes, generatedHulls, settings) &&
                                             (movementBoxes.box_count > 0 || queryBoxes.box_count > 0 || generatedHulls.hull_count > 0);
            if (sourceVisualMeshes.mesh_count == 0)
                blockers.Add("no_source_visual_mesh_baseline");
            if (shouldRemoveTinyCollision)
                blockers.Add("tiny_asset_collision_should_be_removed");
            if (settings.treatArenaGameplayCollisionBoxesAsReplacementDebt &&
                !shouldRemoveTinyCollision &&
                movementBoxes.box_count > 0 &&
                generatedHulls.hull_count == 0)
            {
                blockers.Add("generated_movement_box_requires_replacement");
            }
            if (settings.treatMissingMovementCollisionAsGenerationCandidate &&
                !shouldRemoveTinyCollision &&
                HasGeometry(solidGeometryBaseline) &&
                movementBoxes.box_count == 0 &&
                generatedHulls.hull_count == 0)
            {
                blockers.Add("missing_movement_collision_requires_generation");
            }
            if (HasGeometry(solidGeometryBaseline) &&
                !shouldRemoveTinyCollision &&
                movementBoxes.box_count > 0 &&
                generatedHulls.hull_count == 0 &&
                movementBoxes.solid_geometry_aabb_volume_ratio > settings.badMovementBoxVolumeRatio)
            {
                blockers.Add($"movement_box_solid_fit_ratio_exceeds_{settings.badMovementBoxVolumeRatio:F2}");
            }
            if (movementBoxes.box_count > 0 &&
                !shouldRemoveTinyCollision &&
                !HasGeometry(solidGeometryBaseline) &&
                generatedHulls.hull_count == 0 &&
                movementBoxes.source_aabb_volume_ratio > settings.badMovementBoxVolumeRatio)
            {
                blockers.Add($"movement_box_fit_ratio_exceeds_{settings.badMovementBoxVolumeRatio:F2}");
            }
            if (generatedHulls.hull_count > settings.maxMovementHullCount)
                blockers.Add($"generated_hull_count_exceeds_{settings.maxMovementHullCount}");
            if (generatedHulls.max_vertices_per_hull > settings.maxVerticesPerMovementHull)
                blockers.Add($"generated_hull_vertices_exceed_{settings.maxVerticesPerMovementHull}");
            if (HasGeometry(solidGeometryBaseline) &&
                generatedHulls.hull_count > 0 &&
                generatedHulls.solid_geometry_aabb_volume_ratio > settings.badGeneratedHullSolidFitRatio)
            {
                blockers.Add($"generated_hull_solid_fit_ratio_exceeds_{settings.badGeneratedHullSolidFitRatio:F2}");
            }
            int queryTriangles = rawQueryMeshes.triangle_count + simplifiedQueryMeshes.triangle_count;
            if (queryTriangles > settings.maxQueryTrianglesPerAsset)
                blockers.Add($"query_triangles_exceed_{settings.maxQueryTrianglesPerAsset}");
            if (rawQueryMeshes.unreadable_mesh_count + simplifiedQueryMeshes.unreadable_mesh_count > 0)
                blockers.Add("query_mesh_not_readable");
            if (rawQueryMeshes.max_triangles_per_mesh > settings.maxQueryTrianglesPerMesh ||
                simplifiedQueryMeshes.max_triangles_per_mesh > settings.maxQueryTrianglesPerMesh)
            {
                blockers.Add($"query_mesh_triangles_exceed_{settings.maxQueryTrianglesPerMesh}");
            }

            return blockers.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static bool HasGeometry(CandidateEvaluation candidate)
            => candidate.box_count + candidate.capsule_count + candidate.mesh_count + candidate.hull_count > 0;

        private static bool IsTinyNoQueryCollisionRemovalCandidate(
            CandidateEvaluation sourceVisualMeshes,
            CandidateEvaluation rawQueryMeshes,
            CandidateEvaluation simplifiedQueryMeshes,
            CandidateEvaluation generatedHulls,
            OptimizerSettings settings)
        {
            if (!settings.treatTinyNoQueryAssetsAsCollisionRemovalCandidates ||
                sourceVisualMeshes.mesh_count == 0 ||
                rawQueryMeshes.mesh_count + simplifiedQueryMeshes.mesh_count > 0 ||
                generatedHulls.hull_count > 0)
            {
                return false;
            }

            Bounds sourceBounds = BoundsFromEvaluation(sourceVisualMeshes);
            float maxExtent = Mathf.Max(sourceBounds.size.x, Mathf.Max(sourceBounds.size.y, sourceBounds.size.z));
            return maxExtent <= settings.tinyNoQueryCollisionMaxSourceExtent &&
                   BoundsVolume(sourceBounds) <= settings.tinyNoQueryCollisionMaxSourceVolume;
        }

        private static string BuildRecommendation(
            CandidateEvaluation sourceVisualMeshes,
            CandidateEvaluation movementBoxes,
            CandidateEvaluation queryBoxes,
            CandidateEvaluation generatedHulls,
            CandidateEvaluation solidGeometryBaseline,
            CandidateEvaluation rawQueryMeshes,
            CandidateEvaluation simplifiedQueryMeshes,
            CandidateEvaluation capsules,
            string[] blockers,
            OptimizerSettings settings)
        {
            if (blockers.Contains("tiny_asset_collision_should_be_removed", StringComparer.Ordinal))
                return "remove_tiny_collision";
            if (IsTinyNoQueryCollisionRemovalCandidate(sourceVisualMeshes, rawQueryMeshes, simplifiedQueryMeshes, generatedHulls, settings) &&
                movementBoxes.box_count == 0 &&
                queryBoxes.box_count == 0)
            {
                return "no_action_required";
            }
            if (generatedHulls.hull_count > 0 && blockers.Length == 0)
                return "review_generated_movement_replacement";
            if ((movementBoxes.box_count > 0 || HasGeometry(solidGeometryBaseline)) && generatedHulls.hull_count == 0)
                return "generate_movement_hulls";
            if (capsules.capsule_count > 0 && generatedHulls.hull_count == 0)
                return "convert_capsules_or_generate_hulls";
            if (rawQueryMeshes.mesh_count + simplifiedQueryMeshes.mesh_count == 0)
                return "author_query_mesh_baseline";
            return blockers.Length == 0 ? "no_action_required" : "fix_blockers_before_replacement";
        }

        private static IEnumerable<Collider> OrderedColliders(GameObject root)
            => root.GetComponentsInChildren<Collider>(includeInactive: false)
                .Where(collider => collider != null && collider.enabled && !collider.isTrigger && collider.gameObject.activeInHierarchy)
                .OrderBy(collider => HierarchyPath(collider.transform), StringComparer.Ordinal)
                .ThenBy(collider => collider.GetType().Name, StringComparer.Ordinal);

        private static bool IsMovementCollisionObject(GameObject gameObject, int gameplayLayer)
            => (gameplayLayer >= 0 && gameObject.layer == gameplayLayer) ||
               gameObject.name == MovementCollisionChildName ||
               gameObject.name.StartsWith(MovementCollisionChildName + "_", StringComparison.Ordinal);

        private static bool IsQueryCollisionObject(GameObject gameObject, int queryLayer)
            => (queryLayer >= 0 && gameObject.layer == queryLayer) ||
               gameObject.name == QueryCollisionChildName ||
               gameObject.name.StartsWith(QueryCollisionChildName + "_", StringComparison.Ordinal);

        private static bool IsGeneratedCollisionObject(GameObject gameObject)
            => gameObject.name.Contains("Generated", StringComparison.OrdinalIgnoreCase) ||
               gameObject.name.Contains("VHACD", StringComparison.OrdinalIgnoreCase) ||
               gameObject.name.Contains("CoACD", StringComparison.OrdinalIgnoreCase);

        private static bool IsGeneratedMovementHullObject(GameObject gameObject, Mesh mesh, OptimizerSettings settings)
            => IsGeneratedCollisionObject(gameObject) || IsSimplifiedOrGeneratedMesh(mesh, settings);

        private static bool IsSimplifiedOrGeneratedMesh(Mesh mesh, OptimizerSettings settings)
        {
            string path = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrEmpty(path) && path.StartsWith(GeneratedCollisionRoot + "/", StringComparison.Ordinal))
                return true;

            string nameAndPath = $"{path}/{mesh.name}";
            foreach (string token in settings.generatedMeshPathTokens)
            {
                if (!string.IsNullOrWhiteSpace(token) &&
                    nameAndPath.IndexOf(token.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string InferGeneratedToolName(string[] meshAssetPaths, OptimizerSettings settings)
        {
            string joined = string.Join(" ", meshAssetPaths);
            foreach (string tool in settings.decompositionToolNames)
            {
                if (!string.IsNullOrWhiteSpace(tool) &&
                    joined.IndexOf(tool.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return tool.Trim();
                }
            }

            return meshAssetPaths.Length > 0 ? "generated_or_manual" : "";
        }

        private static bool ShouldUseRenderer(Renderer renderer)
            => renderer.enabled &&
               renderer is not ParticleSystemRenderer &&
               renderer is not TrailRenderer &&
               renderer is not LineRenderer;

        private static List<string> ResolveSelectedVariantAssetPaths()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnityEngine.Object selected in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(selected);
                AddSelectedPath(path, paths);

                if (selected is GameObject gameObject && string.IsNullOrEmpty(path))
                {
                    GameObject? prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
                    if (prefabRoot != null)
                        AddSelectedPath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefabRoot), paths);
                }
            }

            foreach (string guid in Selection.assetGUIDs)
                AddSelectedPath(AssetDatabase.GUIDToAssetPath(guid), paths);

            return paths
                .Where(IsArenaEnvironmentVariantPrefab)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        private static void AddSelectedPath(string path, HashSet<string> paths)
        {
            if (string.IsNullOrEmpty(path))
                return;

            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(path);
                return;
            }

            if (!AssetDatabase.IsValidFolder(path))
                return;

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { path }))
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(prefabPath))
                    paths.Add(prefabPath);
            }
        }

        private static bool IsArenaEnvironmentVariantPrefab(string path)
            => path.StartsWith(VariantRoot + "/", StringComparison.Ordinal) &&
               path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

        private static OptimizerSettings LoadSettings()
        {
            string absolutePath = ProjectAbsolutePath(SettingsPath);
            if (!File.Exists(absolutePath))
                return new OptimizerSettings();

            try
            {
                return JsonUtility.FromJson<OptimizerSettings>(File.ReadAllText(absolutePath)) ?? new OptimizerSettings();
            }
            catch (Exception exception)
            {
                Debug.LogError($"[GeneratedCollisionOptimizerEvaluator] Failed to read '{SettingsPath}': {exception.Message}");
                return new OptimizerSettings();
            }
        }

        private static string ProjectAbsolutePath(string relativePath)
            => Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, relativePath);

        private static string HierarchyPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = $"{transform.name}/{path}";
            }

            return path;
        }

        private static Bounds BoundsFromEvaluation(CandidateEvaluation evaluation)
            => new(new Vector3(evaluation.bounds_center[0], evaluation.bounds_center[1], evaluation.bounds_center[2]),
                new Vector3(evaluation.bounds_size[0], evaluation.bounds_size[1], evaluation.bounds_size[2]));

        private static float BoundsVolume(Bounds bounds)
            => Mathf.Max(0f, bounds.size.x) * Mathf.Max(0f, bounds.size.y) * Mathf.Max(0f, bounds.size.z);

        private static Bounds Intersect(Bounds a, Bounds b)
        {
            Vector3 min = Vector3.Max(a.min, b.min);
            Vector3 max = Vector3.Min(a.max, b.max);
            Vector3 size = Vector3.Max(Vector3.zero, max - min);
            return new Bounds(min + size * 0.5f, size);
        }

        private static Bounds Encapsulate(Bounds a, Bounds b)
        {
            a.Encapsulate(b.min);
            a.Encapsulate(b.max);
            return a;
        }

        private static string BuildCombinedHash(IEnumerable<string> contentHashes)
        {
            using SHA256 sha256 = SHA256.Create();
            string joined = string.Join("|", contentHashes.OrderBy(value => value, StringComparer.Ordinal));
            byte[] digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(joined));
            return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
        }

        private sealed class CandidateAccumulator
        {
            private readonly Transform root;
            private readonly BoundsAccumulator bounds = new();
            private readonly SHA256 hash = SHA256.Create();
            private readonly MemoryStream hashBytes = new();
            private readonly List<string> meshAssetPaths = new();
            private readonly List<string> meshAssetGuids = new();
            private readonly CandidateEvaluation evaluation;

            public CandidateAccumulator(string label, string purpose, Transform root, string assetPath)
            {
                this.root = root;
                evaluation = new CandidateEvaluation
                {
                    label = label,
                    purpose = purpose,
                };
                AppendString(assetPath);
                AppendString(label);
                AppendString(purpose);
            }

            public void AddCollider(Collider collider)
            {
                if (collider is MeshCollider meshCollider)
                {
                    AddMeshCollider(meshCollider, isHull: meshCollider.convex);
                    return;
                }

                evaluation.collider_count++;
                AppendString(HierarchyPath(collider.transform));
                AppendString(collider.GetType().Name);
                EncapsulateWorldBounds(collider.bounds);

                switch (collider)
                {
                    case BoxCollider box:
                        evaluation.box_count++;
                        AppendVector3(box.center);
                        AppendVector3(box.size);
                        evaluation.estimated_export_bytes += 192 + HierarchyPath(collider.transform).Length;
                        break;
                    case CapsuleCollider capsule:
                        evaluation.capsule_count++;
                        AppendVector3(capsule.center);
                        AppendFloat(capsule.radius);
                        AppendFloat(capsule.height);
                        AppendInt(capsule.direction);
                        evaluation.estimated_export_bytes += 96 + HierarchyPath(collider.transform).Length;
                        break;
                }
            }

            public void AddMeshCollider(MeshCollider collider, bool isHull)
            {
                evaluation.collider_count++;
                if (isHull)
                    evaluation.hull_count++;
                AddMesh(collider.sharedMesh, collider.transform.localToWorldMatrix, isHull, AssetDatabase.GetAssetPath(collider.sharedMesh));
                AppendString(HierarchyPath(collider.transform));
                AppendInt(collider.convex ? 1 : 0);
            }

            public void AddMesh(Mesh? mesh, Matrix4x4 localToWorld, bool isHull, string generatedAssetPath)
            {
                if (mesh == null)
                    return;

                evaluation.mesh_count++;
                evaluation.vertex_count += mesh.vertexCount;
                evaluation.max_vertices_per_hull = isHull ? Mathf.Max(evaluation.max_vertices_per_hull, mesh.vertexCount) : evaluation.max_vertices_per_hull;

                string meshPath = AssetDatabase.GetAssetPath(mesh);
                string meshGuid = string.IsNullOrEmpty(meshPath) ? "" : AssetDatabase.AssetPathToGUID(meshPath);
                string reportedMeshPath = string.IsNullOrEmpty(generatedAssetPath) ? meshPath : generatedAssetPath;
                if (!string.IsNullOrEmpty(reportedMeshPath) && !meshAssetPaths.Contains(reportedMeshPath, StringComparer.Ordinal))
                    meshAssetPaths.Add(reportedMeshPath);
                if (!string.IsNullOrEmpty(meshGuid) && !meshAssetGuids.Contains(meshGuid, StringComparer.Ordinal))
                    meshAssetGuids.Add(meshGuid);
                AppendString(meshPath);
                AppendString(meshGuid);
                AppendString(mesh.name);
                AppendInt(mesh.vertexCount);

                Vector3[] vertices;
                int[] triangles;
                try
                {
                    vertices = mesh.vertices;
                    triangles = mesh.triangles;
                }
                catch (Exception)
                {
                    evaluation.unreadable_mesh_count++;
                    evaluation.estimated_export_bytes += mesh.vertexCount * 12 + 96;
                    EncapsulateLocalBounds(mesh.bounds, localToWorld);
                    AppendInt(-1);
                    return;
                }

                int triangleCount = 0;
                int degenerateCount = 0;
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int a = triangles[i];
                    int b = triangles[i + 1];
                    int c = triangles[i + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= vertices.Length || b >= vertices.Length || c >= vertices.Length)
                        continue;
                    if (Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude <= DegenerateTriangleAreaSquaredEpsilon)
                    {
                        degenerateCount++;
                        continue;
                    }

                    triangleCount++;
                }

                evaluation.triangle_count += triangleCount;
                evaluation.degenerate_triangle_count += degenerateCount;
                evaluation.max_triangles_per_mesh = Mathf.Max(evaluation.max_triangles_per_mesh, triangleCount);
                evaluation.estimated_export_bytes += mesh.vertexCount * 12 + triangleCount * 12 + 96;

                AppendInt(triangleCount);
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 local = vertices[i];
                    Vector3 world = localToWorld.MultiplyPoint3x4(local);
                    Vector3 rootLocal = root.InverseTransformPoint(world);
                    bounds.Encapsulate(rootLocal);
                    AppendVector3(local);
                }

                foreach (int index in triangles)
                    AppendInt(index);
            }

            public CandidateEvaluation ToEvaluation()
            {
                Bounds outputBounds = bounds.HasBounds ? bounds.Bounds : new Bounds(Vector3.zero, Vector3.zero);
                evaluation.bounds_center = new[] { outputBounds.center.x, outputBounds.center.y, outputBounds.center.z };
                evaluation.bounds_size = new[] { outputBounds.size.x, outputBounds.size.y, outputBounds.size.z };
                evaluation.mesh_asset_paths = meshAssetPaths.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                evaluation.mesh_asset_guids = meshAssetGuids.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                evaluation.content_hash = FinishHash();
                return evaluation;
            }

            private void EncapsulateWorldBounds(Bounds worldBounds)
            {
                foreach (Vector3 corner in BoundsCorners(worldBounds))
                    bounds.Encapsulate(root.InverseTransformPoint(corner));
            }

            private void EncapsulateLocalBounds(Bounds localBounds, Matrix4x4 localToWorld)
            {
                foreach (Vector3 localCorner in BoundsCorners(localBounds))
                {
                    Vector3 world = localToWorld.MultiplyPoint3x4(localCorner);
                    bounds.Encapsulate(root.InverseTransformPoint(world));
                }
            }

            private string FinishHash()
            {
                byte[] digest = hash.ComputeHash(hashBytes.ToArray());
                return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
            }

            private void AppendString(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
                AppendInt(bytes.Length);
                hashBytes.Write(bytes, 0, bytes.Length);
            }

            private void AppendVector3(Vector3 value)
            {
                AppendFloat(value.x);
                AppendFloat(value.y);
                AppendFloat(value.z);
            }

            private void AppendFloat(float value)
            {
                byte[] bytes = BitConverter.GetBytes(value);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                hashBytes.Write(bytes, 0, bytes.Length);
            }

            private void AppendInt(int value)
            {
                byte[] bytes = BitConverter.GetBytes(value);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                hashBytes.Write(bytes, 0, bytes.Length);
            }
        }

        private sealed class BoundsAccumulator
        {
            private Bounds bounds;
            public bool HasBounds { get; private set; }
            public Bounds Bounds => bounds;

            public void Encapsulate(Vector3 point)
            {
                if (!HasBounds)
                {
                    bounds = new Bounds(point, Vector3.zero);
                    HasBounds = true;
                    return;
                }

                bounds.Encapsulate(point);
            }
        }

        private static IEnumerable<Vector3> BoundsCorners(Bounds bounds)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            yield return new Vector3(min.x, min.y, min.z);
            yield return new Vector3(min.x, min.y, max.z);
            yield return new Vector3(min.x, max.y, min.z);
            yield return new Vector3(min.x, max.y, max.z);
            yield return new Vector3(max.x, min.y, min.z);
            yield return new Vector3(max.x, min.y, max.z);
            yield return new Vector3(max.x, max.y, min.z);
            yield return new Vector3(max.x, max.y, max.z);
        }

        [Serializable]
        private sealed class EvaluationReport
        {
            public int version = 1;
            public string generated_at_utc = "";
            public string settings_path = "";
            public string report_path = "";
            public int asset_count;
            public ReportSummary summary = new();
            public AssetEvaluation[] assets = Array.Empty<AssetEvaluation>();
            public string[] warnings = Array.Empty<string>();
        }

        [Serializable]
        private sealed class ReportSummary
        {
            public int asset_count;
            public int current_movement_box_count;
            public int current_query_box_count;
            public int generated_movement_hull_count;
            public int raw_query_mesh_count;
            public int raw_query_triangle_count;
            public int assets_with_solid_geometry_baseline;
            public int assets_requiring_generated_movement;
            public int assets_ready_for_generated_replacement_review;
            public int assets_recommended_collision_removal;
            public int generated_hull_solid_fit_blocker_assets;
            public int generated_movement_box_replacement_debt_assets;
            public int missing_movement_collision_assets;
            public int movement_box_solid_fit_blocker_assets;
            public float max_movement_box_solid_fit_ratio;
            public AssetFitSummary[] top_movement_box_solid_fit = Array.Empty<AssetFitSummary>();
        }

        [Serializable]
        private sealed class AssetFitSummary
        {
            public string asset_path = "";
            public string recommendation = "";
            public float movement_box_solid_fit_ratio;
            public int movement_box_count;
            public int solid_baseline_triangle_count;
            public string[] blockers = Array.Empty<string>();
        }

        [Serializable]
        private sealed class AssetEvaluation
        {
            public string asset_path = "";
            public string asset_guid = "";
            public string prefab_name = "";
            public string recommendation = "";
            public string[] blockers = Array.Empty<string>();
            public CandidateEvaluation source_visual_meshes = new();
            public CandidateEvaluation solid_geometry_baseline = new();
            public CandidateEvaluation author_source_colliders = new();
            public CandidateEvaluation current_movement_boxes = new();
            public CandidateEvaluation current_query_boxes = new();
            public CandidateEvaluation raw_query_meshes = new();
            public CandidateEvaluation simplified_query_meshes = new();
            public CandidateEvaluation generated_compound_hulls = new();
            public CandidateEvaluation unsupported_capsules = new();
        }

        [Serializable]
        private sealed class CandidateEvaluation
        {
            public string label = "";
            public string purpose = "";
            public int collider_count;
            public int box_count;
            public int capsule_count;
            public int mesh_count;
            public int unreadable_mesh_count;
            public int hull_count;
            public int vertex_count;
            public int triangle_count;
            public int degenerate_triangle_count;
            public int max_triangles_per_mesh;
            public int max_vertices_per_hull;
            public int estimated_export_bytes;
            public float[] bounds_center = { 0f, 0f, 0f };
            public float[] bounds_size = { 0f, 0f, 0f };
            public float source_aabb_volume_ratio;
            public float source_aabb_coverage;
            public float solid_geometry_aabb_volume_ratio;
            public float solid_geometry_aabb_coverage;
            public string content_hash = "";
            public string tool_name = "";
            public string tool_version = "";
            public string parameter_profile = "";
            public string[] mesh_asset_paths = Array.Empty<string>();
            public string[] mesh_asset_guids = Array.Empty<string>();
            public string notes = "";
        }

        [Serializable]
        private sealed class OptimizerSettings
        {
            public bool treatArenaGameplayCollisionBoxesAsReplacementDebt = true;
            public bool treatMissingMovementCollisionAsGenerationCandidate = true;
            public float badMovementBoxVolumeRatio = 2.5f;
            public float badGeneratedHullSolidFitRatio = 1.35f;
            public int maxMovementHullCount = 16;
            public int maxVerticesPerMovementHull = 64;
            public int maxQueryTrianglesPerMesh = 512;
            public int maxQueryTrianglesPerAsset = 4096;
            public bool treatTinyNoQueryAssetsAsCollisionRemovalCandidates = true;
            public float tinyNoQueryCollisionMaxSourceExtent = 0.75f;
            public float tinyNoQueryCollisionMaxSourceVolume = 0.25f;
            public bool enableAdaptiveCompoundMovementHulls = true;
            public int compoundHullMaxSegments = 3;
            public float compoundHullMinSourceMaxExtent = 3f;
            public float compoundHullMinHeightRatio = 0.45f;
            public float compoundHullTargetSegmentExtent = 8f;
            public float compoundHullSegmentOverlapRatio = 0.08f;
            public string generatedToolVersion = "";
            public string generatedParameterProfile = "";
            public string[] generatedMeshPathTokens =
            {
                "GeneratedCollision",
                "CollisionMesh",
                "Simplified",
                "VHACD",
                "V-HACD",
                "CoACD",
            };
            public string[] decompositionToolNames =
            {
                "VHACD",
                "V-HACD",
                "CoACD",
            };
        }
    }
}
#endif

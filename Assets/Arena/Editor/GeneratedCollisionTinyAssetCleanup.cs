#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    internal static class GeneratedCollisionTinyAssetCleanup
    {
        private const string VariantRoot = "Assets/Arena/Content/Prefabs/OpenWorld/EnvironmentVariants";
        private const string SettingsPath = "Assets/Arena/Content/Settings/OpenWorld/generated_collision_optimizer_settings.json";
        private const string MovementCollisionChildName = "ArenaGameplayCollision";
        private const string QueryCollisionChildName = "ArenaGameplayQueryCollision";
        private const string GeneratedMovementRootName = "ArenaGeneratedMovementCollision";
        private const string GameplayQueryCollisionLayer = "GameplayQueryCollision";

        [MenuItem("Arena/OpenWorld/Collision Optimization/3 Remove Selected Tiny Collision", false, 520)]
        private static void RemoveSelectedTinyCollision()
        {
            OptimizerSettings settings = LoadSettings();
            List<string> assetPaths = ResolveSelectedVariantAssetPaths();
            if (assetPaths.Count == 0)
            {
                Debug.LogError(
                    "[GeneratedCollisionTinyAssetCleanup] Select one or more Arena environment variant prefab assets, " +
                    $"Project folders under {VariantRoot}, or scene instances of those variants.");
                return;
            }

            int queryLayer = LayerMask.NameToLayer(GameplayQueryCollisionLayer);
            var stats = new CleanupStats();
            foreach (string assetPath in assetPaths)
                RemoveForPrefabAsset(assetPath, queryLayer, settings, stats);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            Debug.Log(
                $"[GeneratedCollisionTinyAssetCleanup] Processed {stats.ProcessedAssets} asset(s). " +
                $"Updated: {stats.UpdatedAssets}; skipped not tiny/no-query-mesh: {stats.SkippedNotTiny}; " +
                $"removed collision roots: {stats.RemovedCollisionRoots}.");
        }

        private static void RemoveForPrefabAsset(
            string assetPath,
            int queryLayer,
            OptimizerSettings settings,
            CleanupStats stats)
        {
            stats.ProcessedAssets++;
            GameObject root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                VisualBounds visualBounds = CalculateVisualBounds(root);
                bool hasQueryMesh = HasQueryMeshCollider(root, queryLayer);
                bool isTiny = visualBounds.MeshCount > 0 &&
                              visualBounds.MaxExtent <= settings.tinyNoQueryCollisionMaxSourceExtent &&
                              visualBounds.Volume <= settings.tinyNoQueryCollisionMaxSourceVolume;

                if (!isTiny || hasQueryMesh)
                {
                    stats.SkippedNotTiny++;
                    return;
                }

                List<GameObject> collisionRoots = CollectTopLevelArenaCollisionRoots(root);
                if (collisionRoots.Count == 0)
                {
                    stats.SkippedNotTiny++;
                    return;
                }

                foreach (GameObject collisionRoot in collisionRoots)
                    UnityEngine.Object.DestroyImmediate(collisionRoot);

                PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                stats.UpdatedAssets++;
                stats.RemovedCollisionRoots += collisionRoots.Count;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static bool HasQueryMeshCollider(GameObject root, int queryLayer)
            => root.GetComponentsInChildren<MeshCollider>(includeInactive: false)
                .Any(collider =>
                    collider != null &&
                    collider.enabled &&
                    !collider.isTrigger &&
                    collider.sharedMesh != null &&
                    (collider.gameObject.name == QueryCollisionChildName ||
                     collider.gameObject.name.StartsWith(QueryCollisionChildName + "_", StringComparison.Ordinal) ||
                     (queryLayer >= 0 && collider.gameObject.layer == queryLayer)));

        private static List<GameObject> CollectTopLevelArenaCollisionRoots(GameObject root)
        {
            var matching = root.GetComponentsInChildren<Transform>(includeInactive: true)
                .Where(transform => transform != root.transform && IsArenaCollisionRootName(transform.name))
                .ToList();

            return matching
                .Where(transform => !matching.Any(candidate => candidate != transform && transform.IsChildOf(candidate)))
                .OrderBy(transform => HierarchyPath(transform), StringComparer.Ordinal)
                .Select(transform => transform.gameObject)
                .ToList();
        }

        private static bool IsArenaCollisionRootName(string name)
            => name == MovementCollisionChildName ||
               name.StartsWith(MovementCollisionChildName + "_", StringComparison.Ordinal) ||
               name == QueryCollisionChildName ||
               name.StartsWith(QueryCollisionChildName + "_", StringComparison.Ordinal) ||
               name == GeneratedMovementRootName ||
               name.StartsWith(GeneratedMovementRootName + "_", StringComparison.Ordinal);

        private static VisualBounds CalculateVisualBounds(GameObject root)
        {
            Bounds? bounds = null;
            int meshCount = 0;
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(includeInactive: false)
                         .OrderBy(filter => HierarchyPath(filter.transform), StringComparer.Ordinal))
            {
                Renderer? renderer = filter.GetComponent<Renderer>();
                if (filter.sharedMesh == null || renderer == null || !ShouldUseRenderer(renderer))
                    continue;

                meshCount++;
                foreach (Vector3 corner in BoundsCorners(renderer.bounds))
                {
                    Vector3 rootLocal = root.transform.InverseTransformPoint(corner);
                    if (bounds.HasValue)
                    {
                        Bounds expanded = bounds.Value;
                        expanded.Encapsulate(rootLocal);
                        bounds = expanded;
                    }
                    else
                    {
                        bounds = new Bounds(rootLocal, Vector3.zero);
                    }
                }
            }

            Bounds output = bounds ?? new Bounds(Vector3.zero, Vector3.zero);
            return new VisualBounds(meshCount, Mathf.Max(output.size.x, Mathf.Max(output.size.y, output.size.z)), BoundsVolume(output));
        }

        private static bool ShouldUseRenderer(Renderer renderer)
            => renderer.enabled &&
               renderer is not ParticleSystemRenderer &&
               renderer is not TrailRenderer &&
               renderer is not LineRenderer;

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

        private static float BoundsVolume(Bounds bounds)
            => Mathf.Max(0f, bounds.size.x) * Mathf.Max(0f, bounds.size.y) * Mathf.Max(0f, bounds.size.z);

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
                Debug.LogError($"[GeneratedCollisionTinyAssetCleanup] Failed to read '{SettingsPath}': {exception.Message}");
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

        private readonly struct VisualBounds
        {
            public VisualBounds(int meshCount, float maxExtent, float volume)
            {
                MeshCount = meshCount;
                MaxExtent = maxExtent;
                Volume = volume;
            }

            public int MeshCount { get; }
            public float MaxExtent { get; }
            public float Volume { get; }
        }

        [Serializable]
        private sealed class OptimizerSettings
        {
            public float tinyNoQueryCollisionMaxSourceExtent = 0.75f;
            public float tinyNoQueryCollisionMaxSourceVolume = 0.25f;
        }

        private sealed class CleanupStats
        {
            public int ProcessedAssets;
            public int UpdatedAssets;
            public int SkippedNotTiny;
            public int RemovedCollisionRoots;
        }
    }
}
#endif

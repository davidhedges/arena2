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
    internal static class GeneratedMovementCollisionCandidateGenerator
    {
        private const string VariantRoot = "Assets/Arena/Content/Prefabs/OpenWorld/EnvironmentVariants";
        private const string GeneratedCollisionRoot = "Assets/Arena/Content/Prefabs/OpenWorld/GeneratedCollision";
        private const string SettingsPath = "Assets/Arena/Content/Settings/OpenWorld/generated_collision_optimizer_settings.json";
        private const string GameplayCollisionLayer = "GameplayCollision";
        private const string GameplayQueryCollisionLayer = "GameplayQueryCollision";
        private const string GeneratedMovementRootName = "ArenaGeneratedMovementCollision";
        private const string QueryCollisionChildName = "ArenaGameplayQueryCollision";
        private const string GeneratorProfile = "support_silhouette_compound_adaptive_v1";
        private const int ReviewCandidateLayer = 0;
        private static readonly SupportProfile TallSupportProfile = new("tall_s7_r8", 7, 8);
        private static readonly SupportProfile BalancedSupportProfile = new("balanced_s5_r12", 5, 12);
        private static readonly SupportProfile WideSupportProfile = new("wide_s4_r14", 4, 14);
        private const float DegenerateTriangleAreaSquaredEpsilon = 0.000000000001f;
        private const float MinimumSupportRadius = 0.01f;

        [MenuItem("Arena/OpenWorld/Collision Optimization/2 Generate Selected Movement Hull Candidates", false, 510)]
        private static void GenerateSelectedMovementHullCandidates()
        {
            int gameplayLayer = LayerMask.NameToLayer(GameplayCollisionLayer);
            int queryLayer = LayerMask.NameToLayer(GameplayQueryCollisionLayer);
            if (gameplayLayer < 0 || queryLayer < 0)
            {
                Debug.LogError(
                    $"[GeneratedMovementCollisionCandidateGenerator] Required layers are missing: " +
                    $"{GameplayCollisionLayer}={gameplayLayer}, {GameplayQueryCollisionLayer}={queryLayer}.");
                return;
            }

            List<string> assetPaths = ResolveSelectedVariantAssetPaths();
            if (assetPaths.Count == 0)
            {
                Debug.LogError(
                    "[GeneratedMovementCollisionCandidateGenerator] Select one or more Arena environment variant prefab assets, " +
                    $"Project folders under {VariantRoot}, or scene instances of those variants.");
                return;
            }

            OptimizerSettings settings = LoadSettings();
            var stats = new GenerationStats();
            foreach (string assetPath in assetPaths)
                GenerateForPrefabAsset(assetPath, queryLayer, settings, stats);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            Debug.Log(
                $"[GeneratedMovementCollisionCandidateGenerator] Processed {stats.ProcessedAssets} asset(s). " +
                $"Updated: {stats.UpdatedAssets}; skipped no solid baseline: {stats.SkippedNoSolidBaseline}; " +
                $"generated hull candidates: {stats.GeneratedHullCandidates}; mesh assets written: {stats.MeshAssetsWritten}; " +
                $"compound source meshes: {stats.CompoundAssets}; " +
                $"profile: {GeneratorProfile}; support profiles: {stats.ProfileSummary()}. " +
                $"Old {GameplayCollisionLayer} boxes were preserved for review/export safety; generated candidates stay on Default until accepted.");
        }

        private static void GenerateForPrefabAsset(
            string assetPath,
            int queryLayer,
            OptimizerSettings settings,
            GenerationStats stats)
        {
            stats.ProcessedAssets++;
            GameObject root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                List<MeshCollider> queryMeshes = CollectQueryMeshColliders(root, queryLayer);
                if (queryMeshes.Count == 0)
                {
                    stats.SkippedNoSolidBaseline++;
                    Debug.LogWarning(
                        $"[GeneratedMovementCollisionCandidateGenerator] Skipping '{assetPath}': no query MeshCollider baseline found. " +
                        "Run collision role prep or author query/source geometry before generating movement candidates.");
                    return;
                }

                GameObject generatedRoot = GetOrCreateDirectChild(root, GeneratedMovementRootName);
                generatedRoot.layer = ReviewCandidateLayer;
                ClearGeneratedChildren(generatedRoot);

                string assetDirectory = EnsureGeneratedAssetDirectory(assetPath);
                int colliderIndex = 0;
                foreach (MeshCollider queryMesh in queryMeshes)
                {
                    colliderIndex++;
                    Mesh sourceMesh = queryMesh.sharedMesh!;
                    string baseMeshName = $"{root.name}_MovementHull_{colliderIndex:00}";
                    List<GeneratedHullMesh> generatedMeshes = BuildCandidateMeshesFromSource(sourceMesh, baseMeshName, settings);
                    if (generatedMeshes.Count > 1)
                        stats.CompoundAssets++;

                    int segmentIndex = 0;
                    foreach (GeneratedHullMesh generated in generatedMeshes)
                    {
                        segmentIndex++;
                        string meshAssetPath = $"{assetDirectory}/{generated.Mesh.name}.asset";
                        Mesh persistedMesh = WriteMeshAsset(meshAssetPath, generated.Mesh);
                        stats.MeshAssetsWritten++;
                        stats.AddProfile(generated.Profile.Id);

                        GameObject hullObject = new($"{GeneratedMovementRootName}_{SanitizeObjectName(queryMesh.gameObject.name)}_{colliderIndex:00}_{segmentIndex:00}");
                        hullObject.layer = ReviewCandidateLayer;
                        hullObject.transform.SetParent(generatedRoot.transform, false);
                        CopyWorldTransformIntoParentChild(queryMesh.transform, generatedRoot.transform, hullObject.transform);

                        MeshCollider hullCollider = hullObject.AddComponent<MeshCollider>();
                        hullCollider.sharedMesh = persistedMesh;
                        hullCollider.convex = true;
                        hullCollider.isTrigger = false;
                        hullCollider.enabled = true;
                        stats.GeneratedHullCandidates++;
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                stats.UpdatedAssets++;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static List<MeshCollider> CollectQueryMeshColliders(GameObject root, int queryLayer)
            => root.GetComponentsInChildren<MeshCollider>(includeInactive: false)
                .Where(collider =>
                    collider != null &&
                    collider.enabled &&
                    !collider.isTrigger &&
                    collider.sharedMesh != null &&
                    (collider.gameObject.layer == queryLayer ||
                     collider.gameObject.name == QueryCollisionChildName ||
                     collider.gameObject.name.StartsWith(QueryCollisionChildName + "_", StringComparison.Ordinal)))
                .OrderBy(collider => HierarchyPath(collider.transform), StringComparer.Ordinal)
                .ToList();

        private static List<GeneratedHullMesh> BuildCandidateMeshesFromSource(Mesh sourceMesh, string baseMeshName, OptimizerSettings settings)
        {
            Vector3[] sourceVertices = sourceMesh.vertices;
            int[] sourceTriangles = sourceMesh.triangles;
            List<Vector3> usableVertices = CollectUsableVertices(sourceVertices, sourceTriangles);
            if (usableVertices.Count == 0)
                usableVertices = sourceVertices.Where(IsFinite).ToList();

            if (usableVertices.Count == 0)
            {
                var empty = new Mesh { name = baseMeshName };
                empty.RecalculateBounds();
                return new List<GeneratedHullMesh> { new(empty, BalancedSupportProfile) };
            }

            Bounds bounds = BoundsFromVertices(usableVertices);
            List<List<Vector3>> segments = BuildCompoundSegments(usableVertices, bounds, settings);
            var generatedMeshes = new List<GeneratedHullMesh>(segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                string meshName = segments.Count == 1
                    ? baseMeshName
                    : $"{baseMeshName}_Part_{i + 1:00}";
                generatedMeshes.Add(BuildSupportMesh(segments[i], meshName));
            }

            return generatedMeshes;
        }

        private static GeneratedHullMesh BuildSupportMesh(IReadOnlyList<Vector3> usableVertices, string meshName)
        {
            Bounds bounds = BoundsFromVertices(usableVertices);
            SupportProfile supportProfile = SelectSupportProfile(bounds);
            Vector2 center = new(bounds.center.x, bounds.center.z);
            float height = Mathf.Max(bounds.size.y, MinimumSupportRadius);
            float bandHalfHeight = Mathf.Max(height / (supportProfile.SliceCount - 1), MinimumSupportRadius);
            var vertices = new List<Vector3>(supportProfile.VertexCount);
            for (int slice = 0; slice < supportProfile.SliceCount; slice++)
            {
                float t = supportProfile.SliceCount == 1 ? 0.5f : (float)slice / (supportProfile.SliceCount - 1);
                float y = Mathf.Lerp(bounds.min.y, bounds.max.y, t);
                for (int sector = 0; sector < supportProfile.SectorCount; sector++)
                {
                    float angle = Mathf.PI * 2f * sector / supportProfile.SectorCount;
                    Vector2 direction = new(Mathf.Cos(angle), Mathf.Sin(angle));
                    Vector2 supportPoint = FindSupportPoint(usableVertices, center, y, bandHalfHeight, direction);
                    vertices.Add(new Vector3(supportPoint.x, y, supportPoint.y));
                }
            }

            int bottomCenterIndex = vertices.Count;
            vertices.Add(new Vector3(center.x, bounds.min.y, center.y));
            int topCenterIndex = vertices.Count;
            vertices.Add(new Vector3(center.x, bounds.max.y, center.y));

            var triangles = new List<int>(supportProfile.SliceCount * supportProfile.SectorCount * 6);
            for (int slice = 0; slice < supportProfile.SliceCount - 1; slice++)
            {
                int lower = slice * supportProfile.SectorCount;
                int upper = (slice + 1) * supportProfile.SectorCount;
                for (int sector = 0; sector < supportProfile.SectorCount; sector++)
                {
                    int next = (sector + 1) % supportProfile.SectorCount;
                    triangles.Add(lower + sector);
                    triangles.Add(upper + sector);
                    triangles.Add(upper + next);
                    triangles.Add(lower + sector);
                    triangles.Add(upper + next);
                    triangles.Add(lower + next);
                }
            }

            int topRing = (supportProfile.SliceCount - 1) * supportProfile.SectorCount;
            for (int sector = 0; sector < supportProfile.SectorCount; sector++)
            {
                int next = (sector + 1) % supportProfile.SectorCount;
                triangles.Add(bottomCenterIndex);
                triangles.Add(next);
                triangles.Add(sector);

                triangles.Add(topCenterIndex);
                triangles.Add(topRing + sector);
                triangles.Add(topRing + next);
            }

            var mesh = new Mesh
            {
                name = meshName,
                vertices = vertices.ToArray(),
                triangles = triangles.ToArray(),
            };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return new GeneratedHullMesh(mesh, supportProfile);
        }

        private static List<List<Vector3>> BuildCompoundSegments(
            IReadOnlyList<Vector3> vertices,
            Bounds bounds,
            OptimizerSettings settings)
        {
            float longestExtent = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            float horizontalMax = Mathf.Max(Mathf.Max(bounds.size.x, bounds.size.z), MinimumSupportRadius);
            float heightRatio = bounds.size.y / horizontalMax;
            int segmentCount = settings.enableAdaptiveCompoundMovementHulls
                ? Mathf.Clamp(Mathf.CeilToInt(longestExtent / Mathf.Max(settings.compoundHullTargetSegmentExtent, MinimumSupportRadius)), 1, Mathf.Max(1, settings.compoundHullMaxSegments))
                : 1;

            if (segmentCount <= 1 ||
                longestExtent < settings.compoundHullMinSourceMaxExtent ||
                heightRatio < settings.compoundHullMinHeightRatio)
            {
                return new List<List<Vector3>> { vertices.ToList() };
            }

            int axis = SelectSplitAxis(bounds);
            float min = AxisValue(bounds.min, axis);
            float max = AxisValue(bounds.max, axis);
            float segmentLength = (max - min) / segmentCount;
            if (segmentLength <= MinimumSupportRadius)
                return new List<List<Vector3>> { vertices.ToList() };

            float overlap = segmentLength * Mathf.Clamp(settings.compoundHullSegmentOverlapRatio, 0f, 0.45f);
            var segments = new List<List<Vector3>>(segmentCount);
            for (int segment = 0; segment < segmentCount; segment++)
            {
                float segmentMin = min + segmentLength * segment - overlap;
                float segmentMax = min + segmentLength * (segment + 1) + overlap;
                List<Vector3> segmentVertices = vertices
                    .Where(vertex =>
                    {
                        float value = AxisValue(vertex, axis);
                        return value >= segmentMin && value <= segmentMax;
                    })
                    .ToList();

                if (segmentVertices.Count >= 4)
                    segments.Add(segmentVertices);
            }

            return segments.Count > 1 ? segments : new List<List<Vector3>> { vertices.ToList() };
        }

        private static int SelectSplitAxis(Bounds bounds)
        {
            if (bounds.size.y >= bounds.size.x && bounds.size.y >= bounds.size.z)
                return 1;
            return bounds.size.x >= bounds.size.z ? 0 : 2;
        }

        private static float AxisValue(Vector3 value, int axis)
            => axis switch
            {
                0 => value.x,
                1 => value.y,
                _ => value.z,
            };

        private static SupportProfile SelectSupportProfile(Bounds bounds)
        {
            float horizontalMax = Mathf.Max(Mathf.Max(bounds.size.x, bounds.size.z), MinimumSupportRadius);
            float horizontalMin = Mathf.Max(Mathf.Min(bounds.size.x, bounds.size.z), MinimumSupportRadius);
            float heightRatio = bounds.size.y / horizontalMax;
            float horizontalRatio = horizontalMax / horizontalMin;

            if (heightRatio >= 1.35f)
                return TallSupportProfile;
            if (heightRatio <= 0.55f || horizontalRatio >= 1.75f)
                return WideSupportProfile;
            return BalancedSupportProfile;
        }

        private static List<Vector3> CollectUsableVertices(Vector3[] sourceVertices, int[] sourceTriangles)
        {
            var usedIndices = new SortedSet<int>();
            for (int i = 0; i + 2 < sourceTriangles.Length; i += 3)
            {
                int a = sourceTriangles[i];
                int b = sourceTriangles[i + 1];
                int c = sourceTriangles[i + 2];
                if (a < 0 || b < 0 || c < 0 ||
                    a >= sourceVertices.Length ||
                    b >= sourceVertices.Length ||
                    c >= sourceVertices.Length)
                {
                    continue;
                }

                if (Vector3.Cross(sourceVertices[b] - sourceVertices[a], sourceVertices[c] - sourceVertices[a]).sqrMagnitude <= DegenerateTriangleAreaSquaredEpsilon)
                    continue;

                usedIndices.Add(a);
                usedIndices.Add(b);
                usedIndices.Add(c);
            }

            return usedIndices
                .Select(index => sourceVertices[index])
                .Where(IsFinite)
                .ToList();
        }

        private static Bounds BoundsFromVertices(IReadOnlyList<Vector3> vertices)
        {
            Bounds bounds = new(vertices[0], Vector3.zero);
            for (int i = 1; i < vertices.Count; i++)
                bounds.Encapsulate(vertices[i]);
            return bounds;
        }

        private static Vector2 FindSupportPoint(
            IReadOnlyList<Vector3> vertices,
            Vector2 center,
            float y,
            float bandHalfHeight,
            Vector2 direction)
        {
            float best = float.NegativeInfinity;
            Vector2 bestPoint = center + direction * MinimumSupportRadius;
            bool foundInBand = false;
            foreach (Vector3 vertex in vertices)
            {
                if (Mathf.Abs(vertex.y - y) > bandHalfHeight)
                    continue;

                Vector2 offset = new(vertex.x - center.x, vertex.z - center.y);
                float score = Vector2.Dot(offset, direction);
                if (score > best)
                {
                    best = score;
                    bestPoint = new Vector2(vertex.x, vertex.z);
                }
                foundInBand = true;
            }

            if (foundInBand)
                return bestPoint;

            foreach (Vector3 vertex in vertices)
            {
                Vector2 offset = new(vertex.x - center.x, vertex.z - center.y);
                float yPenalty = Mathf.Abs(vertex.y - y) * 0.25f;
                float score = Vector2.Dot(offset, direction) - yPenalty;
                if (score > best)
                {
                    best = score;
                    bestPoint = new Vector2(vertex.x, vertex.z);
                }
            }

            return bestPoint;
        }

        private static bool IsFinite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static Mesh WriteMeshAsset(string meshAssetPath, Mesh mesh)
        {
            Mesh? existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, meshAssetPath);
                AssetDatabase.SaveAssetIfDirty(mesh);
                AssetDatabase.ImportAsset(meshAssetPath, ImportAssetOptions.ForceSynchronousImport);
                return AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath) ?? mesh;
            }

            existing.Clear();
            existing.name = mesh.name;
            existing.vertices = mesh.vertices;
            existing.triangles = mesh.triangles;
            existing.RecalculateBounds();
            existing.RecalculateNormals();
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssetIfDirty(existing);
            AssetDatabase.ImportAsset(meshAssetPath, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath) ?? existing;
        }

        private static GameObject GetOrCreateDirectChild(GameObject root, string childName)
        {
            foreach (Transform child in root.transform)
            {
                if (child.name == childName)
                    return child.gameObject;
            }

            GameObject created = new(childName);
            created.transform.SetParent(root.transform, false);
            return created;
        }

        private static void ClearGeneratedChildren(GameObject generatedRoot)
        {
            for (int i = generatedRoot.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(generatedRoot.transform.GetChild(i).gameObject);
        }

        private static string EnsureGeneratedAssetDirectory(string variantAssetPath)
        {
            string relative = variantAssetPath.Substring(VariantRoot.Length).Trim('/');
            string directory = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
            string prefabName = Path.GetFileNameWithoutExtension(relative);
            string targetDirectory = string.IsNullOrEmpty(directory)
                ? $"{GeneratedCollisionRoot}/{prefabName}"
                : $"{GeneratedCollisionRoot}/{directory}/{prefabName}";
            EnsureAssetFolder(targetDirectory);
            return targetDirectory;
        }

        private static void EnsureAssetFolder(string folderPath)
        {
            string[] parts = folderPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            string currentPath = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string nextPath = $"{currentPath}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(nextPath))
                    AssetDatabase.CreateFolder(currentPath, parts[i]);

                currentPath = nextPath;
            }
        }

        private static void CopyWorldTransformIntoParentChild(Transform source, Transform parent, Transform target)
        {
            target.SetParent(parent, worldPositionStays: false);
            target.SetPositionAndRotation(source.position, source.rotation);
            Vector3 parentScale = parent.lossyScale;
            Vector3 sourceScale = source.lossyScale;
            target.localScale = new Vector3(
                SafeScaleDivide(sourceScale.x, parentScale.x),
                SafeScaleDivide(sourceScale.y, parentScale.y),
                SafeScaleDivide(sourceScale.z, parentScale.z));
        }

        private static float SafeScaleDivide(float numerator, float denominator)
            => Mathf.Abs(denominator) <= 0.0001f ? numerator : numerator / denominator;

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
                Debug.LogError($"[GeneratedMovementCollisionCandidateGenerator] Failed to read '{SettingsPath}': {exception.Message}");
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

        private static string SanitizeObjectName(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            string sanitized = new string(chars);
            while (sanitized.Contains("__", StringComparison.Ordinal))
                sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);
            return sanitized.Trim('_');
        }

        private sealed class GenerationStats
        {
            private readonly Dictionary<string, int> profileCounts = new(StringComparer.Ordinal);
            public int ProcessedAssets;
            public int UpdatedAssets;
            public int SkippedNoSolidBaseline;
            public int GeneratedHullCandidates;
            public int MeshAssetsWritten;
            public int CompoundAssets;

            public void AddProfile(string profileId)
            {
                profileCounts.TryGetValue(profileId, out int count);
                profileCounts[profileId] = count + 1;
            }

            public string ProfileSummary()
                => profileCounts.Count == 0
                    ? "none"
                    : string.Join(", ", profileCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        }

        private readonly struct SupportProfile
        {
            public SupportProfile(string id, int sliceCount, int sectorCount)
            {
                Id = id;
                SliceCount = sliceCount;
                SectorCount = sectorCount;
            }

            public string Id { get; }
            public int SliceCount { get; }
            public int SectorCount { get; }
            public int VertexCount => SliceCount * SectorCount + 2;
        }

        private readonly struct GeneratedHullMesh
        {
            public GeneratedHullMesh(Mesh mesh, SupportProfile profile)
            {
                Mesh = mesh;
                Profile = profile;
            }

            public Mesh Mesh { get; }
            public SupportProfile Profile { get; }
        }

        [Serializable]
        private sealed class OptimizerSettings
        {
            public bool enableAdaptiveCompoundMovementHulls = true;
            public int compoundHullMaxSegments = 3;
            public float compoundHullMinSourceMaxExtent = 3f;
            public float compoundHullMinHeightRatio = 0.45f;
            public float compoundHullTargetSegmentExtent = 8f;
            public float compoundHullSegmentOverlapRatio = 0.08f;
        }
    }
}
#endif

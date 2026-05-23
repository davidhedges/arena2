#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.Editor
{
    internal static class OpenWorldToonEnvironmentVariantTools
    {
        private const string ThirdPartyEnvironmentRoot = "Assets/ThirdParty/AssetStore/Environments";
        private const string VariantRoot = "Assets/Arena/Content/Prefabs/OpenWorld/EnvironmentVariants";
        private const string SettingsPath = "Assets/Arena/Content/Settings/OpenWorld/toon_variant_generation_settings.json";
        private const string GameplayCollisionLayerName = "GameplayCollision";
        private const string GeneratedCollisionChildName = "ArenaGameplayCollision";
        private const string VariantSuffix = "_Arena";

        private static readonly HashSet<string> IncludedPackages = new HashSet<string>(StringComparer.Ordinal)
        {
            "ToonAdventureIsland",
            "ToonDesertedTemples",
            "ToonEnchantedMeadow",
            "ToonGoldenValley",
        };

        [MenuItem("Arena/OpenWorld/Scene Prep/1 Generate + Replace Toon Variants", false, 100)]
        private static void GenerateVariantsAndReplaceActiveScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
            {
                Debug.LogError("[OpenWorldToonEnvironmentVariantTools] Open a saved scene before replacing prefab instances.");
                return;
            }

            ToonVariantGenerationSettings settings = LoadSettings();
            List<string> sceneSourcePaths = CollectSourcePrefabsForScene(scene.path);
            List<string> sourcePaths = sceneSourcePaths
                .Where(settings.ShouldGenerateCollider)
                .ToList();
            VariantStats stats = EnsureVariants(sourcePaths);
            LogVariantStats(scene.path, stats);
            Debug.Log(
                $"[OpenWorldToonEnvironmentVariantTools] Skipped {sceneSourcePaths.Count - sourcePaths.Count} " +
                $"Toon prefab(s) in '{scene.name}' because of package settings.");
            ReplaceToonPrefabsInActiveScene(settings);
            ReplaceDirectModelPrefabsInActiveScene(settings, dryRun: false);
        }

        [MenuItem("Arena/OpenWorld/Scene Prep/1b Audit Direct Model Prefab Instances", false, 110)]
        private static void AuditDirectModelPrefabsInActiveScene()
        {
            ToonVariantGenerationSettings settings = LoadSettings();
            ReplaceDirectModelPrefabsInActiveScene(settings, dryRun: true);
        }

        [MenuItem("Arena/OpenWorld/Scene Prep/1c Replace Direct Model Prefab Instances", false, 120)]
        private static void ReplaceDirectModelPrefabsInActiveScene()
        {
            ToonVariantGenerationSettings settings = LoadSettings();
            ReplaceDirectModelPrefabsInActiveScene(settings, dryRun: false);
        }

        private static void ReplaceToonPrefabsInActiveScene(ToonVariantGenerationSettings settings)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Debug.LogError("[OpenWorldToonEnvironmentVariantTools] No active scene is loaded.");
                return;
            }

            List<PrefabReplacement> replacements = CollectPrefabReplacements(scene, settings);
            if (replacements.Count == 0)
            {
                Debug.Log("[OpenWorldToonEnvironmentVariantTools] No replaceable Toon prefab instances found in the active scene.");
                return;
            }

            Undo.SetCurrentGroupName("Replace Toon Prefabs With Arena Variants");
            int undoGroup = Undo.GetCurrentGroup();
            int replaced = 0;
            int missingVariants = 0;

            foreach (PrefabReplacement replacement in replacements)
            {
                GameObject? targetPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(replacement.TargetPath);
                if (targetPrefab == null)
                {
                    missingVariants++;
                    Debug.LogWarning(
                        $"[OpenWorldToonEnvironmentVariantTools] Missing replacement target for '{replacement.CurrentPrefabPath}'. " +
                        $"Expected '{replacement.TargetPath}'.");
                    continue;
                }

                GameObject? newInstance = PrefabUtility.InstantiatePrefab(targetPrefab, scene) as GameObject;
                if (newInstance == null)
                {
                    Debug.LogWarning($"[OpenWorldToonEnvironmentVariantTools] Failed to instantiate '{replacement.TargetPath}'.");
                    continue;
                }

                Undo.RegisterCreatedObjectUndo(newInstance, "Replace Toon Prefab");
                CopyScenePlacement(replacement.Instance, newInstance);
                Undo.DestroyObjectImmediate(replacement.Instance);
                replaced++;
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (replaced > 0)
                EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                $"[OpenWorldToonEnvironmentVariantTools] Replaced {replaced} Toon prefab instance(s) in '{scene.name}'. " +
                $"Missing targets: {missingVariants}.");
        }

        private static void ReplaceDirectModelPrefabsInActiveScene(ToonVariantGenerationSettings settings, bool dryRun)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Debug.LogError("[OpenWorldToonEnvironmentVariantTools] No active scene is loaded.");
                return;
            }

            List<PrefabReplacement> replacements = CollectDirectModelPrefabReplacements(scene, settings, out DirectModelRepairStats stats);
            string mode = dryRun ? "Audit" : "Repair";

            foreach (string warning in stats.Warnings)
                Debug.LogWarning($"[OpenWorldToonEnvironmentVariantTools] {warning}");

            if (dryRun)
            {
                foreach (PrefabReplacement replacement in replacements)
                {
                    Debug.Log(
                        $"[OpenWorldToonEnvironmentVariantTools] Direct model instance '{replacement.Instance.name}' " +
                        $"can be replaced: '{replacement.CurrentPrefabPath}' -> '{replacement.TargetPath}'.");
                }

                Debug.Log(
                    $"[OpenWorldToonEnvironmentVariantTools] {mode} direct model prefab instances in '{scene.name}': " +
                    $"scanned {stats.DirectModelInstances}, replaceable {replacements.Count}, unresolved {stats.Unresolved}.");
                return;
            }

            if (replacements.Count == 0)
            {
                Debug.Log(
                    $"[OpenWorldToonEnvironmentVariantTools] No replaceable direct model prefab instances found in '{scene.name}'. " +
                    $"Scanned {stats.DirectModelInstances}, unresolved {stats.Unresolved}.");
                return;
            }

            Undo.SetCurrentGroupName("Replace Direct Model Prefabs With Toon Prefabs");
            int undoGroup = Undo.GetCurrentGroup();
            int replaced = 0;
            int missingTargets = 0;

            foreach (PrefabReplacement replacement in replacements)
            {
                GameObject? targetPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(replacement.TargetPath);
                if (targetPrefab == null)
                {
                    missingTargets++;
                    Debug.LogWarning(
                        $"[OpenWorldToonEnvironmentVariantTools] Missing direct model replacement target for " +
                        $"'{replacement.CurrentPrefabPath}'. Expected '{replacement.TargetPath}'.");
                    continue;
                }

                GameObject? newInstance = PrefabUtility.InstantiatePrefab(targetPrefab, scene) as GameObject;
                if (newInstance == null)
                {
                    Debug.LogWarning($"[OpenWorldToonEnvironmentVariantTools] Failed to instantiate '{replacement.TargetPath}'.");
                    continue;
                }

                Undo.RegisterCreatedObjectUndo(newInstance, "Replace Direct Model Prefab");
                CopyScenePlacement(replacement.Instance, newInstance);
                Undo.DestroyObjectImmediate(replacement.Instance);
                replaced++;
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (replaced > 0)
                EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                $"[OpenWorldToonEnvironmentVariantTools] Replaced {replaced} direct model prefab instance(s) in '{scene.name}'. " +
                $"Scanned {stats.DirectModelInstances}, unresolved {stats.Unresolved}, missing targets: {missingTargets}.");
        }

        private static VariantStats EnsureVariants(IReadOnlyCollection<string> sourcePaths)
        {
            var stats = new VariantStats();
            Scene previewScene = EditorSceneManager.NewPreviewScene();

            try
            {
                foreach (string sourcePath in sourcePaths.OrderBy(path => path, StringComparer.Ordinal))
                {
                    GameObject? sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
                    if (sourcePrefab == null)
                    {
                        stats.Failed++;
                        Debug.LogWarning($"[OpenWorldToonEnvironmentVariantTools] Could not load source prefab '{sourcePath}'.");
                        continue;
                    }

                    string variantPath = GetVariantPath(sourcePath);
                    GameObject? existingVariant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
                    if (existingVariant != null)
                    {
                        if (HasGameplayCollisionCollider(existingVariant))
                        {
                            stats.Existing++;
                            continue;
                        }

                        if (AddGeneratedColliderToExistingVariant(existingVariant, variantPath, previewScene, out bool generatedCollider))
                        {
                            if (generatedCollider)
                            {
                                stats.UpdatedExisting++;
                                stats.GeneratedColliders++;
                            }
                            else
                            {
                                stats.Existing++;
                            }
                        }
                        else
                        {
                            stats.Failed++;
                        }

                        continue;
                    }

                    EnsureAssetFolder(GetDirectoryName(variantPath));

                    GameObject? instance = PrefabUtility.InstantiatePrefab(sourcePrefab, previewScene) as GameObject;
                    if (instance == null)
                    {
                        stats.Failed++;
                        Debug.LogWarning($"[OpenWorldToonEnvironmentVariantTools] Could not instantiate source prefab '{sourcePath}'.");
                        continue;
                    }

                    try
                    {
                        instance.name = Path.GetFileNameWithoutExtension(variantPath);
                        if (!EnsureGeneratedBoxCollider(instance))
                        {
                            stats.SkippedNoRendererBounds++;
                            continue;
                        }

                        stats.GeneratedColliders++;

                        bool success;
                        PrefabUtility.SaveAsPrefabAsset(instance, variantPath, out success);
                        if (success)
                        {
                            stats.Created++;
                        }
                        else
                        {
                            stats.Failed++;
                            Debug.LogWarning($"[OpenWorldToonEnvironmentVariantTools] Failed to save variant '{variantPath}'.");
                        }
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(instance);
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(previewScene);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            return stats;
        }

        private static bool EnsureGeneratedBoxCollider(GameObject root)
        {
            if (HasGameplayCollisionCollider(root))
                return false;

            if (!TryGetLocalRendererBounds(root.transform, out Bounds bounds))
                return false;

            GameObject collision = new GameObject(GeneratedCollisionChildName);
            collision.transform.SetParent(root.transform, false);

            int gameplayCollisionLayer = LayerMask.NameToLayer(GameplayCollisionLayerName);
            if (gameplayCollisionLayer >= 0)
                collision.layer = gameplayCollisionLayer;

            BoxCollider collider = collision.AddComponent<BoxCollider>();
            collider.center = bounds.center;
            collider.size = ClampColliderSize(bounds.size);
            collider.isTrigger = false;
            return true;
        }

        private static bool HasGameplayCollisionCollider(GameObject root)
        {
            int gameplayCollisionLayer = LayerMask.NameToLayer(GameplayCollisionLayerName);
            foreach (BoxCollider collider in root.GetComponentsInChildren<BoxCollider>(true))
            {
                if (!collider.enabled || collider.isTrigger)
                    continue;

                if (collider.gameObject.name == GeneratedCollisionChildName)
                    return true;

                if (gameplayCollisionLayer >= 0 && collider.gameObject.layer == gameplayCollisionLayer)
                    return true;
            }

            foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
            {
                if (!collider.enabled || collider.isTrigger)
                    continue;

                if (collider.gameObject.name == GeneratedCollisionChildName)
                    return true;

                if (gameplayCollisionLayer >= 0 && collider.gameObject.layer == gameplayCollisionLayer)
                    return true;
            }

            return false;
        }

        private static bool AddGeneratedColliderToExistingVariant(
            GameObject existingVariant,
            string variantPath,
            Scene previewScene,
            out bool generatedCollider)
        {
            generatedCollider = false;
            GameObject? instance = PrefabUtility.InstantiatePrefab(existingVariant, previewScene) as GameObject;
            if (instance == null)
            {
                Debug.LogWarning($"[OpenWorldToonEnvironmentVariantTools] Could not instantiate existing variant '{variantPath}'.");
                return false;
            }

            try
            {
                generatedCollider = EnsureGeneratedBoxCollider(instance);
                if (!generatedCollider)
                    return true;

                bool success;
                PrefabUtility.SaveAsPrefabAsset(instance, variantPath, out success);
                if (!success)
                    Debug.LogWarning($"[OpenWorldToonEnvironmentVariantTools] Failed to update existing variant '{variantPath}'.");

                return success;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static bool TryGetLocalRendererBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!ShouldUseRendererForColliderBounds(renderer))
                    continue;

                Bounds rendererBounds = renderer.bounds;
                foreach (Vector3 worldCorner in GetBoundsCorners(rendererBounds))
                {
                    Vector3 localCorner = root.InverseTransformPoint(worldCorner);
                    if (!hasBounds)
                    {
                        bounds = new Bounds(localCorner, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(localCorner);
                    }
                }
            }

            return hasBounds;
        }

        private static bool ShouldUseRendererForColliderBounds(Renderer renderer)
        {
            return renderer.enabled &&
                   renderer is not ParticleSystemRenderer &&
                   renderer is not TrailRenderer &&
                   renderer is not LineRenderer;
        }

        private static IEnumerable<Vector3> GetBoundsCorners(Bounds bounds)
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

        private static Vector3 ClampColliderSize(Vector3 size)
        {
            const float minimumSize = 0.01f;
            return new Vector3(
                Mathf.Max(size.x, minimumSize),
                Mathf.Max(size.y, minimumSize),
                Mathf.Max(size.z, minimumSize));
        }

        private static List<string> CollectSourcePrefabsForScene(string scenePath)
        {
            return AssetDatabase.GetDependencies(scenePath, true)
                .Where(IsIncludedSourcePrefab)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        private static List<PrefabReplacement> CollectPrefabReplacements(Scene scene, ToonVariantGenerationSettings settings)
        {
            var replacements = new List<PrefabReplacement>();
            var visited = new HashSet<GameObject>();

            foreach (GameObject rootObject in scene.GetRootGameObjects())
                CollectPrefabReplacements(rootObject.transform, settings, replacements, visited);

            return replacements;
        }

        private static List<PrefabReplacement> CollectDirectModelPrefabReplacements(
            Scene scene,
            ToonVariantGenerationSettings settings,
            out DirectModelRepairStats stats)
        {
            var replacements = new List<PrefabReplacement>();
            var visited = new HashSet<GameObject>();
            stats = new DirectModelRepairStats();

            foreach (GameObject rootObject in scene.GetRootGameObjects())
                CollectDirectModelPrefabReplacements(rootObject.transform, settings, replacements, visited, stats);

            return replacements;
        }

        private static void CollectPrefabReplacements(
            Transform transform,
            ToonVariantGenerationSettings settings,
            List<PrefabReplacement> replacements,
            HashSet<GameObject> visited)
        {
            GameObject gameObject = transform.gameObject;
            GameObject? outermostRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
            if (outermostRoot == gameObject && visited.Add(gameObject))
            {
                string currentPrefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
                if (TryGetReplacementTargetPath(currentPrefabPath, settings, out string targetPath))
                {
                    replacements.Add(new PrefabReplacement(gameObject, currentPrefabPath, targetPath));
                    return;
                }
            }

            for (int i = 0; i < transform.childCount; i++)
                CollectPrefabReplacements(transform.GetChild(i), settings, replacements, visited);
        }

        private static void CollectDirectModelPrefabReplacements(
            Transform transform,
            ToonVariantGenerationSettings settings,
            List<PrefabReplacement> replacements,
            HashSet<GameObject> visited,
            DirectModelRepairStats stats)
        {
            GameObject gameObject = transform.gameObject;
            GameObject? outermostRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
            if (outermostRoot == gameObject && visited.Add(gameObject))
            {
                string currentPrefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
                if (IsIncludedModelPrefab(currentPrefabPath))
                {
                    stats.DirectModelInstances++;
                    if (TryGetDirectModelReplacementTargetPath(
                            currentPrefabPath,
                            settings,
                            out string targetPath,
                            out string warning))
                    {
                        replacements.Add(new PrefabReplacement(gameObject, currentPrefabPath, targetPath));
                        return;
                    }

                    stats.Unresolved++;
                    if (!string.IsNullOrEmpty(warning))
                        stats.Warnings.Add($"{gameObject.name}: {warning}");
                }
            }

            for (int i = 0; i < transform.childCount; i++)
                CollectDirectModelPrefabReplacements(transform.GetChild(i), settings, replacements, visited, stats);
        }

        private static bool TryGetReplacementTargetPath(
            string currentPrefabPath,
            ToonVariantGenerationSettings settings,
            out string targetPath)
        {
            targetPath = "";

            if (IsIncludedSourcePrefab(currentPrefabPath))
            {
                if (!settings.ShouldGenerateCollider(currentPrefabPath))
                    return false;

                targetPath = GetVariantPath(currentPrefabPath);
                return ShouldUseVariant(targetPath);
            }

            if (TryGetSourcePathFromVariantPath(currentPrefabPath, out string sourcePath) &&
                (!settings.ShouldGenerateCollider(sourcePath) || !ShouldUseVariant(currentPrefabPath)))
            {
                targetPath = sourcePath;
                return true;
            }

            return false;
        }

        private static bool TryGetDirectModelReplacementTargetPath(
            string modelPrefabPath,
            ToonVariantGenerationSettings settings,
            out string targetPath,
            out string warning)
        {
            targetPath = "";
            warning = "";

            if (!TryGetSourcePrefabPathFromModelPrefabPath(modelPrefabPath, out string sourcePrefabPath, out warning))
                return false;

            if (!settings.ShouldGenerateCollider(sourcePrefabPath))
            {
                targetPath = sourcePrefabPath;
                return true;
            }

            string variantPath = GetVariantPath(sourcePrefabPath);
            if (ShouldUseVariant(variantPath))
            {
                targetPath = variantPath;
                return true;
            }

            warning =
                $"Direct model prefab '{modelPrefabPath}' maps to '{sourcePrefabPath}', but the expected Arena variant " +
                $"'{variantPath}' is missing or does not contain gameplay collision.";
            return false;
        }

        private static bool ShouldUseVariant(string variantPath)
        {
            GameObject? variantPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
            return variantPrefab != null && HasGameplayCollisionCollider(variantPrefab);
        }

        private static void CopyScenePlacement(GameObject source, GameObject target)
        {
            Transform sourceTransform = source.transform;
            Transform targetTransform = target.transform;
            Transform? parent = sourceTransform.parent;
            int siblingIndex = sourceTransform.GetSiblingIndex();

            target.name = source.name;
            target.SetActive(source.activeSelf);
            target.tag = source.tag;
            target.layer = source.layer;
            GameObjectUtility.SetStaticEditorFlags(target, GameObjectUtility.GetStaticEditorFlags(source));

            targetTransform.SetParent(parent, false);
            targetTransform.localPosition = sourceTransform.localPosition;
            targetTransform.localRotation = sourceTransform.localRotation;
            targetTransform.localScale = sourceTransform.localScale;
            targetTransform.SetSiblingIndex(siblingIndex);
        }

        private static bool IsIncludedSourcePrefab(string assetPath)
        {
            return TryGetSourcePackageAndRelativePath(assetPath, out _, out _);
        }

        private static bool IsIncludedModelPrefab(string assetPath)
        {
            return TryGetModelPackageAndName(assetPath, out _, out _);
        }

        private static bool TryGetSourcePackageAndRelativePath(
            string assetPath,
            out string packageName,
            out string relativePath)
        {
            packageName = "";
            relativePath = "";
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!assetPath.StartsWith(ThirdPartyEnvironmentRoot + "/", StringComparison.Ordinal))
                return false;

            foreach (string includedPackageName in IncludedPackages)
            {
                string packagePrefabRoot = $"{ThirdPartyEnvironmentRoot}/{includedPackageName}/Prefabs/";
                if (!assetPath.StartsWith(packagePrefabRoot, StringComparison.Ordinal))
                    continue;

                packageName = includedPackageName;
                relativePath = assetPath.Substring(packagePrefabRoot.Length);
                return true;
            }

            return false;
        }

        private static bool TryGetModelPackageAndName(
            string assetPath,
            out string packageName,
            out string modelName)
        {
            packageName = "";
            modelName = "";
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!assetPath.StartsWith(ThirdPartyEnvironmentRoot + "/", StringComparison.Ordinal))
                return false;

            foreach (string includedPackageName in IncludedPackages)
            {
                string packageModelRoot = $"{ThirdPartyEnvironmentRoot}/{includedPackageName}/Models/";
                if (!assetPath.StartsWith(packageModelRoot, StringComparison.Ordinal))
                    continue;

                packageName = includedPackageName;
                modelName = Path.GetFileNameWithoutExtension(assetPath);
                return !string.IsNullOrEmpty(modelName);
            }

            return false;
        }

        private static bool TryGetSourcePrefabPathFromModelPrefabPath(
            string modelPrefabPath,
            out string sourcePrefabPath,
            out string warning)
        {
            sourcePrefabPath = "";
            warning = "";

            if (!TryGetModelPackageAndName(modelPrefabPath, out string packageName, out string modelName))
                return false;

            string packagePrefabRoot = $"{ThirdPartyEnvironmentRoot}/{packageName}/Prefabs";
            string[] guids = AssetDatabase.FindAssets($"{modelName} t:Prefab", new[] { packagePrefabRoot });
            List<string> matches = guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path =>
                    IsIncludedSourcePrefab(path) &&
                    string.Equals(Path.GetFileNameWithoutExtension(path), modelName, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            if (matches.Count == 1)
            {
                sourcePrefabPath = matches[0];
                return true;
            }

            if (matches.Count == 0)
            {
                warning = $"Direct model prefab '{modelPrefabPath}' has no matching source prefab named '{modelName}.prefab'.";
                return false;
            }

            warning =
                $"Direct model prefab '{modelPrefabPath}' has multiple matching source prefabs named '{modelName}.prefab': " +
                string.Join(", ", matches);
            return false;
        }

        private static string GetVariantPath(string sourcePath)
        {
            foreach (string packageName in IncludedPackages)
            {
                string packagePrefabRoot = $"{ThirdPartyEnvironmentRoot}/{packageName}/Prefabs/";
                if (!sourcePath.StartsWith(packagePrefabRoot, StringComparison.Ordinal))
                    continue;

                string relativePath = sourcePath.Substring(packagePrefabRoot.Length);
                string relativeDirectory = GetDirectoryName(relativePath);
                string prefabName = Path.GetFileNameWithoutExtension(relativePath);
                string variantFileName = $"{prefabName}{VariantSuffix}.prefab";

                if (string.IsNullOrEmpty(relativeDirectory))
                    return $"{VariantRoot}/{packageName}/{variantFileName}";

                return $"{VariantRoot}/{packageName}/{relativeDirectory}/{variantFileName}";
            }

            throw new InvalidOperationException($"Source path is not an included Toon prefab: {sourcePath}");
        }

        private static bool TryGetSourcePathFromVariantPath(string variantPath, out string sourcePath)
        {
            sourcePath = "";
            if (string.IsNullOrEmpty(variantPath) ||
                !variantPath.StartsWith(VariantRoot + "/", StringComparison.Ordinal) ||
                !variantPath.EndsWith($"{VariantSuffix}.prefab", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (string packageName in IncludedPackages)
            {
                string packageVariantRoot = $"{VariantRoot}/{packageName}/";
                if (!variantPath.StartsWith(packageVariantRoot, StringComparison.Ordinal))
                    continue;

                string relativeVariantPath = variantPath.Substring(packageVariantRoot.Length);
                string relativeDirectory = GetDirectoryName(relativeVariantPath);
                string variantName = Path.GetFileNameWithoutExtension(relativeVariantPath);
                string sourceName = variantName.Substring(0, variantName.Length - VariantSuffix.Length);

                if (string.IsNullOrEmpty(relativeDirectory))
                    sourcePath = $"{ThirdPartyEnvironmentRoot}/{packageName}/Prefabs/{sourceName}.prefab";
                else
                    sourcePath = $"{ThirdPartyEnvironmentRoot}/{packageName}/Prefabs/{relativeDirectory}/{sourceName}.prefab";

                return IsIncludedSourcePrefab(sourcePath);
            }

            return false;
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

        private static string GetDirectoryName(string path)
        {
            return (Path.GetDirectoryName(path) ?? "").Replace('\\', '/');
        }

        private static void LogVariantStats(string scope, VariantStats stats)
        {
            Debug.Log(
                $"[OpenWorldToonEnvironmentVariantTools] Generated Toon variants for {scope}. " +
                $"Created: {stats.Created}, existing: {stats.Existing}, updated existing: {stats.UpdatedExisting}, " +
                $"generated colliders: {stats.GeneratedColliders}, " +
                $"skipped no renderer bounds: {stats.SkippedNoRendererBounds}, failed: {stats.Failed}.");
        }

        private static ToonVariantGenerationSettings LoadSettings()
        {
            string absolutePath = Path.Combine(Directory.GetCurrentDirectory(), SettingsPath);
            if (!File.Exists(absolutePath))
                return new ToonVariantGenerationSettings();

            try
            {
                return JsonUtility.FromJson<ToonVariantGenerationSettings>(File.ReadAllText(absolutePath)) ??
                       new ToonVariantGenerationSettings();
            }
            catch (Exception exception)
            {
                Debug.LogError($"[OpenWorldToonEnvironmentVariantTools] Failed to read '{SettingsPath}': {exception.Message}");
                return new ToonVariantGenerationSettings();
            }
        }

        private sealed class VariantStats
        {
            public int Created;
            public int Existing;
            public int UpdatedExisting;
            public int GeneratedColliders;
            public int SkippedNoRendererBounds;
            public int Failed;
        }

        private sealed class DirectModelRepairStats
        {
            public int DirectModelInstances;
            public int Unresolved;
            public readonly List<string> Warnings = new List<string>();
        }

        [Serializable]
        private sealed class ToonVariantGenerationSettings
        {
            public PackageColliderRule[] packageRules = Array.Empty<PackageColliderRule>();

            public bool ShouldGenerateCollider(string sourcePath)
            {
                if (!TryGetSourcePackageAndRelativePath(sourcePath, out string packageName, out string relativePath))
                    return true;

                PackageColliderRule? packageRule = FindPackageRule(packageName);
                return packageRule == null || packageRule.ShouldGenerateCollider(relativePath);
            }

            private PackageColliderRule? FindPackageRule(string packageName)
            {
                foreach (PackageColliderRule packageRule in packageRules)
                {
                    if (string.Equals(packageRule.packageName, packageName, StringComparison.OrdinalIgnoreCase))
                        return packageRule;
                }

                return null;
            }
        }

        [Serializable]
        private sealed class PackageColliderRule
        {
            public string packageName = "";
            public string[] skipColliderCategoryPaths = Array.Empty<string>();
            public string[] skipColliderPathContains = Array.Empty<string>();
            public string[] allowColliderPathContains = Array.Empty<string>();

            public bool ShouldGenerateCollider(string relativePath)
            {
                if (MatchesAny(relativePath, allowColliderPathContains))
                    return true;

                if (MatchesAnyCategory(relativePath, skipColliderCategoryPaths))
                    return false;

                if (MatchesAny(relativePath, skipColliderPathContains))
                    return false;

                return true;
            }

            private static bool MatchesAnyCategory(string relativePath, IEnumerable<string> categoryPaths)
            {
                foreach (string categoryPath in categoryPaths)
                {
                    if (string.IsNullOrWhiteSpace(categoryPath))
                        continue;

                    string normalizedCategory = categoryPath.Trim().Trim('/');
                    if (string.Equals(relativePath, normalizedCategory, StringComparison.OrdinalIgnoreCase) ||
                        relativePath.StartsWith(normalizedCategory + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool MatchesAny(string relativePath, IEnumerable<string> tokens)
            {
                foreach (string token in tokens)
                {
                    if (!string.IsNullOrWhiteSpace(token) &&
                        relativePath.IndexOf(token.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private readonly struct PrefabReplacement
        {
            public readonly GameObject Instance;
            public readonly string CurrentPrefabPath;
            public readonly string TargetPath;

            public PrefabReplacement(GameObject instance, string currentPrefabPath, string targetPath)
            {
                Instance = instance;
                CurrentPrefabPath = currentPrefabPath;
                TargetPath = targetPath;
            }
        }
    }
}
#endif

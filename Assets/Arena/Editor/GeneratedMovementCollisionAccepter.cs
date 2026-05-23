#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    internal static class GeneratedMovementCollisionAccepter
    {
        private const string VariantRoot = "Assets/Arena/Content/Prefabs/OpenWorld/EnvironmentVariants";
        private const string GameplayCollisionLayer = "GameplayCollision";
        private const string GameplayCollisionRootName = "ArenaGameplayCollision";
        private const string GeneratedMovementRootName = "ArenaGeneratedMovementCollision";

        [MenuItem("Arena/OpenWorld/Collision Optimization/4 Accept Selected Generated Movement Collision", false, 530)]
        private static void AcceptSelectedGeneratedMovementCollision()
        {
            int gameplayLayer = LayerMask.NameToLayer(GameplayCollisionLayer);
            if (gameplayLayer < 0)
            {
                Debug.LogError(
                    $"[GeneratedMovementCollisionAccepter] Required layer is missing: {GameplayCollisionLayer}.");
                return;
            }

            List<string> assetPaths = ResolveSelectedVariantAssetPaths();
            if (assetPaths.Count == 0)
            {
                Debug.LogError(
                    "[GeneratedMovementCollisionAccepter] Select one or more Arena environment variant prefab assets, " +
                    $"Project folders under {VariantRoot}, or scene instances of those variants.");
                return;
            }

            var stats = new AcceptStats();
            foreach (string assetPath in assetPaths)
                AcceptForPrefabAsset(assetPath, gameplayLayer, stats);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            Debug.Log(
                $"[GeneratedMovementCollisionAccepter] Processed {stats.ProcessedAssets} asset(s). " +
                $"Accepted: {stats.AcceptedAssets}; skipped missing generated hulls: {stats.SkippedMissingGeneratedHulls}; " +
                $"skipped invalid generated hulls: {stats.SkippedInvalidGeneratedHulls}; " +
                $"old movement roots removed: {stats.RemovedOldMovementRoots}; accepted hull colliders: {stats.AcceptedHullColliders}. " +
                $"Run the world-data exporter after accepting to write convex movement hulls into collision JSON.");
        }

        private static void AcceptForPrefabAsset(string assetPath, int gameplayLayer, AcceptStats stats)
        {
            stats.ProcessedAssets++;
            GameObject root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                Transform? generatedRoot = FindDirectChild(root.transform, GeneratedMovementRootName);
                if (generatedRoot == null)
                {
                    stats.SkippedMissingGeneratedHulls++;
                    Debug.LogWarning(
                        $"[GeneratedMovementCollisionAccepter] Skipping '{assetPath}': no {GeneratedMovementRootName} child exists.");
                    return;
                }

                List<MeshCollider> generatedColliders = generatedRoot
                    .GetComponentsInChildren<MeshCollider>(includeInactive: false)
                    .Where(collider => collider != null && collider.enabled && !collider.isTrigger)
                    .OrderBy(collider => HierarchyPath(collider.transform), StringComparer.Ordinal)
                    .ToList();

                if (generatedColliders.Count == 0 ||
                    generatedColliders.Any(collider => collider.sharedMesh == null || !collider.convex))
                {
                    stats.SkippedInvalidGeneratedHulls++;
                    Debug.LogError(
                        $"[GeneratedMovementCollisionAccepter] Skipping '{assetPath}': generated movement hulls must be enabled convex MeshColliders with shared meshes.");
                    return;
                }

                stats.RemovedOldMovementRoots += RemoveDirectChildren(root.transform, IsGameplayCollisionRoot);

                GameObject gameplayRoot = new(GameplayCollisionRootName);
                gameplayRoot.layer = gameplayLayer;
                gameplayRoot.transform.SetParent(root.transform, false);
                ResetLocalTransform(gameplayRoot.transform);

                foreach (MeshCollider generatedCollider in generatedColliders)
                {
                    GameObject acceptedHull = UnityEngine.Object.Instantiate(generatedCollider.gameObject);
                    acceptedHull.name = BuildAcceptedHullName(generatedCollider.gameObject.name);
                    acceptedHull.transform.SetParent(gameplayRoot.transform, worldPositionStays: true);
                    SetLayerRecursively(acceptedHull, gameplayLayer);

                    foreach (MeshCollider collider in acceptedHull.GetComponentsInChildren<MeshCollider>(includeInactive: true))
                    {
                        collider.enabled = true;
                        collider.isTrigger = false;
                        collider.convex = true;
                    }

                    stats.AcceptedHullColliders += acceptedHull
                        .GetComponentsInChildren<MeshCollider>(includeInactive: true)
                        .Length;
                }

                UnityEngine.Object.DestroyImmediate(generatedRoot.gameObject);
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                stats.AcceptedAssets++;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static int RemoveDirectChildren(Transform root, Func<GameObject, bool> predicate)
        {
            var toRemove = new List<GameObject>();
            for (int i = 0; i < root.childCount; i++)
            {
                GameObject child = root.GetChild(i).gameObject;
                if (predicate(child))
                    toRemove.Add(child);
            }

            foreach (GameObject child in toRemove)
                UnityEngine.Object.DestroyImmediate(child);
            return toRemove.Count;
        }

        private static bool IsGameplayCollisionRoot(GameObject gameObject)
            => gameObject.name == GameplayCollisionRootName ||
               gameObject.name.StartsWith(GameplayCollisionRootName + "_", StringComparison.Ordinal);

        private static string BuildAcceptedHullName(string generatedName)
        {
            if (generatedName.StartsWith(GeneratedMovementRootName, StringComparison.Ordinal))
                return GameplayCollisionRootName + generatedName.Substring(GeneratedMovementRootName.Length);
            return GameplayCollisionRootName + "_" + generatedName;
        }

        private static void SetLayerRecursively(GameObject gameObject, int layer)
        {
            gameObject.layer = layer;
            foreach (Transform child in gameObject.transform)
                SetLayerRecursively(child.gameObject, layer);
        }

        private static Transform? FindDirectChild(Transform root, string childName)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name == childName)
                    return child;
            }
            return null;
        }

        private static void ResetLocalTransform(Transform transform)
        {
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
        }

        private static string HierarchyPath(Transform transform)
        {
            var names = new Stack<string>();
            Transform? current = transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names);
        }

        private static List<string> ResolveSelectedVariantAssetPaths()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnityEngine.Object selected in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(selected);
                AddSelectedPath(path, paths);

                if (selected is GameObject selectedGameObject)
                {
                    GameObject? prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(selectedGameObject);
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

            if (AssetDatabase.IsValidFolder(path))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { path }))
                    AddSelectedPath(AssetDatabase.GUIDToAssetPath(guid), paths);
                return;
            }

            if (IsArenaEnvironmentVariantPrefab(path))
                paths.Add(path);
        }

        private static bool IsArenaEnvironmentVariantPrefab(string path)
            => path.StartsWith(VariantRoot + "/", StringComparison.Ordinal) &&
               path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

        private sealed class AcceptStats
        {
            public int ProcessedAssets;
            public int AcceptedAssets;
            public int SkippedMissingGeneratedHulls;
            public int SkippedInvalidGeneratedHulls;
            public int RemovedOldMovementRoots;
            public int AcceptedHullColliders;
        }
    }
}
#endif

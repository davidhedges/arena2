using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DungeonLab.Editor
{
    internal static class ReviewedStairSourceResolver
    {
        private const string PackageFloorPrefabRoot = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/01_PARTS/Floor/";
        private const string PackageStairPrefabSurfaceRoot = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/01_PARTS/Stairs/Stairs/";
        private const string PackageStairMeshSurfaceRoot = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/3d/modular/Stairs/Stairs/";

        public static bool IsContractSurfacePath(string path)
        {
            string normalized = NormalizePath(path);
            return
                (normalized.StartsWith(PackageFloorPrefabRoot, StringComparison.Ordinal) &&
                    normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) ||
                (normalized.StartsWith(PackageStairPrefabSurfaceRoot, StringComparison.Ordinal) &&
                    normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) ||
                (normalized.StartsWith(PackageStairMeshSurfaceRoot, StringComparison.Ordinal) &&
                    normalized.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsSourceRoot(Transform source, HashSet<string> contractSources)
        {
            if (!TryGetMatchingSurfacePath(source, contractSources, out string sourcePath))
            {
                return false;
            }

            TryGetMatchingSurfacePath(source.parent, contractSources, out string parentSourcePath);
            return !string.Equals(parentSourcePath, sourcePath, StringComparison.Ordinal);
        }

        public static int CountSourceRoots(GameObject root, HashSet<string> contractSources)
        {
            if (root == null || contractSources == null || contractSources.Count == 0)
            {
                return 0;
            }

            int count = 0;
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (IsSourceRoot(transform, contractSources))
                {
                    count++;
                }
            }

            return count;
        }

        public static bool TryGetMatchingSurfacePath(
            Transform source,
            HashSet<string> contractSources,
            out string sourcePath)
        {
            sourcePath = string.Empty;
            if (source == null || contractSources == null)
            {
                return false;
            }

            string meshSourcePath = StructuralMeshSourcePath(source);
            if (!string.IsNullOrWhiteSpace(meshSourcePath) && contractSources.Contains(meshSourcePath))
            {
                sourcePath = meshSourcePath;
                return true;
            }

            GameObject nearestPrefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(source.gameObject);
            if (nearestPrefabRoot == source.gameObject)
            {
                string nearestPrefabPath = NormalizePath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source.gameObject));
                if (!string.IsNullOrWhiteSpace(nearestPrefabPath) && contractSources.Contains(nearestPrefabPath))
                {
                    sourcePath = nearestPrefabPath;
                    return true;
                }
            }

            string prefabSourcePath = PrefabSourcePath(source);
            if (!string.IsNullOrWhiteSpace(prefabSourcePath) && contractSources.Contains(prefabSourcePath))
            {
                sourcePath = prefabSourcePath;
                return true;
            }

            return false;
        }

        public static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim();
        }

        private static string PrefabSourcePath(Transform source)
        {
            if (source == null)
            {
                return string.Empty;
            }

            GameObject sourceObject = PrefabUtility.GetCorrespondingObjectFromOriginalSource(source.gameObject);
            if (sourceObject == null)
            {
                sourceObject = PrefabUtility.GetCorrespondingObjectFromSource(source.gameObject);
            }

            return sourceObject == null ? string.Empty : NormalizePath(AssetDatabase.GetAssetPath(sourceObject));
        }

        private static string StructuralMeshSourcePath(Transform source)
        {
            if (source == null)
            {
                return string.Empty;
            }

            MeshFilter meshFilter = source.GetComponent<MeshFilter>();
            string meshPath = meshFilter == null || meshFilter.sharedMesh == null
                ? string.Empty
                : NormalizePath(AssetDatabase.GetAssetPath(meshFilter.sharedMesh));
            if (IsContractSurfacePath(meshPath))
            {
                return meshPath;
            }

            MeshCollider meshCollider = source.GetComponent<MeshCollider>();
            meshPath = meshCollider == null || meshCollider.sharedMesh == null
                ? string.Empty
                : NormalizePath(AssetDatabase.GetAssetPath(meshCollider.sharedMesh));
            return IsContractSurfacePath(meshPath) ? meshPath : string.Empty;
        }
    }
}

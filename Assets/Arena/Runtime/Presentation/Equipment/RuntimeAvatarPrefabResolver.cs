#nullable enable
using System;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Shared runtime-avatar prefab selection. Runtime spawning and editor validation
    /// should both resolve the same prefab instead of hard-coding different assets.
    /// </summary>
    public static class RuntimeAvatarPrefabResolver
    {
        public const string PrimaryResourceName = "PlayerArmature";
        public const string FallbackResourceName = "PlayerArmature 1";

        public static bool IsValidRuntimePlayerPrefab(GameObject prefab)
        {
            return prefab.GetComponentInChildren<AvatarWeaponMounts>(true) != null
                && prefab.GetComponentInChildren<Animator>(true) != null;
        }

        public static string? ResolveRuntimePlayerPrefabResourceName()
        {
            GameObject? primary = Resources.Load<GameObject>(PrimaryResourceName);
            if (primary != null && IsValidRuntimePlayerPrefab(primary))
                return PrimaryResourceName;

            GameObject? fallback = Resources.Load<GameObject>(FallbackResourceName);
            if (fallback != null && IsValidRuntimePlayerPrefab(fallback))
                return FallbackResourceName;

            if (primary != null)
                return PrimaryResourceName;
            if (fallback != null)
                return FallbackResourceName;

            return null;
        }

        public static GameObject? LoadRuntimePlayerPrefab()
        {
            string? resourceName = ResolveRuntimePlayerPrefabResourceName();
            if (string.IsNullOrWhiteSpace(resourceName))
                return null;

            return Resources.Load<GameObject>(resourceName);
        }

        public static string? ResolveRuntimePlayerPrefabAssetPath()
        {
            string? resourceName = ResolveRuntimePlayerPrefabResourceName();
            return string.IsNullOrWhiteSpace(resourceName)
                ? null
                : $"Assets/Arena/Resources/{resourceName}.prefab";
        }

        public static bool ResolvedPrefabUsesFallback()
        {
            return string.Equals(
                ResolveRuntimePlayerPrefabResourceName(),
                FallbackResourceName,
                StringComparison.Ordinal);
        }
    }
}

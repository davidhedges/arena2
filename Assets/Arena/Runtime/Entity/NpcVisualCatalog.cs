#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Entity
{
    [CreateAssetMenu(menuName = "Arena/NPC Visual Catalog", fileName = "NpcVisualCatalog")]
    public sealed class NpcVisualCatalog : ScriptableObject
    {
        private const string DefaultResourcePath = "NpcVisualCatalog";

        [SerializeField] private List<NpcVisualCatalogEntry> entries = new();

        private Dictionary<string, UnityEngine.Object>? _prefabsByTemplateId;
        private static NpcVisualCatalog? _cachedDefault;

        public static bool TryLoadDefault(out NpcVisualCatalog catalog, out string error)
        {
            if (_cachedDefault == null)
                _cachedDefault = Resources.Load<NpcVisualCatalog>(DefaultResourcePath);

            if (_cachedDefault == null)
            {
                catalog = null!;
                error = $"Resources/{DefaultResourcePath} was not found.";
                return false;
            }

            catalog = _cachedDefault;
            error = string.Empty;
            return true;
        }

        public bool TryGetPrefab(string templateId, out UnityEngine.Object prefab)
        {
            EnsureIndex();
            string key = Normalize(templateId);
            return _prefabsByTemplateId!.TryGetValue(key, out prefab!) && prefab != null;
        }

        private void EnsureIndex()
        {
            if (_prefabsByTemplateId != null)
                return;

            _prefabsByTemplateId = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry == null)
                    continue;

                string key = Normalize(entry.templateId);
                if (!string.IsNullOrEmpty(key))
                {
                    UnityEngine.Object? prefab = entry.ResolvePrefab();
                    if (prefab != null)
                        _prefabsByTemplateId[key] = prefab;
                }
            }
        }

        private static string Normalize(string? value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }

    [Serializable]
    public sealed class NpcVisualCatalogEntry
    {
        public string templateId = string.Empty;
        public string assetPath = string.Empty;
        public UnityEngine.Object? prefab;

        public UnityEngine.Object? ResolvePrefab()
        {
#if UNITY_EDITOR
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                GameObject? loaded = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assetPath.Trim());
                if (loaded != null)
                    return loaded;
            }
#endif
            return prefab;
        }
    }
}

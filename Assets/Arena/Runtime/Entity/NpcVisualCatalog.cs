#nullable enable
using System;
using System.Collections.Generic;
using Arena.Presentation;
using UnityEngine;
using UnityEngine.Serialization;

namespace Arena.Entity
{
    [CreateAssetMenu(menuName = "Arena/NPC Visual Catalog", fileName = "NpcVisualCatalog")]
    public sealed class NpcVisualCatalog : ScriptableObject
    {
        private const string DefaultResourcePath = "NpcVisualCatalog";

        [SerializeField] private List<NpcVisualCatalogEntry> entries = new();

        private Dictionary<string, UnityEngine.Object>? _prefabsByVisualId;
        private Dictionary<string, NpcVisualCatalogEntry>? _entriesByVisualId;
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

        public bool TryGetPrefab(string visualId, out UnityEngine.Object prefab)
        {
            EnsureIndex();
            string key = Normalize(visualId);
            return _prefabsByVisualId!.TryGetValue(key, out prefab!) && prefab != null;
        }

        public bool TryGetEntry(string visualId, out NpcVisualCatalogEntry entry)
        {
            EnsureIndex();
            string key = Normalize(visualId);
            return _entriesByVisualId!.TryGetValue(key, out entry!) && entry != null;
        }

        public IReadOnlyList<string> ValidateEntries()
        {
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                NpcVisualCatalogEntry? entry = entries[i];
                if (entry == null)
                {
                    errors.Add($"Entry {i} is null.");
                    continue;
                }

                string key = Normalize(entry.visualId);
                if (string.IsNullOrEmpty(key))
                    errors.Add($"Entry {i} has no visual ID.");
                else if (!seen.Add(key))
                    errors.Add($"Visual ID '{key}' is duplicated.");

                if (entry.ResolvePrefab() == null)
                    errors.Add($"Visual '{key}' has no resolvable prefab.");
            }

            return errors;
        }

        private void EnsureIndex()
        {
            if (_prefabsByVisualId != null)
                return;

            _prefabsByVisualId = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            _entriesByVisualId = new Dictionary<string, NpcVisualCatalogEntry>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry == null)
                    continue;

                string key = Normalize(entry.visualId);
                if (!string.IsNullOrEmpty(key))
                {
                    _entriesByVisualId[key] = entry;
                    UnityEngine.Object? prefab = entry.ResolvePrefab();
                    if (prefab != null)
                        _prefabsByVisualId[key] = prefab;
                }
            }
        }

        private static string Normalize(string? value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }

    [Serializable]
    public sealed class NpcVisualCatalogEntry
    {
        [FormerlySerializedAs("templateId")]
        public string visualId = string.Empty;
        public NpcVisualProfile? profile;
        public string assetPath = string.Empty;
        public UnityEngine.Object? prefab;
        public List<NpcStatusReactionEntry> statusReactions = new();

        public UnityEngine.Object? ResolvePrefab()
        {
            if (profile != null && profile.Prefab != null)
                return profile.Prefab;

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

        public bool TryGetStatusReaction(string statusKind, out NpcStatusReactionEntry reaction)
        {
            string normalized = string.IsNullOrWhiteSpace(statusKind) ? string.Empty : statusKind.Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(normalized))
            {
                for (int i = 0; i < statusReactions.Count; i++)
                {
                    NpcStatusReactionEntry candidate = statusReactions[i];
                    if (candidate != null
                        && candidate.HasAny
                        && string.Equals(candidate.StatusKindOrEmpty, normalized, StringComparison.Ordinal))
                    {
                        reaction = candidate;
                        return true;
                    }
                }
            }

            reaction = null!;
            return false;
        }
    }
}

namespace Arena.Presentation
{
    [Serializable]
    public sealed class NpcStatusReactionEntry
    {
        public string statusKind = string.Empty;
        public AnimationClip? loop;
        public bool requireHumanoidAvatar = true;

        public string StatusKindOrEmpty
            => string.IsNullOrWhiteSpace(statusKind) ? string.Empty : statusKind.Trim().ToUpperInvariant();

        public bool HasAny => loop != null;
    }
}

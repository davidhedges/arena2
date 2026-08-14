#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Arena.Presentation.VFX
{
    [CreateAssetMenu(menuName = "Arena/Combat VFX Registry", fileName = "CombatVFXRegistry")]
    public sealed class CombatVFXRegistry : ScriptableObject
    {
        internal const string RegistryResourcePath = "CombatVFX/CombatVFXRegistry";

        [Serializable]
        public sealed class Entry
        {
            public string vfxId = string.Empty;
            public UnityEngine.Object prefab = null!;
            [Min(0f)] public float scale = 1f;
            [Tooltip("Position offset applied to FOLLOW_ANCHOR and FOLLOW_GROUND_POSITION VFX.")]
            public Vector3 localPositionOffset = Vector3.zero;
            [Tooltip("Prefab-space rotation correction composed after cue or animation-slot rotation.")]
            public Vector3 localEulerAngles = Vector3.zero;
            [Tooltip("Projectile-body scale multiplier at the end of its travel lifetime. Zero or one preserves the authored scale.")]
            [Range(0f, 1f)] public float scaleMultiplierAtLifetimeEnd = 1f;
            [Tooltip("For projectile-body prefabs with baked travel, disable particle translation and make emitted particles follow the authoritative projectile root. Particle rotation is preserved.")]
            public bool followAuthoritativeProjectileMotion;
            [Tooltip("Keep the projectile VFX root fixed at its spawn position because travel is already baked into the prefab. Gameplay motion remains authoritative and unaffected.")]
            public bool lockProjectileRootToSpawn;
        }

        public sealed class Template
        {
            public Template(
                string vfxId,
                GameObject prefab,
                float scale,
                float scaleMultiplierAtLifetimeEnd = 1f,
                Vector3 localPositionOffset = default,
                Vector3 localEulerAngles = default,
                bool followAuthoritativeProjectileMotion = false,
                bool lockProjectileRootToSpawn = false)
            {
                VfxId = vfxId;
                Prefab = prefab;
                Scale = scale > 0f ? scale : 1f;
                ScaleMultiplierAtLifetimeEnd = scaleMultiplierAtLifetimeEnd > 0f
                    ? Mathf.Clamp01(scaleMultiplierAtLifetimeEnd)
                    : 1f;
                LocalPositionOffset = localPositionOffset;
                LocalRotation = Quaternion.Euler(localEulerAngles);
                FollowAuthoritativeProjectileMotion = followAuthoritativeProjectileMotion;
                LockProjectileRootToSpawn = lockProjectileRootToSpawn;
            }

            public string VfxId { get; }
            public GameObject Prefab { get; }
            public float Scale { get; }
            public float ScaleMultiplierAtLifetimeEnd { get; }
            public Vector3 LocalPositionOffset { get; }
            public Quaternion LocalRotation { get; }
            public bool FollowAuthoritativeProjectileMotion { get; }
            public bool LockProjectileRootToSpawn { get; }
        }

        private static CombatVFXRegistry? _sharedRegistry;
        private static bool _missingSharedRegistryLogged;

        [SerializeField] private List<Entry> entries = new();

        private Dictionary<string, Template>? _templatesById;

        public IReadOnlyList<Entry> Entries => entries;

        public static CombatVFXRegistry? LoadShared()
        {
            if (_sharedRegistry != null)
                return _sharedRegistry;

            _sharedRegistry = Resources.Load<CombatVFXRegistry>(RegistryResourcePath);
            if (_sharedRegistry == null && !_missingSharedRegistryLogged)
            {
                _missingSharedRegistryLogged = true;
                Debug.LogWarning($"Combat VFX registry missing at Resources/{RegistryResourcePath}.");
            }

            return _sharedRegistry;
        }

        public static CombatVFXRegistry? ReloadShared()
        {
            if (_sharedRegistry != null)
                _sharedRegistry.InvalidateIndex();

            _sharedRegistry = null;
            _missingSharedRegistryLogged = false;
            CombatVFXRegistry? registry = LoadShared();
            registry?.InvalidateIndex();
            return registry;
        }

        internal static void RegisterPreloaded(CombatVFXRegistry? registry)
        {
            if (registry == null)
                return;

            _sharedRegistry = registry;
            _missingSharedRegistryLogged = false;
            registry.EnsureIndex();
        }

        public static GameObject? ResolveSharedPrefab(string vfxId)
        {
            return LoadShared()?.ResolvePrefab(vfxId);
        }

        public static Template? ResolveSharedTemplate(string vfxId)
        {
            return LoadShared()?.ResolveTemplate(vfxId);
        }

        public GameObject? ResolvePrefab(string vfxId)
        {
            return ResolveTemplate(vfxId)?.Prefab;
        }

        public Template? ResolveTemplate(string vfxId)
        {
            if (string.IsNullOrWhiteSpace(vfxId))
                return null;

            EnsureIndex();
            string normalized = WireIdentifier.Normalize(vfxId);
            return _templatesById != null && _templatesById.TryGetValue(normalized, out Template template)
                ? template
                : null;
        }

        private void EnsureIndex()
        {
            if (_templatesById != null)
                return;

            _templatesById = new Dictionary<string, Template>(StringComparer.Ordinal);
            foreach (Entry entry in entries)
            {
                if (entry.prefab == null || string.IsNullOrWhiteSpace(entry.vfxId))
                    continue;

                GameObject? prefab = entry.prefab switch
                {
                    GameObject gameObject => gameObject,
                    Component component => component.gameObject,
                    _ => null,
                };
                if (prefab != null)
                {
                    string normalizedId = WireIdentifier.Normalize(entry.vfxId);
                    _templatesById[normalizedId] = new Template(
                        normalizedId,
                        prefab,
                        entry.scale,
                        entry.scaleMultiplierAtLifetimeEnd,
                        entry.localPositionOffset,
                        entry.localEulerAngles,
                        entry.followAuthoritativeProjectileMotion,
                        entry.lockProjectileRootToSpawn);
                }
            }
        }

        public void InvalidateIndex()
        {
            _templatesById = null;
        }

        private void OnEnable()
        {
            InvalidateIndex();
        }

        private void OnValidate()
        {
            InvalidateIndex();
#if UNITY_EDITOR
            // Resources.Load can invoke OnValidate while the Editor is in Play mode.
            // Runtime warm-up owns validation-free cache creation; the dedicated
            // authoring validator remains the place for expensive prefab diagnostics.
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
#endif
            var errors = new List<string>();
            CollectAuthoringErrors(errors);
            foreach (string error in errors)
                Debug.LogError(error, this);
        }

        public void CollectAuthoringErrors(List<string> errors)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry entry in entries)
            {
                string normalizedId = WireIdentifier.Normalize(entry.vfxId);
                if (string.IsNullOrWhiteSpace(normalizedId))
                {
                    errors.Add("CombatVFXRegistry contains an entry with an empty vfxId.");
                    continue;
                }

                if (!seen.Add(normalizedId))
                    errors.Add($"CombatVFXRegistry contains duplicate normalized vfxId '{normalizedId}'.");

                if (entry.scale <= 0f)
                    errors.Add($"CombatVFXRegistry entry '{entry.vfxId}' scale must be positive.");

                GameObject? prefab = entry.prefab switch
                {
                    GameObject gameObject => gameObject,
                    Component component => component.gameObject,
                    _ => null,
                };
                if (prefab == null)
                {
                    errors.Add($"CombatVFXRegistry entry '{entry.vfxId}' has no prefab or component reference.");
                    continue;
                }

#if UNITY_EDITOR
                if (DescribeInvalidVisualOnlyPrefab(prefab, out string detail))
                    errors.Add(
                        $"CombatVFXRegistry entry '{entry.vfxId}' references prefab '{prefab.name}' with Collider or Rigidbody components. Combat VFX templates must be visual-only prefabs. {detail}");
#endif
            }
        }

#if UNITY_EDITOR
        private static bool DescribeInvalidVisualOnlyPrefab(GameObject prefab, out string detail)
        {
            Collider? collider = prefab.GetComponentInChildren<Collider>(true);
            if (collider != null)
            {
                detail = DescribeComponent(prefab, collider);
                return true;
            }

            Rigidbody? rigidbody = prefab.GetComponentInChildren<Rigidbody>(true);
            if (rigidbody != null)
            {
                detail = DescribeComponent(prefab, rigidbody);
                return true;
            }

            detail = string.Empty;
            return false;
        }

        private static string DescribeComponent(GameObject prefab, Component component)
        {
            string assetPath = AssetDatabase.GetAssetPath(prefab);
            string guid = string.IsNullOrWhiteSpace(assetPath) ? string.Empty : AssetDatabase.AssetPathToGUID(assetPath);
            string path = TransformPath(prefab.transform, component.transform);
            return $"Asset path: {assetPath}. GUID: {guid}. First offending component: {component.GetType().Name} on '{path}'.";
        }

        private static string TransformPath(Transform root, Transform transform)
        {
            if (transform == root)
                return root.name;

            var parts = new Stack<string>();
            Transform? current = transform;
            while (current != null)
            {
                parts.Push(current.name);
                if (current == root)
                    break;
                current = current.parent;
            }

            return string.Join("/", parts);
        }
#endif
    }
}

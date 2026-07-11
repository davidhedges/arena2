#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
using UnityEngine;
using UnityEngine.VFX;

namespace Arena.Presentation.VFX
{
    internal sealed class ProjectileVfxPool : IDisposable
    {
        private const int MaxInactivePerVfxId = 64;

        private readonly Dictionary<string, Stack<Rental>> _inactiveByVfxId = new(StringComparer.Ordinal);
        private readonly GameObject _poolRoot;
        private bool _disposed;

        public ProjectileVfxPool(string name)
        {
            _poolRoot = new GameObject(name);
            _poolRoot.hideFlags = HideFlags.HideInHierarchy;
            UnityEngine.Object.DontDestroyOnLoad(_poolRoot);
            _poolRoot.SetActive(false);
        }

        public Rental? TryRent(
            CombatVFXRegistry.Template template,
            CombatVFXRegistry.Template? trailTemplate,
            string instanceId)
        {
            if (_disposed || template.Prefab == null)
                return null;
            if (HasUnsafePoolingComponents(template.Prefab)
                || (trailTemplate != null && HasUnsafePoolingComponents(trailTemplate.Prefab)))
                return null;

            string key = CompositionKey(template.VfxId, trailTemplate?.VfxId);
            Rental? rental = null;
            try
            {
                if (!_inactiveByVfxId.TryGetValue(key, out Stack<Rental> inactive))
                {
                    inactive = new Stack<Rental>();
                    _inactiveByVfxId[key] = inactive;
                }

                rental = inactive.Count > 0
                    ? inactive.Pop()
                    : CreateRental(key, template, trailTemplate);
                rental.PrepareForRent(instanceId);
                return rental;
            }
            catch (Exception ex)
            {
                rental?.Destroy();
                Debug.LogWarning($"Failed to rent projectile VFX '{key}' from pool. Falling back to direct instantiate. {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (Stack<Rental> rentals in _inactiveByVfxId.Values)
            {
                while (rentals.Count > 0)
                    rentals.Pop().Destroy();
            }
            _inactiveByVfxId.Clear();

            if (_poolRoot != null)
                UnityEngine.Object.Destroy(_poolRoot);
        }

        private Rental CreateRental(
            string key,
            CombatVFXRegistry.Template template,
            CombatVFXRegistry.Template? trailTemplate)
        {
            var root = new GameObject("PooledProjectileVFX");
            root.SetActive(false);

            GameObject body = UnityEngine.Object.Instantiate(template.Prefab, root.transform, false);
            body.name = $"{template.Prefab.name}_Body";
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            body.transform.localScale = Vector3.one;
            VFXUtils.ApplyPrefabPresentationScale(body, template.Scale);

            if (trailTemplate != null)
            {
                GameObject trail = UnityEngine.Object.Instantiate(trailTemplate.Prefab, root.transform, false);
                trail.name = $"{trailTemplate.Prefab.name}_Trail";
                trail.transform.localPosition = Vector3.zero;
                trail.transform.localRotation = Quaternion.identity;
                trail.transform.localScale = Vector3.one;
                VFXUtils.ApplyPrefabPresentationScale(trail, trailTemplate.Scale);
            }

            var rental = new Rental(this, key, root, body, body.transform.localScale);
            rental.ReturnToPool();
            return rental;
        }

        private void Return(Rental rental)
        {
            if (_disposed)
            {
                rental.Destroy();
                return;
            }

            if (!_inactiveByVfxId.TryGetValue(rental.Key, out Stack<Rental> inactive))
            {
                inactive = new Stack<Rental>();
                _inactiveByVfxId[rental.Key] = inactive;
            }

            if (inactive.Count >= MaxInactivePerVfxId)
            {
                rental.Destroy();
                return;
            }

            rental.ReturnToPool();
            inactive.Push(rental);
        }

        private Transform PoolRootTransform => _poolRoot.transform;

        private static string CompositionKey(string bodyVfxId, string? trailVfxId)
            => $"{WireIdentifier.Normalize(bodyVfxId)}|{WireIdentifier.Normalize(trailVfxId)}";

        // VFX Graph (VisualEffect) state can't be safely reset between rentals — particles in flight have
        // no way to be re-keyed to a different cast — so prefabs containing one fall back to direct
        // instantiate. Rigidbody/Collider prefabs are already rejected upstream by CombatVFXRegistry.
        private static bool HasUnsafePoolingComponents(GameObject prefab)
        {
            return prefab.GetComponentsInChildren<VisualEffect>(true).Length > 0;
        }

        private static void StopAndClear(GameObject root)
        {
            ParticleSystem[] particleSystems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particleSystems.Length; i++)
            {
                particleSystems[i].Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
                particleSystems[i].Clear(withChildren: true);
            }

            TrailRenderer[] trails = root.GetComponentsInChildren<TrailRenderer>(true);
            for (int i = 0; i < trails.Length; i++)
                trails[i].Clear();
        }

        private static void PlayAwakeParticleSystems(GameObject root)
        {
            ParticleSystem[] particleSystems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particleSystems.Length; i++)
            {
                ParticleSystem system = particleSystems[i];
                if (system.main.playOnAwake)
                    system.Play(withChildren: true);
            }
        }

        internal sealed class Rental
        {
            private readonly ProjectileVfxPool _owner;
            private readonly Vector3 _bodyScale;
            private bool _returned = true;

            public Rental(ProjectileVfxPool owner, string key, GameObject root, GameObject body, Vector3 bodyScale)
            {
                _owner = owner;
                Key = key;
                Root = root;
                Body = body;
                _bodyScale = bodyScale;
            }

            public string Key { get; }
            public GameObject Root { get; }
            public GameObject Body { get; }

            public void PrepareForRent(string instanceId)
            {
                _returned = false;
                Root.name = $"VFX_Projectile_{ShortId(instanceId)}";
                Root.transform.SetParent(null, true);
                Root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                Body.transform.localPosition = Vector3.zero;
                Body.transform.localRotation = Quaternion.identity;
                Body.transform.localScale = _bodyScale;
                StopAndClear(Root);
                Root.SetActive(true);
                PlayAwakeParticleSystems(Root);
            }

            public void Return()
            {
                if (_returned)
                    return;

                _owner.Return(this);
            }

            public void Destroy()
            {
                _returned = true;
                if (Root != null)
                    UnityEngine.Object.Destroy(Root);
            }

            internal void ReturnToPool()
            {
                _returned = true;
                StopAndClear(Root);
                Root.transform.SetParent(_owner.PoolRootTransform, false);
                Root.transform.localPosition = Vector3.zero;
                Root.transform.localRotation = Quaternion.identity;
                Root.SetActive(false);
            }

            private static string ShortId(string value)
            {
                if (string.IsNullOrEmpty(value))
                    return "unknown";
                return value[..Mathf.Min(6, value.Length)];
            }
        }
    }
}

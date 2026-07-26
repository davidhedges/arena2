#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Interaction
{
    /// <summary>
    /// Scene-authored identity and geometry for one trap. Placement is exported
    /// to the paired trap manifest; timing, hazard extents and damage come from
    /// the referenced <see cref="TrapProfile"/>.
    ///
    /// Traps carry NO colliders: they never block movement, sight, or
    /// projectiles, and nothing here may leak into the immutable collision bake.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TrapAuthoring : MonoBehaviour
    {
        [SerializeField] private string _trapDefinitionId = string.Empty;
        [SerializeField] private string _worldDefinitionKey = "RANDOM_DUNGEON";
        [SerializeField] private bool _templateOnly;
        [SerializeField] private bool _productionEnabled;
        [SerializeField, Min(1)] private int _definitionVersion = 1;
        [SerializeField, Min(1)] private int _footprintCells = 1;
        [SerializeField] private TrapProfile? _profile;

        public string TrapDefinitionId => NormalizeId(_trapDefinitionId);
        public string WorldDefinitionKey => NormalizeId(_worldDefinitionKey);
        public bool TemplateOnly => _templateOnly;
        public bool ProductionEnabled => _productionEnabled;
        public int DefinitionVersion => Mathf.Max(1, _definitionVersion);
        public int FootprintCells => Mathf.Max(1, _footprintCells);
        public TrapProfile? Profile => _profile;

        /// <summary>Trap root in world space; every profile volume is relative to it.</summary>
        public Vector3 Origin => transform.position;

        public float YawDegrees => Mathf.Repeat(transform.eulerAngles.y, 360f);

        public string TrapProfileId => _profile == null ? string.Empty : _profile.ProfileId;

        public void Configure(
            string trapDefinitionId,
            string worldDefinitionKey,
            bool templateOnly,
            bool productionEnabled,
            int definitionVersion,
            int footprintCells,
            TrapProfile? profile)
        {
            _trapDefinitionId = NormalizeId(trapDefinitionId);
            _worldDefinitionKey = NormalizeId(worldDefinitionKey);
            _templateOnly = templateOnly;
            _productionEnabled = productionEnabled;
            _definitionVersion = Mathf.Max(1, definitionVersion);
            _footprintCells = Mathf.Max(1, footprintCells);
            _profile = profile;
        }

        public void SetDefinitionId(string trapDefinitionId)
        {
            _trapDefinitionId = NormalizeId(trapDefinitionId);
        }

        public void SetProductionEnabled(bool enabled)
        {
            _productionEnabled = enabled;
        }

        public void SetTemplateOnly(bool templateOnly)
        {
            _templateOnly = templateOnly;
        }

        /// <summary>World-space centre of the hazard box at <paramref name="clipMs"/>.</summary>
        public Vector3 HazardCenterAt(float clipMs)
        {
            if (_profile == null)
                return transform.position;

            return transform.TransformPoint(_profile.HazardCenterAt(clipMs));
        }

        private void OnValidate()
        {
            _trapDefinitionId = NormalizeId(_trapDefinitionId);
            _worldDefinitionKey = NormalizeId(_worldDefinitionKey);
            _definitionVersion = Mathf.Max(1, _definitionVersion);
            _footprintCells = Mathf.Max(1, _footprintCells);
        }

        private void OnDrawGizmosSelected()
        {
            if (_profile == null)
                return;

            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(
                transform.position,
                Quaternion.Euler(0f, YawDegrees, 0f),
                Vector3.one);

            TrapVolume trigger = _profile.TriggerVolume;
            Gizmos.color = new Color(0.95f, 0.8f, 0.1f, 0.22f);
            Gizmos.DrawCube(trigger.center, trigger.size);

            TrapVolume hazard = _profile.HazardVolume;
            Gizmos.color = new Color(0.9f, 0.1f, 0.05f, 0.35f);
            int steps = 12;
            float start = _profile.HazardStartMs;
            float end = Mathf.Max(start, _profile.HazardEndMs);
            for (int i = 0; i <= steps; i++)
            {
                float clipMs = Mathf.Lerp(start, end, steps == 0 ? 0f : i / (float)steps);
                Gizmos.DrawWireCube(_profile.HazardCenterAt(clipMs), hazard.size);
            }

            Gizmos.matrix = previous;
        }

        private static string NormalizeId(string value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Stable-ID lookup for scene-authored trap presentations. Replication drives
    /// this registry; there is no client-side trap gameplay at all.
    /// </summary>
    public static class TrapRuntimeRegistry
    {
        private static readonly Dictionary<string, List<TrapPresenter>> Traps =
            new(StringComparer.Ordinal);

        public static void Register(TrapPresenter presenter)
        {
            string id = presenter.TrapDefinitionId;
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (!Traps.TryGetValue(id, out List<TrapPresenter>? instances))
            {
                instances = new List<TrapPresenter>();
                Traps.Add(id, instances);
            }
            if (!instances.Contains(presenter))
                instances.Add(presenter);
        }

        public static void Unregister(TrapPresenter presenter)
        {
            string id = presenter.TrapDefinitionId;
            if (string.IsNullOrWhiteSpace(id)
                || !Traps.TryGetValue(id, out List<TrapPresenter>? instances))
            {
                return;
            }

            instances.Remove(presenter);
            if (instances.Count == 0)
                Traps.Remove(id);
        }

        public static void ApplyCycle(
            string trapDefinitionId,
            long cycleStartedAtMs,
            ulong activation)
        {
            foreach (TrapPresenter presenter in Resolve(trapDefinitionId))
                presenter.ApplyAuthoritativeCycle(cycleStartedAtMs, activation);
        }

        public static void ClearCycle(string trapDefinitionId)
        {
            foreach (TrapPresenter presenter in Resolve(trapDefinitionId))
                presenter.ClearAuthoritativeCycle();
        }

        public static void ClearAllCycles()
        {
            foreach (List<TrapPresenter> instances in Traps.Values)
            {
                for (int i = instances.Count - 1; i >= 0; i--)
                {
                    TrapPresenter? presenter = instances[i];
                    if (presenter == null)
                        instances.RemoveAt(i);
                    else
                        presenter.ClearAuthoritativeCycle();
                }
            }
        }

        internal static void ClearForTests() => Traps.Clear();

        private static List<TrapPresenter> Resolve(string trapDefinitionId)
        {
            string id = NormalizeId(trapDefinitionId);
            if (!Traps.TryGetValue(id, out List<TrapPresenter>? instances))
                return EmptyPresenters;

            for (int i = instances.Count - 1; i >= 0; i--)
            {
                if (instances[i] == null)
                    instances.RemoveAt(i);
            }
            if (instances.Count == 0)
            {
                Traps.Remove(id);
                return EmptyPresenters;
            }
            return instances;
        }

        private static readonly List<TrapPresenter> EmptyPresenters = new();

        private static string NormalizeId(string value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
    }
}

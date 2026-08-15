#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// A compact, target-attached mental-state tell: three unevenly orbiting
    /// motes above the head. The irregular phase and altitude shifts visually
    /// echo Confusion's direction changes without obscuring the character.
    /// </summary>
    internal sealed class ConfusionVFX : ISpellVFX
    {
        private const int MoteCount = 3;
        private const float HeadHeight = 2.15f;
        private readonly GameObject _group;
        private readonly List<Transform> _motes = new();
        private readonly List<Material> _materials = new();
        private float _elapsed;
        private bool _disposed;

        internal ConfusionVFX(CombatVFXTemplateContext context)
        {
            _group = new GameObject($"VFX_Confusion_{ShortKey(context.ActionInstanceId)}");
            if (context.FollowAnchor != null)
            {
                _group.transform.SetParent(context.FollowAnchor, false);
                _group.transform.localPosition = Vector3.zero;
                _group.transform.localRotation = Quaternion.identity;
            }
            else
            {
                _group.transform.SetPositionAndRotation(context.Point, Quaternion.identity);
            }

            for (int index = 0; index < MoteCount; index++)
                CreateMote(index);
        }

        public bool Tick(float dt)
        {
            if (_disposed)
                return false;

            _elapsed += Mathf.Max(dt, 0f);
            for (int index = 0; index < _motes.Count; index++)
            {
                float speed = index == 1 ? -2.15f : 1.65f + index * 0.22f;
                float angle = _elapsed * speed + index * 2.094f;
                float radius = 0.42f + 0.08f * Mathf.Sin(_elapsed * 2.7f + index * 1.3f);
                float height = HeadHeight
                    + 0.13f * Mathf.Sin(_elapsed * (3.1f + index * 0.35f) + index);
                _motes[index].localPosition = new Vector3(
                    Mathf.Cos(angle) * radius,
                    height,
                    Mathf.Sin(angle) * radius);
                _motes[index].localRotation = Quaternion.Euler(
                    _elapsed * (90f + index * 23f),
                    -angle * Mathf.Rad2Deg,
                    _elapsed * (130f - index * 17f));

                float pulse = 0.68f + 0.32f * (Mathf.Sin(_elapsed * 5.2f + index * 2.1f) * 0.5f + 0.5f);
                Color color = Color.Lerp(
                    new Color(0.45f, 0.08f, 1f, 0.72f),
                    new Color(1f, 0.25f, 0.85f, 0.9f),
                    pulse);
                ApplyMaterialColor(_materials[index], color);
            }

            return true;
        }

        public void OnUpdate(Vector3 position, Vector3 direction, float speed) { }
        public void OnImpact(Vector3 point) { }
        public void OnFizzle(Vector3 point) { }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_group != null)
                Object.Destroy(_group);
            foreach (Material material in _materials)
            {
                if (material != null)
                    Object.Destroy(material);
            }
            _materials.Clear();
        }

        private void CreateMote(int index)
        {
            GameObject mote = GameObject.CreatePrimitive(index == 1 ? PrimitiveType.Cube : PrimitiveType.Sphere);
            mote.name = $"ConfusionMote_{index + 1}";
            mote.transform.SetParent(_group.transform, false);
            mote.transform.localScale = index == 1
                ? new Vector3(0.13f, 0.25f, 0.08f)
                : Vector3.one * 0.16f;
            if (mote.TryGetComponent(out Collider collider))
                Object.Destroy(collider);

            Material material = new(VFXUtils.GetAdditiveGlowShader())
            {
                name = $"ConfusionMoteMaterial_{index + 1}",
                renderQueue = (int)RenderQueue.Transparent,
            };
            ApplyMaterialColor(material, new Color(0.75f, 0.12f, 1f, 0.8f));
            if (mote.TryGetComponent(out MeshRenderer renderer))
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            _motes.Add(mote.transform);
            _materials.Add(material);
        }

        private static void ApplyMaterialColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            if (material.HasProperty("_TintColor"))
                material.SetColor("_TintColor", color);
        }

        private static string ShortKey(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "unknown";
            return value.Substring(0, Mathf.Min(6, value.Length));
        }
    }
}

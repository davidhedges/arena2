#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Reconstructs the remaining Mirror Image charges from the authoritative
    /// StatusEffect stack count. The dispatcher recreates this visual whenever
    /// that count changes, so intercepted attacks visibly remove one image.
    /// </summary>
    internal sealed class MirrorImageVFX : ISpellVFX
    {
        private const int MaxImages = 3;
        private const float OrbitRadius = 1.05f;
        private const float BobAmplitude = 0.06f;
        private const float BobSpeed = 2.4f;

        private readonly GameObject _group;
        private readonly List<Transform> _images = new();
        private readonly List<Vector3> _basePositions = new();
        private readonly List<Material> _materials = new();
        private float _elapsed;
        private bool _disposed;

        internal MirrorImageVFX(CombatVFXTemplateContext context)
        {
            _group = new GameObject($"VFX_MirrorImage_{ShortKey(context.ActionInstanceId)}");
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

            int imageCount = Mathf.Clamp((int)context.SequenceCount, 1, MaxImages);
            for (int index = 0; index < imageCount; index++)
                CreateImage(index, imageCount);
        }

        public bool Tick(float dt)
        {
            if (_disposed)
                return false;

            _elapsed += Mathf.Max(dt, 0f);
            for (int index = 0; index < _images.Count; index++)
            {
                float phase = _elapsed * BobSpeed + index * 2.094f;
                Vector3 position = _basePositions[index];
                position.y += Mathf.Sin(phase) * BobAmplitude;
                _images[index].localPosition = position;

                float pulse = 0.72f + (Mathf.Sin(phase * 1.35f) * 0.5f + 0.5f) * 0.28f;
                Color color = new(0.32f, 0.22f, 1f, 0.42f * pulse);
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

        private void CreateImage(int index, int imageCount)
        {
            float angle = imageCount == 1
                ? 0f
                : -60f + index * (120f / (imageCount - 1));
            float radians = angle * Mathf.Deg2Rad;
            Vector3 basePosition = new(
                Mathf.Sin(radians) * OrbitRadius,
                0.05f,
                -Mathf.Cos(radians) * OrbitRadius);

            var image = new GameObject($"MirrorImage_{index + 1}");
            image.transform.SetParent(_group.transform, false);
            image.transform.localPosition = basePosition;
            image.transform.localRotation = Quaternion.identity;

            Material material = CreateMaterial(index);
            CreateBodyPart(image.transform, PrimitiveType.Capsule, "Body", new Vector3(0f, 1.0f, 0f), new Vector3(0.34f, 0.72f, 0.24f), material);
            CreateBodyPart(image.transform, PrimitiveType.Sphere, "Head", new Vector3(0f, 1.88f, 0f), Vector3.one * 0.28f, material);
            CreateBodyPart(image.transform, PrimitiveType.Cube, "Shoulders", new Vector3(0f, 1.48f, 0f), new Vector3(0.92f, 0.12f, 0.18f), material);

            _images.Add(image.transform);
            _basePositions.Add(basePosition);
            _materials.Add(material);
        }

        private static void CreateBodyPart(
            Transform parent,
            PrimitiveType primitive,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Material material)
        {
            GameObject part = GameObject.CreatePrimitive(primitive);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = localScale;
            if (part.TryGetComponent(out Collider collider))
                Object.Destroy(collider);
            if (part.TryGetComponent(out MeshRenderer renderer))
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        private static Material CreateMaterial(int index)
        {
            var material = new Material(VFXUtils.GetAdditiveGlowShader())
            {
                name = $"MirrorImageMaterial_{index + 1}",
                renderQueue = (int)RenderQueue.Transparent,
            };
            ApplyMaterialColor(material, new Color(0.32f, 0.22f, 1f, 0.42f));
            return material;
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

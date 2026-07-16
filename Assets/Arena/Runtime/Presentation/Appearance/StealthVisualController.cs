#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arena.Presentation.Appearance
{
    /// <summary>
    /// Owns the live renderer/material override used by the stealthed combat mode.
    /// Gameplay visibility remains server-owned; this component is presentation only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StealthVisualController : MonoBehaviour
    {
        private const float DefaultStealthOpacity = 0.4f;
        private const float DefaultFadeSeconds = 0.2f;
        private const float OpaqueThreshold = 0.999f;

        [SerializeField] [Range(0.05f, 0.95f)] private float stealthOpacity = DefaultStealthOpacity;
        [SerializeField] [Min(0f)] private float fadeSeconds = DefaultFadeSeconds;

        private readonly List<Renderer> _targets = new();
        private readonly List<RendererOverride> _overrides = new();
        private bool _isStealthed;
        private float _currentOpacity = 1f;

        public event Action? MaterialsRestored;

        public bool IsStealthed => _isStealthed;
        public float CurrentOpacity => _currentOpacity;
        public bool HasMaterialOverrides => _overrides.Count > 0;

        public void RefreshRenderers(IEnumerable<Renderer> renderers)
        {
            bool shouldOverride = _isStealthed || _currentOpacity < OpaqueThreshold;
            ReleaseOverrides(restoreOriginals: true, notify: false);

            _targets.Clear();
            var seen = new HashSet<Renderer>();
            foreach (Renderer renderer in renderers)
            {
                if (renderer != null && seen.Add(renderer))
                    _targets.Add(renderer);
            }

            if (!shouldOverride)
                return;

            CaptureOverrides();
            ApplyOpacity(_currentOpacity);
        }

        public void SetStealthed(bool stealthed, bool immediate = false)
        {
            _isStealthed = stealthed;
            if (stealthed && _overrides.Count == 0)
                CaptureOverrides();

            float targetOpacity = stealthed ? ResolveStealthOpacity() : 1f;
            if (!immediate && fadeSeconds > 0f)
                return;

            _currentOpacity = targetOpacity;
            ApplyOpacity(_currentOpacity);
            if (!stealthed)
                ReleaseOverrides(restoreOriginals: true, notify: true);
        }

        public void ReapplyOpacity()
        {
            if (_overrides.Count > 0)
                ApplyOpacity(_currentOpacity);
        }

        private void Update()
        {
            if (_overrides.Count == 0)
                return;

            float targetOpacity = _isStealthed ? ResolveStealthOpacity() : 1f;
            float opacityRange = 1f - ResolveStealthOpacity();
            float maxDelta = fadeSeconds <= 0f
                ? 1f
                : opacityRange * Time.unscaledDeltaTime / fadeSeconds;
            _currentOpacity = Mathf.MoveTowards(_currentOpacity, targetOpacity, maxDelta);
            ApplyOpacity(_currentOpacity);

            if (!_isStealthed && _currentOpacity >= OpaqueThreshold)
            {
                _currentOpacity = 1f;
                ReleaseOverrides(restoreOriginals: true, notify: true);
            }
        }

        private float ResolveStealthOpacity()
        {
            return Mathf.Clamp(stealthOpacity, 0.05f, 0.95f);
        }

        private void CaptureOverrides()
        {
            if (_overrides.Count > 0)
                return;

            for (int rendererIndex = 0; rendererIndex < _targets.Count; rendererIndex++)
            {
                Renderer renderer = _targets[rendererIndex];
                if (renderer == null)
                    continue;

                Material[] originals = renderer.sharedMaterials;
                var stealthMaterials = new Material[originals.Length];
                for (int materialIndex = 0; materialIndex < originals.Length; materialIndex++)
                {
                    Material source = originals[materialIndex];
                    if (source == null)
                        continue;

                    var material = new Material(source)
                    {
                        name = $"{source.name}_stealth_instance",
                    };
                    ConfigureTransparentMaterial(material);
                    stealthMaterials[materialIndex] = material;
                }

                _overrides.Add(new RendererOverride(
                    renderer,
                    originals,
                    stealthMaterials,
                    renderer.shadowCastingMode));
                renderer.sharedMaterials = stealthMaterials;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        private void ApplyOpacity(float opacity)
        {
            float clampedOpacity = Mathf.Clamp01(opacity);
            for (int rendererIndex = 0; rendererIndex < _overrides.Count; rendererIndex++)
            {
                Material[] materials = _overrides[rendererIndex].StealthMaterials;
                for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    Material material = materials[materialIndex];
                    if (material == null)
                        continue;

                    ApplyColorPropertyAlpha(material, "_BaseColor", clampedOpacity);
                    ApplyColorPropertyAlpha(material, "_Color", clampedOpacity);
                }
            }
        }

        private void ReleaseOverrides(bool restoreOriginals, bool notify)
        {
            if (_overrides.Count == 0)
                return;

            for (int rendererIndex = 0; rendererIndex < _overrides.Count; rendererIndex++)
            {
                RendererOverride rendererOverride = _overrides[rendererIndex];
                if (restoreOriginals && rendererOverride.Renderer != null)
                {
                    rendererOverride.Renderer.sharedMaterials = rendererOverride.OriginalMaterials;
                    rendererOverride.Renderer.shadowCastingMode = rendererOverride.OriginalShadowCastingMode;
                }

                for (int materialIndex = 0; materialIndex < rendererOverride.StealthMaterials.Length; materialIndex++)
                {
                    Material material = rendererOverride.StealthMaterials[materialIndex];
                    if (material == null)
                        continue;

                    if (Application.isPlaying)
                        Destroy(material);
                    else
                        DestroyImmediate(material);
                }
            }

            _overrides.Clear();
            if (notify)
                MaterialsRestored?.Invoke();
        }

        private static void ConfigureTransparentMaterial(Material material)
        {
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;

            if (material.HasProperty("_Surface"))
                material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend"))
                material.SetFloat("_Blend", 0f);
            if (material.HasProperty("_AlphaClip"))
                material.SetFloat("_AlphaClip", 0f);
            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_SrcBlendAlpha"))
                material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            if (material.HasProperty("_DstBlendAlpha"))
                material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 0f);
            if (material.HasProperty("_AlphaToMask"))
                material.SetFloat("_AlphaToMask", 0f);
            if (material.HasProperty("_BlendModePreserveSpecular"))
                material.SetFloat("_BlendModePreserveSpecular", 0f);

            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.DisableKeyword("_ALPHAMODULATE_ON");
            material.SetShaderPassEnabled("DepthOnly", false);
            material.SetShaderPassEnabled("DepthNormals", false);
        }

        private static void ApplyColorPropertyAlpha(Material material, string propertyName, float opacity)
        {
            if (!material.HasProperty(propertyName))
                return;

            Color color = material.GetColor(propertyName);
            color.a = opacity;
            material.SetColor(propertyName, color);
        }

        private void OnDestroy()
        {
            ReleaseOverrides(restoreOriginals: true, notify: false);
        }

        private sealed class RendererOverride
        {
            public RendererOverride(
                Renderer renderer,
                Material[] originalMaterials,
                Material[] stealthMaterials,
                ShadowCastingMode originalShadowCastingMode)
            {
                Renderer = renderer;
                OriginalMaterials = originalMaterials;
                StealthMaterials = stealthMaterials;
                OriginalShadowCastingMode = originalShadowCastingMode;
            }

            public Renderer Renderer { get; }
            public Material[] OriginalMaterials { get; }
            public Material[] StealthMaterials { get; }
            public ShadowCastingMode OriginalShadowCastingMode { get; }
        }
    }
}

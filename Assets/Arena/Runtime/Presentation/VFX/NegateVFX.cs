#nullable enable
using UnityEngine;
using Arena.Presentation;
using SpacetimeDB.Types;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Port of negate_vfx.ts.
    /// Expanding dome mesh with animated shader.
    /// Ease-out expansion, pulse, 0.14s fade-out.
    /// </summary>
    public class NegateVFX : ISpellVFX
    {
        private const float InitialScale = 0.05f;
        private const float ActiveOpacity = 0.38f;
        private const float FadeOutSeconds = 0.14f;
        private const float HoldAfterExpansion = 0.08f;
        private const float PulseSpeed = 6.2f;

        private readonly GameObject _group;
        private GameObject _domeMesh = null!;
        private Material _material = null!;
        private readonly float _finalRadius;
        private readonly float _expansionDuration;
        private float _elapsed;
        private float _fadeRemaining;
        private bool _fadingOut;
        private bool _disposed;

        internal NegateVFX(CombatVFXTemplateContext context)
        {
            _finalRadius = Mathf.Max(context.MaxDistance, 0.01f);
            _expansionDuration = _finalRadius / Mathf.Max(context.Speed, 0.01f);

            _group = new GameObject($"VFX_Negate_{context.ActionInstanceId[..Mathf.Min(6, context.ActionInstanceId.Length)]}");
            _group.transform.position = context.Origin;
            _group.transform.localScale = Vector3.one * InitialScale;

            CreateVisuals();
        }

        public NegateVFX(CombatEvent castEvent)
        {
            _finalRadius = Mathf.Max(castEvent.MaxDistance, 0.01f);
            _expansionDuration = _finalRadius / Mathf.Max(castEvent.Speed, 0.01f);

            _group = new GameObject($"VFX_Negate_{castEvent.ActionInstanceId[..Mathf.Min(6, castEvent.ActionInstanceId.Length)]}");
            _group.transform.position = new Vector3(castEvent.OriginX, castEvent.OriginY, castEvent.OriginZ);
            _group.transform.localScale = Vector3.one * InitialScale;

            CreateVisuals();
        }

        private void CreateVisuals()
        {
            // Create dome mesh
            _domeMesh = new GameObject("NegateDome");
            _domeMesh.transform.SetParent(_group.transform, false);
            var mf = _domeMesh.AddComponent<MeshFilter>();
            var mr = _domeMesh.AddComponent<MeshRenderer>();

            mf.mesh = VFXUtils.CreateDomeMesh(_finalRadius, _finalRadius, 24, 12);

            _material = new Material(VFXUtils.GetNegateDomeShader());
            _material.SetFloat("_Opacity", 0);
            _material.SetFloat("_Time2", 0);
            _material.SetFloat("_Radius", _finalRadius);
            _material.SetFloat("_Height", _finalRadius);
            _material.SetColor("_BaseColor", new Color(0.85f, 0.2f, 0.2f));
            _material.SetColor("_BandColor", new Color(1f, 0.3f, 0.15f));

            mr.material = _material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        public bool Tick(float dt)
        {
            if (_disposed) return false;

            _elapsed += dt;

            if (!_fadingOut && _elapsed >= _expansionDuration + HoldAfterExpansion)
                BeginFadeOut();

            if (_fadeRemaining > 0)
                _fadeRemaining = Mathf.Max(0, _fadeRemaining - dt);

            // Expansion with ease-out cubic (TS: 1 - (1-t)^3)
            float expansionT = _expansionDuration <= 0.0001f ? 1f
                : Mathf.Clamp01(_elapsed / _expansionDuration);
            float easedExpansion = 1f - Mathf.Pow(1f - expansionT, 3f);

            float pulse = 0.92f + (Mathf.Sin(_elapsed * PulseSpeed) * 0.5f + 0.5f) * 0.14f;
            float fadeT = _fadingOut ? Mathf.Clamp01(_fadeRemaining / FadeOutSeconds) : 1f;

            float scale = Mathf.Lerp(InitialScale, 1f, easedExpansion);
            _group.transform.localScale = Vector3.one * scale;

            _material.SetFloat("_Time2", _elapsed);
            _material.SetFloat("_Opacity", ActiveOpacity * pulse * fadeT);

            return !_fadingOut || _fadeRemaining > 0;
        }

        public void OnUpdate(Vector3 position, Vector3 direction, float speed) { }
        public void OnImpact(Vector3 point) { }

        public void OnFizzle(Vector3 point)
        {
            BeginFadeOut();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_group != null) Object.Destroy(_group);
            if (_material != null) Object.Destroy(_material);
        }

        private void BeginFadeOut()
        {
            _fadingOut = true;
            _fadeRemaining = Mathf.Max(_fadeRemaining, FadeOutSeconds);
        }
    }
}

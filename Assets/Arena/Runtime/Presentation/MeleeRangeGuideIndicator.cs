#nullable enable

using Arena.Combat;
using Arena.Entity;
using Arena.Network;
using Arena.Presentation.VFX;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Local-only ground guide for active melee range modifiers.
    /// Combat authority remains server-side; this visual reads replicated modifier/status rows.
    /// </summary>
    public sealed class MeleeRangeGuideIndicator : MonoBehaviour
    {
        private const float GroundOffset = 0.045f;
        private const float RadiusSmoothSpeed = 14f;
        private const int Segments = 96;
        private static readonly Color GuideColor = new(1.0f, 1.0f, 1.0f, 0.06f);

        private PlayerEntity? _entity;
        private GameObject? _circle;
        private MeshRenderer? _renderer;
        private Material? _material;
        private float _displayRadius;

        public void Initialize(PlayerEntity entity)
        {
            _entity = entity;
        }

        private void Update()
        {
            if (_entity == null || _entity.IsDestroyed)
            {
                Hide();
                return;
            }

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                Hide();
                return;
            }

            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float targetRadius = MeleeAttackModifierResolver.ResolveActiveModifierGuideRange(
                conn,
                _entity.Identity,
                nowMs);

            if (targetRadius <= 0.01f)
            {
                Hide();
                _displayRadius = 0f;
                return;
            }

            EnsureCircle();
            if (_circle == null)
                return;

            _circle.SetActive(true);
            _displayRadius = _displayRadius <= 0.01f
                ? targetRadius
                : Mathf.Lerp(_displayRadius, targetRadius, 1f - Mathf.Exp(-RadiusSmoothSpeed * Time.deltaTime));
            _circle.transform.localPosition = Vector3.up * GroundOffset;
            _circle.transform.localRotation = Quaternion.identity;
            _circle.transform.localScale = Vector3.one * _displayRadius;
        }

        private void EnsureCircle()
        {
            if (_circle != null)
                return;

            _circle = new GameObject("MeleeRangeGuide");
            _circle.transform.SetParent(transform, false);

            var mf = _circle.AddComponent<MeshFilter>();
            mf.mesh = VFXUtils.CreateRingMesh(0.985f, 1.0f, Segments);

            _renderer = _circle.AddComponent<MeshRenderer>();
            _material = new Material(VFXUtils.GetAdditiveGlowShader());
            if (_material.HasProperty("_Color"))
                _material.SetColor("_Color", GuideColor);
            if (_material.HasProperty("_Intensity"))
                _material.SetFloat("_Intensity", 0.22f);

            _renderer.material = _material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _circle.SetActive(false);
        }

        private void Hide()
        {
            if (_circle != null)
                _circle.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_material != null)
                Destroy(_material);
        }
    }
}

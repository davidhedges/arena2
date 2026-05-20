#nullable enable
using UnityEngine;
using SpacetimeDB.Types;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Port of icicle_vfx.ts.
    /// Ice shard projectile with particle trail and impact burst.
    /// Same position reconciliation as Fireball.
    /// </summary>
    public class IcicleVFX : ISpellVFX
    {
        private const float PositionCorrectionHalfLife = 0.08f;
        private const float HardSnapDistance = 3f;
        private static readonly Color IcicleColor = new(0.5f, 0.87f, 1f);
        private static readonly Color ImpactColor = new(0.545f, 0.91f, 1f); // 0x8be8ff
        private static readonly Color TrailColor = new(0.7f, 0.95f, 1f, 0.6f);

        private readonly GameObject _group;
        private readonly GameObject _coreMesh;
        private readonly Material _coreMaterial;
        private readonly LineRenderer _trail;
        private readonly Material _trailMaterial;
        private Vector3 _direction;
        private Vector3 _authoritativePosition;
        private float _speed;
        private float _elapsed;
        private bool _active = true;
        private bool _disposed;

        private const int TrailMaxPoints = 36;
        private const float TrailMinSampleDist = 0.04f;
        private const float TrailDecayRate = 22f;
        private float _trailDecayAccum;

        private ImpactBurstVFX? _impactBurst;

        public IcicleVFX(CombatEvent castEvent)
        {
            _group = new GameObject($"VFX_Icicle_{castEvent.ActionInstanceId[..Mathf.Min(6, castEvent.ActionInstanceId.Length)]}");
            _group.transform.position = VFXUtils.ResolveSpawnOrigin(castEvent);

            Vector3 serverOrigin = new Vector3(castEvent.OriginX, castEvent.OriginY, castEvent.OriginZ);
            Vector3 serverDir = new Vector3(castEvent.DirX, castEvent.DirY, castEvent.DirZ).normalized;
            _direction = VFXUtils.CorrectedDirection(_group.transform.position, serverOrigin, serverDir, castEvent.MaxDistance);
            _speed = castEvent.Speed;
            _authoritativePosition = _group.transform.position;

            // Core mesh — elongated capsule for ice shard look
            _coreMesh = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _coreMesh.name = "IcicleCore";
            _coreMesh.transform.SetParent(_group.transform, false);
            _coreMesh.transform.localScale = new Vector3(0.15f, 0.35f, 0.15f);
            _coreMesh.transform.localRotation = Quaternion.Euler(90, 0, 0); // Point forward
            Object.Destroy(_coreMesh.GetComponent<Collider>());

            _coreMaterial = new Material(Shader.Find("Standard")!);
            _coreMaterial.SetColor("_Color", IcicleColor);
            _coreMaterial.SetColor("_EmissionColor", new Color(0.5f, 0.87f, 1f) * 0.15f);
            _coreMaterial.EnableKeyword("_EMISSION");
            _coreMaterial.SetFloat("_Glossiness", 0.85f);
            _coreMaterial.SetFloat("_Metallic", 0.1f);
            var coreRenderer = _coreMesh.GetComponent<Renderer>();
            coreRenderer.material = _coreMaterial;
            coreRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Trail
            var trailGo = new GameObject("IcicleTrail");
            trailGo.transform.SetParent(_group.transform, false);
            _trail = trailGo.AddComponent<LineRenderer>();
            _trailMaterial = new Material(VFXUtils.GetAdditiveGlowShader());
            _trailMaterial.SetColor("_Color", TrailColor);
            _trailMaterial.SetFloat("_Intensity", 1.2f);
            _trail.material = _trailMaterial;
            _trail.startWidth = 0.3f;
            _trail.endWidth = 0.01f;
            _trail.positionCount = 0;
            _trail.useWorldSpace = true;
            _trail.numCapVertices = 2;
            _trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            if (_direction.sqrMagnitude > 0.0001f)
                _group.transform.rotation = Quaternion.LookRotation(_direction);
        }

        public bool Tick(float dt)
        {
            if (_disposed) return false;
            if (!_active && _impactBurst == null) return false;

            _elapsed += dt;

            if (_active)
            {
                // Pulsing scale (TS: 7.5 rad/s, 0.025 amplitude)
                float pulse = 1f + Mathf.Sin(_elapsed * 7.5f) * 0.025f;
                _coreMesh.transform.localScale = new Vector3(0.15f * pulse, 0.35f * pulse, 0.15f * pulse);

                float step = _speed * dt;
                _group.transform.position += _direction * step;
                _authoritativePosition += _direction * step;
                ReconcilePosition(dt);
                UpdateTrail(dt);
            }

            if (_impactBurst != null)
            {
                if (!_impactBurst.Tick(dt))
                {
                    _impactBurst.Dispose();
                    _impactBurst = null;
                }
            }

            return _active || _impactBurst != null;
        }

        public void OnUpdate(Vector3 position, Vector3 direction, float speed)
        {
            if (!_active) return;
            _authoritativePosition = position;
            _direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            _speed = speed;
            _group.transform.rotation = Quaternion.LookRotation(_direction);
        }

        public void OnImpact(Vector3 point)
        {
            if (!_active) return;
            _impactBurst = new ImpactBurstVFX(point, ImpactColor);
            EndAt(point);
        }

        public void OnFizzle(Vector3 point) => EndAt(point);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _impactBurst?.Dispose();
            if (_group != null) Object.Destroy(_group);
            if (_coreMaterial != null) Object.Destroy(_coreMaterial);
            if (_trailMaterial != null) Object.Destroy(_trailMaterial);
        }

        private void EndAt(Vector3 point)
        {
            if (!_active) return;
            _active = false;
            _group.transform.position = point;
            _coreMesh.SetActive(false);
            _trail.positionCount = 0;
        }

        private void ReconcilePosition(float dt)
        {
            float errorSq = (_group.transform.position - _authoritativePosition).sqrMagnitude;
            if (errorSq <= 0.000001f) return;
            if (errorSq >= HardSnapDistance * HardSnapDistance)
            {
                _group.transform.position = _authoritativePosition;
                return;
            }
            float alpha = VFXUtils.ExponentialAlpha(dt, PositionCorrectionHalfLife);
            _group.transform.position = Vector3.Lerp(_group.transform.position, _authoritativePosition, alpha);
        }

        private void UpdateTrail(float dt)
        {
            Vector3 pos = _group.transform.position;
            int count = _trail.positionCount;
            bool shouldAdd = count == 0;
            if (!shouldAdd && count > 0)
            {
                Vector3 last = _trail.GetPosition(count - 1);
                shouldAdd = (pos - last).sqrMagnitude >= TrailMinSampleDist * TrailMinSampleDist;
            }
            if (shouldAdd && count < TrailMaxPoints)
            {
                _trail.positionCount = count + 1;
                _trail.SetPosition(count, pos);
            }
            else if (shouldAdd)
            {
                for (int i = 0; i < count - 1; i++)
                    _trail.SetPosition(i, _trail.GetPosition(i + 1));
                _trail.SetPosition(count - 1, pos);
            }
            _trailDecayAccum += dt * TrailDecayRate;
            while (_trailDecayAccum >= 1f && _trail.positionCount > 1)
            {
                _trailDecayAccum -= 1f;
                int c = _trail.positionCount;
                for (int i = 0; i < c - 1; i++)
                    _trail.SetPosition(i, _trail.GetPosition(i + 1));
                _trail.positionCount = c - 1;
            }
        }
    }
}

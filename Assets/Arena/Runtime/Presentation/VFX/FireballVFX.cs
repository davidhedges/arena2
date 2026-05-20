#nullable enable
using UnityEngine;
using SpacetimeDB.Types;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Port of fireball_vfx.ts.
    /// Emissive sphere with fire shader + trail (LineRenderer) + impact burst.
    /// Position reconciliation with exponential smoothing (0.08s half-life).
    /// </summary>
    public class FireballVFX : ISpellVFX
    {
        private const float PositionCorrectionHalfLife = 0.08f;
        private const float HardSnapDistance = 3f;

        private readonly GameObject _group;
        private readonly GameObject _coreMesh;
        private readonly Material _fireMaterial;
        private readonly LineRenderer _trail;
        private readonly Material _trailMaterial;
        private Vector3 _direction;
        private Vector3 _authoritativePosition;
        private float _speed;
        private bool _active = true;
        private bool _disposed;

        // Trail history
        private const int TrailMaxPoints = 36;
        private const float TrailMinSampleDist = 0.04f;
        private const float TrailDecayRate = 22f;
        private float _trailDecayAccum;

        private ImpactBurstVFX? _impactBurst;

        public FireballVFX(CombatEvent castEvent)
        {
            _group = new GameObject($"VFX_Fireball_{castEvent.ActionInstanceId[..Mathf.Min(6, castEvent.ActionInstanceId.Length)]}");
            _group.transform.position = VFXUtils.ResolveSpawnOrigin(castEvent);

            Vector3 serverOrigin = new Vector3(castEvent.OriginX, castEvent.OriginY, castEvent.OriginZ);
            Vector3 serverDir = new Vector3(castEvent.DirX, castEvent.DirY, castEvent.DirZ).normalized;
            _direction = VFXUtils.CorrectedDirection(_group.transform.position, serverOrigin, serverDir, castEvent.MaxDistance);
            _speed = castEvent.Speed;
            _authoritativePosition = _group.transform.position;

            // Core mesh — sphere with fire shader
            _coreMesh = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _coreMesh.name = "FireballCore";
            _coreMesh.transform.SetParent(_group.transform, false);
            _coreMesh.transform.localScale = Vector3.one * 0.35f;
            Object.Destroy(_coreMesh.GetComponent<Collider>());

            var fireTexture = Resources.Load<Texture2D>("VFX/fire_texture");
            _fireMaterial = new Material(VFXUtils.GetFireballShader());
            if (_fireMaterial.HasProperty("_Color"))
                _fireMaterial.SetColor("_Color", new Color(1.1f, 0.34f, 0.07f));
            if (_fireMaterial.HasProperty("_CoreColor"))
                _fireMaterial.SetColor("_CoreColor", new Color(1f, 0.7f, 0.3f));
            if (_fireMaterial.HasProperty("_Intensity"))
                _fireMaterial.SetFloat("_Intensity", 2f);
            if (_fireMaterial.HasProperty("_FresnelPower"))
                _fireMaterial.SetFloat("_FresnelPower", 2.5f);
            if (_fireMaterial.HasProperty("_ScrollSpeed"))
                _fireMaterial.SetFloat("_ScrollSpeed", 1.8f);
            if (fireTexture != null && _fireMaterial.HasProperty("_MainTex"))
                _fireMaterial.SetTexture("_MainTex", fireTexture);

            var coreRenderer = _coreMesh.GetComponent<Renderer>();
            coreRenderer.material = _fireMaterial;
            coreRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Trail — LineRenderer
            var trailGo = new GameObject("FireballTrail");
            trailGo.transform.SetParent(_group.transform, false);
            _trail = trailGo.AddComponent<LineRenderer>();
            _trailMaterial = new Material(VFXUtils.GetAdditiveGlowShader());
            if (_trailMaterial.HasProperty("_Color"))
                _trailMaterial.SetColor("_Color", new Color(1f, 0.54f, 0.18f, 0.8f));
            if (_trailMaterial.HasProperty("_Intensity"))
                _trailMaterial.SetFloat("_Intensity", 1.5f);
            _trail.material = _trailMaterial;
            _trail.startWidth = 0.5f;
            _trail.endWidth = 0.02f;
            _trail.positionCount = 0;
            _trail.useWorldSpace = true;
            _trail.numCapVertices = 2;
            _trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Orient group to face direction
            if (_direction.sqrMagnitude > 0.0001f)
                _group.transform.rotation = Quaternion.LookRotation(_direction);
        }

        public bool Tick(float dt)
        {
            if (_disposed) return false;

            if (!_active && _impactBurst == null)
                return false;

            if (_active)
            {
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
            _impactBurst = new ImpactBurstVFX(point, new Color(1f, 0.54f, 0.18f)); // 0xff8a2f
            EndAt(point);
        }

        public void OnFizzle(Vector3 point)
        {
            EndAt(point);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _impactBurst?.Dispose();
            if (_group != null) Object.Destroy(_group);
            if (_fireMaterial != null) Object.Destroy(_fireMaterial);
            if (_trailMaterial != null) Object.Destroy(_trailMaterial);
        }

        private void EndAt(Vector3 point)
        {
            if (!_active) return;
            _active = false;
            _group.transform.position = point;
            _coreMesh.SetActive(false);
            // Clear trail
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
            // Add current position to trail
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
                // Shift points forward and add new one at end
                for (int i = 0; i < count - 1; i++)
                    _trail.SetPosition(i, _trail.GetPosition(i + 1));
                _trail.SetPosition(count - 1, pos);
            }

            // Decay old points
            _trailDecayAccum += dt * TrailDecayRate;
            while (_trailDecayAccum >= 1f && _trail.positionCount > 1)
            {
                _trailDecayAccum -= 1f;
                // Remove oldest point
                int c = _trail.positionCount;
                for (int i = 0; i < c - 1; i++)
                    _trail.SetPosition(i, _trail.GetPosition(i + 1));
                _trail.positionCount = c - 1;
            }
        }
    }
}

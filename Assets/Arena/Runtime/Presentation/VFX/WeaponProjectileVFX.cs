#nullable enable
using UnityEngine;
using UnityEngine.VFX;

namespace Arena.Presentation.VFX
{
    public sealed class WeaponProjectileVFX : ISpellVFX
    {
        private const float PositionCorrectionHalfLife = 0.05f;
        // ARROW_STANDARD moves 4.5m per 0.10s authoritative update; snap only after clear divergence.
        private const float HardSnapDistance = 6f;
        // Covers the longest per-particle lifetime authored in the VFX graphs we ship as projectile bodies.
        private const float VisualEffectLingerSeconds = 5f;
        // Safety cap on the visual sweep extension for VisualEffect-bearing prefabs whose graphs need a
        // sustained transform travel to render their world-space leading head + trailing emissions.
        // Gameplay impact/fizzle remains server-authoritative; this only governs cosmetic playout.
        private const float VisualEffectSweepMaxSeconds = 2f;

        private GameObject _group = null!;
        private GameObject _projectileBody = null!;
        private Vector3 _direction;
        private Vector3 _authoritativePosition;
        private float _speed;
        private float _maxDistance;
        private float _traveled;
        private Vector3 _initialBodyScale;
        private float _scaleMultiplierAtLifetimeEnd = 1f;
        private bool _authoritativeLifetime;
        private bool _active = true;
        private bool _disposed;
        private bool _hasVisualEffect;
        private bool _visualSweepExtended;
        private float _visualSweepElapsed;
        private ProjectileVfxPool.Rental? _rental;

        public WeaponProjectileVFX(
            string instanceId,
            Vector3 position,
            Vector3 direction,
            float speed,
            float maxDistance,
            float visualScale,
            GameObject prefab,
            CombatVFXRegistry.Template? trailTemplate = null,
            float scaleMultiplierAtLifetimeEnd = 1f,
            bool authoritativeLifetime = false)
        {
            _group = new GameObject($"VFX_Projectile_{ShortId(instanceId)}");
            _projectileBody = Object.Instantiate(prefab, _group.transform, false);
            _projectileBody.name = $"{prefab.name}_Body";
            _projectileBody.transform.localPosition = Vector3.zero;
            _projectileBody.transform.localRotation = Quaternion.identity;
            _projectileBody.transform.localScale = Vector3.one;
            VFXUtils.ApplyPrefabPresentationScale(_projectileBody, ResolveVisualScale(visualScale));
            _initialBodyScale = _projectileBody.transform.localScale;
            _scaleMultiplierAtLifetimeEnd = ResolveEndScaleMultiplier(scaleMultiplierAtLifetimeEnd);
            if (trailTemplate != null)
            {
                GameObject trail = Object.Instantiate(trailTemplate.Prefab, _group.transform, false);
                trail.name = $"{trailTemplate.Prefab.name}_Trail";
                trail.transform.localPosition = Vector3.zero;
                trail.transform.localRotation = Quaternion.identity;
                trail.transform.localScale = Vector3.one;
                VFXUtils.ApplyPrefabPresentationScale(trail, ResolveVisualScale(trailTemplate.Scale));
            }
            _hasVisualEffect = _group.GetComponentInChildren<VisualEffect>(true) != null;
            Initialize(instanceId, position, direction, speed, maxDistance, authoritativeLifetime);
        }

        internal WeaponProjectileVFX(
            string instanceId,
            Vector3 position,
            Vector3 direction,
            float speed,
            float maxDistance,
            ProjectileVfxPool.Rental rental,
            float scaleMultiplierAtLifetimeEnd = 1f,
            bool authoritativeLifetime = false)
        {
            _rental = rental;
            _group = rental.Root;
            _projectileBody = rental.Body;
            _initialBodyScale = _projectileBody.transform.localScale;
            _scaleMultiplierAtLifetimeEnd = ResolveEndScaleMultiplier(scaleMultiplierAtLifetimeEnd);
            // Pool bypass already excludes VisualEffect prefabs, so the rental path never carries one.
            _hasVisualEffect = false;
            Initialize(instanceId, position, direction, speed, maxDistance, authoritativeLifetime);
        }

        private void Initialize(
            string instanceId,
            Vector3 position,
            Vector3 direction,
            float speed,
            float maxDistance,
            bool authoritativeLifetime)
        {
            _group.name = $"VFX_Projectile_{ShortId(instanceId)}";
            _group.transform.SetParent(null, true);
            _group.transform.position = position;
            _direction = direction.normalized;
            if (_direction.sqrMagnitude <= 0.0001f)
                _direction = Vector3.forward;
            _speed = speed;
            _maxDistance = maxDistance > 0f ? maxDistance : 35f;
            _authoritativePosition = _group.transform.position;
            _traveled = 0f;
            _authoritativeLifetime = authoritativeLifetime;
            _active = true;
            _disposed = false;
            _visualSweepExtended = false;
            _visualSweepElapsed = 0f;

            Orient();
        }

        public bool Tick(float dt)
        {
            if (_disposed)
                return false;

            if (_active)
            {
                float step = _speed * dt;
                _traveled += step;
                ApplyLifetimeScale();
                _group.transform.position += _direction * step;

                if (_visualSweepExtended)
                {
                    _visualSweepElapsed += dt;
                    if (_visualSweepElapsed >= VisualEffectSweepMaxSeconds || _traveled >= _maxDistance)
                        EndAt(_group.transform.position);
                }
                else
                {
                    _authoritativePosition += _direction * step;
                    ReconcilePosition(dt);
                    if (!_authoritativeLifetime && _traveled >= _maxDistance)
                        EndAt(_group.transform.position);
                }
            }

            return _active;
        }

        public void OnUpdate(Vector3 position, Vector3 direction, float speed)
        {
            if (!_active)
                return;

            _authoritativePosition = position;
            if (direction.sqrMagnitude > 0.0001f)
                _direction = direction.normalized;
            _speed = speed;
            Orient();
        }

        public void OnUpdate(Vector3 position, Vector3 direction, float speed, float traveled)
        {
            OnUpdate(position, direction, speed);
            if (!_active)
                return;

            _traveled = Mathf.Max(0f, traveled);
        }

        public void OnUpdate(
            Vector3 position,
            Vector3 direction,
            float speed,
            bool snapToAuthoritative)
        {
            OnUpdate(position, direction, speed);
            if (!_active || !snapToAuthoritative)
                return;

            _group.transform.position = _authoritativePosition;
        }

        public void OnUpdate(
            Vector3 position,
            Vector3 direction,
            float speed,
            float traveled,
            bool snapToAuthoritative)
        {
            OnUpdate(position, direction, speed, traveled);
            if (!_active || !snapToAuthoritative)
                return;

            _group.transform.position = _authoritativePosition;
        }

        public void OnImpact(Vector3 point)
        {
            if (!_active)
                return;

            if (_hasVisualEffect && !_visualSweepExtended)
            {
                ExtendVisualSweepPastImpact();
                return;
            }

            EndAt(point);
        }

        public void OnFizzle(Vector3 point)
        {
            if (!_active)
                return;

            if (_hasVisualEffect && !_visualSweepExtended)
            {
                ExtendVisualSweepPastImpact();
                return;
            }

            EndAt(point);
        }

        // VisualEffect graphs that emit in world space (slash sweeps, ground trails) need sustained
        // transform travel to paint their leading head + trailing emissions. Gameplay impact has already
        // been applied authoritatively by the server; this only governs cosmetic playout. Tick keeps
        // advancing the transform until max distance or the safety cap.
        private void ExtendVisualSweepPastImpact()
        {
            _visualSweepExtended = true;
            _visualSweepElapsed = 0f;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_rental != null)
            {
                _rental.Return();
                _rental = null;
                return;
            }

            DetachAndLingerVisualEffectBody();

            if (_group != null)
                Object.Destroy(_group);
        }

        // Non-pooled prefabs with a VisualEffect own world-space particle state (trails, debris) that should
        // outlive the projectile transform. Reparent the body to the scene root, stop new spawns, and schedule
        // self-destruct so already-emitted particles complete their lifetimes instead of being wiped at impact.
        private void DetachAndLingerVisualEffectBody()
        {
            if (_projectileBody == null)
                return;

            VisualEffect[] effects = _projectileBody.GetComponentsInChildren<VisualEffect>(true);
            if (effects.Length == 0)
                return;

            _projectileBody.transform.SetParent(null, worldPositionStays: true);
            for (int i = 0; i < effects.Length; i++)
                effects[i].Stop();

            Object.Destroy(_projectileBody, VisualEffectLingerSeconds);
            _projectileBody = null!;
        }

        private void EndAt(Vector3 point)
        {
            if (!_active)
                return;

            _active = false;
            _group.transform.position = point;
        }

        private void Orient()
        {
            _group.transform.rotation = Quaternion.LookRotation(_direction);
        }

        private void ReconcilePosition(float dt)
        {
            float errorSq = (_group.transform.position - _authoritativePosition).sqrMagnitude;
            if (errorSq <= 0.000001f)
                return;
            if (errorSq >= HardSnapDistance * HardSnapDistance)
            {
                _group.transform.position = _authoritativePosition;
                return;
            }
            float alpha = VFXUtils.ExponentialAlpha(dt, PositionCorrectionHalfLife);
            _group.transform.position = Vector3.Lerp(_group.transform.position, _authoritativePosition, alpha);
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "unknown";
            return value[..Mathf.Min(6, value.Length)];
        }

        private static float ResolveVisualScale(float visualScale)
        {
            return visualScale > 0f ? visualScale : 1f;
        }

        private void ApplyLifetimeScale()
        {
            if (_projectileBody == null || Mathf.Approximately(_scaleMultiplierAtLifetimeEnd, 1f))
                return;

            float progress = _maxDistance > 0f
                ? Mathf.Clamp01(_traveled / _maxDistance)
                : 0f;
            _projectileBody.transform.localScale = _initialBodyScale
                * Mathf.Lerp(1f, _scaleMultiplierAtLifetimeEnd, progress);
        }

        private static float ResolveEndScaleMultiplier(float multiplier)
        {
            return multiplier > 0f ? Mathf.Clamp01(multiplier) : 1f;
        }
    }
}

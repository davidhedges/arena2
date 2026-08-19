#nullable enable
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    /// <summary>
    /// Drives a presentation transform's uniform scale toward a requested size over time,
    /// so size-changing statuses (Gigantism) grow in and shrink back instead of snapping.
    ///
    /// Presentation only: hit radius, hit height and melee reach stay server-owned.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PresentationScaleController : MonoBehaviour
    {
        private const float DefaultGrowSeconds = 0.45f;
        private const float DefaultShrinkSeconds = 0.35f;
        private const float ScaleEpsilon = 0.0001f;

        [SerializeField] [Min(0f)] private float growSeconds = DefaultGrowSeconds;
        [SerializeField] [Min(0f)] private float shrinkSeconds = DefaultShrinkSeconds;

        private Transform? _target;
        private float _fromScale = 1f;
        private float _targetScale = 1f;
        private float _currentScale = 1f;
        private float _elapsedSeconds;
        private float _durationSeconds;

        public float CurrentScale => _currentScale;
        public float TargetScale => _targetScale;
        public bool IsAnimating => _elapsedSeconds < _durationSeconds;

        /// <summary>
        /// Points the controller at the transform it scales. Safe to call on every avatar
        /// rebind: the live scale is re-applied to the new root without restarting the ramp.
        /// </summary>
        public void SetTarget(Transform? target)
        {
            _target = target;
            ApplyScale();
        }

        /// <summary>
        /// Requests a uniform scale. Ramps toward it unless <paramref name="immediate"/>,
        /// which is what spawn-time hydration wants so an already-sized entity pops in sized.
        /// </summary>
        public void SetScale(float scale, bool immediate = false)
        {
            if (immediate)
            {
                _fromScale = scale;
                _targetScale = scale;
                _currentScale = scale;
                _elapsedSeconds = 0f;
                _durationSeconds = 0f;
                ApplyScale();
                return;
            }

            if (Mathf.Abs(scale - _targetScale) <= ScaleEpsilon)
                return;

            _fromScale = _currentScale;
            _targetScale = scale;
            _elapsedSeconds = 0f;
            _durationSeconds = Mathf.Max(0f, scale > _currentScale ? growSeconds : shrinkSeconds);

            if (_durationSeconds <= 0f)
            {
                _currentScale = scale;
                ApplyScale();
            }
        }

        private void Update()
        {
            if (_elapsedSeconds >= _durationSeconds)
                return;

            _elapsedSeconds = Mathf.Min(_elapsedSeconds + Time.unscaledDeltaTime, _durationSeconds);
            float progress = _elapsedSeconds / _durationSeconds;
            _currentScale = Mathf.Lerp(_fromScale, _targetScale, SmoothStep(progress));
            ApplyScale();
        }

        private static float SmoothStep(float t) => t * t * (3f - 2f * t);

        private void ApplyScale()
        {
            Transform? target = _target;
            if (target == null)
                return;

            target.localScale = Vector3.one * _currentScale;
        }
    }
}

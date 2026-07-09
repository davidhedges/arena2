#nullable enable
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Tiny animator pause for the predicted melee contact cue (feel audit F5
    /// slice 2). Freezes the host's Animator for at most
    /// <see cref="MaxHitstopSeconds"/> of unscaled time, then restores the
    /// speed it captured. Owns this orthogonal Animator property exclusively;
    /// nothing else in the runtime writes Animator.speed.
    /// </summary>
    public sealed class MeleeContactHitstop : MonoBehaviour
    {
        public const float MaxHitstopSeconds = 0.05f;

        private Animator? _animator;
        private float _restoreSpeed = 1f;
        private float _resumeAtRealtime;
        private bool _active;

        public static void Play(GameObject host, float durationSeconds)
        {
            if (host == null)
                return;

            MeleeContactHitstop hitstop = host.GetComponent<MeleeContactHitstop>();
            if (hitstop == null)
                hitstop = host.AddComponent<MeleeContactHitstop>();
            hitstop.Begin(durationSeconds);
        }

        private void Begin(float durationSeconds)
        {
            float clamped = Mathf.Clamp(durationSeconds, 0f, MaxHitstopSeconds);
            if (clamped <= 0f)
                return;

            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();
            if (_animator == null)
                return;

            if (!_active)
            {
                _restoreSpeed = _animator.speed;
                _animator.speed = 0f;
                _active = true;
            }

            _resumeAtRealtime = Mathf.Max(_resumeAtRealtime, Time.realtimeSinceStartup + clamped);
        }

        private void LateUpdate()
        {
            if (!_active || Time.realtimeSinceStartup < _resumeAtRealtime)
                return;

            Restore();
        }

        private void OnDisable()
        {
            Restore();
        }

        private void Restore()
        {
            if (_active && _animator != null)
                _animator.speed = _restoreSpeed;
            _active = false;
        }
    }
}

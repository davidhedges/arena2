#nullable enable

using System;
using Arena.Network;
using UnityEngine;

namespace Arena.Interaction
{
    /// <summary>
    /// Presentation-only trap driver. The server owns every trap decision; the
    /// only thing replicated is the cycle anchor timestamp, and the only thing
    /// this does is scrub the vendor clip to the frame that timestamp implies.
    ///
    /// Each vendor trap controller is a single looping state with no transitions
    /// and no parameters, so scrubbing is exact and a player who joins mid-cycle
    /// lands on the correct frame instead of replaying the strike.
    ///
    /// No client prediction: a trap firing is server news. At 40 ms RTT the
    /// spikes rise 40 ms late and the damage lands with them, which reads fair.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TrapAuthoring))]
    public sealed class TrapPresenter : MonoBehaviour
    {
        /// <summary>
        /// Never scrub to exactly 1. Every vendor trap clip is authored with
        /// `m_LoopTime: 1`, and a looping state takes the fractional part of the
        /// normalized time, so 1.0 samples as 0.0 — the strike frame. Both ends
        /// of a clip hold the same retracted pose, so the wrap is invisible in
        /// the geometry and shows up only as a second spark burst fired just as
        /// the trap finishes retracting (owner report, 2026-07-27).
        ///
        /// This doubles as the dormant pose: the clip's last frame is retracted
        /// with its sparks emitting nothing, whereas frame 0 is the strike.
        /// </summary>
        private const float RestNormalizedTime = 0.999f;

        [SerializeField] private TrapAuthoring? _authoring;
        [SerializeField] private Animator[] _animators = Array.Empty<Animator>();

        private bool _hasCycle;
        private long _cycleStartedAtMs;
        private ulong _activation;
        private int _stateHash;
        private bool _restPoseApplied;

        public string TrapDefinitionId
            => _authoring == null ? string.Empty : _authoring.TrapDefinitionId;

        public bool HasCycle => _hasCycle;
        public ulong Activation => _activation;

        public void Configure(TrapAuthoring authoring, Animator[] animators)
        {
            _authoring = authoring;
            _animators = animators ?? Array.Empty<Animator>();
        }

        private void Awake()
        {
            if (_authoring == null)
                _authoring = GetComponent<TrapAuthoring>();
            if (_animators == null || _animators.Length == 0)
                _animators = GetComponentsInChildren<Animator>(true);
            ApplyProfileController();
            _stateHash = ResolveStateHash();
        }

        private void OnEnable()
        {
            TrapRuntimeRegistry.Register(this);
            ClearAuthoritativeCycle();
        }

        private void OnDisable()
        {
            TrapRuntimeRegistry.Unregister(this);
        }

        public void ApplyAuthoritativeCycle(long cycleStartedAtMs, ulong activation)
        {
            if (_hasCycle && activation < _activation)
                return;

            _hasCycle = true;
            _cycleStartedAtMs = cycleStartedAtMs;
            _activation = activation;
            _restPoseApplied = false;
        }

        public void ClearAuthoritativeCycle()
        {
            _hasCycle = false;
            _restPoseApplied = false;
            ApplyRestPose();
        }

        private void Update()
        {
            TrapProfile? profile = _authoring == null ? null : _authoring.Profile;
            if (profile == null || _stateHash == 0)
                return;

            if (!_hasCycle)
            {
                ApplyRestPose();
                return;
            }

            float clipSeconds = (ArenaServerClock.ServerNowMs - _cycleStartedAtMs) / 1000f
                - profile.TriggerDelaySeconds;
            if (clipSeconds < 0f)
            {
                // The telegraph window: the row exists but the clip has not
                // started. Park the rest pose; audio is the only v1 warning.
                ApplyRestPose();
                return;
            }

            Scrub(NormalizedClipTime(clipSeconds, profile.CycleSeconds));
            _restPoseApplied = false;
        }

        private void ApplyRestPose()
        {
            if (_restPoseApplied || _stateHash == 0)
                return;

            Scrub(RestNormalizedTime);
            _restPoseApplied = true;
        }

        /// <summary>
        /// Clip phase for a scrub, clamped strictly below 1 so a looping state
        /// cannot wrap the tail of the cycle back onto the strike frame.
        /// </summary>
        public static float NormalizedClipTime(float clipSeconds, float cycleSeconds)
        {
            if (cycleSeconds <= 0f || !float.IsFinite(clipSeconds))
                return 0f;

            return Mathf.Clamp(clipSeconds / cycleSeconds, 0f, RestNormalizedTime);
        }

        private void Scrub(float normalizedTime)
        {
            for (int i = 0; i < _animators.Length; i++)
            {
                Animator animator = _animators[i];
                if (animator == null)
                    continue;

                animator.speed = 0f;
                animator.Play(_stateHash, 0, normalizedTime);
                animator.Update(0f);
            }
        }

        /// <summary>
        /// A profile may ship its own controller when the vendor clip needed
        /// retiming. Applying it here keeps the wrapper prefab a pure nesting of
        /// the untouched vendor prefab.
        /// </summary>
        private void ApplyProfileController()
        {
            TrapProfile? profile = _authoring == null ? null : _authoring.Profile;
            RuntimeAnimatorController? controller = profile == null ? null : profile.AnimatorController;
            if (controller == null)
                return;

            for (int i = 0; i < _animators.Length; i++)
            {
                Animator animator = _animators[i];
                if (animator != null)
                    animator.runtimeAnimatorController = controller;
            }
        }

        private int ResolveStateHash()
        {
            TrapProfile? profile = _authoring == null ? null : _authoring.Profile;
            if (profile == null)
                return 0;

            string stateName = profile.AnimatorStateName;
            return string.IsNullOrWhiteSpace(stateName)
                ? 0
                : Animator.StringToHash(stateName);
        }
    }
}

#nullable enable

using System;
using UnityEngine;

namespace Arena.Interaction
{
    public enum TrapTriggerKind
    {
        /// <summary>A player capsule entering the trigger volume starts a cycle.</summary>
        Proximity = 0,

        /// <summary>The cycle runs forever from module start; no trigger, no state row.</summary>
        Always = 1,
    }

    public enum TrapOnHitEffectKind
    {
        Damage = 0,
        Dot = 1,
    }

    /// <summary>Trap-local oriented box. Yaw comes from the placed trap root.</summary>
    [Serializable]
    public struct TrapVolume
    {
        public Vector3 center;
        public Vector3 size;

        public TrapVolume(Vector3 center, Vector3 size)
        {
            this.center = center;
            this.size = size;
        }

        public bool IsValid =>
            Finite(center) && Finite(size) && size.x > 0f && size.y > 0f && size.z > 0f;

        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }

    /// <summary>
    /// One sample of the hazard centre's trap-local offset, at <see cref="tMs"/>
    /// milliseconds into the clip. The server interpolates linearly between
    /// samples and clamps outside the authored range.
    /// </summary>
    [Serializable]
    public struct TrapHazardTrackKey
    {
        [Min(0)] public int tMs;
        public Vector3 offset;

        public TrapHazardTrackKey(int tMs, Vector3 offset)
        {
            this.tMs = tMs;
            this.offset = offset;
        }
    }

    /// <summary>
    /// One authored effect applied when the hazard catches an actor. Each entry
    /// maps 1:1 onto a server <c>EffectPacket</c> variant, so a new trap kind is
    /// a new profile row rather than new server code.
    /// </summary>
    [Serializable]
    public sealed class TrapOnHitEffect
    {
        public TrapOnHitEffectKind effect = TrapOnHitEffectKind.Damage;

        [Header("DAMAGE")]
        [Min(0)] public int amount;

        [Header("Shared")]
        public string damageType = "PHYSICAL";

        [Header("DOT")]
        [Min(0)] public int tickAmount;
        [Min(0)] public int tickIntervalMs;
        [Min(0)] public int durationMs;
        public string stackGroup = string.Empty;
        [Min(1)] public int maxStacks = 1;
        public string stackPolicy = "REFRESH";
    }

    /// <summary>
    /// Timing, hazard geometry and damage for one trap kind. Placement lives in
    /// the generated trap manifest; retuning a profile therefore never requires
    /// a dungeon rebuild — export the profiles and republish.
    /// </summary>
    [CreateAssetMenu(
        fileName = "TrapProfile",
        menuName = "Arena/Interaction/Trap Profile")]
    public sealed class TrapProfile : ScriptableObject
    {
        [SerializeField] private string _profileId = string.Empty;
        [SerializeField] private TrapTriggerKind _triggerKind = TrapTriggerKind.Proximity;

        [Header("Timing (clip-relative, milliseconds)")]
        [SerializeField, Min(0)] private int _triggerDelayMs;
        [SerializeField, Min(1)] private int _cycleMs = 1000;
        [SerializeField, Min(0)] private int _hazardStartMs;
        [SerializeField, Min(0)] private int _hazardEndMs;
        [SerializeField, Min(0)] private int _rearmMs;

        [Header("Geometry (trap-local, yaw applied at runtime)")]
        [SerializeField] private TrapVolume _triggerVolume =
            new(new Vector3(0f, 1f, 0f), new Vector3(4f, 2f, 4f));
        [SerializeField] private TrapVolume _hazardVolume =
            new(new Vector3(0f, 0.6f, 0f), new Vector3(4f, 1.2f, 4f));
        [SerializeField] private TrapHazardTrackKey[] _hazardTrack =
            Array.Empty<TrapHazardTrackKey>();

        [Header("Effects")]
        [SerializeField] private TrapOnHitEffect[] _onHit = Array.Empty<TrapOnHitEffect>();
        [SerializeField] private bool _oneHitPerActivation = true;

        [Header("Presentation")]
        [Tooltip("Animator state that holds the single rest -> fire -> rest clip. "
            + "The presenter scrubs it; it never plays it at speed.")]
        [SerializeField] private string _animatorStateName = string.Empty;

        [Tooltip("Optional Arena-owned controller applied over the vendor prefab's own. "
            + "Set when the vendor clip needed retiming — the profile already owns the "
            + "state name and the cycle length, so it owns the clip that defines them.")]
        [SerializeField] private RuntimeAnimatorController? _animatorController;

        public string ProfileId => NormalizeId(_profileId);
        public TrapTriggerKind TriggerKind => _triggerKind;
        public int TriggerDelayMs => Mathf.Max(0, _triggerDelayMs);
        public int CycleMs => Mathf.Max(1, _cycleMs);
        public int HazardStartMs => Mathf.Max(0, _hazardStartMs);
        public int HazardEndMs => Mathf.Max(0, _hazardEndMs);
        public int RearmMs => Mathf.Max(0, _rearmMs);
        public TrapVolume TriggerVolume => _triggerVolume;
        public TrapVolume HazardVolume => _hazardVolume;
        public TrapHazardTrackKey[] HazardTrack => _hazardTrack;
        public TrapOnHitEffect[] OnHit => _onHit;
        public bool OneHitPerActivation => _oneHitPerActivation;
        public string AnimatorStateName => (_animatorStateName ?? string.Empty).Trim();
        public RuntimeAnimatorController? AnimatorController => _animatorController;

        public float TriggerDelaySeconds => TriggerDelayMs / 1000f;
        public float CycleSeconds => CycleMs / 1000f;

        public void Configure(
            string profileId,
            TrapTriggerKind triggerKind,
            int triggerDelayMs,
            int cycleMs,
            int hazardStartMs,
            int hazardEndMs,
            int rearmMs,
            TrapVolume triggerVolume,
            TrapVolume hazardVolume,
            TrapHazardTrackKey[] hazardTrack,
            TrapOnHitEffect[] onHit,
            bool oneHitPerActivation,
            string animatorStateName,
            RuntimeAnimatorController? animatorController = null)
        {
            _profileId = NormalizeId(profileId);
            _triggerKind = triggerKind;
            _triggerDelayMs = Mathf.Max(0, triggerDelayMs);
            _cycleMs = Mathf.Max(1, cycleMs);
            _hazardStartMs = Mathf.Max(0, hazardStartMs);
            _hazardEndMs = Mathf.Max(0, hazardEndMs);
            _rearmMs = Mathf.Max(0, rearmMs);
            _triggerVolume = triggerVolume;
            _hazardVolume = hazardVolume;
            _hazardTrack = hazardTrack ?? Array.Empty<TrapHazardTrackKey>();
            _onHit = onHit ?? Array.Empty<TrapOnHitEffect>();
            _oneHitPerActivation = oneHitPerActivation;
            _animatorStateName = (animatorStateName ?? string.Empty).Trim();
            _animatorController = animatorController;
        }

        /// <summary>
        /// Trap-local hazard centre at <paramref name="clipMs"/>. An empty track
        /// is a stationary hazard; a one-key track is a constant offset.
        /// </summary>
        public Vector3 HazardCenterAt(float clipMs)
        {
            Vector3 center = _hazardVolume.center;
            if (_hazardTrack == null || _hazardTrack.Length == 0)
                return center;
            if (_hazardTrack.Length == 1)
                return center + _hazardTrack[0].offset;

            if (clipMs <= _hazardTrack[0].tMs)
                return center + _hazardTrack[0].offset;
            int last = _hazardTrack.Length - 1;
            if (clipMs >= _hazardTrack[last].tMs)
                return center + _hazardTrack[last].offset;

            for (int i = 1; i <= last; i++)
            {
                TrapHazardTrackKey next = _hazardTrack[i];
                if (clipMs > next.tMs)
                    continue;

                TrapHazardTrackKey previous = _hazardTrack[i - 1];
                float span = next.tMs - previous.tMs;
                float blend = span <= 0f ? 0f : (clipMs - previous.tMs) / span;
                return center + Vector3.Lerp(previous.offset, next.offset, blend);
            }

            return center + _hazardTrack[last].offset;
        }

        private void OnValidate()
        {
            _profileId = NormalizeId(_profileId);
            _cycleMs = Mathf.Max(1, _cycleMs);
            _hazardStartMs = Mathf.Clamp(_hazardStartMs, 0, _cycleMs);
            _hazardEndMs = Mathf.Clamp(_hazardEndMs, _hazardStartMs, _cycleMs);
            _hazardTrack ??= Array.Empty<TrapHazardTrackKey>();
            _onHit ??= Array.Empty<TrapOnHitEffect>();
        }

        private static string NormalizeId(string value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
    }
}

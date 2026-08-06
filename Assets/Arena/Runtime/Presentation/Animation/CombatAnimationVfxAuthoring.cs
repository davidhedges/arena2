#nullable enable
using System;
using UnityEngine;

namespace Arena.Presentation
{
    public enum CombatAnimationVfxAnchor
    {
        CharacterRoot = 0,
        RightHand = 1,
        LeftHand = 2,
        MainWeapon = 3,
        MainWeaponBladeStart = 4,
        MainWeaponBladeEnd = 5,
    }

    public enum CombatAnimationVfxAttachment
    {
        FollowAnchor = 0,
        SpawnAtAnchor = 1,
    }

    /// <summary>
    /// A semantic VFX opportunity calibrated against one animation clip. The
    /// concrete effect is deliberately supplied by the attack/runtime binding.
    /// </summary>
    [Serializable]
    public sealed class CombatAnimationVfxTrack
    {
        [Tooltip("Animation clip whose sampled timeline owns this slot.")]
        public AnimationClip? clip;
        [Tooltip("Semantic slot name shared by the animation and attack binding, for example SLASH_PRIMARY.")]
        public string slotId = "SLASH_PRIMARY";
        [Tooltip("Clip-local time at which the effect is spawned.")]
        [Min(0f)] public float startTimeSeconds;
        [Tooltip("Optional clip-local time at which the effect is cut. Zero or a value at/before Start Time lets the prefab finish naturally.")]
        [Min(0f)] public float endTimeSeconds;
        public CombatAnimationVfxAnchor anchor = CombatAnimationVfxAnchor.CharacterRoot;
        public CombatAnimationVfxAttachment attachment = CombatAnimationVfxAttachment.FollowAnchor;
        [Tooltip("Slot-space position. Prefab normalization from CombatVFXRegistry is added after this value.")]
        public Vector3 localPosition = Vector3.zero;
        [Tooltip("Slot-space rotation. Prefab normalization from CombatVFXRegistry is composed after this value.")]
        public Vector3 localEulerAngles = Vector3.zero;
        [Tooltip("Per-animation shape correction. Prefab normalization from CombatVFXRegistry is multiplied by this value.")]
        public Vector3 localScale = Vector3.one;

        public string NormalizedSlotId => NormalizeSlotId(slotId);
        public bool HasFiniteWindow => endTimeSeconds > startTimeSeconds;

        public static string NormalizeSlotId(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
        }
    }

    /// <summary>
    /// Deterministic binding from an animation's semantic slot to a registered
    /// VFX template. An empty VFX id explicitly disables the slot when supplied
    /// as a request-time override.
    /// </summary>
    [Serializable]
    public struct CombatAnimationVfxBinding
    {
        [Tooltip("Semantic slot exposed by the animation VFX track.")]
        public string slotId;
        [Tooltip("Stable id registered in CombatVFXRegistry. Leave empty to disable this slot in a runtime override.")]
        public string vfxId;

        public CombatAnimationVfxBinding(string slotId, string vfxId)
        {
            this.slotId = slotId ?? string.Empty;
            this.vfxId = vfxId ?? string.Empty;
        }

        public string NormalizedSlotId => CombatAnimationVfxTrack.NormalizeSlotId(slotId);
        public string NormalizedVfxId => string.IsNullOrWhiteSpace(vfxId)
            ? string.Empty
            : vfxId.Trim().ToUpperInvariant();
    }
}

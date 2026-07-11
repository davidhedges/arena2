#nullable enable
using System;
using System.Collections.Generic;
using Arena.Presentation;
using UnityEngine;

namespace Arena.Entity
{
    [CreateAssetMenu(menuName = "Arena/NPC Visual Profile", fileName = "NpcVisualProfile")]
    public sealed class NpcVisualProfile : ScriptableObject
    {
        [SerializeField] private UnityEngine.Object? prefab;
        [SerializeField] private string primaryAnimatorPath = string.Empty;
        [SerializeField] private RuntimeAnimatorController? animatorControllerOverride;
        [SerializeField] private float presentationVerticalOffset;
        [SerializeField] private NpcNativeAnimationRoleMap animations = new();
        [SerializeField] private List<NpcNativeActionAnimationEntry> actionAnimations = new();
        [SerializeField] private NpcHardCrowdControlFallbackPolicy hardCrowdControlFallbackPolicy =
            NpcHardCrowdControlFallbackPolicy.ReadyPose;
        [SerializeField] private List<NpcVisualSocketEntry> vfxSockets = new();
        [SerializeField] private List<NpcStatusReactionEntry> statusReactions = new();

        public UnityEngine.Object? Prefab => prefab;
        public string PrimaryAnimatorPath => primaryAnimatorPath?.Trim() ?? string.Empty;
        public RuntimeAnimatorController? AnimatorControllerOverride => animatorControllerOverride;
        public float PresentationVerticalOffset => presentationVerticalOffset;
        public NpcNativeAnimationRoleMap Animations => animations;
        public IReadOnlyList<NpcNativeActionAnimationEntry> ActionAnimations => actionAnimations;
        public NpcHardCrowdControlFallbackPolicy HardCrowdControlFallbackPolicy => hardCrowdControlFallbackPolicy;
        public IReadOnlyList<NpcVisualSocketEntry> VfxSockets => vfxSockets;
        public IReadOnlyList<NpcStatusReactionEntry> StatusReactions => statusReactions;

        public bool TryResolvePrimaryAnimator(GameObject root, out Animator animator)
        {
            Transform? target = string.IsNullOrEmpty(PrimaryAnimatorPath)
                || string.Equals(PrimaryAnimatorPath, ".", StringComparison.Ordinal)
                ? root.transform
                : root.transform.Find(PrimaryAnimatorPath);
            animator = target != null ? target.GetComponent<Animator>() : null!;
            return animator != null;
        }

        public bool TryResolveVfxAnchor(
            GameObject root,
            string anchor,
            out Transform transform,
            out bool authored)
        {
            string normalized = NormalizeAnchor(anchor);
            for (int i = 0; i < vfxSockets.Count; i++)
            {
                NpcVisualSocketEntry? entry = vfxSockets[i];
                if (entry == null
                    || !string.Equals(entry.AnchorOrEmpty, normalized, StringComparison.Ordinal))
                {
                    continue;
                }

                authored = true;
                string path = entry.TransformPathOrEmpty;
                Transform? resolved = string.Equals(path, ".", StringComparison.Ordinal)
                    ? root.transform
                    : root.transform.Find(path);
                if (resolved != null)
                {
                    transform = resolved;
                    return true;
                }

                if (entry.fallbackPolicy == NpcVisualSocketFallbackPolicy.PresentationRoot)
                {
                    transform = root.transform;
                    return true;
                }

                transform = null!;
                return false;
            }

            authored = false;
            transform = null!;
            return false;
        }

        public bool TryGetActionAnimationStates(string? abilityId, out IReadOnlyList<string> states)
        {
            string normalized = NormalizeActionId(abilityId);
            for (int i = 0; i < actionAnimations.Count; i++)
            {
                NpcNativeActionAnimationEntry? entry = actionAnimations[i];
                if (entry != null
                    && string.Equals(entry.AbilityIdOrEmpty, normalized, StringComparison.Ordinal)
                    && entry.States.Count > 0)
                {
                    states = entry.States;
                    return true;
                }
            }

            states = Array.Empty<string>();
            return false;
        }

        internal static string NormalizeAnchor(string? value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

        internal static string NormalizeActionId(string? value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

        public static bool SupportsVfxAnchor(string anchor)
        {
            string normalized = NormalizeAnchor(anchor);
            return string.Equals(normalized, "LEFT_HAND", StringComparison.Ordinal)
                || string.Equals(normalized, "RIGHT_HAND", StringComparison.Ordinal)
                || string.Equals(normalized, "TARGET", StringComparison.Ordinal);
        }
    }

    [Serializable]
    public sealed class NpcNativeActionAnimationEntry
    {
        [SerializeField] private string abilityId = string.Empty;
        [SerializeField] private List<string> states = new();

        public string AbilityIdOrEmpty => NpcVisualProfile.NormalizeActionId(abilityId);
        public IReadOnlyList<string> States => states;
    }

    public enum NpcVisualSocketFallbackPolicy
    {
        SkipCue = 0,
        PresentationRoot = 1,
    }

    public enum NpcHardCrowdControlFallbackPolicy
    {
        ReadyPose = 0,
        FreezeCurrentPose = 1,
    }

    [Serializable]
    public sealed class NpcVisualSocketEntry
    {
        public string anchor = string.Empty;
        public string transformPath = string.Empty;
        public NpcVisualSocketFallbackPolicy fallbackPolicy = NpcVisualSocketFallbackPolicy.SkipCue;

        public string AnchorOrEmpty => NpcVisualProfile.NormalizeAnchor(anchor);
        public string TransformPathOrEmpty => transformPath?.Trim() ?? string.Empty;
    }

    [Serializable]
    public sealed class NpcNativeAnimationRoleMap
    {
        public List<string> idle = new() { "Idle01" };
        public List<string> ready = new();
        public List<string> walk = new() { "Walk_Forward" };
        public List<string> run = new() { "Run_Forward" };
        public List<string> basicAttack = new();
        public List<string> spellCastStart = new();
        public List<string> spellRelease = new();
        public List<string> spellCancel = new();
        public List<string> hit = new();
        public List<string> death = new() { "Death" };
    }
}

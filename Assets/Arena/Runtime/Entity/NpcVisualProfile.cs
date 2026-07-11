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
        [SerializeField] private NpcNativeAnimationRoleMap animations = new();
        [SerializeField] private List<NpcStatusReactionEntry> statusReactions = new();

        public UnityEngine.Object? Prefab => prefab;
        public string PrimaryAnimatorPath => primaryAnimatorPath?.Trim() ?? string.Empty;
        public NpcNativeAnimationRoleMap Animations => animations;
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

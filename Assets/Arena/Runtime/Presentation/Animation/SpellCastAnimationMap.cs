#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Weapon-agnostic description of the visible motion a spell cast performs. Combat animation
    /// sets translate these semantic motions into the animation families appropriate for their
    /// weapon pose.
    /// </summary>
    public enum SpellCastMotion
    {
        None = 0,
        Direct,
        Raise,
        Call,
        Omni,
        Special,
    }

    /// <summary>How a spell obtains its cast presentation.</summary>
    public enum SpellCastAnimationAssignmentKind
    {
        Motion = 0,
        Fixed,
    }

    /// <summary>Optional per-spell playback-layer override. <see cref="Auto"/> keeps the composer's derived layer.</summary>
    public enum SpellCastLayerOverride
    {
        Auto = 0,
        UpperBody,
        LeftGesture,
        FullBody,
        UpperBodyWhileMoving,
    }

    /// <summary>Optional per-spell combat-entry-mode override. <see cref="Auto"/> keeps the composer's derived mode.</summary>
    public enum SpellCastEntryModeOverride
    {
        Auto = 0,
        Immediate,
        AnimatedAfterCast,
        ImmediateForFullBody,
    }

    /// <summary>
    /// Weapon-agnostic spell cast classification: ordinary spells select a semantic
    /// <see cref="SpellCastMotion"/>, while exceptional spells may own a fixed animation that ignores
    /// the active combat animation set. The resolver combines a motion with the active set's family
    /// binding, casting hand, and the spell's derived archetype. Lives in Resources so this is the
    /// single global spell-to-motion/fixed-animation authority.
    /// </summary>
    [CreateAssetMenu(menuName = "Arena/Spell Cast Animation Map", fileName = "SpellCastAnimationMap")]
    public sealed class SpellCastAnimationMap : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            [Tooltip("Authoritative runtime action id, e.g. FIREBALL (no SPELL_ prefix — matches progression gameplay action_id).")]
            public string spellId;
            [Tooltip("Motion uses the active CombatAnimationSet's family binding. Fixed always uses fixedAnimation, regardless of combat set.")]
            public SpellCastAnimationAssignmentKind assignmentKind;
            [Tooltip("Semantic cast motion used when Assignment Kind is Motion.")]
            public SpellCastMotion motion;
            [Tooltip("Complete set-independent presentation used only when Assignment Kind is Fixed. Its spellId is replaced with this entry's spellId at resolution time.")]
            public WeaponSpellAnimationEntry fixedAnimation;

            [Header("Optional overrides (Auto/disabled = the composed default)")]
            [Tooltip("Override the release/instant playback layer. Auto uses the composer default (LeftGesture for left-hand 1H, else UpperBody/two-hand). Right-hand 1H is unsupported until a RightGesture layer exists.")]
            public SpellCastLayerOverride playbackLayer;
            [Tooltip("Override the combat entry mode. Auto uses the composer default.")]
            public SpellCastEntryModeOverride combatEntryMode;
            [Tooltip("A temporary weapon/shield visual spawned during the cast (enable for spells like BLESSED_SHIELD). Disabled = none.")]
            public SpellAnimatedPropHandoff animatedProp;
        }

        [SerializeField] private List<Entry> entries = new();
        [NonSerialized] private Dictionary<string, Entry>? _entryBySpellId;

        public IReadOnlyList<Entry> Entries => entries;

        public bool TryGetEntry(string spellId, out Entry entry)
        {
            string key = Normalize(spellId);
            if (key.Length != 0 && EntryBySpellId.TryGetValue(key, out entry))
                return true;

            entry = default;
            return false;
        }

        private Dictionary<string, Entry> EntryBySpellId
        {
            get
            {
                if (_entryBySpellId != null)
                    return _entryBySpellId;

                _entryBySpellId = new Dictionary<string, Entry>(StringComparer.Ordinal);
                for (int i = 0; i < entries.Count; i++)
                {
                    Entry candidate = entries[i];
                    string candidateKey = Normalize(candidate.spellId);
                    if (candidateKey.Length == 0 || _entryBySpellId.ContainsKey(candidateKey))
                        continue;

                    _entryBySpellId.Add(candidateKey, candidate);
                }

                return _entryBySpellId;
            }
        }

        private void OnEnable() => _entryBySpellId = null;
        private void OnValidate()
        {
            _entryBySpellId = null;
            SpellCastAnimationResolver.InvalidateCache();
        }

        private static string Normalize(string? spellId)
            => string.IsNullOrWhiteSpace(spellId) ? string.Empty : spellId.Trim().ToUpperInvariant();
    }
}

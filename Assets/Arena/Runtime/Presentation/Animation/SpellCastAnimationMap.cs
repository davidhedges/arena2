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
        Direct1H,
        Raise,
        Call,
        Omni,
        Special,
        Direct2H,
        Ground,
    }

    /// <summary>How a spell obtains its cast presentation, or explicitly opts out of one.</summary>
    public enum SpellCastAnimationAssignmentKind
    {
        LegacyMotion = 0,
        Fixed,
        NoAnimation,
        Catalog,
    }

    /// <summary>Optional per-spell playback-layer override. <see cref="Auto"/> keeps the composer's derived layer.</summary>
    public enum SpellCastLayerOverride
    {
        Auto = 0,
        UpperBody,
        LeftGesture,
        FullBody,
        UpperBodyWhileMoving,
        RightGesture,
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
    /// Global spell-to-animation selection. New assignments point at reusable catalog recipes.
    /// Legacy semantic motions and inline fixed presentations remain migration paths for existing
    /// content, while intentionally unanimated spells opt out explicitly.
    /// </summary>
    [CreateAssetMenu(menuName = "Arena/Spell Cast Animation Map", fileName = "SpellCastAnimationMap")]
    public sealed class SpellCastAnimationMap : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            [Tooltip("Authoritative runtime action id, e.g. FIREBALL (no SPELL_ prefix — matches progression gameplay action_id).")]
            public string spellId;
            [Tooltip("Catalog selects a shared reusable recipe. Legacy Motion preserves the old per-set family path. Fixed is an inline exception. No Animation explicitly suppresses playback.")]
            public SpellCastAnimationAssignmentKind assignmentKind;
            [Tooltip("Shared recipe id used when Assignment Kind is Catalog.")]
            public string animationId;
            [Tooltip("Semantic cast motion used only when Assignment Kind is Legacy Motion.")]
            public SpellCastMotion motion;
            [Tooltip("Complete set-independent presentation used only when Assignment Kind is Fixed. Its spellId is replaced with this entry's spellId at resolution time.")]
            public WeaponSpellAnimationEntry fixedAnimation;

            [Header("Optional overrides (Auto/disabled = the composed default)")]
            [Tooltip("Override the release/instant playback layer. Auto uses the composer default (LeftGesture/RightGesture for one-hand casts, else UpperBody/two-hand).")]
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

#if UNITY_EDITOR
        public void EditorSetCatalogAssignment(string spellId, string animationId)
        {
            string normalizedSpellId = Normalize(spellId);
            if (normalizedSpellId.Length == 0)
                return;

            for (int index = 0; index < entries.Count; index++)
            {
                Entry entry = entries[index];
                if (!string.Equals(Normalize(entry.spellId), normalizedSpellId, StringComparison.Ordinal))
                    continue;

                entry.spellId = normalizedSpellId;
                entry.assignmentKind = SpellCastAnimationAssignmentKind.Catalog;
                entry.animationId = Normalize(animationId);
                entry.motion = SpellCastMotion.None;
                entry.fixedAnimation = default;
                entries[index] = entry;
                _entryBySpellId = null;
                SpellCastAnimationResolver.InvalidateCache();
                return;
            }

            entries.Add(new Entry
            {
                spellId = normalizedSpellId,
                assignmentKind = SpellCastAnimationAssignmentKind.Catalog,
                animationId = Normalize(animationId),
            });
            _entryBySpellId = null;
            SpellCastAnimationResolver.InvalidateCache();
        }
#endif

        private static string Normalize(string? spellId)
            => string.IsNullOrWhiteSpace(spellId) ? string.Empty : spellId.Trim().ToUpperInvariant();
    }
}

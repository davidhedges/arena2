#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
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
    /// Weapon-agnostic assignment of a spell to a cast-animation flavor family (design doc §4/§5):
    /// <c>spellId → baseName</c>, authored once, plus optional per-spell overrides for the handful of
    /// spells that need a specific playback layer, entry mode, or an animated prop (e.g.
    /// <c>BLESSED_SHIELD</c>/<c>BLADE_BARRIER</c> keep their shield/weapon visual). The resolver
    /// combines this with the casting weapon's <see cref="CombatAnimationSet.OneHandedCastHand"/> and
    /// the spell's derived archetype. A spell absent here (or whose weapon authored an explicit entry)
    /// is unaffected. Lives in Resources so the resolver can load it globally.
    /// </summary>
    [CreateAssetMenu(menuName = "Arena/Spell Cast Animation Map", fileName = "SpellCastAnimationMap")]
    public sealed class SpellCastAnimationMap : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            [Tooltip("Runtime spell/action id, e.g. FIREBALL (no SPELL_ prefix — matches CombatAnimationSet spellId).")]
            public string spellId;
            [Tooltip("Flavor-family base name from the SpellCastAnimationLibrary, e.g. MagicAttackGround01.")]
            public string baseName;

            [Header("Optional overrides (Auto/disabled = the composed default)")]
            [Tooltip("Override the release/instant playback layer. Auto uses the composer default (LeftGesture for left-hand 1H, else UpperBody).")]
            public SpellCastLayerOverride playbackLayer;
            [Tooltip("Override the combat entry mode. Auto uses the composer default.")]
            public SpellCastEntryModeOverride combatEntryMode;
            [Tooltip("A temporary weapon/shield visual spawned during the cast (enable for spells like BLESSED_SHIELD). Disabled = none.")]
            public SpellAnimatedPropHandoff animatedProp;
        }

        [SerializeField] private List<Entry> entries = new();

        public IReadOnlyList<Entry> Entries => entries;

        public bool TryGetEntry(string spellId, out Entry entry)
        {
            string key = Normalize(spellId);
            if (key.Length != 0)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (string.Equals(Normalize(entries[i].spellId), key, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(entries[i].baseName))
                    {
                        entry = entries[i];
                        return true;
                    }
                }
            }

            entry = default;
            return false;
        }

        private static string Normalize(string? spellId)
            => string.IsNullOrWhiteSpace(spellId) ? string.Empty : spellId.Trim().ToUpperInvariant();
    }
}

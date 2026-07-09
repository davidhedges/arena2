#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Weapon-agnostic assignment of a spell to a cast-animation flavor family (design doc §4/§5):
    /// <c>spellId → baseName</c>, authored once. The resolver combines this with the casting weapon's
    /// <see cref="CombatAnimationSet.OneHandedCastHand"/> and the spell's derived archetype to
    /// produce the concrete stitched entry. Lives in Resources so the resolver can load it globally.
    /// A spell absent here (or whose weapon authored an explicit entry) is unaffected.
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
        }

        [SerializeField] private List<Entry> entries = new();

        public IReadOnlyList<Entry> Entries => entries;

        public bool TryGetBaseName(string spellId, out string baseName)
        {
            string key = Normalize(spellId);
            if (key.Length != 0)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (string.Equals(Normalize(entries[i].spellId), key, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(entries[i].baseName))
                    {
                        baseName = entries[i].baseName.Trim();
                        return true;
                    }
                }
            }

            baseName = string.Empty;
            return false;
        }

        private static string Normalize(string? spellId)
            => string.IsNullOrWhiteSpace(spellId) ? string.Empty : spellId.Trim().ToUpperInvariant();
    }
}

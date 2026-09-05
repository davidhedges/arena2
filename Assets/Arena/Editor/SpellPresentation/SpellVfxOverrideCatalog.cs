#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
using UnityEngine;

namespace Arena.Presentation
{
    [Serializable]
    public sealed class SpellVfxAbilityOverride
    {
        public string abilityId = string.Empty;
        [SerializeField] private List<SchoolVfxSlotEntry> slots = new();

        public string AbilityIdOrEmpty => WireIdentifier.Normalize(abilityId);
        public IReadOnlyList<SchoolVfxSlotEntry> Slots => slots;

        public bool TryGet(SpellVfxSlot slot, out SchoolVfxSlotEntry entry)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].slot == slot && !string.IsNullOrWhiteSpace(slots[i].vfxId))
                {
                    entry = slots[i];
                    return true;
                }
            }

            entry = default;
            return false;
        }
    }

    /// <summary>
    /// Editor-only per-spell exceptions to school-derived VFX slot looks.
    /// Cast hand belongs to the resolved animation presentation.
    /// </summary>
    [CreateAssetMenu(menuName = "Arena/Spell VFX Override Catalog", fileName = "SpellVfxOverrideCatalog")]
    public sealed class SpellVfxOverrideCatalog : ScriptableObject
    {
        [SerializeField] private List<SpellVfxAbilityOverride> entries = new();

        public IReadOnlyList<SpellVfxAbilityOverride> Entries => entries;

        public bool TryGet(string abilityId, out SpellVfxAbilityOverride entry)
        {
            string normalized = WireIdentifier.Normalize(abilityId);
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].AbilityIdOrEmpty, normalized, StringComparison.Ordinal))
                {
                    entry = entries[i];
                    return true;
                }
            }

            entry = null!;
            return false;
        }
    }
}

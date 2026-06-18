#nullable enable
using System;
using System.Collections.Generic;
using NHance.Assets.Scripts.Enums;
using NHance.Assets.Scripts.Items;
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    [CreateAssetMenu(menuName = "Arena/Appearance/Equipment Appearance Catalog")]
    public sealed class EquipmentAppearanceCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class EquipmentItem
        {
            public ItemTypeEnum expectedItemType;
            public NHItem? item;
        }

        [Serializable]
        public sealed class Entry
        {
            public string itemDefId = string.Empty;
            public string equipSlot = string.Empty;
            public string raceId = string.Empty;
            public string sexId = string.Empty;
            public bool enabled = true;
            public List<EquipmentItem> items = new();
        }

        [SerializeField] private List<Entry> entries = new();
        public IReadOnlyList<Entry> Entries => entries;

        public bool TryGetItems(
            string? itemDefId,
            string? equipSlot,
            string? raceId,
            string? sexId,
            out Entry entry)
        {
            string normalizedItem = CharacterAppearanceIds.Normalize(itemDefId);
            string normalizedSlot = CharacterAppearanceIds.Normalize(equipSlot);
            string normalizedRace = CharacterAppearanceIds.Normalize(raceId);
            string normalizedSex = CharacterAppearanceIds.Normalize(sexId);

            for (int i = 0; i < entries.Count; i++)
            {
                Entry candidate = entries[i];
                if (candidate == null || !candidate.enabled)
                    continue;

                if (CharacterAppearanceIds.Normalize(candidate.itemDefId) == normalizedItem
                    && CharacterAppearanceIds.Normalize(candidate.equipSlot) == normalizedSlot
                    && CharacterAppearanceIds.Normalize(candidate.raceId) == normalizedRace
                    && CharacterAppearanceIds.Normalize(candidate.sexId) == normalizedSex)
                {
                    entry = candidate;
                    return true;
                }
            }

            entry = null!;
            return false;
        }

        public void SetEntriesForEditor(List<Entry> replacement)
        {
            entries = replacement ?? new List<Entry>();
        }
    }
}

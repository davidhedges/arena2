#nullable enable
using System;
using System.Collections.Generic;
using NHance.Assets.Scripts.Enums;
using NHance.Assets.Scripts.Items;
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    [CreateAssetMenu(menuName = "Arena/Appearance/Avatar Part Catalog")]
    public sealed class AvatarPartCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public string partId = string.Empty;
            public string raceId = string.Empty;
            public string sexId = string.Empty;
            public AvatarPartSlot slot;
            public ItemTypeEnum expectedItemType;
            public bool enabled = true;
            public NHItem? item;
        }

        [SerializeField] private List<Entry> entries = new();
        public IReadOnlyList<Entry> Entries => entries;

        public bool TryGetItem(
            AvatarPartSlot slot,
            string? partId,
            string? raceId,
            string? sexId,
            out NHItem item,
            out string error)
        {
            string normalizedPart = CharacterAppearanceIds.Normalize(partId);
            if (string.IsNullOrWhiteSpace(normalizedPart))
            {
                item = null!;
                error = string.Empty;
                return false;
            }

            string normalizedRace = CharacterAppearanceIds.Normalize(raceId);
            string normalizedSex = CharacterAppearanceIds.Normalize(sexId);

            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry == null || !entry.enabled)
                    continue;

                if (entry.slot != slot ||
                    CharacterAppearanceIds.Normalize(entry.partId) != normalizedPart ||
                    CharacterAppearanceIds.Normalize(entry.raceId) != normalizedRace ||
                    CharacterAppearanceIds.Normalize(entry.sexId) != normalizedSex)
                {
                    continue;
                }

                if (entry.item == null)
                {
                    item = null!;
                    error = $"Avatar part '{normalizedPart}' has no NHItem prefab.";
                    return false;
                }

                if (entry.item.Type != entry.expectedItemType)
                {
                    item = null!;
                    error = $"Avatar part '{normalizedPart}' expected item type {entry.expectedItemType} but found {entry.item.Type}.";
                    return false;
                }

                item = entry.item;
                error = string.Empty;
                return true;
            }

            item = null!;
            error = $"Avatar part '{normalizedPart}' is not available for {normalizedRace}/{normalizedSex}.";
            return false;
        }

        public void SetEntriesForEditor(List<Entry> replacement)
        {
            entries = replacement ?? new List<Entry>();
        }
    }
}

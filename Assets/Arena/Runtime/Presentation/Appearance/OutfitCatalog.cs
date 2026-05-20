#nullable enable
using System;
using System.Collections.Generic;
using NHance.Assets.Scripts.Enums;
using NHance.Assets.Scripts.Items;
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    [CreateAssetMenu(menuName = "Arena/Appearance/Outfit Catalog")]
    public sealed class OutfitCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class OutfitItem
        {
            public ItemTypeEnum expectedItemType;
            public NHItem? item;
        }

        [Serializable]
        public sealed class Entry
        {
            public string outfitId = string.Empty;
            public string displayName = string.Empty;
            public bool enabled = true;
            public List<OutfitItem> items = new();
        }

        [SerializeField] private List<Entry> entries = new();
        public IReadOnlyList<Entry> Entries => entries;

        public bool TryGetOutfit(string? outfitId, out Entry outfit)
        {
            string normalized = CharacterAppearanceIds.Normalize(outfitId);
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry == null || !entry.enabled)
                    continue;

                if (CharacterAppearanceIds.Normalize(entry.outfitId) == normalized)
                {
                    outfit = entry;
                    return true;
                }
            }

            outfit = null!;
            return false;
        }

        public void SetEntriesForEditor(List<Entry> replacement)
        {
            entries = replacement ?? new List<Entry>();
        }
    }
}

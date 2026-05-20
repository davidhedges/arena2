#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    [CreateAssetMenu(menuName = "Arena/Appearance/Class Outfit Catalog")]
    public sealed class ClassOutfitCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public string classId = string.Empty;
            public string raceId = string.Empty;
            public string sexId = string.Empty;
            public string outfitId = string.Empty;
            public bool enabled = true;
        }

        [SerializeField] private List<Entry> entries = new();
        public IReadOnlyList<Entry> Entries => entries;

        public bool TryGetDefaultOutfitId(string? classId, string? raceId, string? sexId, out string outfitId)
        {
            string normalizedClass = CharacterAppearanceIds.Normalize(classId);
            string normalizedRace = CharacterAppearanceIds.Normalize(raceId);
            string normalizedSex = CharacterAppearanceIds.Normalize(sexId);

            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry == null || !entry.enabled)
                    continue;

                if (CharacterAppearanceIds.Normalize(entry.classId) == normalizedClass &&
                    CharacterAppearanceIds.Normalize(entry.raceId) == normalizedRace &&
                    CharacterAppearanceIds.Normalize(entry.sexId) == normalizedSex)
                {
                    outfitId = CharacterAppearanceIds.Normalize(entry.outfitId);
                    return !string.IsNullOrWhiteSpace(outfitId);
                }
            }

            outfitId = string.Empty;
            return false;
        }

        public void SetEntriesForEditor(List<Entry> replacement)
        {
            entries = replacement ?? new List<Entry>();
        }
    }
}

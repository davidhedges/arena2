#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    [CreateAssetMenu(menuName = "Arena/Appearance/Avatar Base Catalog")]
    public sealed class AvatarBaseCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public string raceId = string.Empty;
            public string sexId = string.Empty;
            public bool playerFacingEnabled;
            public GameObject? basePrefab;
        }

        [SerializeField] private List<Entry> entries = new();
        public IReadOnlyList<Entry> Entries => entries;

        public bool TryGetBasePrefab(string? raceId, string? sexId, out GameObject prefab)
        {
            string normalizedRace = CharacterAppearanceIds.Normalize(raceId);
            string normalizedSex = CharacterAppearanceIds.Normalize(sexId);

            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry == null || entry.basePrefab == null)
                    continue;

                if (CharacterAppearanceIds.Normalize(entry.raceId) == normalizedRace &&
                    CharacterAppearanceIds.Normalize(entry.sexId) == normalizedSex)
                {
                    prefab = entry.basePrefab;
                    return true;
                }
            }

            prefab = null!;
            return false;
        }

        public void SetEntriesForEditor(List<Entry> replacement)
        {
            entries = replacement ?? new List<Entry>();
        }
    }
}

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

        [Serializable]
        public sealed class WeaponVisualEntry
        {
            public string itemDefId = string.Empty;
            public string visualRoleId = string.Empty;
            public string raceId = string.Empty;
            public string sexId = string.Empty;
            public bool enabled = true;
            public GameObject? prefab;
        }

        [SerializeField] private List<Entry> entries = new();
        [SerializeField] private List<WeaponVisualEntry> weaponVisuals = new();
        public IReadOnlyList<Entry> Entries => entries;
        public IReadOnlyList<WeaponVisualEntry> WeaponVisuals => weaponVisuals;

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

        public bool TryGetWeaponVisual(
            string? itemDefId,
            string? visualRoleId,
            string? raceId,
            string? sexId,
            out WeaponVisualEntry entry)
        {
            string normalizedItem = CharacterAppearanceIds.Normalize(itemDefId);
            string normalizedRole = CharacterAppearanceIds.Normalize(visualRoleId);
            string normalizedRace = CharacterAppearanceIds.Normalize(raceId);
            string normalizedSex = CharacterAppearanceIds.Normalize(sexId);

            for (int i = 0; i < weaponVisuals.Count; i++)
            {
                WeaponVisualEntry candidate = weaponVisuals[i];
                if (candidate == null || !candidate.enabled || candidate.prefab == null)
                    continue;

                if (CharacterAppearanceIds.Normalize(candidate.itemDefId) == normalizedItem
                    && CharacterAppearanceIds.Normalize(candidate.visualRoleId) == normalizedRole
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

        public void SetWeaponVisualsForEditor(List<WeaponVisualEntry> replacement)
        {
            weaponVisuals = replacement ?? new List<WeaponVisualEntry>();
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using NHance.Assets.Scripts.Enums;
using NHance.Assets.Scripts.Items;
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    public enum WeaponAppearancePlacementProfile
    {
        LegacyAnimationBinding = 0,
        NHanceNative = 1,
    }

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
        public sealed class ArmorSetSlotVisual
        {
            public string equipSlot = string.Empty;
            public List<EquipmentItem> items = new();
        }

        [Serializable]
        public sealed class ArmorSetVisualEntry
        {
            public string armorSetId = string.Empty;
            public string raceId = string.Empty;
            public string sexId = string.Empty;
            public bool enabled = true;
            public List<ArmorSetSlotVisual> slots = new();
        }

        [Serializable]
        public sealed class WeaponVisualEntry
        {
            public string itemDefId = string.Empty;
            public string colorId = string.Empty;
            public string visualRoleId = string.Empty;
            public string raceId = string.Empty;
            public string sexId = string.Empty;
            public bool enabled = true;
            public GameObject? prefab;
            [Tooltip("Opt-in placement convention for this prefab. Legacy entries continue to use the animation-set binding unchanged.")]
            public WeaponAppearancePlacementProfile placementProfile;
        }

        [SerializeField] private List<Entry> entries = new();
        [SerializeField] private List<ArmorSetVisualEntry> armorSets = new();
        [SerializeField] private List<WeaponVisualEntry> weaponVisuals = new();
        public IReadOnlyList<Entry> Entries => entries;
        public IReadOnlyList<ArmorSetVisualEntry> ArmorSets => armorSets;
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

            for (int setIndex = 0; setIndex < armorSets.Count; setIndex++)
            {
                ArmorSetVisualEntry set = armorSets[setIndex];
                if (set == null
                    || !set.enabled
                    || CharacterAppearanceIds.Normalize(set.raceId) != normalizedRace
                    || CharacterAppearanceIds.Normalize(set.sexId) != normalizedSex)
                {
                    continue;
                }

                string normalizedSetId = CharacterAppearanceIds.Normalize(set.armorSetId);
                if (normalizedItem != $"ARMOR_SET_{normalizedSetId}_{normalizedSlot}")
                    continue;

                for (int slotIndex = 0; slotIndex < set.slots.Count; slotIndex++)
                {
                    ArmorSetSlotVisual slot = set.slots[slotIndex];
                    if (slot == null
                        || CharacterAppearanceIds.Normalize(slot.equipSlot) != normalizedSlot)
                    {
                        continue;
                    }

                    entry = new Entry
                    {
                        itemDefId = normalizedItem,
                        equipSlot = normalizedSlot,
                        raceId = normalizedRace,
                        sexId = normalizedSex,
                        enabled = true,
                        items = slot.items,
                    };
                    return true;
                }
            }

            entry = null!;
            return false;
        }

        public bool TryGetWeaponVisual(
            string? itemDefId,
            string? colorId,
            string? visualRoleId,
            string? raceId,
            string? sexId,
            out WeaponVisualEntry entry)
        {
            string normalizedItem = CharacterAppearanceIds.Normalize(itemDefId);
            string normalizedColor = CharacterAppearanceIds.Normalize(colorId);
            string normalizedRole = CharacterAppearanceIds.Normalize(visualRoleId);
            string normalizedRace = CharacterAppearanceIds.Normalize(raceId);
            string normalizedSex = CharacterAppearanceIds.Normalize(sexId);

            for (int i = 0; i < weaponVisuals.Count; i++)
            {
                WeaponVisualEntry candidate = weaponVisuals[i];
                if (candidate == null || !candidate.enabled || candidate.prefab == null)
                    continue;

                if (CharacterAppearanceIds.Normalize(candidate.itemDefId) == normalizedItem
                    && CharacterAppearanceIds.Normalize(candidate.colorId) == normalizedColor
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

        public static IEnumerable<string> WeaponVisualRoleIdsForKind(string? weaponKind)
        {
            switch (CharacterAppearanceIds.Normalize(weaponKind))
            {
                case "TWO_HAND_SWORD":
                case "TWO_HANDED_SWORD":
                case "TWO_HAND_AXE":
                case "TWO_HAND_HAMMER":
                case "POLEARM":
                    yield return "greatsword";
                    yield break;
                case "STAFF":
                    yield return "staff";
                    yield break;
                case "ONE_HAND_SWORD":
                case "ONE_HAND_AXE":
                case "ONE_HAND_HAMMER":
                case "ONE_HAND_FIST":
                    yield return "sword";
                    yield break;
                case "DAGGER_PAIR":
                    yield return "dagger_main";
                    yield return "dagger_off";
                    yield break;
                case "SWORD_AND_SHIELD":
                    yield return "sword";
                    yield return "shield";
                    yield break;
                case "SHIELD":
                    yield return "shield";
                    yield break;
                case "BOW":
                    yield return "bow_drawn";
                    yield return "bow_stowed";
                    yield return "quiver";
                    yield break;
            }
        }

        public void SetEntriesForEditor(List<Entry> replacement)
        {
            entries = replacement ?? new List<Entry>();
        }

        public void SetArmorSetsForEditor(List<ArmorSetVisualEntry> replacement)
        {
            armorSets = replacement ?? new List<ArmorSetVisualEntry>();
        }

        public void SetWeaponVisualsForEditor(List<WeaponVisualEntry> replacement)
        {
            weaponVisuals = replacement ?? new List<WeaponVisualEntry>();
        }
    }
}

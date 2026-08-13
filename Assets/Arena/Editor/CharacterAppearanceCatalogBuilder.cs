#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Arena.Presentation;
using Arena.Presentation.Appearance;
using NHance.Assets.Scripts.Enums;
using NHance.Assets.Scripts.Items;
using UnityEditor;
using UnityEngine;

namespace Arena.EditorTools
{
    public static class CharacterAppearanceCatalogBuilder
    {
        private const string CatalogFolder = "Assets/Arena/Resources/CharacterAppearance";
        private const string StylizedCharacterRoot = "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter";
        private const string BaseCatalogPath = CatalogFolder + "/AvatarBaseCatalog.asset";
        private const string PartCatalogPath = CatalogFolder + "/AvatarPartCatalog.asset";
        private const string OutfitCatalogPath = CatalogFolder + "/OutfitCatalog.asset";
        private const string EquipmentAppearanceCatalogPath = CatalogFolder + "/EquipmentAppearanceCatalog.asset";
        private const string WeaponAppearanceCatalogPath = "Assets/Arena/Resources/SharedData/weapon_appearance_catalog.shared.json";
        private static readonly bool IncludePeasantStarterHatAndCape = false;
        private static readonly CompleteArmorVisualSetSpec[] CompleteArmorVisualSets =
        {
            new("FMAGE_BL", "FMage", "Bl"),
            new("FMAGE_GN", "FMage", "Gn"),
            new("FMAGE_RD", "FMage", "Rd"),
            new("WARLOCK_GN", "Warlock", "Gn"),
            new("WARLOCK_PE", "Warlock", "Pe"),
            new("WARLOCK_VT", "Warlock", "Vt"),
            new("WIZARD_PE", "Wizard", "Pe"),
            new("WIZARD_VT", "Wizard", "Vt"),
            new("BARBARIAN_BL", "Barbarian", "Bl"),
            new("BARBARIAN_GN", "Barbarian", "Gn"),
            new("BARBARIAN_RD", "Barbarian", "Rd"),
            new("HUNTER_BL", "Hunter", "Bl"),
            new("HUNTER_GN", "Hunter", "Gn"),
            new("HUNTER_PE", "Hunter", "Pe"),
            new("HUNTER_RD", "Hunter", "Rd"),
            new("NRANGER_BL", "NRanger", "Bl"),
            new("NRANGER_RD", "NRanger", "Rd"),
            new("RANGER_GN", "Ranger", "Gn"),
            new("RANGER_PE", "Ranger", "Pe"),
            new("RANGER_RD", "Ranger", "Rd"),
            new("REAPER_BL", "Reaper", "Bl"),
            new("REAPER_CN", "Reaper", "Cn"),
            new("REAPER_GN", "Reaper", "Gn"),
            new("ROGUE_BL", "Rogue", "Bl"),
            new("ROGUE_GN", "Rogue", "Gn"),
            new("ROGUE_RD", "Rogue", "Rd"),
            new("DK_BL", "DK", "Bl"),
            new("DK_GN", "DK", "Gn"),
            new("DK_RD", "DK", "Rd"),
            new("DUNGPLATE_BL", "DungPlate", "Bl"),
            new("DUNGPLATE_PE", "DungPlate", "Pe"),
            new("DUNGPLATE_RD", "DungPlate", "Rd"),
            new("NWARRIOR_RD", "NWarrior", "Rd"),
            new("PALADIN_BL", "Paladin", "Bl"),
            new("PALADIN_GN", "Paladin", "Gn"),
            new("PALADIN_GR", "Paladin", "Gr"),
            new("PALADIN_RD", "Paladin", "Rd"),
            new("WARRIOR_GN", "Warrior", "Gn"),
            new("WARRIOR_PE", "Warrior", "Pe"),
            new("WARRIOR_RD", "Warrior", "Rd"),
        };

        [MenuItem("Arena/Appearance/Rebuild Default Catalog Assets")]
        public static void RebuildDefaultCatalogAssetsFromMenu()
        {
            RebuildDefaultCatalogAssets();
            EditorUtility.DisplayDialog(
                "Character Appearance Catalogs Rebuilt",
                $"Rebuilt default catalog assets in {CatalogFolder}.",
                "OK");
        }

        public static void RebuildDefaultCatalogAssetsBatch()
        {
            RebuildDefaultCatalogAssets();
        }

        public static void RebuildDefaultCatalogAssets()
        {
            ValidateSupportedStylizedCharacterImport();
            EnsureFolder("Assets/Arena/Resources");
            EnsureFolder(CatalogFolder);

            AvatarBaseCatalog baseCatalog = LoadOrCreate<AvatarBaseCatalog>(BaseCatalogPath);
            baseCatalog.SetEntriesForEditor(new List<AvatarBaseCatalog.Entry>
            {
                new()
                {
                    raceId = CharacterAppearanceIds.RaceHuman,
                    sexId = CharacterAppearanceIds.SexMale,
                    playerFacingEnabled = true,
                    basePrefab = LoadRequired<GameObject>("Assets/Arena/Resources/CharacterAvatarBases/HumanMale.prefab"),
                },
                new()
                {
                    raceId = CharacterAppearanceIds.RaceHuman,
                    sexId = CharacterAppearanceIds.SexFemale,
                    playerFacingEnabled = false,
                    basePrefab = LoadRequired<GameObject>("Assets/Arena/Resources/CharacterAvatarBases/HumanFemale.prefab"),
                },
            });

            AvatarPartCatalog partCatalog = LoadOrCreate<AvatarPartCatalog>(PartCatalogPath);
            partCatalog.SetEntriesForEditor(new List<AvatarPartCatalog.Entry>
            {
                Part(
                    CharacterAppearanceIds.DefaultBodyId,
                    AvatarPartSlot.Body,
                    ItemTypeEnum.Skin,
                    "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Character/Human/Male/Skin/Body/Hu_M_Body_01.prefab"),
                Part(
                    CharacterAppearanceIds.DefaultHeadId,
                    AvatarPartSlot.Head,
                    ItemTypeEnum.HeadSkin,
                    "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Character/Human/Male/Skin/Head/Hu_M_Head_01_A.prefab"),
                Part(
                    CharacterAppearanceIds.DefaultEyesId,
                    AvatarPartSlot.Eyes,
                    ItemTypeEnum.Eyes,
                    "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Character/Human/Eye/Eyes_Bl.prefab"),
            });

            OutfitCatalog outfitCatalog = LoadOrCreate<OutfitCatalog>(OutfitCatalogPath);
            outfitCatalog.SetEntriesForEditor(new List<OutfitCatalog.Entry>
            {
                PeasantOutfit(
                    "HUMAN_MALE_PEASANT_STARTER",
                    "Human Male Peasant Starter",
                    "Br",
                    "01"),
                Outfit(
                    "HUMAN_MALE_WARRIOR_STARTER",
                    "Human Male Warrior Starter",
                    "Hu_M",
                    "NWarrior",
                    "Bl"),
                Outfit(
                    "HUMAN_MALE_PALADIN_STARTER",
                    "Human Male Paladin Starter",
                    "Hu_M",
                    "NWarrior",
                    "Gn"),
                Outfit(
                    "HUMAN_MALE_ARCHER_STARTER",
                    "Human Male Archer Starter",
                    "Hu_M",
                    "NRanger",
                    "Gn",
                    usePlainPants: true),
            });

            EquipmentAppearanceCatalog equipmentAppearanceCatalog =
                LoadOrCreate<EquipmentAppearanceCatalog>(EquipmentAppearanceCatalogPath);
            List<EquipmentAppearanceCatalog.Entry> equipmentAppearanceEntries = BuildEquipmentAppearanceEntries();
            ValidateEquipmentAppearanceEntries(equipmentAppearanceEntries);
            equipmentAppearanceCatalog.SetEntriesForEditor(equipmentAppearanceEntries);
            List<EquipmentAppearanceCatalog.ArmorSetVisualEntry> armorSetVisualEntries =
                BuildCompleteArmorSetVisualEntries();
            ValidateArmorSetVisualEntries(armorSetVisualEntries);
            equipmentAppearanceCatalog.SetArmorSetsForEditor(armorSetVisualEntries);
            List<EquipmentAppearanceCatalog.WeaponVisualEntry> weaponVisualEntries = BuildWeaponVisualEntries();
            ValidateWeaponVisualEntries(weaponVisualEntries);
            equipmentAppearanceCatalog.SetWeaponVisualsForEditor(weaponVisualEntries);

            EditorUtility.SetDirty(baseCatalog);
            EditorUtility.SetDirty(partCatalog);
            EditorUtility.SetDirty(outfitCatalog);
            EditorUtility.SetDirty(equipmentAppearanceCatalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        [MenuItem("Arena/Appearance/Validate Default Catalog Source Assets")]
        public static void ValidateDefaultCatalogSourceAssetsFromMenu()
        {
            ValidateSupportedStylizedCharacterImport();
            ValidateEquipmentAppearanceEntries(BuildEquipmentAppearanceEntries());
            ValidateArmorSetVisualEntries(BuildCompleteArmorSetVisualEntries());
            EditorUtility.DisplayDialog(
                "Character Appearance Sources Valid",
                "Default character appearance source assets are valid.",
                "OK");
        }

        private static AvatarPartCatalog.Entry Part(
            string partId,
            AvatarPartSlot slot,
            ItemTypeEnum expectedItemType,
            string path)
        {
            return new AvatarPartCatalog.Entry
            {
                partId = partId,
                raceId = CharacterAppearanceIds.RaceHuman,
                sexId = CharacterAppearanceIds.SexMale,
                slot = slot,
                expectedItemType = expectedItemType,
                enabled = true,
                item = LoadRequiredItem(path, expectedItemType),
            };
        }

        private static OutfitCatalog.Entry Outfit(
            string outfitId,
            string displayName,
            string prefix,
            string family,
            string color,
            bool usePlainPants = false)
        {
            string pantsPath = usePlainPants
                ? $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Pants/{prefix}_Pants.prefab"
                : $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Pants/{prefix}_Pants_{family}_{color}.prefab";

            return new OutfitCatalog.Entry
            {
                outfitId = outfitId,
                displayName = displayName,
                enabled = true,
                items = new List<OutfitCatalog.OutfitItem>
                {
                    OutfitItem(ItemTypeEnum.ChestSkin, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/ChestSkin/ChestSkin_{family}_U_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.GlovesSkin, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/GlovesSkin/GlovesSkin_{family}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.PantsSkin, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/PantsSkin/Pants_{family}_U_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Helmet, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Helmet/{prefix}_Helm_{family}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Chest, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Chest/{prefix}_Chest_{family}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Cape, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Cape/{prefix}_Cape_{family}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Shoulders, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Shoulder/{prefix}_Shoulders_{family}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Gloves, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Gloves/{prefix}_Gloves_{family}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Belt, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Belt/{prefix}_Belt_{family}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Pants, pantsPath),
                    OutfitItem(ItemTypeEnum.Boots, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Boots/{prefix}_Boots_{family}_{color}.prefab"),
                },
            };
        }

        private static OutfitCatalog.Entry PeasantOutfit(
            string outfitId,
            string displayName,
            string color,
            string chestVariant)
        {
            const string prefix = "Hu_M";
            const string family = "Peasant";
            var items = new List<OutfitCatalog.OutfitItem>
            {
                OutfitItem(ItemTypeEnum.GlovesSkin, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/GlovesSkin/GlovesSkin_{family}_{color}.prefab"),
                OutfitItem(ItemTypeEnum.PantsSkin, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/PantsSkin/Pants_{family}_U_{color}.prefab"),
                OutfitItem(ItemTypeEnum.Chest, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Chest/{prefix}_Chest_{family}_{chestVariant}_{color}.prefab"),
                OutfitItem(ItemTypeEnum.Belt, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Belt/{prefix}_Belt_{family}_{color}.prefab"),
                OutfitItem(ItemTypeEnum.Boots, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Boots/{prefix}_Boots_{family}_{color}.prefab"),
            };
            if (IncludePeasantStarterHatAndCape)
            {
                items.Insert(2, OutfitItem(ItemTypeEnum.Helmet, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Helmet/{prefix}_Helm_{family}_{color}.prefab"));
                items.Insert(4, OutfitItem(ItemTypeEnum.Cape, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Cape/{prefix}_Cape_{family}_{color}.prefab"));
            }

            return new OutfitCatalog.Entry
            {
                outfitId = outfitId,
                displayName = displayName,
                enabled = true,
                items = items,
            };
        }

        private static OutfitCatalog.OutfitItem OutfitItem(ItemTypeEnum expectedItemType, string path)
        {
            return new OutfitCatalog.OutfitItem
            {
                expectedItemType = expectedItemType,
                item = LoadRequiredItem(path, expectedItemType),
            };
        }

        private static EquipmentAppearanceCatalog.Entry EquipmentVisual(
            string itemDefId,
            string equipSlot,
            params EquipmentAppearanceCatalog.EquipmentItem[] items)
        {
            return new EquipmentAppearanceCatalog.Entry
            {
                itemDefId = itemDefId,
                equipSlot = equipSlot,
                raceId = CharacterAppearanceIds.RaceHuman,
                sexId = CharacterAppearanceIds.SexMale,
                enabled = true,
                items = new List<EquipmentAppearanceCatalog.EquipmentItem>(items),
            };
        }

        private static List<EquipmentAppearanceCatalog.Entry> BuildEquipmentAppearanceEntries()
        {
            var entries = new List<EquipmentAppearanceCatalog.Entry>();
            var visualSets = new[]
            {
                new EquipmentVisualSetSpec(
                    "NWarrior",
                    "Bl",
                    "IRON_HELM",
                    "IRON_SHOULDERS",
                    "TRAVELER_CAPE",
                    "IRON_CHESTPLATE",
                    "IRON_LEGGINGS",
                    "IRON_BOOTS",
                    "IRON_GLOVES",
                    usePlainPantsMesh: false),
                new EquipmentVisualSetSpec(
                    "NWarrior",
                    "Gn",
                    "GILDED_HELM",
                    "GILDED_SHOULDERS",
                    "GILDED_CAPE",
                    "GILDED_CHESTPLATE",
                    "GILDED_LEGGINGS",
                    "GILDED_BOOTS",
                    "GILDED_GLOVES",
                    usePlainPantsMesh: false),
                new EquipmentVisualSetSpec(
                    "NRanger",
                    "Gn",
                    "LEATHER_HELM",
                    "LEATHER_SHOULDERS",
                    "LEATHER_CAPE",
                    "LEATHER_CHESTPIECE",
                    "LEATHER_LEGGINGS",
                    "LEATHER_BOOTS",
                    "LEATHER_GLOVES",
                    usePlainPantsMesh: true),
            };

            for (int i = 0; i < visualSets.Length; i++)
                AddHumanMaleEquipmentVisualSet(entries, visualSets[i]);

            AddHumanMaleApprenticeEquipmentVisualSet(entries);
            AddHumanMalePeasantEquipmentVisualSet(entries);
            return entries;
        }

        private static List<EquipmentAppearanceCatalog.ArmorSetVisualEntry> BuildCompleteArmorSetVisualEntries()
        {
            var entries = new List<EquipmentAppearanceCatalog.ArmorSetVisualEntry>(CompleteArmorVisualSets.Length);
            const string prefix = "Hu_M";
            for (int i = 0; i < CompleteArmorVisualSets.Length; i++)
            {
                CompleteArmorVisualSetSpec spec = CompleteArmorVisualSets[i];
                string chestSkinPath = EquipmentPath(
                    "ChestSkin",
                    $"ChestSkin_{spec.Family}_U_{spec.Color}.prefab");
                var chestItems = new List<EquipmentAppearanceCatalog.EquipmentItem>();
                if (File.Exists(chestSkinPath))
                    chestItems.Add(EquipmentItem(ItemTypeEnum.ChestSkin, chestSkinPath));
                chestItems.Add(EquipmentItem(
                    ItemTypeEnum.Chest,
                    EquipmentPath("Chest", $"{prefix}_Chest_{spec.Family}_{spec.Color}.prefab")));

                string pantsSkinPath = FirstExistingEquipmentPath(
                    "PantsSkin",
                    $"Pants_{spec.Family}_U_{spec.Color}.prefab",
                    $"Pants_{spec.Family}_M_{spec.Color}.prefab",
                    $"Pants_{spec.Family}_{spec.Color}.prefab");
                string authoredPantsPath = EquipmentPath(
                    "Pants",
                    $"{prefix}_Pants_{spec.Family}_{spec.Color}.prefab");
                string pantsPath = File.Exists(authoredPantsPath)
                    ? authoredPantsPath
                    : EquipmentPath("Pants", $"{prefix}_Pants.prefab");

                entries.Add(new EquipmentAppearanceCatalog.ArmorSetVisualEntry
                {
                    armorSetId = spec.ArmorSetId,
                    raceId = CharacterAppearanceIds.RaceHuman,
                    sexId = CharacterAppearanceIds.SexMale,
                    enabled = true,
                    slots = new List<EquipmentAppearanceCatalog.ArmorSetSlotVisual>
                    {
                        ArmorSetSlot("HEAD", EquipmentItem(ItemTypeEnum.Helmet, EquipmentPath("Helmet", $"{prefix}_Helm_{spec.Family}_{spec.Color}.prefab"))),
                        ArmorSetSlot("SHOULDER", EquipmentItem(ItemTypeEnum.Shoulders, EquipmentPath("Shoulder", $"{prefix}_Shoulders_{spec.Family}_{spec.Color}.prefab"))),
                        ArmorSetSlot("CAPE", EquipmentItem(ItemTypeEnum.Cape, EquipmentPath("Cape", $"{prefix}_Cape_{spec.Family}_{spec.Color}.prefab"))),
                        new() { equipSlot = "CHEST", items = chestItems },
                        ArmorSetSlot(
                            "LEGS",
                            EquipmentItem(ItemTypeEnum.PantsSkin, pantsSkinPath),
                            EquipmentItem(ItemTypeEnum.Pants, pantsPath)),
                        ArmorSetSlot("BOOTS", EquipmentItem(ItemTypeEnum.Boots, EquipmentPath("Boots", $"{prefix}_Boots_{spec.Family}_{spec.Color}.prefab"))),
                        ArmorSetSlot(
                            "GLOVES",
                            EquipmentItem(ItemTypeEnum.GlovesSkin, EquipmentPath("GlovesSkin", $"GlovesSkin_{spec.Family}_{spec.Color}.prefab")),
                            EquipmentItem(ItemTypeEnum.Gloves, EquipmentPath("Gloves", $"{prefix}_Gloves_{spec.Family}_{spec.Color}.prefab"))),
                    },
                });
            }

            return entries;
        }

        private static EquipmentAppearanceCatalog.ArmorSetSlotVisual ArmorSetSlot(
            string equipSlot,
            params EquipmentAppearanceCatalog.EquipmentItem[] items)
        {
            return new EquipmentAppearanceCatalog.ArmorSetSlotVisual
            {
                equipSlot = equipSlot,
                items = new List<EquipmentAppearanceCatalog.EquipmentItem>(items),
            };
        }

        private static string FirstExistingEquipmentPath(string folder, params string[] fileNames)
        {
            for (int i = 0; i < fileNames.Length; i++)
            {
                string path = EquipmentPath(folder, fileNames[i]);
                if (File.Exists(path))
                    return path;
            }

            throw new InvalidOperationException(
                $"No supported {folder} asset exists for candidates: {string.Join(", ", fileNames)}");
        }

        private static List<EquipmentAppearanceCatalog.WeaponVisualEntry> BuildWeaponVisualEntries()
        {
            var entries = new List<EquipmentAppearanceCatalog.WeaponVisualEntry>
            {
                WeaponVisual("TRAINING_TWO_HAND_SWORD", "greatsword", "Assets/Arena/Resources/CombatAnimationSets/GreatSwordPackAuthored.prefab"),
                NHanceWeaponVisual("NEWBIE_TWO_HAND_SWORD_01", "greatsword", WeaponPath("Sword", "Sword_2H_Newbie_01_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_TWO_HAND_SWORD_02", "greatsword", WeaponPath("Sword", "Sword_2H_Newbie_02_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_TWO_HAND_AXE_01", "greatsword", WeaponPath("Axe", "Axe_2HL_Newbie_01_Cl.prefab")),

                WeaponVisual("TRAINING_ONE_HAND_SWORD", "sword", "Assets/Arena/Resources/CombatAnimationSets/SwordPackAuthored.prefab"),
                NHanceWeaponVisual("NEWBIE_ONE_HAND_SWORD_01", "sword", WeaponPath("Sword", "Sword_1H_Newbie_01_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_ONE_HAND_SWORD_02", "sword", WeaponPath("Sword", "Sword_1H_Newbie_02_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_ONE_HAND_AXE_02", "sword", WeaponPath("Axe", "Axe_1H_Newbie_02_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_ONE_HAND_AXE_03", "sword", WeaponPath("Axe", "Axe_1H_Newbie_03_Cl.prefab")),

                WeaponVisual("TRAINING_DAGGER_PAIR", "dagger_main", "Assets/Arena/Resources/CombatAnimationSets/DaggerMainPackAuthored.prefab"),
                WeaponVisual("TRAINING_DAGGER_PAIR", "dagger_off", "Assets/Arena/Resources/CombatAnimationSets/DaggerOffPackAuthored.prefab"),
                NHanceWeaponVisual("NEWBIE_DAGGER_PAIR_01", "dagger_main", WeaponPath("Dagger", "Dagger_1H_Newbie_01_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_DAGGER_PAIR_01", "dagger_off", WeaponPath("Dagger", "Dagger_1H_Newbie_01_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_DAGGER_PAIR_02", "dagger_main", WeaponPath("Dagger", "Dagger_1H_Newbie_02_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_DAGGER_PAIR_02", "dagger_off", WeaponPath("Dagger", "Dagger_1H_Newbie_02_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_DAGGER_PAIR_03", "dagger_main", WeaponPath("Dagger", "Dagger_1H_Newbie_03_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_DAGGER_PAIR_03", "dagger_off", WeaponPath("Dagger", "Dagger_1H_Newbie_03_Cl.prefab")),

                WeaponVisual("TRAINING_SHIELD", "shield", "Assets/Arena/Resources/CombatAnimationSets/ShieldPackAuthored.prefab"),
                NHanceWeaponVisual("NEWBIE_SHIELD_01", "shield", WeaponPath("Shield", "Shield_Newbie_01_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_SHIELD_02", "shield", WeaponPath("Shield", "Shield_Newbie_02_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_SHIELD_03", "shield", WeaponPath("Shield", "Shield_Newbie_03_Cl.prefab")),
                WeaponVisual("TRAINING_SWORD_AND_SHIELD", "sword", "Assets/Arena/Resources/CombatAnimationSets/SwordPackAuthored.prefab"),
                WeaponVisual("TRAINING_SWORD_AND_SHIELD", "shield", "Assets/Arena/Resources/CombatAnimationSets/ShieldPackAuthored.prefab"),

                WeaponVisual("TRAINING_BOW", "bow_drawn", "Assets/Arena/Resources/CombatAnimationSets/ArcherBowDrawnPackAuthored.prefab"),
                WeaponVisual("TRAINING_BOW", "bow_stowed", "Assets/Arena/Resources/CombatAnimationSets/ArcherBowStowedPackAuthored.prefab"),
                WeaponVisual("TRAINING_BOW", "quiver", "Assets/Arena/Resources/CombatAnimationSets/ArcherQuiverPackAuthored.prefab"),
                NHanceWeaponVisual("NEWBIE_BOW_01", "bow_drawn", WeaponPath("Bow", "Bow_Newbie_01_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_BOW_01", "bow_stowed", WeaponPath("Bow", "Bow_Newbie_01_Cl.prefab")),
                WeaponVisual("NEWBIE_BOW_01", "quiver", "Assets/Arena/Resources/CombatAnimationSets/ArcherQuiverPackAuthored.prefab"),
                NHanceWeaponVisual("NEWBIE_BOW_02", "bow_drawn", WeaponPath("Bow", "Bow_Newbie_02_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_BOW_02", "bow_stowed", WeaponPath("Bow", "Bow_Newbie_02_Cl.prefab")),
                WeaponVisual("NEWBIE_BOW_02", "quiver", "Assets/Arena/Resources/CombatAnimationSets/ArcherQuiverPackAuthored.prefab"),
                NHanceWeaponVisual("NEWBIE_BOW_03", "bow_drawn", WeaponPath("Bow", "Bow_Newbie_03_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_BOW_03", "bow_stowed", WeaponPath("Bow", "Bow_Newbie_03_Cl.prefab")),
                WeaponVisual("NEWBIE_BOW_03", "quiver", "Assets/Arena/Resources/CombatAnimationSets/ArcherQuiverPackAuthored.prefab"),

                WeaponVisual("NEWBIE_STAFF_01", "staff", "Assets/Arena/Resources/CombatAnimationSets/StaffPackAuthored.prefab"),
                NHanceWeaponVisual("NEWBIE_STAFF_02", "staff", WeaponPath("Staff", "Staff_Newbie_02_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_STAFF_03", "staff", WeaponPath("Staff", "Staff_Newbie_03_Cl.prefab")),
                NHanceWeaponVisual("NEWBIE_STAFF_04", "staff", WeaponPath("Staff", "Staff_Newbie_04_Cl.prefab")),
            };

            WeaponAppearanceCatalogFile catalog = JsonUtility.FromJson<WeaponAppearanceCatalogFile>(
                File.ReadAllText(WeaponAppearanceCatalogPath));
            if (catalog == null || catalog.schema_version != 1 || catalog.families == null)
                throw new InvalidOperationException($"Invalid weapon appearance catalog: {WeaponAppearanceCatalogPath}");

            foreach (WeaponFamilyAuthoring family in catalog.families)
            {
                if (family == null || family.variants == null)
                    throw new InvalidOperationException("Weapon appearance catalog contains an invalid family.");
                foreach (WeaponVariantAuthoring variant in family.variants)
                    AddWeaponVariantVisuals(entries, family, variant);
            }

            return entries;
        }

        private static void AddWeaponVariantVisuals(
            List<EquipmentAppearanceCatalog.WeaponVisualEntry> entries,
            WeaponFamilyAuthoring family,
            WeaponVariantAuthoring variant)
        {
            WeaponAppearancePlacementProfile placementProfile = ParseWeaponPlacementProfile(
                family.placement_profile_id,
                family.item_def_id);
            switch (family.weapon_kind)
            {
                case "DAGGER_PAIR":
                    entries.Add(WeaponVisual(family.item_def_id, variant.color_id, "dagger_main", variant.prefab_path, placementProfile));
                    entries.Add(WeaponVisual(family.item_def_id, variant.color_id, "dagger_off", variant.off_hand_prefab_path, placementProfile));
                    break;
                case "TWO_HAND_SWORD":
                case "TWO_HAND_AXE":
                case "TWO_HAND_HAMMER":
                case "POLEARM":
                    entries.Add(WeaponVisual(family.item_def_id, variant.color_id, "greatsword", variant.prefab_path, placementProfile));
                    break;
                case "ONE_HAND_SWORD":
                case "ONE_HAND_AXE":
                case "ONE_HAND_HAMMER":
                case "ONE_HAND_FIST":
                    entries.Add(WeaponVisual(family.item_def_id, variant.color_id, "sword", variant.prefab_path, placementProfile));
                    break;
                case "SHIELD":
                    entries.Add(WeaponVisual(family.item_def_id, variant.color_id, "shield", variant.prefab_path, placementProfile));
                    break;
                case "BOW":
                    entries.Add(WeaponVisual(family.item_def_id, variant.color_id, "bow_drawn", variant.prefab_path, placementProfile));
                    entries.Add(WeaponVisual(
                        family.item_def_id,
                        variant.color_id,
                        "bow_stowed",
                        string.IsNullOrWhiteSpace(variant.stowed_prefab_path)
                            ? variant.prefab_path
                            : variant.stowed_prefab_path,
                        placementProfile));
                    entries.Add(WeaponVisual(
                        family.item_def_id,
                        variant.color_id,
                        "quiver",
                        string.IsNullOrWhiteSpace(variant.quiver_prefab_path)
                            ? "Assets/Arena/Resources/CombatAnimationSets/ArcherQuiverPackAuthored.prefab"
                            : variant.quiver_prefab_path,
                        ParseWeaponPlacementProfile(
                            variant.quiver_placement_profile_id,
                            $"{family.item_def_id}/{variant.color_id}/quiver")));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported weapon kind '{family.weapon_kind}' in {WeaponAppearanceCatalogPath}.");
            }
        }

        private static EquipmentAppearanceCatalog.WeaponVisualEntry WeaponVisual(
            string itemDefId,
            string visualRoleId,
            string path)
            => WeaponVisual(itemDefId, string.Empty, visualRoleId, path);

        private static EquipmentAppearanceCatalog.WeaponVisualEntry WeaponVisual(
            string itemDefId,
            string colorId,
            string visualRoleId,
            string path,
            WeaponAppearancePlacementProfile placementProfile = WeaponAppearancePlacementProfile.LegacyAnimationBinding)
        {
            return new EquipmentAppearanceCatalog.WeaponVisualEntry
            {
                itemDefId = itemDefId,
                colorId = colorId,
                visualRoleId = visualRoleId,
                raceId = CharacterAppearanceIds.RaceHuman,
                sexId = CharacterAppearanceIds.SexMale,
                enabled = true,
                prefab = LoadRequired<GameObject>(path),
                placementProfile = placementProfile,
            };
        }

        private static EquipmentAppearanceCatalog.WeaponVisualEntry NHanceWeaponVisual(
            string itemDefId,
            string visualRoleId,
            string path)
            => WeaponVisual(
                itemDefId,
                string.Empty,
                visualRoleId,
                path,
                WeaponAppearancePlacementProfile.NHanceNative);

        private static WeaponAppearancePlacementProfile ParseWeaponPlacementProfile(string profileId, string context)
        {
            return profileId switch
            {
                "LEGACY_ANIMATION_BINDING" => WeaponAppearancePlacementProfile.LegacyAnimationBinding,
                "NHANCE_NATIVE" => WeaponAppearancePlacementProfile.NHanceNative,
                _ => throw new InvalidOperationException(
                    $"Weapon appearance '{context}' has unsupported placement profile '{profileId}'."),
            };
        }

        private static void AddHumanMaleEquipmentVisualSet(
            List<EquipmentAppearanceCatalog.Entry> entries,
            EquipmentVisualSetSpec spec)
        {
            const string prefix = "Hu_M";
            entries.Add(EquipmentVisual(
                spec.HeadItemDefId,
                "HEAD",
                EquipmentItem(ItemTypeEnum.Helmet, EquipmentPath("Helmet", $"{prefix}_Helm_{spec.Family}_{spec.Color}.prefab"))));
            entries.Add(EquipmentVisual(
                spec.ShoulderItemDefId,
                "SHOULDER",
                EquipmentItem(ItemTypeEnum.Shoulders, EquipmentPath("Shoulder", $"{prefix}_Shoulders_{spec.Family}_{spec.Color}.prefab"))));
            entries.Add(EquipmentVisual(
                spec.CapeItemDefId,
                "CAPE",
                EquipmentItem(ItemTypeEnum.Cape, EquipmentPath("Cape", $"{prefix}_Cape_{spec.Family}_{spec.Color}.prefab"))));
            entries.Add(EquipmentVisual(
                spec.ChestItemDefId,
                "CHEST",
                EquipmentItem(ItemTypeEnum.ChestSkin, EquipmentPath("ChestSkin", $"ChestSkin_{spec.Family}_U_{spec.Color}.prefab")),
                EquipmentItem(ItemTypeEnum.Chest, EquipmentPath("Chest", $"{prefix}_Chest_{spec.Family}_{spec.Color}.prefab"))));

            string pantsMeshPath = spec.UsePlainPantsMesh
                ? EquipmentPath("Pants", $"{prefix}_Pants.prefab")
                : EquipmentPath("Pants", $"{prefix}_Pants_{spec.Family}_{spec.Color}.prefab");
            entries.Add(EquipmentVisual(
                spec.LegsItemDefId,
                "LEGS",
                EquipmentItem(ItemTypeEnum.PantsSkin, EquipmentPath("PantsSkin", $"Pants_{spec.Family}_U_{spec.Color}.prefab")),
                EquipmentItem(ItemTypeEnum.Pants, pantsMeshPath)));
            entries.Add(EquipmentVisual(
                spec.BootsItemDefId,
                "BOOTS",
                EquipmentItem(ItemTypeEnum.Boots, EquipmentPath("Boots", $"{prefix}_Boots_{spec.Family}_{spec.Color}.prefab"))));
            entries.Add(EquipmentVisual(
                spec.GlovesItemDefId,
                "GLOVES",
                EquipmentItem(ItemTypeEnum.GlovesSkin, EquipmentPath("GlovesSkin", $"GlovesSkin_{spec.Family}_{spec.Color}.prefab")),
                EquipmentItem(ItemTypeEnum.Gloves, EquipmentPath("Gloves", $"{prefix}_Gloves_{spec.Family}_{spec.Color}.prefab"))));
        }

        private static void AddHumanMalePeasantEquipmentVisualSet(
            List<EquipmentAppearanceCatalog.Entry> entries)
        {
            const string prefix = "Hu_M";
            const string family = "Peasant";
            const string color = "Br";
            const string chestVariant = "01";

            entries.Add(EquipmentVisual(
                "PEASANT_TUNIC",
                "CHEST",
                EquipmentItem(ItemTypeEnum.Chest, EquipmentPath("Chest", $"{prefix}_Chest_{family}_{chestVariant}_{color}.prefab"))));
            entries.Add(EquipmentVisual(
                "PEASANT_TROUSERS",
                "LEGS",
                EquipmentItem(ItemTypeEnum.PantsSkin, EquipmentPath("PantsSkin", $"Pants_{family}_U_{color}.prefab"))));
            entries.Add(EquipmentVisual(
                "PEASANT_BOOTS",
                "BOOTS",
                EquipmentItem(ItemTypeEnum.Boots, EquipmentPath("Boots", $"{prefix}_Boots_{family}_{color}.prefab"))));
            entries.Add(EquipmentVisual(
                "PEASANT_GLOVES",
                "GLOVES",
                EquipmentItem(ItemTypeEnum.GlovesSkin, EquipmentPath("GlovesSkin", $"GlovesSkin_{family}_{color}.prefab"))));
        }

        private static void AddHumanMaleApprenticeEquipmentVisualSet(
            List<EquipmentAppearanceCatalog.Entry> entries)
        {
            const string prefix = "Hu_M";
            const string family = "Wizard";
            const string color = "Bl";

            entries.Add(EquipmentVisual(
                "APPRENTICE_HOOD",
                "HEAD",
                EquipmentItem(ItemTypeEnum.Helmet, EquipmentPath("Helmet", $"{prefix}_Helm_{family}_{color}.prefab"))));
            entries.Add(EquipmentVisual(
                "APPRENTICE_MANTLE",
                "SHOULDER",
                EquipmentItem(ItemTypeEnum.Shoulders, EquipmentPath("Shoulder", $"{prefix}_Shoulders_{family}_{color}.prefab"))));
            entries.Add(EquipmentVisual(
                "APPRENTICE_CLOAK",
                "CAPE",
                EquipmentItem(ItemTypeEnum.Cape, EquipmentPath("Cape", $"{prefix}_Cape_{family}_{color}.prefab"))));
            entries.Add(EquipmentVisual(
                "APPRENTICE_ROBE",
                "CHEST",
                EquipmentItem(ItemTypeEnum.Chest, EquipmentPath("Chest", $"{prefix}_Chest_{family}_{color}.prefab"))));
            entries.Add(EquipmentVisual(
                "APPRENTICE_TROUSERS",
                "LEGS",
                EquipmentItem(ItemTypeEnum.PantsSkin, EquipmentPath("PantsSkin", $"Pants_{family}_M_{color}.prefab")),
                EquipmentItem(ItemTypeEnum.Pants, EquipmentPath("Pants", $"{prefix}_Pants_{family}_{color}.prefab"))));
            entries.Add(EquipmentVisual(
                "APPRENTICE_BOOTS",
                "BOOTS",
                EquipmentItem(ItemTypeEnum.Boots, EquipmentPath("Boots", $"{prefix}_Boots_{family}_{color}.prefab"))));
            entries.Add(EquipmentVisual(
                "APPRENTICE_GLOVES",
                "GLOVES",
                EquipmentItem(ItemTypeEnum.GlovesSkin, EquipmentPath("GlovesSkin", $"GlovesSkin_{family}_{color}.prefab")),
                EquipmentItem(ItemTypeEnum.Gloves, EquipmentPath("Gloves", $"{prefix}_Gloves_{family}_{color}.prefab"))));
        }

        private static string EquipmentPath(string folder, string fileName)
        {
            return $"{StylizedCharacterRoot}/Prefabs/Item/Equipment/{folder}/{fileName}";
        }

        private static string WeaponPath(string folder, string fileName)
        {
            return $"{StylizedCharacterRoot}/Prefabs/Item/Weapon/{folder}/{fileName}";
        }

        private readonly struct EquipmentVisualSetSpec
        {
            public EquipmentVisualSetSpec(
                string family,
                string color,
                string headItemDefId,
                string shoulderItemDefId,
                string capeItemDefId,
                string chestItemDefId,
                string legsItemDefId,
                string bootsItemDefId,
                string glovesItemDefId,
                bool usePlainPantsMesh)
            {
                Family = family;
                Color = color;
                HeadItemDefId = headItemDefId;
                ShoulderItemDefId = shoulderItemDefId;
                CapeItemDefId = capeItemDefId;
                ChestItemDefId = chestItemDefId;
                LegsItemDefId = legsItemDefId;
                BootsItemDefId = bootsItemDefId;
                GlovesItemDefId = glovesItemDefId;
                UsePlainPantsMesh = usePlainPantsMesh;
            }

            public string Family { get; }
            public string Color { get; }
            public string HeadItemDefId { get; }
            public string ShoulderItemDefId { get; }
            public string CapeItemDefId { get; }
            public string ChestItemDefId { get; }
            public string LegsItemDefId { get; }
            public string BootsItemDefId { get; }
            public string GlovesItemDefId { get; }
            public bool UsePlainPantsMesh { get; }
        }

        private readonly struct CompleteArmorVisualSetSpec
        {
            public CompleteArmorVisualSetSpec(string armorSetId, string family, string color)
            {
                ArmorSetId = armorSetId;
                Family = family;
                Color = color;
            }

            public string ArmorSetId { get; }
            public string Family { get; }
            public string Color { get; }
        }

        private static EquipmentAppearanceCatalog.EquipmentItem EquipmentItem(
            ItemTypeEnum expectedItemType,
            string path)
        {
            return new EquipmentAppearanceCatalog.EquipmentItem
            {
                expectedItemType = expectedItemType,
                item = LoadRequiredItem(path, expectedItemType),
            };
        }

        private static void ValidateSupportedStylizedCharacterImport()
        {
            if (!Directory.Exists(StylizedCharacterRoot))
                throw new InvalidOperationException($"Required source folder was not found: {StylizedCharacterRoot}");

            var unsupported = new List<string>();
            foreach (string path in Directory.EnumerateFiles(StylizedCharacterRoot, "*", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(path);
                if (!IsUnsupportedRaceAssetFileName(fileName))
                    continue;

                unsupported.Add(path.Replace('\\', '/'));
                if (unsupported.Count >= 25)
                    break;
            }

            if (unsupported.Count == 0)
                return;

            throw new InvalidOperationException(
                "Unsupported dwarf/orc StylizedCharacter assets are present. Remove the following files before rebuilding catalogs:\n"
                + string.Join("\n", unsupported));
        }

        private static bool IsUnsupportedRaceAssetFileName(string fileName)
        {
            return fileName.StartsWith("Dw_", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("Or_", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("_Dw_", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("_Or_", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateEquipmentAppearanceEntries(IReadOnlyList<EquipmentAppearanceCatalog.Entry> entries)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                EquipmentAppearanceCatalog.Entry entry = entries[i];
                if (entry == null)
                    throw new InvalidOperationException($"Equipment appearance entry {i} is null.");
                if (string.IsNullOrWhiteSpace(entry.itemDefId))
                    throw new InvalidOperationException($"Equipment appearance entry {i} has no item definition id.");
                if (string.IsNullOrWhiteSpace(entry.equipSlot))
                    throw new InvalidOperationException($"Equipment appearance entry '{entry.itemDefId}' has no equip slot.");
                if (entry.items == null || entry.items.Count == 0)
                    throw new InvalidOperationException($"Equipment appearance entry '{entry.itemDefId}' has no visual items.");

                string key = $"{entry.itemDefId}|{entry.equipSlot}|{entry.raceId}|{entry.sexId}";
                if (!keys.Add(key))
                    throw new InvalidOperationException($"Duplicate equipment appearance entry: {key}");

                for (int itemIndex = 0; itemIndex < entry.items.Count; itemIndex++)
                {
                    EquipmentAppearanceCatalog.EquipmentItem item = entry.items[itemIndex];
                    if (item == null || item.item == null)
                        throw new InvalidOperationException(
                            $"Equipment appearance entry '{entry.itemDefId}' has a missing visual item at index {itemIndex}.");
                    if (item.item.Type != item.expectedItemType)
                        throw new InvalidOperationException(
                            $"Equipment appearance entry '{entry.itemDefId}' expected item type {item.expectedItemType} but found {item.item.Type} on '{item.item.name}'.");
                }
            }
        }

        private static void ValidateArmorSetVisualEntries(
            IReadOnlyList<EquipmentAppearanceCatalog.ArmorSetVisualEntry> entries)
        {
            var setIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int setIndex = 0; setIndex < entries.Count; setIndex++)
            {
                EquipmentAppearanceCatalog.ArmorSetVisualEntry entry = entries[setIndex];
                if (entry == null || string.IsNullOrWhiteSpace(entry.armorSetId))
                    throw new InvalidOperationException($"Armor-set visual entry {setIndex} has no set id.");
                if (!setIds.Add(entry.armorSetId))
                    throw new InvalidOperationException($"Duplicate armor-set visual entry: {entry.armorSetId}");
                if (entry.slots == null || entry.slots.Count != 7)
                    throw new InvalidOperationException(
                        $"Armor-set visual entry '{entry.armorSetId}' must define all seven armor slots.");

                var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int slotIndex = 0; slotIndex < entry.slots.Count; slotIndex++)
                {
                    EquipmentAppearanceCatalog.ArmorSetSlotVisual slot = entry.slots[slotIndex];
                    if (slot == null || string.IsNullOrWhiteSpace(slot.equipSlot) || !slots.Add(slot.equipSlot))
                        throw new InvalidOperationException(
                            $"Armor-set visual entry '{entry.armorSetId}' has a missing or duplicate slot.");
                    if (slot.items == null || slot.items.Count == 0)
                        throw new InvalidOperationException(
                            $"Armor-set visual entry '{entry.armorSetId}' slot '{slot.equipSlot}' has no visual items.");

                    for (int itemIndex = 0; itemIndex < slot.items.Count; itemIndex++)
                    {
                        EquipmentAppearanceCatalog.EquipmentItem item = slot.items[itemIndex];
                        if (item == null || item.item == null || item.item.Type != item.expectedItemType)
                            throw new InvalidOperationException(
                                $"Armor-set visual entry '{entry.armorSetId}' slot '{slot.equipSlot}' has an invalid visual item.");
                    }
                }
            }
        }

        private static void ValidateWeaponVisualEntries(IReadOnlyList<EquipmentAppearanceCatalog.WeaponVisualEntry> entries)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                EquipmentAppearanceCatalog.WeaponVisualEntry entry = entries[i];
                if (entry == null)
                    throw new InvalidOperationException($"Weapon visual entry {i} is null.");
                if (string.IsNullOrWhiteSpace(entry.itemDefId))
                    throw new InvalidOperationException($"Weapon visual entry {i} has no item definition id.");
                if (string.IsNullOrWhiteSpace(entry.visualRoleId))
                    throw new InvalidOperationException($"Weapon visual entry '{entry.itemDefId}' has no visual role id.");
                if (entry.prefab == null)
                    throw new InvalidOperationException($"Weapon visual entry '{entry.itemDefId}' role '{entry.visualRoleId}' has no prefab.");

                string prefabPath = AssetDatabase.GetAssetPath(entry.prefab);
                bool rawNHanceWeapon = prefabPath.StartsWith(
                    $"{StylizedCharacterRoot}/Prefabs/Item/Weapon/",
                    StringComparison.Ordinal);
                if (rawNHanceWeapon && entry.placementProfile != WeaponAppearancePlacementProfile.NHanceNative)
                {
                    throw new InvalidOperationException(
                        $"Raw N-Hance weapon visual '{entry.itemDefId}/{entry.colorId}/{entry.visualRoleId}' must opt into native placement.");
                }

                if (rawNHanceWeapon
                    && (entry.prefab.transform.localPosition.sqrMagnitude > 0.00000001f
                        || Quaternion.Angle(entry.prefab.transform.localRotation, Quaternion.identity) > 0.001f
                        || (entry.prefab.transform.localScale - Vector3.one).sqrMagnitude > 0.00000001f))
                {
                    throw new InvalidOperationException(
                        $"Raw N-Hance weapon visual '{entry.itemDefId}/{entry.colorId}/{entry.visualRoleId}' " +
                        "does not have an identity root and needs an explicit family placement correction.");
                }

                if (entry.placementProfile == WeaponAppearancePlacementProfile.NHanceNative
                    && (!WeaponAppearancePlacementResolver.TryResolve(
                            entry.placementProfile,
                            entry.visualRoleId,
                            inCombat: true,
                            out _)
                        || !WeaponAppearancePlacementResolver.TryResolve(
                            entry.placementProfile,
                            entry.visualRoleId,
                            inCombat: false,
                            out _)))
                {
                    throw new InvalidOperationException(
                        $"N-Hance weapon visual '{entry.itemDefId}/{entry.colorId}/{entry.visualRoleId}' has no complete placement mapping.");
                }

                string key = $"{entry.itemDefId}|{entry.colorId}|{entry.visualRoleId}|{entry.raceId}|{entry.sexId}";
                if (!keys.Add(key))
                    throw new InvalidOperationException($"Duplicate weapon visual entry: {key}");
            }
        }

        [Serializable]
        private sealed class WeaponAppearanceCatalogFile
        {
            public int schema_version;
            public List<WeaponFamilyAuthoring> families = new();
        }

        [Serializable]
        private sealed class WeaponFamilyAuthoring
        {
            public string item_def_id = string.Empty;
            public string weapon_kind = string.Empty;
            public string placement_profile_id = string.Empty;
            public List<WeaponVariantAuthoring> variants = new();
        }

        [Serializable]
        private sealed class WeaponVariantAuthoring
        {
            public string color_id = string.Empty;
            public string prefab_path = string.Empty;
            public string off_hand_prefab_path = string.Empty;
            public string stowed_prefab_path = string.Empty;
            public string quiver_prefab_path = string.Empty;
            public string quiver_placement_profile_id = string.Empty;
        }

        private static NHItem LoadRequiredItem(string path, ItemTypeEnum expectedItemType)
        {
            GameObject prefab = LoadRequired<GameObject>(path);
            NHItem item = prefab.GetComponent<NHItem>();
            if (item == null)
                throw new InvalidOperationException($"Required avatar item prefab is missing NHItem: {path}");
            if (item.Type != expectedItemType)
                throw new InvalidOperationException($"Avatar item prefab '{path}' expected type {expectedItemType} but found {item.Type}.");
            return item;
        }

        private static T LoadRequired<T>(string path)
            where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException($"Required asset was not found: {path}");
            return asset;
        }

        private static T LoadOrCreate<T>(string path)
            where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null)
                return asset;

            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            int slash = folderPath.LastIndexOf('/');
            if (slash <= 0)
                throw new InvalidOperationException($"Cannot create Unity asset folder '{folderPath}'.");

            string parent = folderPath.Substring(0, slash);
            string child = folderPath.Substring(slash + 1);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, child);
        }
    }
}

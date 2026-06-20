#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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
        private const string ClassOutfitCatalogPath = CatalogFolder + "/ClassOutfitCatalog.asset";
        private const string EquipmentAppearanceCatalogPath = CatalogFolder + "/EquipmentAppearanceCatalog.asset";

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

            ClassOutfitCatalog classOutfitCatalog = LoadOrCreate<ClassOutfitCatalog>(ClassOutfitCatalogPath);
            classOutfitCatalog.SetEntriesForEditor(new List<ClassOutfitCatalog.Entry>
            {
                ClassOutfit("WARRIOR", "HUMAN_MALE_PEASANT_STARTER"),
                ClassOutfit("PALADIN", "HUMAN_MALE_PEASANT_STARTER"),
                ClassOutfit("RANGER", "HUMAN_MALE_PEASANT_STARTER"),
            });

            EquipmentAppearanceCatalog equipmentAppearanceCatalog =
                LoadOrCreate<EquipmentAppearanceCatalog>(EquipmentAppearanceCatalogPath);
            List<EquipmentAppearanceCatalog.Entry> equipmentAppearanceEntries = BuildEquipmentAppearanceEntries();
            ValidateEquipmentAppearanceEntries(equipmentAppearanceEntries);
            equipmentAppearanceCatalog.SetEntriesForEditor(equipmentAppearanceEntries);

            EditorUtility.SetDirty(baseCatalog);
            EditorUtility.SetDirty(partCatalog);
            EditorUtility.SetDirty(outfitCatalog);
            EditorUtility.SetDirty(classOutfitCatalog);
            EditorUtility.SetDirty(equipmentAppearanceCatalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        [MenuItem("Arena/Appearance/Validate Default Catalog Source Assets")]
        public static void ValidateDefaultCatalogSourceAssetsFromMenu()
        {
            ValidateSupportedStylizedCharacterImport();
            ValidateEquipmentAppearanceEntries(BuildEquipmentAppearanceEntries());
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
            return new OutfitCatalog.Entry
            {
                outfitId = outfitId,
                displayName = displayName,
                enabled = true,
                items = new List<OutfitCatalog.OutfitItem>
                {
                    OutfitItem(ItemTypeEnum.GlovesSkin, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/GlovesSkin/GlovesSkin_{family}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.PantsSkin, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/PantsSkin/Pants_{family}_U_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Helmet, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Helmet/{prefix}_Helm_{family}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Chest, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Chest/{prefix}_Chest_{family}_{chestVariant}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Cape, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Cape/{prefix}_Cape_{family}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Belt, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Belt/{prefix}_Belt_{family}_{color}.prefab"),
                    OutfitItem(ItemTypeEnum.Boots, $"Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Boots/{prefix}_Boots_{family}_{color}.prefab"),
                },
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

            return entries;
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

        private static string EquipmentPath(string folder, string fileName)
        {
            return $"{StylizedCharacterRoot}/Prefabs/Item/Equipment/{folder}/{fileName}";
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

        private static ClassOutfitCatalog.Entry ClassOutfit(string classId, string outfitId)
        {
            return new ClassOutfitCatalog.Entry
            {
                classId = classId,
                raceId = CharacterAppearanceIds.RaceHuman,
                sexId = CharacterAppearanceIds.SexMale,
                outfitId = outfitId,
                enabled = true,
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

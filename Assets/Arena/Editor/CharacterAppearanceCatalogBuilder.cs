#nullable enable
using System;
using System.Collections.Generic;
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
        private const string BaseCatalogPath = CatalogFolder + "/AvatarBaseCatalog.asset";
        private const string PartCatalogPath = CatalogFolder + "/AvatarPartCatalog.asset";
        private const string OutfitCatalogPath = CatalogFolder + "/OutfitCatalog.asset";
        private const string ClassOutfitCatalogPath = CatalogFolder + "/ClassOutfitCatalog.asset";

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
                ClassOutfit("WARRIOR", "HUMAN_MALE_WARRIOR_STARTER"),
                ClassOutfit("PALADIN", "HUMAN_MALE_PALADIN_STARTER"),
                ClassOutfit("RANGER", "HUMAN_MALE_ARCHER_STARTER"),
            });

            EditorUtility.SetDirty(baseCatalog);
            EditorUtility.SetDirty(partCatalog);
            EditorUtility.SetDirty(outfitCatalog);
            EditorUtility.SetDirty(classOutfitCatalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
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

        private static OutfitCatalog.OutfitItem OutfitItem(ItemTypeEnum expectedItemType, string path)
        {
            return new OutfitCatalog.OutfitItem
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

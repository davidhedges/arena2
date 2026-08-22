#nullable enable

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class CharacterAppearanceCatalogTests
    {
        private const string BaseCatalogPath = "Assets/Arena/Resources/CharacterAppearance/AvatarBaseCatalog.asset";
        private const string PartCatalogPath = "Assets/Arena/Resources/CharacterAppearance/AvatarPartCatalog.asset";
        private const string OutfitCatalogPath = "Assets/Arena/Resources/CharacterAppearance/OutfitCatalog.asset";
        private const string EquipmentAppearanceCatalogPath = "Assets/Arena/Resources/CharacterAppearance/EquipmentAppearanceCatalog.asset";

        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        [Test]
        public void GeneratedCatalogAssets_ReferenceValidAvatarPrefabsAndItems()
        {
            Type nhAvatarType = RequireType("NHance.Assets.Scripts.NHAvatar");

            object baseCatalog = LoadRequiredAsset(BaseCatalogPath, "Arena.Presentation.Appearance.AvatarBaseCatalog");
            foreach (object entry in Entries(baseCatalog))
            {
                GameObject basePrefab = RequireField<GameObject>(entry, "basePrefab");
                Assert.That(basePrefab.GetComponent(nhAvatarType), Is.Not.Null, $"{basePrefab.name} must include NHAvatar.");
            }

            object partCatalog = LoadRequiredAsset(PartCatalogPath, "Arena.Presentation.Appearance.AvatarPartCatalog");
            foreach (object entry in Entries(partCatalog))
            {
                bool enabled = RequireField<bool>(entry, "enabled");
                if (!enabled)
                    continue;

                Component item = RequireField<Component>(entry, "item");
                object expectedType = RequireField<object>(entry, "expectedItemType");
                Assert.That(GetMemberValue(item, "Type"), Is.EqualTo(expectedType), $"{item.name} has the wrong NHItem type.");
            }

            object outfitCatalog = LoadRequiredAsset(OutfitCatalogPath, "Arena.Presentation.Appearance.OutfitCatalog");
            foreach (object outfit in Entries(outfitCatalog))
            {
                bool enabled = RequireField<bool>(outfit, "enabled");
                if (!enabled)
                    continue;

                IList items = RequireField<IList>(outfit, "items");
                Assert.That(items.Count, Is.GreaterThan(0), "Enabled outfits must include at least one equipment slot.");
                foreach (object slot in items)
                {
                    Component item = RequireField<Component>(slot, "item");
                    object expectedType = RequireField<object>(slot, "expectedItemType");
                    Assert.That(GetMemberValue(item, "Type"), Is.EqualTo(expectedType), $"{item.name} has the wrong outfit item type.");
                }
            }

            Assert.That(Entries(outfitCatalog).Cast<object>().Count(), Is.GreaterThan(0));
        }

        [Test]
        public void EquipmentAppearanceCatalog_ContainsPeasantStarterGearVisuals()
        {
            object equipmentCatalog = LoadRequiredAsset(EquipmentAppearanceCatalogPath, "Arena.Presentation.Appearance.EquipmentAppearanceCatalog");
            object[] entries = Entries(equipmentCatalog).Cast<object>().ToArray();

            AssertEquipmentVisual(entries, "PEASANT_TUNIC", "CHEST");
            AssertEquipmentVisual(entries, "PEASANT_TROUSERS", "LEGS");
            AssertEquipmentVisual(entries, "PEASANT_BOOTS", "BOOTS");
            AssertEquipmentVisual(entries, "PEASANT_GLOVES", "GLOVES");
        }

        [Test]
        public void StaffCombatPrefab_UsesProjectAuthoredUrpMaterial()
        {
            const string materialPath = "Assets/Arena/Resources/CombatAnimationSets/StaffPackAuthored.mat";
            const string prefabPath = "Assets/Arena/Resources/CombatAnimationSets/StaffPackAuthored.prefab";

            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            Assert.That(material, Is.Not.Null);
            Assert.That(material!.shader, Is.Not.Null);
            Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"));
            Assert.That(material.GetTexture("_BaseMap"), Is.Not.Null);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab, Is.Not.Null);
            Renderer[] renderers = prefab!.GetComponentsInChildren<Renderer>(true);
            Assert.That(renderers, Is.Not.Empty);
            foreach (Material rendererMaterial in renderers.SelectMany(renderer => renderer.sharedMaterials))
            {
                Assert.That(rendererMaterial, Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(rendererMaterial), Is.EqualTo(materialPath));
                Assert.That(rendererMaterial!.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"));
            }
        }

        [Test]
        public void EquipmentAppearanceCatalog_ContainsCompleteApprenticeSetVisuals()
        {
            object equipmentCatalog = LoadRequiredAsset(EquipmentAppearanceCatalogPath, "Arena.Presentation.Appearance.EquipmentAppearanceCatalog");
            object[] entries = Entries(equipmentCatalog).Cast<object>().ToArray();

            AssertEquipmentVisual(entries, "APPRENTICE_HOOD", "HEAD");
            AssertEquipmentVisual(entries, "APPRENTICE_MANTLE", "SHOULDER");
            AssertEquipmentVisual(entries, "APPRENTICE_CLOAK", "CAPE");
            AssertEquipmentVisual(entries, "APPRENTICE_ROBE", "CHEST");
            AssertEquipmentVisual(entries, "APPRENTICE_TROUSERS", "LEGS");
            AssertEquipmentVisual(entries, "APPRENTICE_BOOTS", "BOOTS");
            AssertEquipmentVisual(entries, "APPRENTICE_GLOVES", "GLOVES");
        }

        [Test]
        public void WeaponAppearanceCatalog_OptsOnlyRawNHancePrefabsIntoNativePlacement()
        {
            object equipmentCatalog = LoadRequiredAsset(
                EquipmentAppearanceCatalogPath,
                "Arena.Presentation.Appearance.EquipmentAppearanceCatalog");
            object[] visuals = ((IEnumerable)RequireProperty(equipmentCatalog, "WeaponVisuals")
                    .GetValue(equipmentCatalog)!)
                .Cast<object>()
                .ToArray();

            foreach (object visual in visuals)
            {
                GameObject prefab = RequireField<GameObject>(visual, "prefab");
                string path = AssetDatabase.GetAssetPath(prefab);
                bool rawNHanceWeapon = path.StartsWith(
                    "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Weapon/",
                    StringComparison.Ordinal);
                int profile = Convert.ToInt32(RequireField<object>(visual, "placementProfile"));
                Assert.That(
                    profile == 1,
                    Is.EqualTo(rawNHanceWeapon),
                    $"{RequireField<string>(visual, "itemDefId")}/{RequireField<string>(visual, "colorId")}/" +
                    $"{RequireField<string>(visual, "visualRoleId")} has the wrong placement profile for {path}.");
                if (rawNHanceWeapon)
                {
                    Assert.That(prefab.transform.localPosition, Is.EqualTo(Vector3.zero), path);
                    Assert.That(prefab.transform.localRotation, Is.EqualTo(Quaternion.identity), path);
                    Assert.That(prefab.transform.localScale, Is.EqualTo(Vector3.one), path);
                }
            }

            AssertWeaponPlacementProfile(visuals, "TRAINING_ONE_HAND_SWORD", "DEFAULT", "sword", 0);
            AssertWeaponPlacementProfile(visuals, "NEWBIE_ONE_HAND_SWORD_01", "CL", "sword", 1);
            AssertWeaponPlacementProfile(visuals, "NEWBIE_BOW_01", "CL", "bow_drawn", 1);
            AssertWeaponPlacementProfile(visuals, "NEWBIE_BOW_01", "CL", "bow_stowed", 1);
            AssertWeaponPlacementProfile(visuals, "NEWBIE_BOW_01", "CL", "quiver", 0);
            AssertWeaponPlacementProfile(visuals, "NEWBIE_STAFF_01", "DEFAULT", "staff", 0);
        }

        [Test]
        public void NHanceWeaponPlacementProfile_UsesRoleAndStateSpecificNativeMounts()
        {
            Type profileType = RequireType("Arena.Presentation.Appearance.WeaponAppearancePlacementProfile");
            Type resolverType = RequireType("Arena.Presentation.WeaponAppearancePlacementResolver");
            object nativeProfile = Enum.Parse(profileType, "NHanceNative");
            object legacyProfile = Enum.Parse(profileType, "LegacyAnimationBinding");
            MethodInfo tryResolve = RequireMethod(resolverType, "TryResolve");

            AssertResolvedPlacement(tryResolve, nativeProfile, "sword", true, "nhance_weapon_r");
            AssertResolvedPlacement(tryResolve, nativeProfile, "sword", false, "nhance_back_l");
            AssertResolvedPlacement(tryResolve, nativeProfile, "shield", true, "nhance_weapon_shield");
            AssertResolvedPlacement(tryResolve, nativeProfile, "shield", false, "nhance_back_r");
            AssertResolvedPlacement(
                tryResolve,
                nativeProfile,
                "dagger_main",
                true,
                "nhance_weapon_r",
                new Quaternion(-0.213657289f, -0.975105758f, 0f, 0.059323895f));
            AssertResolvedPlacement(tryResolve, nativeProfile, "dagger_main", false, "nhance_hip_r");
            AssertResolvedPlacement(
                tryResolve,
                nativeProfile,
                "dagger_off",
                true,
                "nhance_weapon_l",
                new Quaternion(-0.280249268f, 0.957732224f, 0f, 0.064879383f));
            AssertResolvedPlacement(tryResolve, nativeProfile, "dagger_off", false, "nhance_hip_l");
            AssertResolvedPlacement(tryResolve, nativeProfile, "greatsword", true, "nhance_greatsword_hand");
            AssertResolvedPlacement(tryResolve, nativeProfile, "greatsword", false, "nhance_back_2hl");
            AssertResolvedPlacement(tryResolve, nativeProfile, "staff", true, "nhance_staff_hand");
            AssertResolvedPlacement(tryResolve, nativeProfile, "staff", false, "nhance_staff_stowed");
            AssertResolvedPlacement(tryResolve, nativeProfile, "bow_drawn", true, "nhance_weapon_l");
            AssertResolvedPlacement(tryResolve, nativeProfile, "bow_drawn", false, "nhance_weapon_l");
            AssertResolvedPlacement(tryResolve, nativeProfile, "bow_stowed", true, "nhance_back_bow");
            AssertResolvedPlacement(tryResolve, nativeProfile, "bow_stowed", false, "nhance_back_bow");
            AssertResolvedPlacement(tryResolve, nativeProfile, "quiver", true, "nhance_back_quiver");
            AssertResolvedPlacement(tryResolve, nativeProfile, "quiver", false, "nhance_back_quiver");

            object?[] legacyArgs = { legacyProfile, "sword", true, null };
            Assert.That((bool)tryResolve.Invoke(null, legacyArgs)!, Is.False,
                "Legacy appearances must continue to use their animation-set binding without an override.");
        }

        [Test]
        public void EquipmentAppearanceCatalog_ContainsEveryShippedCompleteArmorSetVisual()
        {
            object equipmentCatalog = LoadRequiredAsset(
                EquipmentAppearanceCatalogPath,
                "Arena.Presentation.Appearance.EquipmentAppearanceCatalog");
            object[] armorSets = ((IEnumerable)RequireProperty(equipmentCatalog, "ArmorSets")
                    .GetValue(equipmentCatalog)!)
                .Cast<object>()
                .ToArray();
            string[] expectedSlots = { "HEAD", "SHOULDER", "CAPE", "CHEST", "LEGS", "BOOTS", "GLOVES" };

            Assert.That(armorSets.Length, Is.EqualTo(40));
            Assert.That(
                armorSets.Select(entry => RequireField<string>(entry, "armorSetId")).Distinct().Count(),
                Is.EqualTo(armorSets.Length));

            MethodInfo tryGetItems = RequireMethod(equipmentCatalog.GetType(), "TryGetItems");
            foreach (object armorSet in armorSets)
            {
                string armorSetId = RequireField<string>(armorSet, "armorSetId");
                Assert.That(RequireField<bool>(armorSet, "enabled"), Is.True, armorSetId);
                Assert.That(RequireField<string>(armorSet, "raceId"), Is.EqualTo("HUMAN"), armorSetId);
                Assert.That(RequireField<string>(armorSet, "sexId"), Is.EqualTo("MALE"), armorSetId);

                object[] slots = RequireField<IList>(armorSet, "slots").Cast<object>().ToArray();
                Assert.That(
                    slots.Select(slot => RequireField<string>(slot, "equipSlot")).OrderBy(slot => slot),
                    Is.EqualTo(expectedSlots.OrderBy(slot => slot)),
                    armorSetId);

                foreach (object slot in slots)
                {
                    string equipSlot = RequireField<string>(slot, "equipSlot");
                    IList items = RequireField<IList>(slot, "items");
                    Assert.That(items.Count, Is.GreaterThan(0), $"{armorSetId}/{equipSlot}");
                    foreach (object itemVisual in items)
                    {
                        Component item = RequireField<Component>(itemVisual, "item");
                        object expectedType = RequireField<object>(itemVisual, "expectedItemType");
                        Assert.That(
                            GetMemberValue(item, "Type"),
                            Is.EqualTo(expectedType),
                            $"{armorSetId}/{equipSlot}/{item.name} has the wrong NHItem type.");
                    }

                    object?[] args =
                    {
                        $"ARMOR_SET_{armorSetId}_{equipSlot}", equipSlot, "HUMAN", "MALE", null,
                    };
                    Assert.That(
                        (bool)tryGetItems.Invoke(equipmentCatalog, args)!,
                        Is.True,
                        $"The runtime lookup cannot resolve {armorSetId}/{equipSlot}.");
                    Assert.That(args[4], Is.Not.Null);
                }
            }
        }

        [Test]
        public void RuntimeAvatarController_SignatureFor_IsStableAndIncludesSavedOutfit()
        {
            object row = CreateAppearanceRow("human_male_archer_starter");
            Type controllerType = RequireType("Arena.Presentation.Appearance.RuntimeAvatarController");
            MethodInfo signatureFor = RequireMethod(controllerType, "SignatureFor", row.GetType());

            string signature = (string)signatureFor.Invoke(null, new[] { row })!;

            Assert.That(signature, Is.EqualTo("HUMAN|MALE|HUMAN_MALE_BODY_01|HUMAN_MALE_HEAD_01_A|||HUMAN_EYES_BLUE|HUMAN_MALE_ARCHER_STARTER"));
        }

        [Test]
        public void RuntimeAvatarController_BuildsBindingsForStarterOutfits()
        {
            AssertRuntimeBindingForOutfit("HUMAN_MALE_PEASANT_STARTER");
            AssertRuntimeBindingForOutfit("HUMAN_MALE_WARRIOR_STARTER");
            AssertRuntimeBindingForOutfit("HUMAN_MALE_PALADIN_STARTER");
            AssertRuntimeBindingForOutfit("HUMAN_MALE_ARCHER_STARTER");
        }

        [Test]
        public void RuntimeAvatarController_AppliesSavedOutfitInsteadOfStarterDefault()
        {
            object savedPaladinAppearance = CreateAppearanceRow("HUMAN_MALE_PALADIN_STARTER");
            Type controllerType = RequireType("Arena.Presentation.Appearance.RuntimeAvatarController");
            MethodInfo signatureForRow = RequireMethod(controllerType, "SignatureFor", savedPaladinAppearance.GetType());
            string savedSignature = (string)signatureForRow.Invoke(null, new[] { savedPaladinAppearance })!;

            Type selectionType = RequireType("Arena.Presentation.Appearance.CharacterAppearanceSelection");
            object archerDefaultSelection = RequireMethod(selectionType, "DefaultHumanMale").Invoke(null, new object?[] { "HUMAN_MALE_ARCHER_STARTER" })!;
            MethodInfo signatureForSelection = RequireMethod(controllerType, "SignatureFor", selectionType);
            string archerDefaultSignature = (string)signatureForSelection.Invoke(null, new[] { archerDefaultSelection })!;

            Assert.That(savedSignature, Is.Not.EqualTo(archerDefaultSignature));

            GameObject host = new("RuntimeAvatarSavedOutfitTest");
            try
            {
                Component controller = host.AddComponent(controllerType);
                object binding = ApplyRuntimeAvatar(controller, savedPaladinAppearance);
                Assert.That(RequireProperty(binding, "AppearanceSignature").GetValue(binding), Is.EqualTo(savedSignature));
                RequireAvatarIntegrity((GameObject)RequireProperty(binding, "AvatarRoot").GetValue(binding)!);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RuntimeAvatarController_RebindsArcherMountsAfterAppearanceReplacement()
        {
            GameObject host = new("RuntimeAvatarReplacementTest");
            try
            {
                Type controllerType = RequireType("Arena.Presentation.Appearance.RuntimeAvatarController");
                Type attachmentsType = RequireType("Arena.Presentation.WeaponAttachmentController");
                Type mountsType = RequireType("Arena.Presentation.AvatarWeaponMounts");
                Component controller = host.AddComponent(controllerType);
                Component attachments = host.AddComponent(attachmentsType);

                object dreadBinding = ApplyRuntimeAvatar(controller, CreateAppearanceRow("HUMAN_MALE_WARRIOR_STARTER"));
                RequireMethod(attachmentsType, "BindMounts", mountsType).Invoke(attachments, new[] { RequireProperty(dreadBinding, "Mounts").GetValue(dreadBinding) });
                RequireMethod(attachmentsType, "ClearVisuals").Invoke(attachments, Array.Empty<object>());

                object archerBinding = ApplyRuntimeAvatar(controller, CreateAppearanceRow("HUMAN_MALE_ARCHER_STARTER"));
                object archerMounts = RequireProperty(archerBinding, "Mounts").GetValue(archerBinding)!;
                RequireMethod(attachmentsType, "BindMounts", mountsType).Invoke(attachments, new[] { archerMounts });

                AssertMountExists(archerMounts, "archer_bow_hand");
                AssertMountExists(archerMounts, "archer_bow_stowed");
                AssertMountExists(archerMounts, "archer_quiver_stowed");
                AssertMountExists(archerMounts, "nhance_weapon_r");
                AssertMountExists(archerMounts, "nhance_weapon_l");
                AssertMountExists(archerMounts, "nhance_weapon_shield");
                AssertMountExists(archerMounts, "nhance_back_l");
                AssertMountExists(archerMounts, "nhance_back_r");
                AssertMountExists(archerMounts, "nhance_back_bow");
                AssertMountExists(archerMounts, "nhance_back_2hl");
                AssertMountExists(archerMounts, "nhance_back_quiver");
                AssertMountExists(archerMounts, "nhance_hip_r");
                AssertMountExists(archerMounts, "nhance_hip_l");
                AssertMountExists(archerMounts, "nhance_greatsword_hand");
                AssertMountExists(archerMounts, "nhance_staff_hand");
                AssertMountExists(archerMounts, "nhance_staff_stowed");

                Transform nativeWeaponR = ResolveMount(archerMounts, "nhance_weapon_r");
                Transform greatswordCorrection = ResolveMount(archerMounts, "nhance_greatsword_hand");
                Assert.That(Vector3.Distance(greatswordCorrection.position, nativeWeaponR.position), Is.LessThan(0.00001f));
                Assert.That(Quaternion.Angle(greatswordCorrection.rotation, nativeWeaponR.rotation), Is.LessThan(0.001f));
                Assert.That(greatswordCorrection.parent, Is.Not.SameAs(nativeWeaponR.parent),
                    "The greatsword correction must remain under its animation-driven socket.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        private static void AssertAssemblesHumanMaleOutfit(object catalogs, Transform parent, string outfitId)
        {
            Type selectionType = RequireType("Arena.Presentation.Appearance.CharacterAppearanceSelection");
            object selection = RequireMethod(selectionType, "DefaultHumanMale").Invoke(null, new object?[] { outfitId })!;

            Type assemblerType = RequireType("Arena.Presentation.Appearance.CharacterAvatarAssembler");
            MethodInfo tryAssemble = assemblerType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == "TryAssemble" && m.GetParameters().Length == 7);

            object?[] args = { selection, catalogs, parent, null, string.Empty, null, null };
            bool assembled = (bool)tryAssemble.Invoke(null, args)!;
            Assert.That(assembled, Is.True, (string?)args[4]);

            GameObject avatar = (GameObject)args[3]!;
            try
            {
                RequireAvatarIntegrity(avatar);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(avatar);
            }
        }

        private static void AssertRuntimeBindingForOutfit(string outfitId)
        {
            GameObject host = new($"RuntimeAvatarBindingTest_{outfitId}");
            try
            {
                Type controllerType = RequireType("Arena.Presentation.Appearance.RuntimeAvatarController");
                Component controller = host.AddComponent(controllerType);
                object binding = ApplyRuntimeAvatar(controller, CreateAppearanceRow(outfitId));
                Assert.That(RequireProperty(binding, "AvatarRoot").GetValue(binding), Is.Not.Null);
                Assert.That(RequireProperty(binding, "Animator").GetValue(binding), Is.Not.Null);
                Assert.That(RequireProperty(binding, "Mounts").GetValue(binding), Is.Not.Null);
                Array renderers = (Array)RequireProperty(binding, "Renderers").GetValue(binding)!;
                Assert.That(renderers.Length, Is.GreaterThan(0));
                RequireAvatarIntegrity((GameObject)RequireProperty(binding, "AvatarRoot").GetValue(binding)!);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        private static object CreateAppearanceRow(string outfitId)
        {
            object row = Activator.CreateInstance(RequireType("SpacetimeDB.Types.CharacterAppearance"))!;
            RequireField(row.GetType(), "RaceId").SetValue(row, "HUMAN");
            RequireField(row.GetType(), "SexId").SetValue(row, "MALE");
            RequireField(row.GetType(), "BodyId").SetValue(row, "HUMAN_MALE_BODY_01");
            RequireField(row.GetType(), "HeadId").SetValue(row, "HUMAN_MALE_HEAD_01_A");
            RequireField(row.GetType(), "FaceId").SetValue(row, "");
            RequireField(row.GetType(), "HairId").SetValue(row, "");
            RequireField(row.GetType(), "EyesId").SetValue(row, "HUMAN_EYES_BLUE");
            RequireField(row.GetType(), "OutfitId").SetValue(row, outfitId);
            RequireField(row.GetType(), "CreationComplete").SetValue(row, true);
            return row;
        }

        private static object ApplyRuntimeAvatar(Component controller, object appearanceRow)
        {
            MethodInfo apply = RequireMethod(controller.GetType(), "Apply", appearanceRow.GetType());
            object?[] args = { appearanceRow, null, string.Empty };
            bool applied = (bool)apply.Invoke(controller, args)!;
            Assert.That(applied, Is.True, (string?)args[2]);
            Assert.That(args[1], Is.Not.Null);
            return args[1]!;
        }

        private static void AssertMountExists(object mounts, string mountId)
        {
            MethodInfo tryGetMount = RequireMethod(mounts.GetType(), "TryGetMount");
            object?[] args = { mountId, null };
            Assert.That((bool)tryGetMount.Invoke(mounts, args)!, Is.True, $"Expected mount '{mountId}'.");
        }

        private static Transform ResolveMount(object mounts, string mountId)
        {
            MethodInfo tryGetMount = RequireMethod(mounts.GetType(), "TryGetMount");
            object?[] args = { mountId, null };
            Assert.That((bool)tryGetMount.Invoke(mounts, args)!, Is.True, $"Expected mount '{mountId}'.");
            return (Transform)args[1]!;
        }

        private static void AssertWeaponPlacementProfile(
            object[] visuals,
            string itemDefId,
            string colorId,
            string roleId,
            int expectedProfile)
        {
            object? visual = visuals.FirstOrDefault(candidate =>
                string.Equals(RequireField<string>(candidate, "itemDefId"), itemDefId, StringComparison.Ordinal)
                && string.Equals(RequireField<string>(candidate, "colorId"), colorId, StringComparison.Ordinal)
                && string.Equals(RequireField<string>(candidate, "visualRoleId"), roleId, StringComparison.Ordinal));
            Assert.That(visual, Is.Not.Null, $"Missing weapon appearance {itemDefId}/{colorId}/{roleId}.");
            Assert.That(Convert.ToInt32(RequireField<object>(visual!, "placementProfile")), Is.EqualTo(expectedProfile));
        }

        private static void AssertResolvedPlacement(
            MethodInfo tryResolve,
            object profile,
            string roleId,
            bool inCombat,
            string expectedMountId,
            Quaternion? expectedRotation = null)
        {
            object?[] args = { profile, roleId, inCombat, null };
            Assert.That((bool)tryResolve.Invoke(null, args)!, Is.True, $"No placement for {roleId}/{inCombat}.");
            Assert.That(
                (string)RequireProperty(args[3]!, "MountId").GetValue(args[3])!,
                Is.EqualTo(expectedMountId));
            Assert.That((Vector3)RequireProperty(args[3]!, "LocalPosition").GetValue(args[3])!, Is.EqualTo(Vector3.zero));
            Quaternion actualRotation = (Quaternion)RequireProperty(args[3]!, "LocalRotation").GetValue(args[3])!;
            Assert.That(
                Quaternion.Angle(actualRotation, expectedRotation ?? Quaternion.identity),
                Is.LessThan(0.001f),
                $"Unexpected rotation for {roleId}/{inCombat}.");
            Assert.That((Vector3)RequireProperty(args[3]!, "LocalScaleMultiplier").GetValue(args[3])!, Is.EqualTo(Vector3.one));
        }

        private static void RequireAvatarIntegrity(GameObject avatar)
        {
            Type nhAvatarType = RequireType("NHance.Assets.Scripts.NHAvatar");
            Component nhAvatar = avatar.GetComponent(nhAvatarType);
            Assert.That(nhAvatar, Is.Not.Null);
            Assert.That(RequireField<Transform>(nhAvatar, "rootBone"), Is.Not.Null);
            Assert.That(RequireField<Transform>(nhAvatar, "rootGeometry"), Is.Not.Null);
            Assert.That(RequireField<IList>(nhAvatar, "PartsMap").Count, Is.GreaterThan(0));

            object socketMap = RequireField<object>(nhAvatar, "SocketMap");
            Assert.That(RequireField<IList>(socketMap, "_list").Count, Is.GreaterThan(0));

            Type mountsType = RequireType("Arena.Presentation.AvatarWeaponMounts");
            Component mounts = avatar.GetComponent(mountsType);
            Assert.That(mounts, Is.Not.Null, "Assembled avatars must expose Arena weapon mounts.");

            MethodInfo tryGetMount = RequireMethod(mountsType, "TryGetMount");
            object?[] mountArgs = { "main_weapon_hand", null };
            Assert.That((bool)tryGetMount.Invoke(mounts, mountArgs)!, Is.True, "Assembled avatars must expose a main-hand weapon mount.");
            AssertMountExists(mounts, "staff_hand");
            AssertMountExists(mounts, "staff_stowed");
            AssertMountExists(mounts, "nhance_staff_hand");
            AssertMountExists(mounts, "nhance_staff_stowed");
        }

        private static ScriptableObject LoadRequiredAsset(string assetPath, string typeName)
        {
            Type type = RequireType(typeName);
            ScriptableObject? asset = AssetDatabase.LoadAssetAtPath(assetPath, type) as ScriptableObject;
            Assert.That(asset, Is.Not.Null, $"Missing generated catalog asset: {assetPath}");
            return asset!;
        }

        private static IEnumerable Entries(object catalog)
        {
            return (IEnumerable)RequireProperty(catalog, "Entries").GetValue(catalog)!;
        }

        private static void AssertEquipmentVisual(object[] entries, string itemDefId, string equipSlot)
        {
            object? entry = entries.FirstOrDefault(candidate =>
                string.Equals(RequireField<string>(candidate, "itemDefId"), itemDefId, StringComparison.Ordinal)
                && string.Equals(RequireField<string>(candidate, "equipSlot"), equipSlot, StringComparison.Ordinal)
                && string.Equals(RequireField<string>(candidate, "raceId"), "HUMAN", StringComparison.Ordinal)
                && string.Equals(RequireField<string>(candidate, "sexId"), "MALE", StringComparison.Ordinal));
            Assert.That(entry, Is.Not.Null, $"Missing equipment visual for {itemDefId}/{equipSlot}.");
            Assert.That(RequireField<bool>(entry!, "enabled"), Is.True);
            Assert.That(RequireField<IList>(entry!, "items").Count, Is.GreaterThan(0));
        }

        private static T RequireField<T>(object instance, string fieldName)
        {
            object? value = RequireField(instance.GetType(), fieldName).GetValue(instance);
            Assert.That(value, Is.Not.Null, $"{instance.GetType().Name}.{fieldName} must not be null.");
            return (T)value!;
        }

        private static FieldInfo RequireField(Type type, string fieldName)
        {
            FieldInfo? field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"{type.FullName}.{fieldName} was not found.");
            return field!;
        }

        private static PropertyInfo RequireProperty(object instance, string propertyName)
        {
            return RequireProperty(instance.GetType(), propertyName);
        }

        private static object? GetMemberValue(object instance, string memberName)
        {
            Type type = instance.GetType();
            PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
                return property.GetValue(instance);

            FieldInfo? field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"{type.FullName}.{memberName} was not found.");
            return field!.GetValue(instance);
        }

        private static PropertyInfo RequireProperty(Type type, string propertyName)
        {
            PropertyInfo? property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null, $"{type.FullName}.{propertyName} was not found.");
            return property!;
        }

        private static MethodInfo RequireMethod(Type type, string methodName)
        {
            MethodInfo? method = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == methodName);
            Assert.That(method, Is.Not.Null, $"{type.FullName}.{methodName} was not found.");
            return method!;
        }

        private static MethodInfo RequireMethod(Type type, string methodName, params Type[] parameterTypes)
        {
            MethodInfo? method = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                {
                    if (m.Name != methodName)
                        return false;

                    ParameterInfo[] parameters = m.GetParameters();
                    if (parameters.Length < parameterTypes.Length)
                        return false;

                    for (int i = 0; i < parameterTypes.Length; i++)
                    {
                        Type parameterType = parameters[i].ParameterType;
                        if (parameterType.IsByRef)
                            parameterType = parameterType.GetElementType()!;
                        if (parameterType != parameterTypes[i])
                            return false;
                    }

                    return true;
                });
            Assert.That(method, Is.Not.Null, $"{type.FullName}.{methodName}({string.Join(", ", parameterTypes.Select(t => t.Name))}) was not found.");
            return method!;
        }

        private static Type RequireType(string typeName)
        {
            Type? type = RuntimeAssembly.GetType(typeName)
                ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
            Assert.That(type, Is.Not.Null, $"{typeName} was not found.");
            return type!;
        }
    }
}

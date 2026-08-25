#nullable enable
using System;
using Arena.Presentation;
using System.Collections.Generic;
using NHance.Assets.Scripts;
using NHance.Assets.Scripts.Enums;
using NHance.Assets.Scripts.Items;
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    public sealed class CharacterAvatarAssembler : MonoBehaviour
    {
        private static readonly string[] ArmorEquipmentSlots =
        {
            "HEAD",
            "SHOULDER",
            "CAPE",
            "CHEST",
            "LEGS",
            "BOOTS",
            "GLOVES",
        };

        [SerializeField] private Transform? avatarParent;
        [SerializeField] private AvatarBaseCatalog? baseCatalog;
        [SerializeField] private AvatarPartCatalog? partCatalog;
        [SerializeField] private OutfitCatalog? outfitCatalog;
        [SerializeField] private EquipmentAppearanceCatalog? equipmentAppearanceCatalog;

        private GameObject? _currentAvatar;

        public GameObject? CurrentAvatar => _currentAvatar;

        public bool TryApply(CharacterAppearanceSelection selection, out GameObject avatar, out string error)
        {
            if (!TryResolveCatalogs(out CharacterAppearanceCatalogSet catalogs, out error))
            {
                avatar = null!;
                return false;
            }

            Transform parent = avatarParent != null ? avatarParent : transform;
            return TryAssemble(selection, catalogs, parent, out avatar, out error, existingAvatar: _currentAvatar, owner: this);
        }

        public bool TryApplyStarterDefault(out GameObject avatar, out string error)
        {
            if (!TryResolveCatalogs(out CharacterAppearanceCatalogSet catalogs, out error))
            {
                avatar = null!;
                return false;
            }

            CharacterAppearanceSelection selection =
                CharacterAppearanceSelection.DefaultHumanMale(CharacterAppearanceIds.DefaultOutfitId);
            Transform parent = avatarParent != null ? avatarParent : transform;
            return TryAssemble(selection, catalogs, parent, out avatar, out error, existingAvatar: _currentAvatar, owner: this);
        }

        public void Clear()
        {
            DestroyAvatar(_currentAvatar);
            _currentAvatar = null;
        }

        public static bool TryAssemble(
            CharacterAppearanceSelection selection,
            CharacterAppearanceCatalogSet catalogs,
            Transform parent,
            out GameObject avatar,
            out string error,
            GameObject? existingAvatar = null,
            CharacterAvatarAssembler? owner = null,
            IReadOnlyDictionary<string, string>? equippedArmorBySlot = null)
        {
            selection.NormalizeInPlace();

            if (catalogs == null)
            {
                avatar = null!;
                error = "Character appearance catalogs are required.";
                return false;
            }

            if (parent == null)
            {
                avatar = null!;
                error = "Avatar parent is required.";
                return false;
            }

            if (!catalogs.BaseCatalog.TryGetBasePrefab(selection.raceId, selection.sexId, out GameObject basePrefab))
            {
                avatar = null!;
                error = $"No avatar base prefab is available for {selection.raceId}/{selection.sexId}.";
                return false;
            }

            GameObject instance = Instantiate(basePrefab, parent, false);
            instance.name = $"CharacterAvatar_{selection.raceId}_{selection.sexId}";

            NHAvatar? nhAvatar = instance.GetComponent<NHAvatar>();
            if (nhAvatar == null)
            {
                DestroyAvatar(instance);
                avatar = null!;
                error = $"Avatar base '{basePrefab.name}' is missing NHAvatar.";
                return false;
            }

            try
            {
                nhAvatar.Clean();
                nhAvatar.ClearItems();
                ApplyPart(catalogs.PartCatalog, nhAvatar, AvatarPartSlot.Body, selection.bodyId, selection.raceId, selection.sexId);
                ApplyPart(catalogs.PartCatalog, nhAvatar, AvatarPartSlot.Head, selection.headId, selection.raceId, selection.sexId);
                ApplyPart(catalogs.PartCatalog, nhAvatar, AvatarPartSlot.Face, selection.faceId, selection.raceId, selection.sexId);
                ApplyPart(catalogs.PartCatalog, nhAvatar, AvatarPartSlot.Hair, selection.hairId, selection.raceId, selection.sexId);
                ApplyPart(catalogs.PartCatalog, nhAvatar, AvatarPartSlot.Eyes, selection.eyesId, selection.raceId, selection.sexId);
                ApplyOutfit(catalogs, nhAvatar, selection, equippedArmorBySlot);
                nhAvatar.Compile();
                EnsureArenaWeaponMounts(instance, nhAvatar);
                CombatAnimationEventReceiver.EnsureOn(ResolveAnimator(instance));
            }
            catch (Exception ex)
            {
                DestroyAvatar(instance);
                avatar = null!;
                error = ex.Message;
                return false;
            }

            DestroyAvatar(existingAvatar);
            if (owner != null)
                owner._currentAvatar = instance;

            avatar = instance;
            error = string.Empty;
            return true;
        }

        public static bool TryApplyToExisting(
            GameObject instance,
            CharacterAppearanceSelection selection,
            CharacterAppearanceCatalogSet catalogs,
            out string error,
            IReadOnlyDictionary<string, string>? equippedArmorBySlot = null)
        {
            selection.NormalizeInPlace();

            if (instance == null)
            {
                error = "Avatar instance is required.";
                return false;
            }

            if (catalogs == null)
            {
                error = "Character appearance catalogs are required.";
                return false;
            }

            NHAvatar? nhAvatar = instance.GetComponent<NHAvatar>();
            if (nhAvatar == null)
                nhAvatar = instance.GetComponentInChildren<NHAvatar>(includeInactive: true);
            if (nhAvatar == null)
            {
                error = $"Avatar instance '{instance.name}' is missing NHAvatar.";
                return false;
            }

            try
            {
                nhAvatar.Clean();
                nhAvatar.ClearItems();
                ApplyPart(catalogs.PartCatalog, nhAvatar, AvatarPartSlot.Body, selection.bodyId, selection.raceId, selection.sexId);
                ApplyPart(catalogs.PartCatalog, nhAvatar, AvatarPartSlot.Head, selection.headId, selection.raceId, selection.sexId);
                ApplyPart(catalogs.PartCatalog, nhAvatar, AvatarPartSlot.Face, selection.faceId, selection.raceId, selection.sexId);
                ApplyPart(catalogs.PartCatalog, nhAvatar, AvatarPartSlot.Hair, selection.hairId, selection.raceId, selection.sexId);
                ApplyPart(catalogs.PartCatalog, nhAvatar, AvatarPartSlot.Eyes, selection.eyesId, selection.raceId, selection.sexId);
                ApplyOutfit(catalogs, nhAvatar, selection, equippedArmorBySlot);
                nhAvatar.Compile();
                EnsureArenaWeaponMounts(instance, nhAvatar);
                CombatAnimationEventReceiver.EnsureOn(ResolveAnimator(instance));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            error = string.Empty;
            return true;
        }

        private bool TryResolveCatalogs(out CharacterAppearanceCatalogSet catalogs, out string error)
        {
            if (baseCatalog != null && partCatalog != null && outfitCatalog != null)
            {
                catalogs = new CharacterAppearanceCatalogSet(
                    baseCatalog,
                    partCatalog,
                    outfitCatalog,
                    equipmentAppearanceCatalog);
                error = string.Empty;
                return true;
            }

            return CharacterAppearanceCatalogSet.TryLoadDefault(out catalogs, out error);
        }

        private static void ApplyPart(
            AvatarPartCatalog catalog,
            NHAvatar avatar,
            AvatarPartSlot slot,
            string partId,
            string raceId,
            string sexId)
        {
            if (string.IsNullOrWhiteSpace(partId))
                return;

            if (!catalog.TryGetItem(slot, partId, raceId, sexId, out NHItem item, out string error))
                throw new InvalidOperationException(error);

            avatar.SetItem(item);
        }

        private static void ApplyOutfit(
            CharacterAppearanceCatalogSet catalogs,
            NHAvatar avatar,
            CharacterAppearanceSelection selection,
            IReadOnlyDictionary<string, string>? equippedArmorBySlot)
        {
            if (string.IsNullOrWhiteSpace(selection.outfitId))
                return;

            if (!catalogs.OutfitCatalog.TryGetOutfit(selection.outfitId, out OutfitCatalog.Entry outfit))
                throw new InvalidOperationException($"Outfit '{selection.outfitId}' is not available.");

            for (int i = 0; i < outfit.items.Count; i++)
            {
                OutfitCatalog.OutfitItem slot = outfit.items[i];
                if (slot == null || slot.item == null)
                    continue;

                if (!TryGetEquipmentSlotForItemType(slot.expectedItemType, out string equipSlot))
                {
                    ApplyValidatedOutfitItem(avatar, slot, selection.outfitId);
                    continue;
                }

                if (equippedArmorBySlot != null)
                    continue;

                ApplyValidatedOutfitItem(avatar, slot, selection.outfitId);
            }

            if (equippedArmorBySlot == null)
                return;

            if (catalogs.EquipmentAppearanceCatalog == null)
                throw new InvalidOperationException("An equipment appearance catalog is required for equipped armor.");

            for (int i = 0; i < ArmorEquipmentSlots.Length; i++)
            {
                string equipSlot = ArmorEquipmentSlots[i];
                if (!equippedArmorBySlot.TryGetValue(equipSlot, out string itemDefId))
                    continue;

                if (!catalogs.EquipmentAppearanceCatalog.TryGetItems(
                        itemDefId,
                        equipSlot,
                        selection.raceId,
                        selection.sexId,
                        out EquipmentAppearanceCatalog.Entry equipmentEntry))
                {
                    throw new InvalidOperationException(
                        $"No equipment visual is available for '{itemDefId}' in slot '{equipSlot}' " +
                        $"for {selection.raceId}/{selection.sexId}.");
                }

                ApplyEquipmentEntry(avatar, equipmentEntry);
            }
        }

        private static bool TryGetEquipmentSlotForItemType(ItemTypeEnum itemType, out string equipSlot)
        {
            equipSlot = itemType switch
            {
                ItemTypeEnum.Helmet => "HEAD",
                ItemTypeEnum.Shoulders => "SHOULDER",
                ItemTypeEnum.Cape => "CAPE",
                ItemTypeEnum.ChestSkin or ItemTypeEnum.Chest => "CHEST",
                ItemTypeEnum.PantsSkin or ItemTypeEnum.Pants => "LEGS",
                ItemTypeEnum.Boots => "BOOTS",
                ItemTypeEnum.GlovesSkin or ItemTypeEnum.Gloves => "GLOVES",
                _ => string.Empty,
            };
            return !string.IsNullOrWhiteSpace(equipSlot);
        }

        private static void ApplyEquipmentEntry(NHAvatar avatar, EquipmentAppearanceCatalog.Entry entry)
        {
            for (int i = 0; i < entry.items.Count; i++)
            {
                EquipmentAppearanceCatalog.EquipmentItem item = entry.items[i];
                if (item == null || item.item == null)
                    continue;

                if (item.item.Type != item.expectedItemType)
                {
                    throw new InvalidOperationException(
                        $"Equipment visual '{entry.itemDefId}' expected item type {item.expectedItemType} but found {item.item.Type} on '{item.item.name}'.");
                }

                avatar.SetItem(item.item);
            }
        }

        private static void ApplyValidatedOutfitItem(
            NHAvatar avatar,
            OutfitCatalog.OutfitItem slot,
            string outfitId)
        {
            if (slot.item == null)
                return;

            if (slot.item.Type != slot.expectedItemType)
            {
                throw new InvalidOperationException(
                    $"Outfit '{outfitId}' expected item type {slot.expectedItemType} but found {slot.item.Type} on '{slot.item.name}'.");
            }

            avatar.SetItem(slot.item);
        }

        private static void EnsureArenaWeaponMounts(GameObject instance, NHAvatar nhAvatar)
        {
            AvatarWeaponMounts mounts = instance.GetComponent<AvatarWeaponMounts>();
            if (mounts == null)
                mounts = instance.AddComponent<AvatarWeaponMounts>();

            Transform? mainHandParent = ResolveHumanoidBone(instance, HumanBodyBones.RightHand)
                ?? ResolveNamedTransform(instance, "hand_r")
                ?? ResolveSocket(nhAvatar, BoneType.WeaponR);
            Transform? offHandParent = ResolveHumanoidBone(instance, HumanBodyBones.LeftHand)
                ?? ResolveNamedTransform(instance, "hand_l")
                ?? ResolveSocket(nhAvatar, BoneType.Shield)
                ?? ResolveSocket(nhAvatar, BoneType.WeaponL);
            Transform? backParent = ResolveNamedTransform(instance, "spine_03")
                ?? ResolveHumanoidBone(instance, HumanBodyBones.UpperChest)
                ?? ResolveHumanoidBone(instance, HumanBodyBones.Chest)
                ?? ResolveSocket(nhAvatar, BoneType.BackM)
                ?? ResolveSocket(nhAvatar, BoneType.BackL);
            Transform? pelvisParent = ResolveHumanoidBone(instance, HumanBodyBones.Hips)
                ?? ResolveNamedTransform(instance, "Pelvis")
                ?? ResolveNamedTransform(instance, "pelvis");
            Transform? mainHip = ResolveSocket(nhAvatar, BoneType.HipR) ?? ResolveHumanoidBone(instance, HumanBodyBones.RightUpperLeg);
            Transform? offHip = ResolveSocket(nhAvatar, BoneType.HipL) ?? ResolveHumanoidBone(instance, HumanBodyBones.LeftUpperLeg);
            Transform? nhanceWeaponR = ResolveSocket(nhAvatar, BoneType.WeaponR);
            Transform? nhanceWeaponL = ResolveSocket(nhAvatar, BoneType.WeaponL);
            Transform? nhanceShield = ResolveSocket(nhAvatar, BoneType.Shield);
            Transform? nhanceBackL = ResolveSocket(nhAvatar, BoneType.BackL);
            Transform? nhanceBackR = ResolveSocket(nhAvatar, BoneType.BackR);
            Transform? nhanceBackBow = ResolveSocket(nhAvatar, BoneType.BackBow);
            Transform? nhanceBack2HL = ResolveSocket(nhAvatar, BoneType.Back2HL);
            Transform? nhanceQuiver = ResolveSocket(nhAvatar, BoneType.Quiver);
            Transform? nhanceHipR = ResolveSocket(nhAvatar, BoneType.HipR);
            Transform? nhanceHipL = ResolveSocket(nhAvatar, BoneType.HipL);
            bool hadNhanceWeaponR = nhanceWeaponR != null;
            Vector3 nhanceWeaponRWorldPosition = nhanceWeaponR != null ? nhanceWeaponR.position : Vector3.zero;
            Quaternion nhanceWeaponRWorldRotation = nhanceWeaponR != null ? nhanceWeaponR.rotation : Quaternion.identity;

            Transform? mainHand = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                mainHandParent,
                ArenaWeaponMountCalibration.MainHand);
            Transform? offHand = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                offHandParent,
                ArenaWeaponMountCalibration.OffHand);
            Transform? mainBack = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                backParent,
                ArenaWeaponMountCalibration.MainSheath);
            Transform? offBack = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                backParent,
                ArenaWeaponMountCalibration.OffSheath);
            Transform? archerBowHand = offHand ?? offHandParent;
            Transform? archerBowStowed = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                backParent,
                ArenaWeaponMountCalibration.ArcherBowStowed);
            Transform? archerQuiverStowed = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                backParent,
                ArenaWeaponMountCalibration.ArcherQuiverStowed);
            Transform? greatswordAnimatedSocket = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                mainHandParent,
                ArenaWeaponMountCalibration.GreatswordAnimatedHandSocket);
            Transform? greatswordHand = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                greatswordAnimatedSocket,
                ArenaWeaponMountCalibration.GreatswordHand);
            Transform? nhanceGreatswordHand = AvatarWeaponMounts.CreateOrUpdateWorldAlignedMountChild(
                greatswordAnimatedSocket,
                nhanceWeaponR,
                "Arena_NHance_greatsword_hand");
            Transform? greatswordStowed = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                backParent,
                ArenaWeaponMountCalibration.GreatswordStowed);
            Transform? staffStowedReference = nhanceBack2HL ?? mainBack;
            Vector3 staffStowedReferencePosition = staffStowedReference != null ? staffStowedReference.position : Vector3.zero;
            Quaternion staffStowedReferenceRotation = staffStowedReference != null ? staffStowedReference.rotation : Quaternion.identity;
            Transform? mageStaffAnimatedHandSocket = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                mainHandParent,
                ArenaWeaponMountCalibration.MageStaffAnimatedHandSocket);
            Transform? staffHand = AvatarWeaponMounts.CreateOrUpdateWorldAlignedMountChild(
                mageStaffAnimatedHandSocket,
                mageStaffAnimatedHandSocket,
                "Arena_Staff_hand");
            // The mage socket recalibrates the hand's native Weapon_R node onto the
            // MageAnimationPack bind pose, and the pack's clips drive it. Restore the
            // native socket under the hand itself, not under the mage socket: this mount
            // carries swords and main-hand daggers, which must not inherit staff motion.
            Transform? restoredNhanceWeaponR = hadNhanceWeaponR
                ? AvatarWeaponMounts.CreateOrUpdateWorldAlignedMountChild(
                    mainHandParent,
                    nhanceWeaponRWorldPosition,
                    nhanceWeaponRWorldRotation,
                    "Arena_NHance_weapon_r")
                : mageStaffAnimatedHandSocket;
            Transform? nhanceStaffHand = hadNhanceWeaponR
                ? AvatarWeaponMounts.CreateOrUpdateWorldAlignedMountChild(
                    mageStaffAnimatedHandSocket,
                    nhanceWeaponRWorldPosition,
                    nhanceWeaponRWorldRotation,
                    "Arena_NHance_staff_hand")
                : mageStaffAnimatedHandSocket;
            Transform? mageStaffAnimatedStowedSocket = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                backParent,
                ArenaWeaponMountCalibration.MageStaffAnimatedStowedSocket);
            Transform? staffStowed = AvatarWeaponMounts.CreateOrUpdateWorldAlignedMountChild(
                mageStaffAnimatedStowedSocket,
                mageStaffAnimatedStowedSocket,
                "Arena_Staff_stowed");
            Transform? nhanceStaffStowed = staffStowedReference != null
                ? AvatarWeaponMounts.CreateOrUpdateWorldAlignedMountChild(
                    mageStaffAnimatedStowedSocket,
                    staffStowedReferencePosition,
                    staffStowedReferenceRotation,
                    "Arena_NHance_staff_stowed")
                : mageStaffAnimatedStowedSocket;
            Transform? daggerMainStowed = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                pelvisParent,
                ArenaWeaponMountCalibration.DaggerMainStowed);
            Transform? daggerOffStowed = ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                pelvisParent,
                ArenaWeaponMountCalibration.DaggerOffStowed);

            SetMount(mounts, AvatarWeaponMounts.MainHandMountId, mainHand);
            SetMount(mounts, AvatarWeaponMounts.GreatswordHandMountId, greatswordHand ?? mainHand);
            SetMount(mounts, AvatarWeaponMounts.GreatswordStowedMountId, greatswordStowed);
            SetMount(mounts, AvatarWeaponMounts.StaffHandMountId, staffHand);
            SetMount(mounts, AvatarWeaponMounts.OffHandMountId, offHand);
            SetMount(mounts, AvatarWeaponMounts.MainBackMountId, mainBack);
            SetMount(mounts, AvatarWeaponMounts.MainStowedMountId, mainBack);
            SetMount(mounts, AvatarWeaponMounts.MainSheathMountId, mainBack);
            SetMount(mounts, AvatarWeaponMounts.OffBackMountId, offBack);
            SetMount(mounts, AvatarWeaponMounts.OffStowedMountId, offBack);
            SetMount(mounts, AvatarWeaponMounts.OffSheathMountId, offBack);
            SetMount(mounts, AvatarWeaponMounts.MainHipMountId, mainHip);
            SetMount(mounts, AvatarWeaponMounts.OffHipMountId, offHip);
            SetMount(mounts, AvatarWeaponMounts.ArcherBowHandMountId, archerBowHand);
            SetMount(mounts, AvatarWeaponMounts.ArcherBowStowedMountId, archerBowStowed);
            SetMount(mounts, AvatarWeaponMounts.ArcherQuiverStowedMountId, archerQuiverStowed);
            SetMount(mounts, AvatarWeaponMounts.StaffStowedMountId, staffStowed);
            SetMount(mounts, AvatarWeaponMounts.DaggerMainStowedMountId, daggerMainStowed);
            SetMount(mounts, AvatarWeaponMounts.DaggerOffStowedMountId, daggerOffStowed);

            SetMount(mounts, AvatarWeaponMounts.NHanceWeaponRMountId, restoredNhanceWeaponR);
            SetMount(mounts, AvatarWeaponMounts.NHanceWeaponLMountId, nhanceWeaponL);
            SetMount(mounts, AvatarWeaponMounts.NHanceShieldMountId, nhanceShield);
            SetMount(mounts, AvatarWeaponMounts.NHanceBackLMountId, nhanceBackL);
            SetMount(mounts, AvatarWeaponMounts.NHanceBackRMountId, nhanceBackR);
            SetMount(mounts, AvatarWeaponMounts.NHanceBackBowMountId, nhanceBackBow);
            SetMount(mounts, AvatarWeaponMounts.NHanceBack2HLMountId, nhanceBack2HL);
            SetMount(mounts, AvatarWeaponMounts.NHanceQuiverMountId, nhanceQuiver);
            SetMount(mounts, AvatarWeaponMounts.NHanceHipRMountId, nhanceHipR);
            SetMount(mounts, AvatarWeaponMounts.NHanceHipLMountId, nhanceHipL);
            SetMount(mounts, AvatarWeaponMounts.NHanceGreatswordHandMountId, nhanceGreatswordHand);
            SetMount(mounts, AvatarWeaponMounts.NHanceStaffHandMountId, nhanceStaffHand);
            SetMount(mounts, AvatarWeaponMounts.NHanceStaffStowedMountId, nhanceStaffStowed);
        }

        private static Transform? ResolveSocket(NHAvatar avatar, BoneType boneType)
        {
            if (avatar.SocketMap == null)
                return null;

            return avatar.SocketMap[boneType];
        }

        private static Transform? ResolveHumanoidBone(GameObject instance, HumanBodyBones bone)
        {
            Animator? animator = ResolveAnimator(instance);
            return animator != null && animator.isHuman ? animator.GetBoneTransform(bone) : null;
        }

        private static Animator? ResolveAnimator(GameObject instance)
        {
            Animator animator = instance.GetComponent<Animator>();
            if (animator == null)
                animator = instance.GetComponentInChildren<Animator>(includeInactive: true);

            return animator;
        }

        private static Transform? ResolveNamedTransform(GameObject instance, string transformName)
        {
            Transform[] transforms = instance.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (string.Equals(transforms[i].name, transformName, StringComparison.Ordinal))
                    return transforms[i];
            }

            return null;
        }

        private static Transform? ResolveDirectChild(Transform? parent, string childName)
        {
            return parent != null ? parent.Find(childName) : null;
        }

        private static void SetMount(AvatarWeaponMounts mounts, string mountId, Transform? mount)
        {
            if (mount != null)
                mounts.SetOrReplaceMount(mountId, mount);
        }

        private static void DestroyAvatar(GameObject? avatar)
        {
            if (avatar == null)
                return;

            avatar.SetActive(false);
            if (Application.isPlaying)
                Destroy(avatar);
            else
                DestroyImmediate(avatar);
        }
    }

}

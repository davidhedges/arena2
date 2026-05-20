#nullable enable
using System;
using System.Collections.Generic;
using Arena.Presentation;
using NHance.Assets.Scripts;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.EditorTools
{
    public static class StylizedPlayerAvatarBuilder
    {
        private const string LegacyRuntimePrefabPath = "Assets/Arena/Resources/PlayerArmature 1.prefab";
        private const string RuntimePrefabPath = "Assets/Arena/Resources/PlayerArmature.prefab";
        private const string StylizedPresetPath = "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Presets/Hu_M_NWarrior_Bl.prefab";
        private const string PropSword = "Sword";
        private const string PropShield = "Shield";
        private const string PropSwordHolder = "Sword_Holder";
        private const string PropShieldHolder = "Shield_Holder";
        private const string PropGreatsword = "weapon_r";

        private readonly struct LegacyMountPose
        {
            public LegacyMountPose(
                string mountId,
                HumanBodyBones parentBone,
                Vector3 rootLocalPosition,
                Quaternion rootLocalRotation,
                Vector3 parentLocalPosition,
                Quaternion parentLocalRotation)
            {
                MountId = mountId;
                ParentBone = parentBone;
                RootLocalPosition = rootLocalPosition;
                RootLocalRotation = rootLocalRotation;
                ParentLocalPosition = parentLocalPosition;
                ParentLocalRotation = parentLocalRotation;
            }

            public string MountId { get; }
            public HumanBodyBones ParentBone { get; }
            public Vector3 RootLocalPosition { get; }
            public Quaternion RootLocalRotation { get; }
            public Vector3 ParentLocalPosition { get; }
            public Quaternion ParentLocalRotation { get; }
        }

        [MenuItem("Arena/Avatars/Rebuild Runtime Player With Stylized Male")]
        public static void BuildRuntimePlayerWithStylizedMaleFromMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog(
                    "Stop Play Mode First",
                    "Exit Play Mode before rebuilding the runtime avatar prefab.",
                    "OK");
                return;
            }

            BuildRuntimePlayerWithStylizedMale();
            RebuildHubIfOpen();
            EditorUtility.DisplayDialog(
                "Runtime Avatar Rebuilt",
                $"Rebuilt {RuntimePrefabPath} from {StylizedPresetPath}.",
                "OK");
        }

        public static void BuildRuntimePlayerWithStylizedMaleBatch()
        {
            BuildRuntimePlayerWithStylizedMale();
        }

        [MenuItem("Arena/Avatars/Validate Runtime Avatar Weapon Mount Contract")]
        public static void ValidateRuntimeAvatarWeaponMountContractFromMenu()
        {
            GameObject runtimePrefab = LoadRequired<GameObject>(RuntimePrefabPath);
            GameObject legacyPrefab = LoadRequired<GameObject>(LegacyRuntimePrefabPath);
            GameObject runtime = (GameObject)PrefabUtility.InstantiatePrefab(runtimePrefab);
            GameObject legacy = (GameObject)PrefabUtility.InstantiatePrefab(legacyPrefab);

            try
            {
                Animator legacyAnimator = RequireComponent<Animator>(legacy, LegacyRuntimePrefabPath);
                Dictionary<string, LegacyMountPose> legacyMountPoses = CaptureLegacyMountPoses(legacy.transform, legacyAnimator);
                ValidateAvatarWeaponMountContract(runtime, legacyMountPoses, throwOnError: true);
                EditorUtility.DisplayDialog(
                    "Avatar Weapon Mount Contract Valid",
                    $"{RuntimePrefabPath} passed the semantic mount contract checks.",
                    "OK");
            }
            finally
            {
                if (runtime != null)
                    UnityEngine.Object.DestroyImmediate(runtime);
                if (legacy != null)
                    UnityEngine.Object.DestroyImmediate(legacy);
            }
        }

        public static void BuildRuntimePlayerWithStylizedMale()
        {
            GameObject legacyPrefab = LoadRequired<GameObject>(LegacyRuntimePrefabPath);
            GameObject stylizedPreset = LoadRequired<GameObject>(StylizedPresetPath);

            GameObject player = (GameObject)PrefabUtility.InstantiatePrefab(legacyPrefab);
            GameObject stylized = (GameObject)PrefabUtility.InstantiatePrefab(stylizedPreset);

            try
            {
                PrefabUtility.UnpackPrefabInstance(player, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                PrefabUtility.UnpackPrefabInstance(stylized, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                player.name = "PlayerArmature";
                player.tag = "Player";
                int playerLayer = LayerMask.NameToLayer("Player");
                if (playerLayer >= 0)
                    player.layer = playerLayer;

                Animator stylizedAnimator = RequireComponent<Animator>(stylized, StylizedPresetPath);
                Animator playerAnimator = RequireComponent<Animator>(player, LegacyRuntimePrefabPath);
                Dictionary<string, LegacyMountPose> legacyMountPoses = CaptureLegacyMountPoses(player.transform, playerAnimator);
                Dictionary<string, LegacyMountPose> legacyPropPoses = CaptureLegacyPropPoses(player.transform, playerAnimator);
                RuntimeAnimatorController runtimeController = playerAnimator.runtimeAnimatorController;
                playerAnimator.avatar = stylizedAnimator.avatar;
                playerAnimator.runtimeAnimatorController = runtimeController;
                playerAnimator.applyRootMotion = false;
                playerAnimator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                playerAnimator.updateMode = AnimatorUpdateMode.Normal;

                RemoveLegacyVisualChildren(player.transform);
                MoveStylizedChildrenToRuntimeRoot(stylized.transform, player.transform);
                RemoveTemporaryCape(player.transform);
                RetargetStylizedEquipment(player.transform);

                UnityEngine.Object.DestroyImmediate(stylized);
                stylized = null!;

                playerAnimator.Rebind();
                playerAnimator.Update(0f);

                EnsureRuntimeComponents(player);
                ConfigureWeaponMounts(player, playerAnimator, legacyMountPoses, legacyPropPoses);
                ValidateAvatarWeaponMountContract(player, legacyMountPoses, throwOnError: true);

                EditorUtility.SetDirty(player);
                PrefabUtility.SaveAsPrefabAsset(player, RuntimePrefabPath);
                AssetDatabase.ImportAsset(RuntimePrefabPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"[{nameof(StylizedPlayerAvatarBuilder)}] Rebuilt {RuntimePrefabPath} using {StylizedPresetPath}.");
            }
            finally
            {
                if (player != null)
                    UnityEngine.Object.DestroyImmediate(player);
                if (stylized != null)
                    UnityEngine.Object.DestroyImmediate(stylized);
            }
        }

        private static T LoadRequired<T>(string assetPath)
            where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset == null)
                throw new InvalidOperationException($"Required asset was not found: {assetPath}");
            return asset;
        }

        private static T RequireComponent<T>(GameObject gameObject, string context)
            where T : Component
        {
            T component = gameObject.GetComponent<T>();
            if (component == null)
                throw new InvalidOperationException($"{context} is missing required component {typeof(T).Name}.");
            return component;
        }

        private static void RemoveLegacyVisualChildren(Transform playerRoot)
        {
            for (int i = playerRoot.childCount - 1; i >= 0; i--)
            {
                Transform child = playerRoot.GetChild(i);
                if (string.Equals(child.name, "PlayerCameraRoot", StringComparison.Ordinal))
                    continue;

                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
        }

        private static void MoveStylizedChildrenToRuntimeRoot(Transform stylizedRoot, Transform playerRoot)
        {
            while (stylizedRoot.childCount > 0)
            {
                Transform child = stylizedRoot.GetChild(0);
                child.SetParent(playerRoot, false);
            }
        }

        private static void RetargetStylizedEquipment(Transform playerRoot)
        {
            Transform rootBone = playerRoot.Find("Root");
            if (rootBone == null)
                throw new InvalidOperationException("Stylized avatar is missing the expected top-level Root bone.");

            Equipment[] equipmentComponents = playerRoot.GetComponentsInChildren<Equipment>(includeInactive: true);
            for (int i = 0; i < equipmentComponents.Length; i++)
                equipmentComponents[i].Target = rootBone;
        }

        private static void RemoveTemporaryCape(Transform playerRoot)
        {
            Transform cape = playerRoot.Find("Hu_M_Cape_NWarrior_Bl");
            if (cape != null)
                UnityEngine.Object.DestroyImmediate(cape.gameObject);
        }

        private static void EnsureRuntimeComponents(GameObject player)
        {
            if (player.GetComponent<AvatarWeaponMounts>() == null)
                player.AddComponent<AvatarWeaponMounts>();

            if (player.GetComponent<WeaponAttachmentController>() == null)
                player.AddComponent<WeaponAttachmentController>();
        }

        private static Dictionary<string, LegacyMountPose> CaptureLegacyMountPoses(Transform root, Animator animator)
        {
            Dictionary<string, LegacyMountPose> poses = new(StringComparer.Ordinal);
            AvatarWeaponMounts? mounts = root.GetComponent<AvatarWeaponMounts>();
            if (mounts == null)
                return poses;

            IReadOnlyList<AvatarWeaponMountDefinition> definitions = mounts.MountDefinitions;
            for (int i = 0; i < definitions.Count; i++)
            {
                AvatarWeaponMountDefinition definition = definitions[i];
                if (definition == null ||
                    string.IsNullOrWhiteSpace(definition.mountId) ||
                    definition.mount == null ||
                    poses.ContainsKey(definition.mountId))
                {
                    continue;
                }

                HumanBodyBones parentBone = ResolveMountParentBone(definition.mountId);
                if (animator.GetBoneTransform(parentBone) == null)
                    continue;

                poses.Add(definition.mountId, new LegacyMountPose(
                    definition.mountId,
                    parentBone,
                    root.InverseTransformPoint(definition.mount.position),
                    Quaternion.Inverse(root.rotation) * definition.mount.rotation,
                    definition.mount.localPosition,
                    definition.mount.localRotation));
            }

            return poses;
        }

        private static Dictionary<string, LegacyMountPose> CaptureLegacyPropPoses(Transform root, Animator animator)
        {
            Dictionary<string, LegacyMountPose> poses = new(StringComparer.Ordinal);

            CaptureLegacyPropPose(poses, root, animator, HumanBodyBones.RightHand, PropSword);
            CaptureLegacyPropPose(poses, root, animator, HumanBodyBones.LeftHand, PropShield);
            CaptureLegacyPropPose(poses, root, animator, HumanBodyBones.Chest, PropSwordHolder);
            CaptureLegacyPropPose(poses, root, animator, HumanBodyBones.Chest, PropShieldHolder);
            CaptureLegacyPropPose(poses, root, animator, HumanBodyBones.RightHand, PropGreatsword);

            return poses;
        }

        private static void CaptureLegacyPropPose(
            Dictionary<string, LegacyMountPose> poses,
            Transform root,
            Animator animator,
            HumanBodyBones parentBone,
            string propName)
        {
            Transform? parent = ResolveNewMountParent(animator, parentBone);
            if (parent == null)
                return;

            Transform? prop = FindDescendant(parent, propName);
            if (prop == null || poses.ContainsKey(propName))
                return;

            poses.Add(propName, new LegacyMountPose(
                propName,
                parentBone,
                root.InverseTransformPoint(prop.position),
                Quaternion.Inverse(root.rotation) * prop.rotation,
                prop.localPosition,
                prop.localRotation));
        }

        private static HumanBodyBones ResolveMountParentBone(string mountId)
        {
            return mountId switch
            {
                AvatarWeaponMounts.OffHandMountId => HumanBodyBones.LeftHand,
                AvatarWeaponMounts.OffSheathMountId => HumanBodyBones.Chest,
                AvatarWeaponMounts.OffStowedMountId => HumanBodyBones.Chest,
                AvatarWeaponMounts.OffBackMountId => HumanBodyBones.Chest,
                AvatarWeaponMounts.MainSheathMountId => HumanBodyBones.Chest,
                AvatarWeaponMounts.MainStowedMountId => HumanBodyBones.Chest,
                AvatarWeaponMounts.MainBackMountId => HumanBodyBones.Chest,
                _ => HumanBodyBones.RightHand,
            };
        }

        private static void ConfigureWeaponMounts(
            GameObject player,
            Animator animator,
            IReadOnlyDictionary<string, LegacyMountPose> legacyMountPoses,
            IReadOnlyDictionary<string, LegacyMountPose> legacyPropPoses)
        {
            AvatarWeaponMounts mounts = RequireComponent<AvatarWeaponMounts>(player, player.name);

            Transform rightHand = RequireBone(animator, HumanBodyBones.RightHand);
            Transform leftHand = RequireBone(animator, HumanBodyBones.LeftHand);
            Transform chest = animator.GetBoneTransform(HumanBodyBones.UpperChest) ??
                animator.GetBoneTransform(HumanBodyBones.Chest) ??
                RequireBone(animator, HumanBodyBones.Spine);

            // Keep the legacy prop-node names present for authored animation clips,
            // but do not use them as semantic attachment mounts. The spawned weapon
            // prefabs already include their own Sword/Shield/weapon_r children.
            CreateLegacyPropMount(player.transform, animator, legacyPropPoses, PropSword);
            CreateLegacyPropMount(player.transform, animator, legacyPropPoses, PropShield);
            CreateLegacyPropMount(player.transform, animator, legacyPropPoses, PropSwordHolder);
            CreateLegacyPropMount(player.transform, animator, legacyPropPoses, PropShieldHolder);
            Transform? greatswordAnimatedSocket =
                CreateLegacyPropMount(player.transform, animator, legacyPropPoses, PropGreatsword) ??
                ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                    rightHand,
                    ArenaWeaponMountCalibration.GreatswordAnimatedHandSocket);

            Transform mainHand =
                CreateTransferredMount(player.transform, animator, legacyMountPoses, AvatarWeaponMounts.MainHandMountId) ??
                FindDescendant(rightHand, "Weapon_R") ??
                rightHand;
            Transform offHand =
                CreateTransferredMount(player.transform, animator, legacyMountPoses, AvatarWeaponMounts.OffHandMountId) ??
                FindDescendant(leftHand, "Weapon_Shield") ??
                FindDescendant(leftHand, "Weapon_L") ??
                leftHand;
            Transform greatswordHand =
                CreateTransferredMount(player.transform, animator, legacyMountPoses, AvatarWeaponMounts.GreatswordHandMountId) ??
                ArenaWeaponMountCalibration.CreateOrUpdateMountChild(
                    greatswordAnimatedSocket,
                    ArenaWeaponMountCalibration.GreatswordHand) ??
                mainHand;

            Transform mainBack =
                CreateTransferredMount(player.transform, animator, legacyMountPoses, AvatarWeaponMounts.MainSheathMountId) ??
                FindDescendant(chest, PropSwordHolder) ??
                FindDescendant(chest, "Back_L") ??
                FindDescendant(chest, "Back_2HL") ??
                chest;
            Transform offBack =
                CreateTransferredMount(player.transform, animator, legacyMountPoses, AvatarWeaponMounts.OffSheathMountId) ??
                FindDescendant(chest, PropShieldHolder) ??
                FindDescendant(chest, "Back_R") ??
                FindDescendant(chest, "Back_M") ??
                chest;

            mounts.SetOrReplaceMount(AvatarWeaponMounts.MainHandMountId, mainHand);
            mounts.SetOrReplaceMount(AvatarWeaponMounts.OffHandMountId, offHand);
            mounts.SetOrReplaceMount(AvatarWeaponMounts.GreatswordHandMountId, greatswordHand);

            mounts.SetOrReplaceMount(AvatarWeaponMounts.MainSheathMountId, mainBack);
            mounts.SetOrReplaceMount(AvatarWeaponMounts.MainStowedMountId, mainBack);
            mounts.SetOrReplaceMount(AvatarWeaponMounts.MainBackMountId, mainBack);

            mounts.SetOrReplaceMount(AvatarWeaponMounts.OffSheathMountId, offBack);
            mounts.SetOrReplaceMount(AvatarWeaponMounts.OffStowedMountId, offBack);
            mounts.SetOrReplaceMount(AvatarWeaponMounts.OffBackMountId, offBack);

            Debug.Log(
                $"[{nameof(StylizedPlayerAvatarBuilder)}] Mounts: " +
                $"main={BuildPath(player.transform, mainHand)}, " +
                $"off={BuildPath(player.transform, offHand)}, " +
                $"greatsword={BuildPath(player.transform, greatswordHand)}, " +
                $"mainBack={BuildPath(player.transform, mainBack)}, " +
                $"offBack={BuildPath(player.transform, offBack)}.");
        }

        private static Transform? CreateLegacyPropMount(
            Transform root,
            Animator animator,
            IReadOnlyDictionary<string, LegacyMountPose> legacyPropPoses,
            string propName,
            Transform? parentOverride = null)
        {
            if (!legacyPropPoses.TryGetValue(propName, out LegacyMountPose pose))
                return null;

            Transform? parent = parentOverride ?? ResolveNewMountParent(animator, pose.ParentBone);
            if (parent == null)
                return null;

            Transform mount = parent.Find(propName);
            if (mount == null)
            {
                GameObject mountObject = new(propName);
                mount = mountObject.transform;
                mount.SetParent(parent, false);
            }

            mount.position = root.TransformPoint(pose.RootLocalPosition);
            mount.rotation = root.rotation * pose.RootLocalRotation;
            mount.localScale = Vector3.one;
            return mount;
        }

        private static void ValidateAvatarWeaponMountContract(
            GameObject player,
            IReadOnlyDictionary<string, LegacyMountPose> legacyMountPoses,
            bool throwOnError)
        {
            AvatarWeaponMounts mounts = RequireComponent<AvatarWeaponMounts>(player, player.name);
            List<string> errors = new();

            Transform? mainHand = ResolveMountForValidation(mounts, AvatarWeaponMounts.MainHandMountId);
            Transform? offHand = ResolveMountForValidation(mounts, AvatarWeaponMounts.OffHandMountId);
            Transform? greatswordHand = ResolveMountForValidation(mounts, AvatarWeaponMounts.GreatswordHandMountId);

            if (mainHand == null)
                errors.Add($"Missing mount '{AvatarWeaponMounts.MainHandMountId}'.");
            if (offHand == null)
                errors.Add($"Missing mount '{AvatarWeaponMounts.OffHandMountId}'.");
            if (greatswordHand == null)
                errors.Add($"Missing mount '{AvatarWeaponMounts.GreatswordHandMountId}'.");

            AddIfSemanticMountUsesPropNode(
                errors,
                player.transform,
                AvatarWeaponMounts.MainHandMountId,
                mainHand,
                PropSword);
            AddIfSemanticMountUsesPropNode(
                errors,
                player.transform,
                AvatarWeaponMounts.OffHandMountId,
                offHand,
                PropShield);
            AddIfSemanticMountUsesPropNode(
                errors,
                player.transform,
                AvatarWeaponMounts.GreatswordHandMountId,
                greatswordHand,
                PropGreatsword);

            if (!legacyMountPoses.ContainsKey(AvatarWeaponMounts.GreatswordHandMountId) &&
                mainHand != null &&
                greatswordHand != null &&
                !ReferenceEquals(mainHand, greatswordHand))
            {
                errors.Add(
                    $"'{AvatarWeaponMounts.GreatswordHandMountId}' must resolve to '{AvatarWeaponMounts.MainHandMountId}' " +
                    $"because {LegacyRuntimePrefabPath} has no distinct greatsword semantic mount. " +
                    $"Resolved greatsword path: {BuildPath(player.transform, greatswordHand)}; " +
                    $"main path: {BuildPath(player.transform, mainHand)}.");
            }

            if (errors.Count == 0)
            {
                Debug.Log($"[{nameof(StylizedPlayerAvatarBuilder)}] Runtime avatar weapon mount contract passed.", player);
                return;
            }

            string message = string.Join("\n", errors);
            Debug.LogError($"[{nameof(StylizedPlayerAvatarBuilder)}] Runtime avatar weapon mount contract failed:\n{message}", player);
            if (throwOnError)
                throw new InvalidOperationException($"Runtime avatar weapon mount contract failed:\n{message}");
        }

        private static Transform? ResolveMountForValidation(AvatarWeaponMounts mounts, string mountId)
        {
            return mounts.TryGetMount(mountId, out Transform mount) ? mount : null;
        }

        private static void AddIfSemanticMountUsesPropNode(
            List<string> errors,
            Transform root,
            string mountId,
            Transform? mount,
            string propNodeName)
        {
            if (mount == null)
                return;

            if (!IsNamedOrDescendantOfName(mount, root, propNodeName))
                return;

            errors.Add(
                $"Semantic mount '{mountId}' resolves to prop-node '{propNodeName}' at " +
                $"'{BuildPath(root, mount)}'. Semantic mounts must use transferred Arena_Transferred_* mounts, " +
                "not weapon prefab prop-node names.");
        }

        private static bool IsNamedOrDescendantOfName(Transform candidate, Transform root, string name)
        {
            Transform? current = candidate;
            while (current != null && current != root)
            {
                if (string.Equals(current.name, name, StringComparison.Ordinal))
                    return true;

                current = current.parent;
            }

            return false;
        }

        private static Transform? CreateTransferredMount(
            Transform root,
            Animator animator,
            IReadOnlyDictionary<string, LegacyMountPose> legacyMountPoses,
            string mountId)
        {
            if (!legacyMountPoses.TryGetValue(mountId, out LegacyMountPose pose))
                return null;

            Transform? parent = ResolveNewMountParent(animator, pose.ParentBone);
            if (parent == null)
                return null;

            string name = $"Arena_Transferred_{SanitizeName(mountId)}";
            Transform mount = parent.Find(name);
            if (mount == null)
            {
                GameObject mountObject = new(name);
                mount = mountObject.transform;
                mount.SetParent(parent, false);
            }

            mount.position = root.TransformPoint(pose.RootLocalPosition);
            mount.rotation = root.rotation * pose.RootLocalRotation;
            mount.localScale = Vector3.one;
            return mount;
        }

        private static Transform? ResolveNewMountParent(Animator animator, HumanBodyBones bone)
        {
            if (bone == HumanBodyBones.Chest)
            {
                return animator.GetBoneTransform(HumanBodyBones.UpperChest) ??
                    animator.GetBoneTransform(HumanBodyBones.Chest) ??
                    animator.GetBoneTransform(HumanBodyBones.Spine);
            }

            return animator.GetBoneTransform(bone);
        }

        private static string SanitizeName(string value)
        {
            return value.Replace(' ', '_').Replace('/', '_');
        }

        private static Transform RequireBone(Animator animator, HumanBodyBones bone)
        {
            Transform transform = animator.GetBoneTransform(bone);
            if (transform == null)
                throw new InvalidOperationException($"Stylized avatar did not resolve humanoid bone {bone}.");
            return transform;
        }

        private static Transform? FindDescendant(Transform root, string name)
        {
            Queue<Transform> pending = new();
            pending.Enqueue(root);

            while (pending.Count > 0)
            {
                Transform current = pending.Dequeue();
                if (string.Equals(current.name, name, StringComparison.Ordinal))
                    return current;

                for (int i = 0; i < current.childCount; i++)
                    pending.Enqueue(current.GetChild(i));
            }

            return null;
        }

        private static string BuildPath(Transform root, Transform child)
        {
            Stack<string> parts = new();
            Transform? current = child;
            while (current != null && current != root)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }

        private static void RebuildHubIfOpen()
        {
            Scene activeScene = EditorSceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !string.Equals(activeScene.name, "Hub", StringComparison.Ordinal))
                return;

            EditorApplication.ExecuteMenuItem("Tools/Hub/Rebuild Authored Hub");
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using NHance.Assets.Scripts;
using NHance.Assets.Scripts.Enums;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.EditorTools
{
    public static class StylizedAvatarBaseExtractor
    {
        private const string OutputFolder = "Assets/Arena/Resources/CharacterAvatarBases";

        private readonly struct AvatarBaseDefinition
        {
            public AvatarBaseDefinition(string scenePath, string prefabName, Gender gender, string characterPrefix)
            {
                ScenePath = scenePath;
                PrefabName = prefabName;
                Gender = gender;
                CharacterPrefix = characterPrefix;
            }

            public string ScenePath { get; }
            public string PrefabName { get; }
            public Gender Gender { get; }
            public string CharacterPrefix { get; }
            public string PrefabPath => $"{OutputFolder}/{PrefabName}.prefab";
        }

        private static readonly AvatarBaseDefinition[] BaseDefinitions =
        {
            new(
                "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Scenes/Character/Human/Male/Hu_M_Demo_Newbie.unity",
                "HumanMale",
                Gender.HumanMale,
                "Hu_M"),
            new(
                "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Scenes/Character/Human/Female/Hu_F_Demo_Newbie.unity",
                "HumanFemale",
                Gender.HumanFemale,
                "Hu_F"),
            new(
                "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Scenes/Character/Orc/Male/Or_M_Demo_Newbie.unity",
                "OrcMale",
                Gender.OrcMale,
                "Or_M"),
            new(
                "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Scenes/Character/Orc/Female/Or_F_Demo_Newbie.unity",
                "OrcFemale",
                Gender.OrcFemale,
                "Or_F"),
            new(
                "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Scenes/Character/Dwarf/Male/Dw_M_Demo_Newbie.unity",
                "DwarfMale",
                Gender.DwarfMale,
                "Dw_M"),
            new(
                "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Scenes/Character/Dwarf/Female/Dw_F_Demo_Newbie.unity",
                "DwarfFemale",
                Gender.DwarfFemale,
                "Dw_F"),
        };

        [MenuItem("Arena/Avatars/Extract Stylized Customizable Avatar Bases")]
        public static void ExtractStylizedCustomizableAvatarBasesFromMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog(
                    "Stop Play Mode First",
                    "Exit Play Mode before extracting stylized avatar bases.",
                    "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            IReadOnlyList<string> extracted = ExtractStylizedCustomizableAvatarBases();
            EditorUtility.DisplayDialog(
                "Stylized Avatar Bases Extracted",
                $"Extracted {extracted.Count} customizable avatar base prefabs to {OutputFolder}.",
                "OK");
        }

        public static void ExtractStylizedCustomizableAvatarBasesBatch()
        {
            ExtractStylizedCustomizableAvatarBases();
        }

        public static IReadOnlyList<string> ExtractStylizedCustomizableAvatarBases()
        {
            EnsureFolder("Assets/Arena/Resources");
            EnsureFolder(OutputFolder);

            var extracted = new List<string>();
            Scene originalScene = EditorSceneManager.GetActiveScene();
            string originalScenePath = originalScene.IsValid() ? originalScene.path : string.Empty;

            try
            {
                foreach (AvatarBaseDefinition definition in BaseDefinitions)
                {
                    if (TryExtractBase(definition))
                        extracted.Add(definition.PrefabPath);
                }
            }
            finally
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                if (!string.IsNullOrWhiteSpace(originalScenePath) &&
                    AssetDatabase.LoadAssetAtPath<SceneAsset>(originalScenePath) != null)
                {
                    EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
                }
            }

            return extracted;
        }

        private static bool TryExtractBase(AvatarBaseDefinition definition)
        {
            GameObject? clone = null;
            try
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(definition.ScenePath) == null)
                    throw new InvalidOperationException($"Required StylizedCharacter demo scene was not found: {definition.ScenePath}");

                Scene scene = EditorSceneManager.OpenScene(definition.ScenePath, OpenSceneMode.Single);
                NHAvatar sourceAvatar = FindSingleAvatar(scene, definition);
                clone = UnityEngine.Object.Instantiate(sourceAvatar.gameObject);
                clone.name = definition.PrefabName;

                if (PrefabUtility.IsAnyPrefabInstanceRoot(clone))
                    PrefabUtility.UnpackPrefabInstance(clone, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                PrepareBaseAvatar(clone, definition);
                ValidateBaseAvatar(clone, definition);

                PrefabUtility.SaveAsPrefabAsset(clone, definition.PrefabPath);
                AssetDatabase.ImportAsset(definition.PrefabPath, ImportAssetOptions.ForceUpdate);
                Debug.Log($"[{nameof(StylizedAvatarBaseExtractor)}] Extracted {definition.PrefabPath} from {definition.ScenePath}.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[{nameof(StylizedAvatarBaseExtractor)}] Skipped {definition.PrefabName} from {definition.ScenePath}: {ex.Message}");
                return false;
            }
            finally
            {
                if (clone != null)
                    UnityEngine.Object.DestroyImmediate(clone);
            }
        }

        private static void PrepareBaseAvatar(GameObject root, AvatarBaseDefinition definition)
        {
            root.tag = "Untagged";
            root.layer = 0;

            DestroyComponentIfPresent<NHCharacterController>(root);
            DestroyComponentIfPresent<CharacterController>(root);
            DestroyComponentIfPresent<Camera>(root);
            DestroyComponentIfPresent<AudioListener>(root);

            NHAvatar avatar = RequireComponent<NHAvatar>(root, definition.PrefabPath);
            avatar.Gender = definition.Gender;
            avatar.characterPrefix = definition.CharacterPrefix;
            avatar.Auto = false;
            avatar.showAnimationControls = false;
            avatar.FoldoutBodyPartSetup = false;
            avatar.FoldoutSocketsSetup = false;
            avatar.FoldoutSocketsTransformSetup = false;
            avatar.FoldoutItemToBoneMapper = false;

            avatar.ClearItems();
            avatar.Clean();

            Animator animator = RequireComponent<Animator>(root, definition.PrefabPath);
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            animator.updateMode = AnimatorUpdateMode.Normal;
        }

        private static void ValidateBaseAvatar(GameObject root, AvatarBaseDefinition definition)
        {
            NHAvatar avatar = RequireComponent<NHAvatar>(root, definition.PrefabPath);
            RequireComponent<Animator>(root, definition.PrefabPath);

            if (avatar.rootBone == null)
                throw new InvalidOperationException($"{definition.PrefabPath} is missing NHAvatar.rootBone.");
            if (avatar.rootGeometry == null)
                throw new InvalidOperationException($"{definition.PrefabPath} is missing NHAvatar.rootGeometry.");
            if (avatar.PartsMap == null || avatar.PartsMap.Count == 0)
                throw new InvalidOperationException($"{definition.PrefabPath} is missing NHAvatar.PartsMap setup.");
            if (avatar.SocketMap == null || avatar.SocketMap._list.Count == 0)
                throw new InvalidOperationException($"{definition.PrefabPath} is missing NHAvatar.SocketMap setup.");
            if (avatar.Items == null || avatar.Items._list.Count != 0)
                throw new InvalidOperationException($"{definition.PrefabPath} should be saved as a blank base avatar with no preselected NHItems.");
            if (avatar.Cache == null || avatar.Cache._cache.Count != 0)
                throw new InvalidOperationException($"{definition.PrefabPath} should be saved with no cached spawned equipment.");
        }

        private static NHAvatar FindSingleAvatar(Scene scene, AvatarBaseDefinition definition)
        {
            var avatars = new List<NHAvatar>();
            foreach (GameObject root in scene.GetRootGameObjects())
                avatars.AddRange(root.GetComponentsInChildren<NHAvatar>(includeInactive: true));

            if (avatars.Count == 1)
                return avatars[0];

            throw new InvalidOperationException(
                $"Expected exactly one NHAvatar in {definition.ScenePath}; found {avatars.Count}.");
        }

        private static T RequireComponent<T>(GameObject root, string context)
            where T : Component
        {
            T component = root.GetComponent<T>();
            if (component == null)
                throw new InvalidOperationException($"{context} is missing required component {typeof(T).Name}.");
            return component;
        }

        private static void DestroyComponentIfPresent<T>(GameObject root)
            where T : Component
        {
            T[] components = root.GetComponentsInChildren<T>(includeInactive: true);
            for (int i = components.Length - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(components[i]);
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

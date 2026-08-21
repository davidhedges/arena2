#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Presentation;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    [InitializeOnLoad]
    internal static class IntimidateAnimationAuthoring
    {
        private const string TerrifiedFbxPath = "Assets/ThirdParty/AssetStore/Animation/Mixamo/Animations/Humanoid/terrified.fbx";
        private const string TwoHandedSwordSetPath = "Assets/Arena/Resources/CombatAnimationSets/TwoHandedSword.asset";
        private const string IntimidatedStatusKind = "INTIMIDATED";
        private const string TerrifiedClipName = "terrified";
        private const int ImporterRevision = 4;
        private const string ImporterRevisionKey = "Arena.IntimidateAnimationAuthoring.ImporterRevision";

        static IntimidateAnimationAuthoring()
        {
            EditorApplication.delayCall += AutoBindIfNeeded;
        }

        [MenuItem("Arena/Animation/Bind Intimidate Animations")]
        public static void BindIntimidateAnimations()
        {
            ConfigureTerrifiedImporter();
            AssetDatabase.ImportAsset(TerrifiedFbxPath, ImportAssetOptions.ForceUpdate);
            EditorPrefs.SetInt(ImporterRevisionKey, ImporterRevision);

            AnimationClip? terrified = LoadClip(TerrifiedFbxPath, TerrifiedClipName) ?? LoadFirstClip(TerrifiedFbxPath);
            CombatAnimationSet? set = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>(TwoHandedSwordSetPath);
            if (set == null)
            {
                Debug.LogWarning($"[IntimidateAnimationAuthoring] Missing animation set at {TwoHandedSwordSetPath}.");
                return;
            }
            if (terrified == null)
            {
                Debug.LogWarning("[IntimidateAnimationAuthoring] Missing imported Intimidate reaction clip; reimport terrified.fbx.");
                return;
            }

            CombatAnimationSetProtection.MarkTrustedMutation(set, "intimidate-animation-bind");
            Undo.RecordObject(set, "Bind Intimidate Animations");
            UpsertIntimidatedReaction(set, terrified);
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            CombatAnimationSetProtection.RecordTrustedState(set, "intimidate-animation-bind");
        }

        private static void AutoBindIfNeeded()
        {
            if (!System.IO.File.Exists(TerrifiedFbxPath)
                || !System.IO.File.Exists(TwoHandedSwordSetPath))
            {
                return;
            }

            CombatAnimationSet? set = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>(TwoHandedSwordSetPath);
            if (set == null)
                return;

            bool importerNeedsRefresh = EditorPrefs.GetInt(ImporterRevisionKey, 0) < ImporterRevision;
            if (!importerNeedsRefresh && HasIntimidatedReactionClip(set))
                return;

            BindIntimidateAnimations();
        }

        private static void ConfigureTerrifiedImporter()
        {
            if (AssetImporter.GetAtPath(TerrifiedFbxPath) is not ModelImporter importer)
                return;

            importer.importAnimation = true;
            importer.animationType = ModelImporterAnimationType.Human;
            ModelImporterClipAnimation clip = FirstClip(importer, TerrifiedClipName);
            clip.name = TerrifiedClipName;
            clip.loopTime = true;
            clip.loopPose = true;
            StabilizeHumanoidClip(clip, loop: true);
            importer.clipAnimations = new[] { clip };
        }

        private static ModelImporterClipAnimation FirstClip(ModelImporter importer, string fallbackName)
        {
            ModelImporterClipAnimation[] clips = importer.clipAnimations;
            if (clips.Length == 0)
                clips = importer.defaultClipAnimations;

            ModelImporterClipAnimation clip = clips.Length > 0
                ? CloneClip(clips[0])
                : new ModelImporterClipAnimation();
            if (string.IsNullOrWhiteSpace(clip.name))
                clip.name = fallbackName;
            TrimInitialBindPoseFrame(clip);
            return clip;
        }

        private static void TrimInitialBindPoseFrame(ModelImporterClipAnimation clip)
        {
            if (clip.lastFrame - clip.firstFrame > 2f)
                clip.firstFrame += 1f;
        }

        private static void StabilizeHumanoidClip(ModelImporterClipAnimation clip, bool loop)
        {
            clip.loopTime = loop;
            clip.lockRootRotation = true;
            clip.keepOriginalOrientation = true;
            clip.lockRootHeightY = true;
            clip.keepOriginalPositionY = true;
            clip.heightFromFeet = true;
            clip.lockRootPositionXZ = true;
            clip.keepOriginalPositionXZ = true;
        }

        private static ModelImporterClipAnimation CloneClip(ModelImporterClipAnimation source)
        {
            return new ModelImporterClipAnimation
            {
                name = source.name,
                takeName = source.takeName,
                firstFrame = source.firstFrame,
                lastFrame = source.lastFrame,
                wrapMode = source.wrapMode,
                loopTime = source.loopTime,
                loopPose = source.loopPose,
                cycleOffset = source.cycleOffset,
                lockRootRotation = source.lockRootRotation,
                keepOriginalOrientation = source.keepOriginalOrientation,
                lockRootHeightY = source.lockRootHeightY,
                keepOriginalPositionY = source.keepOriginalPositionY,
                heightFromFeet = source.heightFromFeet,
                lockRootPositionXZ = source.lockRootPositionXZ,
                keepOriginalPositionXZ = source.keepOriginalPositionXZ,
                mirror = source.mirror,
                maskType = source.maskType,
                maskSource = source.maskSource,
                additiveReferencePoseFrame = source.additiveReferencePoseFrame,
                curves = source.curves,
                events = source.events
            };
        }

        private static AnimationClip? LoadClip(string assetPath, string clipName)
        {
            return AssetDatabase.LoadAllAssetsAtPath(assetPath)
                .OfType<AnimationClip>()
                .FirstOrDefault(clip => string.Equals(clip.name, clipName, StringComparison.OrdinalIgnoreCase));
        }

        private static AnimationClip? LoadFirstClip(string assetPath)
        {
            return AssetDatabase.LoadAllAssetsAtPath(assetPath)
                .OfType<AnimationClip>()
                .FirstOrDefault(clip => !clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase));
        }

        private static void UpsertIntimidatedReaction(CombatAnimationSet set, AnimationClip terrified)
        {
            List<StatusReactionAnimationEntry> reactions =
                set.statusReactions?.ToList() ?? new List<StatusReactionAnimationEntry>();
            int index = reactions.FindIndex(entry => string.Equals(entry.StatusKindOrEmpty, IntimidatedStatusKind, StringComparison.Ordinal));
            var entry = index >= 0 ? reactions[index] : new StatusReactionAnimationEntry();
            entry.statusKind = IntimidatedStatusKind;
            entry.loop = terrified;

            if (index >= 0)
                reactions[index] = entry;
            else
                reactions.Add(entry);
            set.statusReactions = reactions.ToArray();
        }

        private static bool HasIntimidatedReactionClip(CombatAnimationSet set) =>
            set.TryGetStatusReaction(IntimidatedStatusKind, out StatusReactionAnimationEntry entry)
            && entry.loop != null;
    }
}

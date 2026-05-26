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
        private const string PointFbxPath = "Assets/ThirdParty/AssetStore/Animation/Mixamo/Animations/Humanoid/point.fbx";
        private const string TerrifiedFbxPath = "Assets/ThirdParty/AssetStore/Animation/Mixamo/Animations/Humanoid/terrified.fbx";
        private const string TwoHandedSwordSetPath = "Assets/Arena/Resources/CombatAnimationSets/TwoHandedSword.asset";
        private const string IntimidateSpellId = "INTIMIDATE";
        private const string IntimidatedStatusKind = "INTIMIDATED";
        private const string PointLeftClipName = "point_left";
        private const string TerrifiedClipName = "terrified";
        private const int ImporterRevision = 3;
        private const string ImporterRevisionKey = "Arena.IntimidateAnimationAuthoring.ImporterRevision";

        static IntimidateAnimationAuthoring()
        {
            EditorApplication.delayCall += AutoBindIfNeeded;
        }

        [MenuItem("Arena/Animation/Bind Intimidate Animations")]
        public static void BindIntimidateAnimations()
        {
            ConfigurePointImporter();
            ConfigureTerrifiedImporter();
            AssetDatabase.ImportAsset(PointFbxPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(TerrifiedFbxPath, ImportAssetOptions.ForceUpdate);
            EditorPrefs.SetInt(ImporterRevisionKey, ImporterRevision);

            AnimationClip? pointLeft = LoadClip(PointFbxPath, PointLeftClipName) ?? LoadFirstClip(PointFbxPath);
            AnimationClip? terrified = LoadClip(TerrifiedFbxPath, TerrifiedClipName) ?? LoadFirstClip(TerrifiedFbxPath);
            CombatAnimationSet? set = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>(TwoHandedSwordSetPath);
            if (set == null)
            {
                Debug.LogWarning($"[IntimidateAnimationAuthoring] Missing animation set at {TwoHandedSwordSetPath}.");
                return;
            }
            if (pointLeft == null || terrified == null)
            {
                Debug.LogWarning("[IntimidateAnimationAuthoring] Missing imported Intimidate clips; reimport point.fbx and terrified.fbx.");
                return;
            }

            CombatAnimationSetProtection.MarkTrustedMutation(set, "intimidate-animation-bind");
            Undo.RecordObject(set, "Bind Intimidate Animations");
            UpsertIntimidateSpell(set, pointLeft);
            UpsertIntimidatedReaction(set, terrified);
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            CombatAnimationSetProtection.RecordTrustedState(set, "intimidate-animation-bind");
        }

        private static void AutoBindIfNeeded()
        {
            if (!System.IO.File.Exists(PointFbxPath)
                || !System.IO.File.Exists(TerrifiedFbxPath)
                || !System.IO.File.Exists(TwoHandedSwordSetPath))
            {
                return;
            }

            CombatAnimationSet? set = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>(TwoHandedSwordSetPath);
            if (set == null)
                return;

            bool importerNeedsRefresh = EditorPrefs.GetInt(ImporterRevisionKey, 0) < ImporterRevision;
            if (!importerNeedsRefresh && HasIntimidateSpellClip(set) && HasIntimidatedReactionClip(set))
                return;

            BindIntimidateAnimations();
        }

        private static void ConfigurePointImporter()
        {
            if (AssetImporter.GetAtPath(PointFbxPath) is not ModelImporter importer)
                return;

            importer.importAnimation = true;
            importer.animationType = ModelImporterAnimationType.Human;
            ModelImporterClipAnimation source = FirstClip(importer, "point");
            source.name = "point";
            source.loopTime = false;
            StabilizeHumanoidClip(source, loop: false);

            ModelImporterClipAnimation mirrored = CloneClip(source);
            mirrored.name = PointLeftClipName;
            mirrored.mirror = true;
            mirrored.loopTime = false;
            StabilizeHumanoidClip(mirrored, loop: false);
            importer.clipAnimations = new[] { source, mirrored };
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

        private static void UpsertIntimidateSpell(CombatAnimationSet set, AnimationClip pointLeft)
        {
            List<WeaponSpellAnimationEntry> spells = set.spells?.ToList() ?? new List<WeaponSpellAnimationEntry>();
            int index = spells.FindIndex(entry => string.Equals(entry.SpellIdOrEmpty, IntimidateSpellId, StringComparison.Ordinal));
            var entry = index >= 0 ? spells[index] : new WeaponSpellAnimationEntry();
            entry.spellId = IntimidateSpellId;
            entry.ground = pointLeft;
            entry.air = pointLeft;
            entry.requiresCombatStance = true;
            entry.combatEntryMode = CombatEntryMode.AnimatedAfterCast;
            entry.playbackLayer = SpellPlaybackLayer.LeftGesture;
            entry.groundEffectTime = 0f;
            entry.airEffectTime = 0f;
            entry.lowerBodyUnlockAtSeconds = 0f;
            entry.lowerBodyBlendOutSeconds = 0f;
            entry.visualInterruptibleAtSeconds = 0f;

            if (index >= 0)
                spells[index] = entry;
            else
                spells.Add(entry);
            set.spells = spells.ToArray();
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

        private static bool HasIntimidateSpellClip(CombatAnimationSet set) =>
            set.TryGetSpellAnimation(IntimidateSpellId, out WeaponSpellAnimationEntry entry)
            && entry.ResolveClip(grounded: true) != null;

        private static bool HasIntimidatedReactionClip(CombatAnimationSet set) =>
            set.TryGetStatusReaction(IntimidatedStatusKind, out StatusReactionAnimationEntry entry)
            && entry.loop != null;
    }
}

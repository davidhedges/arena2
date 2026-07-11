#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class NpcVisualProfileTests
    {
        private const string ProfileFolder = "Assets/Arena/Content/NPC/VisualProfiles";

        [Test]
        public void EveryFirstPartyProfile_ResolvesAuthoredAnimatorStatesAndSockets()
        {
            Type profileType = RequireType("Arena.Entity.NpcVisualProfile");
            Type editorType = RequireType("Arena.Editor.NpcVisualProfileEditor");
            MethodInfo validate = editorType.GetMethod(
                "Validate",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(editorType.FullName, "Validate");
            PropertyInfo prefabProperty = profileType.GetProperty("Prefab")
                ?? throw new MissingMemberException(profileType.FullName, "Prefab");

            string[] profileGuids = AssetDatabase.FindAssets("t:NpcVisualProfile", new[] { ProfileFolder });
            Assert.That(profileGuids, Is.Not.Empty);
            foreach (string profileGuid in profileGuids.OrderBy(value => value, StringComparer.Ordinal))
            {
                string path = AssetDatabase.GUIDToAssetPath(profileGuid);
                UnityEngine.Object profile = AssetDatabase.LoadAssetAtPath(path, profileType);
                Assert.That(profile, Is.Not.Null, $"Missing profile at {path}");

                var errors = (IReadOnlyList<string>)validate.Invoke(null, new object[] { profile })!;
                Assert.That(errors, Is.Empty, $"{path}:\n{string.Join("\n", errors)}");

                var prefab = prefabProperty.GetValue(profile) as GameObject;
                Assert.That(prefab, Is.Not.Null, path);
                AssertSocketResolves(profileType, profile, prefab!, "LEFT_HAND");
                AssertSocketResolves(profileType, profile, prefab!, "TARGET");
            }
        }

        [Test]
        public void VisualCatalog_ResolvesWizardAndLichProfiles()
        {
            Type catalogType = RequireType("Arena.Entity.NpcVisualCatalog");
            UnityEngine.Object catalog = AssetDatabase.LoadAssetAtPath(
                "Assets/Arena/Resources/NpcVisualCatalog.asset",
                catalogType);
            Assert.That(catalog, Is.Not.Null);
            var errors = (ICollection)catalogType.GetMethod("ValidateEntries")!.Invoke(catalog, null)!;
            Assert.That(errors, Is.Empty);

            MethodInfo tryGetEntry = catalogType.GetMethod("TryGetEntry")!;
            AssertProfile(tryGetEntry, catalog, "SKELETON_WIZARD_GN");
            AssertProfile(tryGetEntry, catalog, "SKELETON_WIZARD_PE");
            AssertProfile(tryGetEntry, catalog, "SKELETON_WIZARD_RD");
            AssertProfile(tryGetEntry, catalog, "LICH_GN");
            AssertProfile(tryGetEntry, catalog, "LICH_CN");
            AssertProfile(tryGetEntry, catalog, "LICH_GR");
            AssertProfile(tryGetEntry, catalog, "LICH_OR");
            AssertProfile(tryGetEntry, catalog, "LICH_PE");
            AssertProfile(tryGetEntry, catalog, "LICH_RD");
            AssertProfile(tryGetEntry, catalog, "SKELETON_ARCHER_GN");
            AssertProfile(tryGetEntry, catalog, "SKELETON_ARCHER_BK");
            AssertProfile(tryGetEntry, catalog, "SKELETON_ARCHER_YE");
            AssertProfile(tryGetEntry, catalog, "HUMANOID_SCARAB_BL");
            AssertProfile(tryGetEntry, catalog, "HUMANOID_SCARAB_GN");
            AssertProfile(tryGetEntry, catalog, "HUMANOID_SCARAB_RD");
            AssertProfile(tryGetEntry, catalog, "HUMANOID_SCARAB_YE");
            AssertProfile(tryGetEntry, catalog, "SLIME_MAN_BL");
            AssertProfile(tryGetEntry, catalog, "SLIME_MAN_GN");
            AssertProfile(tryGetEntry, catalog, "SLIME_MAN_PE");
            AssertProfile(tryGetEntry, catalog, "SLIME_MAN_RD");
        }

        [Test]
        public void ExemplarProfile_AppliesExplicitMissingSocketFallbackPolicies()
        {
            Type profileType = RequireType("Arena.Entity.NpcVisualProfile");
            UnityEngine.Object profile = AssetDatabase.LoadAssetAtPath(
                "Assets/Arena/Content/NPC/VisualProfiles/SkeletonWizard_Gn_VisualProfile.asset",
                profileType);
            Assert.That(profile, Is.Not.Null);

            MethodInfo tryResolve = profileType.GetMethod("TryResolveVfxAnchor")
                ?? throw new MissingMethodException(profileType.FullName, "TryResolveVfxAnchor");
            var root = new GameObject("MissingSocketRoot");
            try
            {
                object?[] castArgs = { root, "LEFT_HAND", null, false };
                Assert.That(tryResolve.Invoke(profile, castArgs), Is.EqualTo(false));
                Assert.That(castArgs[2], Is.Null);
                Assert.That(castArgs[3], Is.EqualTo(true));

                object?[] hitArgs = { root, "TARGET", null, false };
                Assert.That(tryResolve.Invoke(profile, hitArgs), Is.EqualTo(true));
                Assert.That(hitArgs[2], Is.SameAs(root.transform));
                Assert.That(hitArgs[3], Is.EqualTo(true));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void FreezeCurrentPoseFallback_FreezesAndRestoresAnimatorSpeed()
        {
            Type profileType = RequireType("Arena.Entity.NpcVisualProfile");
            Type animationType = RequireType("Arena.Presentation.NpcAnimationController");
            var profile = (ScriptableObject)ScriptableObject.CreateInstance(profileType);
            var root = new GameObject("FreezeFallbackNpc");
            Animator animator = root.AddComponent<Animator>();
            try
            {
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("primaryAnimatorPath").stringValue = ".";
                serialized.FindProperty("hardCrowdControlFallbackPolicy").enumValueIndex = 1;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                object controller = animationType.GetMethod("Attach")!.Invoke(null, new object[] { root })!;
                animationType.GetMethod("SetVisualProfile")!.Invoke(controller, new object[] { profile });
                animator.speed = 0.65f;

                animationType.GetMethod("SetHardCrowdControl")!.Invoke(controller, new object?[] { "STUN" });
                Assert.That(animator.speed, Is.EqualTo(0f));

                animationType.GetMethod("SetHardCrowdControl")!.Invoke(controller, new object?[] { null });
                Assert.That(animator.speed, Is.EqualTo(0.65f).Within(0.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void PresentationVerticalOffset_IsExplicitProfileData()
        {
            Type profileType = RequireType("Arena.Entity.NpcVisualProfile");
            var profile = (ScriptableObject)ScriptableObject.CreateInstance(profileType);
            try
            {
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("presentationVerticalOffset").floatValue = 1.25f;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                PropertyInfo property = profileType.GetProperty("PresentationVerticalOffset")
                    ?? throw new MissingMemberException(profileType.FullName, "PresentationVerticalOffset");
                Assert.That((float)property.GetValue(profile)!, Is.EqualTo(1.25f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void SlimeManProfile_MapsBothAuthoritativeAttackAbilities()
        {
            Type profileType = RequireType("Arena.Entity.NpcVisualProfile");
            UnityEngine.Object profile = AssetDatabase.LoadAssetAtPath(
                "Assets/Arena/Content/NPC/VisualProfiles/SlimeMan_Bl_VisualProfile.asset",
                profileType);
            Assert.That(profile, Is.Not.Null);

            MethodInfo resolve = profileType.GetMethod("TryGetActionAnimationStates")
                ?? throw new MissingMethodException(profileType.FullName, "TryGetActionAnimationStates");
            AssertActionState(resolve, profile, "NPC_SLIME_MAN_HEAVY_SLAM", "Attack01");
            AssertActionState(resolve, profile, "NPC_SLIME_MAN_SLAM", "attack");
        }

        [Test]
        public void ExplicitPrimaryAnimator_DisablesRootMotionAndCompetingAnimators()
        {
            Type profileType = RequireType("Arena.Entity.NpcVisualProfile");
            Type animationType = RequireType("Arena.Presentation.NpcAnimationController");
            var profile = (ScriptableObject)ScriptableObject.CreateInstance(profileType);
            var root = new GameObject("PrimaryAnimatorNpc");
            Animator primary = root.AddComponent<Animator>();
            var child = new GameObject("CompetingAnimator");
            child.transform.SetParent(root.transform, false);
            Animator competing = child.AddComponent<Animator>();
            try
            {
                primary.applyRootMotion = true;
                competing.applyRootMotion = true;
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("primaryAnimatorPath").stringValue = ".";
                serialized.ApplyModifiedPropertiesWithoutUndo();

                object controller = animationType.GetMethod("Attach")!.Invoke(null, new object[] { root })!;
                animationType.GetMethod("SetVisualProfile")!.Invoke(controller, new object[] { profile });

                Assert.That(primary.enabled, Is.True);
                Assert.That(primary.applyRootMotion, Is.False);
                Assert.That(competing.enabled, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        private static void AssertProfile(MethodInfo tryGetEntry, UnityEngine.Object catalog, string visualId)
        {
            object?[] args = { visualId, null };
            Assert.That(tryGetEntry.Invoke(catalog, args), Is.EqualTo(true));
            object entry = args[1] ?? throw new AssertionException($"No entry returned for {visualId}");
            Assert.That(entry.GetType().GetField("profile")!.GetValue(entry), Is.Not.Null);
        }

        private static void AssertActionState(
            MethodInfo resolve,
            UnityEngine.Object profile,
            string abilityId,
            string expectedState)
        {
            object?[] args = { abilityId, null };
            Assert.That(resolve.Invoke(profile, args), Is.EqualTo(true), abilityId);
            var states = (IReadOnlyList<string>)(args[1]
                ?? throw new AssertionException($"No states returned for {abilityId}"));
            Assert.That(states, Is.EqualTo(new[] { expectedState }));
        }

        private static void AssertSocketResolves(
            Type profileType,
            UnityEngine.Object profile,
            GameObject prefab,
            string anchor)
        {
            MethodInfo tryResolve = profileType.GetMethod("TryResolveVfxAnchor")
                ?? throw new MissingMethodException(profileType.FullName, "TryResolveVfxAnchor");
            object?[] args = { prefab, anchor, null, false };
            Assert.That(tryResolve.Invoke(profile, args), Is.EqualTo(true), $"{anchor} did not resolve");
            Assert.That(args[2], Is.TypeOf<Transform>());
            Assert.That(args[3], Is.EqualTo(true));
        }

        private static Type RequireType(string fullName)
            => AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type != null)
                ?? throw new TypeLoadException(fullName);
    }
}

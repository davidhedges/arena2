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
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/SkeletonWizard_Gn_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/SkeletonWizard_Pe_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/SkeletonWizard_Rd_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/Lich_Gn_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/Lich_Cn_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/Lich_Gr_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/Lich_Or_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/Lich_Pe_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/Lich_Rd_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/SkeletonArcher_Gn_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/Abomination_Gn_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/Abomination_Gr_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/Abomination_Pe_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/HumanoidScarab_Bl_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/HumanoidScarab_Gn_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/HumanoidScarab_Rd_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/HumanoidScarab_Ye_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/SlimeMan_Bl_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/SlimeMan_Gn_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/SlimeMan_Pe_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/SlimeMan_Rd_VisualProfile.asset")]
        public void ExemplarProfile_ResolvesAuthoredAnimatorAndStates(string path)
        {
            Type profileType = RequireType("Arena.Entity.NpcVisualProfile");
            UnityEngine.Object profile = AssetDatabase.LoadAssetAtPath(path, profileType);
            Assert.That(profile, Is.Not.Null, $"Missing profile at {path}");

            Type editorType = RequireType("Arena.Editor.NpcVisualProfileEditor");
            MethodInfo validate = editorType.GetMethod(
                "Validate",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(editorType.FullName, "Validate");
            var errors = (IReadOnlyList<string>)validate.Invoke(null, new object[] { profile })!;
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            PropertyInfo prefabProperty = profileType.GetProperty("Prefab")
                ?? throw new MissingMemberException(profileType.FullName, "Prefab");
            var prefab = prefabProperty.GetValue(profile) as GameObject;
            Assert.That(prefab, Is.Not.Null);
            AssertSocketResolves(profileType, profile, prefab!, "LEFT_HAND");
            AssertSocketResolves(profileType, profile, prefab!, "TARGET");
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

        private static void AssertProfile(MethodInfo tryGetEntry, UnityEngine.Object catalog, string visualId)
        {
            object?[] args = { visualId, null };
            Assert.That(tryGetEntry.Invoke(catalog, args), Is.EqualTo(true));
            object entry = args[1] ?? throw new AssertionException($"No entry returned for {visualId}");
            Assert.That(entry.GetType().GetField("profile")!.GetValue(entry), Is.Not.Null);
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

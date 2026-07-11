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
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/Lich_Gn_VisualProfile.asset")]
        [TestCase("Assets/Arena/Content/NPC/VisualProfiles/SkeletonArcher_Gn_VisualProfile.asset")]
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
            AssertProfile(tryGetEntry, catalog, "LICH_GN");
            AssertProfile(tryGetEntry, catalog, "SKELETON_ARCHER_GN");
        }

        private static void AssertProfile(MethodInfo tryGetEntry, UnityEngine.Object catalog, string visualId)
        {
            object?[] args = { visualId, null };
            Assert.That(tryGetEntry.Invoke(catalog, args), Is.EqualTo(true));
            object entry = args[1] ?? throw new AssertionException($"No entry returned for {visualId}");
            Assert.That(entry.GetType().GetField("profile")!.GetValue(entry), Is.Not.Null);
        }

        private static Type RequireType(string fullName)
            => AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type != null)
                ?? throw new TypeLoadException(fullName);
    }
}

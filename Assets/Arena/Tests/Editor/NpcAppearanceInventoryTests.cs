#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace Arena.Tests.Editor
{
    public sealed class NpcAppearanceInventoryTests
    {
        private static readonly string[] VendorRoots =
        {
            "Assets/ThirdParty/AssetStore/Characters/KoboldPack/Prefabs",
            "Assets/ThirdParty/AssetStore/Characters/StylizedFantasyEnemyNPCBundle/Prefabs",
            "Assets/ThirdParty/AssetStore/Characters/StylizedFantasyEnemyNPCBundle2/Prefabs",
        };

        [TestCase("DeepSeaLizard_Rd2", "DEEP_SEA_LIZARD_RD_2")]
        [TestCase("Skeleton_Archer_Gn", "SKELETON_ARCHER_GN")]
        [TestCase("KoboldKnight", "KOBOLD_KNIGHT")]
        [TestCase("Attack_1H", "ATTACK_1H")]
        public void CandidateId_IsStableAndReviewable(string source, string expected)
        {
            Type windowType = RequireEditorType("Arena.Editor.NpcAppearanceInventoryWindow");
            MethodInfo candidateId = windowType.GetMethod(
                "CandidateId",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(windowType.FullName, "CandidateId");

            Assert.That(candidateId.Invoke(null, new object[] { source }), Is.EqualTo(expected));
        }

        [Test]
        public void ScanInventory_CurrentLicensedPackagesAreCompleteUniqueAndDeterministic()
        {
            if (VendorRoots.Any(root => !AssetDatabase.IsValidFolder(root)))
                Assert.Ignore("Licensed NPC vendor packages are not present on this checkout.");

            Type windowType = RequireEditorType("Arena.Editor.NpcAppearanceInventoryWindow");
            MethodInfo scan = windowType.GetMethod(
                "ScanInventory",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(windowType.FullName, "ScanInventory");

            object first = scan.Invoke(null, null)!;
            object second = scan.Invoke(null, null)!;
            Type documentType = first.GetType();
            Assert.That(ReadInt(documentType, first, "appearance_count"), Is.EqualTo(146));
            Assert.That(ReadInt(documentType, first, "family_count"), Is.EqualTo(35));

            List<string> firstPaths = ReadAppearanceField(documentType, first, "prefab_path");
            List<string> secondPaths = ReadAppearanceField(documentType, second, "prefab_path");
            List<string> appearanceIds = ReadAppearanceField(documentType, first, "appearance_id_candidate");
            Assert.That(firstPaths, Is.EqualTo(secondPaths));
            Assert.That(firstPaths, Has.Count.EqualTo(146));
            Assert.That(firstPaths.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(146));
            Assert.That(appearanceIds.All(value => !string.IsNullOrWhiteSpace(value)), Is.True);
            Assert.That(appearanceIds.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(146));
        }

        private static int ReadInt(Type type, object instance, string fieldName)
            => (int)(type.GetField(fieldName)?.GetValue(instance)
                ?? throw new MissingFieldException(type.FullName, fieldName));

        private static List<string> ReadAppearanceField(Type documentType, object document, string fieldName)
        {
            var appearances = (IEnumerable)(documentType.GetField("appearances")?.GetValue(document)
                ?? throw new MissingFieldException(documentType.FullName, "appearances"));
            var values = new List<string>();
            foreach (object entry in appearances)
            {
                values.Add((string)(entry.GetType().GetField(fieldName)?.GetValue(entry)
                    ?? throw new MissingFieldException(entry.GetType().FullName, fieldName)));
            }
            return values;
        }

        private static Type RequireEditorType(string fullName)
            => AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor").GetType(fullName, throwOnError: true)!;
    }
}

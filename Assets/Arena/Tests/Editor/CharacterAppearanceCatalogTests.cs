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
        private const string ClassOutfitCatalogPath = "Assets/Arena/Resources/CharacterAppearance/ClassOutfitCatalog.asset";

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

            object classOutfitCatalog = LoadRequiredAsset(ClassOutfitCatalogPath, "Arena.Presentation.Appearance.ClassOutfitCatalog");
            Assert.That(Entries(classOutfitCatalog).Cast<object>().Count(), Is.GreaterThan(0));
        }

        [Test]
        public void Assembler_BuildsDefaultHumanMaleAndMappedClassOutfits()
        {
            Type catalogSetType = RequireType("Arena.Presentation.Appearance.CharacterAppearanceCatalogSet");
            MethodInfo tryLoadDefault = RequireMethod(catalogSetType, "TryLoadDefault");
            object?[] loadArgs = { null, string.Empty };
            bool loaded = (bool)tryLoadDefault.Invoke(null, loadArgs)!;
            Assert.That(loaded, Is.True, (string?)loadArgs[1]);

            object catalogs = loadArgs[0]!;
            GameObject parent = new("CharacterAppearanceCatalogTests");
            try
            {
                AssertAssemblesHumanMaleOutfit(catalogs, parent.transform, "HUMAN_MALE_WARRIOR_STARTER");
                AssertAssemblesHumanMaleOutfit(catalogs, parent.transform, "HUMAN_MALE_PALADIN_STARTER");
                AssertAssemblesHumanMaleOutfit(catalogs, parent.transform, "HUMAN_MALE_ARCHER_STARTER");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
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
            AssertRuntimeBindingForOutfit("HUMAN_MALE_WARRIOR_STARTER");
            AssertRuntimeBindingForOutfit("HUMAN_MALE_PALADIN_STARTER");
            AssertRuntimeBindingForOutfit("HUMAN_MALE_ARCHER_STARTER");
        }

        [Test]
        public void RuntimeAvatarController_AppliesSavedOutfitInsteadOfClassDefault()
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

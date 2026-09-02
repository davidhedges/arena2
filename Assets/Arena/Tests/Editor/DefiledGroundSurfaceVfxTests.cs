#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class DefiledGroundSurfaceVfxTests
    {
        private const string PrefabPath =
            "Assets/Arena/Resources/CombatVFX/Area/Shadow/DefiledGroundSkullSurface.prefab";
        private const string SourceMaterialPath =
            "Assets/ThirdParty/AssetStore/Environments/StylizedMaterialsBundle/Materials/Deadlands/M_Deadlands_SkullWall.mat";
        private const string RegistryPath =
            "Assets/Arena/Resources/CombatVFX/CombatVFXRegistry.asset";

        [Test]
        public void DefiledGroundPrefab_UsesRequestedSkullWallMaterialAndGameplayRadius()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null);

            Component? surface = FindSurfaceComponent(prefab);
            Assert.That(surface, Is.Not.Null);
            var serialized = new SerializedObject(surface!);
            var sourceMaterial = serialized.FindProperty("sourceMaterial");
            var fallbackBaseMap = serialized.FindProperty("fallbackBaseMap");
            var dissolveShader = serialized.FindProperty("dissolveShader");

            Assert.That(sourceMaterial.objectReferenceValue, Is.Not.Null);
            Assert.That(
                AssetDatabase.GetAssetPath(sourceMaterial.objectReferenceValue),
                Is.EqualTo(SourceMaterialPath));
            Assert.That(fallbackBaseMap.objectReferenceValue, Is.Not.Null);
            Assert.That(
                AssetDatabase.GetAssetPath(fallbackBaseMap.objectReferenceValue),
                Does.EndWith("T_Deadlands_SkullWall_D.tga"));
            Assert.That(dissolveShader.objectReferenceValue, Is.Not.Null);
            Assert.That(
                AssetDatabase.GetAssetPath(dissolveShader.objectReferenceValue),
                Is.EqualTo("Assets/Arena/Content/Shaders/DefiledGroundSurface.shader"));
            Assert.That(serialized.FindProperty("radiusMeters").floatValue, Is.EqualTo(4.6f).Within(0.0001f));
            Assert.That(serialized.FindProperty("opacity").floatValue, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(serialized.FindProperty("dissolveSeconds").floatValue, Is.EqualTo(0.7f).Within(0.0001f));
        }

        [Test]
        public void DefiledGroundShader_PreservesTheDemoMaterialsDepthInputs()
        {
            string shader = File.ReadAllText("Assets/Arena/Content/Shaders/DefiledGroundSurface.shader");
            string presenter = File.ReadAllText(
                "Assets/Arena/Runtime/Presentation/VFX/DefiledGroundSurfaceVFX.cs");

            Assert.That(shader, Does.Contain("UniversalFragmentPBR"));
            Assert.That(shader, Does.Contain("_BumpMap"));
            Assert.That(shader, Does.Contain("_MetallicGlossMap"));
            Assert.That(shader, Does.Contain("_OcclusionMap"));
            Assert.That(shader, Does.Contain("_ParallaxMap"));
            Assert.That(presenter, Does.Contain("_mesh.RecalculateTangents()"));
            Assert.That(presenter, Does.Contain("LightProbeUsage.BlendProbes"));
            Assert.That(presenter, Does.Contain("ReflectionProbeUsage.BlendProbes"));
        }

        [Test]
        public void DefiledGroundRegistry_ResolvesOnlyTheReplacementSurfacePrefab()
        {
            UnityEngine.Object? registryAsset = AssetDatabase.LoadMainAssetAtPath(RegistryPath);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

            Assert.That(registryAsset, Is.Not.Null);
            Assert.That(prefab, Is.Not.Null);
            var registry = new SerializedObject(registryAsset!);
            SerializedProperty entries = registry.FindProperty("entries");
            UnityEngine.Object? resolvedPrefab = null;
            for (int index = 0; index < entries.arraySize; index++)
            {
                SerializedProperty entry = entries.GetArrayElementAtIndex(index);
                if (!string.Equals(
                        entry.FindPropertyRelative("vfxId").stringValue,
                        "VFX_DEFILED_GROUND_AREA_01",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                resolvedPrefab = entry.FindPropertyRelative("prefab").objectReferenceValue;
                break;
            }

            Assert.That(resolvedPrefab, Is.SameAs(prefab));
            Assert.That(prefab.GetComponentsInChildren<ParticleSystem>(true), Is.Empty);
        }

        [Test]
        public void DefiledGroundCue_IsBoundToAuthoritativePersistentAreaEnd()
        {
            string catalog = File.ReadAllText("server/src/progression_catalog.shared.json");
            int owner = catalog.IndexOf("\"owner_id\": \"SPELL_DEFILED_GROUND\"", System.StringComparison.Ordinal);
            Assert.That(owner, Is.GreaterThanOrEqualTo(0));

            string cue = catalog.Substring(owner, Mathf.Min(700, catalog.Length - owner));
            Assert.That(cue, Does.Contain("\"vfx_id\": \"VFX_DEFILED_GROUND_AREA_01\""));
            Assert.That(cue, Does.Contain("\"lifecycle\": \"UNTIL_RADIAL_EFFECT_END\""));
            Assert.That(cue, Does.Contain("\"duration_ms\": 0"));
        }

        private static Component? FindSurfaceComponent(GameObject prefab)
        {
            foreach (Component component in prefab.GetComponents<Component>())
            {
                if (string.Equals(
                        component.GetType().FullName,
                        "Arena.Presentation.VFX.DefiledGroundSurfaceVFX",
                        StringComparison.Ordinal))
                {
                    return component;
                }
            }

            return null;
        }
    }
}

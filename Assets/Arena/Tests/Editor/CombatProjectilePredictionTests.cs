#nullable enable

using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class CombatProjectilePredictionTests
    {
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        [Test]
        public void AdoptedPredictedBoomerangDoesNotHardSnapOnAuthoritativeUpdate()
        {
            Assert.That(ShouldSnapAuthoritativeVisualUpdate("BOOMERANG_CASTER", adoptedPredictedProjectile: true, movesRootWithProjectile: true), Is.False);
            Assert.That(ShouldSnapAuthoritativeVisualUpdate("BOOMERANG_CASTER", adoptedPredictedProjectile: false, movesRootWithProjectile: true), Is.True);
            Assert.That(ShouldSnapAuthoritativeVisualUpdate("TRAVELING_AREA", adoptedPredictedProjectile: true, movesRootWithProjectile: true), Is.False);
            Assert.That(ShouldSnapAuthoritativeVisualUpdate("TRAVELING_AREA", adoptedPredictedProjectile: false, movesRootWithProjectile: true), Is.True);
        }

        [Test]
        public void NonAuthoritativeMotionDoesNotHardSnap()
        {
            Assert.That(ShouldSnapAuthoritativeVisualUpdate("LINEAR", adoptedPredictedProjectile: false, movesRootWithProjectile: true), Is.False);
            Assert.That(ShouldSnapAuthoritativeVisualUpdate(string.Empty, adoptedPredictedProjectile: false, movesRootWithProjectile: true), Is.False);
        }

        [Test]
        public void GravewakeKeepsItsBakedVisualAtTheSpawnPosition()
        {
            UnityEngine.Object? registryAsset = AssetDatabase.LoadMainAssetAtPath(
                "Assets/Arena/Resources/CombatVFX/CombatVFXRegistry.asset");
            Assert.That(registryAsset, Is.Not.Null);
            var registry = new SerializedObject(registryAsset!);
            SerializedProperty entries = registry.FindProperty("entries");
            bool found = false;
            bool lockProjectileRootToSpawn = false;
            bool followAuthoritativeProjectileMotion = false;
            for (int index = 0; index < entries.arraySize; index++)
            {
                SerializedProperty entry = entries.GetArrayElementAtIndex(index);
                if (!string.Equals(
                        entry.FindPropertyRelative("vfxId").stringValue,
                        "VFX_GRAVEWAKE_BONE_WAVE_01",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                found = true;
                lockProjectileRootToSpawn = entry
                    .FindPropertyRelative("lockProjectileRootToSpawn")
                    .boolValue;
                followAuthoritativeProjectileMotion = entry
                    .FindPropertyRelative("followAuthoritativeProjectileMotion")
                    .boolValue;
                break;
            }

            Assert.That(found, Is.True);
            Assert.That(lockProjectileRootToSpawn, Is.True);
            Assert.That(followAuthoritativeProjectileMotion, Is.False);
            Assert.That(ShouldSnapAuthoritativeVisualUpdate(
                "TRAVELING_AREA",
                adoptedPredictedProjectile: false,
                movesRootWithProjectile: !lockProjectileRootToSpawn), Is.False);
        }

        [Test]
        public void CloudburstVfxRegistryReferencesTheFullAuthoredPrefab()
        {
            GameObject? cloudburst = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Arena/Resources/CombatVFX/playground/primal/Water_Healing_Rain 1.prefab");
            Assert.That(cloudburst, Is.Not.Null);

            UnityEngine.Object? registryAsset = AssetDatabase.LoadMainAssetAtPath(
                "Assets/Arena/Resources/CombatVFX/CombatVFXRegistry.asset");
            Assert.That(registryAsset, Is.Not.Null);
            var registry = new SerializedObject(registryAsset!);
            SerializedProperty entries = registry.FindProperty("entries");

            Assert.That(ResolvePrefab(entries, "VFX_CLOUDBURST_RAIN_01"), Is.SameAs(cloudburst));
        }

        [Test]
        public void FissureVfxRegistrySeparatesAuthoritativeTravelFromTerminalEruption()
        {
            GameObject? travel = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Arena/Resources/CombatVFX/playground/primal/ARPG_GroundSlams_ForwardPunch Travel.prefab");
            GameObject? impact = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Arena/Resources/CombatVFX/playground/primal/ARPG_GroundSlams_ForwardPunch Impact.prefab");
            Assert.That(travel, Is.Not.Null);
            Assert.That(impact, Is.Not.Null);

            UnityEngine.Object? registryAsset = AssetDatabase.LoadMainAssetAtPath(
                "Assets/Arena/Resources/CombatVFX/CombatVFXRegistry.asset");
            Assert.That(registryAsset, Is.Not.Null);
            var registry = new SerializedObject(registryAsset!);
            SerializedProperty entries = registry.FindProperty("entries");
            SerializedProperty travelEntry = ResolveEntry(entries, "VFX_FISSURE_TRAVEL_01");
            SerializedProperty impactEntry = ResolveEntry(entries, "VFX_FISSURE_ERUPTION_01");

            Assert.That(
                travelEntry.FindPropertyRelative("prefab").objectReferenceValue,
                Is.SameAs(travel));
            Assert.That(
                travelEntry.FindPropertyRelative("followAuthoritativeProjectileMotion").boolValue,
                Is.False);
            Assert.That(
                travelEntry.FindPropertyRelative("lockProjectileRootToSpawn").boolValue,
                Is.False);
            Assert.That(
                travelEntry.FindPropertyRelative("lingerEmittedParticles").boolValue,
                Is.True);
            Assert.That(
                impactEntry.FindPropertyRelative("prefab").objectReferenceValue,
                Is.SameAs(impact));
        }

        [Test]
        public void LockedProjectileRootIgnoresSimulationAndAuthoritativePositionUpdates()
        {
            var prefab = new GameObject("StationaryProjectileTestPrefab");
            Type vfxType = RuntimeAssembly.GetType("Arena.Presentation.VFX.WeaponProjectileVFX")
                ?? throw new InvalidOperationException("WeaponProjectileVFX not found in Assembly-CSharp.");
            ConstructorInfo constructor = vfxType
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Single();
            object vfx = constructor.Invoke(new object?[]
            {
                "stationary-test",
                new Vector3(1f, 2f, 3f),
                Vector3.forward,
                10f,
                12f,
                1f,
                prefab,
                null,
                1f,
                true,
                false,
                false,
                false,
            });

            try
            {
                FieldInfo groupField = vfxType.GetField(
                        "_group",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("WeaponProjectileVFX._group not found.");
                var group = (GameObject)groupField.GetValue(vfx)!;
                Vector3 spawnPosition = group.transform.position;

                MethodInfo tick = vfxType.GetMethod("Tick", new[] { typeof(float) })
                    ?? throw new InvalidOperationException("WeaponProjectileVFX.Tick not found.");
                MethodInfo update = vfxType.GetMethod(
                        "OnUpdate",
                        new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(bool) })
                    ?? throw new InvalidOperationException("WeaponProjectileVFX.OnUpdate not found.");
                tick.Invoke(vfx, new object[] { 0.5f });
                update.Invoke(
                    vfx,
                    new object[] { new Vector3(9f, 2f, 9f), Vector3.right, 10f, true });

                Assert.That(group.transform.position, Is.EqualTo(spawnPosition));
            }
            finally
            {
                // Runtime destruction is deferred; native EditMode fixture cleanup is immediate.
                var group = (GameObject)vfxType.GetField("_group", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(vfx)!;
                UnityEngine.Object.DestroyImmediate(group);
                vfxType.GetMethod("Dispose")!.Invoke(vfx, null);
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }

        [TestCase("VFX_FIREBALL_PROJECTILE_01", "OnImpact")]
        [TestCase("VFX_FIREBALL_PROJECTILE_01", "OnFizzle")]
        [TestCase("VFX_ICICLE_PROJECTILE_01", "OnImpact")]
        [TestCase("VFX_ICICLE_PROJECTILE_01", "OnFizzle")]
        public void CurrentProjectileBody_TravelsReconcilesAndTerminates(string vfxId, string terminalMethod)
        {
            Type registryType = RuntimeAssembly.GetType("Arena.Presentation.CombatVFXTemplateRegistry", true)!;
            object template = registryType.GetMethod("ResolveTemplate")!.Invoke(null, new object[] { vfxId })!;
            Assert.That(template, Is.Not.Null, "The current factory must resolve the authored projectile body.");
            var prefab = (GameObject)template.GetType().GetProperty("Prefab")!.GetValue(template)!;
            Assert.That(prefab, Is.Not.Null);
            Type vfxType = RuntimeAssembly.GetType("Arena.Presentation.VFX.WeaponProjectileVFX", true)!;
            object vfx = vfxType.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single().Invoke(new object?[]
            {
                "cleanup-" + vfxId, new Vector3(1f, 2f, 3f), Vector3.forward,
                10f, 100f, 1f, prefab, null, 1f, true, false, true, false,
            });
            var group = (GameObject)vfxType.GetField("_group", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vfx)!;
            MethodInfo tick = vfxType.GetMethod("Tick")!;
            try
            {
                Assert.That(group.transform.childCount, Is.GreaterThan(0));
                Assert.That((bool)tick.Invoke(vfx, new object[] { 0.1f })!, Is.True);
                Assert.That(group.transform.position.z, Is.GreaterThan(3f));
                Vector3 authoritative = new(8f, 2f, 8f);
                vfxType.GetMethod("OnUpdate", new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(bool) })!
                    .Invoke(vfx, new object[] { authoritative, Vector3.right, 10f, true });
                Assert.That(group.transform.position, Is.EqualTo(authoritative));
                vfxType.GetMethod(terminalMethod)!.Invoke(vfx, new object[] { authoritative });
                // Authored VFX Graph sweeps can finish cosmetically after the terminal event, capped at two seconds.
                Assert.That((bool)tick.Invoke(vfx, new object[] { 2.1f })!, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(group);
                vfxType.GetMethod("Dispose")!.Invoke(vfx, null);
            }
            Assert.That((bool)tick.Invoke(vfx, new object[] { 0.1f })!, Is.False);
        }

        private static bool ShouldSnapAuthoritativeVisualUpdate(
            string motionKind,
            bool adoptedPredictedProjectile,
            bool movesRootWithProjectile)
        {
            Type controller = RuntimeAssembly.GetType("Arena.Presentation.CombatProjectileVisualController")
                ?? throw new InvalidOperationException("CombatProjectileVisualController not found in Assembly-CSharp.");
            MethodInfo method = controller.GetMethod(
                    "ShouldSnapAuthoritativeVisualUpdate",
                    BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ShouldSnapAuthoritativeVisualUpdate not found.");
            return (bool)method.Invoke(
                null,
                new object[] { motionKind, adoptedPredictedProjectile, movesRootWithProjectile })!;
        }

        private static UnityEngine.Object? ResolvePrefab(SerializedProperty entries, string vfxId)
        {
            return ResolveEntry(entries, vfxId)
                .FindPropertyRelative("prefab")
                .objectReferenceValue;
        }

        private static SerializedProperty ResolveEntry(SerializedProperty entries, string vfxId)
        {
            for (int index = 0; index < entries.arraySize; index++)
            {
                SerializedProperty entry = entries.GetArrayElementAtIndex(index);
                if (string.Equals(
                        entry.FindPropertyRelative("vfxId").stringValue,
                        vfxId,
                        StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            Assert.Fail($"Missing VFX registry entry {vfxId}.");
            return null!;
        }
    }
}

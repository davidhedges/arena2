#nullable enable

using System.IO;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class ProjectileVfxPoolingTests
    {
        private const string PoolPath = "Assets/Arena/Runtime/Presentation/VFX/ProjectileVfxPool.cs";
        private const string WeaponProjectilePath = "Assets/Arena/Runtime/Presentation/VFX/WeaponProjectileVFX.cs";
        private const string ProjectileControllerPath = "Assets/Arena/Runtime/Presentation/CombatProjectileVisualController.cs";
        private const string TravelControllerPath = "Assets/Arena/Runtime/Presentation/CombatTravelVisualController.cs";
        private const string LifecycleRegistryPath = "Assets/Arena/Runtime/Presentation/CombatVFXLifecycleRegistry.cs";
        private const string VfxRegistryPath = "Assets/Arena/Runtime/Presentation/VFX/CombatVFXRegistry.cs";
        private const string VfxUtilsPath = "Assets/Arena/Runtime/Presentation/VFX/VFXUtils.cs";

        [Test]
        public void ProjectileVfxPool_ReusesProjectileBodiesUnderHiddenRoot()
        {
            string source = File.ReadAllText(PoolPath);

            Assert.That(source, Does.Contain("internal sealed class ProjectileVfxPool"));
            Assert.That(source, Does.Contain("MaxInactivePerVfxId"));
            Assert.That(source, Does.Contain("Dictionary<string, Stack<Rental>>"));
            Assert.That(source, Does.Contain("CompositionKey(template.VfxId, trailTemplate?.VfxId)"));
            Assert.That(source, Does.Contain("trailTemplate.Prefab"));
            Assert.That(source, Does.Contain("HasUnsafePoolingComponents(template.Prefab)"));
            Assert.That(source, Does.Contain("GetComponentsInChildren<VisualEffect>(true)"));
            Assert.That(source, Does.Contain("UnityEngine.Object.Instantiate(template.Prefab"));
            Assert.That(source, Does.Contain("inactive.Count >= MaxInactivePerVfxId"));
            Assert.That(source, Does.Contain("Root.SetActive(false)"));
            Assert.That(source, Does.Contain("TrailRenderer[] trails"));
            Assert.That(source, Does.Contain("ParticleSystemStopBehavior.StopEmittingAndClear"));
        }

        [Test]
        public void WeaponProjectileVfx_ReturnsPooledRentalsInsteadOfDestroyingThem()
        {
            string source = File.ReadAllText(WeaponProjectilePath);

            Assert.That(source, Does.Contain("ProjectileVfxPool.Rental? _rental"));
            Assert.That(source, Does.Contain("internal WeaponProjectileVFX("));
            Assert.That(source, Does.Contain("_rental.Return();"));
            Assert.That(source, Does.Contain("Object.Destroy(_group)"));
        }

        [Test]
        public void WeaponProjectileVfx_AppliesAuthoredLifetimeScaleFalloff()
        {
            string source = File.ReadAllText(WeaponProjectilePath);

            Assert.That(source, Does.Contain("_scaleMultiplierAtLifetimeEnd"));
            Assert.That(source, Does.Contain("ApplyLifetimeScale();"));
            Assert.That(source, Does.Contain("_traveled / _maxDistance"));
            Assert.That(source, Does.Contain("Mathf.Lerp(1f, _scaleMultiplierAtLifetimeEnd, progress)"));
        }

        [Test]
        public void AuthoritativeProjectileParticleMotion_RemovesTranslationButPreservesRotation()
        {
            string registry = File.ReadAllText(VfxRegistryPath);
            string utilities = File.ReadAllText(VfxUtilsPath);
            string pool = File.ReadAllText(PoolPath);

            Assert.That(registry, Does.Contain("followAuthoritativeProjectileMotion"));
            Assert.That(registry, Does.Contain("FollowAuthoritativeProjectileMotion"));
            Assert.That(utilities, Does.Contain("main.simulationSpace = ParticleSystemSimulationSpace.Local"));
            Assert.That(utilities, Does.Contain("velocity.enabled = false"));
            Assert.That(utilities, Does.Not.Contain("rotationOverLifetime.enabled = false"));
            Assert.That(pool, Does.Contain("ApplyAuthoritativeProjectileParticleMotion(body)"));
        }

        [Test]
        public void FollowAnchorVfx_AppliesRegistryLocalTransform()
        {
            string registry = File.ReadAllText(VfxRegistryPath);
            string lifecycle = File.ReadAllText(LifecycleRegistryPath);

            Assert.That(registry, Does.Contain("public Vector3 localPositionOffset = Vector3.zero"));
            Assert.That(registry, Does.Contain("public Vector3 LocalPositionOffset { get; }"));
            Assert.That(lifecycle, Does.Contain("if (followAnchor != null && !followsGroundPosition)"));
            Assert.That(lifecycle, Does.Contain("template.LocalPositionOffset"));
            Assert.That(lifecycle, Does.Contain("template.LocalRotation"));
            Assert.That(lifecycle, Does.Contain("SetLocalPositionAndRotation"));
        }

        [Test]
        public void ProjectileAndTravelControllers_OwnProjectilePools()
        {
            string projectileController = File.ReadAllText(ProjectileControllerPath);
            string travelController = File.ReadAllText(TravelControllerPath);

            Assert.That(projectileController, Does.Contain("private readonly ProjectileVfxPool _projectilePool"));
            Assert.That(projectileController, Does.Contain("_projectilePool.TryRent(template, trailTemplate, projectileKey)"));
            Assert.That(projectileController, Does.Contain("_projectilePool.Dispose();"));

            Assert.That(travelController, Does.Contain("private readonly ProjectileVfxPool _projectilePool"));
            Assert.That(travelController, Does.Contain("_projectilePool.TryRent(template, null, context.ActionInstanceId)"));
            Assert.That(travelController, Does.Contain("_projectilePool.Dispose();"));
        }

        [Test]
        public void ProjectileController_SmoothsAuthoritativeOrbitRephasing()
        {
            string source = File.ReadAllText(ProjectileControllerPath);

            Assert.That(source, Does.Contain("OrbitRetargetSeconds = 0.2f"));
            Assert.That(source, Does.Contain("existingOrbit.Retarget(row)"));
            Assert.That(source, Does.Contain("Mathf.DeltaAngle("));
            Assert.That(source, Does.Contain("RetargetRemainingSeconds"));
        }
    }
}

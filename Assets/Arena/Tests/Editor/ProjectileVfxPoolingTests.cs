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
    }
}

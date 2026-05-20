#nullable enable

using System;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class CombatProjectilePredictionTests
    {
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        [Test]
        public void AdoptedPredictedBoomerangDoesNotHardSnapOnAuthoritativeUpdate()
        {
            Assert.That(ShouldSnapAuthoritativeVisualUpdate("BOOMERANG_CASTER", adoptedPredictedProjectile: true), Is.False);
            Assert.That(ShouldSnapAuthoritativeVisualUpdate("BOOMERANG_CASTER", adoptedPredictedProjectile: false), Is.True);
        }

        [Test]
        public void NonAuthoritativeMotionDoesNotHardSnap()
        {
            Assert.That(ShouldSnapAuthoritativeVisualUpdate("LINEAR", adoptedPredictedProjectile: false), Is.False);
            Assert.That(ShouldSnapAuthoritativeVisualUpdate(string.Empty, adoptedPredictedProjectile: false), Is.False);
        }

        private static bool ShouldSnapAuthoritativeVisualUpdate(string motionKind, bool adoptedPredictedProjectile)
        {
            Type controller = RuntimeAssembly.GetType("Arena.Presentation.CombatProjectileVisualController")
                ?? throw new InvalidOperationException("CombatProjectileVisualController not found in Assembly-CSharp.");
            MethodInfo method = controller.GetMethod(
                    "ShouldSnapAuthoritativeVisualUpdate",
                    BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ShouldSnapAuthoritativeVisualUpdate not found.");
            return (bool)method.Invoke(null, new object[] { motionKind, adoptedPredictedProjectile })!;
        }
    }
}

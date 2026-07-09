#nullable enable

using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    /// <summary>
    /// Covers the Step 1 animation resolver core: archetype derivation from gameplay and the
    /// layered-resolution structure. Runtime types live in Assembly-CSharp, which this editor
    /// test assembly cannot reference statically, so behavior is exercised via reflection (same
    /// pattern as LocalSpellPresentationStateMachineTests).
    /// </summary>
    public sealed class SpellAnimationResolverTests
    {
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        private static string Derive(ulong castTimeMs, string behavior)
        {
            Type type = RuntimeAssembly.GetType("Arena.Presentation.SpellAnimationArchetypes", throwOnError: true)!;
            MethodInfo method = type.GetMethod("Derive", new[] { typeof(ulong), typeof(string) })!;
            return method.Invoke(null, new object[] { castTimeMs, behavior })!.ToString()!;
        }

        private static string ToPresentationMode(string archetypeName)
        {
            Type archetypeType = RuntimeAssembly.GetType("Arena.Presentation.SpellAnimationArchetype", throwOnError: true)!;
            object archetype = Enum.Parse(archetypeType, archetypeName);
            Type type = RuntimeAssembly.GetType("Arena.Presentation.SpellAnimationArchetypes", throwOnError: true)!;
            MethodInfo method = type.GetMethod("ToPresentationMode", new[] { archetypeType })!;
            return method.Invoke(null, new[] { archetype })!.ToString()!;
        }

        [Test]
        public void Derive_ChannelBehavior_IsChannel()
        {
            Assert.That(Derive(0UL, "CHANNEL"), Is.EqualTo("Channel"));
        }

        [Test]
        public void Derive_ZeroCastNonChannel_IsInstant()
        {
            // ~40 spells today: cast_time_ms 0, not casts-on-release.
            Assert.That(Derive(0UL, ""), Is.EqualTo("Instant"));
        }

        [Test]
        public void Derive_PositiveCastTime_IsCharged()
        {
            // METEOR 750, INSTANT_BEAM 1200, ICICLE 1500, GLACIAL_SPIKE 2000.
            Assert.That(Derive(750UL, ""), Is.EqualTo("Charged"));
            Assert.That(Derive(1500UL, ""), Is.EqualTo("Charged"));
        }

        [Test]
        public void Derive_InstantBeamWithCastTime_IsCharged()
        {
            // A cast-time beam is a charged beam, not a channel (Appendix C).
            Assert.That(Derive(1200UL, "INSTANT_BEAM"), Is.EqualTo("Charged"));
        }

        [Test]
        public void ToPresentationMode_MatchesDerivedArchetype()
        {
            Assert.That(ToPresentationMode("Instant"), Is.EqualTo("ReleaseOnly"));
            Assert.That(ToPresentationMode("Charged"), Is.EqualTo("HoldThenRelease"));
            Assert.That(ToPresentationMode("Channel"), Is.EqualTo("HoldOnly"));
        }

        [Test]
        public void ComposedResolver_CachesOnlySuccessesAndInvalidatesExplicitly()
        {
            Type resolverType = RuntimeAssembly.GetType(
                "Arena.Presentation.SpellCastAnimationResolver", throwOnError: true)!;
            Type setType = RuntimeAssembly.GetType(
                "Arena.Presentation.CombatAnimationSet", throwOnError: true)!;
            Type archetypeType = RuntimeAssembly.GetType(
                "Arena.Presentation.SpellAnimationArchetype", throwOnError: true)!;
            Type entryType = RuntimeAssembly.GetType(
                "Arena.Presentation.WeaponSpellAnimationEntry", throwOnError: true)!;
            MethodInfo invalidate = resolverType.GetMethod("InvalidateCache")!;
            MethodInfo resolve = resolverType.GetMethod(
                "TryResolveComposed",
                new[] { setType, typeof(string), archetypeType, entryType.MakeByRefType() })!;
            FieldInfo cacheField = resolverType.GetField(
                "ComposedEntries",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var cache = (IDictionary)cacheField.GetValue(null)!;
            UnityEngine.Object set = Resources.Load("CombatAnimationSets/TwoHandedSword", setType);
            Assert.That(set, Is.Not.Null);

            invalidate.Invoke(null, null);
            Assert.That(cache.Count, Is.Zero);

            object charged = Enum.Parse(archetypeType, "Charged");
            object?[] missingArgs = { set, "NOT_MAPPED", charged, Activator.CreateInstance(entryType) };
            Assert.That(resolve.Invoke(null, missingArgs), Is.False);
            Assert.That(cache.Count, Is.Zero, "failed resolutions must never become sticky");

            object?[] firstArgs = { set, "ICICLE", charged, Activator.CreateInstance(entryType) };
            Assert.That(resolve.Invoke(null, firstArgs), Is.True);
            Assert.That(cache.Count, Is.EqualTo(1));

            object?[] secondArgs = { set, "icicle", charged, Activator.CreateInstance(entryType) };
            Assert.That(resolve.Invoke(null, secondArgs), Is.True);
            Assert.That(cache.Count, Is.EqualTo(1), "normalized repeat should reuse the composed result");

            invalidate.Invoke(null, null);
            Assert.That(cache.Count, Is.Zero);
        }
    }
}

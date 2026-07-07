#nullable enable

using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

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
        public void Resolver_TriesExplicitEntryFirst_ForByteIdenticalLegacyBehavior()
        {
            string source = File.ReadAllText(
                "Assets/Arena/Runtime/Presentation/Animation/SpellAnimationResolver.cs");

            // Layer 1 is the explicit entry via the existing lookup — the byte-identical guarantee.
            Assert.That(source, Does.Contain("set.TryGetSpellAnimation(spellId, out"));
            Assert.That(source, Does.Contain("SpellAnimationSource.Explicit"));
            // Layers 2 and 3 fall through to templates (empty in Step 1).
            Assert.That(source, Does.Contain("TryGetWeaponArchetypeTemplate"));
            Assert.That(source, Does.Contain("TryGetArchetypeTemplate"));
        }
    }
}

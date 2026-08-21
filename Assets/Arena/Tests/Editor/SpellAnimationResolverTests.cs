#nullable enable

using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    /// <summary>
    /// Covers semantic spell cast classification, per-set family binding, fixed exceptions, and
    /// archetype-aware family composition. Runtime types are exercised through reflection because
    /// this editor test assembly cannot statically reference Assembly-CSharp.
    /// </summary>
    public sealed class SpellAnimationResolverTests
    {
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        private static Type T(string name) =>
            RuntimeAssembly.GetType($"Arena.Presentation.{name}", throwOnError: true)!;

        private static string Derive(ulong castTimeMs, string behavior)
        {
            MethodInfo method = T("SpellAnimationArchetypes").GetMethod(
                "Derive",
                new[] { typeof(ulong), typeof(string) })!;
            return method.Invoke(null, new object[] { castTimeMs, behavior })!.ToString()!;
        }

        private static string ToPresentationMode(string archetypeName)
        {
            Type archetypeType = T("SpellAnimationArchetype");
            object archetype = Enum.Parse(archetypeType, archetypeName);
            MethodInfo method = T("SpellAnimationArchetypes").GetMethod(
                "ToPresentationMode",
                new[] { archetypeType })!;
            return method.Invoke(null, new[] { archetype })!.ToString()!;
        }

        private static bool Resolve(
            UnityEngine.Object? set,
            string spellId,
            string archetypeName,
            out object entry)
        {
            Type setType = T("CombatAnimationSet");
            Type archetypeType = T("SpellAnimationArchetype");
            Type entryType = T("WeaponSpellAnimationEntry");
            MethodInfo method = T("SpellCastAnimationResolver").GetMethod(
                "TryResolve",
                new[] { setType, typeof(string), archetypeType, entryType.MakeByRefType() })!;
            object archetype = Enum.Parse(archetypeType, archetypeName);
            object?[] args = { set, spellId, archetype, Activator.CreateInstance(entryType) };
            bool resolved = (bool)method.Invoke(null, args)!;
            entry = args[3]!;
            return resolved;
        }

        private static UnityEngine.Object LoadSet(string resourceName) =>
            Resources.Load($"CombatAnimationSets/{resourceName}", T("CombatAnimationSet"));

        private static AnimationClip? Ground(object entry) =>
            (AnimationClip?)T("WeaponSpellAnimationEntry").GetField("ground")!.GetValue(entry);

        private static string MotionFor(string spellId)
        {
            Type mapType = T("SpellCastAnimationMap");
            UnityEngine.Object map = Resources.Load("SpellCastAnimationMap", mapType);
            Assert.That(map, Is.Not.Null);
            IEnumerable entries = (IEnumerable)mapType.GetProperty("Entries")!.GetValue(map)!;
            foreach (object entry in entries)
            {
                Type entryType = entry.GetType();
                string id = (string)entryType.GetField("spellId")!.GetValue(entry)!;
                if (string.Equals(id, spellId, StringComparison.OrdinalIgnoreCase))
                    return entryType.GetField("motion")!.GetValue(entry)!.ToString()!;
            }

            Assert.Fail($"No SpellCastAnimationMap entry for {spellId}.");
            return string.Empty;
        }

        private static string FamilyFor(UnityEngine.Object set, string motionName)
        {
            Type motionType = T("SpellCastMotion");
            object motion = Enum.Parse(motionType, motionName);
            object?[] args = { motion, null };
            bool found = (bool)T("CombatAnimationSet").GetMethod(
                "TryGetSpellCastFamily",
                new[] { motionType, typeof(string).MakeByRefType() })!
                .Invoke(set, args)!;
            Assert.That(found, Is.True);
            return (string)args[1]!;
        }

        [Test]
        public void Derive_ChannelBehavior_IsChannel()
        {
            Assert.That(Derive(0UL, "CHANNEL"), Is.EqualTo("Channel"));
        }

        [Test]
        public void Derive_ZeroCastNonChannel_IsInstant()
        {
            Assert.That(Derive(0UL, ""), Is.EqualTo("Instant"));
        }

        [Test]
        public void Derive_PositiveCastTime_IsCharged()
        {
            Assert.That(Derive(750UL, ""), Is.EqualTo("Charged"));
            Assert.That(Derive(1500UL, ""), Is.EqualTo("Charged"));
        }

        [Test]
        public void Derive_InstantBeamWithCastTime_IsCharged()
        {
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
        public void MotionResolver_CachesOnlySuccessesAndInvalidatesExplicitly()
        {
            Type resolverType = T("SpellCastAnimationResolver");
            MethodInfo invalidate = resolverType.GetMethod("InvalidateCache")!;
            var cache = (IDictionary)resolverType.GetField(
                "ResolvedEntries",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            UnityEngine.Object set = LoadSet("TwoHandedSword");

            invalidate.Invoke(null, null);
            Assert.That(cache.Count, Is.Zero);
            Assert.That(Resolve(set, "NOT_MAPPED", "Charged", out _), Is.False);
            Assert.That(cache.Count, Is.Zero);

            Assert.That(Resolve(set, "ICICLE", "Charged", out _), Is.True);
            Assert.That(cache.Count, Is.EqualTo(1));
            Assert.That(Resolve(set, "icicle", "Charged", out _), Is.True);
            Assert.That(cache.Count, Is.EqualTo(1));

            invalidate.Invoke(null, null);
            Assert.That(cache.Count, Is.Zero);
        }

        [Test]
        public void Greatsword_RaiseAndCallBindingsUseDistinctFamilies()
        {
            UnityEngine.Object set = LoadSet("TwoHandedSword");
            Assert.That(FamilyFor(set, "Raise"), Is.EqualTo("MagicAttackCall1H01"));
            Assert.That(FamilyFor(set, "Call"), Is.EqualTo("MagicAttackCall1H02"));
        }

        [Test]
        public void Upheaval_IsRaiseAndUsesGreatswordLeftRaiseCast()
        {
            UnityEngine.Object set = LoadSet("TwoHandedSword");
            Assert.That(MotionFor("UPHEAVAL"), Is.EqualTo("Raise"));
            Assert.That(Resolve(set, "UPHEAVAL", "Instant", out object entry), Is.True);
            Assert.That(Ground(entry)?.name, Is.EqualTo("HumanM@MagicAttackCall1H01_L - Cast"));
        }

        [Test]
        public void MagicMissile_IsCallAndUsesGreatswordLeftCallFamily()
        {
            UnityEngine.Object set = LoadSet("TwoHandedSword");
            Assert.That(MotionFor("MAGIC_MISSILE"), Is.EqualTo("Call"));
            Assert.That(Resolve(set, "MAGIC_MISSILE", "Channel", out object entry), Is.True);
            object hold = T("WeaponSpellAnimationEntry").GetField("holdOverride")!.GetValue(entry)!;
            AnimationClip? enter = (AnimationClip?)T("SpellCastHoldProfile").GetField("enter")!.GetValue(hold);
            Assert.That(enter?.name, Is.EqualTo("HumanM@MagicAttackCall1H02_L"));
        }

        [Test]
        public void BattleCry_FixedGreatswordAnimationIgnoresCombatSet()
        {
            UnityEngine.Object greatsword = LoadSet("TwoHandedSword");
            UnityEngine.Object daggers = LoadSet("Daggers");
            Assert.That(Resolve(greatsword, "BATTLE_CRY", "Instant", out object greatswordEntry), Is.True);
            Assert.That(Resolve(daggers, "BATTLE_CRY", "Instant", out object daggersEntry), Is.True);
            Assert.That(Ground(greatswordEntry)?.name, Is.EqualTo("Buff"));
            Assert.That(Ground(daggersEntry), Is.SameAs(Ground(greatswordEntry)));
        }

        [Test]
        public void Nova_UsesSpecialFamily()
        {
            UnityEngine.Object set = LoadSet("TwoHandedSword");
            Assert.That(Resolve(set, "NOVA", "Instant", out object entry), Is.True);
            Assert.That(Ground(entry)?.name, Is.EqualTo("HumanM@SpecialMagicAttack01 - Cast"));
        }

        [Test]
        public void FlamingOrb_ComposesDirectFamilyAsChargedCast()
        {
            UnityEngine.Object set = LoadSet("TwoHandedSword");
            Assert.That(Resolve(set, "FLAMING_ORB", "Charged", out object entry), Is.True);
            Type entryType = T("WeaponSpellAnimationEntry");
            object hold = entryType.GetField("holdOverride")!.GetValue(entry)!;
            Type holdType = T("SpellCastHoldProfile");
            Assert.That(Ground(entry)?.name, Is.EqualTo("HumanM@MagicAttackDirect1H01_L - Cast"));
            Assert.That(
                ((AnimationClip?)holdType.GetField("enter")!.GetValue(hold))?.name,
                Is.EqualTo("HumanM@MagicAttackDirect1H01_L"));
            Assert.That(
                ((AnimationClip?)holdType.GetField("idleLoop")!.GetValue(hold))?.name,
                Is.EqualTo("HumanM@MagicAttackDirect1H01_L - Load"));
        }
    }
}

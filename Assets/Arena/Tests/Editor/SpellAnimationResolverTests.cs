#nullable enable

using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    /// <summary>
    /// Covers shared catalog selection, per-set overrides, legacy family migration, and fixed
    /// exceptions. Runtime types are exercised through reflection because
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

        private static AnimationClip? Clip(object entry) =>
            (AnimationClip?)T("WeaponSpellAnimationEntry").GetField("clip")!.GetValue(entry);

        private static bool HasPlayableHold(UnityEngine.Object set, string spellId)
        {
            Type holdType = T("SpellCastHoldProfile");
            object?[] args = { spellId, Activator.CreateInstance(holdType) };
            return (bool)T("CombatAnimationSet").GetMethod(
                    "TryGetSpellCastHoldProfile",
                    new[] { typeof(string), holdType.MakeByRefType() })!
                .Invoke(set, args)!;
        }

        private static string AnimationIdFor(string spellId)
        {
            Type mapType = T("SpellCastAnimationMap");
            UnityEngine.Object map = Resources.Load("SpellCastAnimationMap", mapType);
            IEnumerable entries = (IEnumerable)mapType.GetProperty("Entries")!.GetValue(map)!;
            foreach (object entry in entries)
            {
                Type entryType = entry.GetType();
                string id = (string)entryType.GetField("spellId")!.GetValue(entry)!;
                if (string.Equals(id, spellId, StringComparison.OrdinalIgnoreCase))
                    return (string)entryType.GetField("animationId")!.GetValue(entry)!;
            }

            Assert.Fail($"No SpellCastAnimationMap entry for {spellId}.");
            return string.Empty;
        }

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

        private static string AssignmentFor(string spellId)
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
                    return entryType.GetField("assignmentKind")!.GetValue(entry)!.ToString()!;
            }

            Assert.Fail($"No SpellCastAnimationMap entry for {spellId}.");
            return string.Empty;
        }

        private static bool CatalogRecipeIsCompatibleWith(string animationId, string archetypeName)
        {
            Type catalogType = T("SpellCastAnimationCatalog");
            Type archetypeType = T("SpellAnimationArchetype");
            UnityEngine.Object catalog = Resources.Load("SpellCastAnimationCatalog", catalogType);
            Assert.That(catalog, Is.Not.Null);
            IEnumerable recipes = (IEnumerable)catalogType.GetProperty("Recipes")!.GetValue(catalog)!;
            foreach (object recipe in recipes)
            {
                Type recipeType = recipe.GetType();
                string recipeId = (string)recipeType.GetProperty("AnimationIdOrEmpty")!.GetValue(recipe)!;
                if (!string.Equals(recipeId, animationId, StringComparison.Ordinal))
                    continue;

                object archetype = Enum.Parse(archetypeType, archetypeName);
                return (bool)recipeType.GetMethod("IsCompatibleWith")!.Invoke(recipe, new[] { archetype })!;
            }

            Assert.Fail($"No SpellCastAnimationCatalog recipe for {animationId}.");
            return false;
        }

        private static bool IsExplicitlyNoAnimation(string spellId) =>
            (bool)T("SpellCastAnimationResolver").GetMethod(
                "IsExplicitlyNoAnimation",
                new[] { typeof(string) })!.Invoke(null, new object[] { spellId })!;

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

        private static (string Family, string ResolvedMotion) FamilyAndResolvedMotionFor(
            UnityEngine.Object set,
            string motionName)
        {
            Type motionType = T("SpellCastMotion");
            object motion = Enum.Parse(motionType, motionName);
            object?[] args = { motion, null, null };
            bool found = (bool)T("CombatAnimationSet").GetMethod(
                    "TryGetSpellCastFamily",
                    new[]
                    {
                        motionType,
                        typeof(string).MakeByRefType(),
                        motionType.MakeByRefType(),
                    })!
                .Invoke(set, args)!;
            Assert.That(found, Is.True);
            return ((string)args[1]!, args[2]!.ToString()!);
        }

        private static string OneHandedCastHandFor(UnityEngine.Object set) =>
            T("CombatAnimationSet").GetProperty("OneHandedCastHand")!.GetValue(set)!.ToString()!;

        [Test]
        public void DirectMotionSplit_PreservesSerializedDirect1HValueAndAddsDirect2H()
        {
            Type motionType = T("SpellCastMotion");
            Assert.That(Convert.ToInt32(Enum.Parse(motionType, "Direct1H")), Is.EqualTo(1));
            Assert.That(Convert.ToInt32(Enum.Parse(motionType, "Direct2H")), Is.EqualTo(6));
            Assert.That(Convert.ToInt32(Enum.Parse(motionType, "Ground")), Is.EqualTo(7));

            Type assignmentType = T("SpellCastAnimationAssignmentKind");
            Assert.That(Convert.ToInt32(Enum.Parse(assignmentType, "LegacyMotion")), Is.EqualTo(0));
            Assert.That(Convert.ToInt32(Enum.Parse(assignmentType, "Fixed")), Is.EqualTo(1));
            Assert.That(Convert.ToInt32(Enum.Parse(assignmentType, "NoAnimation")), Is.EqualTo(2));
            Assert.That(Convert.ToInt32(Enum.Parse(assignmentType, "Catalog")), Is.EqualTo(3));
        }

        [Test]
        public void RequestedSpellClassifications_MatchAuthoredMotions()
        {
            foreach ((string motion, string[] spellIds) in new[]
                     {
                         ("Direct2H", new[] { "ICICLE", "FIREBALL", "SMITE" }),
                         ("Direct1H", new[]
                         {
                             "PLAGUEBOLT", "EARTH_BLAST", "LAVA_BLAST", "TIDAL_BLAST",
                             "WIND_BLAST", "BOLT", "CAPACITOR", "CAUTERIZE", "BUFFET",
                             "FLASHFIRE", "FLASH_FREEZE", "DEEPENING_COLD", "FULMINATION",
                             "VAMPIRIC_ORB", "WITHERING_ORB",
                         }),
                         ("Call", new[] { "CLOUDBURST" }),
                         ("Raise", new[]
                         {
                             "GIGANTISM", "FLURRY", "OVERGROWTH", "WELLSPRING", "NECRO_PRISON",
                             "NECROTIC_AURA", "GRAVEBURST", "GRAVEWAKE", "DEFILED_GROUND",
                             "BENEDICTION", "DIVINE_MEND", "FLASH_OF_GRACE", "RESTORATION",
                             "SANCTUARY", "VERDANT_SPIRITS", "TAILWIND",
                         }),
                         ("Ground", new[] { "EARTHQUAKE", "FISSURE", "BLIZZARD" }),
                     })
            {
                foreach (string spellId in spellIds)
                    Assert.That(MotionFor(spellId), Is.EqualTo(motion), spellId);
            }

            Assert.That(AssignmentFor("FLAMING_ORB"), Is.EqualTo("Catalog"));
            Assert.That(AnimationIdFor("FLAMING_ORB"), Is.EqualTo("MAGE_PROJECTILE_CAST_02"));
        }

        [Test]
        public void RequestedNoAnimationSpells_AreExplicitAndDoNotResolve()
        {
            UnityEngine.Object set = LoadSet("TwoHandedSword");
            foreach (string spellId in new[]
                     {
                         "MIRROR_IMAGE", "RECALL", "MANA_SHIELD", "SHIMMER", "TRANSPOSE",
                         "BLOOD_OFFERING", "RIME", "IMMOLATION", "TELEPORT", "GLACIAL_ADVANCE",
                         "AURA_OF_RENEWAL", "MIASMA", "REAP", "COMBUSTION", "CONTAGION", "MOULT",
                         "STONE_CARAPACE",
                     })
            {
                Assert.That(AssignmentFor(spellId), Is.EqualTo("NoAnimation"), spellId);
                Assert.That(IsExplicitlyNoAnimation(spellId), Is.True, spellId);
                Assert.That(Resolve(set, spellId, "Instant", out _), Is.False, spellId);
            }
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
        public void Ground_UsesGroundFamilyAndEachSetsAssignedOneHand()
        {
            foreach ((string setName, string expectedHand) in new[]
                     {
                         ("ArcherBow", "Left"),
                         ("Daggers", "Left"),
                         ("Staff", "Left"),
                         ("SwordAndShield", "Right"),
                         ("TwoHandedSword", "Left"),
                     })
            {
                UnityEngine.Object set = LoadSet(setName);
                Assert.That(FamilyFor(set, "Ground"), Is.EqualTo("MagicAttackGround01"), setName);
                Assert.That(OneHandedCastHandFor(set), Is.EqualTo(expectedHand), setName);
            }
        }

        [Test]
        public void Direct2H_UsesExplicitDaggersAndStaffFamily()
        {
            foreach (string setName in new[] { "Daggers", "Staff" })
            {
                UnityEngine.Object set = LoadSet(setName);
                (string family, string resolvedMotion) = FamilyAndResolvedMotionFor(set, "Direct2H");
                Assert.That(family, Is.EqualTo("MagicAttackDirect2H02"), setName);
                Assert.That(resolvedMotion, Is.EqualTo("Direct2H"), setName);
            }
        }

        [Test]
        public void Direct2H_FallsBackToSetsAssignedOneHandFamilyAndHand()
        {
            foreach ((string setName, string expectedHand) in new[]
                     {
                         ("ArcherBow", "Left"),
                         ("TwoHandedSword", "Left"),
                         ("SwordAndShield", "Right"),
                     })
            {
                UnityEngine.Object set = LoadSet(setName);
                (string family, string resolvedMotion) = FamilyAndResolvedMotionFor(set, "Direct2H");
                Assert.That(family, Is.EqualTo("MagicAttackDirect1H01"), setName);
                Assert.That(resolvedMotion, Is.EqualTo("Direct1H"), setName);
                Assert.That(OneHandedCastHandFor(set), Is.EqualTo(expectedHand), setName);
            }
        }

        [Test]
        public void Upheaval_IsRaiseAndUsesGreatswordLeftRaiseCast()
        {
            UnityEngine.Object set = LoadSet("TwoHandedSword");
            Assert.That(MotionFor("UPHEAVAL"), Is.EqualTo("Raise"));
            Assert.That(Resolve(set, "UPHEAVAL", "Instant", out object entry), Is.True);
            Assert.That(Clip(entry)?.name, Is.EqualTo("HumanM@MagicAttackCall1H01_L - Cast"));
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
            Assert.That(Clip(greatswordEntry)?.name, Is.EqualTo("Buff"));
            Assert.That(Clip(daggersEntry), Is.SameAs(Clip(greatswordEntry)));
        }

        [Test]
        public void Nova_UsesSpecialFamily()
        {
            UnityEngine.Object set = LoadSet("TwoHandedSword");
            Assert.That(Resolve(set, "NOVA", "Instant", out object entry), Is.True);
            Assert.That(Clip(entry)?.name, Is.EqualTo("HumanM@SpecialMagicAttack01 - Cast"));
        }

        [Test]
        public void FlamingOrb_UsesOneGlobalMageRecipeAcrossCombatSets()
        {
            UnityEngine.Object greatsword = LoadSet("TwoHandedSword");
            UnityEngine.Object staff = LoadSet("Staff");
            Assert.That(Resolve(greatsword, "FLAMING_ORB", "Charged", out object greatswordEntry), Is.True);
            Assert.That(Resolve(staff, "FLAMING_ORB", "Charged", out object staffEntry), Is.True);
            Type entryType = T("WeaponSpellAnimationEntry");
            object hold = entryType.GetField("holdOverride")!.GetValue(greatswordEntry)!;
            Type holdType = T("SpellCastHoldProfile");
            Assert.That(Clip(greatswordEntry)?.name, Is.EqualTo("Attack_02_02"));
            Assert.That(Clip(staffEntry), Is.SameAs(Clip(greatswordEntry)));
            Assert.That(entryType.GetField("presentationMode")!.GetValue(greatswordEntry)!.ToString(), Is.EqualTo("ReleaseOnly"));
            Assert.That(holdType.GetField("enter")!.GetValue(hold), Is.Null);
            Assert.That(holdType.GetField("idleLoop")!.GetValue(hold), Is.Null);
            Assert.That(HasPlayableHold(greatsword, "FLAMING_ORB"), Is.False);
            Assert.That(HasPlayableHold(staff, "FLAMING_ORB"), Is.False);
            Assert.That(HasPlayableHold(greatsword, "ICICLE"), Is.True);
            Assert.That(HasPlayableHold(staff, "ICICLE"), Is.True);
        }

        [Test]
        public void CatalogCompatibility_MatchesTheSupportedPresentationLifecycles()
        {
            Assert.That(CatalogRecipeIsCompatibleWith("MAGE_PROJECTILE_CAST_02", "Instant"), Is.True);
            Assert.That(CatalogRecipeIsCompatibleWith("MAGE_PROJECTILE_CAST_02", "Charged"), Is.True);
            Assert.That(CatalogRecipeIsCompatibleWith("MAGE_PROJECTILE_CAST_02", "Channel"), Is.False);
            Assert.That(CatalogRecipeIsCompatibleWith("MAGE_AIMED_CAST", "Charged"), Is.True);
            Assert.That(CatalogRecipeIsCompatibleWith("MAGE_AIMED_CAST", "Channel"), Is.False);
        }

        [Test]
        public void CombatSetOverride_ReplacesOnlyThatSetsGlobalRecipe()
        {
            Type setType = T("CombatAnimationSet");
            Type overrideType = T("SpellCastAnimationOverride");
            var set = ScriptableObject.CreateInstance(setType);
            try
            {
                object animationOverride = Activator.CreateInstance(overrideType)!;
                overrideType.GetField("spellId")!.SetValue(animationOverride, "FLAMING_ORB");
                overrideType.GetField("animationId")!.SetValue(animationOverride, "MAGE_SKILL_CAST_03");
                Array overrides = Array.CreateInstance(overrideType, 1);
                overrides.SetValue(animationOverride, 0);
                setType.GetField("spellCastAnimationOverrides")!.SetValue(set, overrides);

                Assert.That(Resolve(set, "FLAMING_ORB", "Charged", out object entry), Is.True);
                Assert.That(Clip(entry)?.name, Is.EqualTo("Skill_03"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void SpellEntry_HasOneMovementStateIndependentClipAxis()
        {
            Type entryType = T("WeaponSpellAnimationEntry");
            Assert.That(entryType.GetField("clip"), Is.Not.Null);
            Assert.That(entryType.GetField("ground"), Is.Null);
            Assert.That(entryType.GetField("air"), Is.Null);
            Assert.That(entryType.GetMethod("ResolveClip", Type.EmptyTypes), Is.Not.Null);
        }
    }
}

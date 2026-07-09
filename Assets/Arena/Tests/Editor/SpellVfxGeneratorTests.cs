#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace Arena.Tests.Editor
{
    /// <summary>
    /// Covers the C# spell-VFX generator core (Arena.Presentation.SpellVfxGenerator) — the
    /// counterpart of server/src/vfx_generation.rs (design doc Appendix B; decision 10). Mirrors
    /// the load-bearing Rust tests: archetype derivation over the real spell corpus, the exhaustive
    /// correct-by-construction proof, non-vacuous rejection, and per-rule coverage of the shared
    /// Class-A checker. Runtime types live in Assembly-CSharp, which this editor test assembly
    /// cannot reference statically, so behavior is exercised via reflection (same pattern as
    /// SpellAnimationResolverTests / LocalSpellPresentationStateMachineTests).
    /// </summary>
    public sealed class SpellVfxGeneratorTests
    {
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        private static Type T(string name) => RuntimeAssembly.GetType(name, throwOnError: true)!;

        private static readonly Type GeneratorType = T("Arena.Presentation.SpellVfxGenerator");
        private static readonly Type FactsType = T("Arena.Presentation.SpellDeliveryFacts");
        private static readonly Type VfxArchetypeType = T("Arena.Presentation.SpellVfxArchetype");
        private static readonly Type VfxSlotType = T("Arena.Presentation.SpellVfxSlot");
        private static readonly Type AnimArchetypeType = T("Arena.Presentation.SpellAnimationArchetype");
        private static readonly Type CueWiringType = T("Arena.Presentation.CueWiring");
        private static readonly Type CueFieldsType = T("Arena.Presentation.CueFields");

        // ----- reflection helpers -----

        private static object Facts(
            string kind, string targeting, bool sky = false, bool proj = false, bool deferred = false)
            => Activator.CreateInstance(FactsType, new object[] { kind, targeting, sky, proj, deferred })!;

        private static object AnimMode(string name) => Enum.Parse(AnimArchetypeType, name);
        private static object VfxArch(string name) => Enum.Parse(VfxArchetypeType, name);
        private static object Slot(string name) => Enum.Parse(VfxSlotType, name);

        private static string? DeriveArchetype(object facts)
        {
            object? result = GeneratorType.GetMethod("DeriveArchetype")!.Invoke(null, new[] { facts });
            return result?.ToString();
        }

        private static object Wire(string arch, string slot, string mode, bool selfTerm, bool deferred)
            => GeneratorType.GetMethod("Wire")!.Invoke(
                null, new object[] { VfxArch(arch), Slot(slot), AnimMode(mode), selfTerm, deferred })!;

        private static string WireStr(object wiring, string prop)
            => CueWiringType.GetProperty(prop)!.GetValue(wiring)!.ToString()!;

        private static object? WireVal(object wiring, string prop)
            => CueWiringType.GetProperty(prop)!.GetValue(wiring);

        private static string? ValidateWiring(object wiring, string mode)
            => (string?)GeneratorType.GetMethod("ValidateWiring")!.Invoke(null, new object[] { wiring, AnimMode(mode) });

        private static object Fields(
            string trigger, string anchor, string attach, string role, string lifecycle,
            bool durationIsZero, bool chargedCast)
            => Activator.CreateInstance(
                CueFieldsType,
                new object[] { trigger, anchor, attach, role, lifecycle, durationIsZero, chargedCast })!;

        private static List<string> CheckRules(object fields)
        {
            var result = (IEnumerable)GeneratorType.GetMethod("CheckCueFieldRules")!.Invoke(null, new[] { fields })!;
            var names = new List<string>();
            foreach (object? v in result)
                names.Add(v!.ToString()!);
            return names;
        }

        // A stock impact burst: ONE_SHOT / DURATION with a positive duration — violates nothing.
        private static object LegalOneShot()
            => Fields("SPELL_IMPACT", "IMPACT_POINT", "SPAWN_WORLD", "ONE_SHOT", "DURATION", false, false);

        [Test]
        public void SchoolVfxSets_AreEditorOnlyAuthoringAssets()
        {
            Assert.That(RuntimeAssembly.GetType("Arena.Presentation.SchoolVfxSet"), Is.Null);
            Assert.That(
                AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor")
                    .GetType("Arena.Presentation.SchoolVfxSet", throwOnError: false),
                Is.Not.Null);

            string[] guids = AssetDatabase.FindAssets("t:SchoolVfxSet");
            Assert.That(guids, Is.Not.Empty);
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Assert.That(path, Does.StartWith("Assets/Arena/Editor/"));
                Assert.That(path, Does.Not.Contain("/Resources/"));
                Assert.That(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path), Is.Not.Null);
            }
        }

        [Test]
        public void SpellEditorGameplayLookup_UsesCatalogActionIdsWithoutPrefixGuessing()
        {
            Type dataType = AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor")
                .GetType("Arena.Editor.SpellPresentationEditorData", throwOnError: true)!;
            MethodInfo load = dataType.GetMethod(
                "LoadSpellGameplayByActionId",
                BindingFlags.Public | BindingFlags.Static)!;
            object?[] args = { null };
            var gameplay = (IDictionary)load.Invoke(null, args)!;

            Assert.That(args[0], Is.EqualTo(string.Empty));
            Assert.That(gameplay.Contains("FIREBALL"), Is.True);
            Assert.That(gameplay.Contains("ICICLE"), Is.True);
            Assert.That(gameplay.Contains("WARDING_AURA"), Is.True);
            Assert.That(gameplay.Contains("SPELL_FIREBALL"), Is.False);
            Assert.That(gameplay.Contains("PALADIN_WARDING_AURA"), Is.False);
        }

        // ----- archetype derivation, grounded in the real spell list -----

        [Test]
        public void ArchetypeDerivation_CoversTheCatalog()
        {
            Assert.That(DeriveArchetype(Facts("PROJECTILE", "TARGET")), Is.EqualTo("Projectile")); // FIREBALL
            Assert.That(DeriveArchetype(Facts("DIRECT_TARGET", "TARGET")), Is.EqualTo("TargetHit")); // GLACIAL_SPIKE
            Assert.That(DeriveArchetype(Facts("INSTANT_BEAM", "TARGET")), Is.EqualTo("Beam")); // SPELL_INSTANT_BEAM
            Assert.That(DeriveArchetype(Facts("AREA", "POINT")), Is.EqualTo("GroundAoe")); // LIGHTNING
            Assert.That(DeriveArchetype(Facts("AREA", "SELF")), Is.EqualTo("SelfNova")); // FROST_NOVA
            Assert.That(DeriveArchetype(Facts("APPLY_STATUS", "SELF")), Is.EqualTo("SelfFx")); // BLINDING_LIGHT
            Assert.That(DeriveArchetype(Facts("APPLY_STATUS", "TARGET")), Is.EqualTo("TargetHit")); // PALADIN_SACRED_FLAME

            // METEOR: AREA + sky origin -> SkyDrop, not GroundAoe.
            Assert.That(DeriveArchetype(Facts("AREA", "POINT", sky: true)), Is.EqualTo("SkyDrop"));

            // CHANNEL splits: ELECTROCUTE (no projectiles) is a beam; MAGIC_MISSILE (projectiles) is projectile.
            Assert.That(DeriveArchetype(Facts("CHANNEL", "TARGET")), Is.EqualTo("Beam"));
            Assert.That(DeriveArchetype(Facts("CHANNEL", "TARGET", proj: true)), Is.EqualTo("Projectile"));

            // AURA is supported (decision 11): all spell types generate.
            Assert.That(DeriveArchetype(Facts("AURA", "SELF")), Is.EqualTo("Aura")); // PALADIN_FERVOR
        }

        [Test]
        public void ArchetypeDerivation_UnknownKind_IsNull()
        {
            Assert.That(DeriveArchetype(Facts("SOMETHING_ELSE", "SELF")), Is.Null);
        }

        // ----- Appendix-B wiring spot checks (each grounded in a real cue) -----

        [Test]
        public void CastGlowLifecycle_IsModeDriven()
        {
            object instant = Wire("Projectile", "CastGlow", "Instant", false, false);
            Assert.That(WireStr(instant, "Lifecycle"), Is.EqualTo("DURATION")); // FIREBALL 350
            Assert.That(WireStr(instant, "Duration"), Is.EqualTo("PalettePositive"));

            object charged = Wire("Projectile", "CastGlow", "Charged", false, false);
            Assert.That(WireStr(charged, "Lifecycle"), Is.EqualTo("UNTIL_RELEASE_EVENT")); // ICICLE (Rule 11 forces this)
            Assert.That(WireStr(charged, "Duration"), Is.EqualTo("Zero"));

            object channel = Wire("Projectile", "CastGlow", "Channel", false, false);
            Assert.That(WireStr(channel, "Lifecycle"), Is.EqualTo("UNTIL_CAST_END")); // MAGIC_MISSILE
            Assert.That(WireStr(channel, "Duration"), Is.EqualTo("Zero"));
        }

        [Test]
        public void ProjectileBodyAndTravelBody_Wiring()
        {
            object body = Wire("Projectile", "ProjectileBody", "Instant", false, false);
            Assert.That(WireStr(body, "Trigger"), Is.EqualTo("SPELL_RELEASE"));
            Assert.That(WireStr(body, "VfxRole"), Is.EqualTo("PROJECTILE_BODY"));
            Assert.That(WireStr(body, "Lifecycle"), Is.EqualTo("UNTIL_TERMINAL_EVENT"));
            Assert.That(WireVal(body, "ProjectileSequenceIndex"), Is.EqualTo(0));
            Assert.That(WireStr(body, "Duration"), Is.EqualTo("Zero"));

            object travel = Wire("SkyDrop", "TravelBody", "Charged", false, false);
            Assert.That(WireStr(travel, "VfxRole"), Is.EqualTo("TRAVEL_BODY"));
            Assert.That(WireStr(travel, "Lifecycle"), Is.EqualTo("UNTIL_TERMINAL_EVENT"));
            Assert.That(WireStr(travel, "Anchor"), Is.EqualTo("Origin"));
            Assert.That(WireStr(travel, "Duration"), Is.EqualTo("Zero"));
        }

        [Test]
        public void GroundAoeImpact_SwitchesOnDeferred()
        {
            object instant = Wire("GroundAoe", "Impact", "Instant", false, false);
            Assert.That(WireStr(instant, "Trigger"), Is.EqualTo("SPELL_RELEASE")); // LIGHTNING
            Assert.That(WireStr(instant, "Anchor"), Is.EqualTo("ImpactPoint"));

            object deferred = Wire("GroundAoe", "Impact", "Instant", false, true);
            Assert.That(WireStr(deferred, "Trigger"), Is.EqualTo("AREA_IMPACT")); // ICE_SPIKES-style delayed area
            Assert.That(WireStr(deferred, "Anchor"), Is.EqualTo("AreaOrigin"));
        }

        [Test]
        public void TargetHitImpact_UsesSpellImpactTargetAnchor()
        {
            // Rule 15: TARGET anchor is only legal because the trigger is SPELL_IMPACT (post-impact).
            object hit = Wire("TargetHit", "Impact", "Charged", true, false);
            Assert.That(WireStr(hit, "Trigger"), Is.EqualTo("SPELL_IMPACT")); // GLACIAL_SPIKE
            Assert.That(WireStr(hit, "Anchor"), Is.EqualTo("Target"));
        }

        [Test]
        public void BeamLifecycle_ChannelIsUntilCastEnd()
        {
            // Decision 8: channel beam ends on ActiveCast-delete.
            object channel = Wire("Beam", "Beam", "Channel", false, false);
            Assert.That(WireStr(channel, "Lifecycle"), Is.EqualTo("UNTIL_CAST_END"));
            object charged = Wire("Beam", "Beam", "Charged", false, false);
            Assert.That(WireStr(charged, "Lifecycle"), Is.EqualTo("DURATION")); // INSTANT_BEAM 500
        }

        [Test]
        public void AuraDefaults_ToABriefGroundBurst()
        {
            // Aura buffs persist, but their cast visual is just a brief effect at the caster's feet.
            var slots = (IEnumerable)GeneratorType.GetMethod("RequestedSlots")!
                .Invoke(null, new object[] { VfxArch("Aura"), AnimMode("Instant") })!;
            var slotNames = new List<string>();
            foreach (object? s in slots) slotNames.Add(s!.ToString());
            Assert.That(slotNames, Is.EqualTo(new[] { "AuraGround" }));

            object w = Wire("Aura", "AuraGround", "Instant", false, false);
            Assert.That(WireStr(w, "Trigger"), Is.EqualTo("SPELL_RELEASE"));
            Assert.That(WireStr(w, "Anchor"), Is.EqualTo("Caster")); // follows the caster (FOLLOW_ANCHOR needs a transform)
            Assert.That(WireStr(w, "AttachMode"), Is.EqualTo("FOLLOW_ANCHOR"));
            Assert.That(WireStr(w, "VfxRole"), Is.EqualTo("ONE_SHOT"));
        }

        // ----- the whole generator is correct-by-construction against Class-A rules -----

        [Test]
        public void EveryGeneratedWiring_PassesClassARules()
        {
            MethodInfo requestedSlots = GeneratorType.GetMethod("RequestedSlots")!;
            MethodInfo wire = GeneratorType.GetMethod("Wire")!;
            MethodInfo validate = GeneratorType.GetMethod("ValidateWiring")!;

            foreach (object archetype in Enum.GetValues(VfxArchetypeType))
            foreach (object mode in Enum.GetValues(AnimArchetypeType))
            foreach (bool selfTerm in new[] { false, true })
            foreach (bool deferred in new[] { false, true })
            {
                var slots = (IEnumerable)requestedSlots.Invoke(null, new[] { archetype, mode })!;
                foreach (object slot in slots)
                {
                    object wiring = wire.Invoke(null, new object[] { archetype, slot, mode, selfTerm, deferred })!;
                    string? err = (string?)validate.Invoke(null, new object[] { wiring, mode });
                    Assert.That(
                        err, Is.Null,
                        $"generated wiring for {archetype}/{slot} mode {mode} " +
                        $"(selfTerm {selfTerm}, deferred {deferred}) violates a Class-A rule: {err}");
                }
            }
        }

        [Test]
        public void Validator_RejectsADeliberatelyIllegalWiring()
        {
            // Sanity: the checker is not vacuous. PROJECTILE_BODY on SPELL_CAST must fail (Rule 12).
            // Constructed directly (Wire never emits this) via the CueWiring ctor.
            Type anchorType = T("Arena.Presentation.CueAnchor");
            Type durationType = T("Arena.Presentation.CueDurationPolicy");
            object bad = Activator.CreateInstance(
                CueWiringType,
                new object[]
                {
                    "SPELL_CAST",
                    Enum.Parse(anchorType, "Hand"),
                    "SPAWN_WORLD",
                    "PROJECTILE_BODY",
                    "UNTIL_TERMINAL_EVENT",
                    Enum.Parse(durationType, "Zero"),
                    0, // projectileSequenceIndex (boxes into int?)
                })!;
            Assert.That(ValidateWiring(bad, "Instant"), Is.Not.Null);
        }

        // ----- the shared Class-A checker, exercised directly (the one source of truth the server
        // catalog contract also consumes, so per-rule coverage lives here too) -----

        [Test]
        public void SharedChecker_PassesALegalCue()
        {
            Assert.That(CheckRules(LegalOneShot()), Is.Empty);
        }

        [Test]
        public void Rule9_UntilReleaseEventOnlyOnSpellCast()
        {
            Assert.That(
                CheckRules(Fields("SPELL_RELEASE", "IMPACT_POINT", "SPAWN_WORLD", "ONE_SHOT", "UNTIL_RELEASE_EVENT", false, false)),
                Does.Contain("UntilReleaseEventOffCast"));
            Assert.That(
                CheckRules(Fields("SPELL_CAST", "IMPACT_POINT", "SPAWN_WORLD", "ONE_SHOT", "UNTIL_RELEASE_EVENT", false, false)),
                Does.Not.Contain("UntilReleaseEventOffCast")); // legal on SPELL_CAST
        }

        [Test]
        public void Rule10_ParticleSystemRequiresOneShotZeroDuration()
        {
            List<string> v = CheckRules(
                Fields("SPELL_IMPACT", "IMPACT_POINT", "SPAWN_WORLD", "ATTACHED", "PARTICLE_SYSTEM", false, false));
            Assert.That(v, Does.Contain("ParticleSystemBadRole"));
            Assert.That(v, Does.Contain("ParticleSystemNonZeroDuration"));
        }

        [Test]
        public void Rule11_FiresOnlyForChargedHandGlow()
        {
            // A hand-attached SPELL_CAST ATTACHED glow that is not UNTIL_RELEASE_EVENT...
            // ...is illegal for a charged spell (must be UNTIL_RELEASE_EVENT).
            Assert.That(
                CheckRules(Fields("SPELL_CAST", "LEFT_HAND", "FOLLOW_ANCHOR", "ATTACHED", "DURATION", false, true)),
                Does.Contain("CastTimeHandGlowNotUntilRelease"));
            // ...but perfectly legal for an instant spell — FIREBALL's DURATION 350 hand glow.
            Assert.That(
                CheckRules(Fields("SPELL_CAST", "LEFT_HAND", "FOLLOW_ANCHOR", "ATTACHED", "DURATION", false, false)),
                Does.Not.Contain("CastTimeHandGlowNotUntilRelease"));
        }

        [Test]
        public void Rule12_ProjectileBodyFieldLegality()
        {
            List<string> v = CheckRules(
                Fields("SPELL_CAST", "IMPACT_POINT", "FOLLOW_ANCHOR", "PROJECTILE_BODY", "UNTIL_TERMINAL_EVENT", false, false));
            Assert.That(v, Does.Contain("ProjectileBodyOffRelease"));
            Assert.That(v, Does.Contain("ProjectileBodyFollowAnchor"));
        }

        [Test]
        public void Rule13_TravelBodyFieldLegality()
        {
            List<string> v = CheckRules(
                Fields("SPELL_IMPACT", "ORIGIN", "FOLLOW_ANCHOR", "TRAVEL_BODY", "DURATION", false, false));
            Assert.That(v, Does.Contain("TravelBodyOffRelease"));
            Assert.That(v, Does.Contain("TravelBodyFollowAnchor"));
            Assert.That(v, Does.Contain("TravelBodyBadLifecycle"));
            Assert.That(v, Does.Contain("TravelBodyNonZeroDuration"));
        }

        [Test]
        public void Rule14_OneShotDurationNeedsPositiveDuration()
        {
            Assert.That(
                CheckRules(Fields("SPELL_IMPACT", "IMPACT_POINT", "SPAWN_WORLD", "ONE_SHOT", "DURATION", true, false)),
                Does.Contain("OneShotDurationZero"));
        }

        [Test]
        public void Rule15_TargetAnchorOnlyPostImpact()
        {
            Assert.That(
                CheckRules(Fields("SPELL_RELEASE", "TARGET", "SPAWN_WORLD", "ONE_SHOT", "DURATION", false, false)),
                Does.Contain("TargetAnchorPreImpact")); // pre-impact: illegal
            Assert.That(
                CheckRules(Fields("SPELL_IMPACT", "TARGET", "SPAWN_WORLD", "ONE_SHOT", "DURATION", false, false)),
                Does.Not.Contain("TargetAnchorPreImpact")); // post-impact: legal
        }
    }
}

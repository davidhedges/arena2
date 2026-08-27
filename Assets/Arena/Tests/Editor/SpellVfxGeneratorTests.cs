#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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

        private static void AssertSlotEntriesValid(SerializedProperty slots, string owner)
        {
            int characterFxSlotId = Convert.ToInt32(Enum.Parse(VfxSlotType, "CharacterFx"));
            int characterFxCount = 0;
            for (int slotIndex = 0; slotIndex < slots.arraySize; slotIndex++)
            {
                if (slots.GetArrayElementAtIndex(slotIndex)
                        .FindPropertyRelative("slot").enumValueIndex == characterFxSlotId)
                    characterFxCount++;
            }

            var slotIds = new HashSet<int>();
            var characterFxVariants = new HashSet<string>(StringComparer.Ordinal);
            for (int slotIndex = 0; slotIndex < slots.arraySize; slotIndex++)
            {
                SerializedProperty slot = slots.GetArrayElementAtIndex(slotIndex);
                int slotId = slot.FindPropertyRelative("slot").enumValueIndex;
                if (slotId == characterFxSlotId)
                {
                    string variant = slot.FindPropertyRelative("variantId").stringValue.Trim().ToUpperInvariant();
                    if (characterFxCount > 1)
                        Assert.That(variant, Is.Not.Empty, $"repeatable {owner} CharacterFx entries need variantId");
                    Assert.That(characterFxVariants.Add(variant), Is.True,
                        $"duplicate {owner} CharacterFx variant '{variant}'");
                }
                else
                {
                    Assert.That(slotIds.Add(slotId), Is.True, $"duplicate {owner} slot {slotId}");
                }
                Assert.That(slot.FindPropertyRelative("vfxId").stringValue, Is.Not.Empty);
                Assert.That(slot.FindPropertyRelative("durationMs").intValue, Is.GreaterThanOrEqualTo(0));
            }
        }

        private static string? ValidateWiring(object wiring, string mode)
            => (string?)GeneratorType.GetMethod("ValidateWiring")!.Invoke(null, new object[] { wiring, AnimMode(mode) });

        private static List<string> RequestedSlotNames(string archetype, string mode = "Instant")
        {
            var slots = (IEnumerable)GeneratorType.GetMethod("RequestedSlots")!
                .Invoke(null, new object[] { VfxArch(archetype), AnimMode(mode) })!;
            var names = new List<string>();
            foreach (object? slot in slots)
                names.Add(slot!.ToString()!);
            return names;
        }

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
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                Assert.That(asset, Is.Not.Null);
                AssertSlotEntriesValid(new SerializedObject(asset).FindProperty("slots"), path);
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

        [Test]
        public void CombatVfxRegistryGrouping_DerivesDisciplinesFromCatalogCueOwners()
        {
            Type dataType = AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor")
                .GetType("Arena.Editor.SpellPresentationEditorData", throwOnError: true)!;
            MethodInfo load = dataType.GetMethod(
                "LoadCombatVfxDisciplineUsage",
                BindingFlags.Public | BindingFlags.Static)!;
            object?[] args = { null, null };
            var usageByVfxId = (IDictionary)load.Invoke(null, args)!;

            Assert.That(args[1], Is.EqualTo(string.Empty));
            Assert.That(DisciplineIds("VFX_DARK_BURST_01_ARENA2"), Is.EqualTo(new[] { "TWO_HANDED_SWORD" }));
            Assert.That(DisciplineIds("VFX_PRIMAL_FOUR_ELEMENTS_FORWARD_01"), Is.EqualTo(new[] { "STAFF" }));
            Assert.That(DisciplineIds("VFX_ARCANE_CAST_HAND_01"), Is.EqualTo(new[] { "STAFF" }));
            Assert.That(DisciplineIds("ARROW_STANDARD"), Is.Empty);
            Assert.That(usageByVfxId.Contains("VFX_DARK_BURST_01_ARENA"), Is.False);

            List<string> DisciplineIds(string vfxId)
            {
                object usage = usageByVfxId[vfxId]!;
                var disciplines = (IEnumerable)usage.GetType().GetProperty("Disciplines")!.GetValue(usage)!;
                var ids = new List<string>();
                foreach (object discipline in disciplines)
                {
                    ids.Add((string)discipline.GetType()
                        .GetProperty("DisciplineId")!
                        .GetValue(discipline)!);
                }

                return ids;
            }
        }

        [Test]
        public void SpellVfxOverrides_AreAssetAuthoredUniqueAndOutsideSource()
        {
            string[] guids = AssetDatabase.FindAssets("t:SpellVfxOverrideCatalog");
            Assert.That(guids, Has.Length.EqualTo(1));
            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
            Assert.That(asset, Is.Not.Null);

            var serialized = new SerializedObject(asset);
            SerializedProperty entries = serialized.FindProperty("entries");
            Assert.That(entries, Is.Not.Null);
            Assert.That(entries.arraySize, Is.GreaterThanOrEqualTo(19));

            var abilityIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < entries.arraySize; i++)
            {
                SerializedProperty row = entries.GetArrayElementAtIndex(i);
                string abilityId = row.FindPropertyRelative("abilityId").stringValue.Trim().ToUpperInvariant();
                int castHand = row.FindPropertyRelative("castHand").enumValueIndex;
                SerializedProperty slots = row.FindPropertyRelative("slots");

                Assert.That(abilityId, Is.Not.Empty);
                Assert.That(abilityIds.Add(abilityId), Is.True, $"duplicate override for {abilityId}");
                Assert.That(castHand, Is.InRange(0, 2));
                Assert.That(slots.arraySize > 0 || castHand != 0, Is.True);

                AssertSlotEntriesValid(slots, abilityId);
            }

            string generatorSource = File.ReadAllText(Path.Combine(
                Directory.GetCurrentDirectory(),
                "Assets/Arena/Editor/SpellAuthoringWindow.CueGeneration.cs"));
            Assert.That(generatorSource, Does.Not.Contain("SignatureOverrides"));
            Assert.That(generatorSource, Does.Not.Contain("CastHandOverrides"));
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
            Assert.That(DeriveArchetype(Facts("EMANATION", "SELF")), Is.EqualTo("Emanation")); // NECROTIC_AURA
            Assert.That(DeriveArchetype(Facts("IMMOLATION", "SELF")), Is.EqualTo("Emanation"));
            Assert.That(DeriveArchetype(Facts("PERSISTENT_AREA", "TARGET")), Is.EqualTo("TargetField")); // BLADE_BARRIER
            Assert.That(DeriveArchetype(Facts("PERSISTENT_AREA", "POINT")), Is.EqualTo("GroundField")); // DEFILED_GROUND
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
        public void EverySpellArchetype_RequestsCastGlowAndRepeatableCharacterFx()
        {
            foreach (string archetype in Enum.GetNames(VfxArchetypeType))
            {
                List<string> slots = RequestedSlotNames(archetype);
                Assert.That(slots, Does.Contain("CastGlow"), $"{archetype} must support a hand cast glow");
                Assert.That(slots, Does.Contain("CharacterFx"), $"{archetype} must support character-attached VFX");
            }
        }

        [Test]
        public void ProjectileArchetype_RequestsMuzzleBodyTrailAndImpact()
        {
            Assert.That(RequestedSlotNames("Projectile"), Is.EqualTo(new[]
            {
                "CastGlow",
                "CharacterFx",
                "Muzzle",
                "ProjectileBody",
                "ProjectileTrail",
                "Impact",
            }));
        }

        [Test]
        public void CharacterFx_UsesCasterRootAndCastLifecycle()
        {
            object instant = Wire("GroundAoe", "CharacterFx", "Instant", false, false);
            Assert.That(WireStr(instant, "Trigger"), Is.EqualTo("SPELL_CAST"));
            Assert.That(WireStr(instant, "Anchor"), Is.EqualTo("Caster"));
            Assert.That(WireStr(instant, "AttachMode"), Is.EqualTo("FOLLOW_ANCHOR"));
            Assert.That(WireStr(instant, "VfxRole"), Is.EqualTo("ATTACHED"));
            Assert.That(WireStr(instant, "Lifecycle"), Is.EqualTo("DURATION"));

            object charged = Wire("TargetHit", "CharacterFx", "Charged", false, false);
            Assert.That(WireStr(charged, "Lifecycle"), Is.EqualTo("UNTIL_RELEASE_EVENT"));
            object channel = Wire("Beam", "CharacterFx", "Channel", false, false);
            Assert.That(WireStr(channel, "Lifecycle"), Is.EqualTo("UNTIL_CAST_END"));
        }

        [Test]
        public void Muzzle_IsAProjectileReleaseOneShotAtTheCastHand()
        {
            object muzzle = Wire("Projectile", "Muzzle", "Instant", true, false);
            Assert.That(WireStr(muzzle, "Trigger"), Is.EqualTo("SPELL_RELEASE"));
            Assert.That(WireStr(muzzle, "Anchor"), Is.EqualTo("Hand"));
            Assert.That(WireStr(muzzle, "AttachMode"), Is.EqualTo("SPAWN_WORLD"));
            Assert.That(WireStr(muzzle, "VfxRole"), Is.EqualTo("ONE_SHOT"));
            Assert.That(WireStr(muzzle, "Lifecycle"), Is.EqualTo("PARTICLE_SYSTEM"));
        }

        [Test]
        public void ProjectileBodyTrailAndTravelBody_Wiring()
        {
            object body = Wire("Projectile", "ProjectileBody", "Instant", false, false);
            Assert.That(WireStr(body, "Trigger"), Is.EqualTo("SPELL_RELEASE"));
            Assert.That(WireStr(body, "VfxRole"), Is.EqualTo("PROJECTILE_BODY"));
            Assert.That(WireStr(body, "Lifecycle"), Is.EqualTo("UNTIL_TERMINAL_EVENT"));
            Assert.That(WireVal(body, "ProjectileSequenceIndex"), Is.EqualTo(0));
            Assert.That(WireStr(body, "Duration"), Is.EqualTo("Zero"));

            object trail = Wire("Projectile", "ProjectileTrail", "Instant", false, false);
            Assert.That(WireStr(trail, "Trigger"), Is.EqualTo("SPELL_RELEASE"));
            Assert.That(WireStr(trail, "VfxRole"), Is.EqualTo("PROJECTILE_TRAIL"));
            Assert.That(WireStr(trail, "Lifecycle"), Is.EqualTo("UNTIL_TERMINAL_EVENT"));
            Assert.That(WireVal(trail, "ProjectileSequenceIndex"), Is.EqualTo(0));
            Assert.That(WireStr(trail, "Duration"), Is.EqualTo("Zero"));

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
        public void DeferredSelfNovaBurst_AlignsToCastFacing()
        {
            object burst = Wire("SelfNova", "Burst", "Instant", true, true);
            Assert.That(WireStr(burst, "Trigger"), Is.EqualTo("AREA_IMPACT"));
            Assert.That(WireStr(burst, "Anchor"), Is.EqualTo("AreaOrigin"));
            Assert.That(WireStr(burst, "AttachMode"), Is.EqualTo("WORLD_ALIGNED_TO_FACING"));
            Assert.That(WireStr(burst, "Lifecycle"), Is.EqualTo("PARTICLE_SYSTEM"));
        }

        [Test]
        public void TargetHitImpact_UsesSpellImpactPointAnchor()
        {
            object hit = Wire("TargetHit", "Impact", "Charged", true, false);
            Assert.That(WireStr(hit, "Trigger"), Is.EqualTo("SPELL_IMPACT")); // GLACIAL_SPIKE
            Assert.That(WireStr(hit, "Anchor"), Is.EqualTo("ImpactPoint"));
        }

        [Test]
        public void TargetAttachment_FollowsTheConfirmedTargetsAnimatedBackSocket()
        {
            Assert.That(RequestedSlotNames("TargetHit"), Does.Contain("TargetAttachment"));

            object attachment = Wire("TargetHit", "TargetAttachment", "Instant", false, false);
            Assert.That(WireStr(attachment, "Trigger"), Is.EqualTo("SPELL_IMPACT"));
            Assert.That(WireStr(attachment, "Anchor"), Is.EqualTo("TargetBack"));
            Assert.That(WireStr(attachment, "AttachMode"), Is.EqualTo("FOLLOW_ANCHOR"));
            Assert.That(WireStr(attachment, "VfxRole"), Is.EqualTo("ATTACHED"));
            Assert.That(WireStr(attachment, "Lifecycle"), Is.EqualTo("DURATION"));
            Assert.That(WireStr(attachment, "Duration"), Is.EqualTo("PalettePositive"));
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
            Assert.That(RequestedSlotNames("Aura"),
                Is.EqualTo(new[] { "CastGlow", "CharacterFx", "AuraGround" }));

            object w = Wire("Aura", "AuraGround", "Instant", false, false);
            Assert.That(WireStr(w, "Trigger"), Is.EqualTo("SPELL_RELEASE"));
            Assert.That(WireStr(w, "Anchor"), Is.EqualTo("Caster")); // follows the caster (FOLLOW_ANCHOR needs a transform)
            Assert.That(WireStr(w, "AttachMode"), Is.EqualTo("FOLLOW_ANCHOR"));
            Assert.That(WireStr(w, "VfxRole"), Is.EqualTo("ONE_SHOT"));
        }

        [Test]
        public void EmanationDefaults_ToAPersistentCasterField()
        {
            Assert.That(RequestedSlotNames("Emanation"),
                Is.EqualTo(new[]
                {
                    "CastGlow",
                    "CharacterFx",
                    "PersistentField",
                    "PersistentCharacterFx",
                    "MaxStackCharacterFx",
                }));

            object w = Wire("Emanation", "PersistentField", "Instant", false, false);
            Assert.That(WireStr(w, "Trigger"), Is.EqualTo("EMANATION_ACTIVE"));
            Assert.That(WireStr(w, "Anchor"), Is.EqualTo("Caster"));
            Assert.That(WireStr(w, "AttachMode"), Is.EqualTo("FOLLOW_GROUND_POSITION"));
            Assert.That(WireStr(w, "VfxRole"), Is.EqualTo("ATTACHED"));
            Assert.That(WireStr(w, "Lifecycle"), Is.EqualTo("UNTIL_RADIAL_EFFECT_END"));
            Assert.That(WireStr(w, "Duration"), Is.EqualTo("Zero"));
        }

        [Test]
        public void EmanationStackCharacterFx_UseMutuallyExclusivePersistentTriggers()
        {
            object active = Wire("Emanation", "PersistentCharacterFx", "Instant", false, false);
            Assert.That(WireStr(active, "Trigger"), Is.EqualTo("EMANATION_ACTIVE"));
            Assert.That(WireStr(active, "Anchor"), Is.EqualTo("Caster"));
            Assert.That(WireStr(active, "AttachMode"), Is.EqualTo("FOLLOW_ANCHOR"));
            Assert.That(WireStr(active, "VfxRole"), Is.EqualTo("ATTACHED"));
            Assert.That(WireStr(active, "Lifecycle"), Is.EqualTo("UNTIL_RADIAL_EFFECT_END"));

            object max = Wire("Emanation", "MaxStackCharacterFx", "Instant", false, false);
            Assert.That(WireStr(max, "Trigger"), Is.EqualTo("EMANATION_MAX_STACKS"));
            Assert.That(WireStr(max, "Anchor"), Is.EqualTo("Caster"));
            Assert.That(WireStr(max, "AttachMode"), Is.EqualTo("FOLLOW_ANCHOR"));
            Assert.That(WireStr(max, "VfxRole"), Is.EqualTo("ATTACHED"));
            Assert.That(WireStr(max, "Lifecycle"), Is.EqualTo("UNTIL_RADIAL_EFFECT_END"));
        }

        [Test]
        public void TargetFieldDefaults_ToAPersistentTargetField()
        {
            Assert.That(RequestedSlotNames("TargetField"),
                Is.EqualTo(new[] { "CastGlow", "CharacterFx", "PersistentField" }));

            object w = Wire("TargetField", "PersistentField", "Instant", false, false);
            Assert.That(WireStr(w, "Trigger"), Is.EqualTo("SPELL_IMPACT"));
            Assert.That(WireStr(w, "Anchor"), Is.EqualTo("Target"));
            Assert.That(WireStr(w, "AttachMode"), Is.EqualTo("FOLLOW_ANCHOR"));
            Assert.That(WireStr(w, "VfxRole"), Is.EqualTo("ATTACHED"));
            Assert.That(WireStr(w, "Lifecycle"), Is.EqualTo("DURATION"));
            Assert.That(WireStr(w, "Duration"), Is.EqualTo("PalettePositive"));
        }

        [Test]
        public void GroundFieldDefaults_ToAPersistentWorldPointField()
        {
            Assert.That(RequestedSlotNames("GroundField"),
                Is.EqualTo(new[] { "CastGlow", "CharacterFx", "PersistentField" }));

            object w = Wire("GroundField", "PersistentField", "Instant", false, false);
            Assert.That(WireStr(w, "Trigger"), Is.EqualTo("SPELL_IMPACT"));
            Assert.That(WireStr(w, "Anchor"), Is.EqualTo("ImpactPoint"));
            Assert.That(WireStr(w, "AttachMode"), Is.EqualTo("SPAWN_WORLD"));
            Assert.That(WireStr(w, "VfxRole"), Is.EqualTo("ATTACHED"));
            Assert.That(WireStr(w, "Lifecycle"), Is.EqualTo("DURATION"));
            Assert.That(WireStr(w, "Duration"), Is.EqualTo("PalettePositive"));
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
        public void Rule12_ProjectileTrailFieldLegality()
        {
            List<string> v = CheckRules(
                Fields("SPELL_CAST", "IMPACT_POINT", "FOLLOW_ANCHOR", "PROJECTILE_TRAIL", "DURATION", false, false));
            Assert.That(v, Does.Contain("ProjectileTrailOffRelease"));
            Assert.That(v, Does.Contain("ProjectileTrailFollowAnchor"));
            Assert.That(v, Does.Contain("ProjectileTrailBadLifecycle"));
            Assert.That(v, Does.Contain("ProjectileTrailNonZeroDuration"));
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
            Assert.That(
                CheckRules(Fields("SPELL_RELEASE", "TARGET_BACK", "FOLLOW_ANCHOR", "ATTACHED", "DURATION", false, false)),
                Does.Contain("TargetAnchorPreImpact"));
        }

        [Test]
        public void Rule16_WorldImpactsUseImpactPointUnlessFollowingTarget()
        {
            Assert.That(
                CheckRules(Fields("SPELL_IMPACT", "TARGET", "SPAWN_WORLD", "ONE_SHOT", "DURATION", false, false)),
                Does.Contain("WorldImpactTargetAnchor"));
            Assert.That(
                CheckRules(Fields("SPELL_IMPACT", "IMPACT_POINT", "SPAWN_WORLD", "ONE_SHOT", "DURATION", false, false)),
                Does.Not.Contain("WorldImpactTargetAnchor"));
            Assert.That(
                CheckRules(Fields("SPELL_IMPACT", "TARGET", "FOLLOW_ANCHOR", "ATTACHED", "DURATION", false, false)),
                Does.Not.Contain("WorldImpactTargetAnchor"));
            Assert.That(
                CheckRules(Fields("SPELL_IMPACT", "TARGET_BACK", "SPAWN_WORLD", "ATTACHED", "DURATION", false, false)),
                Does.Contain("WorldImpactTargetAnchor"));
        }
    }
}

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class CombatVfxCueResolverTests
    {
        private const string ResolverPath = "Assets/Arena/Runtime/Presentation/CombatVfxCueResolver.cs";
        private const string DispatcherPath = "Assets/Arena/Runtime/Presentation/CombatVFXDispatcher.cs";

        [TestCase("SPELL_ICE_SPIKES", true, 1, "ABILITY")]
        [TestCase("SPELL_ICE_SPIKES", false, 1, "ABILITY")]
        [TestCase("", true, 1, "SPELL")]
        [TestCase("", false, 0, "")]
        public void IceSpikes_LegacyCueIsOnlyRedundantWhenAbilityIdentityIsPresent(
            string abilityId, bool includeLegacyCue, int expectedCount, string expectedOwner)
        {
            Assembly runtime = AppDomain.CurrentDomain.Load("Assembly-CSharp");
            Type cueType = runtime.GetType("SpacetimeDB.Types.CombatVfxCueCatalog", true)!;
            Type factType = runtime.GetType("Arena.Presentation.CombatVfxResolutionFact", true)!;
            Type indexType = runtime.GetType("Arena.Presentation.CombatVfxCueResolver+Index", true)!;
            Type listType = typeof(List<>).MakeGenericType(cueType);
            var cues = (IList)Activator.CreateInstance(listType)!;
            var output = (IList)Activator.CreateInstance(listType)!;
            cues.Add(Cue("ABILITY", "SPELL_ICE_SPIKES", 30));
            if (includeLegacyCue)
                cues.Add(Cue("SPELL", "ICE_SPIKES", 31));
            object fact = Activator.CreateInstance(factType, new object[]
            {
                true, "AREA_IMPACT", "ICE_SPIKES", abilityId, string.Empty, -1,
            })!;
            object index = Activator.CreateInstance(indexType, nonPublic: true)!;
            indexType.GetMethod("Resolve")!.Invoke(index, new[] { cues, fact, output });

            Assert.That(output.Count, Is.EqualTo(expectedCount));
            if (expectedCount > 0)
            {
                Assert.That(cueType.GetField("OwnerKind")!.GetValue(output[0]), Is.EqualTo(expectedOwner));
                Assert.That(cueType.GetField("VfxId")!.GetValue(output[0]), Is.EqualTo("VFX_ICE_SPIKES_AREA_01"));
                Assert.That(cueType.GetField("DurationMs")!.GetValue(output[0]), Is.EqualTo(2500UL));
            }

            object Cue(string owner, string ownerId, uint order)
            {
                object cue = Activator.CreateInstance(cueType)!;
                Set("OwnerKind", owner);
                Set("OwnerId", ownerId);
                Set("Trigger", "AREA_IMPACT");
                Set("HitIndex", -1);
                Set("Anchor", "AREA_ORIGIN");
                Set("VfxId", "VFX_ICE_SPIKES_AREA_01");
                Set("AttachMode", "WORLD_ALIGNED_TO_FACING");
                Set("VfxRole", "ONE_SHOT");
                Set("Lifecycle", "DURATION");
                Set("ProjectileSequenceIndex", -1);
                Set("DurationMs", 2500UL);
                Set("SortOrder", order);
                return cue;

                void Set(string field, object value) => cueType.GetField(field)!.SetValue(cue, value);
            }
        }

        [Test]
        public void CombatVfxResolver_UsesCachedLookupIndex()
        {
            string source = File.ReadAllText(ResolverPath);

            Assert.That(source, Does.Contain("internal sealed class Index"));
            Assert.That(source, Does.Contain("Dictionary<CueLookupKey, List<IndexedCue>>"));
            Assert.That(source, Does.Contain("EnsureBuilt(cues);"));
            Assert.That(source, Does.Contain("WireIdentifier.Normalize(cue.OwnerKind)"));
            Assert.That(source, Does.Contain("WireIdentifier.Normalize(cue.OwnerId)"));
            Assert.That(source, Does.Contain("WireIdentifier.Normalize(cue.Trigger)"));
        }

        [Test]
        public void CombatVfxResolver_PreservesOwnerAndOverrideRules()
        {
            string source = File.ReadAllText(ResolverPath);

            Assert.That(source, Does.Contain("new CueLookupKey(OwnerKindAbility, fact.AbilityId, fact.Trigger)"));
            Assert.That(source, Does.Contain("new CueLookupKey(OwnerKindSpell, fact.SpellId, fact.Trigger)"));
            Assert.That(source, Does.Contain("new CueLookupKey(OwnerKindMeleeStrike, fact.StrikeId, fact.Trigger)"));
            Assert.That(source, Does.Contain("suppressSpellOverrides: true"));
            Assert.That(source, Does.Contain("VfxRoleProjectileTrail"));
            Assert.That(source, Does.Contain("entry.MatchesHitIndex(fact.HitIndex)"));
            Assert.That(source, Does.Contain("output.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder))"));
        }

        [Test]
        public void CombatVfxDispatcher_InvalidatesCueIndexWhenCatalogChanges()
        {
            string source = File.ReadAllText(DispatcherPath);

            Assert.That(source, Does.Contain("private CombatVfxCueResolver.Index? _cueResolver;"));
            Assert.That(source, Does.Contain("conn.Db.CombatVfxCueCatalog.OnInsert += OnCombatVfxCueCatalogInsert;"));
            Assert.That(source, Does.Contain("conn.Db.CombatVfxCueCatalog.OnUpdate += OnCombatVfxCueCatalogUpdate;"));
            Assert.That(source, Does.Contain("conn.Db.CombatVfxCueCatalog.OnDelete += OnCombatVfxCueCatalogDelete;"));
            Assert.That(source, Does.Contain("CueResolver.Resolve(conn.Db.CombatVfxCueCatalog.Iter(), fact.ToResolutionFact(), matchingCues);"));
        }

        [Test]
        public void CombatVfxDispatcher_ReplacesRadialVisualAtAuthoredMaxStacks()
        {
            string source = File.ReadAllText(DispatcherPath);

            Assert.That(source, Does.Contain("TriggerEmanationMaxStacks"));
            Assert.That(source, Does.Contain("status.Stacks >= status.MaxStacks"));
            Assert.That(source, Does.Contain("RefreshActiveRadialEffectVfxForStatus"));
            Assert.That(source, Does.Contain("DestroyForRadialEffectEnd(row.Key)"));
            Assert.That(source, Does.Contain("ResolveRadialEffectAbilityId"));
            Assert.That(source, Does.Not.Contain("Resources.Load<GameObject>(\"CombatVFX/playground/fire/"));
        }

        [Test]
        public void CombatVfxDispatcher_ReconstructsAndCleansUpPersistentAreaVisuals()
        {
            string source = File.ReadAllText(DispatcherPath);

            Assert.That(source, Does.Contain("ActivePersistentArea.OnInsert += OnActivePersistentAreaInsertForVfx"));
            Assert.That(source, Does.Contain("foreach (ActivePersistentArea row in conn.Db.ActivePersistentArea.Iter())"));
            Assert.That(source, Does.Contain("SpawnActivePersistentAreaVfx(row)"));
            Assert.That(source, Does.Contain("DestroyForRadialEffectEnd(row.SpellInstanceId)"));
        }

        [Test]
        public void PhotosynthesisVfx_ScalesOnlyTheAuthoredLeafFlecksWithStacks()
        {
            const string path = "Assets/Arena/Runtime/Presentation/VFX/PhotosynthesisVFX.cs";
            string source = File.ReadAllText(path);

            Assert.That(source, Does.Contain("Flecks_Shiny_Additive"));
            Assert.That(source, Does.Contain("Mathf.Clamp((int)context.SequenceCount, 1, MaxStacks)"));
            Assert.That(source, Does.Contain("main.maxParticles = LeavesPerStack * stacks"));
        }

        [Test]
        public void CombatVfxDispatcher_ResolvesPassiveOwnedStatusCuesByAbilityId()
        {
            string source = File.ReadAllText(DispatcherPath);

            Assert.That(source, Does.Contain("Passive-owned statuses use their stable ability ID as spell_id"));
            Assert.That(source, Does.Contain("string candidate = WireIdentifier.Normalize(ability.AbilityId);"));
            Assert.That(source, Does.Contain("string.Equals(candidate, spellId, StringComparison.Ordinal)"));
        }

        [Test]
        public void CombatVfxDispatcher_RoutesSpecialMovementThroughNormalCuePipeline()
        {
            string source = File.ReadAllText(DispatcherPath);

            Assert.That(source, Does.Contain("SpecialMovementRuntime.OnInsert += OnSpecialMovementRuntimeInsertForVfx"));
            Assert.That(source, Does.Contain("TriggerSpecialMovementStart"));
            Assert.That(source, Does.Contain("TriggerSpecialMovementArrival"));
            Assert.That(source, Does.Contain("DispatchFact(fact);"));
            Assert.That(source, Does.Not.Contain("Resources.Load<GameObject>(\"CombatVFX/playground/Realistic Ink Spells 1/shadow_in\")"));
        }

        [Test]
        public void CombatVfxDispatcher_CachesProjectileDeliveredSpellImpactClassification()
        {
            string source = File.ReadAllText(DispatcherPath);

            Assert.That(source, Does.Contain("Dictionary<string, bool> _projectileDeliveredSpellImpactByActionKind"));
            Assert.That(source, Does.Contain("_projectileDeliveredSpellImpactByActionKind.TryGetValue(actionKind, out bool cached)"));
            Assert.That(source, Does.Contain("_projectileDeliveredSpellImpactByActionKind[actionKind] = result;"));
            Assert.That(source, Does.Contain("_projectileDeliveredSpellImpactByActionKind.Clear();"));
            Assert.That(source, Does.Contain("conn.Db.SpellDefinition.OnInsert += OnSpellDefinitionInsertForVfx;"));
            Assert.That(source, Does.Contain("conn.Db.SpellDefinition.OnUpdate += OnSpellDefinitionUpdateForVfx;"));
            Assert.That(source, Does.Contain("conn.Db.SpellDefinition.OnDelete += OnSpellDefinitionDeleteForVfx;"));
            Assert.That(source, Does.Contain("InvalidateProjectileSpellImpactClassification(row.Kind);"));
            Assert.That(source, Does.Not.Contain("_projectileDeliveredSpellImpactByActionKind[actionKind] = false;\n                return false;"));
        }

        [Test]
        public void CombatVfxDispatcher_ProjectileContactsRouteExistingSpellImpactCues()
        {
            string source = File.ReadAllText(DispatcherPath);

            Assert.That(source, Does.Contain("case CombatEventTypes.Contact:"));
            Assert.That(source, Does.Contain("DispatchProjectileContactCue(row);"));
            Assert.That(source, Does.Contain("BuildProjectileContactFact(row)"));
            Assert.That(source, Does.Contain("TriggerSpellImpact"));
            Assert.That(source, Does.Contain("ProjectilePresentationEvent row,"));
        }

        [Test]
        public void CombatVfxDispatcher_AreaContactsRouteTargetAnchoredSpellImpactCues()
        {
            string source = File.ReadAllText(DispatcherPath);

            Assert.That(source, Does.Contain("CombatEventTypes.Contact => TriggerSpellImpact"));
            Assert.That(source, Does.Contain("targetAnchoredSpellContact"));
            Assert.That(source, Does.Contain("DispatchFact(fact.Value, targetAnchoredSpellContact);"));
            Assert.That(source, Does.Contain("IsTargetAnchoredCue(cue)"));
            Assert.That(source, Does.Contain("AnchorGroundUnderTarget"));
        }

    }
}

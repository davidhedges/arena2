#nullable enable

using System.IO;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class CombatVfxCueResolverTests
    {
        private const string ResolverPath = "Assets/Arena/Runtime/Presentation/CombatVfxCueResolver.cs";
        private const string DispatcherPath = "Assets/Arena/Runtime/Presentation/CombatVFXDispatcher.cs";

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

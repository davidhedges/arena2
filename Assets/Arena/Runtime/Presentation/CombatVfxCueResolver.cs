#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
using SpacetimeDB.Types;

namespace Arena.Presentation
{
    internal static class CombatVfxCueResolver
    {
        private const string OwnerKindMeleeStrike = "MELEE_STRIKE";
        private const string OwnerKindSpell = "SPELL";
        private const string OwnerKindAbility = "ABILITY";
        private const string VfxRoleProjectileBody = "PROJECTILE_BODY";
        private const string VfxRoleTravelBody = "TRAVEL_BODY";

        internal sealed class Index
        {
            private readonly Dictionary<CueLookupKey, List<IndexedCue>> _cuesByKey = new();
            private readonly HashSet<CueOverrideKey> _abilityOverrideKeys = new();
            private bool _dirty = true;

            public void MarkDirty()
            {
                _dirty = true;
            }

            public void Resolve(
                IEnumerable<CombatVfxCueCatalog> cues,
                CombatVfxResolutionFact fact,
                List<CombatVfxCueCatalog> output)
            {
                EnsureBuilt(cues);
                output.Clear();
                _abilityOverrideKeys.Clear();

                AddMatches(new CueLookupKey(OwnerKindAbility, fact.AbilityId, fact.Trigger), fact, output);
                if (fact.IsSpell)
                    AddMatches(new CueLookupKey(OwnerKindSpell, fact.SpellId, fact.Trigger), fact, output, suppressSpellOverrides: true);
                else
                    AddMatches(new CueLookupKey(OwnerKindMeleeStrike, fact.StrikeId, fact.Trigger), fact, output);

                output.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            }

            private void EnsureBuilt(IEnumerable<CombatVfxCueCatalog> cues)
            {
                if (!_dirty)
                    return;

                _cuesByKey.Clear();
                foreach (CombatVfxCueCatalog cue in cues)
                {
                    var indexed = IndexedCue.From(cue);
                    var key = new CueLookupKey(indexed.OwnerKind, indexed.OwnerId, indexed.Trigger);
                    if (!_cuesByKey.TryGetValue(key, out List<IndexedCue> entries))
                    {
                        entries = new List<IndexedCue>();
                        _cuesByKey[key] = entries;
                    }

                    entries.Add(indexed);
                }

                _dirty = false;
            }

            private void AddMatches(
                CueLookupKey key,
                CombatVfxResolutionFact fact,
                List<CombatVfxCueCatalog> output,
                bool suppressSpellOverrides = false)
            {
                if (!_cuesByKey.TryGetValue(key, out List<IndexedCue> entries))
                    return;

                foreach (IndexedCue entry in entries)
                {
                    if (!entry.MatchesHitIndex(fact.HitIndex))
                        continue;
                    if (suppressSpellOverrides
                        && entry.IsSpellCue
                        && _abilityOverrideKeys.Contains(entry.OverrideKey))
                        continue;

                    output.Add(entry.Cue);
                    if (entry.IsAbilityCue)
                        _abilityOverrideKeys.Add(entry.OverrideKey);
                }
            }
        }

        private readonly struct IndexedCue
        {
            public readonly CombatVfxCueCatalog Cue;
            public readonly string OwnerKind;
            public readonly string OwnerId;
            public readonly string Trigger;
            public readonly bool IsAbilityCue;
            public readonly bool IsSpellCue;
            public readonly CueOverrideKey OverrideKey;

            private IndexedCue(
                CombatVfxCueCatalog cue,
                string ownerKind,
                string ownerId,
                string trigger,
                CueOverrideKey overrideKey)
            {
                Cue = cue;
                OwnerKind = ownerKind;
                OwnerId = ownerId;
                Trigger = trigger;
                IsAbilityCue = string.Equals(ownerKind, OwnerKindAbility, StringComparison.Ordinal);
                IsSpellCue = string.Equals(ownerKind, OwnerKindSpell, StringComparison.Ordinal);
                OverrideKey = overrideKey;
            }

            public static IndexedCue From(CombatVfxCueCatalog cue)
            {
                string ownerKind = WireIdentifier.Normalize(cue.OwnerKind);
                return new IndexedCue(
                    cue,
                    ownerKind,
                    WireIdentifier.Normalize(cue.OwnerId),
                    WireIdentifier.Normalize(cue.Trigger),
                    CueOverrideKey.From(cue));
            }

            public bool MatchesHitIndex(int factHitIndex)
            {
                if (Cue.HitIndex < 0)
                    return true;

                return factHitIndex >= 0 && Cue.HitIndex == factHitIndex;
            }
        }

        private readonly struct CueLookupKey : IEquatable<CueLookupKey>
        {
            private readonly string _ownerKind;
            private readonly string _ownerId;
            private readonly string _trigger;

            public CueLookupKey(string ownerKind, string ownerId, string trigger)
            {
                _ownerKind = ownerKind;
                _ownerId = ownerId;
                _trigger = trigger;
            }

            public bool Equals(CueLookupKey other)
            {
                return string.Equals(_ownerKind, other._ownerKind, StringComparison.Ordinal)
                    && string.Equals(_ownerId, other._ownerId, StringComparison.Ordinal)
                    && string.Equals(_trigger, other._trigger, StringComparison.Ordinal);
            }

            public override bool Equals(object? obj)
            {
                return obj is CueLookupKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_ownerKind, _ownerId, _trigger);
            }
        }

        private readonly struct CueOverrideKey : IEquatable<CueOverrideKey>
        {
            private readonly string _trigger;
            private readonly string _role;
            private readonly string _anchor;
            private readonly string _attachMode;
            private readonly int _hitIndex;
            private readonly int _projectileSequenceIndex;

            private CueOverrideKey(
                string trigger,
                string role,
                string anchor,
                string attachMode,
                int hitIndex,
                int projectileSequenceIndex)
            {
                _trigger = trigger;
                _role = role;
                _anchor = anchor;
                _attachMode = attachMode;
                _hitIndex = hitIndex;
                _projectileSequenceIndex = projectileSequenceIndex;
            }

            public static CueOverrideKey From(CombatVfxCueCatalog cue)
            {
                string role = WireIdentifier.Normalize(cue.VfxRole);
                bool bodyRole = string.Equals(role, VfxRoleProjectileBody, StringComparison.Ordinal)
                    || string.Equals(role, VfxRoleTravelBody, StringComparison.Ordinal);
                return new CueOverrideKey(
                    WireIdentifier.Normalize(cue.Trigger),
                    role,
                    bodyRole ? string.Empty : WireIdentifier.Normalize(cue.Anchor),
                    bodyRole ? string.Empty : WireIdentifier.Normalize(cue.AttachMode),
                    cue.HitIndex,
                    cue.ProjectileSequenceIndex);
            }

            public bool Equals(CueOverrideKey other)
            {
                return _hitIndex == other._hitIndex
                    && _projectileSequenceIndex == other._projectileSequenceIndex
                    && string.Equals(_trigger, other._trigger, StringComparison.Ordinal)
                    && string.Equals(_role, other._role, StringComparison.Ordinal)
                    && string.Equals(_anchor, other._anchor, StringComparison.Ordinal)
                    && string.Equals(_attachMode, other._attachMode, StringComparison.Ordinal);
            }

            public override bool Equals(object? obj)
            {
                return obj is CueOverrideKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_trigger, _role, _anchor, _attachMode, _hitIndex, _projectileSequenceIndex);
            }
        }
    }

    internal readonly struct CombatVfxResolutionFact
    {
        public readonly bool IsSpell;
        public readonly string Trigger;
        public readonly string SpellId;
        public readonly string AbilityId;
        public readonly string StrikeId;
        public readonly int HitIndex;

        public CombatVfxResolutionFact(
            bool isSpell,
            string trigger,
            string spellId,
            string abilityId,
            string strikeId,
            int hitIndex)
        {
            IsSpell = isSpell;
            Trigger = trigger;
            SpellId = spellId;
            AbilityId = abilityId;
            StrikeId = strikeId;
            HitIndex = hitIndex;
        }
    }
}

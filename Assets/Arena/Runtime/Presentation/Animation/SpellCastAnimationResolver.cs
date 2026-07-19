#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Arena.Combat;
using Arena.Network;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// The single runtime entry point for a spell's cast animation: an authored explicit
    /// <see cref="WeaponSpellAnimationEntry"/> wins (byte-identical to legacy behavior); otherwise the
    /// spell is composed from its flavor family (design doc §5). Until a <see cref="SpellCastAnimationMap"/>
    /// exists in Resources, composition is a no-op and this is exactly
    /// <see cref="CombatAnimationSet.TryGetSpellAnimation"/> — so wiring the runtime through it changes
    /// nothing until spells are mapped.
    /// </summary>
    public static class SpellCastAnimationResolver
    {
        private const string LibraryResource = "SpellCastAnimationLibrary";
        private const string MapResource = "SpellCastAnimationMap";

        private static SpellCastAnimationLibrary? _library;
        private static SpellCastAnimationMap? _map;
        private static readonly Dictionary<ComposedCacheKey, WeaponSpellAnimationEntry> ComposedEntries = new();

        private readonly struct ComposedCacheKey : IEquatable<ComposedCacheKey>
        {
            public ComposedCacheKey(
                string spellId,
                CombatAnimationSet? animationSet,
                SpellCastHand hand,
                SpellAnimationArchetype archetype)
            {
                SpellId = spellId;
                AnimationSet = animationSet;
                Hand = hand;
                Archetype = archetype;
            }

            private string SpellId { get; }
            private CombatAnimationSet? AnimationSet { get; }
            private SpellCastHand Hand { get; }
            private SpellAnimationArchetype Archetype { get; }

            public bool Equals(ComposedCacheKey other) =>
                ReferenceEquals(AnimationSet, other.AnimationSet)
                && Hand == other.Hand
                && Archetype == other.Archetype
                && string.Equals(SpellId, other.SpellId, StringComparison.Ordinal);

            public override bool Equals(object? obj) => obj is ComposedCacheKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = StringComparer.Ordinal.GetHashCode(SpellId);
                    hash = (hash * 397) ^ (AnimationSet != null ? RuntimeHelpers.GetHashCode(AnimationSet) : 0);
                    hash = (hash * 397) ^ (int)Hand;
                    return (hash * 397) ^ (int)Archetype;
                }
            }
        }

        /// <summary>Explicit authored entry wins; else the family-composed entry; else not found.</summary>
        public static bool TryResolve(CombatAnimationSet? set, string spellId, out WeaponSpellAnimationEntry entry)
        {
            if (set != null && set.TryGetSpellAnimation(spellId, out entry))
                return true;

            return TryResolveComposed(set, spellId, out entry);
        }

        /// <summary>
        /// Runtime playback overload that also reports whether authoritative synced gameplay confirms
        /// the resolved spell is Instant. Animation resolution retains its existing fallback behavior;
        /// the confirmation fails closed so missing gameplay data can only disable startup trim.
        /// </summary>
        public static bool TryResolve(
            CombatAnimationSet? set,
            string spellId,
            out WeaponSpellAnimationEntry entry,
            out bool confirmedInstant)
        {
            bool resolved = TryResolve(set, spellId, out entry);
            confirmedInstant = resolved
                && TryDeriveSyncedArchetype(spellId, out SpellAnimationArchetype archetype)
                && archetype == SpellAnimationArchetype.Instant;
            return resolved;
        }

        /// <summary>
        /// Authoring/offline overload. The caller supplies the archetype derived from the authored
        /// catalog so validation never guesses Instant merely because no runtime connection exists.
        /// </summary>
        public static bool TryResolve(
            CombatAnimationSet? set,
            string spellId,
            SpellAnimationArchetype archetype,
            out WeaponSpellAnimationEntry entry)
        {
            if (set != null && set.TryGetSpellAnimation(spellId, out entry))
                return true;

            return TryResolveComposed(set, spellId, archetype, out entry);
        }

        /// <summary>
        /// The composed (family-derived) entry only — skips the explicit layer. Returns false when no
        /// map/library is present, the spell is unmapped, its family is missing, or the family lacks
        /// the clips the archetype needs.
        /// </summary>
        public static bool TryResolveComposed(CombatAnimationSet? set, string spellId, out WeaponSpellAnimationEntry entry)
        {
            entry = default;
            if (!TryDeriveArchetype(spellId, out SpellAnimationArchetype archetype, out _))
                return false;
            return TryResolveComposed(set, spellId, archetype, out entry);
        }

        /// <summary>Composed-only authoring/offline overload with an explicit catalog archetype.</summary>
        public static bool TryResolveComposed(
            CombatAnimationSet? set,
            string spellId,
            SpellAnimationArchetype archetype,
            out WeaponSpellAnimationEntry entry)
        {
            entry = default;
            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            SpellCastHand hand = set != null ? set.OneHandedCastHand : SpellCastHand.Left;
            var cacheKey = new ComposedCacheKey(
                normalizedSpellId,
                set,
                hand,
                archetype);
            if (normalizedSpellId.Length != 0 && ComposedEntries.TryGetValue(cacheKey, out entry))
                return true;

            if (!TryResolveComposedAssets(normalizedSpellId, out SpellCastAnimationMap.Entry mapEntry, out SpellCastAnimationFamily family, out _))
                return false;

            if (!SpellCastAnimationComposer.TryCompose(normalizedSpellId, family, hand, archetype, out entry))
                return false;

            ApplyOverrides(mapEntry, ref entry);
            ComposedEntries[cacheKey] = entry;
            return true;
        }

        public static bool TryDescribeMappedResolutionFailure(
            CombatAnimationSet? set,
            string spellId,
            out string reason)
        {
            if (!TryResolveComposedInput(set, spellId, out SpellCastAnimationMap.Entry mapEntry, out SpellCastAnimationFamily family, out SpellAnimationArchetype archetype, out reason))
                return !string.IsNullOrWhiteSpace(reason);

            SpellCastHand hand = set != null ? set.OneHandedCastHand : SpellCastHand.Left;
            if (!SpellCastAnimationComposer.TryCompose(spellId, family, hand, archetype, out _))
            {
                reason = $"map entry baseName '{mapEntry.baseName}' has no playable {archetype} clips for hand '{hand}'";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        private static bool TryResolveComposedInput(
            CombatAnimationSet? set,
            string spellId,
            out SpellCastAnimationMap.Entry mapEntry,
            out SpellCastAnimationFamily family,
            out SpellAnimationArchetype archetype,
            out string failureReason)
        {
            archetype = default;
            if (!TryResolveComposedAssets(spellId, out mapEntry, out family, out failureReason))
                return false;

            if (!TryDeriveArchetype(spellId, out archetype, out failureReason))
                return false;

            return true;
        }

        private static bool TryResolveComposedAssets(
            string spellId,
            out SpellCastAnimationMap.Entry mapEntry,
            out SpellCastAnimationFamily family,
            out string failureReason)
        {
            mapEntry = default;
            family = default;
            failureReason = string.Empty;

            EnsureLoaded();
            if (_map == null)
                return false;
            if (!_map.TryGetEntry(spellId, out mapEntry))
                return false;
            if (_library == null)
            {
                failureReason = "SpellCastAnimationLibrary resource is missing";
                return false;
            }
            if (!_library.TryGetFamily(mapEntry.baseName, out family))
            {
                failureReason = $"map entry baseName '{mapEntry.baseName}' does not resolve in SpellCastAnimationLibrary";
                return false;
            }

            return true;
        }

        /// <summary>Applies the map entry's optional per-spell overrides onto the composed entry.</summary>
        private static void ApplyOverrides(in SpellCastAnimationMap.Entry mapEntry, ref WeaponSpellAnimationEntry entry)
        {
            switch (mapEntry.playbackLayer)
            {
                case SpellCastLayerOverride.UpperBody: entry.playbackLayer = SpellPlaybackLayer.UpperBody; break;
                case SpellCastLayerOverride.LeftGesture: entry.playbackLayer = SpellPlaybackLayer.LeftGesture; break;
                case SpellCastLayerOverride.FullBody: entry.playbackLayer = SpellPlaybackLayer.FullBody; break;
                case SpellCastLayerOverride.UpperBodyWhileMoving: entry.playbackLayer = SpellPlaybackLayer.UpperBodyWhileMoving; break;
                case SpellCastLayerOverride.Auto:
                default: break;
            }

            switch (mapEntry.combatEntryMode)
            {
                case SpellCastEntryModeOverride.Immediate: entry.combatEntryMode = CombatEntryMode.Immediate; break;
                case SpellCastEntryModeOverride.AnimatedAfterCast: entry.combatEntryMode = CombatEntryMode.AnimatedAfterCast; break;
                case SpellCastEntryModeOverride.ImmediateForFullBody: entry.combatEntryMode = CombatEntryMode.ImmediateForFullBodyAnimatedAfterUpperBody; break;
                case SpellCastEntryModeOverride.Auto:
                default: break;
            }

            // A prop the composer never sets — the one field an explicit entry carried that a
            // family can't (the temporary shield/weapon visual). Only overrides when enabled.
            if (mapEntry.animatedProp.enabled)
                entry.animatedProp = mapEntry.animatedProp;
        }

        /// <summary>True when a family mapping exists for the spell (used to gate the composed path).</summary>
        public static bool HasMapping(string spellId)
        {
            EnsureLoaded();
            return _map != null && _map.TryGetEntry(spellId, out _);
        }

        private static bool TryDeriveArchetype(
            string spellId,
            out SpellAnimationArchetype archetype,
            out string failureReason)
        {
            archetype = default;
            failureReason = string.Empty;

            NetworkManager? network = NetworkManager.Instance;
            if (network == null)
            {
                // Editor/offline tools do not have a NetworkManager. Keep their preview path usable;
                // runtime resolution with a NetworkManager present must wait for authoritative rows.
                archetype = SpellAnimationArchetypes.Derive((SpellDefinition?)null);
                return true;
            }

            DbConnection? conn = network.Conn;
            if (conn == null)
            {
                failureReason = "SpellDefinition table is not synced yet";
                return false;
            }

            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            SpellDefinition? def = conn.Db.SpellDefinition.Kind.Find(normalizedSpellId);
            if (def == null)
            {
                failureReason = $"SpellDefinition row '{normalizedSpellId}' is not synced yet";
                return false;
            }

            archetype = SpellAnimationArchetypes.Derive(def);
            return true;
        }

        private static bool TryDeriveSyncedArchetype(
            string spellId,
            out SpellAnimationArchetype archetype)
        {
            archetype = default;
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return false;

            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            SpellDefinition? definition = conn.Db.SpellDefinition.Kind.Find(normalizedSpellId);
            if (definition == null)
                return false;

            archetype = SpellAnimationArchetypes.Derive(definition);
            return true;
        }

        private static void EnsureLoaded()
        {
            if (_library != null && _map != null)
                return;

            // Resources.Load can invoke ScriptableObject validation callbacks in the editor.
            // Those callbacks deliberately invalidate this cache, so hold both results locally
            // and publish the pair only after both loads finish. This prevents a map validation
            // from clearing the library halfway through initialization.
            SpellCastAnimationLibrary? library = _library;
            SpellCastAnimationMap? map = _map;
            library ??= Resources.Load<SpellCastAnimationLibrary>(LibraryResource);
            map ??= Resources.Load<SpellCastAnimationMap>(MapResource);
            _library = library;
            _map = map;
        }

        /// <summary>Editor/test hook: drop cached Resources so a rescan or new map is picked up.</summary>
        public static void InvalidateCache()
        {
            _library = null;
            _map = null;
            ComposedEntries.Clear();
        }
    }
}

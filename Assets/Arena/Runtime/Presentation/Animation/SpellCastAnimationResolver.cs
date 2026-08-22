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
    /// The single runtime entry point for spell cast animation. Shared catalog recipes are the
    /// primary path, with an optional per-CombatAnimationSet recipe override. Fixed and semantic
    /// motion assignments remain migration paths for existing content.
    /// </summary>
    public static class SpellCastAnimationResolver
    {
        internal const string LibraryResource = "SpellCastAnimationLibrary";
        internal const string CatalogResource = "SpellCastAnimationCatalog";
        internal const string MapResource = "SpellCastAnimationMap";

        private static SpellCastAnimationLibrary? _library;
        private static SpellCastAnimationCatalog? _catalog;
        private static SpellCastAnimationMap? _map;
        private static readonly Dictionary<ResolvedCacheKey, WeaponSpellAnimationEntry> ResolvedEntries = new();

        private readonly struct ResolvedCacheKey : IEquatable<ResolvedCacheKey>
        {
            public ResolvedCacheKey(
                string spellId,
                CombatAnimationSet animationSet,
                SpellCastHand hand,
                SpellAnimationArchetype archetype)
            {
                SpellId = spellId;
                AnimationSet = animationSet;
                Hand = hand;
                Archetype = archetype;
            }

            private string SpellId { get; }
            private CombatAnimationSet AnimationSet { get; }
            private SpellCastHand Hand { get; }
            private SpellAnimationArchetype Archetype { get; }

            public bool Equals(ResolvedCacheKey other) =>
                ReferenceEquals(AnimationSet, other.AnimationSet)
                && Hand == other.Hand
                && Archetype == other.Archetype
                && string.Equals(SpellId, other.SpellId, StringComparison.Ordinal);

            public override bool Equals(object? obj) => obj is ResolvedCacheKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = StringComparer.Ordinal.GetHashCode(SpellId);
                    hash = (hash * 397) ^ RuntimeHelpers.GetHashCode(AnimationSet);
                    hash = (hash * 397) ^ (int)Hand;
                    return (hash * 397) ^ (int)Archetype;
                }
            }
        }

        public static bool TryResolve(CombatAnimationSet? set, string spellId, out WeaponSpellAnimationEntry entry)
        {
            entry = default;
            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            if (!TryGetMapEntry(normalizedSpellId, out SpellCastAnimationMap.Entry mapEntry))
                return false;

            if (mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.NoAnimation)
                return false;

            bool catalogResolved = TryResolveCatalogSelection(
                set,
                normalizedSpellId,
                mapEntry,
                out entry,
                out bool hasCatalogSelection);
            if (hasCatalogSelection)
                return catalogResolved;

            if (mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.Fixed)
                return TryResolveFixed(normalizedSpellId, mapEntry, out entry);

            if (!TryDeriveArchetype(normalizedSpellId, out SpellAnimationArchetype archetype, out _))
                return false;

            return TryResolveMotion(set, normalizedSpellId, mapEntry, archetype, out entry);
        }

        /// <summary>
        /// Runtime playback overload that also reports whether authoritative synced gameplay confirms
        /// the resolved spell is Instant. Missing gameplay data fails closed and only disables startup
        /// trim; it does not invalidate a fixed presentation.
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
        /// Fixed presentations intentionally ignore the supplied archetype.
        /// </summary>
        public static bool TryResolve(
            CombatAnimationSet? set,
            string spellId,
            SpellAnimationArchetype archetype,
            out WeaponSpellAnimationEntry entry)
        {
            entry = default;
            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            if (!TryGetMapEntry(normalizedSpellId, out SpellCastAnimationMap.Entry mapEntry))
                return false;

            if (mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.NoAnimation)
                return false;

            bool catalogResolved = TryResolveCatalogSelection(
                set,
                normalizedSpellId,
                mapEntry,
                out entry,
                out bool hasCatalogSelection);
            if (hasCatalogSelection)
                return catalogResolved;

            if (mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.Fixed)
                return TryResolveFixed(normalizedSpellId, mapEntry, out entry);

            return TryResolveMotion(set, normalizedSpellId, mapEntry, archetype, out entry);
        }

        public static bool TryDescribeMappedResolutionFailure(
            CombatAnimationSet? set,
            string spellId,
            out string reason)
        {
            reason = string.Empty;
            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            if (!TryGetMapEntry(normalizedSpellId, out SpellCastAnimationMap.Entry mapEntry))
                return false;

            if (mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.NoAnimation)
                return false;

            if (TryGetSelectedAnimationId(set, normalizedSpellId, mapEntry, out string animationId, out bool isSetOverride))
            {
                EnsureLoaded();
                if (_catalog == null)
                {
                    reason = "SpellCastAnimationCatalog resource is missing";
                    return true;
                }

                if (!_catalog.TryGetRecipe(animationId, out SpellCastAnimationRecipe recipe))
                {
                    string source = isSetOverride ? $"CombatAnimationSet '{set!.name}' override" : "global mapping";
                    reason = $"{source} references missing catalog recipe '{animationId}'";
                    return true;
                }

                if (!recipe.TryBuild(normalizedSpellId, out _))
                {
                    reason = $"catalog recipe '{animationId}' has no playable {recipe.presentationMode} presentation";
                    return true;
                }

                return false;
            }

            if (mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.Catalog)
            {
                reason = "catalog assignment has no animation id";
                return true;
            }

            if (mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.Fixed)
            {
                if (mapEntry.fixedAnimation.HasAnyPresentation)
                    return false;

                reason = "fixed assignment has no playable presentation";
                return true;
            }

            if (mapEntry.motion == SpellCastMotion.None)
            {
                reason = "motion assignment is None";
                return true;
            }

            if (set == null)
            {
                reason = $"motion '{mapEntry.motion}' requires an active CombatAnimationSet";
                return true;
            }

            if (!TryResolveMotionAssets(
                    set,
                    mapEntry,
                    out SpellCastAnimationFamily family,
                    out string familyBaseName,
                    out reason))
            {
                return true;
            }

            if (!TryDeriveArchetype(normalizedSpellId, out SpellAnimationArchetype archetype, out reason))
                return true;

            SpellCastHand hand = set.OneHandedCastHand;
            if (!SpellCastAnimationComposer.TryCompose(normalizedSpellId, family, hand, archetype, out _))
            {
                reason = $"motion '{mapEntry.motion}' family '{familyBaseName}' has no playable {archetype} clips for hand '{hand}'";
                return true;
            }

            return false;
        }

        public static bool IsExplicitlyNoAnimation(string spellId)
        {
            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            return TryGetMapEntry(normalizedSpellId, out SpellCastAnimationMap.Entry mapEntry)
                && mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.NoAnimation;
        }

        private static bool TryResolveMotion(
            CombatAnimationSet? set,
            string spellId,
            in SpellCastAnimationMap.Entry mapEntry,
            SpellAnimationArchetype archetype,
            out WeaponSpellAnimationEntry entry)
        {
            entry = default;
            if (set == null || mapEntry.motion == SpellCastMotion.None)
                return false;

            SpellCastHand hand = set.OneHandedCastHand;
            var cacheKey = new ResolvedCacheKey(spellId, set, hand, archetype);
            if (spellId.Length != 0 && ResolvedEntries.TryGetValue(cacheKey, out entry))
                return true;

            if (!TryResolveMotionAssets(set, mapEntry, out SpellCastAnimationFamily family, out _, out _))
                return false;

            if (!SpellCastAnimationComposer.TryCompose(spellId, family, hand, archetype, out entry))
                return false;

            ApplyOverrides(mapEntry, ref entry);
            ResolvedEntries[cacheKey] = entry;
            return true;
        }

        private static bool TryResolveCatalogSelection(
            CombatAnimationSet? set,
            string spellId,
            in SpellCastAnimationMap.Entry mapEntry,
            out WeaponSpellAnimationEntry entry,
            out bool hasSelection)
        {
            entry = default;
            hasSelection = TryGetSelectedAnimationId(set, spellId, mapEntry, out string animationId, out _);
            if (!hasSelection)
                return false;

            EnsureLoaded();
            if (_catalog == null
                || !_catalog.TryGetRecipe(animationId, out SpellCastAnimationRecipe recipe)
                || !recipe.TryBuild(spellId, out entry))
            {
                entry = default;
                return false;
            }

            ApplyOverrides(mapEntry, ref entry);
            return true;
        }

        public static bool TryGetSelectedAnimationId(
            CombatAnimationSet? set,
            string spellId,
            out string animationId,
            out bool isSetOverride)
        {
            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            if (!TryGetMapEntry(normalizedSpellId, out SpellCastAnimationMap.Entry mapEntry)
                || mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.NoAnimation)
            {
                animationId = string.Empty;
                isSetOverride = false;
                return false;
            }

            return TryGetSelectedAnimationId(set, normalizedSpellId, mapEntry, out animationId, out isSetOverride);
        }

        private static bool TryGetSelectedAnimationId(
            CombatAnimationSet? set,
            string spellId,
            in SpellCastAnimationMap.Entry mapEntry,
            out string animationId,
            out bool isSetOverride)
        {
            if (set != null && set.TryGetSpellCastAnimationOverride(spellId, out animationId))
            {
                isSetOverride = true;
                return true;
            }

            animationId = mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.Catalog
                ? WireIdentifier.Normalize(mapEntry.animationId)
                : string.Empty;
            isSetOverride = false;
            return animationId.Length != 0;
        }

        private static bool TryResolveMotionAssets(
            CombatAnimationSet set,
            in SpellCastAnimationMap.Entry mapEntry,
            out SpellCastAnimationFamily family,
            out string familyBaseName,
            out string failureReason)
        {
            family = default;
            familyBaseName = string.Empty;
            failureReason = string.Empty;

            if (!set.TryGetSpellCastFamily(mapEntry.motion, out familyBaseName))
            {
                failureReason = $"motion '{mapEntry.motion}' has no family binding on CombatAnimationSet '{set.name}'";
                return false;
            }

            EnsureLoaded();
            if (_library == null)
            {
                failureReason = "SpellCastAnimationLibrary resource is missing";
                return false;
            }

            if (!_library.TryGetFamily(familyBaseName, out family))
            {
                failureReason = $"motion '{mapEntry.motion}' on CombatAnimationSet '{set.name}' references family '{familyBaseName}', but SpellCastAnimationLibrary has no matching family";
                return false;
            }

            return true;
        }

        private static bool TryResolveFixed(
            string spellId,
            in SpellCastAnimationMap.Entry mapEntry,
            out WeaponSpellAnimationEntry entry)
        {
            entry = mapEntry.fixedAnimation;
            entry.spellId = spellId;
            return spellId.Length != 0 && entry.HasAnyPresentation;
        }

        /// <summary>Applies optional per-spell overrides onto a motion-composed entry.</summary>
        private static void ApplyOverrides(in SpellCastAnimationMap.Entry mapEntry, ref WeaponSpellAnimationEntry entry)
        {
            switch (mapEntry.playbackLayer)
            {
                case SpellCastLayerOverride.UpperBody: entry.playbackLayer = SpellPlaybackLayer.UpperBody; break;
                case SpellCastLayerOverride.LeftGesture: entry.playbackLayer = SpellPlaybackLayer.LeftGesture; break;
                case SpellCastLayerOverride.RightGesture: entry.playbackLayer = SpellPlaybackLayer.RightGesture; break;
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

            if (mapEntry.animatedProp.enabled)
                entry.animatedProp = mapEntry.animatedProp;
        }

        public static bool HasMapping(string spellId)
        {
            EnsureLoaded();
            return _map != null && _map.TryGetEntry(spellId, out _);
        }

        private static bool TryGetMapEntry(string spellId, out SpellCastAnimationMap.Entry mapEntry)
        {
            EnsureLoaded();
            if (_map != null && _map.TryGetEntry(spellId, out mapEntry))
                return true;

            mapEntry = default;
            return false;
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
            if (_library != null && _catalog != null && _map != null)
                return;

            SpellCastAnimationLibrary? library = _library;
            SpellCastAnimationCatalog? catalog = _catalog;
            SpellCastAnimationMap? map = _map;
            library ??= Resources.Load<SpellCastAnimationLibrary>(LibraryResource);
            catalog ??= Resources.Load<SpellCastAnimationCatalog>(CatalogResource);
            map ??= Resources.Load<SpellCastAnimationMap>(MapResource);
            _library = library;
            _catalog = catalog;
            _map = map;
        }

        internal static void RegisterPreloaded(
            SpellCastAnimationLibrary? library,
            SpellCastAnimationCatalog? catalog,
            SpellCastAnimationMap? map)
        {
            if (library != null)
                _library = library;
            if (catalog != null)
                _catalog = catalog;
            if (map != null)
                _map = map;
            ResolvedEntries.Clear();
        }

        public static void InvalidateCache()
        {
            _library = null;
            _catalog = null;
            _map = null;
            ResolvedEntries.Clear();
        }
    }
}

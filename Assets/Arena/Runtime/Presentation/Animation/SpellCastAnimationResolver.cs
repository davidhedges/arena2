#nullable enable
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
        private static bool _loaded;

        /// <summary>Explicit authored entry wins; else the family-composed entry; else not found.</summary>
        public static bool TryResolve(CombatAnimationSet? set, string spellId, out WeaponSpellAnimationEntry entry)
        {
            if (set != null && set.TryGetSpellAnimation(spellId, out entry))
                return true;

            return TryResolveComposed(set, spellId, out entry);
        }

        /// <summary>
        /// The composed (family-derived) entry only — skips the explicit layer. Returns false when no
        /// map/library is present, the spell is unmapped, its family is missing, or the family lacks
        /// the clips the archetype needs.
        /// </summary>
        public static bool TryResolveComposed(CombatAnimationSet? set, string spellId, out WeaponSpellAnimationEntry entry)
        {
            entry = default;
            EnsureLoaded();
            if (_map == null || _library == null)
                return false;

            if (!_map.TryGetBaseName(spellId, out string baseName))
                return false;
            if (!_library.TryGetFamily(baseName, out SpellCastAnimationFamily family))
                return false;

            SpellAnimationArchetype archetype = DeriveArchetype(spellId);
            SpellCastHand hand = set != null ? set.OneHandedCastHand : SpellCastHand.Left;
            return SpellCastAnimationComposer.TryCompose(spellId, family, hand, archetype, out entry);
        }

        /// <summary>True when a family mapping exists for the spell (used to gate the composed path).</summary>
        public static bool HasMapping(string spellId)
        {
            EnsureLoaded();
            return _map != null && _map.TryGetBaseName(spellId, out _);
        }

        private static SpellAnimationArchetype DeriveArchetype(string spellId)
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            SpellDefinition? def = conn?.Db.SpellDefinition.Kind.Find(WireIdentifier.Normalize(spellId));
            return SpellAnimationArchetypes.Derive(def);
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _library = Resources.Load<SpellCastAnimationLibrary>(LibraryResource);
            _map = Resources.Load<SpellCastAnimationMap>(MapResource);
            _loaded = true;
        }

        /// <summary>Editor/test hook: drop cached Resources so a rescan or new map is picked up.</summary>
        public static void InvalidateCache()
        {
            _library = null;
            _map = null;
            _loaded = false;
        }
    }
}

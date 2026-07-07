#nullable enable

namespace Arena.Presentation
{
    /// <summary>Which resolution layer supplied a resolved spell animation (for the inspector and redundancy validator).</summary>
    public enum SpellAnimationSource
    {
        None = 0,
        /// <summary>Per-spell <see cref="WeaponSpellAnimationEntry"/> authored in this weapon set (today's data).</summary>
        Explicit = 1,
        /// <summary>(weapon profile x archetype) template default.</summary>
        WeaponArchetypeTemplate = 2,
        /// <summary>Weapon-agnostic archetype template default (covers profile-less SPELL_* spells).</summary>
        ArchetypeTemplate = 3,
        /// <summary>Hard fallback (default cast hold + generic release gesture).</summary>
        Fallback = 4,
    }

    public readonly struct ResolvedSpellAnimation
    {
        public ResolvedSpellAnimation(bool found, WeaponSpellAnimationEntry entry, SpellAnimationSource source)
        {
            Found = found;
            Entry = entry;
            Source = source;
        }

        public bool Found { get; }
        public WeaponSpellAnimationEntry Entry { get; }
        public SpellAnimationSource Source { get; }

        public static ResolvedSpellAnimation NotFound => new(false, default, SpellAnimationSource.None);
    }

    /// <summary>
    /// Supplies archetype template entries. Step 1 ships the empty provider so the resolver
    /// returns the explicit per-spell entry verbatim; Step 2 fills these in from authored templates.
    /// </summary>
    public interface ISpellAnimationTemplateProvider
    {
        bool TryGetWeaponArchetypeTemplate(string weaponProfileId, SpellAnimationArchetype archetype, out WeaponSpellAnimationEntry entry);
        bool TryGetArchetypeTemplate(SpellAnimationArchetype archetype, out WeaponSpellAnimationEntry entry);
    }

    /// <summary>Template provider that supplies nothing — resolution collapses to the explicit entry.</summary>
    public sealed class EmptySpellAnimationTemplateProvider : ISpellAnimationTemplateProvider
    {
        public static readonly EmptySpellAnimationTemplateProvider Instance = new();

        public bool TryGetWeaponArchetypeTemplate(string weaponProfileId, SpellAnimationArchetype archetype, out WeaponSpellAnimationEntry entry)
        {
            entry = default;
            return false;
        }

        public bool TryGetArchetypeTemplate(SpellAnimationArchetype archetype, out WeaponSpellAnimationEntry entry)
        {
            entry = default;
            return false;
        }
    }

    /// <summary>
    /// Layered spell-animation resolution: explicit per-spell entry, then (weapon x archetype)
    /// template, then weapon-agnostic archetype template, then none. Entry-level precedence
    /// (whole entry wins) matches today's all-or-nothing authoring, so with the empty template
    /// provider this is byte-identical to <see cref="CombatAnimationSet.TryGetSpellAnimation"/>.
    /// Per-field fallthrough is deferred until templates introduce explicit "authored" markers
    /// (design doc §3.4 / §6.14).
    /// </summary>
    public static class SpellAnimationResolver
    {
        public static ResolvedSpellAnimation Resolve(
            CombatAnimationSet? set,
            string spellId,
            SpellAnimationArchetype archetype,
            ISpellAnimationTemplateProvider? templates = null)
        {
            templates ??= EmptySpellAnimationTemplateProvider.Instance;

            // Layer 1 — explicit per-spell entry. TryGetSpellAnimation's bool already means
            // "matched and has usable presentation", so this preserves legacy behavior exactly.
            if (set != null && set.TryGetSpellAnimation(spellId, out WeaponSpellAnimationEntry explicitEntry))
                return new ResolvedSpellAnimation(true, explicitEntry, SpellAnimationSource.Explicit);

            string weaponProfileId = set != null ? set.CombatProfileIdOrDefault : string.Empty;

            // Layer 2 — per-(weapon profile x archetype) template.
            if (templates.TryGetWeaponArchetypeTemplate(weaponProfileId, archetype, out WeaponSpellAnimationEntry weaponTemplate))
                return new ResolvedSpellAnimation(true, weaponTemplate, SpellAnimationSource.WeaponArchetypeTemplate);

            // Layer 3 — weapon-agnostic archetype template (profile-less SPELL_* spells).
            if (templates.TryGetArchetypeTemplate(archetype, out WeaponSpellAnimationEntry archetypeTemplate))
                return new ResolvedSpellAnimation(true, archetypeTemplate, SpellAnimationSource.ArchetypeTemplate);

            return ResolvedSpellAnimation.NotFound;
        }
    }
}

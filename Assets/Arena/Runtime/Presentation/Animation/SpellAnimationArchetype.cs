#nullable enable
using System;
using Arena.Combat;
using SpacetimeDB.Types;

namespace Arena.Presentation
{
    /// <summary>
    /// The presentation archetype a spell's animation belongs to. Derived from authored
    /// gameplay (cast_time_ms x behavior), not hand-authored. Three buckets, genre-standard:
    /// see docs/spell-presentation-dry-redesign-2026-07-07.md §2.1 / Appendix C.
    /// </summary>
    public enum SpellAnimationArchetype
    {
        /// <summary>cast_time_ms == 0 and not a channel — one-shot cast gesture (ReleaseOnly).</summary>
        Instant = 0,
        /// <summary>cast_time_ms &gt; 0 — enter, hold, then release (HoldThenRelease).</summary>
        Charged = 1,
        /// <summary>CHANNEL behavior — enter and loop until released (HoldOnly).</summary>
        Channel = 2,
    }

    public static class SpellAnimationArchetypes
    {
        /// <summary>
        /// Derives the presentation archetype from authored gameplay. This mirrors the runtime
        /// hold/release gates (EntityRegistry.OnCombatCast, SpellInputHandler), which already key
        /// off (cast_time_ms, behavior): CHANNEL behavior loops (HoldOnly), a positive cast time
        /// holds-then-releases, and everything else is an instant release.
        /// </summary>
        public static SpellAnimationArchetype Derive(ulong castTimeMs, string? behavior)
        {
            if (string.Equals(behavior, SpellDefinitionContracts.BehaviorChannel, StringComparison.Ordinal))
                return SpellAnimationArchetype.Channel;

            return castTimeMs > 0UL
                ? SpellAnimationArchetype.Charged
                : SpellAnimationArchetype.Instant;
        }

        public static SpellAnimationArchetype Derive(SpellDefinition? definition)
            => definition == null
                ? SpellAnimationArchetype.Instant
                : Derive(definition.CastTimeMs, definition.Behavior);

        /// <summary>
        /// The presentation mode this archetype implies. Used by the migration validator to
        /// check that a hand-authored <see cref="SpellAnimationPresentationMode"/> agrees with
        /// gameplay (design doc decision 3: validate agreement this cycle; the runtime keeps
        /// reading the authored field until the desync list is empty).
        /// </summary>
        public static SpellAnimationPresentationMode ToPresentationMode(SpellAnimationArchetype archetype)
            => archetype switch
            {
                SpellAnimationArchetype.Charged => SpellAnimationPresentationMode.HoldThenRelease,
                SpellAnimationArchetype.Channel => SpellAnimationPresentationMode.HoldOnly,
                _ => SpellAnimationPresentationMode.ReleaseOnly,
            };
    }
}

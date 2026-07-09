#nullable enable
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Composes a spell's stitched cast animation from one flavor family + the casting hand + the
    /// derived archetype, producing a <see cref="WeaponSpellAnimationEntry"/> the existing runtime
    /// already knows how to play (design doc §2/§3):
    /// <list type="bullet">
    /// <item><b>Instant</b> → <c>ground</c>/<c>air</c> = the full one-shot (<c>ReleaseOnly</c>).</item>
    /// <item><b>Channel</b> → <c>holdOverride.enter</c> = one-shot, <c>idleLoop</c> = Load, no release
    /// (<c>HoldOnly</c>).</item>
    /// <item><b>Charged</b> → hold enter/loop as Channel, plus <c>ground</c>/<c>air</c> = the final
    /// Cast clip (<c>HoldThenRelease</c>).</item>
    /// </list>
    /// Pure: no assets loaded, no gameplay queried — the caller supplies the archetype (from
    /// <see cref="SpellAnimationArchetypes.Derive"/>) and the family (from the library).
    /// </summary>
    public static class SpellCastAnimationComposer
    {
        // Held casts must resolve to a loop-capable animator layer or the loop freezes (design doc
        // §1.5 / §6.8): UpperBody → UpperBodySpellCastHoldAction*, FullBody → SpellCastHoldAction*.
        // UpperBody is the safe default (loop-capable, keeps facing/aim responsive); tunable later.
        private const SpellPlaybackLayer HoldLayer = SpellPlaybackLayer.UpperBody;
        // Instant casts preserve locomotion while moving (dominant instant default, Appendix C).
        private const SpellPlaybackLayer InstantLayer = SpellPlaybackLayer.UpperBodyWhileMoving;

        /// <summary>
        /// Returns <c>false</c> (with a default entry) when the family has no clips for the requested
        /// hand, or when the archetype's required clips are missing — the caller then treats the spell
        /// as having no composed animation (falls through, exactly like an absent explicit entry).
        /// </summary>
        public static bool TryCompose(
            string spellId,
            in SpellCastAnimationFamily family,
            SpellCastHand hand,
            SpellAnimationArchetype archetype,
            out WeaponSpellAnimationEntry entry)
        {
            entry = default;
            if (!family.TryGetTriple(hand, out SpellCastClipTriple triple))
                return false;

            entry.spellId = spellId;
            // requiresCombatStance is a constant across the authored corpus (87/87 entries).
            entry.requiresCombatStance = true;

            switch (archetype)
            {
                case SpellAnimationArchetype.Instant:
                {
                    // Instant casts fire on press (no release-frame gate) — play the snappy final
                    // "- Cast" gesture, not the slow full one-shot (which is the held-cast wind-up).
                    // Fall back to the one-shot only when a family has no Cast clip.
                    AnimationClip? snap = triple.cast ?? triple.oneShot;
                    if (snap == null)
                        return false;
                    entry.ground = snap;
                    entry.air = snap;
                    entry.presentationMode = SpellAnimationPresentationMode.ReleaseOnly;
                    entry.playbackLayer = InstantLayer;
                    entry.combatEntryMode = CombatEntryMode.ImmediateForFullBodyAnimatedAfterUpperBody;
                    return true;
                }

                case SpellAnimationArchetype.Charged:
                    // one-shot (enter) → Load (hold) → Cast (release).
                    if (!triple.HasHold || triple.cast == null)
                        return false;
                    entry.ground = triple.cast;
                    entry.air = triple.cast;
                    entry.presentationMode = SpellAnimationPresentationMode.HoldThenRelease;
                    entry.playbackLayer = InstantLayer;
                    entry.combatEntryMode = CombatEntryMode.AnimatedAfterCast;
                    entry.holdOverride = MakeHold(triple);
                    return true;

                case SpellAnimationArchetype.Channel:
                    // one-shot (enter) → Load (loop until released). HoldOnly: no release clip.
                    if (!triple.HasHold)
                        return false;
                    entry.presentationMode = SpellAnimationPresentationMode.HoldOnly;
                    entry.combatEntryMode = CombatEntryMode.AnimatedAfterCast;
                    entry.playbackLayer = HoldLayer;
                    entry.holdOverride = MakeHold(triple);
                    return true;

                default:
                    return false;
            }
        }

        private static SpellCastHoldProfile MakeHold(in SpellCastClipTriple triple)
            => new SpellCastHoldProfile
            {
                enter = triple.oneShot,
                idleLoop = triple.load,
                playbackLayer = HoldLayer,
            };
    }
}

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
        // §1.5 / §6.8). Both hold layers used here are loop-capable: UpperBody →
        // UpperBodySpellCastHoldAction*, LeftGesture → LeftGestureSpellCastHoldAction* (FullBody →
        // SpellCastHoldAction*). UpperBody is the safe default (keeps facing/aim responsive).
        private const SpellPlaybackLayer HoldLayer = SpellPlaybackLayer.UpperBody;
        // The default charged release stays UpperBodyWhileMoving — grounded charges read fine
        // full-body when stationary (kept from the ICICLE tuning the owner signed off on).
        private const SpellPlaybackLayer ChargedReleaseLayer = SpellPlaybackLayer.UpperBodyWhileMoving;

        /// <summary>
        /// Whether a resolved cast is a left-handed one-handed cast — the only case that gets the
        /// weapon-bearing-right-arm mask. Two-handed flavors use both hands and ignore the cast hand;
        /// no right-arm equivalent mask exists yet, so a right-handed one-handed cast is not masked.
        /// </summary>
        private static bool IsLeftHandedOneHand(SpellCastHandStyle handStyle, SpellCastHand hand)
            => handStyle == SpellCastHandStyle.OneHand && hand == SpellCastHand.Left;

        /// <summary>
        /// The overlay layer for an instant cast. A left-handed one-handed cast keeps the
        /// weapon-bearing right arm out of the motion (<see cref="SpellPlaybackLayer.LeftGesture"/>
        /// masks to pelvis/spine/left-arm, so the right arm holds its base pose — e.g. gripping the
        /// greatsword). Everything else uses the full upper-body overlay (torso + both arms), which
        /// still keeps the lower body/stance. Neither stands the caster straight up (that was the
        /// full-body-when-stationary behavior of UpperBodyWhileMoving).
        /// </summary>
        private static SpellPlaybackLayer ResolveInstantLayer(SpellCastHandStyle handStyle, SpellCastHand hand)
            => IsLeftHandedOneHand(handStyle, hand)
                ? SpellPlaybackLayer.LeftGesture
                : SpellPlaybackLayer.UpperBody;

        /// <summary>
        /// The loop-capable layer a charged/channel <b>hold</b> (wind-up + loop) plays on. A
        /// left-handed one-handed cast holds on the loop-capable LeftGesture states so the right arm
        /// stays on the weapon through the whole charge/channel; everything else holds on UpperBody.
        /// </summary>
        private static SpellPlaybackLayer ResolveHoldLayer(SpellCastHandStyle handStyle, SpellCastHand hand)
            => IsLeftHandedOneHand(handStyle, hand)
                ? SpellPlaybackLayer.LeftGesture
                : HoldLayer;

        /// <summary>
        /// The layer a charged <b>release</b> (the final Cast clip) plays on. Left-handed one-handed
        /// casts release on LeftGesture too, so the right arm keeps gripping the weapon through the
        /// release — matching the instant path and the masked hold. Everything else keeps the
        /// owner-approved UpperBodyWhileMoving release.
        /// </summary>
        private static SpellPlaybackLayer ResolveChargedReleaseLayer(SpellCastHandStyle handStyle, SpellCastHand hand)
            => IsLeftHandedOneHand(handStyle, hand)
                ? SpellPlaybackLayer.LeftGesture
                : ChargedReleaseLayer;

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
                    entry.playbackLayer = ResolveInstantLayer(family.handStyle, hand);
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
                    entry.playbackLayer = ResolveChargedReleaseLayer(family.handStyle, hand);
                    entry.combatEntryMode = CombatEntryMode.AnimatedAfterCast;
                    entry.holdOverride = MakeHold(triple, ResolveHoldLayer(family.handStyle, hand));
                    return true;

                case SpellAnimationArchetype.Channel:
                    // one-shot (enter) → Load (loop until released). HoldOnly: no release clip.
                    if (!triple.HasHold)
                        return false;
                    entry.presentationMode = SpellAnimationPresentationMode.HoldOnly;
                    entry.combatEntryMode = CombatEntryMode.AnimatedAfterCast;
                    entry.playbackLayer = ResolveHoldLayer(family.handStyle, hand);
                    entry.holdOverride = MakeHold(triple, ResolveHoldLayer(family.handStyle, hand));
                    return true;

                default:
                    return false;
            }
        }

        private static SpellCastHoldProfile MakeHold(in SpellCastClipTriple triple, SpellPlaybackLayer holdLayer)
            => new SpellCastHoldProfile
            {
                enter = triple.oneShot,
                idleLoop = triple.load,
                playbackLayer = holdLayer,
            };
    }
}

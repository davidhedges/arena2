#nullable enable
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Composes a spell's stitched cast animation from one flavor family + the casting hand + the
    /// derived archetype, producing a <see cref="WeaponSpellAnimationEntry"/> the existing runtime
    /// already knows how to play (design doc §2/§3):
    /// <list type="bullet">
    /// <item><b>Instant</b> → <c>clip</c> = the full one-shot (<c>ReleaseOnly</c>).</item>
    /// <item><b>Channel</b> → <c>holdOverride.enter</c> = one-shot, <c>idleLoop</c> = Load, no release
    /// (<c>HoldOnly</c>).</item>
    /// <item><b>Charged</b> → hold enter/loop as Channel, plus <c>clip</c> = the final
    /// Cast clip (<c>HoldThenRelease</c>).</item>
    /// </list>
    /// Pure: no assets loaded, no gameplay queried — the caller supplies the archetype (from
    /// <see cref="SpellAnimationArchetypes.Derive"/>) and the family (from the library).
    /// </summary>
    public static class SpellCastAnimationComposer
    {
        // Held casts must resolve to a loop-capable animator layer or the loop freezes (design doc
        // §1.5 / §6.8). All hold layers used here are loop-capable: UpperBody →
        // UpperBodySpellCastHoldAction*, LeftGesture/RightGesture → their masked
        // *SpellCastHoldAction* states (FullBody → SpellCastHoldAction*). UpperBody is the safe
        // default (keeps facing/aim responsive).
        private const SpellPlaybackLayer HoldLayer = SpellPlaybackLayer.UpperBody;
        // The default charged release stays UpperBodyWhileMoving — grounded charges read fine
        // full-body when stationary (kept from the ICICLE tuning the owner signed off on).
        private const SpellPlaybackLayer ChargedReleaseLayer = SpellPlaybackLayer.UpperBodyWhileMoving;

        /// <summary>
        /// Whether a resolved cast is a left-handed one-handed family.
        /// </summary>
        private static bool IsLeftHandedOneHand(SpellCastHandStyle handStyle, SpellCastHand hand)
            => handStyle == SpellCastHandStyle.OneHand && hand == SpellCastHand.Left;

        /// <summary>Whether a resolved cast is a right-handed one-handed family.</summary>
        private static bool IsRightHandedOneHand(SpellCastHandStyle handStyle, SpellCastHand hand)
            => handStyle == SpellCastHandStyle.OneHand && hand == SpellCastHand.Right;

        /// <summary>
        /// The overlay layer for an instant cast. One-handed casts use the matching gesture mask so
        /// the opposite, weapon-bearing arm holds its base pose. Two-handed families use the full
        /// upper-body overlay (torso + both arms). Neither path takes over the legs.
        /// </summary>
        private static SpellPlaybackLayer ResolveInstantLayer(SpellCastHandStyle handStyle, SpellCastHand hand)
            => IsLeftHandedOneHand(handStyle, hand)
                ? SpellPlaybackLayer.LeftGesture
                : IsRightHandedOneHand(handStyle, hand)
                    ? SpellPlaybackLayer.RightGesture
                    : SpellPlaybackLayer.UpperBody;

        /// <summary>
        /// The loop-capable layer a charged/channel <b>hold</b> (wind-up + loop) plays on. One-handed
        /// families use their matching gesture layer; two-handed families hold on UpperBody.
        /// </summary>
        private static SpellPlaybackLayer ResolveHoldLayer(SpellCastHandStyle handStyle, SpellCastHand hand)
            => IsLeftHandedOneHand(handStyle, hand)
                ? SpellPlaybackLayer.LeftGesture
                : IsRightHandedOneHand(handStyle, hand)
                    ? SpellPlaybackLayer.RightGesture
                    : HoldLayer;

        /// <summary>
        /// The layer a charged <b>release</b> (the final Cast clip) plays on. One-handed families
        /// release on their matching gesture layer; two-handed families keep the owner-approved
        /// UpperBodyWhileMoving release.
        /// </summary>
        private static SpellPlaybackLayer ResolveChargedReleaseLayer(SpellCastHandStyle handStyle, SpellCastHand hand)
            => IsLeftHandedOneHand(handStyle, hand)
                ? SpellPlaybackLayer.LeftGesture
                : IsRightHandedOneHand(handStyle, hand)
                    ? SpellPlaybackLayer.RightGesture
                    : ChargedReleaseLayer;

        /// <summary>
        /// Returns <c>false</c> (with a default entry) when the family has no clips for the requested
        /// hand, or when the archetype's required clips are missing. The resolver then reports that
        /// the semantic motion cannot produce a playable cast animation for the active set.
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
            entry.castOrigin = family.handStyle == SpellCastHandStyle.OneHand
                ? hand == SpellCastHand.Right
                    ? SpellCastOrigin.RightHand
                    : SpellCastOrigin.LeftHand
                : SpellCastOrigin.UseVfxCue;
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
                    entry.clip = snap;
                    entry.presentationMode = SpellAnimationPresentationMode.ReleaseOnly;
                    entry.playbackLayer = ResolveInstantLayer(family.handStyle, hand);
                    entry.combatEntryMode = CombatEntryMode.ImmediateForFullBodyAnimatedAfterUpperBody;
                    return true;
                }

                case SpellAnimationArchetype.Charged:
                    // one-shot (enter) → Load (hold) → Cast (release).
                    if (!triple.HasHold || triple.cast == null)
                        return false;
                    entry.clip = triple.cast;
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

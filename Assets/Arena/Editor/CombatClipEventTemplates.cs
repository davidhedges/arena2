#nullable enable
using System.Collections.Generic;

namespace Arena.Editor
{
    /// <summary>
    /// One required or recommended animation event for a clip of a given role.
    /// Used both by the authoring tool (to stamp default events) and by the validator
    /// (to flag clips missing required events). Single source of truth for event names
    /// and default placement so the two stay in sync.
    /// </summary>
    public readonly struct CombatClipEventTemplate
    {
        public readonly string FunctionName;
        public readonly float DefaultNormalizedTime;
        public readonly bool Required;
        public readonly string Description;

        public CombatClipEventTemplate(
            string functionName,
            float defaultNormalizedTime,
            bool required,
            string description)
        {
            FunctionName = functionName;
            DefaultNormalizedTime = defaultNormalizedTime;
            Required = required;
            Description = description;
        }
    }

    /// <summary>
    /// Per-role templates describing which animation events each clip role should carry.
    /// Empty arrays mean "no events expected." Default normalized times are starting points
    /// for the stamp action; authors refine them per clip in the Animation window.
    /// </summary>
    public static class CombatClipEventTemplates
    {
        public static readonly IReadOnlyDictionary<CombatClipRole, CombatClipEventTemplate[]> ByRole
            = new Dictionary<CombatClipRole, CombatClipEventTemplate[]>
            {
                [CombatClipRole.Unknown] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.Locomotion] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.Dodge] = System.Array.Empty<CombatClipEventTemplate>(),

                [CombatClipRole.SpellCastHoldEnter] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnEnterComplete", 0.85f, required: true,
                        "Hand-off point from cast-enter clip into the looping cast-idle clip."),
                },
                [CombatClipRole.SpellCastHoldIdle] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.SpellCastHoldExit] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnExitComplete", 0.90f, required: true,
                        "Cast-exit clip is finished; return to base presentation."),
                },

                [CombatClipRole.SpellRelease] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnReleaseFrame", 0.50f, required: true,
                        "Visible release moment. VFX/projectile body cue should align here."),
                    new CombatClipEventTemplate(
                        "OnHoldFadeStart", 0.60f, required: true,
                        "When the SpellAction layer should begin fading the cast-hold pose out."),
                    new CombatClipEventTemplate(
                        "OnLowerBodyUnlock", 0.70f, required: false,
                        "When locomotion may regain lower-body control."),
                    new CombatClipEventTemplate(
                        "OnVisualInterruptible", 0.85f, required: false,
                        "Earliest point at which this visual may be cleanly preempted."),
                },

                [CombatClipRole.MeleeStrike] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnStrikeHit", 0.40f, required: true,
                        "Authoritative hit moment. The stamper mirrors and exports it to server gameplay timing."),
                    new CombatClipEventTemplate(
                        "OnLowerBodyUnlock", 0.70f, required: true,
                        "When locomotion may regain lower-body control."),
                    new CombatClipEventTemplate(
                        "OnVisualInterruptible", 0.85f, required: true,
                        "Earliest point at which this visual may be cleanly preempted."),
                },
                [CombatClipRole.PhasedMeleeStart] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnStrikeHit", 0.40f, required: false,
                        "Visible hit moment if this phased melee lands during the start phase."),
                    new CombatClipEventTemplate(
                        "OnPhaseLoopReady", 0.84f, required: true,
                        "Hand-off point from start phase into the looping phase. Earlier markers accelerate the hand-off; the shared strike-state safety exit remains the latest allowed point."),
                },
                [CombatClipRole.PhasedMeleeLoop] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnStrikeHit", 0.40f, required: false,
                        "Visible hit moment if this phased melee lands during the loop phase."),
                },
                [CombatClipRole.PhasedMeleeEnd] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnStrikeHit", 0.40f, required: false,
                        "Visible hit moment if this phased melee lands during the end phase."),
                    new CombatClipEventTemplate(
                        "OnLowerBodyUnlock", 0.70f, required: false,
                        "When locomotion may regain lower-body control."),
                    new CombatClipEventTemplate(
                        "OnVisualInterruptible", 0.85f, required: true,
                        "Earliest point at which this visual may be cleanly preempted."),
                },

                [CombatClipRole.HitReaction] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.Stagger] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnVisualInterruptible", 0.80f, required: false,
                        "Earliest point at which the stagger may be cleanly preempted."),
                },
                [CombatClipRole.StatusReactionLoop] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.KnockdownStart] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnGroundedFrame", 0.85f, required: false,
                        "Visible point at which the character has reached the ground."),
                },
                [CombatClipRole.KnockdownLoop] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.GetUp] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnVisualInterruptible", 0.80f, required: false,
                        "Earliest point at which the get-up may be preempted."),
                },
                [CombatClipRole.StunStart] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.StunLoop] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.StunEnd] = System.Array.Empty<CombatClipEventTemplate>(),

                [CombatClipRole.ParryStart] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnParryWindowStart", 0.10f, required: true,
                        "Earliest frame at which a parry can succeed."),
                    new CombatClipEventTemplate(
                        "OnParryWindowEnd", 0.40f, required: true,
                        "Last frame at which a parry can succeed."),
                },
                [CombatClipRole.ParryHit] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.BlockStart] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnBlockReady", 0.50f, required: true,
                        "Earliest frame at which the block stance is fully active."),
                },
                [CombatClipRole.BlockLoop] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.BlockEnd] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.BlockHit] = System.Array.Empty<CombatClipEventTemplate>(),
                [CombatClipRole.BlockHitBreak] = System.Array.Empty<CombatClipEventTemplate>(),

                [CombatClipRole.Death] = System.Array.Empty<CombatClipEventTemplate>(),

                [CombatClipRole.DrawWeapon] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnWeaponHandoff", 0.45f, required: true,
                        "Frame at which the weapon visual snaps from stowed mount to drawn mount."),
                },
                [CombatClipRole.SheathWeapon] = new[]
                {
                    new CombatClipEventTemplate(
                        "OnWeaponHandoff", 0.70f, required: true,
                        "Frame at which the weapon visual snaps from drawn mount to stowed mount."),
                },
            };

        public static CombatClipEventTemplate[] GetTemplates(CombatClipRole role) =>
            ByRole.TryGetValue(role, out CombatClipEventTemplate[]? templates)
                ? templates
                : System.Array.Empty<CombatClipEventTemplate>();
    }
}

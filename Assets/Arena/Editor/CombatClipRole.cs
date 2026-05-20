#nullable enable

namespace Arena.Editor
{
    /// <summary>
    /// Authoring-only classification of an AnimationClip's role inside a CombatAnimationSet.
    /// Drives which events the clip should carry. Inferred from how the clip is referenced
    /// (e.g. SpellCastHoldProfile.enter → SpellCastHoldEnter), not from naming conventions.
    /// </summary>
    public enum CombatClipRole
    {
        Unknown = 0,

        // Locomotion / non-combat. Events not currently authored on these.
        Locomotion,

        // Cast hold lifecycle.
        SpellCastHoldEnter,
        SpellCastHoldIdle,
        SpellCastHoldExit,

        // Cast-time spell or instant spell release.
        SpellRelease,

        // Melee strike clips.
        MeleeStrike,
        PhasedMeleeStart,
        PhasedMeleeLoop,
        PhasedMeleeEnd,

        // Hit reactions / CC presentation. Currently owned by CombatStatusReactionController.
        HitReaction,
        Stagger,
        StatusReactionLoop,
        KnockdownStart,
        KnockdownLoop,
        GetUp,
        StunStart,
        StunLoop,
        StunEnd,

        // Defense.
        ParryStart,
        ParryHit,
        BlockStart,
        BlockLoop,
        BlockEnd,
        BlockHit,
        BlockHitBreak,

        // Death.
        Death,

        // Weapon transitions.
        DrawWeapon,
        SheathWeapon,
    }
}

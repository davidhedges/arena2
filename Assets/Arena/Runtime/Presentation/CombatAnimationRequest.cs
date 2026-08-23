#nullable enable
using System;
using Arena.Combat;
using UnityEngine;

namespace Arena.Presentation
{
    public enum CombatAnimationCategory
    {
        MeleeSkill = 0,
        AutoAttack = 1,
        Spell = 2,
    }

    public enum CombatAnimationAuthority
    {
        Predicted = 0,
        Authoritative = 1,
        LocalSystem = 2,
    }

    public enum CombatAnimationPlayback
    {
        Automatic = 0,
        SingleClip = 1,
        Phased = 2,
    }

    public enum CombatSpellAnimationPhase
    {
        Release = 0,
        HoldStart = 1,
        Cancel = 2,
    }

    public readonly struct CombatAnimationRequest
    {
        public readonly string ActionId;
        public readonly CombatAnimationCategory Category;
        public readonly CombatAnimationAuthority Authority;
        public readonly CombatAnimationPlayback Playback;
        public readonly CombatSpellAnimationPhase SpellPhase;
        public readonly string? Source;
        public readonly long StartedAtMs;
        /// <summary>
        /// Presentation-only offset into a spell release clip. Cast-speed compression may require
        /// entering after the beginning so OnReleaseFrame still lands on authoritative cast end.
        /// </summary>
        public readonly float SpellPlaybackStartOffsetSeconds;
        public readonly bool HasFacingTargetPoint;
        public readonly Vector3 FacingTargetPoint;
        public readonly string ConsumedModifierStatusKind;
        public readonly string ConsumedModifierStackGroup;
        public readonly bool DrivePhasesFromSpecialMovement;
        public readonly bool ScaleGapClosePhasesFromImpactReach;
        public readonly bool GapCloseUsedMovementAtCast;
        public readonly float GapCloseLoopScale;
        public readonly float AuthoritativeImpactDelaySeconds;
        /// <summary>
        /// Optional request-time replacement for the attack-authored animation
        /// VFX bindings. Null uses the attack bindings; an empty array disables
        /// every semantic slot for this presentation.
        /// </summary>
        public readonly CombatAnimationVfxBinding[]? AnimationVfxBindings;
        public bool HasConsumedModifier =>
            !string.IsNullOrWhiteSpace(ConsumedModifierStatusKind) &&
            !string.IsNullOrWhiteSpace(ConsumedModifierStackGroup);

        public CombatAnimationRequest(
            string actionId,
            CombatAnimationCategory category,
            CombatAnimationAuthority authority,
            CombatAnimationPlayback playback = CombatAnimationPlayback.Automatic,
            CombatSpellAnimationPhase spellPhase = CombatSpellAnimationPhase.Release,
            string? source = null,
            long startedAtMs = 0L,
            Vector3? facingTargetPoint = null,
            string? consumedModifierStatusKind = null,
            string? consumedModifierStackGroup = null,
            bool drivePhasesFromSpecialMovement = false,
            bool scaleGapClosePhasesFromImpactReach = false,
            bool gapCloseUsedMovementAtCast = false,
            float gapCloseLoopScale = 1f,
            float authoritativeImpactDelaySeconds = -1f,
            CombatAnimationVfxBinding[]? animationVfxBindings = null,
            float spellPlaybackStartOffsetSeconds = 0f)
        {
            ActionId = actionId ?? string.Empty;
            Category = category;
            Authority = authority;
            Playback = playback;
            SpellPhase = spellPhase;
            Source = source;
            StartedAtMs = startedAtMs;
            SpellPlaybackStartOffsetSeconds = float.IsNaN(spellPlaybackStartOffsetSeconds)
                || float.IsInfinity(spellPlaybackStartOffsetSeconds)
                    ? 0f
                    : Mathf.Max(0f, spellPlaybackStartOffsetSeconds);
            HasFacingTargetPoint = facingTargetPoint.HasValue;
            FacingTargetPoint = facingTargetPoint ?? Vector3.zero;
            ConsumedModifierStatusKind = WireIdentifier.Normalize(consumedModifierStatusKind);
            ConsumedModifierStackGroup = WireIdentifier.Normalize(consumedModifierStackGroup);
            DrivePhasesFromSpecialMovement = drivePhasesFromSpecialMovement;
            ScaleGapClosePhasesFromImpactReach = scaleGapClosePhasesFromImpactReach;
            GapCloseUsedMovementAtCast = gapCloseUsedMovementAtCast;
            GapCloseLoopScale = Mathf.Clamp01(gapCloseLoopScale);
            AuthoritativeImpactDelaySeconds = authoritativeImpactDelaySeconds;
            AnimationVfxBindings = animationVfxBindings == null
                ? null
                : (CombatAnimationVfxBinding[])animationVfxBindings.Clone();
        }

        public static CombatAnimationRequest PredictedMeleeSkill(
            string actionId,
            long startedAtMs,
            string? source = CombatEventSources.PlayerInput,
            string? consumedModifierStatusKind = null,
            string? consumedModifierStackGroup = null,
            bool drivePhasesFromSpecialMovement = false,
            bool scaleGapClosePhasesFromImpactReach = false,
            bool gapCloseUsedMovementAtCast = false,
            float gapCloseLoopScale = 1f,
            CombatAnimationVfxBinding[]? animationVfxBindings = null)
        {
            return new CombatAnimationRequest(
                actionId,
                CombatAnimationCategory.MeleeSkill,
                CombatAnimationAuthority.Predicted,
                CombatAnimationPlayback.Automatic,
                CombatSpellAnimationPhase.Release,
                source,
                startedAtMs,
                consumedModifierStatusKind: consumedModifierStatusKind,
                consumedModifierStackGroup: consumedModifierStackGroup,
                drivePhasesFromSpecialMovement: drivePhasesFromSpecialMovement,
                scaleGapClosePhasesFromImpactReach: scaleGapClosePhasesFromImpactReach,
                gapCloseUsedMovementAtCast: gapCloseUsedMovementAtCast,
                gapCloseLoopScale: gapCloseLoopScale,
                animationVfxBindings: animationVfxBindings);
        }

        public static CombatAnimationRequest PredictedSpell(
            string actionId,
            long startedAtMs,
            string? source = CombatEventSources.Spell)
        {
            return new CombatAnimationRequest(
                actionId,
                CombatAnimationCategory.Spell,
                CombatAnimationAuthority.Predicted,
                CombatAnimationPlayback.Automatic,
                CombatSpellAnimationPhase.Release,
                source,
                startedAtMs);
        }

        public static CombatAnimationRequest PredictedSpellHoldStart(
            string actionId,
            long startedAtMs,
            Vector3? facingTargetPoint = null,
            string? source = CombatEventSources.PlayerInput)
        {
            return new CombatAnimationRequest(
                actionId,
                CombatAnimationCategory.Spell,
                CombatAnimationAuthority.Predicted,
                CombatAnimationPlayback.Automatic,
                CombatSpellAnimationPhase.HoldStart,
                source,
                startedAtMs,
                facingTargetPoint);
        }

        public static CombatAnimationRequest Authoritative(
            string actionId,
            CombatAnimationCategory category,
            long startedAtMs,
            string? source = null,
            Vector3? facingTargetPoint = null,
            string? consumedModifierStatusKind = null,
            string? consumedModifierStackGroup = null,
            bool drivePhasesFromSpecialMovement = false,
            bool scaleGapClosePhasesFromImpactReach = false,
            bool gapCloseUsedMovementAtCast = false,
            float authoritativeImpactDelaySeconds = -1f,
            CombatAnimationVfxBinding[]? animationVfxBindings = null)
        {
            return new CombatAnimationRequest(
                actionId,
                category,
                CombatAnimationAuthority.Authoritative,
                CombatAnimationPlayback.Automatic,
                CombatSpellAnimationPhase.Release,
                source,
                startedAtMs,
                facingTargetPoint,
                consumedModifierStatusKind,
                consumedModifierStackGroup,
                drivePhasesFromSpecialMovement: drivePhasesFromSpecialMovement,
                scaleGapClosePhasesFromImpactReach: scaleGapClosePhasesFromImpactReach,
                gapCloseUsedMovementAtCast: gapCloseUsedMovementAtCast,
                authoritativeImpactDelaySeconds: authoritativeImpactDelaySeconds,
                animationVfxBindings: animationVfxBindings);
        }

        public static CombatAnimationRequest AuthoritativeSpell(
            string actionId,
            long startedAtMs,
            CombatSpellAnimationPhase phase,
            string? source = CombatEventSources.Spell,
            Vector3? facingTargetPoint = null,
            float playbackStartOffsetSeconds = 0f)
        {
            return new CombatAnimationRequest(
                actionId,
                CombatAnimationCategory.Spell,
                CombatAnimationAuthority.Authoritative,
                CombatAnimationPlayback.Automatic,
                phase,
                source,
                startedAtMs,
                facingTargetPoint,
                spellPlaybackStartOffsetSeconds: playbackStartOffsetSeconds);
        }

        public static CombatAnimationCategory ResolveMeleeCategory(string? source)
        {
            return string.Equals(source, CombatEventSources.AutoAttack, StringComparison.Ordinal)
                ? CombatAnimationCategory.AutoAttack
                : CombatAnimationCategory.MeleeSkill;
        }

        public override string ToString()
        {
            string facing = HasFacingTargetPoint
                ? $" target=({FacingTargetPoint.x:F2},{FacingTargetPoint.y:F2},{FacingTargetPoint.z:F2})"
                : string.Empty;
            string modifier = HasConsumedModifier
                ? $" consumedModifier={ConsumedModifierStatusKind}:{ConsumedModifierStackGroup}"
                : string.Empty;
            string specialMovement = DrivePhasesFromSpecialMovement
                ? " specialMovementPhased=True"
                : string.Empty;
            string impactReachScaled = ScaleGapClosePhasesFromImpactReach
                ? $" impactReachScaled=True movedAtCast={GapCloseUsedMovementAtCast} loopScale={GapCloseLoopScale:F2} impactDelay={AuthoritativeImpactDelaySeconds:F3}"
                : string.Empty;
            string animationVfx = AnimationVfxBindings != null
                ? $" animationVfxBindings={AnimationVfxBindings.Length}"
                : string.Empty;
            string spellPhase = Category == CombatAnimationCategory.Spell
                ? $" spellPhase={SpellPhase}"
                : string.Empty;
            return $"action={ActionId} category={Category}{spellPhase} authority={Authority} playback={Playback} source={Source ?? "-"} startedAtMs={StartedAtMs}{facing}{modifier}{specialMovement}{impactReachScaled}{animationVfx}";
        }
    }
}

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
        public readonly bool HasFacingTargetPoint;
        public readonly Vector3 FacingTargetPoint;
        public readonly string ConsumedModifierStatusKind;
        public readonly string ConsumedModifierStackGroup;
        public readonly bool DrivePhasesFromSpecialMovement;
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
            bool drivePhasesFromSpecialMovement = false)
        {
            ActionId = actionId ?? string.Empty;
            Category = category;
            Authority = authority;
            Playback = playback;
            SpellPhase = spellPhase;
            Source = source;
            StartedAtMs = startedAtMs;
            HasFacingTargetPoint = facingTargetPoint.HasValue;
            FacingTargetPoint = facingTargetPoint ?? Vector3.zero;
            ConsumedModifierStatusKind = WireIdentifier.Normalize(consumedModifierStatusKind);
            ConsumedModifierStackGroup = WireIdentifier.Normalize(consumedModifierStackGroup);
            DrivePhasesFromSpecialMovement = drivePhasesFromSpecialMovement;
        }

        public static CombatAnimationRequest PredictedMeleeSkill(
            string actionId,
            long startedAtMs,
            string? source = CombatEventSources.PlayerInput,
            string? consumedModifierStatusKind = null,
            string? consumedModifierStackGroup = null,
            bool drivePhasesFromSpecialMovement = false)
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
                drivePhasesFromSpecialMovement: drivePhasesFromSpecialMovement);
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
            bool drivePhasesFromSpecialMovement = false)
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
                drivePhasesFromSpecialMovement);
        }

        public static CombatAnimationRequest AuthoritativeSpell(
            string actionId,
            long startedAtMs,
            CombatSpellAnimationPhase phase,
            string? source = CombatEventSources.Spell,
            Vector3? facingTargetPoint = null)
        {
            return new CombatAnimationRequest(
                actionId,
                CombatAnimationCategory.Spell,
                CombatAnimationAuthority.Authoritative,
                CombatAnimationPlayback.Automatic,
                phase,
                source,
                startedAtMs,
                facingTargetPoint);
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
            string spellPhase = Category == CombatAnimationCategory.Spell
                ? $" spellPhase={SpellPhase}"
                : string.Empty;
            return $"action={ActionId} category={Category}{spellPhase} authority={Authority} playback={Playback} source={Source ?? "-"} startedAtMs={StartedAtMs}{facing}{modifier}{specialMovement}";
        }
    }
}

#nullable enable
using Arena.Combat;
using Arena.Entity;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    internal static class CombatAnimationRequestTranslator
    {
        private const string ImpactReachGapCloseActiveSequence = "IMPACT_REACH_GAP_CLOSE_ACTIVE";
        public static CombatAnimationRequest BuildActorNeutralAuthoritativeFromCombatEvent(
            CombatEvent row)
        {
            bool isSpell = string.Equals(
                row.SourceKind,
                CombatEventSources.Spell,
                System.StringComparison.Ordinal);
            string actionId = string.IsNullOrWhiteSpace(row.AbilityId)
                ? row.ActionKind
                : row.AbilityId;
            return CombatAnimationRequest.Authoritative(
                actionId,
                isSpell
                    ? CombatAnimationCategory.Spell
                    : CombatAnimationRequest.ResolveMeleeCategory(row.SourceKind),
                row.CreatedAt.MicrosecondsSinceUnixEpoch / 1000L,
                row.SourceKind,
                new Vector3(row.PointX, row.PointY, row.PointZ));
        }

        public static bool IsAnimationStartEvent(CombatEvent row)
        {
            return string.Equals(row.EventType, CombatEventTypes.Cast, System.StringComparison.Ordinal)
                && row.HitIndex < 0;
        }

        public static CombatAnimationRequest BuildAuthoritativeFromCombatEvent(
            DbConnection? conn,
            PlayerEntity entity,
            CombatEvent row)
        {
            string combatProfile = conn != null
                ? RuntimeCombatProfile.ResolveForEntity(conn, entity)
                : string.Empty;
            if (conn != null
                && CombatActionIds.FindMeleeDefinition(conn, combatProfile, row.ActionKind) != null)
            {
                return CombatAnimationRequest.Authoritative(
                    row.ActionKind,
                    CombatAnimationRequest.ResolveMeleeCategory(row.SourceKind),
                    row.CreatedAt.MicrosecondsSinceUnixEpoch / 1000L,
                    row.SourceKind,
                    new Vector3(row.PointX, row.PointY, row.PointZ),
                    string.Equals(row.MetadataKind, CombatEventMetadataKinds.ConsumedMeleeModifier, System.StringComparison.Ordinal)
                        ? row.MetadataKey
                        : string.Empty,
                    string.Equals(row.MetadataKind, CombatEventMetadataKinds.ConsumedMeleeModifier, System.StringComparison.Ordinal)
                        ? row.MetadataValue
                        : string.Empty,
                    ShouldDriveMeleePhasesFromSpecialMovement(conn, combatProfile, row),
                    ScaleMeleeGapClosePhasesFromImpactReach(conn, combatProfile, row),
                    string.Equals(
                        row.SequenceKind,
                        ImpactReachGapCloseActiveSequence,
                        System.StringComparison.Ordinal),
                    Mathf.Max(0f, row.ScalarValue));
            }

            return CombatAnimationRequest.Authoritative(
                row.ActionKind,
                CombatAnimationCategory.Spell,
                row.CreatedAt.MicrosecondsSinceUnixEpoch / 1000L,
                row.SourceKind);
        }

        private static bool ShouldDriveMeleePhasesFromSpecialMovement(
            DbConnection conn,
            string combatProfile,
            CombatEvent row)
        {
            SpecialMovementRuntime? runtime = conn.Db.SpecialMovementRuntime.Owner.Find(row.Caster);
            if (runtime == null)
                return false;

            CombatAnimationSet? animationSet = CombatAnimationSetCatalog.Resolve(combatProfile);
            if (animationSet == null
                || !animationSet.TryGetPhasedMeleeEntry(row.ActionKind, out WeaponPhasedActionEntry entry)
                || !entry.drivePhasesFromSpecialMovement)
            {
                return false;
            }

            string runtimeActionId = CombatActionIds.ResolveRuntimeActionId(
                conn,
                combatProfile,
                row.ActionKind);
            string expectedKind = $"MELEE_GAP_CLOSE:{runtimeActionId}";
            return string.Equals(runtime.Kind, expectedKind, System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool ScaleMeleeGapClosePhasesFromImpactReach(
            DbConnection conn,
            string combatProfile,
            CombatEvent row)
        {
            MeleeGapCloseCatalog? gapClose = string.IsNullOrWhiteSpace(row.AbilityId)
                ? null
                : conn.Db.MeleeGapCloseCatalog.AbilityId.Find(row.AbilityId);
            if (gapClose?.ActivateOutsideImpactReach != true)
                return false;

            CombatAnimationSet? animationSet = CombatAnimationSetCatalog.Resolve(combatProfile);
            return animationSet != null
                && animationSet.TryGetPhasedMeleeEntry(row.ActionKind, out WeaponPhasedActionEntry entry)
                && entry.drivePhasesFromSpecialMovement
                && entry.scaleGapClosePhasesFromImpactReach;
        }
    }
}

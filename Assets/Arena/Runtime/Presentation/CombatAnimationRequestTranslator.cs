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
        public static CombatAnimationRequest BuildActorNeutralAuthoritativeFromCombatEvent(
            CombatEvent row)
        {
            bool isSpell = string.Equals(
                row.SourceKind,
                CombatEventSources.Spell,
                System.StringComparison.Ordinal);
            return CombatAnimationRequest.Authoritative(
                row.ActionKind,
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
                ? CombatProfileResolver.ResolveForEntity(conn, entity)
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
                    ShouldDriveMeleePhasesFromSpecialMovement(conn, combatProfile, row));
            }

            return BuildActorNeutralAuthoritativeFromCombatEvent(row);
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
    }
}

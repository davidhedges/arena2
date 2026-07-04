#nullable enable
using Arena.Combat;
using Arena.Entity;
using Arena.Input;
using Arena.Simulation;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Arena.Presentation
{
    internal static class CombatAnimationReplayPolicy
    {
        public static bool ShouldSuppressPredictedLocalReplay(
            DbConnection? conn,
            PlayerEntity entity,
            in CombatAnimationRequest request,
            long nowMs,
            string actionInstanceId = "")
        {
            if (request.Authority != CombatAnimationAuthority.Authoritative)
                return false;

            return request.Category switch
            {
                CombatAnimationCategory.MeleeSkill or CombatAnimationCategory.AutoAttack =>
                    ConsumePredictedMeleeReplay(conn, entity, request, nowMs, actionInstanceId),
                CombatAnimationCategory.Spell =>
                    ConsumePredictedSpellReplay(entity, request, nowMs, actionInstanceId),
                _ => false,
            };
        }

        private static bool ConsumePredictedMeleeReplay(
            DbConnection? conn,
            PlayerEntity entity,
            in CombatAnimationRequest request,
            long nowMs,
            string actionInstanceId)
        {
            if (conn == null)
                return false;

            // Locally scheduled auto-attack swings (netcode design review S6)
            // consume their authoritative CAST here; press-predicted melee
            // keeps the MeleeInputHandler path below.
            if (string.Equals(request.Source, CombatEventSources.AutoAttack, System.StringComparison.Ordinal))
            {
                return AutoAttackSwingScheduler.HandleAuthoritativeLocalAutoAttackCast(
                    conn,
                    entity,
                    actionInstanceId,
                    request,
                    nowMs);
            }

            if (!CombatEventSources.IsPredictedLocalMeleeSource(request.Source))
                return false;

            if (MeleeInputHandler.Instance?.HandleAuthoritativeLocalMeleeReplay(
                    conn,
                    entity,
                    actionInstanceId,
                    request,
                    nowMs) != true)
            {
                return false;
            }

            return true;
        }

        private static bool ConsumePredictedSpellReplay(
            PlayerEntity entity,
            in CombatAnimationRequest request,
            long nowMs,
            string actionInstanceId)
        {
            if (SpellInputHandler.Instance?.HandleAuthoritativeLocalSpellReplay(
                    entity,
                    actionInstanceId,
                    request,
                    nowMs) != true)
            {
                return false;
            }

            return true;
        }
    }
}

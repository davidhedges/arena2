#nullable enable

using SpacetimeDB;
using SpacetimeDB.Types;
using Arena.Entity;
using Arena.Input;
using Arena.Match;
using Arena.Simulation;

namespace Arena.Network
{
    internal static class NetworkCallbackBinder
    {
        internal static void BindRuntimeCallbacks(DbConnection conn, EntityRegistry registry, MatchStateCache match, LocalCombatState combat, Identity localIdentity)
        {
            conn.Db.PlayerPhysics.OnInsert += registry.OnPlayerPhysicsInsert;
            conn.Db.PlayerPhysics.OnUpdate += registry.OnPlayerPhysicsUpdate;
            conn.Db.PlayerPhysics.OnDelete += registry.OnPlayerPhysicsDelete;

            conn.Db.Player.OnInsert += registry.OnPlayerInsert;
            conn.Db.Player.OnUpdate += registry.OnPlayerUpdate;
            conn.Db.Player.OnDelete += registry.OnPlayerDelete;

            conn.Db.CharacterAppearance.OnInsert += registry.OnCharacterAppearanceInsert;
            conn.Db.CharacterAppearance.OnUpdate += registry.OnCharacterAppearanceUpdate;
            conn.Db.CharacterAppearance.OnDelete += registry.OnCharacterAppearanceDelete;

            conn.Db.PlayerState.OnInsert += registry.OnPlayerStateInsert;
            conn.Db.PlayerState.OnUpdate += registry.OnPlayerStateUpdate;
            conn.Db.PlayerState.OnDelete += registry.OnPlayerStateDelete;

            conn.Db.CharacterProgression.OnInsert += registry.OnCharacterProgressionInsert;
            conn.Db.CharacterProgression.OnUpdate += registry.OnCharacterProgressionUpdate;
            conn.Db.CharacterProgression.OnDelete += registry.OnCharacterProgressionDelete;

            conn.Db.CombatEngagement.OnInsert += registry.OnCombatEngagementInsert;
            conn.Db.CombatEngagement.OnUpdate += registry.OnCombatEngagementUpdate;
            conn.Db.CombatEngagement.OnDelete += registry.OnCombatEngagementDelete;

            conn.Db.PlayerWorld.OnInsert += registry.OnPlayerWorldInsert;
            conn.Db.PlayerWorld.OnUpdate += registry.OnPlayerWorldUpdate;
            conn.Db.PlayerWorld.OnDelete += registry.OnPlayerWorldDelete;

            conn.Db.PlayerOpenWorldScene.OnInsert += registry.OnPlayerOpenWorldSceneInsert;
            conn.Db.PlayerOpenWorldScene.OnUpdate += registry.OnPlayerOpenWorldSceneUpdate;
            conn.Db.PlayerOpenWorldScene.OnDelete += registry.OnPlayerOpenWorldSceneDelete;

            conn.Db.ArenaInstance.OnInsert += registry.OnArenaInstanceInsert;
            conn.Db.ArenaInstance.OnUpdate += registry.OnArenaInstanceUpdate;
            conn.Db.ArenaInstance.OnDelete += registry.OnArenaInstanceDelete;

            conn.Db.ArenaInstance.OnInsert += match.OnArenaInstanceInsert;
            conn.Db.ArenaInstance.OnUpdate += match.OnArenaInstanceUpdate;
            conn.Db.ArenaInstance.OnDelete += match.OnArenaInstanceDelete;

            conn.Db.StatusEffect.OnInsert += registry.OnStatusEffectInsert;
            conn.Db.StatusEffect.OnUpdate += registry.OnStatusEffectUpdate;
            conn.Db.StatusEffect.OnDelete += registry.OnStatusEffectDelete;

            conn.Db.PlayerResource.OnInsert += registry.OnPlayerResourceInsert;
            conn.Db.PlayerResource.OnUpdate += registry.OnPlayerResourceUpdate;
            conn.Db.PlayerResource.OnDelete += registry.OnPlayerResourceDelete;

            conn.Db.DefenseState.OnInsert += registry.OnDefenseStateInsert;
            conn.Db.DefenseState.OnUpdate += registry.OnDefenseStateUpdate;
            conn.Db.DefenseState.OnDelete += registry.OnDefenseStateDelete;

            combat.Bind(localIdentity);
            conn.Db.GlobalCooldown.OnInsert += combat.OnGlobalCooldownInsert;
            conn.Db.GlobalCooldown.OnUpdate += combat.OnGlobalCooldownUpdate;
            conn.Db.GlobalCooldown.OnDelete += combat.OnGlobalCooldownDelete;

            conn.Db.SpellCooldown.OnInsert += combat.OnSpellCooldownInsert;
            conn.Db.SpellCooldown.OnUpdate += combat.OnSpellCooldownUpdate;
            conn.Db.SpellCooldown.OnDelete += combat.OnSpellCooldownDelete;

            conn.Db.FixedActionChargeState.OnInsert += combat.OnFixedActionChargeStateInsert;
            conn.Db.FixedActionChargeState.OnUpdate += combat.OnFixedActionChargeStateUpdate;
            conn.Db.FixedActionChargeState.OnDelete += combat.OnFixedActionChargeStateDelete;

            // Predicted action results reconcile local prediction state and presentation.
            conn.Db.PredictedActionResult.OnInsert += combat.OnPredictedActionResultInsert;
            conn.Db.PredictedActionResult.OnInsert += registry.OnPredictedActionResultInsert;
            conn.Db.PredictedActionResult.OnInsert += MeleeInputHandler.OnPredictedActionResultInsert;
            conn.Db.PredictedActionResult.OnInsert += SpellInputHandler.OnPredictedActionResultInsert;
            conn.Db.PredictedActionResult.OnInsert += FixedActionDispatcher.OnPredictedActionResultInsert;

            conn.Db.ActiveCast.OnInsert += combat.OnActiveCastInsert;
            conn.Db.ActiveCast.OnUpdate += combat.OnActiveCastUpdate;
            conn.Db.ActiveCast.OnDelete += combat.OnActiveCastDelete;

            conn.Db.ActiveCast.OnInsert += registry.OnActiveCastInsert;
            conn.Db.ActiveCast.OnUpdate += registry.OnActiveCastUpdate;
            conn.Db.ActiveCast.OnDelete += registry.OnActiveCastDelete;

            conn.Db.MovementActionState.OnInsert += combat.OnMovementActionStateInsert;
            conn.Db.MovementActionState.OnUpdate += combat.OnMovementActionStateUpdate;
            conn.Db.MovementActionState.OnDelete += combat.OnMovementActionStateDelete;

            conn.Db.MovementActionState.OnInsert += registry.OnMovementActionStateInsert;
            conn.Db.MovementActionState.OnUpdate += registry.OnMovementActionStateUpdate;
            conn.Db.MovementActionState.OnDelete += registry.OnMovementActionStateDelete;

            conn.Db.SpecialMovementRuntime.OnInsert += registry.OnSpecialMovementRuntimeInsert;
            conn.Db.SpecialMovementRuntime.OnUpdate += registry.OnSpecialMovementRuntimeUpdate;
            conn.Db.SpecialMovementRuntime.OnDelete += registry.OnSpecialMovementRuntimeDelete;

            conn.Db.CombatEvent.OnInsert += registry.OnCombatEventInsert;
            conn.Db.ProjectilePresentationEvent.OnInsert += registry.OnProjectilePresentationEventInsert;
        }
    }
}

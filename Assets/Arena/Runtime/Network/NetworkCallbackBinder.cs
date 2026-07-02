#nullable enable

using SpacetimeDB;
using SpacetimeDB.Types;
using Arena.Debugging;
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
            NetcodeReceiveCounters.ResetForNetworkReconnect();
            BindReceiveCounters(conn);

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

            conn.Db.CombatEngagement.OnInsert += registry.OnCombatEngagementInsert;
            conn.Db.CombatEngagement.OnUpdate += registry.OnCombatEngagementUpdate;
            conn.Db.CombatEngagement.OnDelete += registry.OnCombatEngagementDelete;

            conn.Db.PlayerWorld.OnInsert += registry.OnPlayerWorldInsert;
            conn.Db.PlayerWorld.OnUpdate += registry.OnPlayerWorldUpdate;
            conn.Db.PlayerWorld.OnDelete += registry.OnPlayerWorldDelete;

            conn.Db.NpcInstance.OnInsert += registry.OnNpcInstanceInsert;
            conn.Db.NpcInstance.OnUpdate += registry.OnNpcInstanceUpdate;
            conn.Db.NpcInstance.OnDelete += registry.OnNpcInstanceDelete;

            conn.Db.NpcPhysics.OnInsert += registry.OnNpcPhysicsInsert;
            conn.Db.NpcPhysics.OnUpdate += registry.OnNpcPhysicsUpdate;
            conn.Db.NpcPhysics.OnDelete += registry.OnNpcPhysicsDelete;

            conn.Db.NpcState.OnInsert += registry.OnNpcStateInsert;
            conn.Db.NpcState.OnUpdate += registry.OnNpcStateUpdate;
            conn.Db.NpcState.OnDelete += registry.OnNpcStateDelete;

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

            conn.Db.EquipmentLoadout.OnInsert += registry.OnEquipmentLoadoutInsert;
            conn.Db.EquipmentLoadout.OnUpdate += registry.OnEquipmentLoadoutUpdate;
            conn.Db.EquipmentLoadout.OnDelete += registry.OnEquipmentLoadoutDelete;

            conn.Db.ActiveCombatDiscipline.OnInsert += registry.OnActiveCombatDisciplineInsert;
            conn.Db.ActiveCombatDiscipline.OnUpdate += registry.OnActiveCombatDisciplineUpdate;
            conn.Db.ActiveCombatDiscipline.OnDelete += registry.OnActiveCombatDisciplineDelete;

            conn.Db.ActiveCombatMode.OnInsert += registry.OnActiveCombatModeInsert;
            conn.Db.ActiveCombatMode.OnUpdate += registry.OnActiveCombatModeUpdate;
            conn.Db.ActiveCombatMode.OnDelete += registry.OnActiveCombatModeDelete;

            conn.Db.ItemInstance.OnInsert += registry.OnItemInstanceInsert;
            conn.Db.ItemInstance.OnUpdate += registry.OnItemInstanceUpdate;
            conn.Db.ItemInstance.OnDelete += registry.OnItemInstanceDelete;

            conn.Db.ItemDefinition.OnInsert += registry.OnItemDefinitionInsert;
            conn.Db.ItemDefinition.OnUpdate += registry.OnItemDefinitionUpdate;

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

        /// <summary>
        /// Overlay-only receive counters (netcode audit R3, client half).
        /// One hit per row callback, keyed by table name. Behavior-free.
        /// </summary>
        private static void BindReceiveCounters(DbConnection conn)
        {
            conn.Db.PlayerPhysics.OnInsert += (_, _) => NetcodeReceiveCounters.Record("player_physics");
            conn.Db.PlayerPhysics.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("player_physics");
            conn.Db.PlayerPhysics.OnDelete += (_, _) => NetcodeReceiveCounters.Record("player_physics");

            conn.Db.Player.OnInsert += (_, _) => NetcodeReceiveCounters.Record("player");
            conn.Db.Player.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("player");
            conn.Db.Player.OnDelete += (_, _) => NetcodeReceiveCounters.Record("player");

            conn.Db.PlayerState.OnInsert += (_, _) => NetcodeReceiveCounters.Record("player_state");
            conn.Db.PlayerState.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("player_state");
            conn.Db.PlayerState.OnDelete += (_, _) => NetcodeReceiveCounters.Record("player_state");

            conn.Db.PlayerResource.OnInsert += (_, _) => NetcodeReceiveCounters.Record("player_resource");
            conn.Db.PlayerResource.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("player_resource");
            conn.Db.PlayerResource.OnDelete += (_, _) => NetcodeReceiveCounters.Record("player_resource");

            conn.Db.StatusEffect.OnInsert += (_, _) => NetcodeReceiveCounters.Record("status_effect");
            conn.Db.StatusEffect.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("status_effect");
            conn.Db.StatusEffect.OnDelete += (_, _) => NetcodeReceiveCounters.Record("status_effect");

            conn.Db.NpcPhysics.OnInsert += (_, _) => NetcodeReceiveCounters.Record("npc_physics");
            conn.Db.NpcPhysics.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("npc_physics");
            conn.Db.NpcPhysics.OnDelete += (_, _) => NetcodeReceiveCounters.Record("npc_physics");

            conn.Db.NpcState.OnInsert += (_, _) => NetcodeReceiveCounters.Record("npc_state");
            conn.Db.NpcState.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("npc_state");
            conn.Db.NpcState.OnDelete += (_, _) => NetcodeReceiveCounters.Record("npc_state");

            conn.Db.CombatEvent.OnInsert += (_, _) => NetcodeReceiveCounters.Record("combat_event");
            conn.Db.CombatEffectEvent.OnInsert += (_, _) => NetcodeReceiveCounters.Record("combat_effect_event");
            conn.Db.ProjectilePresentationEvent.OnInsert += (_, _) => NetcodeReceiveCounters.Record("projectile_presentation_event");

            conn.Db.ActiveCast.OnInsert += (_, _) => NetcodeReceiveCounters.Record("active_cast");
            conn.Db.ActiveCast.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("active_cast");
            conn.Db.ActiveCast.OnDelete += (_, _) => NetcodeReceiveCounters.Record("active_cast");

            conn.Db.SpellCooldown.OnInsert += (_, _) => NetcodeReceiveCounters.Record("spell_cooldown");
            conn.Db.SpellCooldown.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("spell_cooldown");
            conn.Db.SpellCooldown.OnDelete += (_, _) => NetcodeReceiveCounters.Record("spell_cooldown");

            conn.Db.GlobalCooldown.OnInsert += (_, _) => NetcodeReceiveCounters.Record("global_cooldown");
            conn.Db.GlobalCooldown.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("global_cooldown");
            conn.Db.GlobalCooldown.OnDelete += (_, _) => NetcodeReceiveCounters.Record("global_cooldown");

            conn.Db.FixedActionChargeState.OnInsert += (_, _) => NetcodeReceiveCounters.Record("fixed_action_charge_state");
            conn.Db.FixedActionChargeState.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("fixed_action_charge_state");
            conn.Db.FixedActionChargeState.OnDelete += (_, _) => NetcodeReceiveCounters.Record("fixed_action_charge_state");

            conn.Db.MovementActionState.OnInsert += (_, _) => NetcodeReceiveCounters.Record("movement_action_state");
            conn.Db.MovementActionState.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("movement_action_state");
            conn.Db.MovementActionState.OnDelete += (_, _) => NetcodeReceiveCounters.Record("movement_action_state");

            conn.Db.SpecialMovementRuntime.OnInsert += (_, _) => NetcodeReceiveCounters.Record("special_movement_runtime");
            conn.Db.SpecialMovementRuntime.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("special_movement_runtime");
            conn.Db.SpecialMovementRuntime.OnDelete += (_, _) => NetcodeReceiveCounters.Record("special_movement_runtime");

            conn.Db.DefenseState.OnInsert += (_, _) => NetcodeReceiveCounters.Record("defense_state");
            conn.Db.DefenseState.OnUpdate += (_, _, _) => NetcodeReceiveCounters.Record("defense_state");
            conn.Db.DefenseState.OnDelete += (_, _) => NetcodeReceiveCounters.Record("defense_state");

            conn.Db.PredictedActionResult.OnInsert += (_, row) =>
            {
                NetcodeReceiveCounters.Record("predicted_action_result");
                NetcodeReceiveCounters.RecordPredictedActionResult(row);
            };
        }
    }
}

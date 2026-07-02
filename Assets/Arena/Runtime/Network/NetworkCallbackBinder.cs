#nullable enable

using SpacetimeDB;
using SpacetimeDB.BSATN;
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
        /// <summary>
        /// Routes a row callback through the dev-only receive-delay queue
        /// (feel audit F2c). With NetworkCallbackDelay disabled — the default —
        /// Dispatch invokes inline and this is a plain pass-through.
        /// </summary>
        private static RemoteTableHandleBase<EventContext, TRow>.RowEventHandler Delayed<TRow>(
            RemoteTableHandleBase<EventContext, TRow>.RowEventHandler handler)
            where TRow : class, IStructuralReadWrite, new()
            => (ctx, row) => NetworkCallbackDelay.Dispatch(() => handler(ctx, row));

        private static RemoteTableHandleBase<EventContext, TRow>.UpdateEventHandler Delayed<TRow>(
            RemoteTableHandleBase<EventContext, TRow>.UpdateEventHandler handler)
            where TRow : class, IStructuralReadWrite, new()
            => (ctx, oldRow, newRow) => NetworkCallbackDelay.Dispatch(() => handler(ctx, oldRow, newRow));

        internal static void BindRuntimeCallbacks(DbConnection conn, EntityRegistry registry, MatchStateCache match, LocalCombatState combat, Identity localIdentity)
        {
            NetcodeReceiveCounters.ResetForNetworkReconnect();
            NetworkCallbackDelay.ResetForNetworkReconnect();
            BindReceiveCounters(conn);

            conn.Db.PlayerPhysics.OnInsert += Delayed<PlayerPhysics>(registry.OnPlayerPhysicsInsert);
            conn.Db.PlayerPhysics.OnUpdate += Delayed<PlayerPhysics>(registry.OnPlayerPhysicsUpdate);
            conn.Db.PlayerPhysics.OnDelete += Delayed<PlayerPhysics>(registry.OnPlayerPhysicsDelete);

            conn.Db.Player.OnInsert += Delayed<Player>(registry.OnPlayerInsert);
            conn.Db.Player.OnUpdate += Delayed<Player>(registry.OnPlayerUpdate);
            conn.Db.Player.OnDelete += Delayed<Player>(registry.OnPlayerDelete);

            conn.Db.CharacterAppearance.OnInsert += Delayed<CharacterAppearance>(registry.OnCharacterAppearanceInsert);
            conn.Db.CharacterAppearance.OnUpdate += Delayed<CharacterAppearance>(registry.OnCharacterAppearanceUpdate);
            conn.Db.CharacterAppearance.OnDelete += Delayed<CharacterAppearance>(registry.OnCharacterAppearanceDelete);

            conn.Db.PlayerState.OnInsert += Delayed<PlayerState>(registry.OnPlayerStateInsert);
            conn.Db.PlayerState.OnUpdate += Delayed<PlayerState>(registry.OnPlayerStateUpdate);
            conn.Db.PlayerState.OnDelete += Delayed<PlayerState>(registry.OnPlayerStateDelete);

            conn.Db.CombatEngagement.OnInsert += Delayed<CombatEngagement>(registry.OnCombatEngagementInsert);
            conn.Db.CombatEngagement.OnUpdate += Delayed<CombatEngagement>(registry.OnCombatEngagementUpdate);
            conn.Db.CombatEngagement.OnDelete += Delayed<CombatEngagement>(registry.OnCombatEngagementDelete);

            conn.Db.PlayerWorld.OnInsert += Delayed<PlayerWorld>(registry.OnPlayerWorldInsert);
            conn.Db.PlayerWorld.OnUpdate += Delayed<PlayerWorld>(registry.OnPlayerWorldUpdate);
            conn.Db.PlayerWorld.OnDelete += Delayed<PlayerWorld>(registry.OnPlayerWorldDelete);

            conn.Db.NpcInstance.OnInsert += Delayed<NpcInstance>(registry.OnNpcInstanceInsert);
            conn.Db.NpcInstance.OnUpdate += Delayed<NpcInstance>(registry.OnNpcInstanceUpdate);
            conn.Db.NpcInstance.OnDelete += Delayed<NpcInstance>(registry.OnNpcInstanceDelete);

            conn.Db.NpcPhysics.OnInsert += Delayed<NpcPhysics>(registry.OnNpcPhysicsInsert);
            conn.Db.NpcPhysics.OnUpdate += Delayed<NpcPhysics>(registry.OnNpcPhysicsUpdate);
            conn.Db.NpcPhysics.OnDelete += Delayed<NpcPhysics>(registry.OnNpcPhysicsDelete);

            conn.Db.NpcState.OnInsert += Delayed<NpcState>(registry.OnNpcStateInsert);
            conn.Db.NpcState.OnUpdate += Delayed<NpcState>(registry.OnNpcStateUpdate);
            conn.Db.NpcState.OnDelete += Delayed<NpcState>(registry.OnNpcStateDelete);

            conn.Db.PlayerOpenWorldScene.OnInsert += Delayed<PlayerOpenWorldScene>(registry.OnPlayerOpenWorldSceneInsert);
            conn.Db.PlayerOpenWorldScene.OnUpdate += Delayed<PlayerOpenWorldScene>(registry.OnPlayerOpenWorldSceneUpdate);
            conn.Db.PlayerOpenWorldScene.OnDelete += Delayed<PlayerOpenWorldScene>(registry.OnPlayerOpenWorldSceneDelete);

            conn.Db.ArenaInstance.OnInsert += Delayed<ArenaInstance>(registry.OnArenaInstanceInsert);
            conn.Db.ArenaInstance.OnUpdate += Delayed<ArenaInstance>(registry.OnArenaInstanceUpdate);
            conn.Db.ArenaInstance.OnDelete += Delayed<ArenaInstance>(registry.OnArenaInstanceDelete);

            conn.Db.ArenaInstance.OnInsert += Delayed<ArenaInstance>(match.OnArenaInstanceInsert);
            conn.Db.ArenaInstance.OnUpdate += Delayed<ArenaInstance>(match.OnArenaInstanceUpdate);
            conn.Db.ArenaInstance.OnDelete += Delayed<ArenaInstance>(match.OnArenaInstanceDelete);

            conn.Db.StatusEffect.OnInsert += Delayed<StatusEffect>(registry.OnStatusEffectInsert);
            conn.Db.StatusEffect.OnUpdate += Delayed<StatusEffect>(registry.OnStatusEffectUpdate);
            conn.Db.StatusEffect.OnDelete += Delayed<StatusEffect>(registry.OnStatusEffectDelete);

            conn.Db.PlayerResource.OnInsert += Delayed<PlayerResource>(registry.OnPlayerResourceInsert);
            conn.Db.PlayerResource.OnUpdate += Delayed<PlayerResource>(registry.OnPlayerResourceUpdate);
            conn.Db.PlayerResource.OnDelete += Delayed<PlayerResource>(registry.OnPlayerResourceDelete);

            conn.Db.DefenseState.OnInsert += Delayed<DefenseState>(registry.OnDefenseStateInsert);
            conn.Db.DefenseState.OnUpdate += Delayed<DefenseState>(registry.OnDefenseStateUpdate);
            conn.Db.DefenseState.OnDelete += Delayed<DefenseState>(registry.OnDefenseStateDelete);

            conn.Db.EquipmentLoadout.OnInsert += Delayed<EquipmentLoadout>(registry.OnEquipmentLoadoutInsert);
            conn.Db.EquipmentLoadout.OnUpdate += Delayed<EquipmentLoadout>(registry.OnEquipmentLoadoutUpdate);
            conn.Db.EquipmentLoadout.OnDelete += Delayed<EquipmentLoadout>(registry.OnEquipmentLoadoutDelete);

            conn.Db.ActiveCombatDiscipline.OnInsert += Delayed<ActiveCombatDiscipline>(registry.OnActiveCombatDisciplineInsert);
            conn.Db.ActiveCombatDiscipline.OnUpdate += Delayed<ActiveCombatDiscipline>(registry.OnActiveCombatDisciplineUpdate);
            conn.Db.ActiveCombatDiscipline.OnDelete += Delayed<ActiveCombatDiscipline>(registry.OnActiveCombatDisciplineDelete);

            conn.Db.ActiveCombatMode.OnInsert += Delayed<ActiveCombatMode>(registry.OnActiveCombatModeInsert);
            conn.Db.ActiveCombatMode.OnUpdate += Delayed<ActiveCombatMode>(registry.OnActiveCombatModeUpdate);
            conn.Db.ActiveCombatMode.OnDelete += Delayed<ActiveCombatMode>(registry.OnActiveCombatModeDelete);

            conn.Db.ItemInstance.OnInsert += Delayed<ItemInstance>(registry.OnItemInstanceInsert);
            conn.Db.ItemInstance.OnUpdate += Delayed<ItemInstance>(registry.OnItemInstanceUpdate);
            conn.Db.ItemInstance.OnDelete += Delayed<ItemInstance>(registry.OnItemInstanceDelete);

            conn.Db.ItemDefinition.OnInsert += Delayed<ItemDefinition>(registry.OnItemDefinitionInsert);
            conn.Db.ItemDefinition.OnUpdate += Delayed<ItemDefinition>(registry.OnItemDefinitionUpdate);

            combat.Bind(localIdentity);
            conn.Db.GlobalCooldown.OnInsert += Delayed<GlobalCooldown>(combat.OnGlobalCooldownInsert);
            conn.Db.GlobalCooldown.OnUpdate += Delayed<GlobalCooldown>(combat.OnGlobalCooldownUpdate);
            conn.Db.GlobalCooldown.OnDelete += Delayed<GlobalCooldown>(combat.OnGlobalCooldownDelete);

            conn.Db.SpellCooldown.OnInsert += Delayed<SpellCooldown>(combat.OnSpellCooldownInsert);
            conn.Db.SpellCooldown.OnUpdate += Delayed<SpellCooldown>(combat.OnSpellCooldownUpdate);
            conn.Db.SpellCooldown.OnDelete += Delayed<SpellCooldown>(combat.OnSpellCooldownDelete);

            conn.Db.FixedActionChargeState.OnInsert += Delayed<FixedActionChargeState>(combat.OnFixedActionChargeStateInsert);
            conn.Db.FixedActionChargeState.OnUpdate += Delayed<FixedActionChargeState>(combat.OnFixedActionChargeStateUpdate);
            conn.Db.FixedActionChargeState.OnDelete += Delayed<FixedActionChargeState>(combat.OnFixedActionChargeStateDelete);

            // Predicted action results reconcile local prediction state and presentation.
            conn.Db.PredictedActionResult.OnInsert += Delayed<PredictedActionResult>(combat.OnPredictedActionResultInsert);
            conn.Db.PredictedActionResult.OnInsert += Delayed<PredictedActionResult>(registry.OnPredictedActionResultInsert);
            conn.Db.PredictedActionResult.OnInsert += Delayed<PredictedActionResult>(MeleeInputHandler.OnPredictedActionResultInsert);
            conn.Db.PredictedActionResult.OnInsert += Delayed<PredictedActionResult>(SpellInputHandler.OnPredictedActionResultInsert);
            conn.Db.PredictedActionResult.OnInsert += Delayed<PredictedActionResult>(FixedActionDispatcher.OnPredictedActionResultInsert);

            conn.Db.ActiveCast.OnInsert += Delayed<ActiveCast>(combat.OnActiveCastInsert);
            conn.Db.ActiveCast.OnUpdate += Delayed<ActiveCast>(combat.OnActiveCastUpdate);
            conn.Db.ActiveCast.OnDelete += Delayed<ActiveCast>(combat.OnActiveCastDelete);

            conn.Db.ActiveCast.OnInsert += Delayed<ActiveCast>(registry.OnActiveCastInsert);
            conn.Db.ActiveCast.OnUpdate += Delayed<ActiveCast>(registry.OnActiveCastUpdate);
            conn.Db.ActiveCast.OnDelete += Delayed<ActiveCast>(registry.OnActiveCastDelete);

            conn.Db.MovementActionState.OnInsert += Delayed<MovementActionState>(combat.OnMovementActionStateInsert);
            conn.Db.MovementActionState.OnUpdate += Delayed<MovementActionState>(combat.OnMovementActionStateUpdate);
            conn.Db.MovementActionState.OnDelete += Delayed<MovementActionState>(combat.OnMovementActionStateDelete);

            conn.Db.MovementActionState.OnInsert += Delayed<MovementActionState>(registry.OnMovementActionStateInsert);
            conn.Db.MovementActionState.OnUpdate += Delayed<MovementActionState>(registry.OnMovementActionStateUpdate);
            conn.Db.MovementActionState.OnDelete += Delayed<MovementActionState>(registry.OnMovementActionStateDelete);

            conn.Db.SpecialMovementRuntime.OnInsert += Delayed<SpecialMovementRuntime>(registry.OnSpecialMovementRuntimeInsert);
            conn.Db.SpecialMovementRuntime.OnUpdate += Delayed<SpecialMovementRuntime>(registry.OnSpecialMovementRuntimeUpdate);
            conn.Db.SpecialMovementRuntime.OnDelete += Delayed<SpecialMovementRuntime>(registry.OnSpecialMovementRuntimeDelete);

            conn.Db.CombatEvent.OnInsert += Delayed<CombatEvent>(registry.OnCombatEventInsert);
            conn.Db.ProjectilePresentationEvent.OnInsert += Delayed<ProjectilePresentationEvent>(registry.OnProjectilePresentationEventInsert);
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

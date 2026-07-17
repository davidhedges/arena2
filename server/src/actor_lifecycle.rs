use spacetimedb::{Identity, ReducerContext, Table};

use crate::arena::{clear_player_world, upsert_player_world};
use crate::combat::clear_player_combat_state;
use crate::inventory::clear_inventory_for_owner;
use crate::movement_actions::{
    clear_fixed_action_charge_states_for_owner, clear_movement_action_for_owner,
    ensure_dodge_charge_state,
};
use crate::player::Player;
use crate::player_input::{clear_pending_player_commands, PlayerInputCursor};
use crate::player_intent::PlayerIntent;
use crate::player_physics::PlayerPhysics;
use crate::player_state::PlayerState;
use crate::resources::clear_player_resources;
use crate::spells::clear_active_cast;
use crate::world_obstacles::clear_world_obstacles_for_owner;

#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::auto_attack::auto_attack_state as _;
#[allow(unused_imports)]
use crate::auto_attack::pending_auto_attack_replacement as _;
#[allow(unused_imports)]
use crate::combat::active_combat_projectile as _;
#[allow(unused_imports)]
use crate::combat::active_combat_projectile_target_state as _;
#[allow(unused_imports)]
use crate::defense::defense_state as _;
#[allow(unused_imports)]
use crate::melee::pending_melee_impact as _;
#[allow(unused_imports)]
use crate::melee::pending_melee_timed_movement as _;
#[allow(unused_imports)]
use crate::melee::pending_projectile_release as _;
#[allow(unused_imports)]
use crate::melee::queued_melee_followup as _;
#[allow(unused_imports)]
use crate::player::player as _;
#[allow(unused_imports)]
use crate::player_input::player_command as _;
#[allow(unused_imports)]
use crate::player_input::player_input_cursor as _;
#[allow(unused_imports)]
use crate::player_intent::player_intent as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::spells::active_bespoke_spell as _;
#[allow(unused_imports)]
use crate::spells::pending_area_impact as _;
#[allow(unused_imports)]
use crate::spells::pending_cast_cancel as _;
#[allow(unused_imports)]
use crate::spells::pending_cast_request as _;
#[allow(unused_imports)]
use crate::spells::special_movement_runtime as _;

pub(crate) enum ActorWorldAssignment {
    Open,
    Instance(u64),
}

pub(crate) struct ActorSpawnSpec {
    pub identity: Identity,
    pub username: String,
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub yaw: f32,
    pub vel_x: f32,
    pub vel_y: f32,
    pub vel_z: f32,
    pub grounded: bool,
    pub last_processed_tick: u32,
    pub state: PlayerState,
    pub world: Option<ActorWorldAssignment>,
}

pub(crate) enum ActorWorldCleanup {
    DeleteOnly,
    RemoveFromCurrentInstance,
}

pub(crate) struct ActorDespawnOptions {
    pub world_cleanup: ActorWorldCleanup,
}

pub(crate) fn spawn_actor_bundle(ctx: &ReducerContext, spec: ActorSpawnSpec) -> Result<(), String> {
    if ctx.db.player().identity().find(spec.identity).is_some() {
        return Err(format!(
            "actor {} already has a player row",
            spec.identity.to_hex()
        ));
    }

    let now = ctx.timestamp;
    ctx.db.player().insert(Player {
        identity: spec.identity,
        username: spec.username,
        connected_at: now,
    });
    ctx.db.player_intent().insert(PlayerIntent {
        identity: spec.identity,
        forward: 0.0,
        strafe: 0.0,
        yaw: spec.yaw,
        jump: false,
        input_tick: spec.last_processed_tick,
        updated_at: now,
    });
    ctx.db.player_input_cursor().insert(PlayerInputCursor {
        identity: spec.identity,
        latest_received_tick: spec.last_processed_tick,
    });
    ctx.db.player_physics().insert(PlayerPhysics {
        identity: spec.identity,
        pos_x: spec.pos_x,
        pos_y: spec.pos_y,
        pos_z: spec.pos_z,
        vel_x: spec.vel_x,
        vel_y: spec.vel_y,
        vel_z: spec.vel_z,
        yaw: spec.yaw,
        grounded: spec.grounded,
        last_processed_tick: spec.last_processed_tick,
        last_tick_consumed_command: true,
        buffered_command_count: 0,
        updated_at: now,
    });
    ctx.db.player_state().insert(spec.state);
    ensure_dodge_charge_state(ctx, spec.identity, now);
    match spec.world {
        Some(ActorWorldAssignment::Open) => {
            upsert_player_world(ctx, spec.identity, "OPEN", None);
        }
        Some(ActorWorldAssignment::Instance(instance_id)) => {
            upsert_player_world(ctx, spec.identity, "INSTANCE", Some(instance_id));
        }
        None => {}
    }

    Ok(())
}

/// Canonical owner of "delete every per-identity transient gameplay row."
///
/// Called from `despawn_actor_bundle` and from the reconnect branch of
/// `client_connected`: SpacetimeDB persists tables across module republish /
/// host restart while connections drop, so rows like a mid-flight cast or a
/// scheduled melee impact would otherwise rehydrate into a fresh session.
///
/// Policy split (netcode audit R1):
/// - Presentation/action state (casts, defense, pending melee, prediction
///   correlation, caster-owned projectiles, auto-attack arming): delete here.
/// - Timestamp-anchored anti-abuse state (`spell_cooldown`, `global_cooldown`):
///   deliberately kept — deleting cooldowns on disconnect would create a
///   relog-to-reset exploit; they expire naturally.
///
/// Every new per-identity transient table must be added here. The
/// `transient_actor_teardown_covers_known_transient_tables` test guards the
/// current list.
pub(crate) fn clear_transient_actor_state(ctx: &ReducerContext, identity: Identity) {
    // Statuses, combat engagement, and stacking-passive runtime rows.
    clear_player_combat_state(ctx, identity);
    // Active cast plus channel runtime, special-movement runtime, and
    // cast-prediction correlation.
    clear_active_cast(ctx, identity);
    // Ordinary cast cleanup preserves externally imposed movement. Actor
    // teardown never does: reconnect/despawn cannot retain any live track.
    ctx.db.special_movement_runtime().owner().delete(identity);
    clear_movement_action_for_owner(ctx, identity);
    clear_pending_player_commands(ctx, identity);
    clear_world_obstacles_for_owner(ctx, identity);

    ctx.db.pending_cast_request().caster().delete(identity);
    let stale_cast_cancels: Vec<String> = ctx
        .db
        .pending_cast_cancel()
        .iter()
        .filter(|row| row.caster == identity)
        .map(|row| row.cancel_key.clone())
        .collect();
    for cancel_key in stale_cast_cancels {
        ctx.db.pending_cast_cancel().cancel_key().delete(cancel_key);
    }
    let stale_area_impacts: Vec<u64> = ctx
        .db
        .pending_area_impact()
        .iter()
        .filter(|row| row.caster == identity)
        .map(|row| row.impact_id)
        .collect();
    for impact_id in stale_area_impacts {
        ctx.db.pending_area_impact().impact_id().delete(impact_id);
    }
    let stale_bespoke_spells: Vec<String> = ctx
        .db
        .active_bespoke_spell()
        .iter()
        .filter(|row| row.caster == identity)
        .map(|row| row.spell_id.clone())
        .collect();
    for spell_id in stale_bespoke_spells {
        ctx.db.active_bespoke_spell().spell_id().delete(spell_id);
    }

    ctx.db.defense_state().owner().delete(identity);

    ctx.db.queued_melee_followup().caster().delete(identity);
    ctx.db
        .pending_melee_timed_movement()
        .owner()
        .delete(identity);
    let stale_melee_impacts: Vec<u64> = ctx
        .db
        .pending_melee_impact()
        .iter()
        .filter(|row| row.source == identity)
        .map(|row| row.impact_id)
        .collect();
    for impact_id in stale_melee_impacts {
        ctx.db.pending_melee_impact().impact_id().delete(impact_id);
    }
    let stale_projectile_releases: Vec<u64> = ctx
        .db
        .pending_projectile_release()
        .iter()
        .filter(|row| row.source == identity)
        .map(|row| row.release_id)
        .collect();
    for release_id in stale_projectile_releases {
        ctx.db
            .pending_projectile_release()
            .release_id()
            .delete(release_id);
    }

    ctx.db.auto_attack_state().owner().delete(identity);
    ctx.db
        .pending_auto_attack_replacement()
        .owner()
        .delete(identity);

    let caster_projectile_ids: Vec<String> = ctx
        .db
        .active_combat_projectile()
        .caster()
        .filter(identity)
        .map(|row| row.projectile_instance_id.clone())
        .collect();
    ctx.db.active_combat_projectile().caster().delete(identity);
    for projectile_instance_id in caster_projectile_ids {
        ctx.db
            .active_combat_projectile_target_state()
            .projectile_instance_id()
            .delete(&projectile_instance_id);
    }
}

pub(crate) fn despawn_actor_bundle(
    ctx: &ReducerContext,
    identity: Identity,
    options: ActorDespawnOptions,
) -> Result<(), String> {
    // World cleanup failure must not abort the row teardown below — bailing
    // early would leave the whole per-identity bundle alive (netcode audit R1).
    let world_cleanup_result = match options.world_cleanup {
        ActorWorldCleanup::DeleteOnly => {
            if ctx.db.player_world().identity().find(identity).is_some() {
                ctx.db.player_world().identity().delete(identity);
            }
            Ok(())
        }
        ActorWorldCleanup::RemoveFromCurrentInstance => clear_player_world(ctx, identity),
    };

    clear_transient_actor_state(ctx, identity);
    clear_inventory_for_owner(ctx, identity);
    clear_fixed_action_charge_states_for_owner(ctx, identity);
    clear_player_resources(ctx, identity);

    if ctx.db.player_physics().identity().find(identity).is_some() {
        ctx.db.player_physics().identity().delete(identity);
    }
    crate::combat::position_history::clear_position_history(ctx, identity);
    crate::defense::clear_defense_telemetry(ctx, identity);
    if ctx
        .db
        .player_input_cursor()
        .identity()
        .find(identity)
        .is_some()
    {
        ctx.db.player_input_cursor().identity().delete(identity);
    }
    if ctx.db.player_intent().identity().find(identity).is_some() {
        ctx.db.player_intent().identity().delete(identity);
    }
    if ctx.db.player_state().player_id().find(identity).is_some() {
        ctx.db.player_state().player_id().delete(identity);
    }
    if ctx.db.player().identity().find(identity).is_some() {
        ctx.db.player().identity().delete(identity);
    }

    world_cleanup_result
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::Path;

    /// Per-identity transient tables (or the helpers that own their deletion)
    /// that the unified teardown must cover. Add every new transient table's
    /// accessor here AND to `clear_transient_actor_state`.
    const TRANSIENT_TEARDOWN_MARKERS: [&str; 18] = [
        "clear_player_combat_state",
        "clear_active_cast",
        "clear_movement_action_for_owner",
        "clear_pending_player_commands",
        "clear_world_obstacles_for_owner",
        "pending_cast_request()",
        "pending_cast_cancel()",
        "pending_area_impact()",
        "active_bespoke_spell()",
        "defense_state()",
        "queued_melee_followup()",
        "pending_melee_timed_movement()",
        "pending_melee_impact()",
        "pending_projectile_release()",
        "auto_attack_state()",
        "pending_auto_attack_replacement()",
        "active_combat_projectile()",
        "active_combat_projectile_target_state()",
    ];

    fn clear_transient_actor_state_body() -> String {
        let source = fs::read_to_string(
            Path::new(env!("CARGO_MANIFEST_DIR")).join("src/actor_lifecycle.rs"),
        )
        .expect("actor_lifecycle.rs should be readable");

        let start = source
            .find("pub(crate) fn clear_transient_actor_state")
            .expect("clear_transient_actor_state should exist");
        let end = source[start..]
            .find("\npub(crate) fn despawn_actor_bundle")
            .map(|offset| start + offset)
            .expect("despawn_actor_bundle should follow clear_transient_actor_state");
        source[start..end].to_string()
    }

    #[test]
    fn transient_actor_teardown_covers_known_transient_tables() {
        let body = clear_transient_actor_state_body();
        for marker in TRANSIENT_TEARDOWN_MARKERS {
            assert!(
                body.contains(marker),
                "clear_transient_actor_state must cover transient surface `{marker}`"
            );
        }
    }

    #[test]
    fn transient_actor_teardown_never_deletes_cooldowns() {
        // Deleting cooldown rows on disconnect would create a relog-to-reset
        // exploit; they must expire naturally.
        let body = clear_transient_actor_state_body();
        assert!(!body.contains("spell_cooldown()"));
        assert!(!body.contains("global_cooldown()"));
    }

    #[test]
    fn transient_actor_teardown_is_called_from_despawn_and_reconnect() {
        let lifecycle_source = fs::read_to_string(
            Path::new(env!("CARGO_MANIFEST_DIR")).join("src/actor_lifecycle.rs"),
        )
        .expect("actor_lifecycle.rs should be readable");
        let despawn_start = lifecycle_source
            .find("pub(crate) fn despawn_actor_bundle")
            .expect("despawn_actor_bundle should exist");
        assert!(
            lifecycle_source[despawn_start..]
                .contains("clear_transient_actor_state(ctx, identity)"),
            "despawn_actor_bundle must call clear_transient_actor_state"
        );

        let player_source =
            fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("src/player.rs"))
                .expect("player.rs should be readable");
        assert!(
            player_source.contains("clear_transient_actor_state(ctx, identity)"),
            "the client_connected reconnect branch must call clear_transient_actor_state"
        );
    }
}

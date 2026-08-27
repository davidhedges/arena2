//! Player Identity and Lifecycle
//!
//! This module handles:
//! - Player identity table (who is connected)
//! - client_connected reducer (spawn player)
//! - client_disconnected reducer (cleanup player)
//!
//! IMPORTANT: Human connect/disconnect reducers now delegate the shared actor row-bundle
//! creation/cleanup to `actor_lifecycle`. Ownership rules still apply:
//! - PlayerIntent is modified only by send_movement_intent
//! - PlayerPhysics is modified only by game_tick

use spacetimedb::{reducer, table, Identity, ReducerContext, Timestamp};

use crate::actor_lifecycle::{
    clear_transient_actor_state, despawn_actor_bundle, spawn_actor_bundle, ActorDespawnOptions,
    ActorSpawnSpec, ActorWorldAssignment, ActorWorldCleanup,
};
// Import table types
use crate::appearance::ensure_default_character_appearance_for_identity;
#[allow(unused_imports)]
use crate::arena::player_world as _;
use crate::arena::{ensure_player_open_world_scene, set_player_open_world};
use crate::combat::new_player_spawn_state;
use crate::inventory::ensure_player_inventory_for_identity;
use crate::npcs::{despawn_all_npcs_for_owner, despawn_dead_npcs_for_owner};
use crate::party::remove_player_from_party_state;
use crate::playground_targets::despawn_all_playground_targets_for_owner;
use crate::progression::ensure_default_progression_for_identity;
use crate::resources::sync_primary_resource_for_player;

pub const DEFAULT_COMBAT_PROFILE: &str = "SWORD_AND_SHIELD";
#[cfg_attr(not(test), allow(dead_code))]
pub const TWO_HANDED_SWORD_COMBAT_PROFILE: &str = "TWO_HANDED_SWORD";
#[allow(unused_imports)]
use crate::player::player as _;

/// Player identity - tracks who is connected.
/// Separate from physics/intent to allow clean lifecycle management.
#[table(accessor = player, public)]
pub struct Player {
    #[primary_key]
    pub identity: Identity,

    /// Display name (auto-generated from identity for now)
    pub username: String,

    /// When the player connected
    pub connected_at: Timestamp,
}

/// Called when a client connects to the SpacetimeDB module.
/// Creates the shared actor row bundle for a connected human player.
#[reducer(client_connected)]
pub fn client_connected(ctx: &ReducerContext) -> Result<(), String> {
    let identity = ctx.sender();
    let now = ctx.timestamp;
    let admission = crate::match_contract::admit_connection(ctx, identity)?;
    if matches!(
        &admission,
        crate::match_contract::ConnectionAdmission::Service
    ) {
        log::info!(
            "[CONNECT] Match owner/service {} connected without spawning a player",
            &identity.to_hex()[..8]
        );
        return Ok(());
    }
    let reservation = match &admission {
        crate::match_contract::ConnectionAdmission::Reserved(reservation) => {
            Some(reservation.clone())
        }
        _ => None,
    };
    let reserved_display_name = reservation
        .as_ref()
        .map(|reserved| reserved.display_name.clone());
    let is_reserved_match_player = reservation.is_some();
    if matches!(
        &admission,
        crate::match_contract::ConnectionAdmission::LocalDirect
    ) {
        crate::game_loop::ensure_game_loop_schedule(ctx);
        crate::game_loop::ensure_game_loop_watchdog_schedule(ctx);
    }

    // Hot module updates do not re-run init. Reconcile the authored catalogs
    // before any player-facing progression/inventory setup reads them so new
    // spells and abilities appear automatically after reconnect.
    crate::contract_version::sync_contract_versions(ctx);
    crate::progression::sync_progression_catalogs(ctx);
    crate::spells::sync_spell_definitions(ctx);
    crate::npcs::sync_npc_catalog(ctx);

    if ctx.db.player().identity().find(identity).is_some() {
        // Stale transient rows (mid-flight casts, scheduled melee impacts,
        // buffs) survive module republish/host restart because SpacetimeDB
        // persists tables while connections drop. Clear them before the
        // session resumes so nothing fires into the fresh session.
        clear_transient_actor_state(ctx, identity);
        despawn_dead_npcs_for_owner(ctx, identity);
        if is_reserved_match_player {
            crate::match_contract::start_reserved_player_match(ctx, identity)?;
        } else if ctx.db.player_world().identity().find(identity).is_none() {
            set_player_open_world(ctx, identity)?;
        } else {
            ensure_player_open_world_scene(ctx, identity);
        }
        ensure_default_progression_for_identity(ctx, identity)?;
        ensure_default_character_appearance_for_identity(ctx, identity)?;
        ensure_player_inventory_for_identity(ctx, identity);
        if let Some(reservation) = reservation.as_ref() {
            crate::match_contract::apply_reserved_player_combat_build(ctx, reservation)?;
        }
        sync_primary_resource_for_player(ctx, identity, now);
        log::info!(
            "[CONNECT] Player {} reconnected with existing actor rows",
            &identity.to_hex()[..8],
        );
        return Ok(());
    }

    // Build combat/lifecycle spawn state once to keep spawn defaults centralized.
    let (spawn_x, spawn_y, spawn_z, player_state) = new_player_spawn_state(identity, now);
    despawn_dead_npcs_for_owner(ctx, identity);
    spawn_actor_bundle(
        ctx,
        ActorSpawnSpec {
            identity,
            username: reserved_display_name
                .unwrap_or_else(|| format!("Player_{}", &identity.to_hex()[..8])),
            pos_x: spawn_x,
            pos_y: spawn_y,
            pos_z: spawn_z,
            yaw: 0.0,
            vel_x: 0.0,
            vel_y: 0.0,
            vel_z: 0.0,
            grounded: true,
            last_processed_tick: 0,
            state: player_state,
            world: (!is_reserved_match_player).then_some(ActorWorldAssignment::Open),
        },
    )?;

    if is_reserved_match_player {
        crate::match_contract::start_reserved_player_match(ctx, identity)?;
    } else {
        set_player_open_world(ctx, identity)?;
    }
    ensure_default_progression_for_identity(ctx, identity)?;
    ensure_default_character_appearance_for_identity(ctx, identity)?;
    ensure_player_inventory_for_identity(ctx, identity);
    if let Some(reservation) = reservation.as_ref() {
        crate::match_contract::apply_reserved_player_combat_build(ctx, reservation)?;
    }
    sync_primary_resource_for_player(ctx, identity, now);

    log::info!(
        "[CONNECT] Player {} spawned at ({:.2}, {:.2}, {:.2})",
        &identity.to_hex()[..8],
        spawn_x,
        spawn_y,
        spawn_z
    );
    Ok(())
}

/// Called when a client disconnects.
/// Removes all player-related rows.
#[reducer(client_disconnected)]
pub fn client_disconnected(ctx: &ReducerContext) -> Result<(), String> {
    let identity = ctx.sender();
    if crate::match_contract::is_provisioned(ctx) {
        if let Err(error) = crate::match_contract::handle_provisioned_disconnect(ctx, identity) {
            log::error!(
                "[DISCONNECT] Provisioned match cleanup for {} failed; continuing: {}",
                &identity.to_hex()[..8],
                error
            );
        }
        log::info!(
            "[DISCONNECT] Provisioned match connection {} removed without entering the legacy world path",
            &identity.to_hex()[..8]
        );
        return Ok(());
    }
    // Never abort the disconnect transaction on ancillary cleanup errors: an
    // early `?` here would roll back the entire teardown and leave every
    // per-identity row alive for the next session (netcode audit R1).
    if let Err(error) = crate::survival::teardown_survival_for_owner(ctx, identity, "disconnect") {
        log::error!(
            "[DISCONNECT] Player {} survival cleanup failed; continuing: {}",
            &identity.to_hex()[..8],
            error
        );
    }
    if let Err(error) = crate::bot_matches::teardown_owned_bot_match(ctx, identity) {
        log::error!(
            "[DISCONNECT] Player {} bot-match cleanup failed; continuing: {}",
            &identity.to_hex()[..8],
            error
        );
    }
    if let Err(error) = despawn_all_playground_targets_for_owner(ctx, identity) {
        log::error!(
            "[DISCONNECT] Player {} playground-target cleanup failed; continuing: {}",
            &identity.to_hex()[..8],
            error
        );
    }
    despawn_all_npcs_for_owner(ctx, identity);
    remove_player_from_party_state(ctx, identity);
    if let Err(error) = despawn_actor_bundle(
        ctx,
        identity,
        ActorDespawnOptions {
            world_cleanup: ActorWorldCleanup::RemoveFromCurrentInstance,
        },
    ) {
        log::error!(
            "[DISCONNECT] Player {} actor despawn failed; continuing: {}",
            &identity.to_hex()[..8],
            error
        );
    }

    log::info!("[DISCONNECT] Player {} removed", &identity.to_hex()[..8]);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{DEFAULT_COMBAT_PROFILE, TWO_HANDED_SWORD_COMBAT_PROFILE};
    use std::{fs, path::Path};

    #[test]
    fn combat_profile_constants_remain_stable() {
        assert_eq!(DEFAULT_COMBAT_PROFILE, "SWORD_AND_SHIELD");
        assert_eq!(TWO_HANDED_SWORD_COMBAT_PROFILE, "TWO_HANDED_SWORD");
    }

    #[test]
    fn hot_module_reconnect_refreshes_authored_spell_catalogs() {
        let source =
            fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("src/player.rs"))
                .expect("player.rs should be readable");
        let connect_start = source
            .find("pub fn client_connected")
            .expect("client_connected should exist");
        let disconnect_start = source[connect_start..]
            .find("pub fn client_disconnected")
            .map(|offset| connect_start + offset)
            .expect("client_disconnected should exist");
        let connect_body = &source[connect_start..disconnect_start];

        assert!(
            connect_body.contains("crate::progression::sync_progression_catalogs(ctx)"),
            "hot module reconnects must refresh AbilityCatalog so newly authored spells appear in the J panel"
        );
        assert!(
            connect_body.contains("crate::spells::sync_spell_definitions(ctx)"),
            "hot module reconnects must refresh SpellDefinition alongside AbilityCatalog"
        );
    }
}

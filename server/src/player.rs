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
    despawn_actor_bundle, spawn_actor_bundle, ActorDespawnOptions, ActorSpawnSpec,
    ActorWorldAssignment, ActorWorldCleanup,
};
// Import table types
use crate::appearance::ensure_default_character_appearance_for_identity;
#[allow(unused_imports)]
use crate::arena::player_world as _;
use crate::arena::{ensure_player_open_world_scene, set_player_open_world};
use crate::combat::new_player_spawn_state;
use crate::inventory::ensure_player_inventory_for_identity;
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

    if ctx.db.player().identity().find(identity).is_some() {
        if ctx.db.player_world().identity().find(identity).is_none() {
            set_player_open_world(ctx, identity)?;
        } else {
            ensure_player_open_world_scene(ctx, identity);
        }
        ensure_default_progression_for_identity(ctx, identity)?;
        ensure_default_character_appearance_for_identity(ctx, identity)?;
        ensure_player_inventory_for_identity(ctx, identity);
        sync_primary_resource_for_player(ctx, identity, now);
        log::info!(
            "[CONNECT] Player {} reconnected with existing actor rows",
            &identity.to_hex()[..8],
        );
        return Ok(());
    }

    // Build combat/lifecycle spawn state once to keep spawn defaults centralized.
    let (spawn_x, spawn_y, spawn_z, player_state) = new_player_spawn_state(identity, now);
    spawn_actor_bundle(
        ctx,
        ActorSpawnSpec {
            identity,
            username: format!("Player_{}", &identity.to_hex()[..8]),
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
            world: Some(ActorWorldAssignment::Open),
        },
    )?;

    set_player_open_world(ctx, identity)?;
    ensure_default_progression_for_identity(ctx, identity)?;
    ensure_default_character_appearance_for_identity(ctx, identity)?;
    ensure_player_inventory_for_identity(ctx, identity);
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
    despawn_all_playground_targets_for_owner(ctx, identity)?;
    remove_player_from_party_state(ctx, identity);
    despawn_actor_bundle(
        ctx,
        identity,
        ActorDespawnOptions {
            world_cleanup: ActorWorldCleanup::RemoveFromCurrentInstance,
        },
    )?;

    log::info!("[DISCONNECT] Player {} removed", &identity.to_hex()[..8]);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{DEFAULT_COMBAT_PROFILE, TWO_HANDED_SWORD_COMBAT_PROFILE};

    #[test]
    fn combat_profile_constants_remain_stable() {
        assert_eq!(DEFAULT_COMBAT_PROFILE, "SWORD_AND_SHIELD");
        assert_eq!(TWO_HANDED_SWORD_COMBAT_PROFILE, "TWO_HANDED_SWORD");
    }
}

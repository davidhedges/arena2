//! Player State - Minimal combat-facing state
//!
//! Ownership contract:
//! - PlayerPhysics is the movement source of truth.
//! - PlayerState is combat/lifecycle state only (HP, alive, hit capsule, respawn timing).
//! - Combat systems may read PlayerState, but never mutate physics.
//!
//! This table is the smallest shared state needed for combat + lifecycle/UI.
//! It intentionally omits stats, inventory, buffs, or any derived systems.

use spacetimedb::{table, Identity, Timestamp};

#[table(accessor = player_state, public)]
pub struct PlayerState {
    #[primary_key]
    pub player_id: Identity,

    // Development/testing marker (dummy target).
    pub is_dummy: bool,

    // === Combat ===
    #[index(btree)]
    pub alive: bool,
    pub eliminated: bool,
    pub hp: i32,
    pub max_hp: i32,

    // === Hit Capsule (radius + height) ===
    pub hit_radius: f32,
    pub hit_height: f32,

    // === Derived Movement Context ===
    // Replicated to clients so local prediction can use the same resolved
    // movement restrictions/modifiers the server applied for the latest
    // authoritative movement tick.
    pub movement_blocked: bool,
    pub move_speed_multiplier: f32,
    pub movement_context_tick: u32,
    pub voluntary_move_epoch: u64,
    pub last_voluntary_move_input_tick: u32,

    // === Respawn ===
    pub respawn_at: Timestamp,

    // === Melee combo tracking ===
    // Server-authoritative record of the last melee strike this player landed,
    // used to gate combo-followup strikes. `last_strike_id` is empty until the
    // player casts their first strike.
    pub last_strike_id: String,
    pub last_strike_at: Timestamp,
}

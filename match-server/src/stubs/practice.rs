//! Practice/training instances are excluded from disposable PvP databases.

use spacetimedb::{Identity, ReducerContext, Timestamp};

pub(crate) fn resolve_instance_spawn_override(
    _ctx: &ReducerContext,
    _arena_id: u64,
    _identity: Identity,
) -> Option<(f32, f32, f32)> {
    None
}

pub(crate) fn resolve_respawn_pose(
    _ctx: &ReducerContext,
    _identity: Identity,
    _hit_radius: f32,
    _hit_height: f32,
) -> Option<(f32, f32, f32, f32)> {
    None
}

pub(crate) fn despawn_training_instance(_ctx: &ReducerContext, _arena_id: u64) {}

pub(crate) fn tick_practice(_ctx: &ReducerContext, _now: Timestamp) -> Result<(), String> {
    Ok(())
}

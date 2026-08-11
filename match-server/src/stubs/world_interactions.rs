//! Doors and authored world interactions belong to exploration/dungeon scenes.

use spacetimedb::{Identity, ReducerContext, Timestamp};

use crate::arena::WorldRayHit;

pub(crate) fn cancel_active_world_interaction_for_actor(
    _ctx: &ReducerContext,
    _actor: Identity,
) -> bool {
    false
}

pub(crate) fn cancel_active_world_interaction_for_damage(
    _ctx: &ReducerContext,
    _actor: Identity,
    _lethal: bool,
) -> bool {
    false
}

pub(crate) fn tick_world_interactions(_ctx: &ReducerContext, _now: Timestamp) {}

#[allow(clippy::too_many_arguments)]
pub(crate) fn resolve_closed_door_movement(
    _ctx: &ReducerContext,
    _actor: Identity,
    _start_x: f32,
    _start_z: f32,
    target_x: f32,
    target_z: f32,
    _radius: f32,
    _foot_y: f32,
    _height: f32,
) -> (f32, f32) {
    (target_x, target_z)
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn first_closed_door_hit(
    _ctx: &ReducerContext,
    _actor: Identity,
    _start_x: f32,
    _start_y: f32,
    _start_z: f32,
    _end_x: f32,
    _end_y: f32,
    _end_z: f32,
    _radius: f32,
) -> Option<WorldRayHit> {
    None
}

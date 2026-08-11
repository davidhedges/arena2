//! Survival is deliberately absent from the disposable PvP schema. These
//! internal no-op hooks let shared combat code keep one implementation.

use spacetimedb::{Identity, ReducerContext, Timestamp};

pub(crate) enum SurvivalNpcPerceptionState {
    Active,
    Paused {
        last_known_x: f32,
        last_known_y: f32,
        last_known_z: f32,
        newly_paused: bool,
    },
}

pub(crate) fn tick_survival(_ctx: &ReducerContext, _now: Timestamp) {}

pub(crate) fn is_survival_npc(_ctx: &ReducerContext, _identity: Identity) -> bool {
    false
}

pub(crate) fn on_survival_combat_mode_changed(_ctx: &ReducerContext, _owner: Identity) {}

pub(crate) fn update_survival_npc_perception(
    _ctx: &ReducerContext,
    _identity: Identity,
    _fallback_position: (f32, f32, f32),
) -> SurvivalNpcPerceptionState {
    SurvivalNpcPerceptionState::Active
}

pub(crate) fn clear_survival_perception_pause(_ctx: &ReducerContext, _identity: Identity) {}

pub(crate) fn survival_player_is_invulnerable(
    _ctx: &ReducerContext,
    _identity: Identity,
) -> bool {
    false
}

pub(crate) fn resolve_survival_spawn_override(
    _ctx: &ReducerContext,
    _instance_id: u64,
) -> Option<(f32, f32, f32)> {
    None
}

pub(crate) fn on_survival_npc_defeated(
    _ctx: &ReducerContext,
    _identity: Identity,
    _killer: Identity,
) -> bool {
    false
}

pub(crate) fn end_survival_run_for_player_death(_ctx: &ReducerContext, _owner: Identity) {}

pub(crate) fn teardown_survival_for_owner(
    _ctx: &ReducerContext,
    _owner: Identity,
    _reason: &str,
) -> Result<bool, String> {
    Ok(false)
}

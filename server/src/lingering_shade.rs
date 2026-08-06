use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::action_prediction::{
    has_predicted_action_result, optional_action_prediction_token, record_predicted_action_result,
    ActionPredictionToken, ActionRejectReason, ActionResultKind, OptionalActionPredictionToken,
    PredictedActionFamily,
};
use crate::arena::{resolve_player_world_context, ResolvedWorldContext};
use crate::combat::{has_active_disabling_status, timestamp_to_micros};
use crate::defense::clear_interruptible_defense_for_owner;
use crate::progression::{
    character_has_selected_discipline, subtlety_movement_return_window, DISCIPLINE_SUBTLETY,
};
use crate::spells::{
    bake_linear_special_movement, begin_instant_special_movement, SpellVec3,
    SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK, SPECIAL_MOVEMENT_FACING_FACE_START,
};
use crate::world_obstacles::resolve_active_world_obstacle_movement;

#[allow(unused_imports)]
use crate::lingering_shade::lingering_shade_state as _;
#[allow(unused_imports)]
use crate::melee::active_melee_channel as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::spells::active_cast as _;
#[allow(unused_imports)]
use crate::spells::special_movement_runtime as _;

pub(crate) const LINGERING_SHADE_RETURN_MOVEMENT_KIND: &str = "LINGERING_SHADE_RETURN";
const WORLD_KIND_OPEN: &str = "OPEN";
const WORLD_KIND_INSTANCE: &str = "INSTANCE";
const RETURN_ENDPOINT_EPSILON_SQ: f32 = 0.0025;
const MOVEMENT_EPSILON_SQ: f32 = 0.0001;

#[table(accessor = lingering_shade_state, public)]
#[derive(Clone)]
pub struct LingeringShadeState {
    #[primary_key]
    pub owner: Identity,
    pub anchor_id: String,
    pub source_action_id: String,
    pub source_ability_id: String,
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub facing_yaw: f32,
    pub world_kind: String,
    pub instance_id: Option<u64>,
    #[index(btree)]
    pub instance_scope_id: u64,
    #[index(btree)]
    pub open_world_scene_name: String,
    pub created_at: Timestamp,
    pub expires_at: Timestamp,
    #[index(btree)]
    pub expires_at_micros: i64,
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn arm_lingering_shade_for_voluntary_movement(
    ctx: &ReducerContext,
    owner: Identity,
    source_action_id: &str,
    source_ability_id: &str,
    start: SpellVec3,
    end: SpellVec3,
    facing_yaw: f32,
    now: Timestamp,
) -> bool {
    let dx = end.x - start.x;
    let dz = end.z - start.z;
    if dx * dx + dz * dz <= MOVEMENT_EPSILON_SQ
        || !start.x.is_finite()
        || !start.y.is_finite()
        || !start.z.is_finite()
        || !facing_yaw.is_finite()
        || !character_has_selected_discipline(ctx, owner, DISCIPLINE_SUBTLETY)
    {
        return false;
    }

    let return_window = subtlety_movement_return_window();
    if return_window.is_zero() {
        return false;
    }

    let Some(world_context) = resolve_player_world_context(ctx, owner) else {
        return false;
    };
    let (world_kind, instance_id, open_world_scene_name) = match world_context {
        ResolvedWorldContext::Open(scene_name) => (WORLD_KIND_OPEN.to_string(), None, scene_name),
        ResolvedWorldContext::Instance(instance_id) => (
            WORLD_KIND_INSTANCE.to_string(),
            Some(instance_id),
            String::new(),
        ),
    };
    let expires_at = now + return_window;
    let row = LingeringShadeState {
        owner,
        anchor_id: format!(
            "{}:{}:{}",
            owner.to_hex(),
            normalize_wire_id(source_action_id),
            now.to_micros_since_unix_epoch()
        ),
        source_action_id: normalize_wire_id(source_action_id),
        source_ability_id: normalize_wire_id(source_ability_id),
        pos_x: start.x,
        pos_y: start.y,
        pos_z: start.z,
        facing_yaw,
        world_kind,
        instance_id,
        instance_scope_id: instance_id.unwrap_or_default(),
        open_world_scene_name,
        created_at: now,
        expires_at,
        expires_at_micros: timestamp_to_micros(expires_at),
    };

    if ctx.db.lingering_shade_state().owner().find(owner).is_some() {
        ctx.db.lingering_shade_state().owner().update(row);
    } else {
        ctx.db.lingering_shade_state().insert(row);
    }
    true
}

#[reducer]
pub fn return_to_lingering_shade(
    ctx: &ReducerContext,
    anchor_id: String,
    predicted_action_id: String,
    client_action_seq: u64,
) -> Result<(), String> {
    let owner = ctx.sender();
    let now = ctx.timestamp;
    let prediction = optional_action_prediction_token(predicted_action_id, client_action_seq);
    let prediction_token = match prediction {
        OptionalActionPredictionToken::Legacy => None,
        OptionalActionPredictionToken::Invalid => return Ok(()),
        OptionalActionPredictionToken::Predicted(token) => {
            if has_predicted_action_result(ctx, owner, PredictedActionFamily::Movement, &token) {
                return Ok(());
            }
            Some(token)
        }
    };

    let Some(anchor) = ctx.db.lingering_shade_state().owner().find(owner) else {
        record_return_result(
            ctx,
            owner,
            prediction_token.as_ref(),
            anchor_id.as_str(),
            ActionResultKind::Rejected,
            ActionRejectReason::InvalidInput,
            now,
        );
        return Ok(());
    };
    if anchor.anchor_id != anchor_id || now >= anchor.expires_at {
        if now >= anchor.expires_at {
            clear_lingering_shade_for_owner(ctx, owner);
        }
        record_return_result(
            ctx,
            owner,
            prediction_token.as_ref(),
            anchor_id.as_str(),
            ActionResultKind::Rejected,
            ActionRejectReason::InvalidInput,
            now,
        );
        return Ok(());
    }

    let Some(state) = ctx.db.player_state().player_id().find(owner) else {
        return reject_return(
            ctx,
            owner,
            prediction_token.as_ref(),
            anchor_id.as_str(),
            ActionRejectReason::InvalidInput,
            now,
        );
    };
    if !state.alive {
        clear_lingering_shade_for_owner(ctx, owner);
        return reject_return(
            ctx,
            owner,
            prediction_token.as_ref(),
            anchor_id.as_str(),
            ActionRejectReason::Dead,
            now,
        );
    }
    if has_active_disabling_status(ctx, owner, now) {
        return reject_return(
            ctx,
            owner,
            prediction_token.as_ref(),
            anchor_id.as_str(),
            ActionRejectReason::Disabled,
            now,
        );
    }
    if state.movement_blocked {
        return reject_return(
            ctx,
            owner,
            prediction_token.as_ref(),
            anchor_id.as_str(),
            ActionRejectReason::MovementRestricted,
            now,
        );
    }
    if !character_has_selected_discipline(ctx, owner, DISCIPLINE_SUBTLETY)
        || !anchor_matches_current_world(ctx, owner, &anchor)
    {
        clear_lingering_shade_for_owner(ctx, owner);
        return reject_return(
            ctx,
            owner,
            prediction_token.as_ref(),
            anchor_id.as_str(),
            ActionRejectReason::InvalidInput,
            now,
        );
    }
    if ctx
        .db
        .special_movement_runtime()
        .owner()
        .find(owner)
        .is_some()
        || ctx.db.active_cast().caster().find(owner).is_some()
        || ctx.db.active_melee_channel().owner().find(owner).is_some()
    {
        return reject_return(
            ctx,
            owner,
            prediction_token.as_ref(),
            anchor_id.as_str(),
            ActionRejectReason::Busy,
            now,
        );
    }

    let Some(physics) = ctx.db.player_physics().identity().find(owner) else {
        return reject_return(
            ctx,
            owner,
            prediction_token.as_ref(),
            anchor_id.as_str(),
            ActionRejectReason::InvalidInput,
            now,
        );
    };
    let start = SpellVec3::new(physics.pos_x, physics.pos_y, physics.pos_z);
    let intended_end = SpellVec3::new(anchor.pos_x, anchor.pos_y, anchor.pos_z);
    let baked = bake_linear_special_movement(
        ctx,
        owner,
        start,
        intended_end,
        state.hit_radius,
        state.hit_height,
        SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK,
    );
    let (resolved_x, resolved_z) = resolve_active_world_obstacle_movement(
        ctx,
        owner,
        start.x,
        start.z,
        baked.end.x,
        baked.end.z,
        state.hit_radius,
        start.y,
        state.hit_height,
    );
    let endpoint_dx = resolved_x - intended_end.x;
    let endpoint_dz = resolved_z - intended_end.z;
    if endpoint_dx * endpoint_dx + endpoint_dz * endpoint_dz > RETURN_ENDPOINT_EPSILON_SQ {
        return reject_return(
            ctx,
            owner,
            prediction_token.as_ref(),
            anchor_id.as_str(),
            ActionRejectReason::GapCloseBlocked,
            now,
        );
    }

    clear_interruptible_defense_for_owner(ctx, owner);
    crate::world_interactions::cancel_active_world_interaction_for_actor(ctx, owner);
    begin_instant_special_movement(
        ctx,
        owner,
        LINGERING_SHADE_RETURN_MOVEMENT_KIND,
        now,
        start,
        intended_end,
        anchor.facing_yaw,
        SPECIAL_MOVEMENT_FACING_FACE_START,
        SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK,
    );
    clear_lingering_shade_for_owner(ctx, owner);
    record_return_result(
        ctx,
        owner,
        prediction_token.as_ref(),
        anchor_id.as_str(),
        ActionResultKind::Accepted,
        ActionRejectReason::None,
        now,
    );
    Ok(())
}

pub(crate) fn expire_lingering_shades(ctx: &ReducerContext, now: Timestamp) {
    let due: Vec<Identity> = ctx
        .db
        .lingering_shade_state()
        .expires_at_micros()
        .filter(..=timestamp_to_micros(now))
        .map(|row| row.owner)
        .collect();
    for owner in due {
        clear_lingering_shade_for_owner(ctx, owner);
    }
}

pub(crate) fn clear_lingering_shade_for_owner(ctx: &ReducerContext, owner: Identity) {
    ctx.db.lingering_shade_state().owner().delete(owner);
}

fn anchor_matches_current_world(
    ctx: &ReducerContext,
    owner: Identity,
    anchor: &LingeringShadeState,
) -> bool {
    match resolve_player_world_context(ctx, owner) {
        Some(ResolvedWorldContext::Open(scene_name)) => {
            anchor.world_kind == WORLD_KIND_OPEN
                && anchor.instance_id.is_none()
                && anchor.open_world_scene_name == scene_name
        }
        Some(ResolvedWorldContext::Instance(instance_id)) => {
            anchor.world_kind == WORLD_KIND_INSTANCE && anchor.instance_id == Some(instance_id)
        }
        None => false,
    }
}

fn normalize_wire_id(value: &str) -> String {
    value.trim().to_ascii_uppercase()
}

fn reject_return(
    ctx: &ReducerContext,
    owner: Identity,
    prediction_token: Option<&ActionPredictionToken>,
    anchor_id: &str,
    reason: ActionRejectReason,
    now: Timestamp,
) -> Result<(), String> {
    record_return_result(
        ctx,
        owner,
        prediction_token,
        anchor_id,
        ActionResultKind::Rejected,
        reason,
        now,
    );
    Ok(())
}

fn record_return_result(
    ctx: &ReducerContext,
    owner: Identity,
    prediction_token: Option<&ActionPredictionToken>,
    anchor_id: &str,
    result: ActionResultKind,
    reason: ActionRejectReason,
    now: Timestamp,
) {
    let Some(prediction_token) = prediction_token else {
        return;
    };
    record_predicted_action_result(
        ctx,
        owner,
        PredictedActionFamily::Movement,
        prediction_token,
        anchor_id,
        result,
        reason,
        now,
    );
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn movement_threshold_requires_actual_displacement() {
        let start = SpellVec3::new(1.0, 2.0, 3.0);
        let same = SpellVec3::new(1.0, 2.0, 3.0);
        let moved = SpellVec3::new(1.02, 2.0, 3.0);
        let same_dx = same.x - start.x;
        let same_dz = same.z - start.z;
        let moved_dx = moved.x - start.x;
        let moved_dz = moved.z - start.z;

        assert!(same_dx * same_dx + same_dz * same_dz <= MOVEMENT_EPSILON_SQ);
        assert!(moved_dx * moved_dx + moved_dz * moved_dz > MOVEMENT_EPSILON_SQ);
    }

    #[test]
    fn wire_ids_are_normalized_for_client_matching() {
        assert_eq!(normalize_wire_id(" dagger_pursue "), "DAGGER_PURSUE");
        assert_eq!(normalize_wire_id("DODGE"), "DODGE");
    }
}

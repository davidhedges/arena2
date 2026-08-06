use std::time::Duration;

use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::action_prediction::{
    has_predicted_action_result, optional_action_prediction_token, record_predicted_action_result,
    ActionPredictionToken, ActionRejectReason, ActionResultKind, OptionalActionPredictionToken,
    PredictedActionFamily,
};
use crate::action_snapshot::{
    validate_authoritative_action_snapshot, ActionSnapshotRequest, MAX_ACTION_INPUT_TICK_DRIFT,
};
use crate::auto_attack::arm_auto_attack_if_unarmed_with_cadence;
use crate::combat::{has_active_disabling_status, mark_harmful_combat_action};
use crate::defense::clear_interruptible_defense_for_owner;
use crate::lingering_shade::arm_lingering_shade_for_voluntary_movement;
use crate::movement::FIXED_TICK_MILLIS;
use crate::progression::{
    character_has_selected_discipline, primary_resource_gain_on_action_accept,
    subtlety_dodge_recharge_time_reduction, AbilityCatalog, MovementDeliveryRuntime,
    DISCIPLINE_SUBTLETY,
};
use crate::resources::{
    can_pay_action_resource_cost, grant_primary_resource_amount, pay_action_resource_cost,
    resolve_ability_action_resource_cost_amount,
};
use crate::spells::{
    is_on_global_cooldown, is_on_named_cooldown, stamp_global_cooldown_for_duration, SpellId,
    SpellVec3,
};

#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::movement_actions::fixed_action_charge_recovery as _;
#[allow(unused_imports)]
use crate::movement_actions::fixed_action_charge_state as _;
#[allow(unused_imports)]
use crate::movement_actions::movement_action_state as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::spells::active_cast as _;
#[allow(unused_imports)]
use crate::spells::special_movement_runtime as _;

const ACTION_KIND_DODGE: &str = "DODGE";
const ACTION_KIND_DASH_TO_TARGET: &str = "DASH_TO_TARGET";
const DODGE_MAX_CHARGES: u32 = 10;
const DODGE_RECHARGE_MS: u64 = 10_000;
const DODGE_DISTANCE_METERS: f32 = 8.0;
const DODGE_SPEED_METERS_PER_SECOND: f32 = 24.0;
const DODGE_RECOVERY_MS: u64 = 220;

#[table(accessor = movement_action_state, public)]
#[derive(Clone)]
pub struct MovementActionState {
    #[primary_key]
    pub owner: Identity,
    pub action_id: String,
    pub kind: String,
    pub ability_id: String,
    pub resolved_action_id: String,
    pub started_at: Timestamp,
    pub effective_from_input_tick: u32,
    pub active_until_input_tick: u32,
    pub recovery_until_input_tick: u32,
    pub active_until: Timestamp,
    pub recovery_until: Timestamp,
    pub dir_x: f32,
    pub dir_z: f32,
    pub facing_yaw_start: f32,
}

#[table(accessor = fixed_action_charge_state, public)]
#[derive(Clone, PartialEq)]
pub struct FixedActionChargeState {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    pub action_id: String,
    pub current_charges: u32,
    pub max_charges: u32,
    pub recharge_duration_ms: u64,
    pub is_recharging: bool,
    pub recharge_started_at: Timestamp,
    pub next_charge_ready_at: Timestamp,
}

#[table(accessor = fixed_action_charge_recovery)]
#[derive(Clone)]
pub struct FixedActionChargeRecovery {
    #[primary_key]
    #[auto_inc]
    pub recovery_id: u64,
    #[index(btree)]
    pub owner: Identity,
    pub action_id: String,
    pub recharge_started_at: Timestamp,
    pub ready_at: Timestamp,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum MovementActionKind {
    Dodge,
    DashToTarget,
}

impl MovementActionKind {
    pub(crate) fn from_wire(value: &str) -> Option<Self> {
        match value {
            ACTION_KIND_DODGE => Some(Self::Dodge),
            ACTION_KIND_DASH_TO_TARGET => Some(Self::DashToTarget),
            _ => None,
        }
    }
}

struct OptionalPredictionToken {
    token: Option<ActionPredictionToken>,
    input_was_invalid: bool,
    duplicate_result_exists: bool,
}

fn movement_prediction_token(
    ctx: &ReducerContext,
    owner: Identity,
    family: PredictedActionFamily,
    predicted_action_id: String,
    client_action_seq: u64,
) -> OptionalPredictionToken {
    let token = match optional_action_prediction_token(predicted_action_id, client_action_seq) {
        OptionalActionPredictionToken::Legacy => {
            return OptionalPredictionToken {
                token: None,
                input_was_invalid: false,
                duplicate_result_exists: false,
            };
        }
        OptionalActionPredictionToken::Invalid => {
            return OptionalPredictionToken {
                token: None,
                input_was_invalid: true,
                duplicate_result_exists: false,
            };
        }
        OptionalActionPredictionToken::Predicted(token) => token,
    };

    let duplicate_result_exists = has_predicted_action_result(ctx, owner, family, &token);
    OptionalPredictionToken {
        token: Some(token),
        input_was_invalid: false,
        duplicate_result_exists,
    }
}

fn record_optional_movement_prediction_result(
    ctx: &ReducerContext,
    owner: Identity,
    token: Option<&ActionPredictionToken>,
    action_instance_id: &str,
    result: ActionResultKind,
    reject_reason: ActionRejectReason,
    now: Timestamp,
) {
    let Some(token) = token else {
        return;
    };

    record_predicted_action_result(
        ctx,
        owner,
        PredictedActionFamily::Movement,
        token,
        action_instance_id,
        result,
        reject_reason,
        now,
    );
}

#[reducer]
pub fn start_dodge(
    ctx: &ReducerContext,
    effective_input_tick: u32,
    input_tick: u32,
    pos_x: f32,
    pos_y: f32,
    pos_z: f32,
    yaw: f32,
    move_forward: f32,
    move_strafe: f32,
    predicted_action_id: String,
    client_action_seq: u64,
) -> Result<(), String> {
    let owner = ctx.sender();
    let now = ctx.timestamp;
    let prediction_token = movement_prediction_token(
        ctx,
        owner,
        PredictedActionFamily::Movement,
        predicted_action_id,
        client_action_seq,
    );
    if prediction_token.input_was_invalid {
        return Ok(());
    }
    if prediction_token.duplicate_result_exists {
        return Ok(());
    }

    let Some(snapshot) = validate_authoritative_action_snapshot(
        ctx,
        owner,
        ActionSnapshotRequest {
            input_tick,
            pos_x,
            pos_y,
            pos_z,
            yaw,
        },
    ) else {
        record_optional_movement_prediction_result(
            ctx,
            owner,
            prediction_token.token.as_ref(),
            "",
            ActionResultKind::Rejected,
            ActionRejectReason::StaleSnapshot,
            now,
        );
        return Ok(());
    };

    if has_active_disabling_status(ctx, owner, now) {
        record_optional_movement_prediction_result(
            ctx,
            owner,
            prediction_token.token.as_ref(),
            "",
            ActionResultKind::Rejected,
            ActionRejectReason::Disabled,
            now,
        );
        return Ok(());
    }

    if reject_or_clear_stale_action(ctx, owner, now) {
        record_optional_movement_prediction_result(
            ctx,
            owner,
            prediction_token.token.as_ref(),
            "",
            ActionResultKind::Rejected,
            ActionRejectReason::Busy,
            now,
        );
        return Ok(());
    }

    let Some(authoritative) = crate::combat::actor_snapshot::actor_snapshot_for(ctx, owner) else {
        record_optional_movement_prediction_result(
            ctx,
            owner,
            prediction_token.token.as_ref(),
            "",
            ActionResultKind::Rejected,
            ActionRejectReason::InvalidInput,
            now,
        );
        return Ok(());
    };
    let Some(physics) = ctx.db.player_physics().identity().find(owner) else {
        record_optional_movement_prediction_result(
            ctx,
            owner,
            prediction_token.token.as_ref(),
            "",
            ActionResultKind::Rejected,
            ActionRejectReason::InvalidInput,
            now,
        );
        return Ok(());
    };
    let effective_input_tick =
        sanitize_effective_input_tick(physics.last_processed_tick, effective_input_tick);
    let dodge_state = crate::combat::actor_snapshot::CombatActorSnapshot {
        pos_x: snapshot.pos_x,
        pos_y: snapshot.pos_y,
        pos_z: snapshot.pos_z,
        facing_yaw: snapshot.facing_yaw,
        last_processed_tick: snapshot.last_processed_tick,
        ..authoritative
    };
    let use_air_path = dodge_uses_air_path(ctx, owner, &authoritative, &dodge_state);
    let (dir_x, dir_z) = resolve_dodge_direction(snapshot.facing_yaw, move_forward, move_strafe);
    let start_x = snapshot.pos_x;
    let start_y = snapshot.pos_y;
    let start_z = snapshot.pos_z;
    let intended_end_x = start_x + dir_x * DODGE_DISTANCE_METERS;
    let intended_end_z = start_z + dir_z * DODGE_DISTANCE_METERS;
    let collision_policy = if use_air_path {
        crate::spells::SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_FIXED_Y
    } else {
        crate::spells::SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK
    };
    let baked = crate::spells::bake_linear_special_movement(
        ctx,
        owner,
        SpellVec3::new(start_x, start_y, start_z),
        SpellVec3::new(intended_end_x, start_y, intended_end_z),
        authoritative.hit_radius,
        authoritative.hit_height,
        collision_policy,
    );
    let duration_ms = movement_duration_ms(
        start_x,
        start_z,
        baked.end.x,
        baked.end.z,
        DODGE_SPEED_METERS_PER_SECOND,
    );
    let active_ticks = duration_ticks(duration_ms);
    let recovery_ticks = duration_ticks(DODGE_RECOVERY_MS);
    let active_until = now + Duration::from_millis(duration_ms);
    let recovery_until = active_until + Duration::from_millis(DODGE_RECOVERY_MS);

    if !consume_fixed_action_charge(ctx, owner, ACTION_KIND_DODGE, now) {
        record_optional_movement_prediction_result(
            ctx,
            owner,
            prediction_token.token.as_ref(),
            "",
            ActionResultKind::Rejected,
            ActionRejectReason::NoCharges,
            now,
        );
        return Ok(());
    }

    clear_interruptible_defense_for_owner(ctx, owner);

    arm_lingering_shade_for_voluntary_movement(
        ctx,
        owner,
        ACTION_KIND_DODGE,
        "",
        SpellVec3::new(start_x, start_y, start_z),
        baked.end,
        snapshot.facing_yaw,
        now,
    );

    crate::spells::begin_special_movement_with_facing_policy(
        ctx,
        owner,
        ACTION_KIND_DODGE,
        now,
        duration_ms,
        crate::spells::SpellVec3::new(start_x, start_y, start_z),
        baked.end,
        snapshot.facing_yaw,
        crate::spells::SPECIAL_MOVEMENT_FACING_FACE_START,
        collision_policy,
    );

    let action_id = build_action_id(owner, ACTION_KIND_DODGE, now);
    upsert_movement_action(
        ctx,
        MovementActionState {
            owner,
            action_id: action_id.clone(),
            kind: ACTION_KIND_DODGE.to_string(),
            ability_id: String::new(),
            resolved_action_id: ACTION_KIND_DODGE.to_string(),
            started_at: now,
            effective_from_input_tick: effective_input_tick,
            active_until_input_tick: effective_input_tick.saturating_add(active_ticks),
            recovery_until_input_tick: effective_input_tick
                .saturating_add(active_ticks)
                .saturating_add(recovery_ticks),
            active_until,
            recovery_until,
            dir_x,
            dir_z,
            facing_yaw_start: snapshot.facing_yaw,
        },
    );
    record_optional_movement_prediction_result(
        ctx,
        owner,
        prediction_token.token.as_ref(),
        action_id.as_str(),
        ActionResultKind::Accepted,
        ActionRejectReason::None,
        now,
    );

    Ok(())
}

pub(crate) fn start_movement_delivery_request(
    ctx: &ReducerContext,
    ability: &AbilityCatalog,
    delivery: &MovementDeliveryRuntime,
    target_id: String,
    cast_input_tick: u32,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
) -> Result<(), String> {
    let owner = ctx.sender();
    let now = ctx.timestamp;
    let Ok(action_id) = SpellId::new(ability.action_id.as_str()) else {
        log::info!(
            "[MOVEMENT_DELIVERY] caster={} rejected reason=invalid_action_id entry=start_request action={}",
            &owner.to_hex()[..8],
            ability.action_id
        );
        return Ok(());
    };

    let Some(snapshot) = validate_authoritative_action_snapshot(
        ctx,
        owner,
        ActionSnapshotRequest {
            input_tick: cast_input_tick,
            pos_x: cast_pos_x,
            pos_y: cast_pos_y,
            pos_z: cast_pos_z,
            yaw: cast_yaw,
        },
    ) else {
        return Ok(());
    };
    if has_active_disabling_status(ctx, owner, now) {
        return Ok(());
    }
    if ctx.db.active_cast().caster().find(owner).is_some() {
        return Ok(());
    }
    if delivery.uses_global_cooldown && is_on_global_cooldown(ctx, owner, now) {
        return Ok(());
    }
    if is_on_named_cooldown(ctx, owner, action_id.as_str(), now) {
        return Ok(());
    }
    let Some(resource_cost) =
        resolve_ability_action_resource_cost_amount(ctx, owner, ability, delivery.resource_cost)
    else {
        return Ok(());
    };
    if !can_pay_action_resource_cost(ctx, owner, &resource_cost, now) {
        return Ok(());
    };

    let Some(authoritative) = crate::combat::actor_snapshot::actor_snapshot_for(ctx, owner) else {
        return Ok(());
    };
    let cast_state = crate::combat::actor_snapshot::CombatActorSnapshot {
        pos_x: snapshot.pos_x,
        pos_y: snapshot.pos_y,
        pos_z: snapshot.pos_z,
        facing_yaw: snapshot.facing_yaw,
        last_processed_tick: snapshot.last_processed_tick,
        ..authoritative
    };
    let launched = launch_movement_delivery(
        ctx,
        owner,
        ability,
        delivery,
        movement_action_kind_for_delivery(delivery),
        &action_id,
        target_id.as_str(),
        cast_state,
        now,
    )?;
    if !launched {
        return Ok(());
    }
    if !pay_action_resource_cost(ctx, owner, &resource_cost, now) {
        crate::spells::clear_active_cast(ctx, owner);
        clear_movement_action_for_owner(ctx, owner);
        return Ok(());
    }
    if delivery.uses_global_cooldown {
        stamp_global_cooldown_for_duration(
            ctx,
            owner,
            Duration::from_millis(delivery.global_cooldown_ms.max(1)),
            now,
        );
    }
    Ok(())
}

fn movement_action_kind_for_delivery(delivery: &MovementDeliveryRuntime) -> &'static str {
    match delivery.kind.as_str() {
        "DASH_TO_TARGET" => ACTION_KIND_DASH_TO_TARGET,
        _ => ACTION_KIND_DASH_TO_TARGET,
    }
}

pub(crate) fn launch_movement_delivery(
    ctx: &ReducerContext,
    owner: Identity,
    ability: &AbilityCatalog,
    delivery: &MovementDeliveryRuntime,
    movement_kind: &str,
    action_id: &SpellId,
    target_id: &str,
    cast_state: crate::combat::actor_snapshot::CombatActorSnapshot,
    now: Timestamp,
) -> Result<bool, String> {
    let Some(authoritative) = crate::combat::actor_snapshot::actor_snapshot_for(ctx, owner) else {
        log::info!(
            "[MOVEMENT_DELIVERY] caster={} action={} rejected reason=missing_authoritative_snapshot entry=launch",
            &owner.to_hex()[..8],
            action_id.as_str()
        );
        return Ok(false);
    };

    if ctx.db.active_cast().caster().find(owner).is_some() {
        log::info!(
            "[MOVEMENT_DELIVERY] caster={} action={} rejected reason=active_cast entry=launch",
            &owner.to_hex()[..8],
            action_id.as_str()
        );
        return Ok(false);
    }
    if reject_or_clear_stale_action(ctx, owner, now) {
        log::info!(
            "[MOVEMENT_DELIVERY] caster={} action={} rejected reason=movement_action_recovery entry=launch",
            &owner.to_hex()[..8],
            action_id.as_str()
        );
        return Ok(false);
    }

    let Some(target) = crate::spells::validate_movement_delivery_target(
        ctx,
        action_id,
        owner,
        &cast_state,
        target_id,
    ) else {
        return Ok(false);
    };
    mark_harmful_combat_action(ctx, owner, target.player_id, now, action_id.as_str());

    let use_air_path =
        crate::spells::special_movement_uses_air_path(ctx, owner, &authoritative, &cast_state);
    let movement_start = SpellVec3::new(cast_state.pos_x, cast_state.pos_y, cast_state.pos_z);
    let collision_policy = if use_air_path {
        crate::spells::SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_FIXED_Y
    } else {
        crate::spells::SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK
    };
    let (end_x, end_y, end_z) =
        crate::spells::movement_delivery_destination(action_id, &cast_state, &target);
    let baked = crate::spells::bake_linear_special_movement(
        ctx,
        owner,
        movement_start,
        SpellVec3::new(end_x, end_y, end_z),
        authoritative.hit_radius,
        authoritative.hit_height,
        collision_policy,
    );
    let duration_ms = crate::spells::horizontal_movement_duration_ms(
        cast_state.pos_x,
        cast_state.pos_z,
        baked.end.x,
        baked.end.z,
        delivery.speed,
        FIXED_TICK_MILLIS as u64,
    );
    let active_until = now + Duration::from_millis(duration_ms);
    let active_ticks = duration_ticks(duration_ms);
    let dx = baked.end.x - movement_start.x;
    let dz = baked.end.z - movement_start.z;
    let dir_mag = (dx * dx + dz * dz).sqrt();
    let (dir_x, dir_z) = if dir_mag > 0.0001 {
        (dx / dir_mag, dz / dir_mag)
    } else {
        (cast_state.facing_yaw.sin(), cast_state.facing_yaw.cos())
    };

    clear_interruptible_defense_for_owner(ctx, owner);
    arm_lingering_shade_for_voluntary_movement(
        ctx,
        owner,
        action_id.as_str(),
        ability.ability_id.as_str(),
        movement_start,
        baked.end,
        cast_state.facing_yaw,
        now,
    );
    crate::spells::begin_special_movement(
        ctx,
        owner,
        action_id.as_str(),
        now,
        duration_ms,
        movement_start,
        baked.end,
        cast_state.facing_yaw,
        collision_policy,
    );
    crate::spells::begin_active_cast(
        ctx,
        owner,
        action_id,
        target_id,
        baked.end.x,
        baked.end.y,
        baked.end.z,
        now,
        0,
        Some(Duration::from_millis(duration_ms)),
        "",
        0,
    );
    grant_primary_resource_amount(
        ctx,
        owner,
        primary_resource_gain_on_action_accept(ability.ability_id.as_str()),
        now,
    );
    upsert_movement_action(
        ctx,
        MovementActionState {
            owner,
            action_id: build_action_id(owner, movement_kind, now),
            kind: movement_kind.to_string(),
            ability_id: ability.ability_id.clone(),
            resolved_action_id: action_id.as_str().to_string(),
            started_at: now,
            effective_from_input_tick: cast_state.last_processed_tick,
            active_until_input_tick: cast_state.last_processed_tick.saturating_add(active_ticks),
            recovery_until_input_tick: cast_state.last_processed_tick.saturating_add(active_ticks),
            active_until,
            recovery_until: active_until,
            dir_x,
            dir_z,
            facing_yaw_start: cast_state.facing_yaw,
        },
    );
    log::info!(
        "[MOVEMENT_DELIVERY] caster={} action={} ability={} target={} launched start=({:.2},{:.2},{:.2}) end=({:.2},{:.2},{:.2}) duration_ms={} collision={} air_path={}",
        &owner.to_hex()[..8],
        action_id.as_str(),
        ability.ability_id,
        &target.player_id.to_hex()[..8],
        movement_start.x,
        movement_start.y,
        movement_start.z,
        baked.end.x,
        baked.end.y,
        baked.end.z,
        duration_ms,
        collision_policy,
        use_air_path
    );
    arm_auto_attack_if_unarmed_with_cadence(ctx, owner, target.player_id, now);
    Ok(true)
}

pub(crate) fn clear_movement_action_for_owner(ctx: &ReducerContext, owner: Identity) {
    ctx.db.movement_action_state().owner().delete(owner);
}

pub(crate) fn ensure_dodge_charge_state(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) -> FixedActionChargeState {
    ensure_fixed_action_charge_state(ctx, owner, ACTION_KIND_DODGE, now)
}

pub(crate) fn reset_dodge_charge_state_to_full(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    clear_fixed_action_charge_recoveries(ctx, owner, Some(ACTION_KIND_DODGE));
    let row = sync_fixed_action_charge_state(
        ctx,
        full_fixed_action_charge_state(owner, ACTION_KIND_DODGE, now),
        now,
    );
    upsert_fixed_action_charge_state(ctx, row);
}

pub(crate) fn sync_all_fixed_action_charge_states(ctx: &ReducerContext, now: Timestamp) {
    let owners: Vec<Identity> = ctx
        .db
        .player_state()
        .iter()
        .map(|row| row.player_id)
        .collect();
    for owner in owners {
        ensure_dodge_charge_state(ctx, owner, now);
    }
}

pub(crate) fn clear_fixed_action_charge_states_for_owner(ctx: &ReducerContext, owner: Identity) {
    clear_fixed_action_charge_recoveries(ctx, owner, None);
    let keys: Vec<String> = ctx
        .db
        .fixed_action_charge_state()
        .owner()
        .filter(owner)
        .map(|row| row.key)
        .collect();
    for key in keys {
        ctx.db.fixed_action_charge_state().key().delete(key);
    }
}

pub(crate) fn tick_fixed_action_charge_states(ctx: &ReducerContext, now: Timestamp) {
    let rows: Vec<FixedActionChargeState> = ctx.db.fixed_action_charge_state().iter().collect();
    for row in rows {
        if row.action_id.as_str() != ACTION_KIND_DODGE {
            continue;
        }
        let synced = sync_fixed_action_charge_state(ctx, row.clone(), now);
        if synced == row {
            // Full charges and not recharging: value-identical — skip the
            // per-tick upsert (tick audit T3 slice 2). The row carries no
            // ack/tick counter, so a skipped no-op write is invisible to
            // clients; consume/recharge/reset paths still write on change.
            continue;
        }
        upsert_fixed_action_charge_state(ctx, synced);
    }
}

fn consume_fixed_action_charge(
    ctx: &ReducerContext,
    owner: Identity,
    action_id: &str,
    now: Timestamp,
) -> bool {
    let row = ensure_fixed_action_charge_state(ctx, owner, action_id, now);
    if row.current_charges == 0 {
        return false;
    }

    insert_fixed_action_charge_recovery(
        ctx,
        owner,
        action_id,
        charge_recovery_timing(now, row.recharge_duration_ms),
    );
    let synced = sync_fixed_action_charge_state(ctx, row, now);
    upsert_fixed_action_charge_state(ctx, synced);
    true
}

fn ensure_fixed_action_charge_state(
    ctx: &ReducerContext,
    owner: Identity,
    action_id: &str,
    now: Timestamp,
) -> FixedActionChargeState {
    let key = fixed_action_charge_key(owner, action_id);
    if let Some(existing) = ctx.db.fixed_action_charge_state().key().find(key) {
        let synced = sync_fixed_action_charge_state(ctx, existing.clone(), now);
        if synced != existing {
            upsert_fixed_action_charge_state(ctx, synced.clone());
        }
        return synced;
    }

    let row = sync_fixed_action_charge_state(
        ctx,
        full_fixed_action_charge_state(owner, action_id, now),
        now,
    );
    ctx.db.fixed_action_charge_state().insert(row.clone());
    row
}

fn sync_fixed_action_charge_state(
    ctx: &ReducerContext,
    row: FixedActionChargeState,
    now: Timestamp,
) -> FixedActionChargeState {
    let (max_charges, recharge_duration_ms) =
        fixed_action_charge_config_for_owner(ctx, row.owner, row.action_id.as_str());
    let recharge_duration_ms = recharge_duration_ms.max(1);
    let mut recoveries = fixed_action_charge_recoveries(ctx, row.owner, row.action_id.as_str());
    if recoveries.is_empty() && row.current_charges < max_charges {
        for timing in legacy_charge_recovery_timings(&row, max_charges, recharge_duration_ms, now) {
            insert_fixed_action_charge_recovery(ctx, row.owner, row.action_id.as_str(), timing);
        }
        recoveries = fixed_action_charge_recoveries(ctx, row.owner, row.action_id.as_str());
    }

    let mut pending = Vec::with_capacity(recoveries.len());
    for recovery in recoveries {
        if now >= recovery.ready_at {
            delete_fixed_action_charge_recovery(ctx, recovery.recovery_id);
        } else {
            pending.push(recovery);
        }
    }
    pending.sort_by_key(|recovery| {
        (
            recovery.ready_at.to_micros_since_unix_epoch(),
            recovery.recovery_id,
        )
    });
    if pending.len() > max_charges as usize {
        for recovery in pending.drain(max_charges as usize..) {
            delete_fixed_action_charge_recovery(ctx, recovery.recovery_id);
        }
    }

    let timings: Vec<ChargeRecoveryTiming> = pending
        .iter()
        .map(ChargeRecoveryTiming::from_recovery)
        .collect();
    let state =
        summarize_charge_progress(max_charges, recharge_duration_ms, timings.as_slice(), now);

    FixedActionChargeState {
        max_charges: state.max_charges,
        recharge_duration_ms: state.recharge_duration_ms,
        current_charges: state.current_charges,
        is_recharging: state.is_recharging,
        recharge_started_at: state.recharge_started_at,
        next_charge_ready_at: state.next_charge_ready_at,
        ..row
    }
}

fn fixed_action_charge_recoveries(
    ctx: &ReducerContext,
    owner: Identity,
    action_id: &str,
) -> Vec<FixedActionChargeRecovery> {
    ctx.db
        .fixed_action_charge_recovery()
        .owner()
        .filter(owner)
        .filter(|recovery| recovery.action_id == action_id)
        .collect()
}

fn insert_fixed_action_charge_recovery(
    ctx: &ReducerContext,
    owner: Identity,
    action_id: &str,
    timing: ChargeRecoveryTiming,
) {
    crate::tick_metrics::record_table_write(
        crate::tick_metrics::TableWriteKind::FixedActionChargeRecovery,
    );
    ctx.db
        .fixed_action_charge_recovery()
        .insert(FixedActionChargeRecovery {
            recovery_id: 0,
            owner,
            action_id: action_id.to_string(),
            recharge_started_at: timing.recharge_started_at,
            ready_at: timing.ready_at,
        });
}

fn delete_fixed_action_charge_recovery(ctx: &ReducerContext, recovery_id: u64) {
    crate::tick_metrics::record_table_write(
        crate::tick_metrics::TableWriteKind::FixedActionChargeRecovery,
    );
    ctx.db
        .fixed_action_charge_recovery()
        .recovery_id()
        .delete(recovery_id);
}

fn clear_fixed_action_charge_recoveries(
    ctx: &ReducerContext,
    owner: Identity,
    action_id: Option<&str>,
) {
    let recovery_ids: Vec<u64> = ctx
        .db
        .fixed_action_charge_recovery()
        .owner()
        .filter(owner)
        .filter(|recovery| {
            action_id
                .map(|expected| recovery.action_id == expected)
                .unwrap_or(true)
        })
        .map(|recovery| recovery.recovery_id)
        .collect();
    for recovery_id in recovery_ids {
        delete_fixed_action_charge_recovery(ctx, recovery_id);
    }
}

fn upsert_fixed_action_charge_state(ctx: &ReducerContext, row: FixedActionChargeState) {
    crate::tick_metrics::record_table_write(
        crate::tick_metrics::TableWriteKind::FixedActionChargeState,
    );
    if ctx
        .db
        .fixed_action_charge_state()
        .key()
        .find(row.key.clone())
        .is_some()
    {
        ctx.db.fixed_action_charge_state().key().update(row);
    } else {
        ctx.db.fixed_action_charge_state().insert(row);
    }
}

fn full_fixed_action_charge_state(
    owner: Identity,
    action_id: &str,
    now: Timestamp,
) -> FixedActionChargeState {
    let (max_charges, recharge_duration_ms) = fixed_action_charge_config(action_id);
    FixedActionChargeState {
        key: fixed_action_charge_key(owner, action_id),
        owner,
        action_id: action_id.to_string(),
        current_charges: max_charges,
        max_charges,
        recharge_duration_ms: recharge_duration_ms.max(1),
        is_recharging: false,
        recharge_started_at: now,
        next_charge_ready_at: now,
    }
}

fn fixed_action_charge_key(owner: Identity, action_id: &str) -> String {
    format!("{}:{}", owner.to_hex(), action_id)
}

fn fixed_action_charge_config(action_id: &str) -> (u32, u64) {
    fixed_action_charge_config_with_dodge_recharge_reduction(action_id, 0.0)
}

fn fixed_action_charge_config_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
    action_id: &str,
) -> (u32, u64) {
    let dodge_recharge_time_reduction = if action_id == ACTION_KIND_DODGE
        && character_has_selected_discipline(ctx, owner, DISCIPLINE_SUBTLETY)
    {
        subtlety_dodge_recharge_time_reduction()
    } else {
        0.0
    };
    fixed_action_charge_config_with_dodge_recharge_reduction(
        action_id,
        dodge_recharge_time_reduction,
    )
}

fn fixed_action_charge_config_with_dodge_recharge_reduction(
    action_id: &str,
    dodge_recharge_time_reduction: f32,
) -> (u32, u64) {
    match action_id {
        ACTION_KIND_DODGE => (
            DODGE_MAX_CHARGES,
            reduced_dodge_recharge_duration_ms(dodge_recharge_time_reduction),
        ),
        _ => (0, 1),
    }
}

fn reduced_dodge_recharge_duration_ms(reduction: f32) -> u64 {
    let reduction = if reduction.is_finite() {
        reduction.clamp(0.0, 1.0)
    } else {
        0.0
    };
    ((DODGE_RECHARGE_MS as f64 * (1.0 - reduction as f64)).round() as u64).max(1)
}

#[derive(Clone, Copy)]
struct ChargeProgressState {
    current_charges: u32,
    max_charges: u32,
    recharge_duration_ms: u64,
    is_recharging: bool,
    recharge_started_at: Timestamp,
    next_charge_ready_at: Timestamp,
}

#[derive(Clone, Copy)]
struct ChargeRecoveryTiming {
    recharge_started_at: Timestamp,
    ready_at: Timestamp,
}

impl ChargeRecoveryTiming {
    fn from_recovery(recovery: &FixedActionChargeRecovery) -> Self {
        Self {
            recharge_started_at: recovery.recharge_started_at,
            ready_at: recovery.ready_at,
        }
    }
}

fn charge_recovery_timing(now: Timestamp, recharge_duration_ms: u64) -> ChargeRecoveryTiming {
    ChargeRecoveryTiming {
        recharge_started_at: now,
        ready_at: now + Duration::from_millis(recharge_duration_ms.max(1)),
    }
}

fn legacy_charge_recovery_timings(
    row: &FixedActionChargeState,
    max_charges: u32,
    recharge_duration_ms: u64,
    now: Timestamp,
) -> Vec<ChargeRecoveryTiming> {
    let missing_charges = max_charges.saturating_sub(row.current_charges.min(max_charges));
    if missing_charges == 0 {
        return Vec::new();
    }

    let recharge_duration_ms = recharge_duration_ms.max(1);
    let first_ready_at = if row.is_recharging && row.next_charge_ready_at > Timestamp::UNIX_EPOCH {
        row.next_charge_ready_at
    } else {
        now + Duration::from_millis(recharge_duration_ms)
    };
    (0..missing_charges)
        .map(|offset| {
            let ready_at = first_ready_at
                + Duration::from_millis(recharge_duration_ms.saturating_mul(offset as u64));
            ChargeRecoveryTiming {
                recharge_started_at: ready_at - Duration::from_millis(recharge_duration_ms),
                ready_at,
            }
        })
        .collect()
}

fn summarize_charge_progress(
    max_charges: u32,
    recharge_duration_ms: u64,
    recoveries: &[ChargeRecoveryTiming],
    now: Timestamp,
) -> ChargeProgressState {
    let earliest_pending = recoveries
        .iter()
        .filter(|recovery| recovery.ready_at > now)
        .min_by_key(|recovery| recovery.ready_at.to_micros_since_unix_epoch());
    let pending_count = recoveries
        .iter()
        .filter(|recovery| recovery.ready_at > now)
        .count()
        .min(max_charges as usize) as u32;
    let current_charges = max_charges.saturating_sub(pending_count);

    match earliest_pending {
        Some(recovery) => ChargeProgressState {
            current_charges,
            max_charges,
            recharge_duration_ms: recharge_duration_ms.max(1),
            is_recharging: true,
            recharge_started_at: recovery.recharge_started_at,
            next_charge_ready_at: recovery.ready_at,
        },
        None => ChargeProgressState {
            current_charges: max_charges,
            max_charges,
            recharge_duration_ms: recharge_duration_ms.max(1),
            is_recharging: false,
            recharge_started_at: Timestamp::UNIX_EPOCH,
            next_charge_ready_at: Timestamp::UNIX_EPOCH,
        },
    }
}

pub(crate) fn tick_movement_actions(ctx: &ReducerContext, now: Timestamp) {
    let actions: Vec<MovementActionState> = ctx.db.movement_action_state().iter().collect();
    for action in actions {
        let Some(kind) = MovementActionKind::from_wire(action.kind.as_str()) else {
            clear_movement_action_for_owner(ctx, action.owner);
            continue;
        };

        let Some(state) = ctx.db.player_state().player_id().find(action.owner) else {
            clear_movement_action_for_owner(ctx, action.owner);
            continue;
        };

        if !state.alive || has_active_disabling_status(ctx, action.owner, now) {
            ctx.db
                .special_movement_runtime()
                .owner()
                .delete(action.owner);
            clear_movement_action_for_owner(ctx, action.owner);
            continue;
        }

        match kind {
            MovementActionKind::Dodge => {
                if now >= action.recovery_until {
                    clear_movement_action_for_owner(ctx, action.owner);
                }
            }
            MovementActionKind::DashToTarget => {
                if ctx.db.active_cast().caster().find(action.owner).is_none() {
                    clear_movement_action_for_owner(ctx, action.owner);
                }
            }
        }
    }
}

pub(crate) fn reject_or_clear_stale_action(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) -> bool {
    let Some(existing) = ctx.db.movement_action_state().owner().find(owner) else {
        return false;
    };

    if now < existing.recovery_until {
        return true;
    }

    clear_movement_action_for_owner(ctx, owner);
    false
}

fn upsert_movement_action(ctx: &ReducerContext, row: MovementActionState) {
    if ctx
        .db
        .movement_action_state()
        .owner()
        .find(row.owner)
        .is_some()
    {
        ctx.db.movement_action_state().owner().update(row);
    } else {
        ctx.db.movement_action_state().insert(row);
    }
}

fn build_action_id(owner: Identity, kind: &str, started_at: Timestamp) -> String {
    format!(
        "{}:{}:{}",
        owner.to_hex(),
        kind,
        started_at.to_micros_since_unix_epoch()
    )
}

fn sanitize_effective_input_tick(last_processed_tick: u32, requested_tick: u32) -> u32 {
    if requested_tick == 0 {
        return last_processed_tick;
    }

    let min_tick = last_processed_tick.saturating_sub(MAX_ACTION_INPUT_TICK_DRIFT);
    let max_tick = last_processed_tick.saturating_add(MAX_ACTION_INPUT_TICK_DRIFT);
    requested_tick.clamp(min_tick, max_tick)
}

fn dodge_uses_air_path(
    ctx: &ReducerContext,
    owner: Identity,
    authoritative: &crate::combat::actor_snapshot::CombatActorSnapshot,
    dodge_state: &crate::combat::actor_snapshot::CombatActorSnapshot,
) -> bool {
    crate::spells::special_movement_uses_air_path(ctx, owner, authoritative, dodge_state)
}

fn resolve_dodge_direction(facing_yaw: f32, move_forward: f32, move_strafe: f32) -> (f32, f32) {
    let mut local_forward = move_forward;
    let mut local_strafe = move_strafe;
    if local_forward.abs() < 0.05 && local_strafe.abs() < 0.05 {
        local_forward = 1.0;
        local_strafe = 0.0;
    }

    let magnitude = (local_forward * local_forward + local_strafe * local_strafe)
        .sqrt()
        .max(0.0001);
    local_forward /= magnitude;
    local_strafe /= magnitude;

    let forward_x = facing_yaw.sin();
    let forward_z = facing_yaw.cos();
    let right_x = facing_yaw.cos();
    let right_z = -facing_yaw.sin();
    let dir_x = forward_x * local_forward + right_x * local_strafe;
    let dir_z = forward_z * local_forward + right_z * local_strafe;
    let dir_mag = (dir_x * dir_x + dir_z * dir_z).sqrt().max(0.0001);
    (dir_x / dir_mag, dir_z / dir_mag)
}

fn duration_ticks(duration_ms: u64) -> u32 {
    ((duration_ms + FIXED_TICK_MILLIS as u64 - 1) / FIXED_TICK_MILLIS as u64) as u32
}

fn movement_duration_ms(
    start_x: f32,
    start_z: f32,
    end_x: f32,
    end_z: f32,
    speed_meters_per_second: f32,
) -> u64 {
    let dx = end_x - start_x;
    let dz = end_z - start_z;
    let distance = (dx * dx + dz * dz).sqrt();
    ((distance / speed_meters_per_second.max(0.01)) * 1000.0)
        .ceil()
        .max(FIXED_TICK_MILLIS as f32) as u64
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ts(ms: u64) -> Timestamp {
        Timestamp::from_micros_since_unix_epoch((ms * 1000) as i64)
    }

    fn recovery(started_ms: u64, recharge_duration_ms: u64) -> ChargeRecoveryTiming {
        charge_recovery_timing(ts(started_ms), recharge_duration_ms)
    }

    fn movement_snapshot(
        pos_y: f32,
        grounded: bool,
    ) -> crate::combat::actor_snapshot::CombatActorSnapshot {
        crate::combat::actor_snapshot::CombatActorSnapshot {
            player_id: Identity::ZERO,
            alive: true,
            pos_x: 0.0,
            pos_y,
            pos_z: 0.0,
            facing_yaw: 0.0,
            grounded,
            hit_radius: 0.5,
            hit_height: 1.8,
            last_processed_tick: 0,
        }
    }

    #[test]
    fn dodge_air_path_uses_grounding_and_accepted_snapshot_height() {
        let grounded_authoritative = movement_snapshot(2.0, true);
        let grounded_dodge_state = movement_snapshot(2.0, true);
        assert!(!crate::spells::special_movement_uses_air_path_with_ground(
            &grounded_authoritative,
            &grounded_dodge_state,
            2.0
        ));

        let airborne_authoritative = movement_snapshot(3.0, false);
        assert!(crate::spells::special_movement_uses_air_path_with_ground(
            &airborne_authoritative,
            &grounded_dodge_state,
            2.0
        ));

        let raised_dodge_state = movement_snapshot(2.2, true);
        assert!(crate::spells::special_movement_uses_air_path_with_ground(
            &grounded_authoritative,
            &raised_dodge_state,
            2.0
        ));
        assert!(!crate::spells::special_movement_uses_air_path_with_ground(
            &grounded_authoritative,
            &raised_dodge_state,
            2.2
        ));
    }

    #[test]
    fn no_input_dodge_defaults_forward_for_representative_yaws() {
        let (x0, z0) = resolve_dodge_direction(0.0, 0.0, 0.0);
        assert!(x0.abs() < 0.0001);
        assert!((z0 - 1.0).abs() < 0.0001);

        let (x90, z90) = resolve_dodge_direction(std::f32::consts::FRAC_PI_2, 0.0, 0.0);
        assert!((x90 - 1.0).abs() < 0.0001);
        assert!(z90.abs() < 0.0001);

        let (x180, z180) = resolve_dodge_direction(std::f32::consts::PI, 0.0, 0.0);
        assert!(x180.abs() < 0.0001);
        assert!((z180 + 1.0).abs() < 0.0001);
    }

    #[test]
    fn spent_charges_recover_on_their_independent_deadlines() {
        let recoveries = [
            recovery(0, 10_000),
            recovery(2_000, 10_000),
            recovery(4_000, 10_000),
        ];

        let state = summarize_charge_progress(3, 10_000, &recoveries, ts(9_999));
        assert_eq!(state.current_charges, 0);
        assert_eq!(state.next_charge_ready_at, ts(10_000));

        let state = summarize_charge_progress(3, 10_000, &recoveries, ts(10_000));
        assert_eq!(state.current_charges, 1);
        assert!(state.is_recharging);
        assert_eq!(state.recharge_started_at, ts(2_000));
        assert_eq!(state.next_charge_ready_at, ts(12_000));

        let state = summarize_charge_progress(3, 10_000, &recoveries, ts(12_000));
        assert_eq!(state.current_charges, 2);
        assert_eq!(state.next_charge_ready_at, ts(14_000));

        let state = summarize_charge_progress(3, 10_000, &recoveries, ts(14_000));
        assert_eq!(state.current_charges, 3);
        assert!(!state.is_recharging);
        assert_eq!(state.recharge_started_at, Timestamp::UNIX_EPOCH);
        assert_eq!(state.next_charge_ready_at, Timestamp::UNIX_EPOCH);
    }

    #[test]
    fn later_spend_does_not_delay_an_existing_recovery() {
        let recoveries = [recovery(0, 10_000), recovery(5_000, 10_000)];
        let state = summarize_charge_progress(2, 10_000, &recoveries, ts(5_000));

        assert_eq!(state.current_charges, 0);
        assert_eq!(state.recharge_started_at, ts(0));
        assert_eq!(state.next_charge_ready_at, ts(10_000));
    }

    #[test]
    fn legacy_sequential_state_migrates_without_losing_missing_charges() {
        let mut row = full_fixed_action_charge_state(Identity::ZERO, ACTION_KIND_DODGE, ts(1_000));
        row.current_charges = row.max_charges - 3;
        row.is_recharging = true;
        row.recharge_started_at = ts(1_000);
        row.next_charge_ready_at = ts(11_000);

        let recoveries = legacy_charge_recovery_timings(&row, row.max_charges, 10_000, ts(2_000));
        assert_eq!(recoveries.len(), 3);
        assert_eq!(recoveries[0].ready_at, ts(11_000));
        assert_eq!(recoveries[1].ready_at, ts(21_000));
        assert_eq!(recoveries[2].ready_at, ts(31_000));
    }

    #[test]
    fn full_fixed_action_charge_state_resets_dodge_to_full() {
        let owner =
            Identity::from_hex("0000000000000000000000000000000000000000000000000000000000000001")
                .expect("valid test identity");
        let row = full_fixed_action_charge_state(owner, ACTION_KIND_DODGE, ts(42));

        assert_eq!(row.owner, owner);
        assert_eq!(row.action_id, ACTION_KIND_DODGE);
        assert_eq!(row.current_charges, DODGE_MAX_CHARGES);
        assert_eq!(row.max_charges, DODGE_MAX_CHARGES);
        assert_eq!(row.recharge_duration_ms, DODGE_RECHARGE_MS);
        assert!(!row.is_recharging);
    }

    #[test]
    fn fleet_footed_reduces_dodge_recharge_time_by_twenty_percent() {
        let (base_max_charges, recharge_duration_ms) =
            fixed_action_charge_config_with_dodge_recharge_reduction(ACTION_KIND_DODGE, 0.0);
        let (fleet_footed_max_charges, fleet_footed_recharge_duration_ms) =
            fixed_action_charge_config_with_dodge_recharge_reduction(ACTION_KIND_DODGE, 0.2);

        assert_eq!(fleet_footed_max_charges, base_max_charges);
        assert_eq!(fleet_footed_recharge_duration_ms, 8_000);
        assert_eq!(
            fleet_footed_recharge_duration_ms,
            recharge_duration_ms * 4 / 5
        );
    }
}

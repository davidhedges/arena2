use std::time::Duration;

#[cfg(feature = "spellcasting_terminal_harness")]
use spacetimedb::reducer;
use spacetimedb::{Identity, ReducerContext, Table, Timestamp};

use crate::action_ids::AuthoredActionId;
use crate::action_prediction::{
    insert_predicted_action_result, ActionPredictionToken, ActionResultKind, PredictedActionFamily,
};
use crate::action_snapshot::{
    validate_authoritative_action_snapshot, ActionSnapshotFallback, ActionSnapshotRequest,
};
#[cfg(feature = "spellcasting_terminal_harness")]
use crate::arena::upsert_player_world;
use crate::arena::{
    arena_seed_for_identity, open_world_scene_name_for_identity, players_share_world_context,
};
use crate::auto_attack::arm_auto_attack_if_unarmed_with_cadence;
#[cfg(feature = "spellcasting_terminal_harness")]
use crate::combat::new_player_state;
use crate::combat::player_snapshot::{
    collect_player_snapshots, player_snapshot_for, PlayerSnapshot, PlayerSnapshotSet,
};
use crate::combat::scene_query::{
    first_hit_on_segment, has_line_of_sight, is_direction_within_facing_arc, line_of_sight_blocker,
    terrain_surface_y_for_caster, CombatAreaShape, SceneHitKind,
};
use crate::combat::{
    has_active_disabling_status, mark_harmful_combat_action, queue_effects,
    temporary_combat_modifiers, timestamp_to_micros, ActiveCombatProjectile, CombatEvent,
    DamageDelivery, EffectPacket, ProjectilePresentationEvent, StatusApplication, StatusPayload,
    StatusPolarity, StatusStackGroupDefault, COMBAT_METADATA_NONE, COMBAT_SCALAR_NONE,
    COMBAT_SEQUENCE_NONE,
};
use crate::defense::{
    clear_interruptible_defense_for_owner, resolve_defensible_combat_hit, CombatHitDeliveryKind,
    DefenseResolution, DefensibleCombatHit,
};
use crate::derived_stats::{
    derived_combat_stats_for_owner, scale_cast_duration,
    scale_fortify_temporary_hitpoints_from_allocations,
};
use crate::player_intent::PlayerIntent;
#[cfg(feature = "spellcasting_terminal_harness")]
use crate::player_physics::{commit_player_physics, PhysicsWriteMode, PlayerPhysics};
use crate::practice::is_training_instance;
use crate::progression::{
    active_selectable_ability_for_authored_action, movement_delivery_for_action_id,
    MovementDeliveryRuntime,
};
use crate::relations::{target_audience_allows, TargetAudience};
use crate::resources::{
    can_pay_action_resource_cost, grant_primary_resource_amount, pay_action_resource_cost,
    resolve_ability_action_resource_cost_amount, ResolvedActionResourceCost,
};
use crate::world_collision::{
    resolve_world_horizontal_collision_y_with_layout_for_scene,
    surface_height_for_world_at_y_with_layout_for_scene,
};

#[cfg(feature = "spellcasting_terminal_harness")]
use super::cooldowns::stamp_global_cooldown;
use super::cooldowns::{
    clear_global_cooldown_if_matches, is_on_cooldown, is_on_global_cooldown, stamp_cooldown,
    stamp_global_cooldown_for_duration, stamp_named_cooldown_for_duration,
};
use super::events::{
    emit_spell_combat_event, emit_spell_combat_event_with_damage, next_spell_instance_id,
    SpellCombatEventPayload, SpellCombatEventScalar, Vec3,
};
use super::manifest::{
    BespokeRuntimeSpell, ImpactEffect, InstantBeamChargeScaling, MeteorSkyOrigin, SpellDefinition,
};
use super::{
    normalize_vec3, ActiveBespokeSpell, ActiveCast, CastPredictionCorrelation, ChannelCastRuntime,
    PendingAreaImpact, PendingCastCancel, PendingCastRequest, SpecialMovementRuntime,
    SpellBehavior, SpellId, EVENT_AREA_IMPACT, EVENT_CAST, EVENT_CONTACT, EVENT_FIZZLE,
    EVENT_IMPACT, EVENT_RELEASE, EVENT_UPDATE,
};
use crate::combat::scene_query::aoe_hits_player;

#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::combat::active_combat_projectile as _;
#[allow(unused_imports)]
use crate::combat::combat_event as _;
#[allow(unused_imports)]
use crate::combat::projectile_presentation_event as _;
#[allow(unused_imports)]
use crate::player_intent::player_intent as _;
#[cfg(feature = "spellcasting_terminal_harness")]
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::spells::active_bespoke_spell as _;
#[allow(unused_imports)]
use crate::spells::active_cast as _;
#[allow(unused_imports)]
use crate::spells::cast_prediction_correlation as _;
#[allow(unused_imports)]
use crate::spells::channel_cast_runtime as _;
#[allow(unused_imports)]
use crate::spells::global_cooldown as _;
#[allow(unused_imports)]
use crate::spells::pending_area_impact as _;
#[allow(unused_imports)]
use crate::spells::pending_cast_cancel as _;
#[allow(unused_imports)]
use crate::spells::pending_cast_request as _;
use crate::spells::special_movement_runtime as _;

const TARGET_FACING_ARC_RADIANS: f32 = std::f32::consts::PI;
const FACING_DOT_EPSILON: f32 = 0.0001;
const SPECIAL_MOVEMENT_AIR_PATH_GROUND_CLEARANCE: f32 = 0.10;
// Spell projectile delivery V1 emits exactly one gameplay projectile. Keep the
// sequence-indexed child id shape so multi-projectile delivery can add p1/p2
// rows without changing action identity or cue resolution contracts.
const PROJECTILE_SEQUENCE_INDEX_V1: u32 = 0;

fn resolved_primary_resource_cost_for_action(
    ctx: &ReducerContext,
    caster: Identity,
    spell_kind: &SpellId,
) -> Option<ResolvedActionResourceCost> {
    let spell_cost = spell_primary_resource_cost_for_action(spell_kind);
    let Some(ability) = active_selectable_ability_for_authored_action(
        ctx,
        caster,
        &AuthoredActionId::new(spell_kind.as_str()),
    ) else {
        return Some(spell_cost);
    };

    resolve_ability_action_resource_cost_amount(ctx, caster, &ability, spell_cost.amount)
}

fn spell_primary_resource_cost_for_action(spell_kind: &SpellId) -> ResolvedActionResourceCost {
    let definition = super::catalog::spell_definition(spell_kind)
        .expect("validated spell id must resolve to a definition");
    ResolvedActionResourceCost::primary(definition.primary_resource_cost)
}

fn instant_beam_charge_scaling() -> InstantBeamChargeScaling {
    bespoke_spell_definition(BespokeRuntimeSpell::InstantBeam)
        .expect("validated spell catalog must define INSTANT_BEAM")
        .secondary
        .instant_beam
        .and_then(|secondary| secondary.charge_scaling)
        .expect("validated spell catalog must define INSTANT_BEAM charge scaling")
}

fn meteor_sky_origin() -> MeteorSkyOrigin {
    bespoke_spell_definition(BespokeRuntimeSpell::Meteor)
        .expect("validated spell catalog must define METEOR")
        .secondary
        .area
        .as_ref()
        .and_then(|secondary| secondary.sky_origin)
        .expect("validated spell catalog must define METEOR sky_origin")
}

fn bespoke_spell_definition(
    spell: BespokeRuntimeSpell,
) -> Result<&'static SpellDefinition, String> {
    super::catalog::require_spell_definition_by_str(spell.as_str())
}

fn push_impact_effect_packets(
    effects: &mut Vec<EffectPacket>,
    impact_effects: &[ImpactEffect],
    source: Identity,
    target: Identity,
    spell_id: &str,
    action_key: &str,
    positive_damage: bool,
) {
    for effect in impact_effects {
        if effect.requires_positive_damage() && !positive_damage {
            continue;
        }
        effects.push(effect.to_effect_packet(
            source,
            target,
            spell_id,
            StatusPolarity::Debuff,
            action_key,
        ));
    }
}

fn movement_impact_effects(movement: &MovementDeliveryRuntime) -> Vec<ImpactEffect> {
    movement
        .impact_effects
        .iter()
        .map(|effect| effect.status.clone())
        .collect()
}
const SPECIAL_MOVEMENT_BAKE_STEP_METERS: f32 = 0.20;
const SPECIAL_MOVEMENT_BLOCK_EPSILON: f32 = 0.001;
const SPECIAL_MOVEMENT_FIXED_Y_TERRAIN_EPSILON: f32 = 0.001;
const SPECIAL_MOVEMENT_PATH_LINEAR: &str = "LINEAR";
pub(crate) const SPECIAL_MOVEMENT_FACING_FACE_PATH: &str = "FACE_PATH";
pub(crate) const SPECIAL_MOVEMENT_FACING_FACE_START: &str = "FACE_START";
pub(crate) const SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK: &str = "STOP_AT_BLOCK";
pub(crate) const SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_FIXED_Y: &str = "STOP_AT_BLOCK_FIXED_Y";
const SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_KEEP_HEIGHT_LEGACY: &str =
    "STOP_AT_BLOCK_KEEP_HEIGHT";
const SPECIAL_MOVEMENT_RESOLVE_AT_END: &str = "RESOLVE_AT_END";
const SPELL_BLOCK_IMPACT_HEIGHT_SCALE: f32 = 0.62;
const SPELL_BLOCK_IMPACT_FORWARD_PADDING: f32 = 0.2;
const CAST_PREDICTION_ROW_RETENTION: Duration = Duration::from_secs(5);
const PRE_END_CANCEL_ACCEPTANCE_GRACE: Duration = Duration::from_millis(100);
const MAX_PREDICTED_CAST_ID_LEN: usize = 64;
const SPELL_PREDICTION_RESULT_ACCEPTED: &str = "accepted";
const SPELL_PREDICTION_RESULT_REJECTED: &str = "rejected";
const SPELL_PREDICTION_RESULT_CANCELED: &str = "canceled";
const SPELL_PREDICTION_RESULT_CANCEL_TOO_LATE: &str = "cancel_too_late";
const SPELL_PREDICTION_RESULT_STALE_TOKEN: &str = "stale_token";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum CastState {
    Idle,
    Casting,
}

pub(crate) struct BakedLinearSpecialMovement {
    pub end: Vec3,
}

fn cast_state_for(ctx: &ReducerContext, caster: Identity) -> CastState {
    if ctx.db.active_cast().caster().find(caster).is_some() {
        CastState::Casting
    } else {
        CastState::Idle
    }
}

fn log_cast_rejected(caster: Identity, spell_kind: &SpellId, reason: &str, detail: &str) {
    if detail.is_empty() {
        log::info!(
            "[SPELL_CAST] caster={} spell={} rejected reason={}",
            &caster.to_hex()[..8],
            spell_kind.as_str(),
            reason
        );
    } else {
        log::info!(
            "[SPELL_CAST] caster={} spell={} rejected reason={} {}",
            &caster.to_hex()[..8],
            spell_kind.as_str(),
            reason,
            detail
        );
    }
}

// Design rule:
// Targeted spells always resolve the target server-side.
// Facing-cast spells always use the player's facing direction.
// Only point-targeted spells accept explicit aim input.
// This keeps combat movement-driven and avoids cursor-based skillshots.
//
// Active-cast lifecycle (authoritative):
// 1. cast_spell() rejects requests while already casting / on GCD / on cooldown.
// 2. Instant spells validate, commit resource cost, then execute effects.
// 3. Cast-time spells validate via process_spell_cast(...ValidateOnly), then begin_active_cast().
// 4. tick_active_casts() either completes on ends_at or interrupts on movement / airborne state.
// 5. release_cast() early-completes release-cast spells (InstantBeam), using charge_pct.
// 6. finish_active_cast() executes and then consumes the active_cast row exactly once.
pub(super) fn cast_spell(
    ctx: &ReducerContext,
    spell_kind: &SpellId,
    target_id: &str,
    aim_x: f32,
    aim_y: f32,
    aim_z: f32,
    cast_input_tick: u32,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
    predicted_cast_id: String,
    client_action_seq: u64,
) -> Result<(), String> {
    cast_spell_for(
        ctx,
        ctx.sender(),
        spell_kind,
        target_id,
        aim_x,
        aim_y,
        aim_z,
        cast_input_tick,
        cast_pos_x,
        cast_pos_y,
        cast_pos_z,
        cast_yaw,
        ctx.timestamp,
        predicted_cast_id,
        client_action_seq,
    )
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn queue_pending_cast_request(
    ctx: &ReducerContext,
    spell_kind: &SpellId,
    target_id: &str,
    aim_x: f32,
    aim_y: f32,
    aim_z: f32,
    cast_input_tick: u32,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
    predicted_cast_id: String,
    client_action_seq: u64,
) -> Result<(), String> {
    let caster = ctx.sender();
    prune_cast_prediction_rows(ctx, ctx.timestamp);
    if !valid_cast_action_token(predicted_cast_id.as_str(), client_action_seq) {
        log_cast_rejected(caster, spell_kind, "invalid_predicted_cast_id", "");
        return Ok(());
    }

    if ctx
        .db
        .pending_cast_request()
        .caster()
        .find(caster)
        .is_some()
    {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        log_cast_rejected(caster, spell_kind, "pending_cast_exists", "");
        return Ok(());
    }

    let Some(definition) = super::catalog::spell_definition(spell_kind) else {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        log_cast_rejected(caster, spell_kind, "unknown_spell", "");
        return Ok(());
    };
    let Some(caster_state) = player_snapshot_for(ctx, caster) else {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        log_cast_rejected(caster, spell_kind, "missing_player_snapshot", "");
        return Ok(());
    };
    if !caster_state.alive {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        log_cast_rejected(caster, spell_kind, "caster_dead", "");
        return Ok(());
    }
    if has_active_disabling_status(ctx, caster, ctx.timestamp)
        && !spell_can_be_cast_while_disabled(definition)
    {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        log_cast_rejected(caster, spell_kind, "active_disabling_status", "");
        return Ok(());
    }
    if cast_state_for(ctx, caster) == CastState::Casting {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        return Ok(());
    }
    if definition.uses_global_cooldown && is_on_global_cooldown(ctx, caster, ctx.timestamp) {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        return Ok(());
    }
    if is_on_cooldown(ctx, caster, spell_kind, ctx.timestamp) {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        return Ok(());
    }

    ctx.db.pending_cast_request().insert(PendingCastRequest {
        caster,
        spell_id: spell_kind.as_str().to_string(),
        target_id: target_id.to_string(),
        aim_x,
        aim_y,
        aim_z,
        cast_input_tick,
        cast_pos_x,
        cast_pos_y,
        cast_pos_z,
        cast_yaw,
        predicted_cast_id,
        client_action_seq,
        received_at: ctx.timestamp,
        received_at_micros: timestamp_to_micros(ctx.timestamp),
    });

    Ok(())
}

pub(crate) fn resolve_pending_casts(ctx: &ReducerContext, now: Timestamp) -> Result<(), String> {
    prune_cast_prediction_rows(ctx, now);
    let mut requests: Vec<PendingCastRequest> = ctx.db.pending_cast_request().iter().collect();
    requests.sort_by_key(|request| request.received_at_micros);

    for request in requests {
        if !pending_cast_request_is_ready(ctx, &request) {
            continue;
        }

        ctx.db
            .pending_cast_request()
            .caster()
            .delete(request.caster);
        let Ok(spell_kind) = SpellId::new(request.spell_id.as_str()) else {
            record_spell_prediction_result(
                ctx,
                request.caster,
                "",
                request.predicted_cast_id.as_str(),
                request.client_action_seq,
                SPELL_PREDICTION_RESULT_REJECTED,
                now,
            );
            continue;
        };

        cast_spell_for(
            ctx,
            request.caster,
            &spell_kind,
            request.target_id.as_str(),
            request.aim_x,
            request.aim_y,
            request.aim_z,
            request.cast_input_tick,
            request.cast_pos_x,
            request.cast_pos_y,
            request.cast_pos_z,
            request.cast_yaw,
            // Pending casts validate on the tick-aligned game loop, but the
            // accepted cast's effective start remains the reducer receipt time.
            // Otherwise the authoritative end extends mid-cast and visibly
            // rewinds the predicted cast bar.
            request.received_at,
            request.predicted_cast_id,
            request.client_action_seq,
        )?;
    }

    Ok(())
}

fn pending_cast_request_is_ready(ctx: &ReducerContext, request: &PendingCastRequest) -> bool {
    // Resolve only after the authoritative movement loop has advanced through
    // the client-authored cast tick. At that point player_intent reflects all
    // movement commands the client authored before the cast, including key-up
    // releases that may have arrived before the cast reducer.
    match player_snapshot_for(ctx, request.caster) {
        Some(snapshot) => snapshot.last_processed_tick >= request.cast_input_tick,
        None => true,
    }
}

pub(crate) fn cast_spell_for(
    ctx: &ReducerContext,
    caster: Identity,
    spell_kind: &SpellId,
    target_id: &str,
    aim_x: f32,
    aim_y: f32,
    aim_z: f32,
    cast_input_tick: u32,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
    cast_started_at: Timestamp,
    predicted_cast_id: String,
    client_action_seq: u64,
) -> Result<(), String> {
    prune_cast_prediction_rows(ctx, ctx.timestamp);
    if !valid_cast_action_token(predicted_cast_id.as_str(), client_action_seq) {
        log_cast_rejected(caster, spell_kind, "invalid_predicted_cast_id", "");
        return Ok(());
    }
    if has_matching_pending_cancel(ctx, caster, predicted_cast_id.as_str(), client_action_seq) {
        delete_pending_cast_cancel(ctx, caster, predicted_cast_id.as_str());
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_CANCELED,
            ctx.timestamp,
        );
        log_cast_rejected(
            caster,
            spell_kind,
            "cancelled_before_start",
            predicted_cast_id.as_str(),
        );
        return Ok(());
    }

    let Some(caster_state) = player_snapshot_for(ctx, caster) else {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        log_cast_rejected(caster, spell_kind, "missing_player_snapshot", "");
        return Ok(());
    };
    if !caster_state.alive {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        log_cast_rejected(caster, spell_kind, "caster_dead", "");
        return Ok(());
    }
    let Some(validated_snapshot) = validate_authoritative_action_snapshot(
        ctx,
        caster,
        ActionSnapshotRequest {
            input_tick: cast_input_tick,
            pos_x: cast_pos_x,
            pos_y: cast_pos_y,
            pos_z: cast_pos_z,
            yaw: cast_yaw,
        },
    ) else {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        log_cast_rejected(
            caster,
            spell_kind,
            "invalid_action_snapshot",
            &format!("tick={cast_input_tick}"),
        );
        return Ok(());
    };
    if let ActionSnapshotFallback::PositionDeltaExceeded {
        position_delta,
        allowed_delta,
        ..
    } = validated_snapshot.fallback
    {
        log::debug!(
            "[SPELL_CAST_FRAME_DROP] caster={} cast_tick={} server_tick={} pos_delta={:.2} allowed={:.2}",
            caster_state.player_id.to_hex(),
            cast_input_tick,
            caster_state.last_processed_tick,
            position_delta,
            allowed_delta
        );
    }
    let cast_state = PlayerSnapshot {
        pos_x: validated_snapshot.pos_x,
        pos_y: validated_snapshot.pos_y,
        pos_z: validated_snapshot.pos_z,
        facing_yaw: validated_snapshot.facing_yaw,
        last_processed_tick: validated_snapshot.last_processed_tick,
        ..caster_state
    };
    let now = ctx.timestamp;
    let Some(definition) = super::catalog::spell_definition(spell_kind) else {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            now,
        );
        log_cast_rejected(caster, spell_kind, "unknown_spell", "");
        return Ok(());
    };
    if has_active_disabling_status(ctx, caster, ctx.timestamp)
        && !spell_can_be_cast_while_disabled(definition)
    {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        log_cast_rejected(caster, spell_kind, "active_disabling_status", "");
        return Ok(());
    }
    let uses_global_cooldown = definition.uses_global_cooldown;
    let Some(primary_resource_cost) =
        resolved_primary_resource_cost_for_action(ctx, caster, spell_kind)
    else {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            now,
        );
        return Ok(());
    };
    if cast_state_for(ctx, caster_state.player_id) == CastState::Casting {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            now,
        );
        return Ok(());
    }
    if uses_global_cooldown && is_on_global_cooldown(ctx, caster_state.player_id, now) {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            now,
        );
        return Ok(());
    }
    // Cooldown rejection is silent by design; v1 emits no rejection events.
    // UI derives cooldown state from COMBAT_CAST events on the client.
    if is_on_cooldown(ctx, caster_state.player_id, spell_kind, now) {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            now,
        );
        return Ok(());
    }
    if violates_cast_mobility_requirement(
        spell_kind,
        caster_state.grounded,
        ctx.db.player_intent().identity().find(caster).as_ref(),
    ) {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            now,
        );
        return Ok(());
    }
    if !can_pay_action_resource_cost(ctx, caster_state.player_id, &primary_resource_cost, now) {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            now,
        );
        return Ok(());
    }

    if BespokeRuntimeSpell::from_spell_id(spell_kind) == Some(BespokeRuntimeSpell::Electrocute) {
        let can_start = process_spell_cast(
            ctx,
            &cast_state,
            caster,
            spell_kind,
            target_id,
            aim_x,
            aim_y,
            aim_z,
            CastExecutionMode::ValidateOnly,
            1,
            1.0,
            "",
            "",
        )?;
        if !can_start {
            record_spell_prediction_result(
                ctx,
                caster,
                "",
                predicted_cast_id.as_str(),
                client_action_seq,
                SPELL_PREDICTION_RESULT_REJECTED,
                now,
            );
            return Ok(());
        }
        mark_harmful_targeted_spell_start(ctx, caster, spell_kind, target_id, now);
        clear_interruptible_defense_for_owner(ctx, caster);
        let active_cast = begin_active_cast(
            ctx,
            caster,
            spell_kind,
            target_id,
            aim_x,
            aim_y,
            aim_z,
            cast_started_at,
            cast_input_tick,
            Some(Duration::from_secs_f32(definition.duration.max(0.01))),
            predicted_cast_id.as_str(),
            client_action_seq,
        );
        record_cast_prediction_correlation(
            ctx,
            caster,
            active_cast.cast_id.as_str(),
            predicted_cast_id.as_str(),
            client_action_seq,
            now,
        );
        record_spell_prediction_result(
            ctx,
            caster,
            active_cast.cast_id.as_str(),
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_ACCEPTED,
            now,
        );
        // begin_active_cast already emitted public COMBAT_CAST. If a post-accept
        // server finalization step fails, emit public fizzle for presentation/VFX
        // cleanup and owner-only rejected result for local reconciliation.
        let started = start_electrocute_channel(ctx, &active_cast, &caster_state, now)?;
        if !started {
            fizzle_active_cast_row_for_interrupt(ctx, &active_cast, &caster_state, spell_kind, now);
            record_spell_prediction_result(
                ctx,
                caster,
                active_cast.cast_id.as_str(),
                predicted_cast_id.as_str(),
                client_action_seq,
                SPELL_PREDICTION_RESULT_REJECTED,
                now,
            );
            return Ok(());
        }
        if !finalize_primary_resource_on_cast_start(ctx, caster, spell_kind, now) {
            fizzle_active_cast_row_for_interrupt(ctx, &active_cast, &caster_state, spell_kind, now);
            record_spell_prediction_result(
                ctx,
                caster,
                active_cast.cast_id.as_str(),
                predicted_cast_id.as_str(),
                client_action_seq,
                SPELL_PREDICTION_RESULT_REJECTED,
                now,
            );
            return Ok(());
        }
        try_arm_auto_attack_for_spell_start(ctx, caster, spell_kind, target_id, now);
        if uses_global_cooldown {
            stamp_global_cooldown_for_duration(ctx, caster, definition.global_cooldown, now);
        }
        stamp_cooldown(ctx, caster, spell_kind, now);
        return Ok(());
    }

    let derived_stats = derived_combat_stats_for_owner(ctx, caster);
    let temporary_modifiers = temporary_combat_modifiers(ctx, now);
    let cast_time = scale_cast_duration(
        definition.cast_time,
        derived_stats.cast_speed_multiplier
            * temporary_modifiers.cast_speed_multiplier_for(&caster),
    );
    if cast_time > Duration::ZERO {
        let can_start = process_spell_cast(
            ctx,
            &cast_state,
            caster,
            spell_kind,
            target_id,
            aim_x,
            aim_y,
            aim_z,
            CastExecutionMode::ValidateOnly,
            1,
            1.0,
            "",
            "",
        )?;
        if !can_start {
            record_spell_prediction_result(
                ctx,
                caster,
                "",
                predicted_cast_id.as_str(),
                client_action_seq,
                SPELL_PREDICTION_RESULT_REJECTED,
                now,
            );
            return Ok(());
        }
        mark_harmful_targeted_spell_start(ctx, caster, spell_kind, target_id, now);
        clear_interruptible_defense_for_owner(ctx, caster);
        let active_cast = begin_active_cast(
            ctx,
            caster,
            spell_kind,
            target_id,
            aim_x,
            aim_y,
            aim_z,
            cast_started_at,
            cast_input_tick,
            Some(cast_time),
            predicted_cast_id.as_str(),
            client_action_seq,
        );
        record_cast_prediction_correlation(
            ctx,
            caster,
            active_cast.cast_id.as_str(),
            predicted_cast_id.as_str(),
            client_action_seq,
            now,
        );
        record_spell_prediction_result(
            ctx,
            caster,
            active_cast.cast_id.as_str(),
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_ACCEPTED,
            now,
        );
        // begin_active_cast already emitted public COMBAT_CAST. If a post-accept
        // server finalization step fails, emit public fizzle for presentation/VFX
        // cleanup and owner-only rejected result for local reconciliation.
        if !finalize_primary_resource_on_cast_start(ctx, caster, spell_kind, now) {
            fizzle_active_cast_row_for_interrupt(ctx, &active_cast, &caster_state, spell_kind, now);
            record_spell_prediction_result(
                ctx,
                caster,
                active_cast.cast_id.as_str(),
                predicted_cast_id.as_str(),
                client_action_seq,
                SPELL_PREDICTION_RESULT_REJECTED,
                now,
            );
            return Ok(());
        }
        try_arm_auto_attack_for_spell_start(ctx, caster, spell_kind, target_id, now);
        if uses_global_cooldown {
            stamp_global_cooldown_for_duration(
                ctx,
                caster,
                definition.global_cooldown,
                cast_started_at,
            );
        }
        return Ok(());
    }

    let can_start = process_spell_cast(
        ctx,
        &cast_state,
        caster,
        spell_kind,
        target_id,
        aim_x,
        aim_y,
        aim_z,
        CastExecutionMode::ValidateOnly,
        1,
        1.0,
        "",
        "",
    )?;
    if !can_start {
        record_spell_prediction_result(
            ctx,
            caster,
            "",
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            now,
        );
        return Ok(());
    }
    let action_instance_id = next_spell_instance_id(ctx, caster);
    let ability_id = ability_id_for_spell(ctx, caster, spell_kind);
    let final_can_start = process_spell_cast(
        ctx,
        &cast_state,
        caster,
        spell_kind,
        target_id,
        aim_x,
        aim_y,
        aim_z,
        CastExecutionMode::FinalValidate,
        1,
        1.0,
        action_instance_id.as_str(),
        ability_id.as_str(),
    )?;
    if !final_can_start {
        record_spell_prediction_result(
            ctx,
            caster,
            action_instance_id.as_str(),
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            now,
        );
        return Ok(());
    }
    if !finalize_primary_resource_on_cast_start(ctx, caster, spell_kind, now) {
        record_spell_prediction_result(
            ctx,
            caster,
            action_instance_id.as_str(),
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            now,
        );
        return Ok(());
    }
    mark_harmful_targeted_spell_start(ctx, caster, spell_kind, target_id, now);
    clear_interruptible_defense_for_owner(ctx, caster);
    let cast_succeeded = process_spell_cast(
        ctx,
        &cast_state,
        caster,
        spell_kind,
        target_id,
        aim_x,
        aim_y,
        aim_z,
        CastExecutionMode::Execute,
        1,
        1.0,
        action_instance_id.as_str(),
        ability_id.as_str(),
    )?;
    if !cast_succeeded {
        record_spell_prediction_result(
            ctx,
            caster,
            action_instance_id.as_str(),
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            now,
        );
        return Ok(());
    }
    emit_spell_cast_accepted_event(
        ctx,
        caster,
        spell_kind,
        action_instance_id.as_str(),
        ability_id.as_str(),
        now,
    );
    record_spell_prediction_result(
        ctx,
        caster,
        action_instance_id.as_str(),
        predicted_cast_id.as_str(),
        client_action_seq,
        SPELL_PREDICTION_RESULT_ACCEPTED,
        now,
    );
    try_arm_auto_attack_for_spell_start(ctx, caster, spell_kind, target_id, now);

    if uses_global_cooldown {
        stamp_global_cooldown_for_duration(ctx, caster, definition.global_cooldown, now);
    }
    stamp_cooldown(ctx, caster, spell_kind, now);
    Ok(())
}

fn finalize_primary_resource_on_cast_start(
    ctx: &ReducerContext,
    caster: Identity,
    spell_kind: &SpellId,
    now: Timestamp,
) -> bool {
    let Some(cost) = resolved_primary_resource_cost_for_action(ctx, caster, spell_kind) else {
        return false;
    };
    if !pay_action_resource_cost(ctx, caster, &cost, now) {
        return false;
    }
    grant_primary_resource_amount(
        ctx,
        caster,
        super::catalog::spell_definition(spell_kind)
            .expect("validated spell id must resolve to a definition")
            .primary_resource_gain_on_cast,
        now,
    );
    true
}

fn try_arm_auto_attack_for_spell_start(
    ctx: &ReducerContext,
    caster: Identity,
    spell_kind: &SpellId,
    target_id: &str,
    now: Timestamp,
) {
    let Some(definition) = super::catalog::spell_definition(spell_kind) else {
        return;
    };
    if !definition.arms_auto_attack_on_cast {
        return;
    }
    let Some(target) = resolve_target(ctx, caster, target_id) else {
        return;
    };
    arm_auto_attack_if_unarmed_with_cadence(ctx, caster, target.player_id, now);
}

fn mark_harmful_targeted_spell_start(
    ctx: &ReducerContext,
    caster: Identity,
    spell_kind: &SpellId,
    target_id: &str,
    now: Timestamp,
) {
    let Some(definition) = super::catalog::spell_definition(spell_kind) else {
        return;
    };
    if !definition.requires_target || definition.damage <= 0 {
        return;
    }
    let Some(target) = resolve_target(ctx, caster, target_id) else {
        return;
    };
    if !target_audience_allows(ctx, caster, target.player_id, definition.target_audience) {
        return;
    }

    mark_harmful_combat_action(ctx, caster, target.player_id, now, spell_kind.as_str());
}

pub(crate) fn special_movement_uses_air_path(
    ctx: &ReducerContext,
    owner: Identity,
    authoritative: &PlayerSnapshot,
    cast_state: &PlayerSnapshot,
) -> bool {
    let arena_seed = arena_seed_for_identity(ctx, owner);
    let flat_ground_only = uses_flat_training_collision(ctx, owner);
    let open_world_scene_name = open_world_scene_name_for_identity(ctx, owner);
    let cast_state_ground_y = surface_height_for_world_at_y_with_layout_for_scene(
        arena_seed,
        flat_ground_only,
        Some(open_world_scene_name.as_str()),
        cast_state.pos_x,
        cast_state.pos_z,
        cast_state.pos_y,
    );

    special_movement_uses_air_path_with_ground(authoritative, cast_state, cast_state_ground_y)
}

pub(crate) fn special_movement_uses_air_path_with_ground(
    authoritative: &PlayerSnapshot,
    cast_state: &PlayerSnapshot,
    cast_state_ground_y: f32,
) -> bool {
    if !authoritative.grounded {
        return true;
    }

    // If the accepted action frame is meaningfully above terrain at its own
    // position, treat the special movement as airborne even if the jump has not
    // fully propagated through the authoritative physics tick yet.
    cast_state.pos_y > cast_state_ground_y + SPECIAL_MOVEMENT_AIR_PATH_GROUND_CLEARANCE
}

#[allow(clippy::too_many_arguments)]
pub(super) fn resolve_blockable_spell_hit(
    ctx: &ReducerContext,
    spell_id: &str,
    ability_id: &str,
    kind: &SpellId,
    caster: Identity,
    target: &PlayerSnapshot,
    source_x: f32,
    source_y: f32,
    source_z: f32,
    dir_x: f32,
    dir_y: f32,
    dir_z: f32,
    speed: f32,
    max_distance: f32,
    point_x: f32,
    point_y: f32,
    point_z: f32,
    damage: i32,
    block_behavior: &str,
    now: Timestamp,
) -> bool {
    resolve_spell_combat_hit_defense(
        ctx,
        spell_id,
        ability_id,
        kind,
        caster,
        target,
        source_x,
        source_y,
        source_z,
        dir_x,
        dir_y,
        dir_z,
        speed,
        max_distance,
        point_x,
        point_y,
        point_z,
        damage,
        "UNPARRYABLE",
        block_behavior,
        now,
    )
}

#[allow(clippy::too_many_arguments)]
pub(super) fn resolve_spell_combat_hit_defense(
    ctx: &ReducerContext,
    spell_id: &str,
    ability_id: &str,
    kind: &SpellId,
    caster: Identity,
    target: &PlayerSnapshot,
    source_x: f32,
    source_y: f32,
    source_z: f32,
    dir_x: f32,
    dir_y: f32,
    dir_z: f32,
    speed: f32,
    max_distance: f32,
    point_x: f32,
    point_y: f32,
    point_z: f32,
    _damage: i32,
    parry_behavior: &str,
    block_behavior: &str,
    now: Timestamp,
) -> bool {
    match resolve_defensible_combat_hit(
        ctx,
        DefensibleCombatHit {
            delivery_kind: CombatHitDeliveryKind::Spell,
            defender: target.player_id,
            active_from: now,
            active_until: now + Duration::from_millis(1),
            parry_behavior,
            block_behavior,
            source_x,
            source_y,
            source_z,
            impact_x: point_x,
            impact_y: point_y,
            impact_z: point_z,
            dir_x,
            dir_y,
            dir_z,
            speed,
        },
    ) {
        DefenseResolution::Blocked => {
            mark_harmful_combat_action(ctx, caster, target.player_id, now, kind.as_str());
            let (block_point_x, block_point_y, block_point_z) = resolve_spell_block_impact_point(
                ctx, target, source_x, source_z, point_x, point_y, point_z,
            );
            emit_spell_combat_event(
                ctx,
                SpellCombatEventPayload {
                    action_instance_id: spell_id,
                    ability_id,
                    kind,
                    event_type: crate::combat::COMBAT_EVENT_BLOCK,
                    caster,
                    hit: target.player_id,
                    origin: Vec3::new(source_x, source_y, source_z),
                    direction: Vec3::new(dir_x, dir_y, dir_z),
                    speed,
                    max_distance,
                    scalar: SpellCombatEventScalar::None,
                    sequence_index: 0,
                    sequence_count: 1,
                    point: Vec3::new(block_point_x, block_point_y, block_point_z),
                    now,
                },
            );
            true
        }
        DefenseResolution::Parried => {
            mark_harmful_combat_action(ctx, caster, target.player_id, now, kind.as_str());
            emit_spell_combat_event(
                ctx,
                SpellCombatEventPayload {
                    action_instance_id: spell_id,
                    ability_id,
                    kind,
                    event_type: crate::combat::COMBAT_EVENT_PARRY,
                    caster,
                    hit: target.player_id,
                    origin: Vec3::new(source_x, source_y, source_z),
                    direction: Vec3::new(dir_x, dir_y, dir_z),
                    speed,
                    max_distance,
                    scalar: SpellCombatEventScalar::None,
                    sequence_index: 0,
                    sequence_count: 1,
                    point: Vec3::new(point_x, point_y, point_z),
                    now,
                },
            );
            true
        }
        DefenseResolution::None => false,
    }
}

fn resolve_spell_block_impact_point(
    ctx: &ReducerContext,
    target: &PlayerSnapshot,
    source_x: f32,
    source_z: f32,
    fallback_x: f32,
    fallback_y: f32,
    fallback_z: f32,
) -> (f32, f32, f32) {
    let yaw = ctx
        .db
        .player_intent()
        .identity()
        .find(target.player_id)
        .map(|intent| intent.yaw)
        .unwrap_or(target.facing_yaw);
    let forward_x = yaw.sin();
    let forward_z = yaw.cos();

    let to_source_x = source_x - target.pos_x;
    let to_source_z = source_z - target.pos_z;
    let to_source_len_sq = to_source_x * to_source_x + to_source_z * to_source_z;
    if to_source_len_sq > 0.0001 {
        let inv_len = 1.0 / to_source_len_sq.sqrt();
        let dot = forward_x * (to_source_x * inv_len) + forward_z * (to_source_z * inv_len);
        if dot <= 0.0 {
            return (fallback_x, fallback_y, fallback_z);
        }
    }

    let forward_offset = target.hit_radius.max(0.1) + SPELL_BLOCK_IMPACT_FORWARD_PADDING;
    (
        target.pos_x + forward_x * forward_offset,
        target.pos_y + target.hit_height * SPELL_BLOCK_IMPACT_HEIGHT_SCALE,
        target.pos_z + forward_z * forward_offset,
    )
}

fn cast_prediction_expiry_micros(now: Timestamp) -> i64 {
    timestamp_to_micros(now + CAST_PREDICTION_ROW_RETENTION)
}

fn pending_cast_cancel_key(caster: Identity, predicted_cast_id: &str) -> String {
    format!("{}:{}", caster.to_hex(), predicted_cast_id)
}

fn valid_predicted_cast_id(predicted_cast_id: &str) -> bool {
    predicted_cast_id.is_empty()
        || (predicted_cast_id.len() <= MAX_PREDICTED_CAST_ID_LEN
            && predicted_cast_id
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_' || byte == b'-'))
}

fn valid_cast_action_token(predicted_cast_id: &str, client_action_seq: u64) -> bool {
    valid_predicted_cast_id(predicted_cast_id)
        && ((!predicted_cast_id.is_empty() && client_action_seq > 0)
            || (predicted_cast_id.is_empty() && client_action_seq == 0))
}

fn delete_pending_cast_cancel(ctx: &ReducerContext, caster: Identity, predicted_cast_id: &str) {
    if predicted_cast_id.is_empty() {
        return;
    }

    ctx.db
        .pending_cast_cancel()
        .cancel_key()
        .delete(pending_cast_cancel_key(caster, predicted_cast_id));
}

fn prune_cast_prediction_rows(ctx: &ReducerContext, now: Timestamp) {
    let now_micros = timestamp_to_micros(now);
    let expired_cancel_keys: Vec<String> = ctx
        .db
        .pending_cast_cancel()
        .iter()
        .filter(|row| row.expires_at_micros <= now_micros)
        .map(|row| row.cancel_key)
        .collect();
    for key in expired_cancel_keys {
        ctx.db.pending_cast_cancel().cancel_key().delete(key);
    }

    let expired_correlation_casters: Vec<Identity> = ctx
        .db
        .cast_prediction_correlation()
        .iter()
        .filter(|row| row.expires_at_micros <= now_micros)
        .map(|row| row.caster)
        .collect();
    for caster in expired_correlation_casters {
        ctx.db.cast_prediction_correlation().caster().delete(caster);
    }
}

fn record_spell_prediction_result(
    ctx: &ReducerContext,
    owner: Identity,
    action_instance_id: &str,
    predicted_cast_id: &str,
    client_action_seq: u64,
    result: &str,
    now: Timestamp,
) {
    if predicted_cast_id.is_empty()
        || !valid_cast_action_token(predicted_cast_id, client_action_seq)
    {
        return;
    }

    if let Some(token) =
        ActionPredictionToken::new(predicted_cast_id.to_string(), client_action_seq)
    {
        insert_predicted_action_result(
            ctx,
            owner,
            PredictedActionFamily::SpellCast,
            &token,
            action_instance_id,
            spell_predicted_action_result_kind(result),
            now,
        );
    }
}

fn spell_predicted_action_result_kind(result: &str) -> ActionResultKind {
    match result {
        SPELL_PREDICTION_RESULT_ACCEPTED => ActionResultKind::Accepted,
        SPELL_PREDICTION_RESULT_CANCELED => ActionResultKind::Canceled,
        SPELL_PREDICTION_RESULT_CANCEL_TOO_LATE => ActionResultKind::CancelTooLate,
        SPELL_PREDICTION_RESULT_STALE_TOKEN => ActionResultKind::StaleToken,
        _ => ActionResultKind::Rejected,
    }
}

fn has_matching_pending_cancel(
    ctx: &ReducerContext,
    caster: Identity,
    predicted_cast_id: &str,
    client_action_seq: u64,
) -> bool {
    if !valid_cast_action_token(predicted_cast_id, client_action_seq) {
        return false;
    }
    if predicted_cast_id.is_empty() {
        return false;
    }

    let key = pending_cast_cancel_key(caster, predicted_cast_id);
    ctx.db
        .pending_cast_cancel()
        .cancel_key()
        .find(key)
        .is_some_and(|row| row.client_action_seq == client_action_seq)
}

fn record_pending_cast_cancel(
    ctx: &ReducerContext,
    caster: Identity,
    predicted_cast_id: &str,
    client_action_seq: u64,
    reason: &str,
    now: Timestamp,
) {
    if predicted_cast_id.is_empty()
        || !valid_cast_action_token(predicted_cast_id, client_action_seq)
    {
        return;
    }

    let row = PendingCastCancel {
        cancel_key: pending_cast_cancel_key(caster, predicted_cast_id),
        caster,
        predicted_cast_id: predicted_cast_id.to_string(),
        client_action_seq,
        reason: reason.to_string(),
        received_at: now,
        expires_at_micros: cast_prediction_expiry_micros(now),
    };
    if ctx
        .db
        .pending_cast_cancel()
        .cancel_key()
        .find(row.cancel_key.clone())
        .is_some()
    {
        ctx.db.pending_cast_cancel().cancel_key().update(row);
    } else {
        ctx.db.pending_cast_cancel().insert(row);
    }
}

fn record_cast_prediction_correlation(
    ctx: &ReducerContext,
    caster: Identity,
    action_instance_id: &str,
    predicted_cast_id: &str,
    client_action_seq: u64,
    now: Timestamp,
) {
    if predicted_cast_id.is_empty()
        || !valid_cast_action_token(predicted_cast_id, client_action_seq)
    {
        return;
    }

    let row = CastPredictionCorrelation {
        caster,
        action_instance_id: action_instance_id.to_string(),
        predicted_cast_id: predicted_cast_id.to_string(),
        client_action_seq,
        received_at: now,
        expires_at_micros: cast_prediction_expiry_micros(now),
    };
    if ctx
        .db
        .cast_prediction_correlation()
        .caster()
        .find(caster)
        .is_some()
    {
        ctx.db.cast_prediction_correlation().caster().update(row);
    } else {
        ctx.db.cast_prediction_correlation().insert(row);
    }
}

fn active_cast_matches_client_action(
    ctx: &ReducerContext,
    active_cast: &ActiveCast,
    predicted_cast_id: &str,
    client_action_seq: u64,
) -> bool {
    if predicted_cast_id.is_empty() && client_action_seq == 0 {
        // Legacy path for casts that were already authoritative before the
        // local client had a prediction token, such as reconnect/cross-session
        // cleanup. New local input must send a non-empty predicted cast id.
        return true;
    }
    if !valid_cast_action_token(predicted_cast_id, client_action_seq) {
        return false;
    }

    let Some(correlation) = ctx
        .db
        .cast_prediction_correlation()
        .caster()
        .find(active_cast.caster)
    else {
        return false;
    };

    correlation.action_instance_id == active_cast.cast_id
        && correlation.predicted_cast_id == predicted_cast_id
        && correlation.client_action_seq == client_action_seq
}

fn active_cast_cancel_receive_window_allows(
    active_cast: &ActiveCast,
    now: Timestamp,
    cancel_observed_remaining_ms: u64,
) -> bool {
    if now <= active_cast.ends_at {
        return true;
    }

    cancel_observed_remaining_ms > 0 && now <= active_cast.ends_at + PRE_END_CANCEL_ACCEPTANCE_GRACE
}

pub(super) fn release_cast(
    ctx: &ReducerContext,
    spell_kind: &SpellId,
    predicted_cast_id: String,
    client_action_seq: u64,
) -> Result<(), String> {
    prune_cast_prediction_rows(ctx, ctx.timestamp);
    if !valid_cast_action_token(predicted_cast_id.as_str(), client_action_seq) {
        return Ok(());
    }
    if !matches!(
        BespokeRuntimeSpell::from_spell_id(spell_kind),
        Some(BespokeRuntimeSpell::InstantBeam | BespokeRuntimeSpell::Electrocute)
    ) {
        return Ok(());
    }

    let Some(active_cast) = ctx.db.active_cast().caster().find(ctx.sender()) else {
        return Ok(());
    };
    if !active_cast_matches_client_action(
        ctx,
        &active_cast,
        predicted_cast_id.as_str(),
        client_action_seq,
    ) {
        return Ok(());
    }
    let Ok(active_kind) = SpellId::new(active_cast.kind.as_str()) else {
        clear_active_cast(ctx, ctx.sender());
        return Ok(());
    };
    let Some(active_definition) = super::catalog::spell_definition(&active_kind) else {
        clear_active_cast(ctx, ctx.sender());
        return Ok(());
    };
    let kind = &active_definition.kind;
    if kind != spell_kind {
        return Ok(());
    }
    let Some(caster_state) = player_snapshot_for(ctx, ctx.sender()) else {
        clear_active_cast(ctx, ctx.sender());
        return Ok(());
    };
    if !caster_state.alive {
        fizzle_active_cast_row_for_interrupt(
            ctx,
            &active_cast,
            &caster_state,
            &active_kind,
            ctx.timestamp,
        );
        return Ok(());
    }

    if BespokeRuntimeSpell::from_spell_id(kind) == Some(BespokeRuntimeSpell::Electrocute) {
        apply_active_cast_terminal_outcome(
            ctx,
            &active_cast,
            &caster_state,
            ctx.timestamp,
            ActiveCastTerminalOutcome::ChannelStop,
        );
        return Ok(());
    }

    finish_active_cast(ctx, &active_cast, &caster_state, kind, ctx.timestamp)
}

pub(super) fn cancel_active_cast_from_input(
    ctx: &ReducerContext,
    predicted_cast_id: String,
    client_action_seq: u64,
    reason: String,
    cancel_observed_remaining_ms: u64,
) -> Result<(), String> {
    let caster = ctx.sender();
    prune_cast_prediction_rows(ctx, ctx.timestamp);
    if !valid_cast_action_token(predicted_cast_id.as_str(), client_action_seq) {
        return Ok(());
    }
    let active_cast = ctx.db.active_cast().caster().find(caster);
    let Some(active_cast) = active_cast else {
        record_pending_cast_cancel(
            ctx,
            caster,
            predicted_cast_id.as_str(),
            client_action_seq,
            reason.as_str(),
            ctx.timestamp,
        );
        return Ok(());
    };
    let active_kind = SpellId::new(active_cast.kind.as_str()).ok();
    if !active_cast_matches_client_action(
        ctx,
        &active_cast,
        predicted_cast_id.as_str(),
        client_action_seq,
    ) {
        record_spell_prediction_result(
            ctx,
            caster,
            active_cast.cast_id.as_str(),
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_STALE_TOKEN,
            ctx.timestamp,
        );
        return Ok(());
    }
    if !active_cast_cancel_receive_window_allows(
        &active_cast,
        ctx.timestamp,
        cancel_observed_remaining_ms,
    ) {
        record_spell_prediction_result(
            ctx,
            caster,
            active_cast.cast_id.as_str(),
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_CANCEL_TOO_LATE,
            ctx.timestamp,
        );
        return Ok(());
    }
    let Some(active_kind) = active_kind else {
        clear_active_cast(ctx, caster);
        return Ok(());
    };
    if movement_delivery_for_action_id(active_kind.as_str()).is_some() {
        record_spell_prediction_result(
            ctx,
            caster,
            active_cast.cast_id.as_str(),
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_REJECTED,
            ctx.timestamp,
        );
        return Ok(());
    }
    let Some(definition) = super::catalog::spell_definition(&active_kind) else {
        clear_active_cast(ctx, caster);
        return Ok(());
    };
    if definition.cast_mobility != super::manifest::SpellCastMobility::GroundedStationary {
        return Ok(());
    }
    let Some(caster_state) = player_snapshot_for(ctx, caster) else {
        clear_active_cast(ctx, caster);
        return Ok(());
    };
    if !caster_state.alive {
        fizzle_active_cast_row_for_interrupt(
            ctx,
            &active_cast,
            &caster_state,
            &active_kind,
            ctx.timestamp,
        );
        return Ok(());
    }

    let now = ctx.timestamp;
    if BespokeRuntimeSpell::from_spell_id(&active_kind) == Some(BespokeRuntimeSpell::Electrocute) {
        record_spell_prediction_result(
            ctx,
            caster,
            active_cast.cast_id.as_str(),
            predicted_cast_id.as_str(),
            client_action_seq,
            SPELL_PREDICTION_RESULT_CANCELED,
            now,
        );
        delete_pending_cast_cancel(ctx, caster, predicted_cast_id.as_str());
        apply_active_cast_terminal_outcome(
            ctx,
            &active_cast,
            &caster_state,
            now,
            ActiveCastTerminalOutcome::ChannelStop,
        );
        return Ok(());
    }

    let canceled_cast_started_at = active_cast.started_at;
    apply_active_cast_terminal_outcome(
        ctx,
        &active_cast,
        &caster_state,
        now,
        ActiveCastTerminalOutcome::SpellFizzle(definition),
    );
    if normal_cast_time_spell_refunds_gcd_on_self_cancel(definition) {
        clear_global_cooldown_if_matches(ctx, caster, canceled_cast_started_at);
    }
    record_spell_prediction_result(
        ctx,
        caster,
        active_cast.cast_id.as_str(),
        predicted_cast_id.as_str(),
        client_action_seq,
        SPELL_PREDICTION_RESULT_CANCELED,
        now,
    );
    delete_pending_cast_cancel(ctx, caster, predicted_cast_id.as_str());
    Ok(())
}

pub(crate) fn tick_active_casts(ctx: &ReducerContext, now: Timestamp) -> Result<(), String> {
    prune_cast_prediction_rows(ctx, now);
    let active_casts: Vec<ActiveCast> = ctx.db.active_cast().iter().collect();
    for active_cast in active_casts {
        let caster = active_cast.caster;
        let Ok(active_kind) = SpellId::new(active_cast.kind.as_str()) else {
            clear_active_cast(ctx, caster);
            continue;
        };
        let is_movement_delivery_cast =
            movement_delivery_for_action_id(active_kind.as_str()).is_some();
        let definition = if is_movement_delivery_cast {
            None
        } else {
            super::catalog::spell_definition(&active_kind)
        };
        let kind = definition
            .as_ref()
            .map(|definition| &definition.kind)
            .unwrap_or(&active_kind);
        if !is_movement_delivery_cast && definition.is_none() {
            clear_active_cast(ctx, caster);
            continue;
        };

        let Some(caster_state) = player_snapshot_for(ctx, caster) else {
            clear_active_cast(ctx, caster);
            continue;
        };
        if !caster_state.alive {
            fizzle_active_cast_row_for_interrupt(
                ctx,
                &active_cast,
                &caster_state,
                &active_kind,
                now,
            );
            continue;
        }
        if has_active_disabling_status(ctx, caster, now)
            && !definition.is_some_and(spell_can_be_cast_while_disabled)
        {
            fizzle_active_cast_row_for_interrupt(
                ctx,
                &active_cast,
                &caster_state,
                &active_kind,
                now,
            );
            continue;
        }

        if is_movement_delivery_cast {
            if ctx
                .db
                .special_movement_runtime()
                .owner()
                .find(caster)
                .is_none()
            {
                finish_active_cast(ctx, &active_cast, &caster_state, kind, now)?;
            }
            continue;
        }

        let definition = definition.expect("non-movement active cast must resolve to a definition");

        if BespokeRuntimeSpell::from_spell_id(kind) == Some(BespokeRuntimeSpell::Electrocute) {
            if should_cancel_cast_from_movement(ctx, caster) {
                apply_active_cast_terminal_outcome(
                    ctx,
                    &active_cast,
                    &caster_state,
                    now,
                    ActiveCastTerminalOutcome::ChannelStop,
                );
                continue;
            }

            if !tick_electrocute_channel(ctx, &active_cast, &caster_state, now)? {
                continue;
            }

            if now >= active_cast.ends_at {
                apply_active_cast_terminal_outcome(
                    ctx,
                    &active_cast,
                    &caster_state,
                    now,
                    ActiveCastTerminalOutcome::ChannelStop,
                );
            }
            continue;
        }

        if violates_active_cast_lifetime_mobility_requirement(
            ctx,
            caster,
            definition,
            caster_state.grounded,
            &active_cast,
        ) {
            let last_voluntary_move_input_tick = ctx
                .db
                .player_state()
                .player_id()
                .find(caster)
                .map(|state| state.last_voluntary_move_input_tick)
                .unwrap_or(0);
            log::info!(
                "[SPELL_CAST] caster={} spell={} active_cast={} fizzled reason=mobility_requirement grounded={} last_move_tick={} cast_input_tick={}",
                &caster.to_hex()[..8],
                kind.as_str(),
                active_cast.cast_id.as_str(),
                caster_state.grounded,
                last_voluntary_move_input_tick,
                active_cast.cast_authored_input_tick
            );
            if BespokeRuntimeSpell::from_spell_id(kind) == Some(BespokeRuntimeSpell::InstantBeam) {
                finish_active_cast(ctx, &active_cast, &caster_state, kind, now)?;
            } else {
                let canceled_cast_started_at = active_cast.started_at;
                apply_active_cast_terminal_outcome(
                    ctx,
                    &active_cast,
                    &caster_state,
                    now,
                    ActiveCastTerminalOutcome::SpellFizzle(definition),
                );
                if normal_cast_time_spell_refunds_gcd_on_self_cancel(definition) {
                    clear_global_cooldown_if_matches(ctx, caster, canceled_cast_started_at);
                }
            }
            continue;
        }

        if now < active_cast.ends_at {
            continue;
        }

        if BespokeRuntimeSpell::from_spell_id(kind) == Some(BespokeRuntimeSpell::InstantBeam)
            && update_instant_beam_charge_cycle(ctx, &active_cast, now)
        {
            continue;
        }

        finish_active_cast(ctx, &active_cast, &caster_state, kind, now)?;
    }

    Ok(())
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum CastExecutionMode {
    ValidateOnly,
    FinalValidate,
    Execute,
}

fn ability_id_for_spell(ctx: &ReducerContext, caster: Identity, spell_kind: &SpellId) -> String {
    let authored_action_id = AuthoredActionId::new(spell_kind.as_str());
    active_selectable_ability_for_authored_action(ctx, caster, &authored_action_id)
        .map(|ability| ability.ability_id)
        .unwrap_or_default()
}

fn spell_can_be_cast_while_disabled(definition: &SpellDefinition) -> bool {
    definition.behavior == SpellBehavior::RemoveStatus
}

fn process_spell_cast(
    ctx: &ReducerContext,
    state: &PlayerSnapshot,
    caster: Identity,
    spell_kind: &SpellId,
    target_id: &str,
    aim_x: f32,
    aim_y: f32,
    aim_z: f32,
    mode: CastExecutionMode,
    charge_count: u32,
    charge_pct: f32,
    action_instance_id: &str,
    ability_id: &str,
) -> Result<bool, String> {
    let definition = super::catalog::spell_definition(spell_kind)
        .expect("validated spell id must resolve to a definition");

    if matches!(
        definition.behavior,
        SpellBehavior::ApplyStatus | SpellBehavior::RemoveStatus | SpellBehavior::SelfResource
    ) {
        if mode == CastExecutionMode::Execute {
            match definition.behavior {
                SpellBehavior::ApplyStatus => {
                    return cast_apply_status(
                        ctx,
                        caster,
                        state,
                        spell_kind,
                        target_id,
                        mode,
                        action_instance_id,
                        ability_id,
                    );
                }
                SpellBehavior::RemoveStatus => {
                    cast_remove_status(
                        ctx,
                        caster,
                        state,
                        spell_kind,
                        action_instance_id,
                        ability_id,
                    );
                }
                SpellBehavior::SelfResource => {
                    cast_self_resource(
                        ctx,
                        caster,
                        state,
                        spell_kind,
                        action_instance_id,
                        ability_id,
                    );
                }
                _ => unreachable!(),
            }
        }
        if definition.behavior == SpellBehavior::ApplyStatus {
            return cast_apply_status(ctx, caster, state, spell_kind, target_id, mode, "", "");
        }
        return Ok(true);
    }

    if definition.behavior == SpellBehavior::Projectile {
        if projectile_motion(definition) == Some("ORBIT_CASTER") {
            if mode == CastExecutionMode::Execute {
                spawn_orbit_projectiles(
                    ctx,
                    caster,
                    state,
                    spell_kind,
                    action_instance_id,
                    ability_id,
                )?;
            }
            return Ok(true);
        }

        let Some(target) = resolve_target(ctx, state.player_id, target_id) else {
            log_cast_rejected(
                caster,
                spell_kind,
                "invalid_target",
                &format!("mode={mode:?} target_id={target_id}"),
            );
            return Ok(false);
        };
        if !target_audience_allows(ctx, caster, target.player_id, definition.target_audience) {
            log_cast_rejected(
                caster,
                spell_kind,
                "invalid_target_audience",
                &format!("mode={mode:?} target={}", &target.player_id.to_hex()[..8]),
            );
            return Ok(false);
        }
        let target_is_in_facing_arc = if projectile_execute_uses_live_facing(mode, definition) {
            is_target_within_live_facing_arc(ctx, state, caster, &target, TARGET_FACING_ARC_RADIANS)
        } else {
            is_target_within_facing_arc(state, &target, TARGET_FACING_ARC_RADIANS)
        };
        if !target_is_in_facing_arc {
            log_cast_rejected(
                caster,
                spell_kind,
                "target_facing_required",
                &format!(
                    "mode={mode:?} target={} caster=({:.2},{:.2}) target=({:.2},{:.2}) yaw={:.2}",
                    &target.player_id.to_hex()[..8],
                    state.pos_x,
                    state.pos_z,
                    target.pos_x,
                    target.pos_z,
                    state.facing_yaw
                ),
            );
            return Ok(false);
        }
        if !has_line_of_sight(ctx, state, &target) {
            if let Some(blocker) = line_of_sight_blocker(ctx, state, &target) {
                log_cast_rejected(
                    caster,
                    spell_kind,
                    "line_of_sight_blocked",
                    &format!(
                        "mode={mode:?} target={} caster=({:.2},{:.2},{:.2}) target=({:.2},{:.2},{:.2}) hit=({:.2},{:.2},{:.2}) hit_t={:.2} target_probe=({:.2},{:.2},{:.2})",
                        &target.player_id.to_hex()[..8],
                        state.pos_x,
                        state.pos_y,
                        state.pos_z,
                        target.pos_x,
                        target.pos_y,
                        target.pos_z,
                        blocker.hit.x,
                        blocker.hit.y,
                        blocker.hit.z,
                        blocker.hit.t,
                        blocker.target_x,
                        blocker.target_y,
                        blocker.target_z
                    ),
                );
            } else {
                log_cast_rejected(
                    caster,
                    spell_kind,
                    "line_of_sight_blocked",
                    &format!(
                        "mode={mode:?} target={} caster=({:.2},{:.2},{:.2}) target=({:.2},{:.2},{:.2})",
                        &target.player_id.to_hex()[..8],
                        state.pos_x,
                        state.pos_y,
                        state.pos_z,
                        target.pos_x,
                        target.pos_y,
                        target.pos_z
                    ),
                );
            }
            return Ok(false);
        }
        if mode == CastExecutionMode::Execute {
            spawn_tracking_projectile(
                ctx,
                caster,
                state,
                &target,
                spell_kind,
                action_instance_id,
                ability_id,
            )?;
        }
        return Ok(true);
    }

    if is_generic_area_spell(spell_kind, definition) {
        if resolve_generic_area_center(definition, state, aim_x, aim_y, aim_z).is_none() {
            return Ok(false);
        }
        if mode == CastExecutionMode::Execute {
            cast_generic_area(
                ctx,
                caster,
                state,
                spell_kind,
                aim_x,
                aim_y,
                aim_z,
                action_instance_id,
                ability_id,
            )?;
        }
        return Ok(true);
    }

    match BespokeRuntimeSpell::from_spell_id(spell_kind) {
        Some(BespokeRuntimeSpell::Meteor) => {
            if mode == CastExecutionMode::Execute {
                spawn_meteor(
                    ctx,
                    caster,
                    state,
                    aim_x,
                    aim_y,
                    aim_z,
                    action_instance_id,
                    ability_id,
                )?;
            }
            Ok(true)
        }
        Some(BespokeRuntimeSpell::InstantBeam) => {
            let Some(target) = resolve_target(ctx, state.player_id, target_id) else {
                return Ok(false);
            };
            if !target_audience_allows(ctx, caster, target.player_id, definition.target_audience) {
                return Ok(false);
            }
            if mode == CastExecutionMode::ValidateOnly
                && !is_target_within_facing_arc(state, &target, TARGET_FACING_ARC_RADIANS)
            {
                return Ok(false);
            }
            if mode != CastExecutionMode::ValidateOnly
                && !is_target_within_live_facing_arc(
                    ctx,
                    state,
                    caster,
                    &target,
                    TARGET_FACING_ARC_RADIANS,
                )
            {
                return Ok(false);
            }
            if mode != CastExecutionMode::Execute && !has_line_of_sight(ctx, state, &target) {
                return Ok(false);
            }
            if mode == CastExecutionMode::Execute {
                spawn_instant_beam(
                    ctx,
                    caster,
                    state,
                    &target,
                    charge_count,
                    charge_pct,
                    action_instance_id,
                    ability_id,
                )?;
            }
            Ok(true)
        }
        Some(BespokeRuntimeSpell::Electrocute) => {
            let Some(target) = resolve_target(ctx, state.player_id, target_id) else {
                return Ok(false);
            };
            if !target_audience_allows(ctx, caster, target.player_id, definition.target_audience) {
                return Ok(false);
            }
            if !is_target_within_live_facing_arc(
                ctx,
                state,
                caster,
                &target,
                TARGET_FACING_ARC_RADIANS,
            ) {
                return Ok(false);
            }
            if !has_line_of_sight(ctx, state, &target) {
                return Ok(false);
            }
            if distance_to_target(state, &target) > definition.max_distance {
                return Ok(false);
            }
            Ok(true)
        }
        Some(BespokeRuntimeSpell::Negate) => {
            if mode == CastExecutionMode::Execute {
                spawn_negate(ctx, caster, state, action_instance_id, ability_id)?;
            }
            Ok(true)
        }
        None => Ok(false),
    }
}

fn projectile_motion(definition: &SpellDefinition) -> Option<&'static str> {
    definition
        .secondary
        .projectile
        .as_ref()
        .map(|projectile| projectile.motion.kind())
}

fn projectile_execute_uses_live_facing(
    mode: CastExecutionMode,
    definition: &SpellDefinition,
) -> bool {
    mode == CastExecutionMode::Execute
        && definition.behavior == SpellBehavior::Projectile
        && definition.cast_time > Duration::ZERO
        && projectile_motion(definition) != Some("ORBIT_CASTER")
}

fn is_generic_area_spell(spell_kind: &SpellId, definition: &SpellDefinition) -> bool {
    definition.behavior == SpellBehavior::Area
        && BespokeRuntimeSpell::from_spell_id(spell_kind).is_none()
}

fn resolve_movement_delivery_impact(
    ctx: &ReducerContext,
    action_instance_id: &str,
    ability_id: &str,
    state: &PlayerSnapshot,
    caster: Identity,
    spell_kind: &SpellId,
    target_id: &str,
    aim_x: f32,
    aim_y: f32,
    aim_z: f32,
) -> Result<bool, String> {
    let target = resolve_target(ctx, state.player_id, target_id);
    if let Some(resolved_target) = target.as_ref() {
        let distance = horizontal_distance_to_target(state, resolved_target);
        let arrival_distance =
            movement_delivery_arrival_distance(spell_kind, state, resolved_target);
        let arrived = movement_delivery_has_arrived(spell_kind, distance, arrival_distance);
        log::warn!(
            "[MOVEMENT_DELIVERY_RESOLVE] kind={} caster={} target={} caster_pos=({:.2},{:.2},{:.2}) target_pos=({:.2},{:.2},{:.2}) final_distance={:.2} arrival_distance={:.2} result={}",
            spell_kind.as_str(),
            caster.to_hex(),
            resolved_target.player_id.to_hex(),
            state.pos_x,
            state.pos_y,
            state.pos_z,
            resolved_target.pos_x,
            resolved_target.pos_y,
            resolved_target.pos_z,
            distance,
            arrival_distance,
            if arrived { "HIT" } else { "FIZZLE" }
        );
        if arrived {
            resolve_movement_delivery_hit(
                ctx,
                action_instance_id,
                ability_id,
                caster,
                spell_kind,
                state,
                resolved_target,
            )?;
        } else {
            emit_movement_delivery_fizzle(
                ctx,
                action_instance_id,
                ability_id,
                spell_kind,
                caster,
                state,
                Some(resolved_target),
                Vec3::new(aim_x, aim_y, aim_z),
            );
        }
    } else {
        log::warn!(
            "[MOVEMENT_DELIVERY_RESOLVE] kind={} caster={} target=<missing> caster_pos=({:.2},{:.2},{:.2}) fallback_point=({:.2},{:.2},{:.2}) result=FIZZLE",
            spell_kind.as_str(),
            caster.to_hex(),
            state.pos_x,
            state.pos_y,
            state.pos_z,
            aim_x,
            aim_y,
            aim_z
        );
        emit_movement_delivery_fizzle(
            ctx,
            action_instance_id,
            ability_id,
            spell_kind,
            caster,
            state,
            None,
            Vec3::new(aim_x, aim_y, aim_z),
        );
    }
    Ok(true)
}

pub(crate) fn begin_active_cast(
    ctx: &ReducerContext,
    caster: Identity,
    spell_kind: &SpellId,
    target_id: &str,
    aim_x: f32,
    aim_y: f32,
    aim_z: f32,
    now: Timestamp,
    cast_authored_input_tick: u32,
    cast_time: Option<Duration>,
    predicted_cast_id: &str,
    client_action_seq: u64,
) -> ActiveCast {
    let cast_id = next_spell_instance_id(ctx, caster);
    let ability_id = ability_id_for_spell(ctx, caster, spell_kind);
    let ends_at = cast_time.map_or(now, |duration| now + duration);
    let active_cast = ActiveCast {
        caster,
        cast_id: cast_id.clone(),
        ability_id: ability_id.clone(),
        kind: spell_kind.as_str().to_string(),
        target_id: target_id.to_string(),
        aim_x,
        aim_y,
        aim_z,
        started_at: now,
        ends_at,
        cast_authored_input_tick,
        charge_count: 1,
        max_charge_count: if BespokeRuntimeSpell::from_spell_id(spell_kind)
            == Some(BespokeRuntimeSpell::InstantBeam)
        {
            instant_beam_charge_scaling().max_charges
        } else {
            1
        },
        predicted_cast_id: predicted_cast_id.to_string(),
        client_action_seq,
    };

    ctx.db.active_cast().insert(active_cast.clone());
    emit_spell_cast_accepted_event(
        ctx,
        caster,
        spell_kind,
        cast_id.as_str(),
        ability_id.as_str(),
        now,
    );
    active_cast
}

fn emit_spell_cast_accepted_event(
    ctx: &ReducerContext,
    caster: Identity,
    spell_kind: &SpellId,
    action_instance_id: &str,
    ability_id: &str,
    now: Timestamp,
) {
    let origin = player_snapshot_for(ctx, caster)
        .map(|state| Vec3::new(state.pos_x, state.pos_y + state.hit_height, state.pos_z))
        .unwrap_or_else(|| Vec3::new(0.0, 0.0, 0.0));
    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: action_instance_id.to_string(),
        action_kind: spell_kind.as_str().to_string(),
        ability_id: ability_id.to_string(),
        hit_index: -1,
        event_type: EVENT_CAST.to_string(),
        source_kind: "SPELL".to_string(),
        caster,
        hit: Identity::ZERO,
        origin_x: origin.x,
        origin_y: origin.y,
        origin_z: origin.z,
        dir_x: 0.0,
        dir_y: 0.0,
        dir_z: 0.0,
        speed: 0.0,
        max_distance: 0.0,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 1,
        point_x: origin.x,
        point_y: origin.y,
        point_z: origin.z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: 0,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });
}

fn violates_cast_mobility_requirement(
    spell_kind: &SpellId,
    grounded: bool,
    intent: Option<&PlayerIntent>,
) -> bool {
    let Some(definition) = super::catalog::spell_definition(spell_kind) else {
        return true;
    };
    if definition.cast_mobility != super::manifest::SpellCastMobility::GroundedStationary {
        return false;
    }
    if !grounded {
        return true;
    }
    intent.is_some_and(has_movement_intent)
}

fn violates_active_cast_lifetime_mobility_requirement(
    ctx: &ReducerContext,
    caster: Identity,
    definition: &SpellDefinition,
    grounded: bool,
    active_cast: &ActiveCast,
) -> bool {
    if definition.cast_mobility != super::manifest::SpellCastMobility::GroundedStationary {
        return false;
    }
    if !grounded {
        return true;
    }

    ctx.db
        .player_state()
        .player_id()
        .find(caster)
        .is_some_and(|state| {
            violates_active_cast_lifetime_mobility_requirement_for_tick(
                definition,
                grounded,
                state.last_voluntary_move_input_tick,
                active_cast,
            )
        })
}

fn violates_active_cast_lifetime_mobility_requirement_for_tick(
    definition: &SpellDefinition,
    grounded: bool,
    last_voluntary_move_input_tick: u32,
    active_cast: &ActiveCast,
) -> bool {
    if definition.cast_mobility != super::manifest::SpellCastMobility::GroundedStationary {
        return false;
    }
    if !grounded {
        return true;
    }

    has_voluntary_movement_after_cast(last_voluntary_move_input_tick, active_cast)
}

fn has_voluntary_movement_after_cast(
    last_voluntary_move_input_tick: u32,
    active_cast: &ActiveCast,
) -> bool {
    last_voluntary_move_input_tick > active_cast.cast_authored_input_tick
}

pub(crate) fn movement_delivery_arrival_distance(
    spell_kind: &SpellId,
    caster: &PlayerSnapshot,
    target: &PlayerSnapshot,
) -> f32 {
    let movement = movement_delivery_for_action_id(spell_kind.as_str())
        .expect("validated movement action must resolve to movement delivery");
    contact_distance_from_radii(
        caster.hit_radius,
        target.hit_radius,
        movement.arrival_buffer,
    )
}

pub(crate) fn movement_delivery_destination(
    spell_kind: &SpellId,
    caster: &PlayerSnapshot,
    target: &PlayerSnapshot,
) -> (f32, f32, f32) {
    let dx = target.pos_x - caster.pos_x;
    let dz = target.pos_z - caster.pos_z;
    let distance = (dx * dx + dz * dz).sqrt();
    let arrival_distance = movement_delivery_arrival_distance(spell_kind, caster, target);
    let desired_travel = (distance - arrival_distance).max(0.0);
    if desired_travel <= 0.0001 || distance <= 0.0001 {
        return (caster.pos_x, caster.pos_y, caster.pos_z);
    }

    let contact = approach_line_contact_point_xz(
        caster.pos_x,
        caster.pos_z,
        caster.facing_yaw,
        target.pos_x,
        target.pos_z,
        arrival_distance,
    );
    (contact.0, caster.pos_y, contact.1)
}

pub(crate) fn approach_line_contact_point_xz(
    caster_x: f32,
    caster_z: f32,
    caster_yaw: f32,
    target_x: f32,
    target_z: f32,
    contact_distance: f32,
) -> (f32, f32) {
    let caster_to_target_x = target_x - caster_x;
    let caster_to_target_z = target_z - caster_z;
    let caster_to_target_len_sq =
        caster_to_target_x * caster_to_target_x + caster_to_target_z * caster_to_target_z;
    let (approach_x, approach_z) = if caster_to_target_len_sq > 0.0001 {
        let inv_len = 1.0 / caster_to_target_len_sq.sqrt();
        (caster_to_target_x * inv_len, caster_to_target_z * inv_len)
    } else {
        (caster_yaw.sin(), caster_yaw.cos())
    };

    (
        target_x - approach_x * contact_distance,
        target_z - approach_z * contact_distance,
    )
}

pub(crate) fn contact_distance_from_radii(
    caster_hit_radius: f32,
    target_hit_radius: f32,
    arrival_buffer: f32,
) -> f32 {
    (caster_hit_radius.max(0.0) + target_hit_radius.max(0.0) + arrival_buffer.max(0.0)).max(0.1)
}

pub(crate) fn has_arrived_at_contact_distance(
    distance: f32,
    contact_distance: f32,
    arrival_epsilon: f32,
) -> bool {
    distance <= contact_distance.max(0.0) + arrival_epsilon.max(0.0)
}

pub(crate) fn horizontal_movement_duration_ms(
    start_x: f32,
    start_z: f32,
    end_x: f32,
    end_z: f32,
    speed_meters_per_second: f32,
    min_duration_ms: u64,
) -> u64 {
    let dx = end_x - start_x;
    let dz = end_z - start_z;
    let distance = (dx * dx + dz * dz).sqrt();
    ((distance / speed_meters_per_second.max(0.01)) * 1000.0)
        .ceil()
        .max(min_duration_ms as f32) as u64
}

fn uses_flat_training_collision(ctx: &ReducerContext, identity: Identity) -> bool {
    let Some(world) = ctx.db.player_world().identity().find(identity) else {
        return false;
    };

    let Some(instance_id) = world.instance_id else {
        return false;
    };

    is_training_instance(ctx, instance_id)
}

pub(crate) fn bake_linear_special_movement(
    ctx: &ReducerContext,
    owner: Identity,
    start: Vec3,
    intended_end: Vec3,
    hit_radius: f32,
    hit_height: f32,
    collision_policy: &str,
) -> BakedLinearSpecialMovement {
    if collision_policy != SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK
        && !uses_fixed_y_collision_policy(collision_policy)
    {
        return BakedLinearSpecialMovement { end: intended_end };
    }

    let dx = intended_end.x - start.x;
    let dz = intended_end.z - start.z;
    let total_distance = (dx * dx + dz * dz).sqrt();
    if total_distance <= 0.0001 {
        return BakedLinearSpecialMovement { end: intended_end };
    }

    let arena_seed = arena_seed_for_identity(ctx, owner);
    let flat_ground_only = uses_flat_training_collision(ctx, owner);
    let open_world_scene_name = open_world_scene_name_for_identity(ctx, owner);
    let step_length = SPECIAL_MOVEMENT_BAKE_STEP_METERS.max(0.01);
    let steps = (total_distance / step_length).ceil().max(1.0) as u32;
    let dir_x = dx / total_distance;
    let dir_z = dz / total_distance;
    let fixed_y = uses_fixed_y_collision_policy(collision_policy);
    let start_ground_y = surface_height_for_world_at_y_with_layout_for_scene(
        arena_seed,
        flat_ground_only,
        Some(open_world_scene_name.as_str()),
        start.x,
        start.z,
        start.y,
    );
    let start_sampled_y = if fixed_y { start.y } else { start_ground_y };
    let mut current = Vec3::new(
        start.x,
        resolve_special_movement_y(collision_policy, start.y, start_sampled_y, start_ground_y),
        start.z,
    );

    for step in 1..=steps {
        let traveled = (step as f32 * step_length).min(total_distance);
        let requested_x = start.x + dir_x * traveled;
        let requested_z = start.z + dir_z * traveled;
        let sampled_ground_y = surface_height_for_world_at_y_with_layout_for_scene(
            arena_seed,
            flat_ground_only,
            Some(open_world_scene_name.as_str()),
            requested_x,
            requested_z,
            current.y,
        );
        if fixed_y && fixed_y_terrain_blocks_special_movement(start.y, sampled_ground_y) {
            return BakedLinearSpecialMovement { end: current };
        }
        let sampled_y = if fixed_y { start.y } else { sampled_ground_y };
        let requested_y =
            resolve_special_movement_y(collision_policy, start.y, sampled_y, sampled_ground_y);
        let (resolved_x, resolved_z) = resolve_world_horizontal_collision_y_with_layout_for_scene(
            arena_seed,
            flat_ground_only,
            Some(open_world_scene_name.as_str()),
            requested_x,
            requested_z,
            hit_radius,
            hit_height,
            requested_y,
        );
        let resolved_ground_y = surface_height_for_world_at_y_with_layout_for_scene(
            arena_seed,
            flat_ground_only,
            Some(open_world_scene_name.as_str()),
            resolved_x,
            resolved_z,
            requested_y,
        );
        if fixed_y && fixed_y_terrain_blocks_special_movement(start.y, resolved_ground_y) {
            return BakedLinearSpecialMovement { end: current };
        }
        let resolved_y =
            resolve_special_movement_y(collision_policy, start.y, requested_y, resolved_ground_y);
        if (resolved_x - requested_x).abs() > SPECIAL_MOVEMENT_BLOCK_EPSILON
            || (resolved_z - requested_z).abs() > SPECIAL_MOVEMENT_BLOCK_EPSILON
        {
            return BakedLinearSpecialMovement { end: current };
        }

        current = Vec3::new(resolved_x, resolved_y, resolved_z);
    }

    BakedLinearSpecialMovement { end: current }
}

fn fixed_y_terrain_blocks_special_movement(fixed_y: f32, ground_y: f32) -> bool {
    ground_y > fixed_y + SPECIAL_MOVEMENT_FIXED_Y_TERRAIN_EPSILON
}

fn uses_fixed_y_collision_policy(collision_policy: &str) -> bool {
    collision_policy == SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_FIXED_Y
        || collision_policy == SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_KEEP_HEIGHT_LEGACY
}

pub(crate) fn resolve_special_movement_y(
    collision_policy: &str,
    fixed_start_y: f32,
    _sampled_y: f32,
    ground_y: f32,
) -> f32 {
    if uses_fixed_y_collision_policy(collision_policy) {
        fixed_start_y
    } else {
        ground_y
    }
}

fn should_cancel_cast_from_movement(ctx: &ReducerContext, caster: Identity) -> bool {
    let Some(intent) = ctx.db.player_intent().identity().find(caster) else {
        return false;
    };
    has_movement_intent(&intent)
}

fn has_movement_intent(intent: &PlayerIntent) -> bool {
    intent.jump || intent.forward.abs() > 0.0001 || intent.strafe.abs() > 0.0001
}

pub(crate) fn resolve_target(
    ctx: &ReducerContext,
    caster: Identity,
    target_id: &str,
) -> Option<PlayerSnapshot> {
    if target_id.is_empty() {
        return None;
    }
    let Ok(identity) = Identity::from_hex(target_id) else {
        return None;
    };
    if identity == caster {
        return None;
    }
    let target = player_snapshot_for(ctx, identity)?;
    if !target.alive {
        return None;
    }
    if !players_share_world_context(ctx, caster, target.player_id) {
        return None;
    }
    Some(target)
}

pub(crate) fn validate_movement_delivery_target(
    ctx: &ReducerContext,
    spell_kind: &SpellId,
    caster: Identity,
    state: &PlayerSnapshot,
    target_id: &str,
) -> Option<PlayerSnapshot> {
    let target = match resolve_target(ctx, caster, target_id) {
        Some(target) => target,
        None => {
            log::info!(
                "[CHARGE] caster={} spell={} rejected reason=invalid_target target_id={}",
                &caster.to_hex()[..8],
                spell_kind.as_str(),
                target_id
            );
            return None;
        }
    };
    if !is_target_within_facing_arc(state, &target, TARGET_FACING_ARC_RADIANS) {
        log::info!(
            "[CHARGE] caster={} spell={} target={} rejected reason=target_facing_required caster=({:.2},{:.2}) target=({:.2},{:.2}) yaw={:.2}",
            &caster.to_hex()[..8],
            spell_kind.as_str(),
            &target.player_id.to_hex()[..8],
            state.pos_x,
            state.pos_z,
            target.pos_x,
            target.pos_z,
            state.facing_yaw
        );
        return None;
    }
    if !has_line_of_sight(ctx, state, &target) {
        if let Some(blocker) = line_of_sight_blocker(ctx, state, &target) {
            log::info!(
                "[CHARGE] caster={} spell={} target={} rejected reason=line_of_sight_blocked caster=({:.2},{:.2},{:.2}) target=({:.2},{:.2},{:.2}) hit=({:.2},{:.2},{:.2}) hit_t={:.2} target_probe=({:.2},{:.2},{:.2})",
                &caster.to_hex()[..8],
                spell_kind.as_str(),
                &target.player_id.to_hex()[..8],
                state.pos_x,
                state.pos_y,
                state.pos_z,
                target.pos_x,
                target.pos_y,
                target.pos_z,
                blocker.hit.x,
                blocker.hit.y,
                blocker.hit.z,
                blocker.hit.t,
                blocker.target_x,
                blocker.target_y,
                blocker.target_z
            );
        } else {
            log::info!(
                "[CHARGE] caster={} spell={} target={} rejected reason=line_of_sight_blocked caster=({:.2},{:.2},{:.2}) target=({:.2},{:.2},{:.2})",
                &caster.to_hex()[..8],
                spell_kind.as_str(),
                &target.player_id.to_hex()[..8],
                state.pos_x,
                state.pos_y,
                state.pos_z,
                target.pos_x,
                target.pos_y,
                target.pos_z
            );
        }
        return None;
    }
    let distance = horizontal_distance_to_target(state, &target);
    let movement = movement_delivery_for_action_id(spell_kind.as_str())
        .expect("validated movement action must resolve to movement delivery");
    let movement_audience = TargetAudience::from_wire(movement.target_audience.as_str())
        .unwrap_or(TargetAudience::Hostile);
    if !target_audience_allows(ctx, caster, target.player_id, movement_audience) {
        log::info!(
            "[CHARGE] caster={} spell={} target={} rejected reason=invalid_target_audience audience={}",
            &caster.to_hex()[..8],
            spell_kind.as_str(),
            &target.player_id.to_hex()[..8],
            movement.target_audience
        );
        return None;
    }
    if distance > movement.max_distance {
        log::info!(
            "[CHARGE] caster={} spell={} target={} rejected reason=out_of_range dist={:.2} max={:.2}",
            &caster.to_hex()[..8],
            spell_kind.as_str(),
            &target.player_id.to_hex()[..8],
            distance,
            movement.max_distance
        );
        return None;
    }
    Some(target)
}

fn spawn_tracking_projectile(
    ctx: &ReducerContext,
    caster: Identity,
    state: &PlayerSnapshot,
    target: &PlayerSnapshot,
    kind: &SpellId,
    action_instance_id: &str,
    ability_id: &str,
) -> Result<(), String> {
    // Spells emit effects; they do not touch HP directly.
    let now = ctx.timestamp;
    let definition = super::catalog::spell_definition(kind)
        .expect("validated PROJECTILE spell must resolve to a definition");
    let projectile_tunables = definition
        .secondary
        .projectile
        .as_ref()
        .expect("validated PROJECTILE spell must define secondary projectile data");
    let boomerang = projectile_tunables.motion.boomerang().copied();
    let lifetime = boomerang
        .map(|motion| motion.lifetime_seconds)
        .unwrap_or_else(|| definition.max_distance / definition.speed);

    let base_x = state.pos_x;
    let base_y = state.pos_y + definition.spawn_height;
    let base_z = state.pos_z;

    let target_y = target.pos_y + definition.spawn_height;

    let mut dir_x = target.pos_x - base_x;
    let mut dir_y = target_y - base_y;
    let mut dir_z = target.pos_z - base_z;

    let distance_sq = dir_x * dir_x + dir_y * dir_y + dir_z * dir_z;
    if distance_sq > 0.0001 {
        let distance = distance_sq.sqrt();
        let inv_len = 1.0 / distance;
        dir_x *= inv_len;
        dir_y *= inv_len;
        dir_z *= inv_len;
    } else {
        log::info!(
            "[SPELL] {} fallback to facing: caster={} target={} caster_pos=({:.2},{:.2},{:.2}) target_pos=({:.2},{:.2},{:.2})",
            kind.as_str(),
            state.player_id.to_hex(),
            target.player_id.to_hex(),
            state.pos_x,
            state.pos_y,
            state.pos_z,
            target.pos_x,
            target.pos_y,
            target.pos_z
        );
        dir_x = state.facing_yaw.sin();
        dir_y = 0.0;
        dir_z = state.facing_yaw.cos();
    }

    let origin_x = base_x + dir_x * definition.spawn_forward;
    let origin_y = base_y + dir_y * definition.spawn_forward;
    let origin_z = base_z + dir_z * definition.spawn_forward;

    log::debug!(
        "[SPELL] {} cast: caster={} target={} origin=({:.2},{:.2},{:.2}) dir=({:.3},{:.3},{:.3})",
        kind.as_str(),
        state.player_id.to_hex(),
        target.player_id.to_hex(),
        origin_x,
        origin_y,
        origin_z,
        dir_x,
        dir_y,
        dir_z
    );

    let projectile_sequence_index = PROJECTILE_SEQUENCE_INDEX_V1;
    let projectile_instance_id = format!("{action_instance_id}:p{projectile_sequence_index}");
    let projectile_id = crate::progression::projectile_body_vfx_id_for_spell(
        ability_id,
        kind.as_str(),
        projectile_sequence_index,
    )
    .unwrap_or_default();

    let parry_behavior = projectile_tunables.parry_behavior.as_str();
    let motion_kind = projectile_tunables.motion.kind();
    let projectile_max_distance = boomerang
        .map(|motion| definition.speed.max(motion.return_speed) * motion.lifetime_seconds)
        .unwrap_or(definition.max_distance);
    let boomerang_outbound_distance = boomerang
        .map(|motion| motion.outbound_distance)
        .unwrap_or(0.0);
    let boomerang_return_speed = boomerang.map(|motion| motion.return_speed).unwrap_or(0.0);
    let boomerang_hit_cooldown_seconds = boomerang
        .map(|motion| motion.hit_cooldown_seconds)
        .unwrap_or(0.0);
    let boomerang_max_hits_per_target = boomerang
        .map(|motion| motion.max_hits_per_target)
        .unwrap_or(0);

    emit_spell_projectile_release_event(
        ctx,
        action_instance_id,
        projectile_instance_id.as_str(),
        projectile_sequence_index,
        projectile_id.as_str(),
        ability_id,
        kind,
        caster,
        target.player_id,
        Vec3::new(origin_x, origin_y, origin_z),
        Vec3::new(dir_x, dir_y, dir_z),
        definition.speed,
        projectile_max_distance,
        definition.radius,
        motion_kind,
        definition.update_interval,
        0.0,
        0.0,
        0.0,
        0.0,
        0.0,
        false,
        boomerang_outbound_distance,
        boomerang_return_speed,
        Vec3::new(origin_x, origin_y, origin_z),
        now,
    );

    ctx.db
        .active_combat_projectile()
        .insert(ActiveCombatProjectile {
            projectile_instance_id: projectile_instance_id.clone(),
            action_instance_id: action_instance_id.to_string(),
            projectile_sequence_index,
            projectile_id,
            source_kind: "SPELL".to_string(),
            action_kind: kind.as_str().to_string(),
            ability_id: ability_id.to_string(),
            motion_kind: motion_kind.to_string(),
            caster,
            intended_target: target.player_id,
            origin_x,
            origin_y,
            origin_z,
            pos_x: origin_x,
            pos_y: origin_y,
            pos_z: origin_z,
            dir_x,
            dir_y,
            dir_z,
            speed: definition.speed,
            max_distance: projectile_max_distance,
            radius: definition.radius,
            orbit_initial_yaw: 0.0,
            orbit_radius: 0.0,
            orbit_height: 0.0,
            orbit_angular_speed_deg_per_sec: 0.0,
            orbit_phase_offset_deg: 0.0,
            orbit_hit_cooldown_seconds: 0.0,
            orbit_max_hits_per_target: 0,
            boomerang_returning: false,
            boomerang_outbound_distance,
            boomerang_return_speed,
            boomerang_hit_cooldown_seconds,
            boomerang_max_hits_per_target,
            traveled: 0.0,
            age: 0.0,
            lifetime,
            update_accum: 0.0,
            update_interval_seconds: definition.update_interval,
            damage: definition.damage,
            parry_behavior: parry_behavior.to_string(),
            block_behavior: definition.block_behavior.as_str().to_string(),
            grants_primary_resource_on_hit: false,
            hit_index: 0,
            created_at: now,
        });

    Ok(())
}

fn spawn_orbit_projectiles(
    ctx: &ReducerContext,
    caster: Identity,
    state: &PlayerSnapshot,
    kind: &SpellId,
    action_instance_id: &str,
    ability_id: &str,
) -> Result<(), String> {
    let now = ctx.timestamp;
    let definition = super::catalog::spell_definition(kind)
        .expect("validated ORBIT_CASTER spell must resolve to a definition");
    let projectile_tunables = definition
        .secondary
        .projectile
        .as_ref()
        .expect("validated PROJECTILE spell must define secondary projectile data");
    let Some(orbit) = projectile_tunables.motion.orbit().copied() else {
        return Err(format!(
            "{} spawn_orbit_projectiles called for non-orbit projectile motion",
            kind.as_str()
        ));
    };

    for sequence_index in 0..orbit.projectile_count {
        let projectile_instance_id = format!("{action_instance_id}:p{sequence_index}");
        let phase_offset_deg =
            orbit.phase_offset_deg + 360.0 * sequence_index as f32 / orbit.projectile_count as f32;
        let angle = state.facing_yaw + phase_offset_deg.to_radians();
        let origin_x = state.pos_x + angle.sin() * orbit.orbit_radius;
        let origin_y = state.pos_y + orbit.orbit_height;
        let origin_z = state.pos_z + angle.cos() * orbit.orbit_radius;
        let dir_x = angle.cos();
        let dir_z = -angle.sin();
        let projectile_id =
            projectile_body_vfx_id_for_orbit_sequence(ability_id, kind, sequence_index)
                .unwrap_or_default();

        emit_spell_projectile_release_event(
            ctx,
            action_instance_id,
            projectile_instance_id.as_str(),
            sequence_index,
            projectile_id.as_str(),
            ability_id,
            kind,
            caster,
            Identity::ZERO,
            Vec3::new(origin_x, origin_y, origin_z),
            Vec3::new(dir_x, 0.0, dir_z),
            0.0,
            0.0,
            orbit.hit_radius,
            "ORBIT_CASTER",
            definition.update_interval,
            state.facing_yaw,
            orbit.orbit_radius,
            orbit.orbit_height,
            orbit.angular_speed_deg_per_sec,
            phase_offset_deg,
            false,
            0.0,
            0.0,
            Vec3::new(origin_x, origin_y, origin_z),
            now,
        );

        ctx.db
            .active_combat_projectile()
            .insert(ActiveCombatProjectile {
                projectile_instance_id,
                action_instance_id: action_instance_id.to_string(),
                projectile_sequence_index: sequence_index,
                projectile_id,
                source_kind: "SPELL".to_string(),
                action_kind: kind.as_str().to_string(),
                ability_id: ability_id.to_string(),
                motion_kind: "ORBIT_CASTER".to_string(),
                caster,
                intended_target: Identity::ZERO,
                origin_x,
                origin_y,
                origin_z,
                pos_x: origin_x,
                pos_y: origin_y,
                pos_z: origin_z,
                dir_x,
                dir_y: 0.0,
                dir_z,
                speed: 0.0,
                max_distance: 0.0,
                radius: orbit.hit_radius,
                orbit_initial_yaw: state.facing_yaw,
                orbit_radius: orbit.orbit_radius,
                orbit_height: orbit.orbit_height,
                orbit_angular_speed_deg_per_sec: orbit.angular_speed_deg_per_sec,
                orbit_phase_offset_deg: phase_offset_deg,
                orbit_hit_cooldown_seconds: orbit.hit_cooldown_seconds,
                orbit_max_hits_per_target: orbit.max_hits_per_target,
                boomerang_returning: false,
                boomerang_outbound_distance: 0.0,
                boomerang_return_speed: 0.0,
                boomerang_hit_cooldown_seconds: 0.0,
                boomerang_max_hits_per_target: 0,
                traveled: 0.0,
                age: 0.0,
                lifetime: orbit.lifetime_seconds,
                update_accum: 0.0,
                update_interval_seconds: definition.update_interval,
                damage: definition.damage,
                parry_behavior: projectile_tunables.parry_behavior.as_str().to_string(),
                block_behavior: definition.block_behavior.as_str().to_string(),
                grants_primary_resource_on_hit: false,
                hit_index: sequence_index,
                created_at: now,
            });
    }

    Ok(())
}

fn projectile_body_vfx_id_for_orbit_sequence(
    ability_id: &str,
    kind: &SpellId,
    sequence_index: u32,
) -> Option<String> {
    crate::progression::projectile_body_vfx_id_for_spell(ability_id, kind.as_str(), sequence_index)
        .or_else(|| {
            crate::progression::projectile_body_vfx_id_for_spell(ability_id, kind.as_str(), 0)
        })
}

#[allow(clippy::too_many_arguments)]
fn emit_spell_projectile_release_event(
    ctx: &ReducerContext,
    action_instance_id: &str,
    projectile_instance_id: &str,
    projectile_sequence_index: u32,
    projectile_id: &str,
    ability_id: &str,
    kind: &SpellId,
    caster: Identity,
    intended_target: Identity,
    origin: Vec3,
    direction: Vec3,
    speed: f32,
    max_distance: f32,
    radius: f32,
    motion_kind: &str,
    update_interval_seconds: f32,
    orbit_initial_yaw: f32,
    orbit_radius: f32,
    orbit_height: f32,
    orbit_angular_speed_deg_per_sec: f32,
    orbit_phase_offset_deg: f32,
    boomerang_returning: bool,
    boomerang_outbound_distance: f32,
    boomerang_return_speed: f32,
    point: Vec3,
    now: Timestamp,
) {
    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: action_instance_id.to_string(),
        action_kind: kind.as_str().to_string(),
        ability_id: ability_id.to_string(),
        hit_index: -1,
        event_type: EVENT_RELEASE.to_string(),
        source_kind: "SPELL".to_string(),
        caster,
        hit: Identity::ZERO,
        origin_x: origin.x,
        origin_y: origin.y,
        origin_z: origin.z,
        dir_x: direction.x,
        dir_y: direction.y,
        dir_z: direction.z,
        speed,
        max_distance,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: projectile_sequence_index,
        sequence_count: 1,
        point_x: point.x,
        point_y: point.y,
        point_z: point.z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage: 0,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });

    ctx.db
        .projectile_presentation_event()
        .insert(ProjectilePresentationEvent {
            event_id: 0,
            action_instance_id: action_instance_id.to_string(),
            action_kind: kind.as_str().to_string(),
            ability_id: ability_id.to_string(),
            source_kind: "SPELL".to_string(),
            projectile_id: projectile_id.to_string(),
            projectile_instance_id: projectile_instance_id.to_string(),
            hit_index: -1,
            event_type: EVENT_RELEASE.to_string(),
            caster,
            hit: Identity::ZERO,
            intended_target,
            origin_x: origin.x,
            origin_y: origin.y,
            origin_z: origin.z,
            dir_x: direction.x,
            dir_y: direction.y,
            dir_z: direction.z,
            point_x: point.x,
            point_y: point.y,
            point_z: point.z,
            speed,
            max_distance,
            radius,
            motion_kind: motion_kind.to_string(),
            update_interval_seconds,
            orbit_initial_yaw,
            orbit_radius,
            orbit_height,
            orbit_angular_speed_deg_per_sec,
            orbit_phase_offset_deg,
            boomerang_returning,
            boomerang_outbound_distance,
            boomerang_return_speed,
            sequence_index: projectile_sequence_index,
            sequence_count: 1,
            terminal: false,
            created_at: now,
            created_at_micros: timestamp_to_micros(now),
        });
}

fn finish_active_cast(
    ctx: &ReducerContext,
    active_cast: &ActiveCast,
    state: &PlayerSnapshot,
    kind: &SpellId,
    now: Timestamp,
) -> Result<(), String> {
    if let Some(movement) = movement_delivery_for_action_id(kind.as_str()) {
        let cast_succeeded = resolve_movement_delivery_impact(
            ctx,
            active_cast.cast_id.as_str(),
            active_cast.ability_id.as_str(),
            state,
            active_cast.caster,
            kind,
            active_cast.target_id.as_str(),
            active_cast.aim_x,
            active_cast.aim_y,
            active_cast.aim_z,
        )?;
        if cast_succeeded {
            stamp_named_cooldown_for_duration(
                ctx,
                active_cast.caster,
                kind.as_str(),
                Duration::from_millis(movement.cooldown_ms),
                now,
            );
        }
        clear_active_cast(ctx, active_cast.caster);
        return Ok(());
    }

    let definition = super::catalog::spell_definition(kind)
        .expect("active cast kind must resolve to a spell definition");
    let charge_pct = compute_charge_pct(active_cast, now);
    let cast_succeeded = process_spell_cast(
        ctx,
        state,
        active_cast.caster,
        kind,
        active_cast.target_id.as_str(),
        active_cast.aim_x,
        active_cast.aim_y,
        active_cast.aim_z,
        CastExecutionMode::Execute,
        active_cast.charge_count,
        charge_pct,
        active_cast.cast_id.as_str(),
        active_cast.ability_id.as_str(),
    )?;
    if cast_succeeded {
        stamp_cooldown(ctx, active_cast.caster, kind, now);
        clear_active_cast(ctx, active_cast.caster);
    } else {
        apply_active_cast_terminal_outcome(
            ctx,
            active_cast,
            state,
            now,
            ActiveCastTerminalOutcome::SpellFizzle(definition),
        );
    }
    Ok(())
}

fn emit_active_cast_fizzle(
    ctx: &ReducerContext,
    active_cast: &ActiveCast,
    caster_state: &PlayerSnapshot,
    kind: &SpellId,
    definition: &SpellDefinition,
    now: Timestamp,
) {
    let origin = Vec3::new(
        caster_state.pos_x,
        caster_state.pos_y + caster_state.hit_height,
        caster_state.pos_z,
    );
    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: active_cast.cast_id.as_str(),
            ability_id: active_cast.ability_id.as_str(),
            kind,
            event_type: EVENT_FIZZLE,
            caster: active_cast.caster,
            hit: Identity::ZERO,
            origin,
            direction: default_forward_direction(caster_state),
            speed: 0.0,
            max_distance: definition.max_distance,
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: origin,
            now,
        },
    );
}

fn compute_charge_pct(active_cast: &ActiveCast, now: Timestamp) -> f32 {
    let start = active_cast.started_at.to_micros_since_unix_epoch();
    let end = active_cast.ends_at.to_micros_since_unix_epoch();
    let now_micros = now.to_micros_since_unix_epoch();
    if end <= start {
        return 1.0;
    }
    let elapsed = (now_micros - start).max(0).min(end - start);
    (elapsed as f64 / (end - start) as f64).clamp(0.0, 1.0) as f32
}

fn update_instant_beam_charge_cycle(
    ctx: &ReducerContext,
    active_cast: &ActiveCast,
    now: Timestamp,
) -> bool {
    if active_cast.charge_count >= active_cast.max_charge_count {
        return true;
    }

    let cycle_duration = bespoke_spell_definition(BespokeRuntimeSpell::InstantBeam)
        .expect("validated spell catalog must define INSTANT_BEAM")
        .cast_time;
    if cycle_duration <= Duration::ZERO {
        return false;
    }

    let mut next_cast = active_cast.clone();
    while next_cast.charge_count < next_cast.max_charge_count && now >= next_cast.ends_at {
        next_cast.started_at = next_cast.ends_at;
        next_cast.ends_at = next_cast.ends_at + cycle_duration;
        next_cast.charge_count = next_cast.charge_count.saturating_add(1);
    }

    ctx.db.active_cast().caster().update(next_cast);
    true
}

pub(crate) fn clear_active_cast(ctx: &ReducerContext, caster: Identity) {
    ctx.db.channel_cast_runtime().caster().delete(caster);
    ctx.db.special_movement_runtime().owner().delete(caster);
    ctx.db.cast_prediction_correlation().caster().delete(caster);
    ctx.db.active_cast().caster().delete(caster);
}

pub(crate) fn fizzle_active_cast_for_interrupt(
    ctx: &ReducerContext,
    caster: Identity,
    now: Timestamp,
) {
    let Some(active_cast) = ctx.db.active_cast().caster().find(caster) else {
        return;
    };
    let Some(caster_state) = player_snapshot_for(ctx, caster) else {
        clear_active_cast(ctx, caster);
        return;
    };
    let Ok(active_kind) = SpellId::new(active_cast.kind.as_str()) else {
        clear_active_cast(ctx, caster);
        return;
    };

    fizzle_active_cast_row_for_interrupt(ctx, &active_cast, &caster_state, &active_kind, now);
}

fn fizzle_active_cast_row_for_interrupt(
    ctx: &ReducerContext,
    active_cast: &ActiveCast,
    caster_state: &PlayerSnapshot,
    active_kind: &SpellId,
    now: Timestamp,
) {
    apply_active_cast_terminal_outcome(
        ctx,
        active_cast,
        caster_state,
        now,
        active_cast_interrupt_terminal_policy(active_kind),
    );
}

#[derive(Clone, Copy, Debug, PartialEq)]
enum ActiveCastTerminalOutcome {
    SilentClear,
    SpellFizzle(&'static SpellDefinition),
    ChannelStop,
}

// Interrupt policy decides how an already-running active cast terminates when an
// external condition kills it. Other terminal paths construct their outcome
// directly because they already know whether they completed, canceled, or failed.
fn active_cast_interrupt_terminal_policy(active_kind: &SpellId) -> ActiveCastTerminalOutcome {
    if movement_delivery_for_action_id(active_kind.as_str()).is_some() {
        return ActiveCastTerminalOutcome::SilentClear;
    }

    let Some(definition) = super::catalog::spell_definition(active_kind) else {
        return ActiveCastTerminalOutcome::SilentClear;
    };

    if BespokeRuntimeSpell::from_spell_id(&definition.kind)
        == Some(BespokeRuntimeSpell::Electrocute)
    {
        ActiveCastTerminalOutcome::ChannelStop
    } else {
        ActiveCastTerminalOutcome::SpellFizzle(definition)
    }
}

fn normal_cast_time_spell_refunds_gcd_on_self_cancel(definition: &SpellDefinition) -> bool {
    definition.cast_time > Duration::ZERO
        && !matches!(
            definition.behavior,
            SpellBehavior::InstantBeam | SpellBehavior::Channel
        )
}

fn apply_active_cast_terminal_outcome(
    ctx: &ReducerContext,
    active_cast: &ActiveCast,
    caster_state: &PlayerSnapshot,
    now: Timestamp,
    outcome: ActiveCastTerminalOutcome,
) {
    match outcome {
        ActiveCastTerminalOutcome::SilentClear => {
            clear_active_cast(ctx, active_cast.caster);
        }
        ActiveCastTerminalOutcome::SpellFizzle(definition) => {
            emit_active_cast_fizzle(
                ctx,
                active_cast,
                caster_state,
                &definition.kind,
                definition,
                now,
            );
            clear_active_cast(ctx, active_cast.caster);
        }
        ActiveCastTerminalOutcome::ChannelStop => {
            stop_electrocute_channel(ctx, active_cast, caster_state, now, None);
        }
    }
}

#[cfg(feature = "spellcasting_terminal_harness")]
const SPELLCASTING_TERMINAL_HARNESS_CASTER_HEX: &str =
    "0000000000000000000000000000000000000000000000000000000000000c01";
#[cfg(feature = "spellcasting_terminal_harness")]
const SPELLCASTING_TERMINAL_HARNESS_TARGET_HEX: &str =
    "0000000000000000000000000000000000000000000000000000000000000c02";
#[cfg(feature = "spellcasting_terminal_harness")]
const SPELLCASTING_TERMINAL_HARNESS_PREFIX: &str = "spell-terminal-harness-";

#[cfg(feature = "spellcasting_terminal_harness")]
#[reducer]
pub fn run_spellcasting_terminal_harness(ctx: &ReducerContext) -> Result<(), String> {
    let now = ctx.timestamp;
    let caster = harness_identity(SPELLCASTING_TERMINAL_HARNESS_CASTER_HEX)?;
    let target = harness_identity(SPELLCASTING_TERMINAL_HARNESS_TARGET_HEX)?;
    cleanup_spellcasting_terminal_harness(ctx, caster, target);

    // yaw=0 faces +Z in the spell system. The target is on -Z so completion
    // must fail the live-facing check even though the cast had already started.
    upsert_harness_player(ctx, caster, 0.0, 0.0, 0.0, false, now);
    upsert_harness_player(ctx, target, 0.0, -5.0, 0.0, true, now);

    let suffix = timestamp_to_micros(now);

    let live_facing_cast_id = format!("{SPELLCASTING_TERMINAL_HARNESS_PREFIX}live-facing-{suffix}");
    let live_facing_cast =
        insert_harness_active_cast(ctx, caster, target, "ICICLE", &live_facing_cast_id, now);
    let caster_state = player_snapshot_for(ctx, caster)
        .ok_or_else(|| "harness caster snapshot missing".to_string())?;
    finish_active_cast(
        ctx,
        &live_facing_cast,
        &caster_state,
        &SpellId::new("ICICLE").map_err(|err| err.to_string())?,
        now,
    )?;
    assert_harness_active_cast_cleared(ctx, caster, "live-facing reject")?;
    assert_harness_event_count(ctx, live_facing_cast_id.as_str(), EVENT_FIZZLE, 1)?;
    assert_harness_no_projectile(ctx, live_facing_cast_id.as_str())?;

    let interrupt_cast_id = format!("{SPELLCASTING_TERMINAL_HARNESS_PREFIX}interrupt-{suffix}");
    let interrupt_cast =
        insert_harness_active_cast(ctx, caster, target, "ICICLE", &interrupt_cast_id, now);
    stamp_global_cooldown(ctx, caster, interrupt_cast.started_at);
    fizzle_active_cast_for_interrupt(ctx, caster, now);
    assert_harness_active_cast_cleared(ctx, caster, "spell interrupt")?;
    assert_harness_event_count(ctx, interrupt_cast_id.as_str(), EVENT_FIZZLE, 1)?;
    assert_harness_global_cooldown_matches(
        ctx,
        caster,
        interrupt_cast.started_at,
        "external spell interrupt",
    )?;

    let channel_cast_id = format!("{SPELLCASTING_TERMINAL_HARNESS_PREFIX}channel-{suffix}");
    let channel_cast =
        insert_harness_active_cast(ctx, caster, target, "ELECTROCUTE", &channel_cast_id, now);
    stamp_global_cooldown(ctx, caster, channel_cast.started_at);
    fizzle_active_cast_for_interrupt(ctx, caster, now);
    assert_harness_active_cast_cleared(ctx, caster, "channel interrupt")?;
    // Electrocute channel-stop currently publishes the same terminal fizzle
    // event type consumed by VFX cleanup.
    assert_harness_event_count(ctx, channel_cast_id.as_str(), EVENT_FIZZLE, 1)?;
    assert_harness_global_cooldown_matches(
        ctx,
        caster,
        channel_cast.started_at,
        "channel interrupt",
    )?;

    let mobility_cancel_cast_id =
        format!("{SPELLCASTING_TERMINAL_HARNESS_PREFIX}mobility-cancel-{suffix}");
    let mobility_cancel_cast =
        insert_harness_active_cast(ctx, caster, target, "ICICLE", &mobility_cancel_cast_id, now);
    stamp_global_cooldown(ctx, caster, mobility_cancel_cast.started_at);
    upsert_harness_player_intent(ctx, caster, 1.0, 0.0, false, now);
    tick_active_casts(ctx, now)?;
    assert_harness_active_cast_cleared(ctx, caster, "mobility cancel")?;
    assert_harness_event_count(ctx, mobility_cancel_cast_id.as_str(), EVENT_FIZZLE, 1)?;
    assert_harness_global_cooldown_absent(ctx, caster, "mobility self-cancel")?;

    cleanup_spellcasting_terminal_harness(ctx, caster, target);
    Ok(())
}

#[cfg(feature = "spellcasting_terminal_harness")]
fn harness_identity(hex: &str) -> Result<Identity, String> {
    Identity::from_hex(hex).map_err(|err| format!("invalid spellcasting harness identity: {err}"))
}

#[cfg(feature = "spellcasting_terminal_harness")]
fn upsert_harness_player(
    ctx: &ReducerContext,
    identity: Identity,
    pos_x: f32,
    pos_z: f32,
    yaw: f32,
    is_dummy: bool,
    now: Timestamp,
) {
    let mut state = new_player_state(identity, now);
    state.is_dummy = is_dummy;
    state.alive = true;
    state.eliminated = false;
    state.hp = state.max_hp.max(100);
    state.max_hp = state.max_hp.max(100);
    if ctx.db.player_state().player_id().find(identity).is_some() {
        ctx.db.player_state().player_id().update(state);
    } else {
        ctx.db.player_state().insert(state);
    }

    let physics = PlayerPhysics {
        identity,
        pos_x,
        pos_y: 0.0,
        pos_z,
        vel_x: 0.0,
        vel_y: 0.0,
        vel_z: 0.0,
        yaw,
        grounded: true,
        last_processed_tick: 0,
        updated_at: now,
    };
    if ctx.db.player_physics().identity().find(identity).is_some() {
        commit_player_physics(
            ctx,
            physics,
            PhysicsWriteMode::Force,
            "spellcasting_terminal_harness",
        );
    } else {
        ctx.db.player_physics().insert(physics);
    }
    upsert_player_world(ctx, identity, "OPEN", None);
}

#[cfg(feature = "spellcasting_terminal_harness")]
fn upsert_harness_player_intent(
    ctx: &ReducerContext,
    identity: Identity,
    forward: f32,
    strafe: f32,
    jump: bool,
    now: Timestamp,
) {
    let intent = PlayerIntent {
        identity,
        forward,
        strafe,
        yaw: 0.0,
        jump,
        input_tick: 0,
        updated_at: now,
    };
    if ctx.db.player_intent().identity().find(identity).is_some() {
        ctx.db.player_intent().identity().update(intent);
    } else {
        ctx.db.player_intent().insert(intent);
    }
}

#[cfg(feature = "spellcasting_terminal_harness")]
fn insert_harness_active_cast(
    ctx: &ReducerContext,
    caster: Identity,
    target: Identity,
    kind: &str,
    cast_id: &str,
    now: Timestamp,
) -> ActiveCast {
    ctx.db.active_cast().caster().delete(caster);
    let active_cast = ActiveCast {
        caster,
        cast_id: cast_id.to_string(),
        ability_id: kind.to_string(),
        kind: kind.to_string(),
        target_id: target.to_hex().to_string(),
        aim_x: 0.0,
        aim_y: 0.0,
        aim_z: 0.0,
        started_at: now - Duration::from_millis(1000),
        ends_at: now,
        cast_authored_input_tick: 0,
        charge_count: 1,
        max_charge_count: 1,
        predicted_cast_id: String::new(),
        client_action_seq: 0,
    };
    ctx.db.active_cast().insert(active_cast.clone());
    active_cast
}

#[cfg(feature = "spellcasting_terminal_harness")]
fn assert_harness_global_cooldown_matches(
    ctx: &ReducerContext,
    caster: Identity,
    expected_started_at: Timestamp,
    label: &str,
) -> Result<(), String> {
    let Some(gcd) = ctx.db.global_cooldown().caster().find(caster) else {
        return Err(format!("{label}: expected global cooldown row"));
    };
    if gcd.started_at == expected_started_at {
        return Ok(());
    }
    Err(format!(
        "{label}: global cooldown started_at mismatch expected={} actual={}",
        timestamp_to_micros(expected_started_at),
        timestamp_to_micros(gcd.started_at)
    ))
}

#[cfg(feature = "spellcasting_terminal_harness")]
fn assert_harness_global_cooldown_absent(
    ctx: &ReducerContext,
    caster: Identity,
    label: &str,
) -> Result<(), String> {
    if ctx.db.global_cooldown().caster().find(caster).is_none() {
        return Ok(());
    }
    Err(format!(
        "{label}: expected global cooldown row to be absent"
    ))
}

#[cfg(feature = "spellcasting_terminal_harness")]
fn assert_harness_active_cast_cleared(
    ctx: &ReducerContext,
    caster: Identity,
    label: &str,
) -> Result<(), String> {
    if ctx.db.active_cast().caster().find(caster).is_some() {
        return Err(format!("{label}: active cast was not cleared"));
    }
    Ok(())
}

#[cfg(feature = "spellcasting_terminal_harness")]
fn assert_harness_event_count(
    ctx: &ReducerContext,
    action_instance_id: &str,
    event_type: &str,
    expected_count: usize,
) -> Result<(), String> {
    let count = ctx
        .db
        .combat_event()
        .iter()
        .filter(|event| {
            event.action_instance_id == action_instance_id && event.event_type == event_type
        })
        .count();
    if count == expected_count {
        return Ok(());
    }
    Err(format!(
        "unexpected harness combat event count type={event_type} action_instance_id={action_instance_id} expected={expected_count} actual={count}"
    ))
}

#[cfg(feature = "spellcasting_terminal_harness")]
fn assert_harness_no_projectile(
    ctx: &ReducerContext,
    action_instance_id: &str,
) -> Result<(), String> {
    if ctx
        .db
        .active_combat_projectile()
        .iter()
        .any(|projectile| projectile.action_instance_id == action_instance_id)
    {
        return Err(format!(
            "harness cast spawned unexpected projectile action_instance_id={action_instance_id}"
        ));
    }
    Ok(())
}

#[cfg(feature = "spellcasting_terminal_harness")]
fn cleanup_spellcasting_terminal_harness(ctx: &ReducerContext, caster: Identity, target: Identity) {
    ctx.db.active_cast().caster().delete(caster);
    ctx.db.channel_cast_runtime().caster().delete(caster);
    ctx.db.special_movement_runtime().owner().delete(caster);
    ctx.db.cast_prediction_correlation().caster().delete(caster);
    ctx.db.global_cooldown().caster().delete(caster);
    for pending in ctx
        .db
        .pending_cast_cancel()
        .iter()
        .filter(|pending| pending.caster == caster)
        .collect::<Vec<_>>()
    {
        ctx.db
            .pending_cast_cancel()
            .cancel_key()
            .delete(pending.cancel_key);
    }
    for projectile_id in ctx
        .db
        .active_combat_projectile()
        .iter()
        .filter(|projectile| {
            projectile
                .action_instance_id
                .starts_with(SPELLCASTING_TERMINAL_HARNESS_PREFIX)
        })
        .map(|projectile| projectile.projectile_instance_id)
        .collect::<Vec<_>>()
    {
        ctx.db
            .active_combat_projectile()
            .projectile_instance_id()
            .delete(projectile_id);
    }
    for event_id in ctx
        .db
        .combat_event()
        .iter()
        .filter(|event| {
            event
                .action_instance_id
                .starts_with(SPELLCASTING_TERMINAL_HARNESS_PREFIX)
        })
        .map(|event| event.event_id)
        .collect::<Vec<_>>()
    {
        ctx.db.combat_event().event_id().delete(event_id);
    }
    for identity in [caster, target] {
        ctx.db.player_intent().identity().delete(identity);
        ctx.db.player_physics().identity().delete(identity);
        ctx.db.player_state().player_id().delete(identity);
        ctx.db.player_world().identity().delete(identity);
    }
}

pub(crate) fn begin_special_movement(
    ctx: &ReducerContext,
    owner: Identity,
    kind: &str,
    started_at: Timestamp,
    duration_ms: u64,
    start: Vec3,
    end: Vec3,
    facing_yaw_start: f32,
    collision_policy: &str,
) -> SpecialMovementRuntime {
    begin_special_movement_with_facing_policy(
        ctx,
        owner,
        kind,
        started_at,
        duration_ms,
        start,
        end,
        facing_yaw_start,
        SPECIAL_MOVEMENT_FACING_FACE_PATH,
        collision_policy,
    )
}

pub(crate) fn begin_special_movement_with_facing_policy(
    ctx: &ReducerContext,
    owner: Identity,
    kind: &str,
    started_at: Timestamp,
    duration_ms: u64,
    start: Vec3,
    end: Vec3,
    facing_yaw_start: f32,
    facing_policy: &str,
    collision_policy: &str,
) -> SpecialMovementRuntime {
    let runtime = SpecialMovementRuntime {
        owner,
        runtime_id: format!(
            "{}:{}:{}",
            owner.to_hex(),
            kind,
            started_at.to_micros_since_unix_epoch()
        ),
        kind: kind.to_string(),
        path_mode: SPECIAL_MOVEMENT_PATH_LINEAR.to_string(),
        started_at,
        duration_ms,
        start_x: start.x,
        start_y: start.y,
        start_z: start.z,
        end_x: end.x,
        end_y: end.y,
        end_z: end.z,
        facing_yaw_start,
        facing_policy: facing_policy.to_string(),
        collision_policy: collision_policy.to_string(),
        resolve_policy: SPECIAL_MOVEMENT_RESOLVE_AT_END.to_string(),
    };

    ctx.db.special_movement_runtime().owner().delete(owner);
    ctx.db.special_movement_runtime().insert(runtime.clone());
    runtime
}

fn emit_movement_delivery_fizzle(
    ctx: &ReducerContext,
    action_instance_id: &str,
    ability_id: &str,
    kind: &SpellId,
    caster: Identity,
    caster_state: &PlayerSnapshot,
    target: Option<&PlayerSnapshot>,
    fallback_point: Vec3,
) {
    let movement = movement_delivery_for_action_id(kind.as_str())
        .expect("validated movement action must resolve to movement delivery");
    let point = target
        .map(|resolved| Vec3::new(resolved.pos_x, resolved.pos_y, resolved.pos_z))
        .unwrap_or(fallback_point);
    let dir_x = point.x - caster_state.pos_x;
    let dir_z = point.z - caster_state.pos_z;
    let (fizzle_dir_x, fizzle_dir_z) = if dir_x * dir_x + dir_z * dir_z > 0.0001 {
        let inv_len = 1.0 / (dir_x * dir_x + dir_z * dir_z).sqrt();
        (dir_x * inv_len, dir_z * inv_len)
    } else {
        let forward = default_forward_direction(caster_state);
        (forward.x, forward.z)
    };

    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: action_instance_id.to_string(),
        action_kind: kind.as_str().to_string(),
        ability_id: ability_id.to_string(),
        hit_index: -1,
        event_type: EVENT_FIZZLE.to_string(),
        source_kind: "SPELL".to_string(),
        caster,
        hit: Identity::ZERO,
        origin_x: caster_state.pos_x,
        origin_y: caster_state.pos_y,
        origin_z: caster_state.pos_z,
        dir_x: fizzle_dir_x,
        dir_y: 0.0,
        dir_z: fizzle_dir_z,
        speed: movement.speed,
        max_distance: movement.max_distance,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 1,
        point_x: point.x,
        point_y: point.y,
        point_z: point.z,
        created_at: ctx.timestamp,
        created_at_micros: timestamp_to_micros(ctx.timestamp),
        damage: 0,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });
}

#[derive(Clone, Copy)]
struct ElectrocuteChannelState {
    origin: Vec3,
    direction: Vec3,
    end_point: Vec3,
    target_id: Identity,
}

enum ElectrocuteChannelResolution {
    Ready(ElectrocuteChannelState),
    Invalid,
}

fn start_electrocute_channel(
    ctx: &ReducerContext,
    active_cast: &ActiveCast,
    caster_state: &PlayerSnapshot,
    now: Timestamp,
) -> Result<bool, String> {
    let definition = bespoke_spell_definition(BespokeRuntimeSpell::Electrocute)
        .expect("validated spell catalog must define ELECTROCUTE");
    let channel = match resolve_electrocute_channel_state(ctx, active_cast, caster_state) {
        ElectrocuteChannelResolution::Ready(channel) => channel,
        ElectrocuteChannelResolution::Invalid => return Ok(false),
    };
    let runtime = ChannelCastRuntime {
        caster: active_cast.caster,
        spell_instance_id: active_cast.cast_id.clone(),
        last_update_at: now,
    };
    ctx.db.channel_cast_runtime().insert(runtime.clone());

    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: runtime.spell_instance_id.as_str(),
            ability_id: active_cast.ability_id.as_str(),
            kind: &definition.kind,
            event_type: EVENT_RELEASE,
            caster: active_cast.caster,
            hit: channel.target_id,
            origin: channel.origin,
            direction: channel.direction,
            speed: 0.0,
            max_distance: definition.max_distance,
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: channel.end_point,
            now,
        },
    );
    queue_electrocute_damage(ctx, active_cast, channel.target_id);
    Ok(true)
}

fn tick_electrocute_channel(
    ctx: &ReducerContext,
    active_cast: &ActiveCast,
    caster_state: &PlayerSnapshot,
    now: Timestamp,
) -> Result<bool, String> {
    let definition = bespoke_spell_definition(BespokeRuntimeSpell::Electrocute)
        .expect("validated spell catalog must define ELECTROCUTE");
    let channel = match resolve_electrocute_channel_state(ctx, active_cast, caster_state) {
        ElectrocuteChannelResolution::Ready(channel) => channel,
        ElectrocuteChannelResolution::Invalid => {
            stop_electrocute_channel(ctx, active_cast, caster_state, now, None);
            return Ok(false);
        }
    };
    let Some(runtime) = ctx
        .db
        .channel_cast_runtime()
        .caster()
        .find(active_cast.caster)
    else {
        stop_electrocute_channel(ctx, active_cast, caster_state, now, None);
        return Ok(false);
    };

    let update_interval = seconds_to_duration(definition.update_interval);
    if now < runtime.last_update_at + update_interval {
        return Ok(true);
    }

    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: runtime.spell_instance_id.as_str(),
            ability_id: active_cast.ability_id.as_str(),
            kind: &definition.kind,
            event_type: EVENT_UPDATE,
            caster: active_cast.caster,
            hit: channel.target_id,
            origin: channel.end_point,
            direction: channel.direction,
            speed: 0.0,
            max_distance: definition.max_distance,
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: channel.end_point,
            now,
        },
    );
    queue_electrocute_damage(ctx, active_cast, channel.target_id);

    let mut next_runtime = runtime.clone();
    next_runtime.last_update_at = now;
    ctx.db.channel_cast_runtime().caster().update(next_runtime);
    Ok(true)
}

fn stop_electrocute_channel(
    ctx: &ReducerContext,
    active_cast: &ActiveCast,
    caster_state: &PlayerSnapshot,
    now: Timestamp,
    override_end: Option<Vec3>,
) {
    let definition = bespoke_spell_definition(BespokeRuntimeSpell::Electrocute)
        .expect("validated spell catalog must define ELECTROCUTE");
    let runtime = ctx
        .db
        .channel_cast_runtime()
        .caster()
        .find(active_cast.caster);
    let fallback_end = override_end.unwrap_or_else(|| {
        match resolve_electrocute_channel_state(ctx, active_cast, caster_state) {
            ElectrocuteChannelResolution::Ready(state) => state.end_point,
            ElectrocuteChannelResolution::Invalid => default_electrocute_end_point(caster_state),
        }
    });

    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: runtime
                .as_ref()
                .map(|entry| entry.spell_instance_id.as_str())
                .unwrap_or(active_cast.cast_id.as_str()),
            ability_id: active_cast.ability_id.as_str(),
            kind: &definition.kind,
            event_type: EVENT_FIZZLE,
            caster: active_cast.caster,
            hit: Identity::ZERO,
            origin: Vec3::new(
                caster_state.pos_x,
                caster_state.pos_y + caster_state.hit_height,
                caster_state.pos_z,
            ),
            direction: default_forward_direction(caster_state),
            speed: 0.0,
            max_distance: definition.max_distance,
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: fallback_end,
            now,
        },
    );

    clear_active_cast(ctx, active_cast.caster);
}

fn resolve_electrocute_channel_state(
    ctx: &ReducerContext,
    active_cast: &ActiveCast,
    caster_state: &PlayerSnapshot,
) -> ElectrocuteChannelResolution {
    let Some(target) = resolve_target(ctx, active_cast.caster, active_cast.target_id.as_str())
    else {
        return ElectrocuteChannelResolution::Invalid;
    };
    if !is_target_within_live_facing_arc(
        ctx,
        caster_state,
        active_cast.caster,
        &target,
        TARGET_FACING_ARC_RADIANS,
    ) {
        return ElectrocuteChannelResolution::Invalid;
    }
    if !has_line_of_sight(ctx, caster_state, &target) {
        return ElectrocuteChannelResolution::Invalid;
    }
    let definition = bespoke_spell_definition(BespokeRuntimeSpell::Electrocute)
        .expect("validated spell catalog must define ELECTROCUTE");
    if distance_to_target(caster_state, &target) > definition.max_distance {
        return ElectrocuteChannelResolution::Invalid;
    }

    let origin_x = caster_state.pos_x;
    let origin_y = caster_state.pos_y + caster_state.hit_height;
    let origin_z = caster_state.pos_z;
    let end_x = target.pos_x;
    let end_y = target.pos_y + target.hit_height * 0.8;
    let end_z = target.pos_z;
    let Some((dir_x, dir_y, dir_z)) =
        normalize_vec3(end_x - origin_x, end_y - origin_y, end_z - origin_z)
    else {
        return ElectrocuteChannelResolution::Invalid;
    };

    ElectrocuteChannelResolution::Ready(ElectrocuteChannelState {
        origin: Vec3::new(origin_x, origin_y, origin_z),
        direction: Vec3::new(dir_x, dir_y, dir_z),
        end_point: Vec3::new(end_x, end_y, end_z),
        target_id: target.player_id,
    })
}

fn queue_electrocute_damage(ctx: &ReducerContext, active_cast: &ActiveCast, target_id: Identity) {
    let Some(runtime) = ctx
        .db
        .channel_cast_runtime()
        .caster()
        .find(active_cast.caster)
    else {
        return;
    };
    let Some(caster_state) = player_snapshot_for(ctx, active_cast.caster) else {
        return;
    };
    let Some(target_state) = player_snapshot_for(ctx, target_id) else {
        return;
    };
    let definition = bespoke_spell_definition(BespokeRuntimeSpell::Electrocute)
        .expect("validated spell catalog must define ELECTROCUTE");
    let channel = resolve_electrocute_channel_state(ctx, active_cast, &caster_state);
    let (dir_x, dir_y, dir_z, point_x, point_y, point_z) = match channel {
        ElectrocuteChannelResolution::Ready(channel_state) => (
            channel_state.direction.x,
            channel_state.direction.y,
            channel_state.direction.z,
            channel_state.end_point.x,
            channel_state.end_point.y,
            channel_state.end_point.z,
        ),
        ElectrocuteChannelResolution::Invalid => {
            let forward = default_forward_direction(&caster_state);
            let end_point = default_electrocute_end_point(&caster_state);
            (
                forward.x,
                forward.y,
                forward.z,
                end_point.x,
                end_point.y,
                end_point.z,
            )
        }
    };
    if resolve_blockable_spell_hit(
        ctx,
        runtime.spell_instance_id.as_str(),
        active_cast.ability_id.as_str(),
        &definition.kind,
        active_cast.caster,
        &target_state,
        caster_state.pos_x,
        caster_state.pos_y + caster_state.hit_height,
        caster_state.pos_z,
        dir_x,
        dir_y,
        dir_z,
        0.0,
        definition.max_distance,
        point_x,
        point_y,
        point_z,
        definition.damage,
        definition.block_behavior.as_str(),
        ctx.timestamp,
    ) {
        return;
    }
    queue_effects(
        ctx,
        vec![EffectPacket::Damage {
            amount: definition.damage,
            source: active_cast.caster,
            target: target_id,
            spell_id: runtime.spell_instance_id.clone(),
            delivery: DamageDelivery::Direct,
            direct_action_key: runtime.spell_instance_id,
        }],
    );
}

fn default_electrocute_end_point(caster_state: &PlayerSnapshot) -> Vec3 {
    let origin = Vec3::new(
        caster_state.pos_x,
        caster_state.pos_y + caster_state.hit_height,
        caster_state.pos_z,
    );
    let forward = default_forward_direction(caster_state);
    let distance = bespoke_spell_definition(BespokeRuntimeSpell::Electrocute)
        .expect("validated spell catalog must define ELECTROCUTE")
        .max_distance;
    Vec3::new(
        origin.x + forward.x * distance,
        origin.y + forward.y * distance,
        origin.z + forward.z * distance,
    )
}

fn default_forward_direction(caster_state: &PlayerSnapshot) -> Vec3 {
    Vec3::new(
        caster_state.facing_yaw.sin(),
        0.0,
        caster_state.facing_yaw.cos(),
    )
}

fn distance_to_target(caster_state: &PlayerSnapshot, target: &PlayerSnapshot) -> f32 {
    let dx = target.pos_x - caster_state.pos_x;
    let dy =
        (target.pos_y + target.hit_height * 0.8) - (caster_state.pos_y + caster_state.hit_height);
    let dz = target.pos_z - caster_state.pos_z;
    (dx * dx + dy * dy + dz * dz).sqrt()
}

fn horizontal_distance_to_target(caster_state: &PlayerSnapshot, target: &PlayerSnapshot) -> f32 {
    let dx = target.pos_x - caster_state.pos_x;
    let dz = target.pos_z - caster_state.pos_z;
    (dx * dx + dz * dz).sqrt()
}

fn movement_delivery_has_arrived(
    spell_kind: &SpellId,
    distance: f32,
    arrival_distance: f32,
) -> bool {
    let movement = movement_delivery_for_action_id(spell_kind.as_str())
        .expect("validated movement action must resolve to movement delivery");
    has_arrived_at_contact_distance(distance, arrival_distance, movement.arrival_epsilon)
}

fn seconds_to_duration(seconds: f32) -> Duration {
    Duration::from_secs_f32(seconds.max(0.001))
}

fn spawn_meteor(
    ctx: &ReducerContext,
    caster: Identity,
    _state: &PlayerSnapshot,
    target_x: f32,
    target_y: f32,
    target_z: f32,
    action_instance_id: &str,
    ability_id: &str,
) -> Result<(), String> {
    // Spells emit effects; they do not touch HP directly.
    let now = ctx.timestamp;
    let definition = bespoke_spell_definition(BespokeRuntimeSpell::Meteor)
        .expect("validated spell catalog must define METEOR");
    let kind = &definition.kind;

    let sky_origin = meteor_sky_origin();
    let origin_x = target_x + sky_origin.drift_x;
    let origin_y = target_y + sky_origin.height;
    let origin_z = target_z + sky_origin.drift_z;

    let mut dir_x = target_x - origin_x;
    let mut dir_y = target_y - origin_y;
    let mut dir_z = target_z - origin_z;
    let distance_sq = dir_x * dir_x + dir_y * dir_y + dir_z * dir_z;
    let distance = if distance_sq > 0.0001 {
        let distance = distance_sq.sqrt();
        let inv_len = 1.0 / distance;
        dir_x *= inv_len;
        dir_y *= inv_len;
        dir_z *= inv_len;
        distance
    } else {
        dir_x = 0.0;
        dir_y = 1.0;
        dir_z = 0.0;
        0.0
    };

    let impact_x = target_x;
    let impact_y = target_y;
    let impact_z = target_z;
    let travel_duration = definition.duration.max(0.01);
    let spell_id = action_instance_id.to_string();

    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: spell_id.as_str(),
            ability_id,
            kind,
            event_type: EVENT_RELEASE,
            caster,
            hit: Identity::ZERO,
            origin: Vec3::new(origin_x, origin_y, origin_z),
            direction: Vec3::new(dir_x, dir_y, dir_z),
            speed: 0.0,
            max_distance: distance,
            scalar: SpellCombatEventScalar::TravelDurationSeconds(travel_duration),
            sequence_index: 0,
            sequence_count: 1,
            point: Vec3::new(impact_x, impact_y, impact_z),
            now,
        },
    );

    ctx.db.active_bespoke_spell().insert(ActiveBespokeSpell {
        spell_id: spell_id.clone(),
        kind: kind.as_str().to_string(),
        ability_id: ability_id.to_string(),
        caster,
        target: Identity::ZERO,
        origin_x,
        origin_y,
        origin_z,
        pos_x: origin_x,
        pos_y: origin_y,
        pos_z: origin_z,
        dir_x,
        dir_y,
        dir_z,
        speed: 0.0,
        max_distance: distance,
        traveled: 0.0,
        age: 0.0,
        lifetime: travel_duration,
        update_accum: 0.0,
        created_at: now,
    });

    Ok(())
}

fn spawn_instant_beam(
    ctx: &ReducerContext,
    caster: Identity,
    state: &PlayerSnapshot,
    target: &PlayerSnapshot,
    charge_count: u32,
    charge_pct: f32,
    action_instance_id: &str,
    ability_id: &str,
) -> Result<(), String> {
    let now = ctx.timestamp;
    let definition = bespoke_spell_definition(BespokeRuntimeSpell::InstantBeam)
        .expect("validated spell catalog must define INSTANT_BEAM");
    let kind = &definition.kind;
    let charge_scaling = instant_beam_charge_scaling();
    let clamped_charge = charge_pct.clamp(0.0, 1.0);
    let damage_scale =
        charge_scaling.min_damage_scale + (1.0 - charge_scaling.min_damage_scale) * clamped_charge;
    let scaled_damage = ((definition.damage as f32) * damage_scale).round().max(1.0) as i32;

    let origin_x = state.pos_x;
    let origin_y = state.pos_y + state.hit_height;
    let origin_z = state.pos_z;

    let target_y = target.pos_y + target.hit_height;
    let mut dir_x = target.pos_x - origin_x;
    let mut dir_y = target_y - origin_y;
    let mut dir_z = target.pos_z - origin_z;

    let Some((nx, ny, nz)) = normalize_vec3(dir_x, dir_y, dir_z) else {
        return Ok(());
    };
    dir_x = nx;
    dir_y = ny;
    dir_z = nz;

    let players: Vec<_> = collect_player_snapshots(ctx)
        .into_iter()
        .filter(|player| {
            target_audience_allows(ctx, caster, player.player_id, definition.target_audience)
        })
        .collect();
    let end_x = origin_x + dir_x * definition.max_distance;
    let end_y = origin_y + dir_y * definition.max_distance;
    let end_z = origin_z + dir_z * definition.max_distance;
    let sequence_count = charge_count.max(1).min(charge_scaling.max_charges);

    for sequence_index in 0..sequence_count {
        let sequence_effect_id = if sequence_count > 1 {
            format!("{action_instance_id}:beam:{sequence_index}")
        } else {
            action_instance_id.to_string()
        };

        emit_spell_combat_event(
            ctx,
            SpellCombatEventPayload {
                action_instance_id,
                ability_id,
                kind,
                event_type: EVENT_RELEASE,
                caster,
                hit: Identity::ZERO,
                origin: Vec3::new(origin_x, origin_y, origin_z),
                direction: Vec3::new(dir_x, dir_y, dir_z),
                speed: 0.0,
                max_distance: definition.max_distance,
                scalar: SpellCombatEventScalar::BeamChargePct(clamped_charge),
                sequence_index,
                sequence_count,
                point: Vec3::new(origin_x, origin_y, origin_z),
                now,
            },
        );

        if let Some(hit) = first_hit_on_segment(
            ctx, caster, origin_x, origin_y, origin_z, end_x, end_y, end_z, 0.0, &players,
        ) {
            match hit.kind {
                SceneHitKind::Player(target_id) => {
                    let Some(hit_target) = player_snapshot_for(ctx, target_id) else {
                        continue;
                    };
                    if resolve_blockable_spell_hit(
                        ctx,
                        action_instance_id,
                        ability_id,
                        kind,
                        caster,
                        &hit_target,
                        origin_x,
                        origin_y,
                        origin_z,
                        dir_x,
                        dir_y,
                        dir_z,
                        0.0,
                        definition.max_distance,
                        hit.x,
                        hit.y,
                        hit.z,
                        scaled_damage,
                        definition.block_behavior.as_str(),
                        now,
                    ) {
                        continue;
                    }
                    emit_spell_combat_event_with_damage(
                        ctx,
                        SpellCombatEventPayload {
                            action_instance_id,
                            ability_id,
                            kind,
                            event_type: EVENT_IMPACT,
                            caster,
                            hit: target_id,
                            origin: Vec3::new(origin_x, origin_y, origin_z),
                            direction: Vec3::new(dir_x, dir_y, dir_z),
                            speed: 0.0,
                            max_distance: definition.max_distance,
                            scalar: SpellCombatEventScalar::BeamChargePct(clamped_charge),
                            sequence_index,
                            sequence_count,
                            point: Vec3::new(hit.x, hit.y, hit.z),
                            now,
                        },
                        scaled_damage,
                    );
                    queue_effects(
                        ctx,
                        vec![EffectPacket::Damage {
                            amount: scaled_damage,
                            source: caster,
                            target: target_id,
                            spell_id: sequence_effect_id.clone(),
                            delivery: DamageDelivery::Direct,
                            direct_action_key: sequence_effect_id,
                        }],
                    );
                }
                SceneHitKind::World => {
                    emit_spell_combat_event_with_damage(
                        ctx,
                        SpellCombatEventPayload {
                            action_instance_id,
                            ability_id,
                            kind,
                            event_type: EVENT_IMPACT,
                            caster,
                            hit: Identity::ZERO,
                            origin: Vec3::new(origin_x, origin_y, origin_z),
                            direction: Vec3::new(dir_x, dir_y, dir_z),
                            speed: 0.0,
                            max_distance: definition.max_distance,
                            scalar: SpellCombatEventScalar::BeamChargePct(clamped_charge),
                            sequence_index,
                            sequence_count,
                            point: Vec3::new(hit.x, hit.y, hit.z),
                            now,
                        },
                        0,
                    );
                }
            }
        } else {
            emit_spell_combat_event(
                ctx,
                SpellCombatEventPayload {
                    action_instance_id,
                    ability_id,
                    kind,
                    event_type: EVENT_FIZZLE,
                    caster,
                    hit: Identity::ZERO,
                    origin: Vec3::new(origin_x, origin_y, origin_z),
                    direction: Vec3::new(dir_x, dir_y, dir_z),
                    speed: 0.0,
                    max_distance: definition.max_distance,
                    scalar: SpellCombatEventScalar::BeamChargePct(clamped_charge),
                    sequence_index,
                    sequence_count,
                    point: Vec3::new(end_x, end_y, end_z),
                    now,
                },
            );
        }
    }

    Ok(())
}

fn cast_generic_area(
    ctx: &ReducerContext,
    caster: Identity,
    state: &PlayerSnapshot,
    kind: &SpellId,
    aim_x: f32,
    aim_y: f32,
    aim_z: f32,
    action_instance_id: &str,
    ability_id: &str,
) -> Result<(), String> {
    let now = ctx.timestamp;
    let definition = super::catalog::spell_definition(kind)
        .expect("validated AREA spell must resolve to a definition");
    debug_assert_eq!(definition.behavior, SpellBehavior::Area);

    let origin_x = state.pos_x;
    let origin_y = state.pos_y;
    let origin_z = state.pos_z;
    let facing_yaw = state.facing_yaw;
    let Some(area_center) =
        resolve_generic_area_center_for_cast(ctx, caster, definition, state, aim_x, aim_y, aim_z)
    else {
        return Ok(());
    };
    let area_shape = area_shape_for(definition);

    let spell_id = action_instance_id.to_string();

    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: spell_id.as_str(),
            ability_id,
            kind,
            event_type: EVENT_RELEASE,
            caster,
            hit: Identity::ZERO,
            origin: Vec3::new(origin_x, origin_y, origin_z),
            direction: Vec3::new(0.0, 1.0, 0.0),
            speed: 0.0,
            max_distance: area_shape.query_radius(),
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: area_center,
            now,
        },
    );

    let impact_delay_ms = definition
        .secondary
        .area
        .as_ref()
        .map(|area| area.impact_delay_ms)
        .unwrap_or(0);
    if impact_delay_ms > 0 {
        let impact_at = now + Duration::from_millis(impact_delay_ms);
        ctx.db.pending_area_impact().insert(PendingAreaImpact {
            impact_id: 0,
            caster,
            spell_id,
            kind: kind.as_str().to_string(),
            ability_id: ability_id.to_string(),
            origin_x,
            origin_y,
            origin_z,
            area_x: area_center.x,
            area_y: area_center.y,
            area_z: area_center.z,
            facing_yaw,
            impact_at,
            resolve_at_micros: timestamp_to_micros(impact_at),
        });
        return Ok(());
    }

    resolve_area_impact(
        ctx,
        AreaImpactResolution {
            caster,
            spell_id: spell_id.as_str(),
            ability_id,
            kind,
            definition,
            origin: Vec3::new(origin_x, origin_y, origin_z),
            area_center,
            facing_yaw,
            now,
        },
    );

    Ok(())
}

struct AreaImpactResolution<'a> {
    caster: Identity,
    spell_id: &'a str,
    ability_id: &'a str,
    kind: &'a SpellId,
    definition: &'a SpellDefinition,
    origin: Vec3,
    area_center: Vec3,
    facing_yaw: f32,
    now: Timestamp,
}

pub(crate) fn resolve_pending_area_impacts(ctx: &ReducerContext, now: Timestamp) {
    let now_micros = timestamp_to_micros(now);
    let mut due: Vec<PendingAreaImpact> = ctx
        .db
        .pending_area_impact()
        .resolve_at_micros()
        .filter(..=now_micros)
        .collect();
    due.sort_by_key(|row| (row.resolve_at_micros, row.impact_id));

    for row in due {
        if ctx
            .db
            .pending_area_impact()
            .impact_id()
            .find(row.impact_id)
            .is_none()
        {
            continue;
        }

        resolve_pending_area_impact(ctx, &row, now);
        if ctx
            .db
            .pending_area_impact()
            .impact_id()
            .find(row.impact_id)
            .is_some()
        {
            ctx.db
                .pending_area_impact()
                .impact_id()
                .delete(row.impact_id);
        }
    }
}

pub(crate) fn has_due_pending_area_impacts(ctx: &ReducerContext, now: Timestamp) -> bool {
    ctx.db
        .pending_area_impact()
        .resolve_at_micros()
        .filter(..=timestamp_to_micros(now))
        .next()
        .is_some()
}

fn resolve_pending_area_impact(ctx: &ReducerContext, row: &PendingAreaImpact, now: Timestamp) {
    let Ok(kind) = SpellId::new(row.kind.as_str()) else {
        return;
    };
    let Some(definition) = super::catalog::spell_definition(&kind) else {
        return;
    };
    if definition.behavior != SpellBehavior::Area {
        return;
    }

    resolve_area_impact(
        ctx,
        AreaImpactResolution {
            caster: row.caster,
            spell_id: row.spell_id.as_str(),
            ability_id: row.ability_id.as_str(),
            kind: &definition.kind,
            definition,
            origin: Vec3::new(row.origin_x, row.origin_y, row.origin_z),
            area_center: Vec3::new(row.area_x, row.area_y, row.area_z),
            facing_yaw: row.facing_yaw,
            now,
        },
    );
}

fn resolve_area_impact(ctx: &ReducerContext, impact: AreaImpactResolution<'_>) {
    let area_shape = area_shape_for(impact.definition);
    let area_direction = area_impact_direction(impact.facing_yaw);

    if matches!(area_shape, CombatAreaShape::Cone { .. }) {
        // AREA_IMPACT is the area action/VFX signal and intentionally fires even when no targets pass
        // the later per-target filters; CONTACT/damage events remain the "hit landed" signal.
        emit_spell_combat_event(
            ctx,
            SpellCombatEventPayload {
                action_instance_id: impact.spell_id,
                ability_id: impact.ability_id,
                kind: impact.kind,
                event_type: EVENT_AREA_IMPACT,
                caster: impact.caster,
                hit: Identity::ZERO,
                origin: impact.area_center,
                direction: area_direction,
                speed: 0.0,
                max_distance: area_shape.query_radius(),
                scalar: SpellCombatEventScalar::None,
                sequence_index: 0,
                sequence_count: 1,
                point: impact.area_center,
                now: impact.now,
            },
        );
    }

    emit_spell_combat_event_with_damage(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: impact.spell_id,
            ability_id: impact.ability_id,
            kind: impact.kind,
            event_type: EVENT_IMPACT,
            caster: impact.caster,
            hit: Identity::ZERO,
            origin: impact.origin,
            direction: Vec3::new(0.0, 1.0, 0.0),
            speed: 0.0,
            max_distance: area_shape.query_radius(),
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: impact.area_center,
            now: impact.now,
        },
        impact.definition.damage,
    );

    let player_snapshots = PlayerSnapshotSet::collect(ctx);
    let players = player_snapshots.as_slice();
    let mut candidate_indices = Vec::new();
    player_snapshots.query_disc_indices(
        impact.area_center.x,
        impact.area_center.z,
        area_shape.query_radius(),
        &mut candidate_indices,
    );

    let mut effects = Vec::new();
    for player in candidate_indices
        .iter()
        .filter_map(|index| players.get(*index))
    {
        if !player.alive || player.player_id == impact.caster {
            continue;
        }
        if !players_share_world_context(ctx, impact.caster, player.player_id) {
            continue;
        }
        if !target_audience_allows(
            ctx,
            impact.caster,
            player.player_id,
            impact.definition.target_audience,
        ) {
            continue;
        }
        if !area_shape_contains_player(area_shape, &impact, player) {
            continue;
        }
        if resolve_blockable_spell_hit(
            ctx,
            impact.spell_id,
            impact.ability_id,
            impact.kind,
            impact.caster,
            player,
            impact.origin.x,
            impact.origin.y,
            impact.origin.z,
            0.0,
            1.0,
            0.0,
            0.0,
            area_shape.query_radius(),
            impact.area_center.x,
            impact.area_center.y,
            impact.area_center.z,
            impact.definition.damage,
            impact.definition.block_behavior.as_str(),
            impact.now,
        ) {
            continue;
        }
        if impact.definition.damage > 0 {
            let contact_direction = area_contact_direction(
                impact.area_center.x,
                impact.area_center.z,
                impact.origin.x,
                impact.origin.z,
                player,
            );
            emit_spell_combat_event_with_damage(
                ctx,
                SpellCombatEventPayload {
                    action_instance_id: impact.spell_id,
                    ability_id: impact.ability_id,
                    kind: impact.kind,
                    event_type: EVENT_CONTACT,
                    caster: impact.caster,
                    hit: player.player_id,
                    origin: impact.origin,
                    direction: contact_direction,
                    speed: 0.0,
                    max_distance: area_shape.query_radius(),
                    scalar: SpellCombatEventScalar::None,
                    sequence_index: 0,
                    sequence_count: 1,
                    point: Vec3::new(player.pos_x, player.pos_y, player.pos_z),
                    now: impact.now,
                },
                impact.definition.damage,
            );
        }
        effects.push(EffectPacket::Damage {
            amount: impact.definition.damage,
            source: impact.caster,
            target: player.player_id,
            spell_id: impact.spell_id.to_string(),
            delivery: DamageDelivery::Direct,
            direct_action_key: impact.spell_id.to_string(),
        });
        if let Some(area) = impact.definition.secondary.area.as_ref() {
            push_impact_effect_packets(
                &mut effects,
                area.impact_effects.as_slice(),
                impact.caster,
                player.player_id,
                impact.spell_id,
                impact.definition.kind.as_str(),
                impact.definition.damage > 0,
            );
        }
    }

    if !effects.is_empty() {
        queue_effects(ctx, effects);
    }
}

fn area_shape_for(definition: &SpellDefinition) -> CombatAreaShape {
    definition
        .secondary
        .area
        .as_ref()
        .map(|area| area.shape)
        .unwrap_or(CombatAreaShape::Disc {
            radius: definition.radius,
        })
}

fn area_shape_contains_player(
    shape: CombatAreaShape,
    impact: &AreaImpactResolution<'_>,
    player: &PlayerSnapshot,
) -> bool {
    shape.contains_player(
        impact.area_center.x,
        impact.area_center.y,
        impact.area_center.z,
        impact.facing_yaw,
        player,
        FACING_DOT_EPSILON,
    )
}

fn area_impact_direction(facing_yaw: f32) -> Vec3 {
    Vec3::new(facing_yaw.sin(), 0.0, facing_yaw.cos())
}

fn area_contact_direction(
    center_x: f32,
    center_z: f32,
    origin_x: f32,
    origin_z: f32,
    target: &PlayerSnapshot,
) -> Vec3 {
    if let Some((dir_x, _, dir_z)) =
        normalize_vec3(target.pos_x - center_x, 0.0, target.pos_z - center_z)
    {
        return Vec3::new(dir_x, 0.0, dir_z);
    }

    if let Some((dir_x, _, dir_z)) =
        normalize_vec3(target.pos_x - origin_x, 0.0, target.pos_z - origin_z)
    {
        return Vec3::new(dir_x, 0.0, dir_z);
    }

    Vec3::new(0.0, 0.0, 1.0)
}

fn resolve_generic_area_center(
    definition: &SpellDefinition,
    state: &PlayerSnapshot,
    aim_x: f32,
    aim_y: f32,
    aim_z: f32,
) -> Option<Vec3> {
    match definition.targeting {
        super::manifest::SpellTargeting::Self_ => {
            Some(Vec3::new(state.pos_x, state.pos_y, state.pos_z))
        }
        super::manifest::SpellTargeting::Point => {
            if !aim_x.is_finite() || !aim_y.is_finite() || !aim_z.is_finite() {
                return None;
            }

            let dx = aim_x - state.pos_x;
            let dz = aim_z - state.pos_z;
            let distance = (dx * dx + dz * dz).sqrt();
            if distance > definition.max_distance + 0.001 {
                return None;
            }

            Some(Vec3::new(aim_x, aim_y, aim_z))
        }
        super::manifest::SpellTargeting::Target => None,
    }
}

fn resolve_generic_area_center_for_cast(
    ctx: &ReducerContext,
    caster: Identity,
    definition: &SpellDefinition,
    state: &PlayerSnapshot,
    aim_x: f32,
    aim_y: f32,
    aim_z: f32,
) -> Option<Vec3> {
    let mut center = resolve_generic_area_center(definition, state, aim_x, aim_y, aim_z)?;
    if self_origin_area_projects_to_ground(definition) {
        center.y = terrain_surface_y_for_caster(ctx, caster, center.x, center.z, state.pos_y);
    }
    Some(center)
}

fn self_origin_area_projects_to_ground(definition: &SpellDefinition) -> bool {
    definition.targeting == super::manifest::SpellTargeting::Self_
        && matches!(
            definition.secondary.area.as_ref().map(|area| area.shape),
            Some(CombatAreaShape::Cone { .. })
        )
}

fn spawn_negate(
    ctx: &ReducerContext,
    caster: Identity,
    state: &PlayerSnapshot,
    action_instance_id: &str,
    ability_id: &str,
) -> Result<(), String> {
    let now = ctx.timestamp;
    let definition = bespoke_spell_definition(BespokeRuntimeSpell::Negate)
        .expect("validated spell catalog must define NEGATE");
    let kind = &definition.kind;
    let lifetime = definition.max_distance / definition.speed.max(0.01);

    let origin_x = state.pos_x;
    let origin_y = state.pos_y;
    let origin_z = state.pos_z;
    let spell_id = action_instance_id.to_string();

    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: spell_id.as_str(),
            ability_id,
            kind,
            event_type: EVENT_RELEASE,
            caster,
            hit: Identity::ZERO,
            origin: Vec3::new(origin_x, origin_y, origin_z),
            direction: Vec3::new(0.0, 1.0, 0.0),
            speed: definition.speed,
            max_distance: definition.max_distance,
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: Vec3::new(origin_x, origin_y, origin_z),
            now,
        },
    );

    ctx.db.active_bespoke_spell().insert(ActiveBespokeSpell {
        spell_id: spell_id.clone(),
        kind: kind.as_str().to_string(),
        ability_id: ability_id.to_string(),
        caster,
        target: Identity::ZERO,
        origin_x,
        origin_y,
        origin_z,
        pos_x: origin_x,
        pos_y: origin_y,
        pos_z: origin_z,
        dir_x: 0.0,
        dir_y: 1.0,
        dir_z: 0.0,
        speed: definition.speed,
        max_distance: definition.max_distance,
        traveled: 0.0,
        age: 0.0,
        lifetime,
        update_accum: 0.0,
        created_at: now,
    });

    Ok(())
}

fn cast_apply_status(
    ctx: &ReducerContext,
    caster: Identity,
    state: &PlayerSnapshot,
    kind: &SpellId,
    target_id: &str,
    mode: CastExecutionMode,
    action_instance_id: &str,
    ability_id: &str,
) -> Result<bool, String> {
    let definition = super::catalog::spell_definition(kind)
        .expect("validated APPLY_STATUS spell must resolve to a definition");

    match definition.targeting {
        super::manifest::SpellTargeting::Self_ => {
            if mode == CastExecutionMode::Execute {
                apply_status_to_self(
                    ctx,
                    caster,
                    state,
                    kind,
                    action_instance_id,
                    ability_id,
                    definition,
                )?;
            }
            Ok(true)
        }
        super::manifest::SpellTargeting::Target => {
            let Some(target) = resolve_target(ctx, caster, target_id) else {
                return Ok(false);
            };
            if !target_audience_allows(ctx, caster, target.player_id, definition.target_audience) {
                return Ok(false);
            }
            if !is_target_within_facing_arc(state, &target, TARGET_FACING_ARC_RADIANS) {
                return Ok(false);
            }
            if !has_line_of_sight(ctx, state, &target) {
                return Ok(false);
            }
            if distance_to_target(state, &target) > definition.max_distance {
                return Ok(false);
            }
            if mode == CastExecutionMode::Execute {
                apply_status_to_target(
                    ctx,
                    caster,
                    state,
                    &target,
                    kind,
                    action_instance_id,
                    ability_id,
                    definition,
                )?;
            }
            Ok(true)
        }
        super::manifest::SpellTargeting::Point => Ok(false),
    }
}

fn apply_status_to_self(
    ctx: &ReducerContext,
    caster: Identity,
    state: &PlayerSnapshot,
    kind: &SpellId,
    action_instance_id: &str,
    ability_id: &str,
    definition: &SpellDefinition,
) -> Result<(), String> {
    let now = ctx.timestamp;
    let status = definition
        .apply_status
        .expect("APPLY_STATUS spells must define a status payload");
    let spell_id = action_instance_id.to_string();
    let application = apply_status_application_for_caster(
        ctx,
        caster,
        kind,
        status,
        Duration::from_secs_f32(definition.duration.max(0.01)),
        definition.status_stack_group.clone(),
        StatusStackGroupDefault::EffectKind,
    );

    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: spell_id.as_str(),
            ability_id,
            kind,
            event_type: EVENT_RELEASE,
            caster,
            hit: caster,
            origin: Vec3::new(state.pos_x, state.pos_y, state.pos_z),
            direction: default_forward_direction(state),
            speed: 0.0,
            max_distance: 0.0,
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: Vec3::new(state.pos_x, state.pos_y, state.pos_z),
            now,
        },
    );

    let polarity = definition
        .apply_status_polarity
        .expect("APPLY_STATUS spells must define polarity");
    let mut effects = vec![application.to_effect_packet_for_audience(
        caster,
        caster,
        spell_id.as_str(),
        polarity,
        TargetAudience::SelfOnly,
        definition.kind.as_str(),
    )];

    if definition.radius > 0.0 && definition.target_audience == TargetAudience::PartyOrSelf {
        for player in collect_player_snapshots(ctx) {
            if !player.alive || player.player_id == caster {
                continue;
            }
            if !players_share_world_context(ctx, caster, player.player_id) {
                continue;
            }
            if !target_audience_allows(ctx, caster, player.player_id, definition.target_audience) {
                continue;
            }
            if !aoe_hits_player(
                state.pos_x,
                state.pos_y,
                state.pos_z,
                definition.radius,
                &player,
            ) {
                continue;
            }
            effects.push(application.to_effect_packet_for_audience(
                caster,
                player.player_id,
                spell_id.as_str(),
                polarity,
                definition.target_audience,
                definition.kind.as_str(),
            ));
        }
    }

    queue_effects(ctx, effects);

    Ok(())
}

fn apply_status_application_for_caster(
    ctx: &ReducerContext,
    caster: Identity,
    kind: &SpellId,
    status: super::manifest::ApplyStatusDefinition,
    duration: Duration,
    status_stack_group: Option<String>,
    stack_group_default: StatusStackGroupDefault,
) -> StatusApplication {
    StatusApplication::new(
        scale_apply_status_payload_for_caster(ctx, caster, kind, status.payload()),
        duration,
        status_stack_group,
        stack_group_default,
        status.max_stacks,
        status.stack_policy,
    )
}

fn scale_apply_status_payload_for_caster(
    ctx: &ReducerContext,
    caster: Identity,
    kind: &SpellId,
    payload: StatusPayload,
) -> StatusPayload {
    if kind.as_str() != "FORTIFY" {
        return payload;
    }

    let StatusPayload::TemporaryHitpoints {
        absorb_amount,
        absorb_cap,
    } = payload
    else {
        return payload;
    };

    let allocated = derived_combat_stats_for_owner(ctx, caster).allocated;

    StatusPayload::TemporaryHitpoints {
        absorb_amount: scale_fortify_temporary_hitpoints_from_allocations(absorb_amount, allocated),
        absorb_cap: scale_fortify_temporary_hitpoints_from_allocations(absorb_cap, allocated),
    }
}

fn apply_status_to_target(
    ctx: &ReducerContext,
    caster: Identity,
    state: &PlayerSnapshot,
    target: &PlayerSnapshot,
    kind: &SpellId,
    action_instance_id: &str,
    ability_id: &str,
    definition: &SpellDefinition,
) -> Result<(), String> {
    let now = ctx.timestamp;
    let status = definition
        .apply_status
        .expect("APPLY_STATUS spells must define a status payload");
    let application = apply_status_application_for_caster(
        ctx,
        caster,
        kind,
        status,
        Duration::from_secs_f32(definition.duration.max(0.01)),
        definition.status_stack_group.clone(),
        StatusStackGroupDefault::EffectKind,
    );
    let spell_id = action_instance_id.to_string();

    let dx = target.pos_x - state.pos_x;
    let dy = (target.pos_y + target.hit_height * 0.5) - (state.pos_y + state.hit_height * 0.5);
    let dz = target.pos_z - state.pos_z;
    let distance_sq = dx * dx + dy * dy + dz * dz;
    let direction = if distance_sq > 0.0001 {
        let inv_len = 1.0 / distance_sq.sqrt();
        Vec3::new(dx * inv_len, dy * inv_len, dz * inv_len)
    } else {
        default_forward_direction(state)
    };
    let origin = Vec3::new(
        state.pos_x,
        state.pos_y + state.hit_height * 0.5,
        state.pos_z,
    );
    let point = Vec3::new(
        target.pos_x,
        target.pos_y + target.hit_height * 0.5,
        target.pos_z,
    );

    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: spell_id.as_str(),
            ability_id,
            kind,
            event_type: EVENT_RELEASE,
            caster,
            hit: target.player_id,
            origin,
            direction,
            speed: 0.0,
            max_distance: definition.max_distance,
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: origin,
            now,
        },
    );

    let parry_behavior = definition
        .secondary
        .apply_status
        .expect("validated APPLY_STATUS spell must define secondary apply-status data")
        .parry_behavior;
    if resolve_spell_combat_hit_defense(
        ctx,
        spell_id.as_str(),
        ability_id,
        kind,
        caster,
        target,
        origin.x,
        origin.y,
        origin.z,
        direction.x,
        direction.y,
        direction.z,
        0.0,
        definition.max_distance,
        point.x,
        point.y,
        point.z,
        0,
        parry_behavior.as_str(),
        definition.block_behavior.as_str(),
        now,
    ) {
        return Ok(());
    }

    emit_spell_combat_event_with_damage(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: spell_id.as_str(),
            ability_id,
            kind,
            event_type: EVENT_IMPACT,
            caster,
            hit: target.player_id,
            origin,
            direction,
            speed: 0.0,
            max_distance: definition.max_distance,
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point,
            now,
        },
        0,
    );

    queue_effects(
        ctx,
        vec![application.to_effect_packet_for_audience(
            caster,
            target.player_id,
            spell_id.as_str(),
            definition
                .apply_status_polarity
                .expect("APPLY_STATUS spells must define polarity"),
            definition.target_audience,
            definition.kind.as_str(),
        )],
    );

    Ok(())
}

fn cast_self_resource(
    ctx: &ReducerContext,
    caster: Identity,
    state: &PlayerSnapshot,
    kind: &SpellId,
    action_instance_id: &str,
    ability_id: &str,
) {
    let now = ctx.timestamp;
    let definition = super::catalog::spell_definition(kind)
        .expect("validated SELF_RESOURCE spell must resolve to a definition");
    let spell_id = action_instance_id.to_string();

    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: spell_id.as_str(),
            ability_id,
            kind,
            event_type: EVENT_RELEASE,
            caster,
            hit: caster,
            origin: Vec3::new(state.pos_x, state.pos_y, state.pos_z),
            direction: default_forward_direction(state),
            speed: 0.0,
            max_distance: 0.0,
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: Vec3::new(state.pos_x, state.pos_y, state.pos_z),
            now,
        },
    );

    debug_assert!(
        definition.primary_resource_gain_on_cast > 0.0,
        "SELF_RESOURCE spells must define a resource gain"
    );
}

fn cast_remove_status(
    ctx: &ReducerContext,
    caster: Identity,
    state: &PlayerSnapshot,
    kind: &SpellId,
    action_instance_id: &str,
    ability_id: &str,
) {
    let now = ctx.timestamp;
    let definition = super::catalog::spell_definition(kind)
        .expect("validated REMOVE_STATUS spell must resolve to a definition");
    let remove_status = definition
        .secondary
        .remove_status
        .as_ref()
        .expect("REMOVE_STATUS spells must define statuses");
    let spell_id = action_instance_id.to_string();

    emit_spell_combat_event(
        ctx,
        SpellCombatEventPayload {
            action_instance_id: spell_id.as_str(),
            ability_id,
            kind,
            event_type: EVENT_RELEASE,
            caster,
            hit: caster,
            origin: Vec3::new(state.pos_x, state.pos_y, state.pos_z),
            direction: default_forward_direction(state),
            speed: 0.0,
            max_distance: 0.0,
            scalar: SpellCombatEventScalar::None,
            sequence_index: 0,
            sequence_count: 1,
            point: Vec3::new(state.pos_x, state.pos_y, state.pos_z),
            now,
        },
    );

    queue_effects(
        ctx,
        remove_status
            .statuses
            .iter()
            .map(|status| EffectPacket::RemoveStatus {
                target: caster,
                kind: status.kind,
                stack_group: status.stack_group.clone().unwrap_or_default(),
            })
            .collect(),
    );
}

fn resolve_movement_delivery_hit(
    ctx: &ReducerContext,
    action_instance_id: &str,
    ability_id: &str,
    caster: Identity,
    kind: &SpellId,
    caster_state: &PlayerSnapshot,
    target: &PlayerSnapshot,
) -> Result<(), String> {
    let now = ctx.timestamp;
    let movement = movement_delivery_for_action_id(kind.as_str())
        .expect("validated movement action must resolve to movement delivery");

    let dx = target.pos_x - caster_state.pos_x;
    let dz = target.pos_z - caster_state.pos_z;
    let horiz_dist = (dx * dx + dz * dz).sqrt();
    let (dir_x, dir_z) = if horiz_dist > 0.001 {
        (dx / horiz_dist, dz / horiz_dist)
    } else {
        let forward = default_forward_direction(caster_state);
        (forward.x, forward.z)
    };

    match resolve_defensible_combat_hit(
        ctx,
        DefensibleCombatHit {
            delivery_kind: CombatHitDeliveryKind::MovementDelivery,
            defender: target.player_id,
            active_from: now,
            active_until: now + Duration::from_millis(1),
            parry_behavior: movement.parry_behavior.as_str(),
            block_behavior: movement.block_behavior.as_str(),
            source_x: caster_state.pos_x,
            source_y: caster_state.pos_y,
            source_z: caster_state.pos_z,
            impact_x: target.pos_x,
            impact_y: target.pos_y + target.hit_height * 0.5,
            impact_z: target.pos_z,
            dir_x,
            dir_y: 0.0,
            dir_z,
            speed: movement.speed,
        },
    ) {
        DefenseResolution::Blocked => {
            mark_harmful_combat_action(ctx, caster, target.player_id, now, kind.as_str());
            emit_direct_spell_terminal_event(
                ctx,
                action_instance_id,
                ability_id,
                kind,
                crate::combat::COMBAT_EVENT_BLOCK,
                caster,
                target.player_id,
                Vec3::new(caster_state.pos_x, caster_state.pos_y, caster_state.pos_z),
                Vec3::new(dir_x, 0.0, dir_z),
                movement.speed,
                movement.max_distance,
                Vec3::new(target.pos_x, target.pos_y, target.pos_z),
                0,
                now,
            );
            return Ok(());
        }
        DefenseResolution::Parried => {
            mark_harmful_combat_action(ctx, caster, target.player_id, now, kind.as_str());
            emit_direct_spell_terminal_event(
                ctx,
                action_instance_id,
                ability_id,
                kind,
                crate::combat::COMBAT_EVENT_PARRY,
                caster,
                target.player_id,
                Vec3::new(caster_state.pos_x, caster_state.pos_y, caster_state.pos_z),
                Vec3::new(dir_x, 0.0, dir_z),
                movement.speed,
                movement.max_distance,
                Vec3::new(target.pos_x, target.pos_y, target.pos_z),
                0,
                now,
            );
            return Ok(());
        }
        DefenseResolution::None => {}
    }

    emit_direct_spell_terminal_event(
        ctx,
        action_instance_id,
        ability_id,
        kind,
        EVENT_IMPACT,
        caster,
        target.player_id,
        Vec3::new(caster_state.pos_x, caster_state.pos_y, caster_state.pos_z),
        Vec3::new(dir_x, 0.0, dir_z),
        movement.speed,
        movement.max_distance,
        Vec3::new(target.pos_x, target.pos_y, target.pos_z),
        movement.damage,
        now,
    );

    let mut effects = vec![EffectPacket::Damage {
        amount: movement.damage,
        source: caster,
        target: target.player_id,
        spell_id: action_instance_id.to_string(),
        delivery: DamageDelivery::Direct,
        direct_action_key: action_instance_id.to_string(),
    }];
    let impact_effects = movement_impact_effects(&movement);
    push_impact_effect_packets(
        &mut effects,
        impact_effects.as_slice(),
        caster,
        target.player_id,
        action_instance_id,
        kind.as_str(),
        movement.damage > 0,
    );
    queue_effects(ctx, effects);

    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn emit_direct_spell_terminal_event(
    ctx: &ReducerContext,
    action_instance_id: &str,
    ability_id: &str,
    kind: &SpellId,
    event_type: &str,
    caster: Identity,
    hit: Identity,
    origin: Vec3,
    direction: Vec3,
    speed: f32,
    max_distance: f32,
    point: Vec3,
    damage: i32,
    now: Timestamp,
) {
    ctx.db.combat_event().insert(CombatEvent {
        event_id: 0,
        action_instance_id: action_instance_id.to_string(),
        action_kind: kind.as_str().to_string(),
        ability_id: ability_id.to_string(),
        hit_index: if hit == Identity::ZERO { -1 } else { 0 },
        event_type: event_type.to_string(),
        source_kind: "SPELL".to_string(),
        caster,
        hit,
        origin_x: origin.x,
        origin_y: origin.y,
        origin_z: origin.z,
        dir_x: direction.x,
        dir_y: direction.y,
        dir_z: direction.z,
        speed,
        max_distance,
        scalar_kind: COMBAT_SCALAR_NONE.to_string(),
        scalar_value: 0.0,
        sequence_kind: COMBAT_SEQUENCE_NONE.to_string(),
        sequence_index: 0,
        sequence_count: 1,
        point_x: point.x,
        point_y: point.y,
        point_z: point.z,
        created_at: now,
        created_at_micros: timestamp_to_micros(now),
        damage,
        metadata_kind: COMBAT_METADATA_NONE.to_string(),
        metadata_key: String::new(),
        metadata_value: String::new(),
    });
}

fn is_target_within_facing_arc(
    caster: &PlayerSnapshot,
    target: &PlayerSnapshot,
    facing_arc_radians: f32,
) -> bool {
    let to_target_x = target.pos_x - caster.pos_x;
    let to_target_z = target.pos_z - caster.pos_z;
    is_direction_within_facing_arc(
        caster.facing_yaw,
        to_target_x,
        to_target_z,
        facing_arc_radians,
        FACING_DOT_EPSILON,
    )
}

fn is_target_within_live_facing_arc(
    ctx: &ReducerContext,
    caster_state: &PlayerSnapshot,
    caster_id: Identity,
    target: &PlayerSnapshot,
    facing_arc_radians: f32,
) -> bool {
    let facing_yaw = ctx
        .db
        .player_intent()
        .identity()
        .find(caster_id)
        .map(|intent| intent.yaw)
        .unwrap_or(caster_state.facing_yaw);

    let to_target_x = target.pos_x - caster_state.pos_x;
    let to_target_z = target.pos_z - caster_state.pos_z;
    is_direction_within_facing_arc(
        facing_yaw,
        to_target_x,
        to_target_z,
        facing_arc_radians,
        FACING_DOT_EPSILON,
    )
}

#[cfg(test)]
mod tests {
    use crate::player_intent::PlayerIntent;
    use crate::progression::movement_delivery_for_action_id;

    use super::{
        active_cast_cancel_receive_window_allows, active_cast_interrupt_terminal_policy,
        approach_line_contact_point_xz, area_contact_direction, contact_distance_from_radii,
        fixed_y_terrain_blocks_special_movement, has_arrived_at_contact_distance,
        has_movement_intent, has_voluntary_movement_after_cast, horizontal_movement_duration_ms,
        is_generic_area_spell, is_target_within_facing_arc,
        normal_cast_time_spell_refunds_gcd_on_self_cancel, projectile_execute_uses_live_facing,
        resolve_generic_area_center, resolve_special_movement_y,
        spell_primary_resource_cost_for_action, valid_cast_action_token,
        violates_active_cast_lifetime_mobility_requirement_for_tick,
        violates_cast_mobility_requirement, ActiveCastTerminalOutcome, CastExecutionMode,
        PlayerSnapshot, SpellBehavior, SpellId, FACING_DOT_EPSILON,
        SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK, SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_FIXED_Y,
        TARGET_FACING_ARC_RADIANS,
    };
    use crate::combat::scene_query::is_direction_within_facing_arc;
    use crate::spells::ActiveCast;
    use core::time::Duration;
    use spacetimedb::{Identity, Timestamp};

    fn test_intent(forward: f32, strafe: f32, jump: bool) -> PlayerIntent {
        PlayerIntent {
            identity: Identity::ZERO,
            forward,
            strafe,
            yaw: 0.0,
            jump,
            input_tick: 0,
            updated_at: Timestamp::UNIX_EPOCH,
        }
    }

    fn test_snapshot(pos_x: f32, pos_z: f32, facing_yaw: f32) -> PlayerSnapshot {
        PlayerSnapshot {
            player_id: Identity::ZERO,
            alive: true,
            pos_x,
            pos_y: 0.0,
            pos_z,
            facing_yaw,
            grounded: true,
            hit_radius: 0.5,
            hit_height: 1.8,
            last_processed_tick: 0,
        }
    }

    fn spell_id(id: &str) -> SpellId {
        SpellId::new(id).expect("test spell id should be valid")
    }

    fn test_active_cast(ends_at: Timestamp) -> ActiveCast {
        ActiveCast {
            caster: Identity::ZERO,
            cast_id: "test-cast".to_string(),
            ability_id: "ICICLE".to_string(),
            kind: "ICICLE".to_string(),
            target_id: String::new(),
            aim_x: 0.0,
            aim_y: 0.0,
            aim_z: 0.0,
            started_at: ends_at - Duration::from_secs(1),
            ends_at,
            cast_authored_input_tick: 0,
            charge_count: 0,
            max_charge_count: 0,
            predicted_cast_id: String::new(),
            client_action_seq: 0,
        }
    }

    #[test]
    fn cast_action_token_validation_allows_legacy_empty_pair_only() {
        assert!(valid_cast_action_token("", 0));
        assert!(valid_cast_action_token("0123456789abcdef", 1));
        assert!(valid_cast_action_token("cast_id-ABC_123", 99));

        assert!(!valid_cast_action_token("", 1));
        assert!(!valid_cast_action_token("0123456789abcdef", 0));
        assert!(!valid_cast_action_token("contains:separator", 1));
        assert!(!valid_cast_action_token(&"a".repeat(65), 1));
    }

    #[test]
    fn pre_end_cancel_window_accepts_matching_late_cancel_inside_grace() {
        let ends_at = Timestamp::UNIX_EPOCH + Duration::from_secs(10);
        let active_cast = test_active_cast(ends_at);

        assert!(active_cast_cancel_receive_window_allows(
            &active_cast,
            ends_at + Duration::from_millis(100),
            1,
        ));
    }

    #[test]
    fn pre_end_cancel_window_rejects_post_end_or_expired_cancel() {
        let ends_at = Timestamp::UNIX_EPOCH + Duration::from_secs(10);
        let active_cast = test_active_cast(ends_at);

        assert!(!active_cast_cancel_receive_window_allows(
            &active_cast,
            ends_at + Duration::from_millis(1),
            0,
        ));
        assert!(!active_cast_cancel_receive_window_allows(
            &active_cast,
            ends_at + Duration::from_millis(101),
            1,
        ));
    }

    #[test]
    fn frost_nova_uses_generic_area_path_without_capturing_bespoke_area_spells() {
        let frost_nova = spell_id("FROST_NOVA");
        let frost_nova_definition =
            crate::spells::spell_definition_by_str(frost_nova.as_str()).unwrap();
        assert!(is_generic_area_spell(&frost_nova, frost_nova_definition));

        for id in ["METEOR", "NEGATE"] {
            let spell = spell_id(id);
            let definition = crate::spells::spell_definition_by_str(spell.as_str()).unwrap();
            assert!(
                !is_generic_area_spell(&spell, definition),
                "{id} must stay on its bespoke runtime path"
            );
        }
    }

    #[test]
    fn generic_area_center_uses_aim_point_for_point_targeted_area_spells() {
        let state = test_snapshot(10.0, 20.0, 0.0);
        let mut definition = crate::spells::spell_definition_by_str("FROST_NOVA")
            .expect("Frost Nova should exist")
            .clone();
        definition.targeting = super::super::manifest::SpellTargeting::Point;
        definition.max_distance = 15.0;

        let center = resolve_generic_area_center(&definition, &state, 13.0, 0.25, 24.0)
            .expect("aim point within range should resolve");

        assert!((center.x - 13.0).abs() < 0.001);
        assert!((center.y - 0.25).abs() < 0.001);
        assert!((center.z - 24.0).abs() < 0.001);
    }

    #[test]
    fn generic_area_center_rejects_point_targeted_area_beyond_range() {
        let state = test_snapshot(0.0, 0.0, 0.0);
        let mut definition = crate::spells::spell_definition_by_str("FROST_NOVA")
            .expect("Frost Nova should exist")
            .clone();
        definition.targeting = super::super::manifest::SpellTargeting::Point;
        definition.max_distance = 5.0;

        assert!(resolve_generic_area_center(&definition, &state, 0.0, 0.0, 5.01).is_none());
    }

    #[test]
    fn area_contact_direction_prefers_area_center_then_caster_origin() {
        let mut target = test_snapshot(3.0, 4.0, 0.0);

        let direction = area_contact_direction(0.0, 0.0, 10.0, 10.0, &target);
        assert!((direction.x - 0.6).abs() < 0.0001);
        assert_eq!(direction.y, 0.0);
        assert!((direction.z - 0.8).abs() < 0.0001);

        target.pos_x = 0.0;
        target.pos_z = 0.0;
        let fallback = area_contact_direction(0.0, 0.0, -1.0, 0.0, &target);
        assert!((fallback.x - 1.0).abs() < 0.0001);
        assert_eq!(fallback.y, 0.0);
        assert!(fallback.z.abs() < 0.0001);
    }

    #[test]
    fn special_movement_y_ground_following_returns_sampled_ground() {
        assert_eq!(
            resolve_special_movement_y(SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK, 2.0, 9.0, 4.5),
            4.5
        );
    }

    #[test]
    fn approach_line_contact_point_stops_at_contact_distance() {
        let (x, z) = approach_line_contact_point_xz(0.0, 0.0, 0.0, 0.0, 10.0, 1.35);

        assert!((x - 0.0).abs() < 0.001);
        assert!((z - 8.65).abs() < 0.001);
    }

    #[test]
    fn contact_distance_uses_hit_radii_and_arrival_buffer() {
        assert!((contact_distance_from_radii(0.5, 0.75, 0.35) - 1.6).abs() < 0.001);
        assert!((contact_distance_from_radii(-1.0, -1.0, -1.0) - 0.1).abs() < 0.001);
    }

    #[test]
    fn arrival_check_uses_contact_distance_and_epsilon() {
        assert!(has_arrived_at_contact_distance(1.64, 1.6, 0.05));
        assert!(!has_arrived_at_contact_distance(1.66, 1.6, 0.05));
    }

    #[test]
    fn charge_actions_no_longer_resolve_as_movement_delivery() {
        assert!(movement_delivery_for_action_id("WARRIOR_CHARGE").is_none());
        assert!(movement_delivery_for_action_id("PALADIN_CHARGE").is_none());
    }

    #[test]
    fn cast_time_projectile_execute_uses_live_facing_policy() {
        let mut definition = crate::spells::spell_definition_by_str("ICICLE")
            .expect("Icicle should exist")
            .clone();
        assert!(definition.cast_time > Duration::ZERO);

        assert!(projectile_execute_uses_live_facing(
            CastExecutionMode::Execute,
            &definition
        ));
        assert!(!projectile_execute_uses_live_facing(
            CastExecutionMode::ValidateOnly,
            &definition
        ));
        assert!(!projectile_execute_uses_live_facing(
            CastExecutionMode::FinalValidate,
            &definition
        ));

        definition.cast_time = Duration::ZERO;
        assert!(!projectile_execute_uses_live_facing(
            CastExecutionMode::Execute,
            &definition
        ));

        definition.behavior = SpellBehavior::Area;
        definition.cast_time = Duration::from_secs(1);
        assert!(!projectile_execute_uses_live_facing(
            CastExecutionMode::Execute,
            &definition
        ));
    }

    #[test]
    fn active_cast_interrupt_policy_preserves_terminal_cleanup_rules() {
        let icicle = spell_id("ICICLE");
        match active_cast_interrupt_terminal_policy(&icicle) {
            ActiveCastTerminalOutcome::SpellFizzle(definition) => {
                assert_eq!(definition.kind.as_str(), "ICICLE");
            }
            other => panic!("Icicle interrupt should fizzle, got {other:?}"),
        }

        let electrocute = spell_id("ELECTROCUTE");
        assert_eq!(
            active_cast_interrupt_terminal_policy(&electrocute),
            ActiveCastTerminalOutcome::ChannelStop
        );

        let unknown = spell_id("UNKNOWN_ACTION");
        assert_eq!(
            active_cast_interrupt_terminal_policy(&unknown),
            ActiveCastTerminalOutcome::SilentClear
        );
    }

    #[test]
    fn gcd_refund_policy_only_allows_normal_cast_time_spells() {
        let icicle = crate::spells::spell_definition_by_str("ICICLE").expect("Icicle should exist");
        assert!(normal_cast_time_spell_refunds_gcd_on_self_cancel(icicle));

        let instant_beam = crate::spells::spell_definition_by_str("INSTANT_BEAM")
            .expect("Instant Beam should exist");
        assert!(!normal_cast_time_spell_refunds_gcd_on_self_cancel(
            instant_beam
        ));

        let electrocute = crate::spells::spell_definition_by_str("ELECTROCUTE")
            .expect("Electrocute should exist");
        assert!(!normal_cast_time_spell_refunds_gcd_on_self_cancel(
            electrocute
        ));

        let momentum =
            crate::spells::spell_definition_by_str("MOMENTUM").expect("Momentum should exist");
        assert!(!normal_cast_time_spell_refunds_gcd_on_self_cancel(momentum));
    }

    #[test]
    fn horizontal_movement_duration_respects_speed_and_floor() {
        assert_eq!(
            horizontal_movement_duration_ms(0.0, 0.0, 0.0, 10.0, 20.0, 1),
            500
        );
        assert_eq!(
            horizontal_movement_duration_ms(0.0, 0.0, 0.0, 0.0, 20.0, 50),
            50
        );
    }

    #[test]
    fn special_movement_y_fixed_height_preserves_height_over_lower_ground() {
        assert_eq!(
            resolve_special_movement_y(
                SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_FIXED_Y,
                6.0,
                6.0,
                2.0
            ),
            6.0
        );
    }

    #[test]
    fn special_movement_y_fixed_height_preserves_height_over_rising_ground() {
        assert_eq!(
            resolve_special_movement_y(
                SPECIAL_MOVEMENT_COLLISION_STOP_AT_BLOCK_FIXED_Y,
                2.0,
                2.0,
                5.5
            ),
            2.0
        );
    }

    #[test]
    fn fixed_y_special_movement_blocks_rising_terrain() {
        assert!(!fixed_y_terrain_blocks_special_movement(4.0, 3.5));
        assert!(!fixed_y_terrain_blocks_special_movement(4.0, 4.0));
        assert!(fixed_y_terrain_blocks_special_movement(4.0, 4.01));
    }

    #[test]
    fn facing_arc_accepts_front_direction() {
        assert!(is_direction_within_facing_arc(
            0.0,
            0.0,
            3.0,
            TARGET_FACING_ARC_RADIANS,
            FACING_DOT_EPSILON,
        ));
    }

    #[test]
    fn facing_arc_accepts_side_boundary_for_half_turn_arc() {
        assert!(is_direction_within_facing_arc(
            0.0,
            2.0,
            0.0,
            TARGET_FACING_ARC_RADIANS,
            FACING_DOT_EPSILON,
        ));
        assert!(is_direction_within_facing_arc(
            0.0,
            -2.0,
            0.0,
            TARGET_FACING_ARC_RADIANS,
            FACING_DOT_EPSILON,
        ));
    }

    #[test]
    fn facing_arc_rejects_behind_direction() {
        assert!(!is_direction_within_facing_arc(
            0.0,
            0.0,
            -2.0,
            TARGET_FACING_ARC_RADIANS,
            FACING_DOT_EPSILON,
        ));
    }

    #[test]
    fn target_facing_arc_rejects_target_behind_caster() {
        let caster = test_snapshot(0.0, 0.0, 0.0);
        let target = test_snapshot(0.0, -3.0, 0.0);

        assert!(!is_target_within_facing_arc(
            &caster,
            &target,
            TARGET_FACING_ARC_RADIANS,
        ));
    }

    #[test]
    fn movement_intent_detects_jump_and_axis_input() {
        assert!(has_movement_intent(&test_intent(1.0, 0.0, false)));
        assert!(has_movement_intent(&test_intent(0.0, -1.0, false)));
        assert!(has_movement_intent(&test_intent(0.0, 0.0, true)));
        assert!(!has_movement_intent(&test_intent(0.0, 0.0, false)));
    }

    #[test]
    fn non_instant_casts_require_grounded_and_stationary() {
        let idle = test_intent(0.0, 0.0, false);
        let moving = test_intent(1.0, 0.0, false);

        assert!(!violates_cast_mobility_requirement(
            &spell_id("ICICLE"),
            true,
            Some(&idle),
        ));
        assert!(violates_cast_mobility_requirement(
            &spell_id("ICICLE"),
            true,
            Some(&moving),
        ));
        assert!(violates_cast_mobility_requirement(
            &spell_id("ICICLE"),
            false,
            Some(&idle),
        ));
    }

    #[test]
    fn mobility_gate_uses_catalog_cast_mobility_policy() {
        let moving = test_intent(1.0, 1.0, true);

        assert!(!violates_cast_mobility_requirement(
            &spell_id("FIREBALL"),
            true,
            Some(&moving),
        ));
        assert!(violates_cast_mobility_requirement(
            &spell_id("ELECTROCUTE"),
            false,
            Some(&moving),
        ));
        assert!(violates_cast_mobility_requirement(
            &spell_id("ELECTROCUTE"),
            true,
            Some(&moving),
        ));
        assert!(!violates_cast_mobility_requirement(
            &spell_id("MOMENTUM"),
            true,
            Some(&moving),
        ));
        assert!(!violates_cast_mobility_requirement(
            &spell_id("NEGATE"),
            false,
            None,
        ));
    }

    #[test]
    fn active_cast_mobility_uses_authored_input_tick_boundary() {
        let mut active_cast = test_active_cast(Timestamp::UNIX_EPOCH + Duration::from_secs(2));
        active_cast.cast_authored_input_tick = 10;

        assert!(!has_voluntary_movement_after_cast(9, &active_cast));
        assert!(!has_voluntary_movement_after_cast(10, &active_cast));
        assert!(has_voluntary_movement_after_cast(11, &active_cast));
    }

    #[test]
    fn active_cast_lifetime_allows_pre_cast_movement_processed_late() {
        let icicle = crate::spells::spell_definition_by_str("ICICLE").expect("Icicle should exist");
        let mut active_cast = test_active_cast(Timestamp::UNIX_EPOCH + Duration::from_secs(2));
        active_cast.cast_authored_input_tick = 42;

        assert!(
            !violates_active_cast_lifetime_mobility_requirement_for_tick(
                icicle,
                true,
                41,
                &active_cast,
            )
        );
        assert!(
            !violates_active_cast_lifetime_mobility_requirement_for_tick(
                icicle,
                true,
                42,
                &active_cast,
            )
        );
    }

    #[test]
    fn active_cast_lifetime_fizzles_post_cast_movement() {
        let icicle = crate::spells::spell_definition_by_str("ICICLE").expect("Icicle should exist");
        let mut active_cast = test_active_cast(Timestamp::UNIX_EPOCH + Duration::from_secs(2));
        active_cast.cast_authored_input_tick = 42;

        assert!(violates_active_cast_lifetime_mobility_requirement_for_tick(
            icicle,
            true,
            43,
            &active_cast,
        ));
    }

    #[test]
    fn active_cast_lifetime_ignores_fallback_inherited_intent() {
        let icicle = crate::spells::spell_definition_by_str("ICICLE").expect("Icicle should exist");
        let mut active_cast = test_active_cast(Timestamp::UNIX_EPOCH + Duration::from_secs(2));
        active_cast.cast_authored_input_tick = 42;

        assert!(violates_cast_mobility_requirement(
            &spell_id("ICICLE"),
            true,
            Some(&test_intent(1.0, 0.0, false)),
        ));
        assert!(
            !violates_active_cast_lifetime_mobility_requirement_for_tick(
                icicle,
                true,
                0,
                &active_cast,
            )
        );
    }

    #[test]
    fn active_cast_lifetime_still_fizzles_airborne_caster() {
        let icicle = crate::spells::spell_definition_by_str("ICICLE").expect("Icicle should exist");
        let mut active_cast = test_active_cast(Timestamp::UNIX_EPOCH + Duration::from_secs(2));
        active_cast.cast_authored_input_tick = 42;

        assert!(violates_active_cast_lifetime_mobility_requirement_for_tick(
            icicle,
            false,
            0,
            &active_cast,
        ));
    }

    #[test]
    fn momentum_resolved_cast_cost_uses_spell_catalog_cost() {
        let cost = spell_primary_resource_cost_for_action(&spell_id("MOMENTUM"));

        assert!((cost.amount - 20.0).abs() < 0.0001);
    }
}

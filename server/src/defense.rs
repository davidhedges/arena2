use std::time::Duration;

use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::action_prediction::{
    has_predicted_action_result, optional_action_prediction_token, record_predicted_action_result,
    ActionPredictionToken, ActionRejectReason, ActionResultKind, OptionalActionPredictionToken,
    PredictedActionFamily,
};
use crate::action_snapshot::{validate_authoritative_action_snapshot, ActionSnapshotRequest};
use crate::combat::{has_active_disabling_status, timestamp_to_micros};

#[allow(unused_imports)]
use crate::defense::defense_state as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;

const BLOCK_HOLD_WINDOW_MS: u64 = 3_600_000;
const PARRY_ARMED_WINDOW_MS: u64 = BLOCK_HOLD_WINDOW_MS;
// S8 favor-the-defender (D4, owner-signed): hits that looked simultaneous on
// a defender's screen (rendering attackers ≤ ~150 ms in the past) must all
// resolve under the defense they reacted with, not land clean during the
// success cooldown. Constant on purpose — the server keeps no per-client RTT.
const PARRY_SUCCESS_GRACE_MS: u64 = 150;
const PARRY_SUCCESS_COOLDOWN_MS: u64 = 10_000;
const BLOCK_SUCCESS_GRACE_MS: u64 = PARRY_SUCCESS_GRACE_MS;
const BLOCK_SUCCESS_COOLDOWN_MS: u64 = PARRY_SUCCESS_COOLDOWN_MS;
const DEFENSE_ARC_TOTAL_DEGREES: f32 = 120.0;
const DEFENSE_VECTOR_EPSILON_SQ: f32 = 0.000001;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DefenseKind {
    Parry,
    ParryCooldown,
    Block,
    BlockCooldown,
    Dodge,
}

impl DefenseKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Parry => "PARRY",
            Self::ParryCooldown => "PARRY_COOLDOWN",
            Self::Block => "BLOCK",
            Self::BlockCooldown => "BLOCK_COOLDOWN",
            Self::Dodge => "DODGE",
        }
    }

    pub fn from_wire(value: &str) -> Option<Self> {
        match value {
            "PARRY" => Some(Self::Parry),
            "PARRY_COOLDOWN" => Some(Self::ParryCooldown),
            "BLOCK" => Some(Self::Block),
            "BLOCK_COOLDOWN" => Some(Self::BlockCooldown),
            "DODGE" => Some(Self::Dodge),
            _ => None,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DefenseResolution {
    None,
    Parried,
    Blocked,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum CombatHitDeliveryKind {
    Melee,
    Projectile,
    Spell,
    MovementDelivery,
}

impl CombatHitDeliveryKind {
    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::Melee => "melee",
            Self::Projectile => "projectile",
            Self::Spell => "spell",
            Self::MovementDelivery => "movement",
        }
    }
}

/// S9 telemetry rider ([DEFENSE_LATE], logging only, active in all builds):
/// the last time a payload-valid, parryable-or-blockable hit resolved
/// undefended for this defender. `start_parry`/`start_block` measure their
/// own lateness against it — the D4b re-evaluation dataset. Player defenders
/// only (NPCs never press a defense); private, one row per player.
#[table(accessor = combat_last_undefended_hit)]
pub struct CombatLastUndefendedHit {
    #[primary_key]
    pub identity: Identity,
    pub resolved_at_micros: i64,
    pub delivery_kind: String,
}

/// E5: the 250 ms rewind cap plus reaction jitter — wide enough to see the
/// distribution's tail, not so wide it counts unrelated presses.
const DEFENSE_LATE_WINDOW_MS: i64 = 400;

pub(crate) struct DefensibleCombatHit<'a> {
    pub delivery_kind: CombatHitDeliveryKind,
    pub defender: Identity,
    pub active_from: Timestamp,
    pub active_until: Timestamp,
    pub parry_behavior: &'a str,
    pub block_behavior: &'a str,
    pub source_x: f32,
    pub source_y: f32,
    pub source_z: f32,
    pub impact_x: f32,
    pub impact_y: f32,
    pub impact_z: f32,
    pub dir_x: f32,
    pub dir_y: f32,
    pub dir_z: f32,
    pub speed: f32,
}

#[table(accessor = defense_state, public)]
#[derive(Clone)]
pub struct DefenseState {
    #[primary_key]
    pub owner: Identity,
    pub action_id: String,
    pub kind: String,
    pub started_at: Timestamp,
    pub active_from: Timestamp,
    pub active_until: Timestamp,
    pub recovery_until: Timestamp,
    pub movement_restrict_from_input_tick: u32,
    pub movement_restrict_until_input_tick: u32,
    pub facing_yaw: f32,
}

struct OptionalDefensePredictionToken {
    token: Option<ActionPredictionToken>,
    input_was_invalid: bool,
    duplicate_result_exists: bool,
}

fn defense_prediction_token(
    ctx: &ReducerContext,
    owner: Identity,
    predicted_action_id: String,
    client_action_seq: u64,
) -> OptionalDefensePredictionToken {
    let token = match optional_action_prediction_token(predicted_action_id, client_action_seq) {
        OptionalActionPredictionToken::Legacy => {
            return OptionalDefensePredictionToken {
                token: None,
                input_was_invalid: false,
                duplicate_result_exists: false,
            };
        }
        OptionalActionPredictionToken::Invalid => {
            return OptionalDefensePredictionToken {
                token: None,
                input_was_invalid: true,
                duplicate_result_exists: false,
            };
        }
        OptionalActionPredictionToken::Predicted(token) => token,
    };

    let duplicate_result_exists =
        has_predicted_action_result(ctx, owner, PredictedActionFamily::Defense, &token);
    OptionalDefensePredictionToken {
        token: Some(token),
        input_was_invalid: false,
        duplicate_result_exists,
    }
}

fn record_optional_defense_prediction_result(
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
        PredictedActionFamily::Defense,
        token,
        action_instance_id,
        result,
        reject_reason,
        now,
    );
}

fn build_defense_action_id(owner: Identity, kind: DefenseKind, now: Timestamp) -> String {
    format!(
        "{}:{}:{}",
        timestamp_to_micros(now),
        owner.to_hex(),
        kind.as_str()
    )
}

#[reducer]
pub fn start_parry(
    ctx: &ReducerContext,
    input_tick: u32,
    pos_x: f32,
    pos_y: f32,
    pos_z: f32,
    yaw: f32,
    predicted_action_id: String,
    client_action_seq: u64,
) -> Result<(), String> {
    let owner = ctx.sender();
    let now = ctx.timestamp;
    let prediction_token =
        defense_prediction_token(ctx, owner, predicted_action_id, client_action_seq);
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
        record_optional_defense_prediction_result(
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
        record_optional_defense_prediction_result(
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

    if let Some(existing) = ctx.db.defense_state().owner().find(owner) {
        if now < existing.recovery_until {
            record_optional_defense_prediction_result(
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
    }

    log_defense_late_press(ctx, owner, DefenseKind::Parry, now);
    let active_from = now;
    let active_until = now + Duration::from_millis(PARRY_ARMED_WINDOW_MS);
    let action_id = build_defense_action_id(owner, DefenseKind::Parry, now);
    upsert_defense_state(
        ctx,
        DefenseState {
            owner,
            action_id: action_id.clone(),
            kind: DefenseKind::Parry.as_str().to_string(),
            started_at: now,
            active_from,
            active_until,
            recovery_until: active_until,
            movement_restrict_from_input_tick: 0,
            movement_restrict_until_input_tick: 0,
            facing_yaw: snapshot.facing_yaw,
        },
    );
    record_optional_defense_prediction_result(
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

#[reducer]
pub fn stop_parry(ctx: &ReducerContext) -> Result<(), String> {
    let owner = ctx.sender();
    let Some(state) = ctx.db.defense_state().owner().find(owner) else {
        return Ok(());
    };
    if DefenseKind::from_wire(state.kind.as_str()) != Some(DefenseKind::Parry) {
        return Ok(());
    }

    ctx.db.defense_state().owner().delete(owner);
    Ok(())
}

#[reducer]
pub fn start_block(
    ctx: &ReducerContext,
    effective_input_tick: u32,
    input_tick: u32,
    pos_x: f32,
    pos_y: f32,
    pos_z: f32,
    yaw: f32,
    predicted_action_id: String,
    client_action_seq: u64,
) -> Result<(), String> {
    let _ = effective_input_tick;
    let owner = ctx.sender();
    let now = ctx.timestamp;
    let prediction_token =
        defense_prediction_token(ctx, owner, predicted_action_id, client_action_seq);
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
        record_optional_defense_prediction_result(
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
        record_optional_defense_prediction_result(
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

    if let Some(existing) = ctx.db.defense_state().owner().find(owner) {
        if now < existing.recovery_until {
            let Some(kind) = DefenseKind::from_wire(existing.kind.as_str()) else {
                ctx.db.defense_state().owner().delete(owner);
                record_optional_defense_prediction_result(
                    ctx,
                    owner,
                    prediction_token.token.as_ref(),
                    "",
                    ActionResultKind::Rejected,
                    ActionRejectReason::Busy,
                    now,
                );
                return Ok(());
            };
            if kind == DefenseKind::Block && now < existing.active_until {
                record_optional_defense_prediction_result(
                    ctx,
                    owner,
                    prediction_token.token.as_ref(),
                    existing.action_id.as_str(),
                    ActionResultKind::Accepted,
                    ActionRejectReason::None,
                    now,
                );
                return Ok(());
            }
            record_optional_defense_prediction_result(
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
    }

    log_defense_late_press(ctx, owner, DefenseKind::Block, now);
    let active_from = now;
    let active_until = now + Duration::from_millis(BLOCK_HOLD_WINDOW_MS);
    let action_id = build_defense_action_id(owner, DefenseKind::Block, now);
    upsert_defense_state(
        ctx,
        DefenseState {
            owner,
            action_id: action_id.clone(),
            kind: DefenseKind::Block.as_str().to_string(),
            started_at: now,
            active_from,
            active_until,
            recovery_until: active_until,
            movement_restrict_from_input_tick: 0,
            movement_restrict_until_input_tick: 0,
            facing_yaw: snapshot.facing_yaw,
        },
    );
    record_optional_defense_prediction_result(
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

#[reducer]
pub fn stop_block(ctx: &ReducerContext, effective_input_tick: u32) -> Result<(), String> {
    let _ = effective_input_tick;
    let owner = ctx.sender();
    let Some(state) = ctx.db.defense_state().owner().find(owner) else {
        return Ok(());
    };
    if DefenseKind::from_wire(state.kind.as_str()) != Some(DefenseKind::Block) {
        return Ok(());
    }

    ctx.db.defense_state().owner().delete(owner);
    Ok(())
}

pub(crate) fn is_defense_active(ctx: &ReducerContext, owner: Identity, now: Timestamp) -> bool {
    let Some(state) = ctx.db.defense_state().owner().find(owner) else {
        return false;
    };
    matches!(
        DefenseKind::from_wire(state.kind.as_str()),
        Some(
            DefenseKind::Block
                | DefenseKind::BlockCooldown
                | DefenseKind::Parry
                | DefenseKind::ParryCooldown
        )
    ) && now >= state.active_from
        && now < state.active_until
}

pub(crate) fn clear_interruptible_defense_for_owner(ctx: &ReducerContext, owner: Identity) {
    let Some(state) = ctx.db.defense_state().owner().find(owner) else {
        return;
    };
    let Some(kind) = DefenseKind::from_wire(state.kind.as_str()) else {
        ctx.db.defense_state().owner().delete(owner);
        return;
    };

    match kind {
        DefenseKind::Block => {
            ctx.db.defense_state().owner().delete(owner);
        }
        DefenseKind::Parry => {
            ctx.db.defense_state().owner().delete(owner);
        }
        DefenseKind::ParryCooldown | DefenseKind::BlockCooldown | DefenseKind::Dodge => {}
    }
}

pub(crate) fn resolve_defensible_combat_hit(
    ctx: &ReducerContext,
    hit: DefensibleCombatHit<'_>,
) -> DefenseResolution {
    if !defensible_hit_payload_is_valid(&hit) {
        return DefenseResolution::None;
    }

    let resolution = resolve_valid_defensible_combat_hit(ctx, &hit);
    // S9 rider: a payload-valid, parryable-or-blockable hit that resolves
    // undefended stamps the defender's row — including the late-press case
    // where no defense_state row existed yet.
    if resolution == DefenseResolution::None
        && (hit.parry_behavior == "PARRYABLE" || hit.block_behavior == "BLOCKABLE")
    {
        record_undefended_hit(ctx, &hit);
    }
    resolution
}

fn resolve_valid_defensible_combat_hit(
    ctx: &ReducerContext,
    hit: &DefensibleCombatHit<'_>,
) -> DefenseResolution {
    let Some(state) = ctx.db.defense_state().owner().find(hit.defender) else {
        return DefenseResolution::None;
    };
    let Some(kind) = DefenseKind::from_wire(state.kind.as_str()) else {
        return DefenseResolution::None;
    };

    if !intervals_overlap(
        state.active_from,
        state.active_until,
        hit.active_from,
        hit.active_until,
    ) {
        return DefenseResolution::None;
    }

    let Some(defender_phys) = ctx.db.player_physics().identity().find(hit.defender) else {
        return DefenseResolution::None;
    };
    let (to_attack_x, to_attack_z) =
        defense_arc_vector(hit, defender_phys.pos_x, defender_phys.pos_z);

    if !is_attack_within_defense_arc(state.facing_yaw, to_attack_x, to_attack_z) {
        return DefenseResolution::None;
    }

    match kind {
        DefenseKind::Parry | DefenseKind::ParryCooldown if hit.parry_behavior == "PARRYABLE" => {
            complete_successful_parry(ctx, state);
            DefenseResolution::Parried
        }
        DefenseKind::Block | DefenseKind::BlockCooldown if hit.block_behavior == "BLOCKABLE" => {
            complete_successful_block(ctx, state);
            DefenseResolution::Blocked
        }
        _ => DefenseResolution::None,
    }
}

fn record_undefended_hit(ctx: &ReducerContext, hit: &DefensibleCombatHit<'_>) {
    // Bounded on purpose: only entities that can press a defense get a row,
    // so NPC-heavy scenes don't grow the commitlog one upsert per hit.
    if ctx.db.player_state().player_id().find(hit.defender).is_none() {
        return;
    }

    let row = CombatLastUndefendedHit {
        identity: hit.defender,
        resolved_at_micros: timestamp_to_micros(ctx.timestamp),
        delivery_kind: hit.delivery_kind.as_str().to_string(),
    };
    if ctx
        .db
        .combat_last_undefended_hit()
        .identity()
        .find(hit.defender)
        .is_some()
    {
        ctx.db.combat_last_undefended_hit().identity().update(row);
    } else {
        ctx.db.combat_last_undefended_hit().insert(row);
    }
}

/// [DEFENSE_LATE] press check (S9 rider): an accepted parry/block press that
/// lands within the window after an undefended defensible hit is exactly the
/// loss D4b (deferred defense resolution) was parked on. Logging only.
fn log_defense_late_press(ctx: &ReducerContext, owner: Identity, kind: DefenseKind, now: Timestamp) {
    let Some(row) = ctx.db.combat_last_undefended_hit().identity().find(owner) else {
        return;
    };
    let late_by_ms = (timestamp_to_micros(now) - row.resolved_at_micros) / 1_000;
    if !(0..=DEFENSE_LATE_WINDOW_MS).contains(&late_by_ms) {
        return;
    }
    log::info!(
        "[DEFENSE_LATE] defender={} kind={} late_by_ms={} delivery={}",
        &owner.to_hex()[..8],
        match kind {
            DefenseKind::Parry => "parry",
            DefenseKind::Block => "block",
            _ => "other",
        },
        late_by_ms,
        row.delivery_kind
    );
}

/// Drop the rider row with the entity's other combat rows.
pub(crate) fn clear_defense_telemetry(ctx: &ReducerContext, identity: Identity) {
    ctx.db.combat_last_undefended_hit().identity().delete(identity);
}

fn defensible_hit_payload_is_valid(hit: &DefensibleCombatHit<'_>) -> bool {
    let required_floats = [
        hit.source_x,
        hit.source_y,
        hit.source_z,
        hit.impact_x,
        hit.impact_y,
        hit.impact_z,
        hit.dir_x,
        hit.dir_y,
        hit.dir_z,
        hit.speed,
    ];
    if required_floats.iter().any(|value| !value.is_finite()) {
        return false;
    }

    match hit.delivery_kind {
        CombatHitDeliveryKind::Projectile => hit.speed > 0.0,
        CombatHitDeliveryKind::Melee
        | CombatHitDeliveryKind::Spell
        | CombatHitDeliveryKind::MovementDelivery => hit.speed >= 0.0,
    }
}

fn defense_arc_vector(
    hit: &DefensibleCombatHit<'_>,
    defender_x: f32,
    defender_z: f32,
) -> (f32, f32) {
    let impact_x = hit.impact_x - defender_x;
    let impact_z = hit.impact_z - defender_z;
    if impact_x * impact_x + impact_z * impact_z > DEFENSE_VECTOR_EPSILON_SQ {
        return (impact_x, impact_z);
    }

    let source_x = hit.source_x - defender_x;
    let source_z = hit.source_z - defender_z;
    if source_x * source_x + source_z * source_z > DEFENSE_VECTOR_EPSILON_SQ {
        return (source_x, source_z);
    }

    let dir_len_sq = hit.dir_x * hit.dir_x + hit.dir_z * hit.dir_z;
    if dir_len_sq > DEFENSE_VECTOR_EPSILON_SQ {
        return (-hit.dir_x, -hit.dir_z);
    }

    (0.0, 0.0)
}

fn complete_successful_parry(ctx: &ReducerContext, mut state: DefenseState) {
    let now = ctx.timestamp;
    if is_successful_parry_cooldown_state(&state, now) {
        return;
    }

    state.active_until = now + Duration::from_millis(PARRY_SUCCESS_GRACE_MS);
    state.recovery_until = now + Duration::from_millis(PARRY_SUCCESS_COOLDOWN_MS);
    state.kind = DefenseKind::ParryCooldown.as_str().to_string();
    ctx.db.defense_state().owner().update(state);
}

fn is_successful_parry_cooldown_state(state: &DefenseState, now: Timestamp) -> bool {
    DefenseKind::from_wire(state.kind.as_str()) == Some(DefenseKind::ParryCooldown)
        && now < state.recovery_until
}

fn complete_successful_block(ctx: &ReducerContext, mut state: DefenseState) {
    let now = ctx.timestamp;
    if is_successful_block_cooldown_state(&state, now) {
        return;
    }

    state.active_until = now + Duration::from_millis(BLOCK_SUCCESS_GRACE_MS);
    state.recovery_until = now + Duration::from_millis(BLOCK_SUCCESS_COOLDOWN_MS);
    state.kind = DefenseKind::BlockCooldown.as_str().to_string();
    state.movement_restrict_from_input_tick = 0;
    state.movement_restrict_until_input_tick = 0;
    ctx.db.defense_state().owner().update(state);
}

fn is_successful_block_cooldown_state(state: &DefenseState, now: Timestamp) -> bool {
    DefenseKind::from_wire(state.kind.as_str()) == Some(DefenseKind::BlockCooldown)
        && now < state.recovery_until
}

pub(crate) fn prune_defense_states(ctx: &ReducerContext, now: Timestamp) {
    let stale: Vec<Identity> = ctx
        .db
        .defense_state()
        .iter()
        .filter(|state| {
            if now >= state.recovery_until {
                return true;
            }

            match ctx.db.player_state().player_id().find(state.owner) {
                Some(player_state) => !player_state.alive,
                None => true,
            }
        })
        .map(|state| state.owner)
        .collect();

    for owner in stale {
        ctx.db.defense_state().owner().delete(owner);
    }
}

fn upsert_defense_state(ctx: &ReducerContext, row: DefenseState) {
    if ctx.db.defense_state().owner().find(row.owner).is_some() {
        ctx.db.defense_state().owner().update(row);
    } else {
        ctx.db.defense_state().insert(row);
    }
}

fn intervals_overlap(
    a_start: Timestamp,
    a_end: Timestamp,
    b_start: Timestamp,
    b_end: Timestamp,
) -> bool {
    a_start <= b_end && b_start <= a_end
}

fn is_attack_within_defense_arc(facing_yaw: f32, to_attacker_x: f32, to_attacker_z: f32) -> bool {
    let len_sq = to_attacker_x * to_attacker_x + to_attacker_z * to_attacker_z;
    if len_sq <= 0.000001 {
        return true;
    }

    let inv_len = len_sq.sqrt().recip();
    let dir_x = to_attacker_x * inv_len;
    let dir_z = to_attacker_z * inv_len;
    let forward_x = facing_yaw.sin();
    let forward_z = facing_yaw.cos();
    let dot = forward_x * dir_x + forward_z * dir_z;
    dot >= (DEFENSE_ARC_TOTAL_DEGREES.to_radians() * 0.5).cos()
}

#[cfg(test)]
mod tests {
    use super::{
        defense_arc_vector, defensible_hit_payload_is_valid, is_attack_within_defense_arc,
        is_successful_block_cooldown_state, is_successful_parry_cooldown_state,
        CombatHitDeliveryKind, DefenseKind, DefenseState, DefensibleCombatHit,
    };
    use spacetimedb::{Identity, Timestamp};
    use std::time::Duration;

    #[test]
    fn defense_arc_accepts_front_and_rejects_back() {
        assert!(is_attack_within_defense_arc(0.0, 0.0, 1.0));
        assert!(is_attack_within_defense_arc(0.0, 0.5, 0.866));
        assert!(!is_attack_within_defense_arc(0.0, 0.0, -1.0));
        assert!(!is_attack_within_defense_arc(0.0, 1.0, 0.0));
    }

    #[test]
    fn defense_arc_vector_prefers_impact_point_then_source_then_incoming_direction() {
        let now = Timestamp::UNIX_EPOCH;
        let mut hit = DefensibleCombatHit {
            delivery_kind: CombatHitDeliveryKind::Projectile,
            defender: Identity::ZERO,
            active_from: now,
            active_until: now + Duration::from_millis(1),
            parry_behavior: "PARRYABLE",
            block_behavior: "BLOCKABLE",
            source_x: 0.0,
            source_y: 0.0,
            source_z: -10.0,
            impact_x: 0.0,
            impact_y: 1.0,
            impact_z: 0.6,
            dir_x: 0.0,
            dir_y: 0.0,
            dir_z: 1.0,
            speed: 45.0,
        };

        assert_eq!(defense_arc_vector(&hit, 0.0, 0.0), (0.0, 0.6));

        hit.impact_z = 0.0;
        assert_eq!(defense_arc_vector(&hit, 0.0, 0.0), (0.0, -10.0));

        hit.source_z = 0.0;
        assert_eq!(defense_arc_vector(&hit, 0.0, 0.0), (-0.0, -1.0));
    }

    #[test]
    fn defensible_projectile_hits_require_positive_speed() {
        let now = Timestamp::UNIX_EPOCH;
        let mut hit = DefensibleCombatHit {
            delivery_kind: CombatHitDeliveryKind::Projectile,
            defender: Identity::ZERO,
            active_from: now,
            active_until: now + Duration::from_millis(1),
            parry_behavior: "PARRYABLE",
            block_behavior: "BLOCKABLE",
            source_x: 0.0,
            source_y: 1.0,
            source_z: -1.0,
            impact_x: 0.0,
            impact_y: 1.0,
            impact_z: 0.5,
            dir_x: 0.0,
            dir_y: 0.0,
            dir_z: 1.0,
            speed: 45.0,
        };

        assert!(defensible_hit_payload_is_valid(&hit));

        hit.speed = 0.0;
        assert!(!defensible_hit_payload_is_valid(&hit));

        hit.delivery_kind = CombatHitDeliveryKind::Melee;
        assert!(defensible_hit_payload_is_valid(&hit));
    }

    #[test]
    fn successful_parry_cooldown_state_is_distinct_from_armed_parry() {
        let now = Timestamp::UNIX_EPOCH;
        let armed = DefenseState {
            owner: Identity::ZERO,
            action_id: "test-parry".to_string(),
            kind: DefenseKind::Parry.as_str().to_string(),
            started_at: now,
            active_from: now,
            active_until: now + Duration::from_millis(1000),
            recovery_until: now + Duration::from_millis(1000),
            movement_restrict_from_input_tick: 0,
            movement_restrict_until_input_tick: 0,
            facing_yaw: 0.0,
        };
        assert!(!is_successful_parry_cooldown_state(&armed, now));

        let mut successful = armed.clone();
        successful.kind = DefenseKind::ParryCooldown.as_str().to_string();
        successful.active_until = now + Duration::from_millis(super::PARRY_SUCCESS_GRACE_MS);
        successful.recovery_until = now + Duration::from_millis(super::PARRY_SUCCESS_COOLDOWN_MS);
        assert!(is_successful_parry_cooldown_state(&successful, now));
    }

    #[test]
    fn successful_block_cooldown_state_is_distinct_from_armed_block() {
        let now = Timestamp::UNIX_EPOCH;
        let armed = DefenseState {
            owner: Identity::ZERO,
            action_id: "test-block".to_string(),
            kind: DefenseKind::Block.as_str().to_string(),
            started_at: now,
            active_from: now,
            active_until: now + Duration::from_millis(1000),
            recovery_until: now + Duration::from_millis(1000),
            movement_restrict_from_input_tick: 0,
            movement_restrict_until_input_tick: 0,
            facing_yaw: 0.0,
        };
        assert!(!is_successful_block_cooldown_state(&armed, now));

        let mut successful = armed.clone();
        successful.kind = DefenseKind::BlockCooldown.as_str().to_string();
        successful.active_until = now + Duration::from_millis(super::BLOCK_SUCCESS_GRACE_MS);
        successful.recovery_until = now + Duration::from_millis(super::BLOCK_SUCCESS_COOLDOWN_MS);
        assert!(is_successful_block_cooldown_state(&successful, now));
    }
}

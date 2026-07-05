use std::time::Duration;

use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::action_ids::AuthoredActionId;
use crate::arena::players_share_world_context;
use crate::combat::has_active_disabling_status;
use crate::combat::player_snapshot::player_snapshot_for;
use crate::combat::position_history::{
    fresh_standing_view_delay, lag_comp_auto_swing_enabled, lag_comp_config, rewound_pose_for,
    stamp_standing_press_context,
};
use crate::combat::scene_query::has_line_of_sight;
use crate::combat::temporary_combat_modifiers;
use crate::defense::is_defense_active;
use crate::melee::{
    auto_attack_gameplay_for_profile_mode_action, auto_attack_reference_for_profile,
    get_melee_definition_for_authored, melee_range_bonus, perform_intrinsic_auto_attack_for,
    perform_intrinsic_auto_attack_replacement_for, scaled_auto_attack_cadence_ms,
    MeleeAttackDispatch,
};
use crate::movement_actions::MovementActionKind;
use crate::player::DEFAULT_COMBAT_PROFILE;
use crate::progression::{
    active_selectable_ability_for_ability_id, derived_combat_profile_id_for_owner,
    movement_delivery_for_action_id, resolved_auto_attack_mode_for_owner, AutoAttackCatalog,
    AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE,
};
use crate::relations::{can_harm, combat_relation};

#[allow(unused_imports)]
use crate::auto_attack::auto_attack_state as _;
#[allow(unused_imports)]
use crate::melee::pending_melee_impact as _;
#[allow(unused_imports)]
use crate::melee::queued_melee_followup as _;
#[allow(unused_imports)]
use crate::movement_actions::movement_action_state as _;
#[allow(unused_imports)]
use crate::player_physics::player_physics as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::progression::auto_attack_catalog as _;
#[allow(unused_imports)]
use crate::progression::auto_attack_replacement_catalog as _;
#[allow(unused_imports)]
use crate::spells::active_cast as _;

// Public so the owner's client can schedule the local swing presentation at
// `next_swing_at` (netcode design review S6). Presentation-only read: the
// client subscribes to its own row (owner-filtered) and never writes.
#[table(accessor = auto_attack_state, public)]
#[derive(Clone)]
pub struct AutoAttackState {
    #[primary_key]
    pub owner: Identity,
    pub target: Identity,
    pub combat_profile_id: String,
    pub mode_id: String,
    pub strike_id: String,
    pub cadence_started_at: Timestamp,
    pub next_swing_at: Timestamp,
    pub pending_due: bool,
    pub movement_epoch_at_schedule: u64,
}

#[table(accessor = pending_auto_attack_replacement)]
#[derive(Clone)]
pub struct PendingAutoAttackReplacement {
    #[primary_key]
    pub owner: Identity,
    pub ability_id: String,
    pub replacement_id: String,
    pub armed_at: Timestamp,
    pub expires_at: Timestamp,
    #[index(btree)]
    pub expires_at_micros: i64,
}

#[reducer]
pub fn arm_auto_attack_replacement(ctx: &ReducerContext, ability_id: String) -> Result<(), String> {
    let owner = ctx.sender();
    if has_active_disabling_status(ctx, owner, ctx.timestamp) {
        return Ok(());
    }

    let Some(ability) = active_selectable_ability_for_ability_id(ctx, owner, ability_id.as_str())
    else {
        log::info!(
            "[AUTO_ATTACK_REPLACEMENT] owner={} arm_rejected reason=inactive_ability ability={}",
            short_identity(owner),
            ability_id
        );
        return Ok(());
    };
    if !ability
        .ability_kind
        .eq_ignore_ascii_case("AUTO_ATTACK_REPLACEMENT")
    {
        log::info!(
            "[AUTO_ATTACK_REPLACEMENT] owner={} arm_rejected reason=wrong_ability_kind ability={} kind={}",
            short_identity(owner),
            ability.ability_id,
            ability.ability_kind
        );
        return Ok(());
    }

    let Some(replacement) = ctx
        .db
        .auto_attack_replacement_catalog()
        .replacement_id()
        .find(ability.action_id.clone())
    else {
        log::info!(
            "[AUTO_ATTACK_REPLACEMENT] owner={} arm_rejected reason=missing_replacement ability={} replacement={}",
            short_identity(owner),
            ability.ability_id,
            ability.action_id
        );
        return Ok(());
    };

    let combat_profile_id = derived_combat_profile_id_for_owner(ctx, owner)
        .filter(|profile| !profile.trim().is_empty())
        .unwrap_or_else(|| DEFAULT_COMBAT_PROFILE.to_string());
    if !replacement
        .combat_profile_id
        .eq_ignore_ascii_case(combat_profile_id.as_str())
    {
        log::info!(
            "[AUTO_ATTACK_REPLACEMENT] owner={} arm_rejected reason=profile_mismatch ability={} replacement={} profile={} expected_profile={}",
            short_identity(owner),
            ability.ability_id,
            replacement.replacement_id,
            combat_profile_id,
            replacement.combat_profile_id
        );
        return Ok(());
    }

    let authored_strike_id = AuthoredActionId::new(replacement.authored_melee_strike_id.as_str());
    if get_melee_definition_for_authored(ctx, combat_profile_id.as_str(), &authored_strike_id)
        .is_none()
    {
        log::info!(
            "[AUTO_ATTACK_REPLACEMENT] owner={} arm_rejected reason=missing_strike ability={} replacement={} strike={} profile={}",
            short_identity(owner),
            ability.ability_id,
            replacement.replacement_id,
            replacement.authored_melee_strike_id,
            combat_profile_id
        );
        return Ok(());
    }

    let expires_ms = replacement.expires_ms.max(1);
    let expires_at = ctx.timestamp + Duration::from_millis(expires_ms);
    upsert_pending_auto_attack_replacement(
        ctx,
        PendingAutoAttackReplacement {
            owner,
            ability_id: ability.ability_id.clone(),
            replacement_id: replacement.replacement_id.clone(),
            armed_at: ctx.timestamp,
            expires_at,
            expires_at_micros: timestamp_to_micros(expires_at),
        },
    );
    log::info!(
        "[AUTO_ATTACK_REPLACEMENT] owner={} armed ability={} replacement={} strike={} expires_ms={}",
        short_identity(owner),
        ability.ability_id,
        replacement.replacement_id,
        replacement.authored_melee_strike_id,
        expires_ms
    );
    Ok(())
}

#[reducer]
pub fn arm_auto_attack_target(ctx: &ReducerContext, target_id: String) -> Result<(), String> {
    let Some(target) = resolve_live_target(ctx, ctx.sender(), target_id.as_str()) else {
        log::info!(
            "[AUTO_ATTACK] owner={} source=right_click failed_to_resolve_target target_id={}",
            short_identity(ctx.sender()),
            target_id
        );
        return Ok(());
    };

    arm_auto_attack_with_cadence(ctx, ctx.sender(), target, ctx.timestamp);
    Ok(())
}

#[reducer]
pub fn clear_auto_attack_target(ctx: &ReducerContext) -> Result<(), String> {
    clear_auto_attack_for_owner(ctx, ctx.sender());
    Ok(())
}

pub(crate) fn arm_auto_attack_with_cadence(
    ctx: &ReducerContext,
    owner: Identity,
    target: Identity,
    now: Timestamp,
) {
    schedule_auto_attack_after_cadence(ctx, owner, target, now);
}

pub(crate) fn arm_auto_attack_if_unarmed_with_cadence(
    ctx: &ReducerContext,
    owner: Identity,
    target: Identity,
    now: Timestamp,
) {
    if let Some(existing) = ctx.db.auto_attack_state().owner().find(owner) {
        log::info!(
            "[AUTO_ATTACK] owner={} source=action_start skipped_already_armed existing_target={} existing_mode={} existing_next_at_micros={} existing_pending_due={}",
            short_identity(owner),
            short_identity(existing.target),
            existing.mode_id,
            existing.next_swing_at.to_micros_since_unix_epoch(),
            existing.pending_due
        );
        return;
    }

    schedule_auto_attack_after_cadence(ctx, owner, target, now);
}

pub(crate) fn schedule_auto_attack_from_swing_start(
    ctx: &ReducerContext,
    owner: Identity,
    target: Identity,
    started_at: Timestamp,
) {
    schedule_auto_attack_after_cadence(ctx, owner, target, started_at);
}

fn schedule_auto_attack_after_cadence(
    ctx: &ReducerContext,
    owner: Identity,
    target: Identity,
    from_time: Timestamp,
) {
    let Some(runtime) = resolved_auto_attack_runtime(ctx, owner) else {
        log::info!(
            "[AUTO_ATTACK] owner={} clear reason=missing_runtime",
            short_identity(owner)
        );
        clear_auto_attack_for_owner(ctx, owner);
        return;
    };
    let authored_strike_id = AuthoredActionId::new(runtime.strike_id.as_str());
    let mode_id =
        resolved_auto_attack_mode_for_owner(ctx, owner, runtime.combat_profile_id.as_str());
    let Some(gameplay) = auto_attack_gameplay_for_profile_mode_action(
        ctx,
        runtime.combat_profile_id.as_str(),
        mode_id.as_str(),
        &authored_strike_id,
    ) else {
        log::info!(
            "[AUTO_ATTACK] owner={} clear reason=missing_gameplay profile={} mode={} strike={}",
            short_identity(owner),
            runtime.combat_profile_id,
            mode_id,
            runtime.strike_id
        );
        clear_auto_attack_for_owner(ctx, owner);
        return;
    };
    let movement_epoch = ctx
        .db
        .player_state()
        .player_id()
        .find(owner)
        .map(|state| state.voluntary_move_epoch)
        .unwrap_or(0);
    let attack_speed_multiplier =
        temporary_combat_modifiers(ctx, from_time).attack_speed_multiplier_for(&owner);
    let cadence = Duration::from_millis(scaled_auto_attack_cadence_ms(
        gameplay.cooldown_ms.max(1),
        attack_speed_multiplier,
    ));
    let combat_profile_id = runtime.combat_profile_id;
    let strike_id = runtime.strike_id;
    let next_swing_at = from_time + cadence;

    upsert_auto_attack_state(
        ctx,
        AutoAttackState {
            owner,
            target,
            combat_profile_id: combat_profile_id.clone(),
            mode_id: mode_id.clone(),
            strike_id: strike_id.clone(),
            cadence_started_at: from_time,
            next_swing_at,
            pending_due: false,
            movement_epoch_at_schedule: movement_epoch,
        },
    );
    log::info!(
        "[AUTO_ATTACK] owner={} armed target={} profile={} mode={} strike={} cadence_ms={} next_at_micros={}",
        short_identity(owner),
        short_identity(target),
        combat_profile_id,
        mode_id,
        strike_id,
        cadence.as_millis(),
        next_swing_at.to_micros_since_unix_epoch()
    );
}

pub(crate) fn clear_auto_attack_for_owner(ctx: &ReducerContext, owner: Identity) {
    if ctx.db.auto_attack_state().owner().find(owner).is_some() {
        ctx.db.auto_attack_state().owner().delete(owner);
    }
}

pub(crate) fn tick_auto_attacks(ctx: &ReducerContext, now: Timestamp) {
    prune_expired_pending_auto_attack_replacements(ctx, now);
    let rows: Vec<AutoAttackState> = ctx.db.auto_attack_state().iter().collect();
    for row in rows {
        if ctx.db.auto_attack_state().owner().find(row.owner).is_none() {
            continue;
        }

        let Some(owner_state) = ctx.db.player_state().player_id().find(row.owner) else {
            log::info!(
                "[AUTO_ATTACK] owner={} clear reason=missing_owner_state",
                short_identity(row.owner)
            );
            clear_auto_attack_for_owner(ctx, row.owner);
            continue;
        };
        if !owner_state.alive {
            log::info!(
                "[AUTO_ATTACK] owner={} clear reason=owner_dead",
                short_identity(row.owner)
            );
            clear_auto_attack_for_owner(ctx, row.owner);
            continue;
        }

        let Some(target_snapshot) = player_snapshot_for(ctx, row.target) else {
            log::info!(
                "[AUTO_ATTACK] owner={} clear reason=missing_target_snapshot target={}",
                short_identity(row.owner),
                short_identity(row.target)
            );
            clear_auto_attack_for_owner(ctx, row.owner);
            continue;
        };
        let shared_world = players_share_world_context(ctx, row.owner, row.target);
        let relation = combat_relation(ctx, row.owner, row.target);
        let can_harm_target = can_harm(ctx, row.owner, row.target);
        if !target_snapshot.alive || !shared_world || !can_harm_target {
            log::info!(
                "[AUTO_ATTACK] owner={} clear reason=invalid_target target={} target_alive={} shared_world={} relation={:?} can_harm={}",
                short_identity(row.owner),
                short_identity(row.target),
                target_snapshot.alive,
                shared_world,
                relation,
                can_harm_target
            );
            clear_auto_attack_for_owner(ctx, row.owner);
            continue;
        }

        if auto_attack_paused(ctx, row.owner, now) {
            continue;
        }

        let mut row = row;
        let authored_strike_id = AuthoredActionId::new(row.strike_id.as_str());
        let Some(_strike) = get_melee_definition_for_authored(
            ctx,
            row.combat_profile_id.as_str(),
            &authored_strike_id,
        ) else {
            log::info!(
                "[AUTO_ATTACK] owner={} clear reason=missing_definition profile={} strike={}",
                short_identity(row.owner),
                row.combat_profile_id,
                row.strike_id
            );
            clear_auto_attack_for_owner(ctx, row.owner);
            continue;
        };
        let current_mode =
            resolved_auto_attack_mode_for_owner(ctx, row.owner, row.combat_profile_id.as_str());
        let Some(gameplay) = auto_attack_gameplay_for_profile_mode_action(
            ctx,
            row.combat_profile_id.as_str(),
            current_mode.as_str(),
            &authored_strike_id,
        ) else {
            log::info!(
                "[AUTO_ATTACK] owner={} clear reason=missing_gameplay profile={} mode={} strike={}",
                short_identity(row.owner),
                row.combat_profile_id,
                current_mode,
                row.strike_id
            );
            clear_auto_attack_for_owner(ctx, row.owner);
            continue;
        };

        if row.mode_id != current_mode {
            row = preserve_cadence_for_mode_change(ctx, row, current_mode.as_str(), &gameplay, now);
            upsert_auto_attack_state(ctx, row.clone());
        }

        if should_reset_cadence_for_voluntary_move(
            gameplay.movement_policy.as_str(),
            owner_state.voluntary_move_epoch,
            row.movement_epoch_at_schedule,
        ) {
            log::info!(
                "[AUTO_ATTACK] owner={} target={} reset reason=voluntary_move profile={} mode={} strike={} previous_epoch={} current_epoch={}",
                short_identity(row.owner),
                short_identity(row.target),
                row.combat_profile_id,
                row.mode_id,
                row.strike_id,
                row.movement_epoch_at_schedule,
                owner_state.voluntary_move_epoch
            );
            schedule_auto_attack_after_cadence(ctx, row.owner, row.target, now);
            continue;
        }

        let pending_due = row.pending_due || now >= row.next_swing_at;
        if !pending_due {
            continue;
        }

        let Some(caster_phys) = ctx.db.player_physics().identity().find(row.owner) else {
            log::info!(
                "[AUTO_ATTACK] owner={} clear reason=missing_owner_physics",
                short_identity(row.owner)
            );
            clear_auto_attack_for_owner(ctx, row.owner);
            continue;
        };

        // S9: a due swing's hold gates judge the target pose the owner is
        // rendering, resolved through the standing view-delay signal — the
        // one attack path with no press to carry a view time. Dual verdicts
        // are logged whenever a fresh signal exists (OFF legs still audit);
        // the rewound verdict is *used* only when both the S8 master switch
        // and the S9 auto_swing flag are on. The caster's own pose stays
        // present — server-authoritative, no claim to honor.
        let (lag_comp_on, _) = lag_comp_config(ctx);
        let auto_swing_on = lag_comp_on && lag_comp_auto_swing_enabled(ctx);
        let standing = fresh_standing_view_delay(ctx, row.owner);
        let rewound_pose = standing.as_ref().and_then(|signal| {
            let pose = rewound_pose_for(
                ctx,
                row.target,
                signal.view_delay_micros,
                now,
                &target_snapshot,
            );
            (pose.rewound_by_micros > 0).then_some(pose)
        });
        let use_rewound = auto_swing_on && rewound_pose.is_some();

        let max_reach = gameplay.range + melee_range_bonus() + target_snapshot.hit_radius;
        let dx = target_snapshot.pos_x - caster_phys.pos_x;
        let dz = target_snapshot.pos_z - caster_phys.pos_z;
        let horiz_dist = (dx * dx + dz * dz).sqrt();
        let present_in_reach = horiz_dist <= max_reach;
        let active_in_reach = if let Some(pose) = rewound_pose.as_ref() {
            let rdx = pose.pos_x - caster_phys.pos_x;
            let rdz = pose.pos_z - caster_phys.pos_z;
            let rewound_in_reach = (rdx * rdx + rdz * rdz).sqrt() <= max_reach;
            let active = if use_rewound {
                rewound_in_reach
            } else {
                present_in_reach
            };
            // A held due swing re-evaluates every tick; unanimous holds at
            // info would spam ~30 lines/s per held attacker. Flips and
            // evaluations that proceed carry the audit signal — those stay
            // info; unanimous holds drop to debug.
            let flip = rewound_in_reach != present_in_reach;
            let line = format!(
                "[LAG_COMP] auto_reach caster={} target={} strike={} rewound_ms={} source={} enabled={} present={} rewound={} flip={} signal=standing",
                short_identity(row.owner),
                short_identity(row.target),
                row.strike_id,
                pose.rewound_by_micros / 1_000,
                pose.source.as_str(),
                auto_swing_on,
                if present_in_reach { "in_reach" } else { "hold" },
                if rewound_in_reach { "in_reach" } else { "hold" },
                flip
            );
            if flip || active {
                log::info!("{}", line);
            } else {
                log::debug!("{}", line);
            }
            active
        } else {
            present_in_reach
        };
        if !active_in_reach {
            log::debug!(
                "[AUTO_ATTACK] owner={} target={} due_out_of_range dist={:.2} max={:.2} strike={}",
                short_identity(row.owner),
                short_identity(row.target),
                horiz_dist,
                max_reach,
                row.strike_id
            );
            mark_pending_due(ctx, &row);
            continue;
        }

        // LOS is a targeting rule (S4): a due swing against a target behind
        // cover holds like an out-of-range swing and resumes when LOS returns.
        // S9: the target endpoint rewinds with the same pose as the reach
        // gate; the caster endpoint stays present (§2.4 rule 4 in spirit).
        if gameplay.requires_target_los {
            let caster_snapshot = player_snapshot_for(ctx, row.owner);
            let present_blocked = caster_snapshot
                .as_ref()
                .map(|snapshot| !has_line_of_sight(ctx, snapshot, &target_snapshot))
                .unwrap_or(false);
            let active_blocked = if let (Some(caster_snapshot), Some(pose)) =
                (caster_snapshot.as_ref(), rewound_pose.as_ref())
            {
                let mut rewound_target = target_snapshot;
                rewound_target.pos_x = pose.pos_x;
                rewound_target.pos_y = pose.pos_y;
                rewound_target.pos_z = pose.pos_z;
                rewound_target.facing_yaw = pose.yaw;
                let rewound_blocked = !has_line_of_sight(ctx, caster_snapshot, &rewound_target);
                let active = if use_rewound {
                    rewound_blocked
                } else {
                    present_blocked
                };
                let flip = rewound_blocked != present_blocked;
                let line = format!(
                    "[LAG_COMP] auto_los caster={} target={} strike={} rewound_ms={} source={} enabled={} present={} rewound={} flip={} signal=standing",
                    short_identity(row.owner),
                    short_identity(row.target),
                    row.strike_id,
                    pose.rewound_by_micros / 1_000,
                    pose.source.as_str(),
                    auto_swing_on,
                    if present_blocked { "blocked" } else { "clear" },
                    if rewound_blocked { "blocked" } else { "clear" },
                    flip
                );
                // Same info/debug split as auto_reach: unanimous blocked
                // holds re-log every tick and carry no audit signal.
                if flip || !active {
                    log::info!("{}", line);
                } else {
                    log::debug!("{}", line);
                }
                active
            } else {
                present_blocked
            };
            if active_blocked {
                log::debug!(
                    "[AUTO_ATTACK] owner={} target={} due_los_blocked strike={}",
                    short_identity(row.owner),
                    short_identity(row.target),
                    row.strike_id
                );
                mark_pending_due(ctx, &row);
                continue;
            }
        }

        // S9 §2.2 step 1 (E3, one-timeline rule): the swing that fired
        // because of the rewound pose must also connect or whiff on that
        // frozen timeline. Stamping the press context in the tick's
        // transaction makes the melee chain gate and the impact freeze use
        // the same standing delay; a held swing leaves no stamp.
        if use_rewound {
            if let Some(signal) = standing.as_ref() {
                stamp_standing_press_context(ctx, row.owner, signal);
            }
        }

        let target_id = row.target.to_hex();
        log::info!(
            "[AUTO_ATTACK] owner={} target={} attempting_due_swing strike={} pending_due={} next_at_micros={}",
            short_identity(row.owner),
            short_identity(row.target),
            row.strike_id,
            row.pending_due,
            row.next_swing_at.to_micros_since_unix_epoch()
        );
        let attempted_replacement = take_pending_auto_attack_replacement(ctx, row.owner, now)
            .and_then(|pending| {
                attempt_auto_attack_replacement(
                    ctx,
                    &row,
                    &pending,
                    target_id.as_str(),
                    caster_phys.pos_x,
                    caster_phys.pos_y,
                    caster_phys.pos_z,
                    caster_phys.yaw,
                )
            });
        let dispatch = attempted_replacement.unwrap_or_else(|| {
            perform_intrinsic_auto_attack_for(
                ctx,
                row.owner,
                &authored_strike_id,
                target_id.as_str(),
                caster_phys.pos_x,
                caster_phys.pos_y,
                caster_phys.pos_z,
                caster_phys.yaw,
            )
        });

        match dispatch {
            Ok(MeleeAttackDispatch::Started) => {
                log::info!(
                    "[AUTO_ATTACK] owner={} target={} result=started strike={}",
                    short_identity(row.owner),
                    short_identity(row.target),
                    row.strike_id
                );
                schedule_auto_attack_from_swing_start(ctx, row.owner, row.target, now);
            }
            Ok(MeleeAttackDispatch::Queued | MeleeAttackDispatch::Rejected(_)) => {
                log::info!(
                    "[AUTO_ATTACK] owner={} target={} result=deferred strike={}",
                    short_identity(row.owner),
                    short_identity(row.target),
                    row.strike_id
                );
                mark_pending_due(ctx, &row);
            }
            Err(error) => {
                log::info!(
                    "[AUTO_ATTACK] owner={} target={} clear reason=intrinsic_error strike={} error={}",
                    short_identity(row.owner),
                    short_identity(row.target),
                    row.strike_id,
                    error
                );
                clear_auto_attack_for_owner(ctx, row.owner);
            }
        }
    }
}

fn prune_expired_pending_auto_attack_replacements(ctx: &ReducerContext, now: Timestamp) {
    let expired: Vec<_> = ctx
        .db
        .pending_auto_attack_replacement()
        .expires_at_micros()
        .filter(..=timestamp_to_micros(now))
        .map(|row| row.owner)
        .collect();
    for owner in expired {
        ctx.db
            .pending_auto_attack_replacement()
            .owner()
            .delete(owner);
    }
}

fn attempt_auto_attack_replacement(
    ctx: &ReducerContext,
    row: &AutoAttackState,
    pending: &PendingAutoAttackReplacement,
    target_id: &str,
    cast_pos_x: f32,
    cast_pos_y: f32,
    cast_pos_z: f32,
    cast_yaw: f32,
) -> Option<Result<MeleeAttackDispatch, String>> {
    let Some(ability) =
        active_selectable_ability_for_ability_id(ctx, row.owner, &pending.ability_id)
    else {
        log::info!(
            "[AUTO_ATTACK_REPLACEMENT] owner={} skipped reason=inactive_ability ability={} replacement={}",
            short_identity(row.owner),
            pending.ability_id,
            pending.replacement_id
        );
        return None;
    };
    if !ability
        .ability_kind
        .eq_ignore_ascii_case("AUTO_ATTACK_REPLACEMENT")
    {
        log::info!(
            "[AUTO_ATTACK_REPLACEMENT] owner={} skipped reason=wrong_ability_kind ability={} kind={}",
            short_identity(row.owner),
            ability.ability_id,
            ability.ability_kind
        );
        return None;
    }
    if ability.action_id != pending.replacement_id {
        log::info!(
            "[AUTO_ATTACK_REPLACEMENT] owner={} skipped reason=ability_retargeted ability={} action={} pending_replacement={}",
            short_identity(row.owner),
            ability.ability_id,
            ability.action_id,
            pending.replacement_id
        );
        return None;
    }

    let Some(replacement) = ctx
        .db
        .auto_attack_replacement_catalog()
        .replacement_id()
        .find(pending.replacement_id.clone())
    else {
        log::info!(
            "[AUTO_ATTACK_REPLACEMENT] owner={} skipped reason=missing_replacement ability={} replacement={}",
            short_identity(row.owner),
            pending.ability_id,
            pending.replacement_id
        );
        return None;
    };
    if !replacement
        .combat_profile_id
        .eq_ignore_ascii_case(row.combat_profile_id.as_str())
    {
        log::info!(
            "[AUTO_ATTACK_REPLACEMENT] owner={} skipped reason=profile_mismatch replacement={} row_profile={} replacement_profile={}",
            short_identity(row.owner),
            replacement.replacement_id,
            row.combat_profile_id,
            replacement.combat_profile_id
        );
        return None;
    }

    log::info!(
        "[AUTO_ATTACK_REPLACEMENT] owner={} target={} attempting replacement={} strike={}",
        short_identity(row.owner),
        short_identity(row.target),
        replacement.replacement_id,
        replacement.authored_melee_strike_id
    );
    match perform_intrinsic_auto_attack_replacement_for(
        ctx,
        row.owner,
        &ability,
        &replacement,
        target_id,
        cast_pos_x,
        cast_pos_y,
        cast_pos_z,
        cast_yaw,
    ) {
        Ok(MeleeAttackDispatch::Started) => Some(Ok(MeleeAttackDispatch::Started)),
        Ok(other) => {
            log::info!(
                "[AUTO_ATTACK_REPLACEMENT] owner={} target={} fallback_to_auto replacement={} result={:?}",
                short_identity(row.owner),
                short_identity(row.target),
                replacement.replacement_id,
                other
            );
            None
        }
        Err(error) => {
            log::info!(
                "[AUTO_ATTACK_REPLACEMENT] owner={} target={} fallback_to_auto replacement={} error={}",
                short_identity(row.owner),
                short_identity(row.target),
                replacement.replacement_id,
                error
            );
            None
        }
    }
}

fn resolved_auto_attack_runtime(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<ResolvedAutoAttackRuntime> {
    let combat_profile_id = derived_combat_profile_id_for_owner(ctx, owner)
        .filter(|profile| !profile.trim().is_empty())
        .unwrap_or_else(|| DEFAULT_COMBAT_PROFILE.to_string());
    let authored_strike_id = auto_attack_reference_for_profile(combat_profile_id.as_str())?;
    let authored_action_id = AuthoredActionId::new(authored_strike_id.as_str());
    let _definition =
        get_melee_definition_for_authored(ctx, combat_profile_id.as_str(), &authored_action_id)?;
    let mode_id = resolved_auto_attack_mode_for_owner(ctx, owner, combat_profile_id.as_str());
    let _gameplay = auto_attack_gameplay_for_profile_mode_action(
        ctx,
        combat_profile_id.as_str(),
        mode_id.as_str(),
        &authored_action_id,
    )?;
    Some(ResolvedAutoAttackRuntime {
        combat_profile_id,
        strike_id: authored_strike_id,
    })
}

fn resolve_live_target(ctx: &ReducerContext, owner: Identity, target_id: &str) -> Option<Identity> {
    if target_id.is_empty() {
        log::info!(
            "[AUTO_ATTACK] owner={} target_rejected reason=empty_target",
            short_identity(owner)
        );
        return None;
    }
    let target = match Identity::from_hex(target_id) {
        Ok(target) => target,
        Err(_) => {
            log::info!(
                "[AUTO_ATTACK] owner={} target_rejected reason=invalid_identity target_id={}",
                short_identity(owner),
                target_id
            );
            return None;
        }
    };
    if target == owner {
        log::info!(
            "[AUTO_ATTACK] owner={} target_rejected reason=self",
            short_identity(owner)
        );
        return None;
    }
    let Some(target_snapshot) = player_snapshot_for(ctx, target) else {
        log::info!(
            "[AUTO_ATTACK] owner={} target_rejected reason=missing_target_snapshot target={}",
            short_identity(owner),
            short_identity(target)
        );
        return None;
    };
    if !target_snapshot.alive {
        log::info!(
            "[AUTO_ATTACK] owner={} target_rejected reason=target_dead target={}",
            short_identity(owner),
            short_identity(target)
        );
        return None;
    }
    if !players_share_world_context(ctx, owner, target) {
        log::info!(
            "[AUTO_ATTACK] owner={} target_rejected reason=world_context target={}",
            short_identity(owner),
            short_identity(target)
        );
        return None;
    }
    if !can_harm(ctx, owner, target) {
        log::info!(
            "[AUTO_ATTACK] owner={} target_rejected reason=not_hostile target={} relation={:?}",
            short_identity(owner),
            short_identity(target),
            combat_relation(ctx, owner, target)
        );
        return None;
    }
    Some(target)
}

fn auto_attack_paused(ctx: &ReducerContext, owner: Identity, now: Timestamp) -> bool {
    has_active_disabling_status(ctx, owner, now)
        || is_defense_active(ctx, owner, now)
        || has_blocking_active_cast(ctx, owner)
        || has_active_dodge_action(ctx, owner)
}

fn has_blocking_active_cast(ctx: &ReducerContext, owner: Identity) -> bool {
    let Some(active_cast) = ctx.db.active_cast().caster().find(owner) else {
        return false;
    };
    if movement_delivery_for_action_id(active_cast.kind.as_str()).is_some() {
        return false;
    }
    true
}

fn has_active_dodge_action(ctx: &ReducerContext, owner: Identity) -> bool {
    let Some(action) = ctx.db.movement_action_state().owner().find(owner) else {
        return false;
    };
    MovementActionKind::from_wire(action.kind.as_str()) == Some(MovementActionKind::Dodge)
}

fn mark_pending_due(ctx: &ReducerContext, row: &AutoAttackState) {
    if row.pending_due {
        return;
    }

    upsert_auto_attack_state(
        ctx,
        AutoAttackState {
            pending_due: true,
            ..row.clone()
        },
    );
}

fn preserve_cadence_for_mode_change(
    ctx: &ReducerContext,
    mut row: AutoAttackState,
    current_mode: &str,
    gameplay: &AutoAttackCatalog,
    now: Timestamp,
) -> AutoAttackState {
    let attack_speed_multiplier =
        temporary_combat_modifiers(ctx, now).attack_speed_multiplier_for(&row.owner);
    let cadence = Duration::from_millis(scaled_auto_attack_cadence_ms(
        gameplay.cooldown_ms.max(1),
        attack_speed_multiplier,
    ));
    let (next_swing_at, due) = preserved_cadence_readiness(row.cadence_started_at, now, cadence);
    let movement_epoch = ctx
        .db
        .player_state()
        .player_id()
        .find(row.owner)
        .map(|state| state.voluntary_move_epoch)
        .unwrap_or(0);

    row.mode_id = current_mode.to_string();
    row.next_swing_at = next_swing_at;
    row.pending_due = due;
    row.movement_epoch_at_schedule = movement_epoch;
    log::info!(
        "[AUTO_ATTACK] owner={} target={} mode_changed profile={} mode={} strike={} cadence_ms={} elapsed_ms={} due={}",
        short_identity(row.owner),
        short_identity(row.target),
        row.combat_profile_id,
        row.mode_id,
        row.strike_id,
        cadence.as_millis(),
        elapsed_millis(row.cadence_started_at, now),
        row.pending_due
    );
    row
}

fn elapsed_millis(start: Timestamp, end: Timestamp) -> i64 {
    (end.to_micros_since_unix_epoch() - start.to_micros_since_unix_epoch()) / 1000
}

fn preserved_cadence_readiness(
    cadence_started_at: Timestamp,
    now: Timestamp,
    cadence: Duration,
) -> (Timestamp, bool) {
    let next_swing_at = cadence_started_at + cadence;
    (next_swing_at, now >= next_swing_at)
}

fn should_reset_cadence_for_voluntary_move(
    movement_policy: &str,
    current_epoch: u64,
    scheduled_epoch: u64,
) -> bool {
    movement_policy == AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE
        && current_epoch != scheduled_epoch
}

fn take_pending_auto_attack_replacement(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) -> Option<PendingAutoAttackReplacement> {
    let pending = ctx
        .db
        .pending_auto_attack_replacement()
        .owner()
        .find(owner)?;
    ctx.db
        .pending_auto_attack_replacement()
        .owner()
        .delete(owner);

    if now > pending.expires_at {
        log::info!(
            "[AUTO_ATTACK_REPLACEMENT] owner={} skipped reason=expired ability={} replacement={} expires_at_micros={}",
            short_identity(owner),
            pending.ability_id,
            pending.replacement_id,
            pending.expires_at.to_micros_since_unix_epoch()
        );
        return None;
    }

    Some(pending)
}

fn upsert_pending_auto_attack_replacement(ctx: &ReducerContext, row: PendingAutoAttackReplacement) {
    if ctx
        .db
        .pending_auto_attack_replacement()
        .owner()
        .find(row.owner)
        .is_some()
    {
        ctx.db.pending_auto_attack_replacement().owner().update(row);
    } else {
        ctx.db.pending_auto_attack_replacement().insert(row);
    }
}

fn upsert_auto_attack_state(ctx: &ReducerContext, row: AutoAttackState) {
    if ctx.db.auto_attack_state().owner().find(row.owner).is_some() {
        ctx.db.auto_attack_state().owner().update(row);
    } else {
        ctx.db.auto_attack_state().insert(row);
    }
}

fn timestamp_to_micros(timestamp: Timestamp) -> i64 {
    timestamp.to_micros_since_unix_epoch()
}

fn short_identity(identity: Identity) -> String {
    identity.to_hex().chars().take(8).collect()
}

struct ResolvedAutoAttackRuntime {
    combat_profile_id: String,
    strike_id: String,
}

#[cfg(test)]
mod tests {
    use super::{preserved_cadence_readiness, should_reset_cadence_for_voluntary_move};
    use crate::progression::{
        AUTO_ATTACK_MOVEMENT_ALLOW_MOVING, AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE,
    };
    use spacetimedb::Timestamp;
    use std::time::Duration;

    #[test]
    fn mode_switch_preserves_elapsed_cadence_and_can_be_due_immediately() {
        let started = Timestamp::UNIX_EPOCH;
        let now = started + Duration::from_millis(800);
        let (next, due) = preserved_cadence_readiness(started, now, Duration::from_millis(600));

        assert_eq!(next, started + Duration::from_millis(600));
        assert!(due);
    }

    #[test]
    fn mode_switch_to_longer_cadence_waits_only_for_remaining_time() {
        let started = Timestamp::UNIX_EPOCH;
        let now = started + Duration::from_millis(800);
        let (next, due) = preserved_cadence_readiness(started, now, Duration::from_millis(1000));

        assert_eq!(next, started + Duration::from_millis(1000));
        assert!(!due);
    }

    #[test]
    fn full_draw_policy_resets_cadence_when_voluntary_move_epoch_changes() {
        assert!(should_reset_cadence_for_voluntary_move(
            AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE,
            8,
            7
        ));
    }

    #[test]
    fn full_draw_policy_does_not_reset_without_epoch_change() {
        assert!(!should_reset_cadence_for_voluntary_move(
            AUTO_ATTACK_MOVEMENT_RESET_ON_VOLUNTARY_MOVE,
            7,
            7
        ));
    }

    #[test]
    fn short_draw_policy_ignores_voluntary_move_epoch_changes() {
        assert!(!should_reset_cadence_for_voluntary_move(
            AUTO_ATTACK_MOVEMENT_ALLOW_MOVING,
            8,
            7
        ));
    }
}

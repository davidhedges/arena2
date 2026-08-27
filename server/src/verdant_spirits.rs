use std::collections::{HashMap, HashSet};
use std::time::Duration;

use spacetimedb::{Identity, ReducerContext, Table, Timestamp};

use crate::arena::players_share_world_context;
use crate::combat::StatusEffect;
use crate::progression::player_build_contains_active_ability;
use crate::relations::{target_audience_allows, TargetAudience};

#[allow(unused_imports)]
use crate::combat::status_effect as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;

pub(crate) const ACTION_ID: &str = "VERDANT_SPIRITS";
pub(crate) const ABILITY_ID: &str = "SPELL_VERDANT_SPIRITS";
pub(crate) const STATUS_KIND: &str = "VERDANT_SPIRITS";
const STATUS_STACK_GROUP: &str = "VERDANT_SPIRITS";
const TOTAL_SPIRITS: u32 = 2;
const BESTOWED_HEAL_PER_SPIRIT: i32 = 1;
const BESTOWED_HEAL_INTERVAL: Duration = Duration::from_secs(1);
const STATUS_LIFETIME: Duration = Duration::from_secs(365 * 24 * 60 * 60);

/// Reconciles the passive allocation onto the existing subscribed status table.
/// One source-owned row per holder carries either one or two visual stacks.
pub(crate) fn reconcile_all(ctx: &ReducerContext, now: Timestamp) {
    let mut origins: Vec<Identity> = ctx
        .db
        .player_state()
        .alive()
        .filter(true)
        .filter(|state| {
            !state.is_dummy
                && player_build_contains_active_ability(ctx, state.player_id, ABILITY_ID)
        })
        .map(|state| state.player_id)
        .collect();
    origins.sort_by_key(|identity| identity.to_hex().to_string());
    let eligible_origins: HashSet<Identity> = origins.iter().copied().collect();

    let stale_ids: Vec<u64> = ctx
        .db
        .status_effect()
        .effect_kind()
        .filter(STATUS_KIND)
        .filter(|effect| !eligible_origins.contains(&effect.source))
        .map(|effect| effect.status_id)
        .collect();
    for status_id in stale_ids {
        ctx.db.status_effect().status_id().delete(status_id);
    }

    for origin in origins {
        reconcile_origin(ctx, origin, now);
    }
}

pub(crate) fn bestow(
    ctx: &ReducerContext,
    origin: Identity,
    target: Identity,
    now: Timestamp,
) -> Result<(), String> {
    if origin == target {
        return Err("Verdant Spirits can only be bestowed on another player".to_string());
    }

    let Some(origin_state) = ctx.db.player_state().player_id().find(origin) else {
        return Err("Verdant Spirits origin does not exist".to_string());
    };
    if !origin_state.alive
        || origin_state.is_dummy
        || !player_build_contains_active_ability(ctx, origin, ABILITY_ID)
    {
        return Err("Verdant Spirits is not active for this player".to_string());
    }

    let Some(target_state) = ctx.db.player_state().player_id().find(target) else {
        return Err("Verdant Spirits can only be bestowed on a player".to_string());
    };
    // Roster-backed bot allies use dummy PlayerState rows, but they are still
    // first-class friendly targets for spell authoring and match simulation.
    // Relationship validation below keeps hostile dummies ineligible.
    if !can_hold_spirits(target_state.alive, target_state.is_dummy, false) {
        return Err("Verdant Spirits requires a living player target".to_string());
    }
    if !players_share_world_context(ctx, origin, target)
        || !target_audience_allows(ctx, origin, target, TargetAudience::Assistable)
    {
        return Err("Verdant Spirits target is not assistable".to_string());
    }

    reconcile_origin(ctx, origin, now);

    let Some(mut source_row) = find_holder_row(ctx, origin, origin) else {
        return Err("No Verdant Spirit remains to bestow".to_string());
    };
    let target_stacks = find_holder_row(ctx, origin, target)
        .map(|row| row.stacks)
        .unwrap_or(0);
    let (source_stacks, target_stacks) = transfer_one(source_row.stacks, target_stacks)?;

    source_row.stacks = source_stacks;
    if source_stacks == 0 {
        ctx.db
            .status_effect()
            .status_id()
            .delete(source_row.status_id);
    } else {
        source_row.applied_at = now;
        ctx.db.status_effect().status_id().update(source_row);
    }
    upsert_holder(ctx, origin, target, target_stacks, now);
    Ok(())
}

pub(crate) fn clear_for_origin(ctx: &ReducerContext, origin: Identity) {
    let status_ids: Vec<u64> = ctx
        .db
        .status_effect()
        .source()
        .filter(origin)
        .filter(|effect| effect.effect_kind == STATUS_KIND)
        .map(|effect| effect.status_id)
        .collect();
    for status_id in status_ids {
        ctx.db.status_effect().status_id().delete(status_id);
    }
}

pub(crate) fn origin_is_active(ctx: &ReducerContext, origin: Identity) -> bool {
    ctx.db
        .player_state()
        .player_id()
        .find(origin)
        .is_some_and(|state| {
            state.alive
                && !state.is_dummy
                && player_build_contains_active_ability(ctx, origin, ABILITY_ID)
        })
}

fn reconcile_origin(ctx: &ReducerContext, origin: Identity, now: Timestamp) {
    let mut grouped: HashMap<Identity, Vec<StatusEffect>> = HashMap::new();
    for effect in ctx
        .db
        .status_effect()
        .source()
        .filter(origin)
        .filter(|effect| effect.effect_kind == STATUS_KIND)
    {
        if holder_is_valid(ctx, origin, effect.target) {
            grouped.entry(effect.target).or_default().push(effect);
        } else {
            ctx.db.status_effect().status_id().delete(effect.status_id);
        }
    }

    let mut groups: Vec<(Identity, Vec<StatusEffect>)> = grouped.into_iter().collect();
    groups.sort_by_key(|(_, rows)| rows.iter().map(|row| row.status_id).min().unwrap_or(0));

    let mut remaining = TOTAL_SPIRITS;
    let mut allocated_to_origin = 0;
    for (holder, mut rows) in groups {
        rows.sort_by_key(|row| row.status_id);
        let requested = rows
            .iter()
            .fold(0_u32, |total, row| total.saturating_add(row.stacks));
        let allocated = requested.min(remaining);
        remaining -= allocated;

        let mut keeper = rows.remove(0);
        for duplicate in rows {
            ctx.db
                .status_effect()
                .status_id()
                .delete(duplicate.status_id);
        }

        if allocated == 0 {
            ctx.db.status_effect().status_id().delete(keeper.status_id);
        } else {
            let is_bestowed = holder != origin;
            if keeper.stacks != allocated
                || keeper.max_stacks != TOTAL_SPIRITS
                || !periodic_heal_is_configured(&keeper, is_bestowed)
            {
                keeper.stacks = allocated;
                keeper.max_stacks = TOTAL_SPIRITS;
                keeper.applied_at = now;
                configure_periodic_heal(&mut keeper, is_bestowed, now);
                ctx.db.status_effect().status_id().update(keeper);
            }
            if holder == origin {
                allocated_to_origin = allocated;
            }
        }
    }

    if remaining > 0 {
        upsert_holder(ctx, origin, origin, allocated_to_origin + remaining, now);
    }
}

fn holder_is_valid(ctx: &ReducerContext, origin: Identity, holder: Identity) -> bool {
    let Some(state) = ctx.db.player_state().player_id().find(holder) else {
        return false;
    };
    can_hold_spirits(state.alive, state.is_dummy, holder == origin)
        && players_share_world_context(ctx, origin, holder)
        && target_audience_allows(ctx, origin, holder, TargetAudience::Assistable)
}

fn can_hold_spirits(alive: bool, is_dummy: bool, is_origin: bool) -> bool {
    alive && (!is_origin || !is_dummy)
}

fn find_holder_row(
    ctx: &ReducerContext,
    origin: Identity,
    holder: Identity,
) -> Option<StatusEffect> {
    ctx.db
        .status_effect()
        .source()
        .filter(origin)
        .filter(|effect| effect.effect_kind == STATUS_KIND && effect.target == holder)
        .min_by_key(|effect| effect.status_id)
}

fn upsert_holder(
    ctx: &ReducerContext,
    origin: Identity,
    holder: Identity,
    stacks: u32,
    now: Timestamp,
) {
    let stacks = stacks.min(TOTAL_SPIRITS);
    if stacks == 0 {
        return;
    }

    if let Some(mut existing) = find_holder_row(ctx, origin, holder) {
        existing.stacks = stacks;
        existing.max_stacks = TOTAL_SPIRITS;
        existing.applied_at = now;
        configure_periodic_heal(&mut existing, origin != holder, now);
        ctx.db.status_effect().status_id().update(existing);
        return;
    }

    let expires_at = now + STATUS_LIFETIME;
    let mut effect = StatusEffect {
        status_id: 0,
        target: holder,
        source: origin,
        effect_kind: STATUS_KIND.to_string(),
        polarity: "BUFF".to_string(),
        stack_group: STATUS_STACK_GROUP.to_string(),
        stacks,
        max_stacks: TOTAL_SPIRITS,
        stack_policy: "ADD_STACK_REFRESH".to_string(),
        applied_at: now,
        base_duration_ms: STATUS_LIFETIME.as_millis().min(u128::from(u64::MAX)) as u64,
        expires_at,
        expires_at_micros: crate::combat::timestamp_to_micros(expires_at),
        slow_pct: 0.0,
        tick_amount: 0,
        tick_interval_ms: 0,
        damage_type: "PHYSICAL".to_string(),
        modifier_scalar: 0.0,
        absorb_amount: 0,
        absorb_cap: 0,
        dispel_types: String::new(),
        next_tick_at: now,
        next_tick_at_micros: crate::combat::timestamp_to_micros(now),
        spell_id: ACTION_ID.to_string(),
    };
    configure_periodic_heal(&mut effect, origin != holder, now);
    ctx.db.status_effect().insert(effect);
}

fn configure_periodic_heal(effect: &mut StatusEffect, is_bestowed: bool, now: Timestamp) {
    let (tick_amount, tick_interval_ms) = periodic_heal_spec(is_bestowed);
    if tick_interval_ms == 0 {
        effect.tick_amount = tick_amount;
        effect.tick_interval_ms = tick_interval_ms;
        effect.next_tick_at = now;
        effect.next_tick_at_micros = crate::combat::timestamp_to_micros(now);
        return;
    }

    let needs_new_schedule =
        effect.tick_amount != tick_amount || effect.tick_interval_ms != tick_interval_ms;
    effect.tick_amount = tick_amount;
    effect.tick_interval_ms = tick_interval_ms;
    if needs_new_schedule {
        effect.next_tick_at = now + BESTOWED_HEAL_INTERVAL;
        effect.next_tick_at_micros = crate::combat::timestamp_to_micros(effect.next_tick_at);
    }
}

fn periodic_heal_spec(is_bestowed: bool) -> (i32, u64) {
    if is_bestowed {
        (
            BESTOWED_HEAL_PER_SPIRIT,
            BESTOWED_HEAL_INTERVAL.as_millis() as u64,
        )
    } else {
        (0, 0)
    }
}

fn periodic_heal_is_configured(effect: &StatusEffect, is_bestowed: bool) -> bool {
    if !is_bestowed {
        return effect.tick_amount == 0 && effect.tick_interval_ms == 0;
    }

    effect.tick_amount == BESTOWED_HEAL_PER_SPIRIT
        && effect.tick_interval_ms == BESTOWED_HEAL_INTERVAL.as_millis() as u64
}

pub(crate) fn periodic_heal_amount(
    source: Identity,
    target: Identity,
    tick_amount: i32,
    stacks: u32,
) -> i32 {
    if source == target {
        return 0;
    }

    tick_amount
        .max(0)
        .saturating_mul(stacks.max(1).min(TOTAL_SPIRITS) as i32)
}

fn transfer_one(source_stacks: u32, target_stacks: u32) -> Result<(u32, u32), String> {
    if source_stacks == 0 {
        return Err("No Verdant Spirit remains to bestow".to_string());
    }
    if target_stacks >= TOTAL_SPIRITS {
        return Err("That player already holds both Verdant Spirits".to_string());
    }

    Ok((source_stacks - 1, target_stacks + 1))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn verdant_spirits_contract_keeps_exactly_two_source_owned_stacks() {
        assert_eq!(TOTAL_SPIRITS, 2);
        assert_eq!(ABILITY_ID, "SPELL_VERDANT_SPIRITS");
        assert_eq!(ACTION_ID, STATUS_KIND);
    }

    #[test]
    fn bestow_moves_exactly_one_spirit_and_can_stack_both_on_one_target() {
        assert_eq!(transfer_one(2, 0), Ok((1, 1)));
        assert_eq!(transfer_one(1, 1), Ok((0, 2)));
    }

    #[test]
    fn bestow_supports_splitting_and_rejects_empty_or_full_allocations() {
        let (source, first_target) = transfer_one(2, 0).expect("first bestow");
        let (source, second_target) = transfer_one(source, 0).expect("split bestow");
        assert_eq!((source, first_target, second_target), (0, 1, 1));
        assert!(transfer_one(0, 0).is_err());
        assert!(transfer_one(1, 2).is_err());
    }

    #[test]
    fn living_roster_bot_allies_can_hold_spirits_but_cannot_originate_them() {
        assert!(can_hold_spirits(true, true, false));
        assert!(!can_hold_spirits(false, true, false));
        assert!(!can_hold_spirits(true, true, true));
        assert!(can_hold_spirits(true, false, true));
    }

    #[test]
    fn bestowed_spirits_heal_one_health_per_stack_each_second() {
        let origin = Identity::from_hex(&format!("{:064x}", 1)).expect("origin identity");
        let holder = Identity::from_hex(&format!("{:064x}", 2)).expect("holder identity");

        assert_eq!(BESTOWED_HEAL_INTERVAL, Duration::from_secs(1));
        assert_eq!(periodic_heal_spec(false), (0, 0));
        assert_eq!(periodic_heal_spec(true), (1, 1_000));
        assert_eq!(periodic_heal_amount(origin, holder, 1, 1), 1);
        assert_eq!(periodic_heal_amount(origin, holder, 1, 2), 2);
        assert_eq!(periodic_heal_amount(origin, holder, 1, 3), 2);
        assert_eq!(periodic_heal_amount(origin, origin, 1, 2), 0);
    }
}

use spacetimedb::{table, Identity, ReducerContext, Table, Timestamp};

use crate::combat::is_in_combat;
use crate::inventory::equipment_modifier_totals_for_owner;
use crate::progression::{
    active_stat_totals_for_owner, effective_resource_kind_for_ability, AbilityCatalog,
};

#[allow(unused_imports)]
use crate::player_state::player_state as _;
#[allow(unused_imports)]
use crate::progression::resource_catalog as _;
#[allow(unused_imports)]
use crate::resources::player_resource as _;

#[table(accessor = player_resource, public)]
#[derive(Clone)]
pub struct PlayerResource {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    pub kind: String,
    pub current: f32,
    pub max: f32,
    pub regen_per_second: f32,
    pub updated_at: Timestamp,
}

pub(crate) const RESOURCE_KIND_STAMINA: &str = "STAMINA";
pub(crate) const RESOURCE_KIND_MANA: &str = "MANA";
const STANDARD_RESOURCE_KINDS: [&str; 2] = [RESOURCE_KIND_STAMINA, RESOURCE_KIND_MANA];

#[derive(Clone, Debug)]
struct ResolvedResourceSpec {
    kind: String,
    max: f32,
    regen_per_second: f32,
    flat_decay_per_second: f32,
    out_of_combat_flat_decay_per_second: f32,
    decay_per_current_point_per_second: f32,
    gain_per_damage_taken: f32,
    gain_per_damage_dealt: f32,
    gain_per_melee_hit: f32,
    gain_per_spell_cast: f32,
    starts_full: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct ResolvedActionResourceCost {
    pub kind: String,
    pub amount: f32,
}

impl ResolvedActionResourceCost {
    pub(crate) fn primary(amount: f32) -> Self {
        Self::stamina(amount)
    }

    pub(crate) fn stamina(amount: f32) -> Self {
        Self::for_kind(RESOURCE_KIND_STAMINA, amount)
    }

    pub(crate) fn mana(amount: f32) -> Self {
        Self::for_kind(RESOURCE_KIND_MANA, amount)
    }

    pub(crate) fn for_kind(kind: &str, amount: f32) -> Self {
        Self {
            kind: normalize_resource_kind(kind),
            amount: amount.max(0.0),
        }
    }

    pub(crate) fn is_free(&self) -> bool {
        self.amount <= 0.0001
    }
}

pub(crate) fn resolve_ability_action_resource_cost(
    ctx: &ReducerContext,
    owner: Identity,
    ability: &AbilityCatalog,
) -> Option<ResolvedActionResourceCost> {
    resolve_ability_action_resource_cost_amount(ctx, owner, ability, ability.resource_cost)
}

pub(crate) fn resolve_ability_action_resource_cost_amount(
    ctx: &ReducerContext,
    owner: Identity,
    ability: &AbilityCatalog,
    amount: f32,
) -> Option<ResolvedActionResourceCost> {
    let amount = amount.max(0.0);
    if amount <= 0.0 {
        return Some(ResolvedActionResourceCost::primary(0.0));
    }

    let ability_resource_kind = effective_resource_kind_for_ability(ctx, owner, ability)?;
    if ability.ability_kind.eq_ignore_ascii_case("SPELL") {
        return Some(ResolvedActionResourceCost::for_kind(
            RESOURCE_KIND_MANA,
            amount,
        ));
    }

    Some(ResolvedActionResourceCost::for_kind(
        ability_resource_kind.as_str(),
        amount,
    ))
}

pub(crate) fn can_pay_action_resource_cost(
    ctx: &ReducerContext,
    owner: Identity,
    cost: &ResolvedActionResourceCost,
    now: Timestamp,
) -> bool {
    cost.is_free() || has_resource_at_least(ctx, owner, cost.kind.as_str(), now, cost.amount)
}

pub(crate) fn pay_action_resource_cost(
    ctx: &ReducerContext,
    owner: Identity,
    cost: &ResolvedActionResourceCost,
    now: Timestamp,
) -> bool {
    cost.is_free() || spend_resource(ctx, owner, cost.kind.as_str(), cost.amount, now)
}

pub(crate) fn sync_all_player_resources(ctx: &ReducerContext, now: Timestamp) {
    let owners: Vec<Identity> = ctx
        .db
        .player_state()
        .iter()
        .map(|row| row.player_id)
        .collect();
    for owner in owners {
        sync_resources_for_player(ctx, owner, now);
    }
}

pub(crate) fn clear_player_resources(ctx: &ReducerContext, owner: Identity) {
    let keys: Vec<String> = ctx
        .db
        .player_resource()
        .owner()
        .filter(owner)
        .map(|row| row.key)
        .collect();
    for key in keys {
        ctx.db.player_resource().key().delete(key);
    }
}

pub(crate) fn reset_player_resources_to_full(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    sync_resources_for_player(ctx, owner, now);
    for kind in STANDARD_RESOURCE_KINDS {
        let Some(spec) = resolve_resource_spec_for_owner_and_kind(ctx, owner, kind) else {
            continue;
        };
        let key = resource_key(owner, spec.kind.as_str());
        let Some(mut primary) = ctx.db.player_resource().key().find(key) else {
            continue;
        };
        let target_current = baseline_current_for_spec(&spec);
        if (primary.current - target_current).abs() > 0.0001 {
            primary.current = target_current;
            primary.updated_at = now;
            ctx.db.player_resource().key().update(primary);
        }
    }
}

pub(crate) fn sync_primary_resource_for_player(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) -> Option<PlayerResource> {
    sync_resources_for_player(ctx, owner, now);
    ctx.db
        .player_resource()
        .key()
        .find(resource_key(owner, RESOURCE_KIND_STAMINA))
}

pub(crate) fn sync_resources_for_player(ctx: &ReducerContext, owner: Identity, now: Timestamp) {
    for kind in STANDARD_RESOURCE_KINDS {
        sync_resource_for_player(ctx, owner, kind, now);
    }
}

fn sync_resource_for_player(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
    now: Timestamp,
) -> Option<PlayerResource> {
    let spec = resolve_resource_spec_for_owner_and_kind(ctx, owner, resource_kind)?;
    let key = resource_key(owner, spec.kind.as_str());
    let mut resource = if let Some(existing) = ctx.db.player_resource().key().find(key.clone()) {
        existing
    } else {
        let initial_current = baseline_current_for_spec(&spec);
        let row = PlayerResource {
            key,
            owner,
            kind: spec.kind.clone(),
            current: initial_current,
            max: spec.max,
            regen_per_second: spec.regen_per_second,
            updated_at: now,
        };
        ctx.db.player_resource().insert(row.clone());
        row
    };

    let next_current = resource.current.clamp(0.0, spec.max.max(0.0));
    if (resource.max - spec.max).abs() <= 0.0001
        && (resource.regen_per_second - spec.regen_per_second).abs() <= 0.0001
        && (resource.current - next_current).abs() <= 0.0001
    {
        return Some(resource);
    }

    resource.max = spec.max.max(0.0);
    resource.regen_per_second = spec.regen_per_second.max(0.0);
    resource.current = next_current;
    resource.updated_at = now;
    ctx.db.player_resource().key().update(resource.clone());
    Some(resource)
}

pub(crate) fn tick_primary_resource_for_player(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
    dt_seconds: f32,
) {
    sync_resources_for_player(ctx, owner, now);
    let resources: Vec<_> = ctx.db.player_resource().owner().filter(owner).collect();
    for resource in resources {
        if !standard_resource_kind(resource.kind.as_str()) {
            continue;
        }
        let Some(spec) =
            resolve_resource_spec_for_owner_and_kind(ctx, owner, resource.kind.as_str())
        else {
            continue;
        };
        tick_resource_row(ctx, owner, now, dt_seconds, resource, spec);
    }
}

pub(crate) fn grant_primary_resource_for_damage_taken(
    ctx: &ReducerContext,
    owner: Identity,
    amount: i32,
    now: Timestamp,
) {
    let amount = amount.max(0) as f32;
    if amount <= 0.0 {
        return;
    }
    let Some(spec) = resolve_resource_spec_for_owner_and_kind(ctx, owner, RESOURCE_KIND_STAMINA)
    else {
        return;
    };
    if spec.gain_per_damage_taken <= 0.0 {
        return;
    }
    let Some(mut resource) = sync_resource_for_player(ctx, owner, RESOURCE_KIND_STAMINA, now)
    else {
        return;
    };

    let next = (resource.current + amount * spec.gain_per_damage_taken).clamp(0.0, resource.max);
    if (next - resource.current).abs() <= 0.0001 {
        return;
    }

    resource.current = next;
    resource.updated_at = now;
    ctx.db.player_resource().key().update(resource);
}

pub(crate) fn grant_primary_resource_for_damage_dealt(
    ctx: &ReducerContext,
    owner: Identity,
    amount: i32,
    now: Timestamp,
) {
    let amount = amount.max(0) as f32;
    if amount <= 0.0 {
        return;
    }
    let Some(spec) = resolve_resource_spec_for_owner_and_kind(ctx, owner, RESOURCE_KIND_STAMINA)
    else {
        return;
    };
    if spec.gain_per_damage_dealt <= 0.0 {
        return;
    }
    let Some(mut resource) = sync_resource_for_player(ctx, owner, RESOURCE_KIND_STAMINA, now)
    else {
        return;
    };

    let next = (resource.current + amount * spec.gain_per_damage_dealt).clamp(0.0, resource.max);
    if (next - resource.current).abs() <= 0.0001 {
        return;
    }

    resource.current = next;
    resource.updated_at = now;
    ctx.db.player_resource().key().update(resource);
}

pub(crate) fn grant_primary_resource_amount_for_kind(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
    amount: f32,
    now: Timestamp,
) {
    let amount = amount.max(0.0);
    if amount <= 0.0 {
        return;
    }
    if !standard_resource_kind(resource_kind) {
        return;
    }
    let Some(mut resource) = sync_resource_for_player(ctx, owner, resource_kind, now) else {
        return;
    };

    let next = (resource.current + amount).clamp(0.0, resource.max);
    if (next - resource.current).abs() <= 0.0001 {
        return;
    }

    resource.current = next;
    resource.updated_at = now;
    ctx.db.player_resource().key().update(resource);
}

#[allow(dead_code)]
pub(crate) fn grant_primary_resource_for_melee_hit(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    let Some(spec) = resolve_resource_spec_for_owner_and_kind(ctx, owner, RESOURCE_KIND_STAMINA)
    else {
        return;
    };
    if spec.gain_per_melee_hit <= 0.0 {
        return;
    }
    let Some(mut resource) = sync_resource_for_player(ctx, owner, RESOURCE_KIND_STAMINA, now)
    else {
        return;
    };
    let next = (resource.current + spec.gain_per_melee_hit).clamp(0.0, resource.max);
    if (next - resource.current).abs() <= 0.0001 {
        return;
    }
    resource.current = next;
    resource.updated_at = now;
    ctx.db.player_resource().key().update(resource);
}

#[allow(dead_code)]
pub(crate) fn grant_primary_resource_for_spell_cast(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    let Some(spec) = resolve_resource_spec_for_owner_and_kind(ctx, owner, RESOURCE_KIND_MANA)
    else {
        return;
    };
    if spec.gain_per_spell_cast <= 0.0 {
        return;
    }
    let Some(mut resource) = sync_resource_for_player(ctx, owner, RESOURCE_KIND_MANA, now) else {
        return;
    };
    let next = (resource.current + spec.gain_per_spell_cast).clamp(0.0, resource.max);
    if (next - resource.current).abs() <= 0.0001 {
        return;
    }
    resource.current = next;
    resource.updated_at = now;
    ctx.db.player_resource().key().update(resource);
}

pub(crate) fn grant_primary_resource_amount(
    ctx: &ReducerContext,
    owner: Identity,
    amount: f32,
    now: Timestamp,
) {
    if amount <= 0.0 {
        return;
    }
    let Some(mut resource) = sync_resource_for_player(ctx, owner, RESOURCE_KIND_STAMINA, now)
    else {
        return;
    };
    let next = (resource.current + amount).clamp(0.0, resource.max);
    if (next - resource.current).abs() <= 0.0001 {
        return;
    }
    resource.current = next;
    resource.updated_at = now;
    ctx.db.player_resource().key().update(resource);
}

#[allow(dead_code)]
pub(crate) fn current_primary_resource(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) -> f32 {
    current_resource(ctx, owner, RESOURCE_KIND_STAMINA, now)
}

#[allow(dead_code)]
pub(crate) fn has_primary_resource_at_least(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
    minimum: f32,
) -> bool {
    has_resource_at_least(ctx, owner, RESOURCE_KIND_STAMINA, now, minimum)
}

#[allow(dead_code)]
pub(crate) fn spend_primary_resource(
    ctx: &ReducerContext,
    owner: Identity,
    amount: f32,
    now: Timestamp,
) -> bool {
    spend_resource(ctx, owner, RESOURCE_KIND_STAMINA, amount, now)
}

fn current_resource(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
    now: Timestamp,
) -> f32 {
    sync_resource_for_player(ctx, owner, resource_kind, now)
        .map(|row| row.current.max(0.0))
        .unwrap_or(0.0)
}

fn has_resource_at_least(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
    now: Timestamp,
    minimum: f32,
) -> bool {
    current_resource(ctx, owner, resource_kind, now) + 0.0001 >= minimum.max(0.0)
}

fn spend_resource(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
    amount: f32,
    now: Timestamp,
) -> bool {
    let cost = amount.max(0.0);
    if cost <= 0.0 {
        return true;
    }

    let Some(mut resource) = sync_resource_for_player(ctx, owner, resource_kind, now) else {
        return false;
    };
    if resource.current + 0.0001 < cost {
        return false;
    }

    let next = (resource.current - cost).clamp(0.0, resource.max.max(0.0));
    if (next - resource.current).abs() > 0.0001 {
        resource.current = next;
        resource.updated_at = now;
        ctx.db.player_resource().key().update(resource);
    }
    true
}

#[allow(dead_code)]
fn resolve_primary_resource_spec_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<ResolvedResourceSpec> {
    resolve_resource_spec_for_owner_and_kind(ctx, owner, RESOURCE_KIND_STAMINA)
}

fn resolve_resource_spec_for_owner_and_kind(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
) -> Option<ResolvedResourceSpec> {
    let primary_kind = resource_kind.trim().to_ascii_uppercase();
    if primary_kind.is_empty() {
        return None;
    }
    let definition = ctx
        .db
        .resource_catalog()
        .resource_kind()
        .find(primary_kind.clone())?;
    let insight = active_stat_totals_for_owner(ctx, owner).insight as f32;
    let gain_multiplier = (1.0 + definition.gain_multiplier_per_insight * insight).max(0.0);
    let equipment_mana_regen = if primary_kind.eq_ignore_ascii_case("MANA") {
        equipment_modifier_totals_for_owner(ctx, owner).mana_regen_per_second
    } else {
        0.0
    };
    Some(ResolvedResourceSpec {
        kind: primary_kind,
        max: (definition.base_max + definition.max_per_insight * insight).max(0.0),
        regen_per_second: (definition.base_regen_per_second
            + definition.regen_per_insight * insight
            + equipment_mana_regen)
            .max(0.0),
        flat_decay_per_second: definition.flat_decay_per_second.max(0.0),
        out_of_combat_flat_decay_per_second: definition
            .out_of_combat_flat_decay_per_second
            .max(0.0),
        decay_per_current_point_per_second: definition.decay_per_current_point_per_second.max(0.0),
        gain_per_damage_taken: (definition.gain_per_damage_taken * gain_multiplier).max(0.0),
        gain_per_damage_dealt: (definition.gain_per_damage_dealt * gain_multiplier).max(0.0),
        gain_per_melee_hit: (definition.gain_per_melee_hit * gain_multiplier).max(0.0),
        gain_per_spell_cast: (definition.gain_per_spell_cast * gain_multiplier).max(0.0),
        starts_full: definition.starts_full,
    })
}

fn tick_resource_row(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
    dt_seconds: f32,
    mut resource: PlayerResource,
    spec: ResolvedResourceSpec,
) {
    let dt = dt_seconds.max(0.0);
    let max = spec.max.max(0.0);
    let out_of_combat = !is_in_combat(ctx, owner, now);
    let regen_per_second = spec.regen_per_second.max(0.0);
    let decay_per_second =
        primary_resource_decay_per_second(&spec, resource.current, out_of_combat);
    let next_current =
        (resource.current + (regen_per_second - decay_per_second) * dt).clamp(0.0, max);

    let spec_changed = (resource.max - max).abs() > 0.0001
        || (resource.regen_per_second - spec.regen_per_second.max(0.0)).abs() > 0.0001;
    let current_changed = (resource.current - next_current).abs() > 0.0001;
    if !spec_changed && !current_changed {
        return;
    }

    resource.max = max;
    resource.regen_per_second = spec.regen_per_second.max(0.0);
    resource.current = next_current;
    resource.updated_at = now;
    ctx.db.player_resource().key().update(resource);
}

fn baseline_current_for_spec(spec: &ResolvedResourceSpec) -> f32 {
    if spec.starts_full {
        spec.max.max(0.0)
    } else {
        0.0
    }
}

fn primary_resource_decay_per_second(
    spec: &ResolvedResourceSpec,
    current: f32,
    out_of_combat: bool,
) -> f32 {
    let mut decay_per_second =
        spec.flat_decay_per_second + current.max(0.0) * spec.decay_per_current_point_per_second;
    if out_of_combat {
        decay_per_second += spec.out_of_combat_flat_decay_per_second;
    }
    decay_per_second.max(0.0)
}

fn resource_key(owner: Identity, kind: &str) -> String {
    format!("{}:{}", owner.to_hex(), kind.trim().to_ascii_uppercase())
}

fn normalize_resource_kind(kind: &str) -> String {
    kind.trim().to_ascii_uppercase()
}

fn standard_resource_kind(kind: &str) -> bool {
    let kind = normalize_resource_kind(kind);
    STANDARD_RESOURCE_KINDS
        .iter()
        .any(|candidate| *candidate == kind)
}

#[cfg(test)]
mod tests {
    use super::{primary_resource_decay_per_second, ResolvedResourceSpec};

    fn resource_spec(
        flat_decay_per_second: f32,
        out_of_combat_flat_decay_per_second: f32,
        decay_per_current_point_per_second: f32,
    ) -> ResolvedResourceSpec {
        ResolvedResourceSpec {
            kind: "TEST".to_string(),
            max: 100.0,
            regen_per_second: 0.0,
            flat_decay_per_second,
            out_of_combat_flat_decay_per_second,
            decay_per_current_point_per_second,
            gain_per_damage_taken: 0.0,
            gain_per_damage_dealt: 0.0,
            gain_per_melee_hit: 0.0,
            gain_per_spell_cast: 0.0,
            starts_full: false,
        }
    }

    #[test]
    fn out_of_combat_decay_applies_only_outside_combat() {
        let spec = resource_spec(0.0, 2.0, 0.0);

        assert_eq!(primary_resource_decay_per_second(&spec, 40.0, false), 0.0);
        assert_eq!(primary_resource_decay_per_second(&spec, 40.0, true), 2.0);
    }

    #[test]
    fn out_of_combat_decay_stacks_with_existing_decay_modes() {
        let spec = resource_spec(1.0, 2.0, 0.025);

        assert_eq!(primary_resource_decay_per_second(&spec, 40.0, false), 2.0);
        assert_eq!(primary_resource_decay_per_second(&spec, 40.0, true), 4.0);
    }
}

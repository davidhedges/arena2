use spacetimedb::{table, Identity, ReducerContext, Table, Timestamp};

use crate::action_ids::AuthoredActionId;
use crate::combat::{is_in_combat, temporary_combat_modifiers, TemporaryCombatModifiers};
use crate::inventory::{equipment_modifier_totals_for_owner, EquipmentModifierTotals};
use crate::melee::{auto_attack_gameplay_for_profile_mode_action, scaled_auto_attack_cadence_ms};
use crate::progression::{
    derived_combat_discipline_id_for_owner, effective_resource_kind_for_ability,
    player_has_selected_passive_ability, resolved_auto_attack_mode_for_owner, AbilityCatalog,
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
const FIGHTING_SPIRIT_ABILITY_ID: &str = "DAGGER_FIGHTING_SPIRIT";
const FIGHTING_SPIRIT_STAMINA_PER_SECOND_OF_CADENCE: f32 = 4.0;
const STANDARD_RESOURCE_KINDS: [&str; 2] = [RESOURCE_KIND_STAMINA, RESOURCE_KIND_MANA];

#[derive(Clone, Debug)]
pub(crate) struct ResolvedResourceSpec {
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

fn record_resource_write() {
    crate::tick_metrics::record_table_write(crate::tick_metrics::TableWriteKind::PlayerResource);
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

    // Spells resolve through the authored catalog kind exactly like melee
    // (netcode audit R2b); the client's SpellResourceKind reads the same
    // replicated AbilityCatalog rows, so the two sides cannot drift.
    let ability_resource_kind = effective_resource_kind_for_ability(ctx, owner, ability)?;
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

/// Tick-shared inputs for resource spec resolution (tick audit T1/T2): the
/// per-tick status view and per-player equipment totals are computed once by
/// the tick orchestrator instead of once per resolution. Event-driven callers
/// use the wrapper functions below, which collect fresh inputs.
pub(crate) struct ResourceSpecInputs<'a> {
    pub status_modifiers: &'a TemporaryCombatModifiers,
    pub equipment: &'a EquipmentModifierTotals,
}

fn fresh_spec_inputs(
    ctx: &ReducerContext,
    owner: Identity,
    needs_equipment: bool,
) -> (TemporaryCombatModifiers, EquipmentModifierTotals) {
    let status_modifiers = temporary_combat_modifiers(ctx, ctx.timestamp);
    // Only MANA specs read equipment; skip the scan otherwise.
    let equipment = if needs_equipment {
        equipment_modifier_totals_for_owner(ctx, owner)
    } else {
        EquipmentModifierTotals::default()
    };
    (status_modifiers, equipment)
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
            record_resource_write();
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

pub(crate) fn sync_primary_resource_for_player_with(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
    inputs: &ResourceSpecInputs,
) {
    sync_resources_for_player_with(ctx, owner, now, inputs);
}

pub(crate) fn sync_resources_for_player(ctx: &ReducerContext, owner: Identity, now: Timestamp) {
    let (status_modifiers, equipment) = fresh_spec_inputs(ctx, owner, true);
    let inputs = ResourceSpecInputs {
        status_modifiers: &status_modifiers,
        equipment: &equipment,
    };
    sync_resources_for_player_with(ctx, owner, now, &inputs);
}

/// Returns the synced row and resolved spec per standard resource kind so
/// per-tick callers can reuse the specs instead of re-resolving them.
pub(crate) fn sync_resources_for_player_with(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
    inputs: &ResourceSpecInputs,
) -> Vec<(PlayerResource, ResolvedResourceSpec)> {
    STANDARD_RESOURCE_KINDS
        .iter()
        .filter_map(|kind| sync_resource_for_player_with(ctx, owner, kind, now, inputs))
        .collect()
}

fn sync_resource_for_player(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
    now: Timestamp,
) -> Option<PlayerResource> {
    let spec = resolve_resource_spec_for_owner_and_kind(ctx, owner, resource_kind)?;
    Some(sync_resource_row_for_spec(ctx, owner, &spec, now))
}

fn sync_resource_for_player_with(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
    now: Timestamp,
    inputs: &ResourceSpecInputs,
) -> Option<(PlayerResource, ResolvedResourceSpec)> {
    let spec = resolve_resource_spec_with(ctx, owner, resource_kind, inputs)?;
    let resource = sync_resource_row_for_spec(ctx, owner, &spec, now);
    Some((resource, spec))
}

fn sync_resource_row_for_spec(
    ctx: &ReducerContext,
    owner: Identity,
    spec: &ResolvedResourceSpec,
    now: Timestamp,
) -> PlayerResource {
    let key = resource_key(owner, spec.kind.as_str());
    let mut resource = if let Some(existing) = ctx.db.player_resource().key().find(key.clone()) {
        existing
    } else {
        let initial_current = baseline_current_for_spec(spec);
        let row = PlayerResource {
            key,
            owner,
            kind: spec.kind.clone(),
            current: initial_current,
            max: spec.max,
            regen_per_second: spec.regen_per_second,
            updated_at: now,
        };
        record_resource_write();
        ctx.db.player_resource().insert(row.clone());
        row
    };

    let next_current = resource.current.clamp(0.0, spec.max.max(0.0));
    if (resource.max - spec.max).abs() <= 0.0001
        && (resource.regen_per_second - spec.regen_per_second).abs() <= 0.0001
        && (resource.current - next_current).abs() <= 0.0001
    {
        return resource;
    }

    resource.max = spec.max.max(0.0);
    resource.regen_per_second = spec.regen_per_second.max(0.0);
    resource.current = next_current;
    resource.updated_at = now;
    record_resource_write();
    ctx.db.player_resource().key().update(resource.clone());
    resource
}

pub(crate) fn tick_primary_resource_for_player(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
    dt_seconds: f32,
    inputs: &ResourceSpecInputs,
) {
    // Sync returns the resolved specs, so the tick loop no longer re-resolves
    // them per row (tick audit T1 slice 2).
    for (resource, spec) in sync_resources_for_player_with(ctx, owner, now, inputs) {
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
    record_resource_write();
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
    record_resource_write();
    ctx.db.player_resource().key().update(resource);
}

pub(crate) fn grant_primary_resource_amount_for_kind(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
    amount: f32,
    now: Timestamp,
) -> f32 {
    let amount = amount.max(0.0);
    if amount <= 0.0 {
        return 0.0;
    }
    if !standard_resource_kind(resource_kind) {
        return 0.0;
    }
    let Some(mut resource) = sync_resource_for_player(ctx, owner, resource_kind, now) else {
        return 0.0;
    };

    let next = (resource.current + amount).clamp(0.0, resource.max);
    let restored = next - resource.current;
    if restored.abs() <= 0.0001 {
        return 0.0;
    }

    resource.current = next;
    resource.updated_at = now;
    record_resource_write();
    ctx.db.player_resource().key().update(resource);
    restored
}

pub(crate) fn grant_primary_resource_for_auto_attack_hit(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    grant_primary_resource_for_melee_hit(ctx, owner, now);
    grant_fighting_spirit_stamina_for_auto_attack_hit(ctx, owner, now);
}

fn grant_primary_resource_for_melee_hit(ctx: &ReducerContext, owner: Identity, now: Timestamp) {
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
    record_resource_write();
    ctx.db.player_resource().key().update(resource);
}

fn grant_fighting_spirit_stamina_for_auto_attack_hit(
    ctx: &ReducerContext,
    owner: Identity,
    now: Timestamp,
) {
    if !player_has_selected_passive_ability(ctx, owner, FIGHTING_SPIRIT_ABILITY_ID) {
        return;
    }
    let Some(combat_discipline_id) = derived_combat_discipline_id_for_owner(ctx, owner) else {
        return;
    };
    let mode_id = resolved_auto_attack_mode_for_owner(ctx, owner, combat_discipline_id.as_str());
    let action_id = AuthoredActionId::new("AUTO_ATTACK_1");
    let Some(gameplay) = auto_attack_gameplay_for_profile_mode_action(
        ctx,
        combat_discipline_id.as_str(),
        mode_id.as_str(),
        &action_id,
    ) else {
        return;
    };
    let attack_speed_multiplier =
        temporary_combat_modifiers(ctx, now).attack_speed_multiplier_for(&owner);
    let cadence_ms =
        scaled_auto_attack_cadence_ms(gameplay.cooldown_ms.max(1), attack_speed_multiplier);
    grant_primary_resource_amount_for_kind(
        ctx,
        owner,
        RESOURCE_KIND_STAMINA,
        fighting_spirit_stamina_for_cadence_ms(cadence_ms),
        now,
    );
}

fn fighting_spirit_stamina_for_cadence_ms(cadence_ms: u64) -> f32 {
    cadence_ms.max(1) as f32 / 1000.0 * FIGHTING_SPIRIT_STAMINA_PER_SECOND_OF_CADENCE
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
    record_resource_write();
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
    record_resource_write();
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

pub(crate) fn current_primary_resource_amount_for_kind(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
    now: Timestamp,
) -> f32 {
    if !standard_resource_kind(resource_kind) {
        return 0.0;
    }
    current_resource(ctx, owner, resource_kind, now)
}

pub(crate) fn spend_primary_resource_amount_for_kind(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
    amount: f32,
    now: Timestamp,
) -> bool {
    standard_resource_kind(resource_kind) && spend_resource(ctx, owner, resource_kind, amount, now)
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
        record_resource_write();
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
    let needs_equipment = resource_kind
        .trim()
        .eq_ignore_ascii_case(RESOURCE_KIND_MANA);
    let (status_modifiers, equipment) = fresh_spec_inputs(ctx, owner, needs_equipment);
    resolve_resource_spec_with(
        ctx,
        owner,
        resource_kind,
        &ResourceSpecInputs {
            status_modifiers: &status_modifiers,
            equipment: &equipment,
        },
    )
}

fn resolve_resource_spec_with(
    ctx: &ReducerContext,
    owner: Identity,
    resource_kind: &str,
    inputs: &ResourceSpecInputs,
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
    let insight = inputs.equipment.allocated_stat_totals().insight as f32;
    let gain_multiplier = (1.0 + definition.gain_multiplier_per_insight * insight).max(0.0);
    let status_modifiers = inputs.status_modifiers;
    let equipment_mana_regen = if primary_kind.eq_ignore_ascii_case("MANA") {
        inputs.equipment.mana_regen_per_second
    } else {
        0.0
    };
    let status_regen = if primary_kind.eq_ignore_ascii_case(RESOURCE_KIND_MANA) {
        status_modifiers.mana_regen_bonus_for(&owner)
            + crate::progression::divinity_faith_mana_regen_bonus_for_owner(ctx, owner)
    } else if primary_kind.eq_ignore_ascii_case(RESOURCE_KIND_STAMINA) {
        status_modifiers.stamina_regen_bonus_for(&owner)
    } else {
        0.0
    };
    Some(ResolvedResourceSpec {
        kind: primary_kind,
        max: (definition.base_max + definition.max_per_insight * insight).max(0.0),
        regen_per_second: (definition.base_regen_per_second
            + definition.regen_per_insight * insight
            + equipment_mana_regen
            + status_regen)
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
    record_resource_write();
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
    use super::{
        fighting_spirit_stamina_for_cadence_ms, primary_resource_decay_per_second,
        ResolvedResourceSpec,
    };

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

    #[test]
    fn fighting_spirit_restore_is_proportional_to_swing_cadence() {
        assert_eq!(fighting_spirit_stamina_for_cadence_ms(1_500), 6.0);
        assert_eq!(fighting_spirit_stamina_for_cadence_ms(2_000), 8.0);
        assert_eq!(fighting_spirit_stamina_for_cadence_ms(3_500), 14.0);
    }
}
